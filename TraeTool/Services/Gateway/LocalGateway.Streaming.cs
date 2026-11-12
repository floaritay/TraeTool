using System.IO;
using System.Net;
using System.Text;
using TraeTool.Models;

namespace TraeTool.Services.Gateway;

/// <summary>
/// 流式转发：取号换号期间不向客户端写任何字节，首个可转发增量到手才提交响应，
/// 之后逐帧落 socket，由各协议的 <see cref="StreamProfile"/> 决定帧形状。
/// </summary>
public sealed partial class LocalGateway
{
    /// <summary>
    /// 流式转发：先干跑换号（此阶段不向客户端写任何字节），
    /// 首个可转发增量到手才提交 200 + text/event-stream，之后逐帧落 socket。
    /// </summary>
    private async Task RelayStreamAsync(HttpListenerContext ctx, ChatRequest parsed,
        StreamProfile profile, CancellationToken ct)
    {
        var cfg = _config();
        var now = DateTime.Now;
        var candidateCount = Math.Max(1, _pool.Candidates(cfg, now).Count);
        var budget = MaxSameAccountBackoff;
        Account? account = null;
        ChatUpstreamError? last = null;
        var tries = 0;
        var guard = 0;

        while (guard++ < candidateCount + MaxSameAccountTries + 1)
        {
            if (account == null)
            {
                account = _pool.Next(cfg, now);
                if (account == null) break;
                tries = 0;
                last = null;   // 同 CompleteBufferedAsync：取到新账号时才清 last
            }

            var beforeToken = account.Token;
            if (!await _ensureToken(account))
            {
                AppLog.Info("pool", $"账号 {account.DisplayName(0)} 凭据不可用，换下一个");
                _pool.MarkFail(account, now, AccountPool.AuthCooldown);
                account = null;
                continue;
            }
            if (_persistRenewedToken && !string.Equals(beforeToken, account.Token, StringComparison.Ordinal))
                ConfigStore.Save(cfg);

            using var streamCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            streamCts.CancelAfter(FirstTokenTimeout);
            var enumerator = _upstream.StreamAsync(account, parsed, streamCts.Token)
                .GetAsyncEnumerator(streamCts.Token);

            UpstreamDelta? first = null;
            UpstreamOutcome? failure = null;
            try
            {
                while (await enumerator.MoveNextAsync())
                {
                    var delta = enumerator.Current;
                    if (delta.Outcome is { } outcome)
                    {
                        failure = outcome;
                        last = outcome.Error ?? new ChatUpstreamError("上游返回没有可用文本", "upstream_error", 502);
                        break;
                    }
                    // 该协议不转发的增量（例如 Anthropic 侧丢弃的思考）不算首字节
                    if (!profile.Emits(delta)) continue;
                    first = delta;
                    break;
                }
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested && streamCts.IsCancellationRequested)
            {
                last = new ChatUpstreamError("上游首字节超时", "upstream_error", 504);
            }
            catch (OperationCanceledException)
            {
                await enumerator.DisposeAsync();
                return;   // 网关停服
            }

            if (first == null)
            {
                last ??= new ChatUpstreamError("上游返回没有可用文本", "upstream_error", 502);
                var decision = RetryPolicy.Decide(last, tries, failure?.RetryAfter);
                ClassifyFailover(account, last, now, decision.Cooldown);
                await enumerator.DisposeAsync();

                if (decision.Action == RetryAction.Fatal)
                {
                    WriteRaw(ctx, StatusFor(last), profile.ErrorBody(last));
                    return;
                }
                if (decision.Action == RetryAction.RetrySame && decision.Delay > TimeSpan.Zero &&
                    decision.Delay <= budget)
                {
                    tries++;
                    budget -= decision.Delay;
                    await Task.Delay(decision.Delay, ct);
                    continue;   // 同一个账号再试
                }
                account = null;
                continue;
            }

            // 提交：越过此点不能再换号。首字节期限换成读空闲期限——每收到一个增量顺延，
            // 上游中途卡死时也能收尾，不让客户端无限挂着
            streamCts.CancelAfter(StreamIdleTimeout);
            _pool.MarkSuccess(account);

            using var writer = new SseWriter(ctx);
            writer.Start();
            profile.Start(writer);
            profile.Delta(writer, first);

            try
            {
                while (await enumerator.MoveNextAsync())
                {
                    var delta = enumerator.Current;
                    if (delta.Outcome is { } outcome)
                    {
                        // 响应已提交，不能再换号重试；但这次失败要记到账号上，
                        // 否则后续请求会继续挑到同一个坏号
                        if (outcome.Error is { } midError)
                            ClassifyFailover(account, midError, DateTime.Now,
                                RetryPolicy.Decide(midError, tries, outcome.RetryAfter).Cooldown);
                        profile.Stop(writer, outcome);
                        return;
                    }
                    if (profile.Emits(delta)) profile.Delta(writer, delta);
                    streamCts.CancelAfter(StreamIdleTimeout);   // 有数据即顺延
                }
                // 上游没给终止帧就断了：补一个收尾，避免客户端一直挂着
                profile.Stop(writer, UpstreamOutcome.Done("stop", truncated: true));
            }
            catch (Exception ex) when (IsDisconnect(ex))
            {
                // 客户端断开：取消上游读取，账号按正常释放（不算失败）
                AppLog.Info("gateway", "客户端断开，已取消上游读取");
                streamCts.Cancel();
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested && streamCts.IsCancellationRequested)
            {
                // 上游读空闲超时（网关未停服）：按协议写错误收尾，别让客户端一直等
                AppLog.Error("gateway", $"上游流式读空闲超时（{StreamIdleTimeout.TotalSeconds:0} 秒），已按协议收尾");
                try
                {
                    profile.Stop(writer, UpstreamOutcome.Failed(
                        new ChatUpstreamError("上游流式响应读取超时", "upstream_error", 504)));
                }
                catch { /* 客户端已断开 */ }
            }
            catch (OperationCanceledException)
            {
                streamCts.Cancel();   // 网关停服
            }
            finally
            {
                try { await enumerator.DisposeAsync(); }
                catch { /* 已取消/已释放 */ }
            }
            return;
        }

        var final = last ?? PoolEmpty();
        WriteRaw(ctx, StatusFor(final), profile.ErrorBody(final));
    }

    /// <summary>写失败是否属于「客户端已经断开」。</summary>
    private static bool IsDisconnect(Exception ex)
        => ex is IOException or HttpListenerException or ObjectDisposedException or InvalidOperationException;

    /// <summary>
    /// 按协议把上游增量渲染成客户端帧。提交前的失败用 <see cref="ErrorBody"/>（普通 JSON 错误体），
    /// 提交后的失败由 <see cref="Stop"/> 收尾（各协议的流内错误形状不同）。
    /// </summary>
    private abstract class StreamProfile
    {
        /// <summary>该协议是否转发这个增量（例如 Anthropic 侧丢弃思考）。</summary>
        public abstract bool Emits(UpstreamDelta delta);

        /// <summary>提交后写协议起始帧。</summary>
        public abstract void Start(SseWriter writer);

        public abstract void Delta(SseWriter writer, UpstreamDelta delta);

        /// <summary>写终止帧；<paramref name="outcome"/> 带错误时写该协议的流内错误形状。</summary>
        public abstract void Stop(SseWriter writer, UpstreamOutcome outcome);

        /// <summary>尚未提交时的错误体（JSON，非 SSE）。</summary>
        public abstract string ErrorBody(ChatUpstreamError error);
    }

    /// <summary>OpenAI Chat Completions：正文 / 思考 / 工具调用都外发。</summary>
    private sealed class OpenAiStream(string requestId, string model, long unixSeconds) : StreamProfile
    {
        public override bool Emits(UpstreamDelta delta) => delta.HasPayload;

        public override void Start(SseWriter writer)
            => writer.Write(ChatProtocol.RenderStreamStart(requestId, model, unixSeconds));

        public override void Delta(SseWriter writer, UpstreamDelta delta)
            => writer.Write(ChatProtocol.RenderStreamDelta(requestId, model, unixSeconds,
                delta.Content, delta.Reasoning, delta.ToolCalls));

        public override void Stop(SseWriter writer, UpstreamOutcome outcome)
        {
            if (outcome.Error is { } error)
            {
                writer.Write(ChatProtocol.RenderStreamError(error));
                writer.Write(ChatProtocol.DoneMarker);
                return;
            }
            writer.Write(ChatProtocol.RenderStreamStop(requestId, model, unixSeconds,
                outcome.FinishReason, outcome.PromptTokens, outcome.CompletionTokens));
            writer.Write(ChatProtocol.DoneMarker);
        }

        public override string ErrorBody(ChatUpstreamError error) => ChatProtocol.RenderError(error);
    }

    /// <summary>Anthropic Messages：只外发正文（思考块回传要签名，上游不给，索性不发）。</summary>
    private sealed class AnthropicStream(string requestId, string model) : StreamProfile
    {
        public override bool Emits(UpstreamDelta delta) => delta.Content is { Length: > 0 };

        public override void Start(SseWriter writer)
        {
            writer.Write(AnthropicProtocol.RenderStreamStart(requestId, model));
            writer.Write(AnthropicProtocol.RenderBlockStart());
        }

        public override void Delta(SseWriter writer, UpstreamDelta delta)
            => writer.Write(AnthropicProtocol.RenderTextDelta(delta.Content!));

        public override void Stop(SseWriter writer, UpstreamOutcome outcome)
        {
            writer.Write(AnthropicProtocol.RenderBlockStop());
            if (outcome.Error is { } error)
            {
                writer.Write(AnthropicProtocol.RenderErrorFrame(error));
                return;   // 流内错误不发 message_stop
            }
            writer.Write(AnthropicProtocol.RenderMessageDelta(outcome.FinishReason,
                outcome.PromptTokens, outcome.CompletionTokens));
            writer.Write(AnthropicProtocol.RenderMessageStop());
        }

        public override string ErrorBody(ChatUpstreamError error) => AnthropicProtocol.RenderError(error);
    }

    /// <summary>OpenAI Responses API：只外发正文，收尾帧需要全文。</summary>
    private sealed class ResponsesStream(string requestId, string model, long unixSeconds) : StreamProfile
    {
        private readonly StringBuilder _text = new();
        private bool _itemStarted;

        public override bool Emits(UpstreamDelta delta) => delta.Content is { Length: > 0 };

        public override void Start(SseWriter writer)
            => writer.Write(ResponsesProtocol.RenderCreated(requestId, model, unixSeconds));

        public override void Delta(SseWriter writer, UpstreamDelta delta)
        {
            if (!_itemStarted)
            {
                writer.Write(ResponsesProtocol.RenderOutputItemAdded(requestId));
                _itemStarted = true;
            }
            _text.Append(delta.Content);
            writer.Write(ResponsesProtocol.RenderTextDelta(requestId, delta.Content!));
        }

        public override void Stop(SseWriter writer, UpstreamOutcome outcome)
        {
            if (outcome.Error is { } error)
            {
                writer.Write(ResponsesProtocol.RenderFailed(requestId, model, unixSeconds, error));
                return;
            }
            if (_itemStarted) writer.Write(ResponsesProtocol.RenderOutputItemDone(requestId, _text.ToString()));
            writer.Write(ResponsesProtocol.RenderCompleted(requestId, model, unixSeconds, _text.ToString(), outcome));
        }

        public override string ErrorBody(ChatUpstreamError error) => ResponsesProtocol.RenderError(error);
    }

    /// <summary>SSE 写出口：逐帧落 socket（不缓冲整段），提交后不可再改状态码与响应头。</summary>
    private sealed class SseWriter : IDisposable
    {
        private readonly HttpListenerContext _ctx;
        private readonly HttpListenerResponse _response;
        private readonly Stream _output;
        private bool _closed;

        public SseWriter(HttpListenerContext ctx)
        {
            _ctx = ctx;
            _response = ctx.Response;
            _output = _response.OutputStream;
        }

        /// <summary>提交响应头；只在首个可转发增量到手后调用一次。</summary>
        public void Start()
        {
            _response.StatusCode = 200;
            _response.ContentType = "text/event-stream; charset=utf-8";
            _response.Headers["Cache-Control"] = "no-cache";
            // 不能设 ContentLength64：那会要求先知道长度，等于把整段缓冲起来
            _response.SendChunked = true;
            ApplyCors(_ctx);
        }

        public void Write(string frame)
        {
            var bytes = Encoding.UTF8.GetBytes(frame);
            _output.Write(bytes, 0, bytes.Length);
            _output.Flush();
        }

        public void Dispose()
        {
            if (_closed) return;
            _closed = true;
            try { _response.Close(); }
            catch { /* 客户端已断开 */ }
        }
    }
}

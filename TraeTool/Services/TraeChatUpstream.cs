using System.IO;
using System.Net;
using System.Net.Http;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using TraeTool.Models;

namespace TraeTool.Services;

/// <summary>上游调用结果（非流式：把整条流拼成一次回答）。</summary>
public sealed record UpstreamResult(ChatCompletion? Completion, ChatUpstreamError? Error)
{
    public bool Ok => Completion != null;

    public static UpstreamResult From(ChatCompletion completion) => new(completion, null);

    public static UpstreamResult Fail(string message, string type, int status)
        => new(null, new ChatUpstreamError(message, type, status));
}

/// <summary>
/// 上游流式返回的一个增量。正文 / 思考 / 工具调用三者择一；<see cref="Outcome"/> 非空即为终末项，
/// 不与前三者混装。
/// </summary>
public sealed record UpstreamDelta(
    string? Content = null,
    string? Reasoning = null,
    string? ToolCalls = null,
    UpstreamOutcome? Outcome = null)
{
    public bool IsTerminal => Outcome != null;

    /// <summary>是否有可转发的内容（不含终末项）。</summary>
    public bool HasPayload =>
        Content is { Length: > 0 } || Reasoning is { Length: > 0 } || ToolCalls is { Length: > 0 };

    public static UpstreamDelta Terminal(UpstreamOutcome outcome) => new(Outcome: outcome);
}

/// <summary>上游流的终止状态：结束原因、是否被截断、真实用量、错误（非空 = 异常终止）。</summary>
public sealed record UpstreamOutcome(
    string FinishReason,
    bool Truncated,
    int? PromptTokens,
    int? CompletionTokens,
    ChatUpstreamError? Error,
    TimeSpan? RetryAfter = null)
{
    public bool Ok => Error == null;

    public static UpstreamOutcome Done(string finishReason, bool truncated = false,
        int? promptTokens = null, int? completionTokens = null)
        => new(finishReason, truncated, promptTokens, completionTokens, null);

    public static UpstreamOutcome Failed(ChatUpstreamError error, bool truncated = false,
        TimeSpan? retryAfter = null)
        => new("stop", truncated, null, null, error, retryAfter);
}

/// <summary>
/// 上游适配器：把一次对话请求交给具体后端。
/// 只需实现 <see cref="StreamAsync"/>；<see cref="CompleteAsync"/> 由它派生（拼成整段回答）。
/// </summary>
public interface IChatUpstream
{
    /// <summary>
    /// 上游请求的流式视图。HTTP 状态错、业务 error 帧、传输中断都以带
    /// <see cref="UpstreamOutcome.Error"/> 的终末项结束，不抛异常；
    /// 只有 <paramref name="ct"/> 被触发时才抛 <see cref="OperationCanceledException"/>。
    /// </summary>
    IAsyncEnumerable<UpstreamDelta> StreamAsync(Account account, ChatRequest request, CancellationToken ct);

    /// <summary>
    /// 非流式：消费 <see cref="StreamAsync"/> 把增量拼成一次回答。
    /// 中途被截断视为失败（保持「上游流中断」的旧语义）。
    /// </summary>
    async Task<UpstreamResult> CompleteAsync(Account account, ChatRequest request, CancellationToken ct)
    {
        var text = new StringBuilder();
        var reasoning = new StringBuilder();
        var finish = "stop";
        ChatUpstreamError? error = null;
        var truncated = false;

        await foreach (var delta in StreamAsync(account, request, ct))
        {
            if (delta.Outcome is { } outcome)
            {
                finish = outcome.FinishReason;
                error = outcome.Error;
                truncated = outcome.Truncated;
                break;
            }
            if (delta.Content is { Length: > 0 } content) text.Append(content);
            else if (delta.Reasoning is { Length: > 0 } reason) reasoning.Append(reason);
        }

        if (error != null) return new UpstreamResult(null, error);
        if (truncated) return UpstreamResult.Fail("上游流中断", "upstream_error", 502);
        if (text.Length == 0) text = reasoning;   // 没有正文时退回思考，总好过报无文本
        if (text.Length == 0) return UpstreamResult.Fail("上游返回没有可用文本", "upstream_error", 502);
        return UpstreamResult.From(new ChatCompletion(text.ToString(), finish, request.Model));
    }
}

/// <summary>
/// 上游 SSE 的逐行解析器：纯逻辑，不接触 socket，可直接喂字符串测试。
/// 调用方按行 <see cref="Feed"/>（空行结算一帧），流结束时调 <see cref="Finish"/> 收尾。
/// 一帧最多产出一个增量：正文 / 思考 / 工具 / 终末。
/// </summary>
public sealed class TraeSseParser
{
    /// <summary>结束原因的取值白名单，词表外的词一律归 stop。</summary>
    private static readonly string[] FinishWhitelist =
        ["stop", "length", "tool_calls", "function_call", "content_filter"];

    private readonly StringBuilder _data = new();
    private readonly StringBuilder _raw = new();   // 错误文案要引原文，只留开头一段

    private const int RawLimit = 512;

    private string _event = "";
    private string _finish = "";
    private int? _promptTokens;
    private int? _completionTokens;

    /// <summary>是否出现过任何一帧。</summary>
    public bool SawAnyFrame { get; private set; }

    /// <summary>是否出现过终止帧（done / [DONE] / error）。</summary>
    public bool SawDone { get; private set; }

    /// <summary>是否产出过正文或思考。</summary>
    public bool SawText { get; private set; }

    /// <summary>是否产出过工具调用。</summary>
    public bool SawToolCalls { get; private set; }

    /// <summary>结算后的结束原因（白名单外的词归 stop；出现过工具调用而结果是 stop 则升级为 tool_calls）。</summary>
    public string FinishReason
    {
        get
        {
            var finish = string.IsNullOrEmpty(_finish) ? "stop" : _finish;
            if (Array.IndexOf(FinishWhitelist, finish) < 0) finish = "stop";
            if (SawToolCalls && finish == "stop") finish = "tool_calls";
            return finish;
        }
    }

    /// <summary>喂入一行原始行（可带 \r），返回该行结算出的增量（通常为空）。</summary>
    public IReadOnlyList<UpstreamDelta> Feed(string rawLine)
    {
        var line = rawLine.TrimEnd('\r');
        Remember(line);

        if (line.Length == 0)
        {
            var frame = Flush();
            return frame == null ? Array.Empty<UpstreamDelta>() : new[] { frame };
        }

        if (line.StartsWith("event:", StringComparison.OrdinalIgnoreCase))
            _event = line["event:".Length..].Trim();
        else if (line.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
        {
            if (_data.Length > 0) _data.Append('\n');
            _data.Append(line["data:".Length..].TrimStart());
        }
        return Array.Empty<UpstreamDelta>();
    }

    /// <summary>流结束：结算残留帧并按累积状态给出终末项。</summary>
    public IReadOnlyList<UpstreamDelta> Finish()
    {
        var frame = Flush();
        if (frame is { IsTerminal: true }) return new[] { frame };

        var tail = new List<UpstreamDelta>(2);
        if (frame != null) tail.Add(frame);

        if (!SawAnyFrame)
        {
            tail.Add(Fail("响应不是 SSE，也不含帧内容：" + Truncate(_raw.ToString())));
        }
        else if (!SawText && !SawToolCalls)
        {
            // 有终止帧说明上游正常收尾，只是没产出文本；两者要能区分开
            tail.Add(SawDone
                ? UpstreamDelta.Terminal(UpstreamOutcome.Failed(
                    new ChatUpstreamError("上游已返回完成但没有文本内容：" + Truncate(_raw.ToString()),
                        "empty_completion", 502)))
                : Fail("SSE 里没有可用文本：" + Truncate(_raw.ToString())));
        }
        else
        {
            // 没有终止帧就结束了：有内容时按正常收尾补一个终末项，并标记截断
            tail.Add(UpstreamDelta.Terminal(UpstreamOutcome.Done(FinishReason, truncated: !SawDone,
                _promptTokens, _completionTokens)));
        }
        return tail;
    }

    /// <summary>结算当前累积的一帧；无可结算时返回 null。</summary>
    private UpstreamDelta? Flush()
    {
        if (_data.Length == 0 && _event.Length == 0) return null;

        var data = _data.ToString();
        var evt = _event;
        _data.Clear();
        _event = "";
        SawAnyFrame = true;

        // 业务错误帧：HTTP 200 也算失败，必须原样透出
        if (evt.Equals("error", StringComparison.OrdinalIgnoreCase))
        {
            SawDone = true;
            return UpstreamDelta.Terminal(UpstreamOutcome.Failed(
                new ChatUpstreamError(TraeChatUpstream.DescribeUpstreamError(data), "api_error", 200)));
        }

        if (evt.Equals("done", StringComparison.OrdinalIgnoreCase))
        {
            SawDone = true;
            ReadFinish(data);
            return null;
        }

        if (data.Trim() == "[DONE]")
        {
            SawDone = true;
            if (string.IsNullOrEmpty(_finish)) _finish = "stop";
            return null;
        }

        if (data.Length == 0) return null;

        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(data);
        }
        catch (JsonException)
        {
            // meta 帧的 data 若非 JSON 则忽略；输出帧的坏 JSON 会截断整条流，必须报错
            return evt is "output" or "content" or "message" or "thought"
                ? Fail("SSE 帧不是合法 JSON：" + Truncate(data))
                : null;
        }

        using (doc)
        {
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return null;

            ReadUsage(root);

            // 输出帧：response = 正文，reasoning / reasoning_content = 思考，tool_calls = 工具
            if (root.TryGetProperty("response", out var responseEl) && responseEl.ValueKind == JsonValueKind.String)
            {
                var content = responseEl.GetString() ?? "";
                var reasoning = ReadReasoning(root);
                var tools = ReadToolCalls(root);
                SawText |= content.Length > 0 || reasoning.Length > 0;
                SawToolCalls |= tools != null;
                if (content.Length == 0 && reasoning.Length == 0 && tools == null) return null;
                return new UpstreamDelta(
                    Content: content.Length > 0 ? content : null,
                    Reasoning: reasoning.Length > 0 ? reasoning : null,
                    ToolCalls: tools);
            }

            // 只有思考的帧（部分端点把思考单独成帧）
            if (root.TryGetProperty("reasoning_content", out _) || root.TryGetProperty("reasoning", out _))
            {
                var reasoning = ReadReasoning(root);
                var tools = ReadToolCalls(root);
                SawText |= reasoning.Length > 0;
                SawToolCalls |= tools != null;
                if (reasoning.Length == 0 && tools == null) return null;
                return new UpstreamDelta(
                    Reasoning: reasoning.Length > 0 ? reasoning : null,
                    ToolCalls: tools);
            }

            // 只带工具调用的帧（OpenAI 形状的增量）
            if (ReadToolCalls(root) is { } standaloneTools)
            {
                SawToolCalls = true;
                return new UpstreamDelta(ToolCalls: standaloneTools);
            }

            string? chunk = null;
            if (root.TryGetProperty("choices", out var choices) && choices.ValueKind == JsonValueKind.Array)
            {
                foreach (var choice in choices.EnumerateArray())
                {
                    chunk ??= TraeChatUpstream.ReadText(choice);
                    if (string.IsNullOrEmpty(_finish) &&
                        choice.TryGetProperty("finish_reason", out var fr) &&
                        fr.ValueKind == JsonValueKind.String && !string.IsNullOrEmpty(fr.GetString()))
                        _finish = fr.GetString()!;
                }
            }
            chunk ??= TraeChatUpstream.ReadText(root);
            if (string.IsNullOrEmpty(chunk)) return null;

            SawText = true;
            return new UpstreamDelta(Content: chunk);
        }
    }

    private static UpstreamDelta Fail(string message)
        => UpstreamDelta.Terminal(UpstreamOutcome.Failed(
            new ChatUpstreamError(message, "upstream_error", 502)));

    private void ReadFinish(string data)
    {
        try
        {
            using var doc = JsonDocument.Parse(data);
            if (doc.RootElement.ValueKind == JsonValueKind.Object &&
                doc.RootElement.TryGetProperty("finish_reason", out var fr) &&
                fr.ValueKind == JsonValueKind.String)
                _finish = fr.GetString() ?? _finish;
        }
        catch (JsonException)
        {
            // done 帧没有 JSON（例如 data: [DONE]）时忽略
        }
    }

    /// <summary>取用量：兼容 input/output 与 prompt/completion 两套命名，可嵌在 usage 对象里。</summary>
    private void ReadUsage(JsonElement root)
    {
        var scope = root;
        if (root.TryGetProperty("usage", out var usage) && usage.ValueKind == JsonValueKind.Object)
            scope = usage;

        _promptTokens ??= FindInt(scope, "prompt_tokens", "input_tokens");
        _completionTokens ??= FindInt(scope, "completion_tokens", "output_tokens");
    }

    private static int? FindInt(JsonElement scope, params string[] keys)
    {
        foreach (var key in keys)
        {
            if (scope.TryGetProperty(key, out var el) && el.ValueKind == JsonValueKind.Number &&
                el.TryGetInt32(out var value))
                return value;
        }
        return null;
    }

    private static string ReadReasoning(JsonElement root)
    {
        foreach (var key in new[] { "reasoning_content", "reasoning" })
        {
            if (root.TryGetProperty(key, out var el) && el.ValueKind == JsonValueKind.String)
                return el.GetString() ?? "";
        }
        return "";
    }

    /// <summary>工具调用：原样保留 JSON（客户端按自己的协议再加工）。</summary>
    private static string? ReadToolCalls(JsonElement root)
    {
        if (!root.TryGetProperty("tool_calls", out var el)) return null;
        if (el.ValueKind == JsonValueKind.Array && el.GetArrayLength() == 0) return null;
        if (el.ValueKind is not (JsonValueKind.Array or JsonValueKind.Object)) return null;
        return el.GetRawText();
    }

    private void Remember(string line)
    {
        if (_raw.Length >= RawLimit) return;
        if (_raw.Length > 0) _raw.Append('\n');
        _raw.Append(line.Length > RawLimit ? line[..RawLimit] : line);
    }

    private static string Truncate(string text) => TraeChatUpstream.Truncate(text);
}

/// <summary>
/// Trae 后端适配器。
///
/// 已按客户端实际请求核实的部分：
///   · 域名两个都出现过——客户端启动配置下发 trae-api-cn.mchost.guru，真实转发落到 api5-normal.mchost.guru；
///   · 请求头集合见 <see cref="ApplyObservedHeaders"/>（authorization 为 Bearer 凭据）；
///   · 响应是 SSE：<c>event: meta</c> / <c>event: output</c>，output 的 data 形如
///     <c>{"choices":[{"text":"…"}]}</c>（completion 形状，不是 message.content），解析见 <see cref="ParseResponse"/>。
///
/// 待校准的部分：对话端点路径与请求体字段（见 <see cref="BuildBody"/>）。
/// 失败时上游 HTTP 状态与响应体前 512 字节写入日志（pool 标签）。
/// 不做按请求轮换设备身份：设备号取账号自己存的那一个，头部是固定的客户端标识。
/// </summary>
public sealed class TraeChatUpstream(HttpClient? http = null) : IChatUpstream
{
    /// <summary>API 域名（客户端启动配置下发；观测到的另一个域名为 api5-normal.mchost.guru）。</summary>
    public static string BaseUrl = "https://trae-api-cn.mchost.guru";

    /// <summary>对话端点路径（未在本机流量中直接观测到，调用失败时按日志核对）。</summary>
    public static string ChatPath = "/api/agent/v3/llm_utils_chat";

    /// <summary>Referer 用的基路径（指向模型配置端点，与客户端实际请求一致）。</summary>
    public static string BasePath = "/api/ide/v1/batch_get_detail_param";

    /// <summary>客户端标识（观测到的实际请求值）。</summary>
    private const string AppId = "6eefa01c-1036-4c7e-9ca5-d891f63bfcd8";

    private HttpClient Client => http ?? HttpClients.Default;

    /// <summary>
    /// 逐行读上游 SSE 并即时产出增量。响应头到达后即开始读取，不在本地缓存整段。
    /// </summary>
    public async IAsyncEnumerable<UpstreamDelta> StreamAsync(
        Account account, ChatRequest request, [EnumeratorCancellation] CancellationToken ct)
    {
        var body = BuildBody(request, account.AccountUid, account.DeviceId);
        using var msg = new HttpRequestMessage(HttpMethod.Post, BaseUrl + ChatPath)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };
        ApplyObservedHeaders(msg, account);

        // 连接阶段单独走一个方法：迭代器里不能在 catch 体内 yield
        var (response, openError) = await OpenAsync(Client, msg, ct);
        if (openError != null)
        {
            AppLog.Error("pool", "上游调用异常: " + openError.Message);
            yield return UpstreamDelta.Terminal(UpstreamOutcome.Failed(openError));
            yield break;
        }

        using (response!)
        {
            var status = (int)response!.StatusCode;
            var retryAfter = ReadRetryAfter(response);

            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            {
                yield return Fail("凭据被上游拒绝（HTTP " + status + "），请重新登录该账号", "auth_error", status, retryAfter);
                yield break;
            }
            if (status == 429)
            {
                yield return Fail("上游限流（HTTP 429）", "rate_limit_error", status, retryAfter);
                yield break;
            }
            if (!response.IsSuccessStatusCode)
            {
                var detail = await SafeReadAsync(response, ct);
                AppLog.Info("pool", $"上游请求体: {body}");
                yield return Fail($"上游返回 HTTP {status}：{Truncate(detail)}", "upstream_error", status, retryAfter);
                yield break;
            }

            var media = response.Content.Headers.ContentType?.MediaType;
            if (string.Equals(media, "application/json", StringComparison.OrdinalIgnoreCase))
            {
                var json = await SafeReadAsync(response, ct);
                var parsed = ParseJson(json, request.Model);
                if (!parsed.Ok)
                {
                    AppLog.Info("pool", $"上游请求体: {body}");
                    yield return UpstreamDelta.Terminal(UpstreamOutcome.Failed(parsed.Error!));
                }
                else
                {
                    yield return new UpstreamDelta(Content: parsed.Completion!.Content);
                    yield return UpstreamDelta.Terminal(UpstreamOutcome.Done(parsed.Completion.FinishReason));
                }
                yield break;
            }

            var parser = new TraeSseParser();
            var jsonBuffer = new StringBuilder();
            var asJson = false;
            var firstLine = true;

            using var reader = new StreamReader(await response.Content.ReadAsStreamAsync(ct), Encoding.UTF8);
            while (true)
            {
                string? line;
                Exception? readError = null;
                try
                {
                    line = await reader.ReadLineAsync(ct);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    line = null;
                    readError = ex;
                }

                // 传输中断：已有内容就补收尾，零内容才让上层换号
                if (readError != null)
                {
                    yield return UpstreamDelta.Terminal(parser.SawText || parser.SawToolCalls
                        ? UpstreamOutcome.Done(parser.FinishReason, truncated: true)
                        : UpstreamOutcome.Failed(
                            new ChatUpstreamError("上游流中断：" + readError.Message, "upstream_error", 502),
                            truncated: true));
                    yield break;
                }

                if (line == null) break;

                if (firstLine)
                {
                    firstLine = false;
                    // 内容类型不可靠时按首行形状判断：以 { 或 [ 开头即整段 JSON
                    var probe = line.TrimStart();
                    if (probe.StartsWith('{') || probe.StartsWith('[')) asJson = true;
                    else if (probe.Length > 0) asJson = false;
                }

                if (asJson)
                {
                    jsonBuffer.Append(line).Append('\n');
                    continue;
                }

                foreach (var delta in parser.Feed(line)) yield return delta;
            }

            if (asJson)
            {
                var parsed = ParseJson(jsonBuffer.ToString(), request.Model);
                if (!parsed.Ok)
                {
                    AppLog.Info("pool", $"上游请求体: {body}");
                    yield return UpstreamDelta.Terminal(UpstreamOutcome.Failed(parsed.Error!));
                }
                else
                {
                    yield return new UpstreamDelta(Content: parsed.Completion!.Content);
                    yield return UpstreamDelta.Terminal(UpstreamOutcome.Done(parsed.Completion.FinishReason));
                }
                yield break;
            }

            foreach (var delta in parser.Finish()) yield return delta;
        }
    }

    private static UpstreamDelta Fail(string message, string type, int status, TimeSpan? retryAfter = null)
        => UpstreamDelta.Terminal(UpstreamOutcome.Failed(
            new ChatUpstreamError(message, type, status), retryAfter: retryAfter));

    /// <summary>
    /// 发出请求并等响应头。迭代器里不能在 catch 体内 yield，所以连接阶段单独成方法，
    /// 把失败当作返回值交给调用方。
    /// </summary>
    private static async Task<(HttpResponseMessage? Response, ChatUpstreamError? Error)> OpenAsync(
        HttpClient client, HttpRequestMessage msg, CancellationToken ct)
    {
        try
        {
            var resp = await client.SendAsync(msg, HttpCompletionOption.ResponseHeadersRead, ct);
            return (resp, null);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return (null, new ChatUpstreamError("上游调用异常：" + ex.Message, "upstream_error", 502));
        }
    }

    /// <summary>读 Retry-After（秒数或 HTTP 日期）；读不到返回 null。</summary>
    private static TimeSpan? ReadRetryAfter(HttpResponseMessage resp)
    {
        var value = resp.Headers.RetryAfter;
        if (value?.Delta is { } delta && delta > TimeSpan.Zero) return delta;
        if (value?.Date is { } date)
        {
            var wait = date - DateTimeOffset.UtcNow;
            if (wait > TimeSpan.Zero) return wait;
        }
        return null;
    }

    private static async Task<string> SafeReadAsync(HttpResponseMessage resp, CancellationToken ct)
    {
        try
        {
            return await resp.Content.ReadAsStringAsync(ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return "";
        }
    }

    /// <summary>按客户端实际观测到的请求头写入（固定标识，不按请求变化）。</summary>
    public static void ApplyObservedHeaders(HttpRequestMessage msg, Account account)
    {
        var baseUri = TraeChatUpstream.BaseUrl;

        msg.Headers.TryAddWithoutValidation("Accept", "*/*");
        msg.Headers.TryAddWithoutValidation("User-Agent", "TraeClient/TTNet");
        // 鉴权方案：本项目对同一产品可用的调用用的是 Cloud-IDE-JWT（Bearer 会被判未认证），
        // IDE 自身请求同时带 x-ide-token，两者都带上
        if (!string.IsNullOrEmpty(account.Token))
        {
            msg.Headers.TryAddWithoutValidation("Authorization", "Cloud-IDE-JWT " + account.Token);
            msg.Headers.TryAddWithoutValidation("x-ide-token", account.Token);
        }
        if (!string.IsNullOrEmpty(account.Session))
            msg.Headers.TryAddWithoutValidation("Cookie", "X-Cloudide-Session=" + account.Session);
        msg.Headers.TryAddWithoutValidation("x-user-region", "CN");
        msg.Headers.TryAddWithoutValidation("Referer", baseUri + TraeChatUpstream.BasePath);
        msg.Headers.TryAddWithoutValidation("Origin", baseUri);

        msg.Headers.TryAddWithoutValidation("x-app-id", AppId);
        msg.Headers.TryAddWithoutValidation("app-version", "3.4.15");
        msg.Headers.TryAddWithoutValidation("x-app-version-code", "20261029");
        msg.Headers.TryAddWithoutValidation("package-type", "stable_cn");
        msg.Headers.TryAddWithoutValidation("x-ide-version", "3.4.15");
        msg.Headers.TryAddWithoutValidation("x-ide-version-code", "20261029");
        msg.Headers.TryAddWithoutValidation("x-ide-version-type", "stable");
        msg.Headers.TryAddWithoutValidation("x-device-type", "windows");
        msg.Headers.TryAddWithoutValidation("x-device-id", account.DeviceId ?? "");
        msg.Headers.TryAddWithoutValidation("x-os-version", Uri.EscapeDataString(Environment.OSVersion.VersionString));
        msg.Headers.TryAddWithoutValidation("x-lscbd-aid", "787976");
        msg.Headers.TryAddWithoutValidation("x-lscbd-platform", "windows");
        msg.Headers.TryAddWithoutValidation("x-lgw-req-sdk-type", "3");
        msg.Headers.TryAddWithoutValidation("x-request-id", "req_" + Guid.NewGuid().ToString());
        // 观测到但未复现的头部：x-device-brand / x-device-cpu（硬件型号，本机无等价值）、
        // x-machine-id（设备指纹）、X-Helios / X-Medusa / X-Neptune（逐请求签名值）。
        // 上游若强制校验会以 4xx 拒绝，响应内容见日志 pool 标签。
    }

    /// <summary>上游 resolveByUsage 的 function 入参；取自 batch_get_detail_param 的 function_configs[0].function。</summary>
    /// <summary>请求体里的 ide 版本标识（与上游注册的客户端版本一致）。</summary>
    public const string IdeVersion = "0.1.50";
    public const string IdeVersionCode = "20260811";

    /// <summary>未命中名单的模型所用 function 视图。</summary>
    public const string DefaultFunction = "solo_work_lite";

    /// <summary>仅在 solo_agent 视图可用的模型（大小写不敏感）；视图不符上游报 4001/4023。</summary>
    private static readonly string[] SoloAgentModels =
    [
        "doubao-seed-code", "glm-5.3-flash", "qwen3.8-flash",
        "deepseek-v4.1-flash", "kimi-k2.8-preview",
    ];

    /// <summary>客户端模型名 → (canonical config_name, 上游内部 model_name)；未收录按 {model}__dev 规则。</summary>
    private static readonly Dictionary<string, (string Config, string Model)> ModelPairs =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["doubao-seed-evolving"] = ("Doubao-Seed-Evolving", "Doubao-Seed-Evolving__dev"),
            ["doubao-seed-2.1-pro"] = ("Doubao-Seed-2.1-Pro", "Doubao-Seed-2.1-Pro__dev"),
            ["seed-code-pro-0430"] = ("Doubao-Seed-2.1-Pro", "Doubao-Seed-2.1-Pro__dev"),
            ["doubao-seed-2.1-turbo"] = ("Doubao-Seed-2.1-Turbo", "Doubao-Seed-2.1-Turbo__dev"),
            ["doubao-seed-code"] = ("Doubao-Seed-Code", "Doubao-Seed-Code__dev"),
            ["glm-5.3-flash"] = ("glm-5.3-flash", "glm-5.3-flash__dev"),
            ["qwen3.8-flash"] = ("qwen3.8-flash", "qwen3.8-flash__dev"),
            ["glm-5.2"] = ("glm-5.2", "glm-5.2__dev"),
            ["glm-5.3"] = ("glm-5.3", "glm-5.3__dev"),
            ["glm-5"] = ("glm-5", "glm-5__dev"),
            ["glm-5-turbo"] = ("glm-5-turbo", "glm-5-turbo__dev"),
            ["deepseek-v4.1-flash"] = ("deepseek-v4.1-flash", "deepseek-v4.1-flash__dev"),
            ["deepseek-v4-flash"] = ("DeepSeek-V4-Flash", "deepseek_v4_flash__dev"),
            ["deepseek-v4-flash-official"] = ("DeepSeek-V4-Flash-Official", "DeepSeek-V4-Flash-Official__dev"),
            ["deepseek-v4-pro"] = ("DeepSeek-V4-Pro", "deepseek_v4_pro__dev"),
            ["deepseek-v4-pro-official"] = ("DeepSeek-V4-Pro-Official", "DeepSeek-V4-Pro-Official__dev"),
            ["kimi-k2.6"] = ("kimi-k2.6", "kimi-k2.6__dev"),
            ["kimi-k2.7-code"] = ("kimi-k2.7-code", "kimi-k2.7-code__dev"),
            ["kimi-k2.8-preview"] = ("kimi-k2.8-preview", "kimi-k2.8-preview__dev"),
            ["kimi-k3"] = ("kimi-k3", "kimi-k3__dev"),
            ["minimax-m3"] = ("minimax-m3", "minimax-m3__dev"),
            ["qwen3.8-max"] = ("qwen3.8-max", "qwen3.8-max__dev"),
            ["qwen-3.7-plus"] = ("qwen-3.7-plus", "qwen-3.7-plus__dev"),
        };

    /// <summary>按模型名定 function。</summary>
    private static string FunctionForModel(string model)
        => Array.IndexOf(SoloAgentModels, model.Trim().ToLowerInvariant()) >= 0
            ? "solo_agent"
            : DefaultFunction;

    /// <summary>解析 (config_name, model_name)；未收录按 model_name = {model}__dev。</summary>
    private static (string Config, string Model) ResolveModelPair(string model)
    {
        var name = (model ?? "").Trim();
        return ModelPairs.TryGetValue(name, out var pair) ? pair : (name, name + "__dev");
    }

    /// <summary>
    /// 请求体映射：OpenAI 对话体 → 上游 llm_utils_chat 所需形态。
    /// 必需字段：config_name / model_name / function / max_tokens / prompt_max_tokens /
    /// conversation_id / session_id / user_id / device_id / project_id / mode / ide+app 标识。
    /// content 必须是对象数组（字符串会 400/code 4001）；上游恒定流式返回，
    /// 对客户端是否流式由网关层按 OpenAI/Anthropic 请求的 stream 决定。
    /// </summary>
    public static string BuildBody(ChatRequest request, string? userUid, string? deviceId)
    {
        var model = (request.Model ?? "").Trim();
        var (configName, modelName) = ResolveModelPair(model);

        var body = new Dictionary<string, object?>
        {
            ["config_name"] = configName,
            ["model_name"] = modelName,
            ["model"] = model,
            ["function"] = FunctionForModel(model),
            ["stream"] = true,
            ["max_tokens"] = request.MaxTokens ?? 4096,
            ["prompt_max_tokens"] = 168000,
            ["mode"] = "FunctionCall",
            ["conversation_id"] = Guid.NewGuid().ToString(),
            ["session_id"] = Guid.NewGuid().ToString(),
            ["project_id"] = Guid.NewGuid().ToString(),
            ["user_id"] = userUid ?? "",
            ["device_id"] = deviceId ?? "",
            ["ide_version"] = IdeVersion,
            ["ide_version_code"] = IdeVersionCode,
            ["app_id"] = AppId,
            ["package_type"] = "stable_cn",
            // messages[].content 必须是对象数组（[*]Content），发字符串会 400/code 4001
            ["messages"] = request.Messages.Select(m => new
            {
                role = m.Role,
                content = new[] { new { type = "text", text = m.Content } },
            }).ToArray(),
        };
        if (request.Temperature.HasValue) body["temperature"] = request.Temperature.Value;
        return JsonSerializer.Serialize(body);
    }

    /// <summary>
    /// 上游响应解析：SSE（客户端实际返回）与普通 JSON 双分支。
    /// SSE 取全部 output 帧的 <c>choices[].text</c> 拼接；JSON 分支先按 OpenAI 结构取
    /// <c>choices[0].message.content</c>，再退回顶层 content/text/data；都不匹配记为格式不符并附原文摘要。
    /// </summary>
    public static UpstreamResult ParseResponse(string body, string model)
    {
        var trimmed = body.TrimStart();
        if (trimmed.StartsWith('{') || trimmed.StartsWith('['))
            return ParseJson(body, model);
        return ParseSse(body, model);
    }

    /// <summary>解析整段 <c>event:/data:</c> 帧（空行分隔）；无返回帧视为截断，不额外报错。</summary>
    public static UpstreamResult ParseSse(string body, string model)
    {
        var parser = new TraeSseParser();
        var text = new StringBuilder();
        var reasoning = new StringBuilder();
        var finish = "stop";
        ChatUpstreamError? error = null;

        foreach (var line in body.Split('\n'))
        {
            Absorb(parser.Feed(line));
            if (error != null) return new UpstreamResult(null, error);
        }
        Absorb(parser.Finish());
        // 没有可用文本时解析器已给出终末错误（空完成 / 不是 SSE），这里只剩可用的情形
        if (error != null) return new UpstreamResult(null, error);

        // 正式回答优先；完全没有回答时退回思考内容（总好过报无文本）
        if (text.Length == 0) text = reasoning;

        return UpstreamResult.From(new ChatCompletion(text.ToString(), finish, model));

        void Absorb(IReadOnlyList<UpstreamDelta> deltas)
        {
            foreach (var delta in deltas)
            {
                if (delta.Outcome is { } outcome)
                {
                    if (outcome.Error != null) { error = outcome.Error; return; }
                    finish = outcome.FinishReason;
                    continue;
                }
                if (delta.Content is { Length: > 0 } content) text.Append(content);
                if (delta.Reasoning is { Length: > 0 } reason) reasoning.Append(reason);
            }
        }
    }

    private static UpstreamResult ParseJson(string body, string model)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                return Fail(body);

            if (root.TryGetProperty("choices", out var choices) && choices.ValueKind == JsonValueKind.Array &&
                choices.GetArrayLength() > 0)
            {
                var first = choices[0];
                var content = ReadText(first);
                var finish = first.TryGetProperty("finish_reason", out var fr) && fr.ValueKind == JsonValueKind.String
                    ? fr.GetString() ?? "stop" : "stop";
                if (content.Length > 0)
                    return UpstreamResult.From(new ChatCompletion(content, finish, model));
            }

            var fallback = ReadText(root);
            if (fallback.Length > 0)
                return UpstreamResult.From(new ChatCompletion(fallback, "stop", model));

            return Fail(body);
        }
        catch (Exception ex)
        {
            return UpstreamResult.Fail("响应不是合法 JSON：" + ex.Message, "upstream_error", 502);
        }

        static UpstreamResult Fail(string body)
            => UpstreamResult.Fail("响应格式与预期不符：" + Truncate(body), "upstream_error", 502);
    }

    /// <summary>取文本：OpenAI 的 message.content / completions 的 text / 顶层 content、text、data。</summary>
    internal static string ReadText(JsonElement node)
    {
        if (node.ValueKind != JsonValueKind.Object) return "";

        if (node.TryGetProperty("message", out var msg) && msg.ValueKind == JsonValueKind.Object)
        {
            if (msg.TryGetProperty("content", out var mc))
            {
                if (mc.ValueKind == JsonValueKind.String) return mc.GetString() ?? "";
                if (mc.ValueKind == JsonValueKind.Array) return Flatten(mc);
            }
        }
        foreach (var key in new[] { "text", "content", "delta" })
        {
            if (!node.TryGetProperty(key, out var v)) continue;
            if (v.ValueKind == JsonValueKind.String)
            {
                var s = v.GetString() ?? "";
                if (s.Length > 0) return s;
            }
            else if (v.ValueKind == JsonValueKind.Object)
            {
                var inner = ReadText(v);
                if (inner.Length > 0) return inner;
            }
        }
        return "";
    }

    private static string Flatten(JsonElement array)
    {
        var sb = new StringBuilder();
        foreach (var seg in array.EnumerateArray())
        {
            if (seg.ValueKind == JsonValueKind.String) sb.Append(seg.GetString());
            else if (seg.ValueKind == JsonValueKind.Object &&
                     seg.TryGetProperty("text", out var tx) && tx.ValueKind == JsonValueKind.String)
                sb.Append(tx.GetString());
        }
        return sb.ToString();
    }

    /// <summary>把上游 error 帧的 code/message 组成可读文案；解析失败回传原始 data。</summary>
    internal static string DescribeUpstreamError(string data)
    {
        try
        {
            using var doc = JsonDocument.Parse(data);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return Truncate(data);

            var code = root.TryGetProperty("code", out var c)
                ? c.ValueKind == JsonValueKind.Number ? c.GetRawText() : c.GetString() ?? ""
                : "";
            var msg = root.TryGetProperty("message", out var m) && m.ValueKind == JsonValueKind.String
                ? m.GetString() ?? ""
                : "";
            if (msg.Length == 0) return Truncate(data);
            return code.Length > 0 ? $"上游 {code}: {msg}" : "上游错误: " + msg;
        }
        catch (JsonException)
        {
            return Truncate(data);
        }
    }

    internal static string Truncate(string text)
    {
        if (string.IsNullOrEmpty(text)) return "(空)";
        var flat = text.Replace("\r", " ").Replace("\n", " ");
        return flat.Length <= 512 ? flat : flat[..512] + "…";
    }
}

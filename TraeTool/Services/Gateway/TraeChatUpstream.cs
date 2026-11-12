using System.IO;
using System.Net;
using System.Net.Http;
using System.Runtime.CompilerServices;
using System.Text;
using TraeTool.Models;

namespace TraeTool.Services.Gateway;

/// <summary>
/// Trae 后端适配器：把一次对话请求发到上游并逐行产出增量。
///
/// 已按客户端实际请求核实的部分：
///   · 域名两个都出现过——客户端启动配置下发 trae-api-cn.mchost.guru，真实转发落到 api5-normal.mchost.guru；
///   · 请求头集合见 <see cref="ApplyObservedHeaders"/>（authorization 为 Bearer 凭据）；
///   · 响应是 SSE：<c>event: meta</c> / <c>event: output</c>，output 的 data 形如
///     <c>{"choices":[{"text":"…"}]}</c>（completion 形状，不是 message.content），解析见 <see cref="ParseResponse"/>。
///
/// 失败时上游 HTTP 状态与响应体前 512 字节写入日志（pool 标签）；
/// 外发请求体只记长度与模型名，不落正文（正文含完整对话内容）。
/// 不做按请求轮换设备身份：设备号取账号自己存的那一个，头部是固定的客户端标识。
/// 请求体构造见 TraeChatUpstream.Body.cs，响应解析见 TraeChatUpstream.Parsing.cs。
/// </summary>
public sealed partial class TraeChatUpstream(HttpClient? http = null) : IChatUpstream
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
                AppLog.Info("pool", $"上游请求体 {body.Length} 字节（model={request.Model}，正文不落日志）");
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
                    AppLog.Info("pool", $"上游请求体 {body.Length} 字节（model={request.Model}，正文不落日志）");
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
                    AppLog.Info("pool", $"上游请求体 {body.Length} 字节（model={request.Model}，正文不落日志）");
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
        var baseUri = BaseUrl;

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
        msg.Headers.TryAddWithoutValidation("Referer", baseUri + BasePath);
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
}

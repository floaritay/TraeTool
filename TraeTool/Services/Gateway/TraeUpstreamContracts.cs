using System.Text;
using TraeTool.Models;

namespace TraeTool.Services.Gateway;

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
    /// 非流式：消费 <see cref="StreamAsync"/> 把增量拼成一次回答，中途被截断视为失败。
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

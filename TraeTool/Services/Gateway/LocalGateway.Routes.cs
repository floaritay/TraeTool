using System.IO;
using System.Net;
using System.Text;
using System.Text.Json;
using TraeTool.Models;
using TraeTool.ViewModels;

namespace TraeTool.Services.Gateway;

/// <summary>
/// 对话端点：/v1/models 与三种协议的 /v1/*。
/// 每个端点都做同一件事：解析请求体 → 交给换号层取上游结果 → 用该协议的渲染函数写响应。
/// </summary>
public sealed partial class LocalGateway
{
    /// <summary>/v1/models：真实模型注册表（带别名），上游不可用时回退内置列表。</summary>
    private async Task HandleModelsAsync(HttpListenerContext ctx)
    {
        var cfg = _config();
        var account = _pool.Candidates(cfg, DateTime.Now).FirstOrDefault();
        IReadOnlyList<string> ids;
        ModelSources sources;
        try
        {
            (ids, sources) = await ModelCatalog.AggregateAsync(cfg, account, Transport);
        }
        catch (Exception ex)
        {
            AppLog.Info("pool", "模型目录拉取被取消：" + ex.Message);
            ids = ModelCatalog.KnownIds;
            sources = ModelCatalog.Snapshot(cfg);
        }
        AppLog.Info("pool",
            $"模型列表：人工 {sources.Manual} · 官网 {sources.Official}{(sources.Live ? "" : "(回退)")} · " +
            $"内置 {sources.Builtin} · 推断 {sources.Inferred} · 合计 {ids.Count}");

        var list = new List<string>(ids);
        if (!list.Contains(ModelCatalog.Alias, StringComparer.OrdinalIgnoreCase))
            list.Add(ModelCatalog.Alias);

        WriteRaw(ctx, 200, ChatProtocol.RenderModels(list, DateTimeOffset.UtcNow.ToUnixTimeSeconds()));
    }

    /// <summary>
    /// 解析请求体 → 记下客户端请求的模型名 → 按模型目录解析真实 model id。
    /// 只处理请求本身：不接触上游，也不写任何响应字节（换号成功前不能提交响应）。
    /// </summary>
    private async Task<(ChatRequest? Parsed, ChatUpstreamError? Error, int Status)>
        ResolveAsync(string raw, Func<string, ChatRequest> parse)
    {
        ChatRequest parsed;
        try
        {
            parsed = parse(raw);
        }
        catch (ChatRequestException cre)
        {
            return (null, new ChatUpstreamError(cre.Message, cre.Type, cre.Status), cre.Status);
        }
        catch (JsonException)
        {
            return (null, new ChatUpstreamError("请求体不是合法 JSON", "invalid_request_error", 400), 400);
        }

        var cfg = _config();
        var now = DateTime.Now;

        // 把客户端请求的模型名记下来（原样名去重、别名不记、条数有上限），供列表与名称推断使用
        var rawModel = parsed.Model;
        if (ModelCatalog.RecordRequested(cfg, rawModel)) AppServices.SaveConfig();

        var (ids, _) = await ModelCatalog.AggregateAsync(cfg, _pool.Candidates(cfg, now).FirstOrDefault(),
            Transport);
        return (parsed with { Model = ModelCatalog.Resolve(rawModel, ids) }, null, 200);
    }

    /// <summary>OpenAI 协议的对话转发（/v1/chat/completions）。</summary>
    private async Task HandleChatAsync(HttpListenerContext ctx, HttpListenerRequest req, CancellationToken ct)
    {
        var (parsed, error, status) = await ResolveAsync(await ReadBodyAsync(req), ChatProtocol.ParseRequest);
        if (parsed == null)
        {
            WriteRaw(ctx, status, ChatProtocol.RenderError(error!));
            return;
        }

        var id = "chatcmpl-" + Guid.NewGuid().ToString("N")[..24];
        var unix = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        if (parsed.Stream)
        {
            await RelayStreamAsync(ctx, parsed, new OpenAiStream(id, parsed.Model, unix), ct);
            return;
        }

        var (ok, err, st) = await CompleteBufferedAsync(parsed, ct);
        if (ok == null) WriteRaw(ctx, st, ChatProtocol.RenderError(err!));
        else WriteRaw(ctx, 200, ChatProtocol.RenderCompletion(ok, id, unix));
    }

    /// <summary>Anthropic 协议的对话转发（/v1/messages）。</summary>
    private async Task HandleMessagesAsync(HttpListenerContext ctx, HttpListenerRequest req, CancellationToken ct)
    {
        var (parsed, error, status) =
            await ResolveAsync(await ReadBodyAsync(req), AnthropicProtocol.ParseRequest);
        if (parsed == null)
        {
            WriteRaw(ctx, status, AnthropicProtocol.RenderError(error!));
            return;
        }

        var id = "msg_" + Guid.NewGuid().ToString("N")[..24];
        if (parsed.Stream)
        {
            await RelayStreamAsync(ctx, parsed, new AnthropicStream(id, parsed.Model), ct);
            return;
        }

        var (ok, err, st) = await CompleteBufferedAsync(parsed, ct);
        if (ok == null) WriteRaw(ctx, st, AnthropicProtocol.RenderError(err!));
        else WriteRaw(ctx, 200, AnthropicProtocol.RenderResponse(ok, id, parsed.Model));
    }

    /// <summary>OpenAI Responses API 的对话转发（/v1/responses）。</summary>
    private async Task HandleResponsesAsync(HttpListenerContext ctx, HttpListenerRequest req, CancellationToken ct)
    {
        var (parsed, error, status) =
            await ResolveAsync(await ReadBodyAsync(req), ResponsesProtocol.ParseRequest);
        if (parsed == null)
        {
            WriteRaw(ctx, status, ResponsesProtocol.RenderError(error!));
            return;
        }

        var id = "resp_" + Guid.NewGuid().ToString("N")[..24];
        var unix = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        if (parsed.Stream)
        {
            await RelayStreamAsync(ctx, parsed, new ResponsesStream(id, parsed.Model, unix), ct);
            return;
        }

        var (ok, err, st) = await CompleteBufferedAsync(parsed, ct);
        if (ok == null) WriteRaw(ctx, st, ResponsesProtocol.RenderError(err!));
        else WriteRaw(ctx, 200, ResponsesProtocol.RenderResponse(ok, id, unix));
    }

    private static async Task<string> ReadBodyAsync(HttpListenerRequest req)
    {
        try
        {
            using var reader = new StreamReader(req.InputStream, req.ContentEncoding ?? Encoding.UTF8);
            return await reader.ReadToEndAsync();
        }
        catch
        {
            return "";
        }
    }
}

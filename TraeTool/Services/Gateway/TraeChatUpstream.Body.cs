using System.Text.Json;

namespace TraeTool.Services.Gateway;

/// <summary>请求体映射：客户端模型名 → 上游 config_name / model_name，以及请求头里的版本标识。</summary>
public sealed partial class TraeChatUpstream
{
    /// <summary>请求体里的 ide 版本标识（与上游注册的客户端版本一致）。</summary>
    public const string IdeVersion = "0.1.50";
    public const string IdeVersionCode = "20260811";

    /// <summary>
    /// 上游 resolveByUsage 的 function 入参；取自 batch_get_detail_param 的 function_configs[0].function。
    /// 未命中 <see cref="SoloAgentModels"/> 的模型用这个视图。
    /// </summary>
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
}

using System.Text.Encodings.Web;
using System.Text.Json;

namespace TraeTool.Services.Gateway;

/// <summary>网关三种协议的响应序列化共用一套选项。</summary>
internal static class GatewayJson
{
    /// <summary>
    /// 输出序列化选项：非 ASCII 保持原样（日志与客户端易读）；
    /// 字符串内的双引号仍由 JSON 规则转义，不影响解析。
    /// </summary>
    private static readonly JsonSerializerOptions Relaxed = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static string Serialize(object payload) => JsonSerializer.Serialize(payload, Relaxed);
}

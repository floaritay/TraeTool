using System.Net.Http;
using System.Text;
using System.Text.Json;

namespace TraeTool.Services;

/// <summary>业务响应通用外壳（Trae 接口统一 code/message 约定）。</summary>
public sealed class ApiResponse
{
    public bool Ok { get; init; }
    public int Code { get; init; }
    public string? Message { get; init; }
    /// <summary>HTTP 层错误信息（非 2xx / 网络异常 / 解析失败），业务失败时为 null。</summary>
    public string? HttpError { get; set; }
    public JsonElement? Body { get; init; }

    public static ApiResponse Fail(string httpError) => new() { Ok = false, HttpError = httpError };
}

/// <summary>Trae 云 API 客户端。无账号态：token/deviceId 全部走方法参数。
/// 请求头采用最小头集；<see cref="UseFallbackHeaders"/> 打开后
/// 附加官方客户端观察头（仅当最小头被服务端拒绝时排查用，默认关闭）。</summary>
public static class TraeApiClient
{
    public const string BaseUrl = "https://api.trae.cn";
    public const int CodeDeviceRisk = 9074; // 「参与用户太多」：设备号被风控

    private static HttpClient Http => HttpClients.Default;

    /// <summary>备用全头集开关（见类注释）。默认 false。</summary>
    public static bool UseFallbackHeaders { get; set; }

    // ==================== 签到三接口 ====================

    public static Task<ApiResponse> GetStatusAsync(string token, string deviceId)
        => PostAsync("/trae/api/v2/ug/checkin_credits/status", token, deviceId, "{}");

    public static Task<ApiResponse> ClaimAsync(string token, string deviceId)
        => PostAsync("/trae/api/v2/ug/checkin_credits/claim", token, deviceId, "{}");

    /// <summary>剩余积分（各资格包 credits_limit − usage 求和）；空列表/解析失败返回 -1。</summary>
    public static async Task<double> GetRemainingCreditsAsync(string token, string deviceId)
    {
        var resp = await PostAsync("/trae/api/v2/pay/user_current_entitlement_list", token, deviceId, "{}");
        if (resp.HttpError != null || resp.Body is not { } root) return -1;
        try
        {
            if (root.ValueKind != JsonValueKind.Object ||
                !root.TryGetProperty("user_entitlement_pack_list", out var packs) ||
                packs.ValueKind != JsonValueKind.Array)
            {
                AppLog.Error("api", "entitlements 响应缺少 user_entitlement_pack_list");
                return -1;
            }
            // 空列表 = 接口异常结构（未登录/风控），按解析失败处理，避免把「解析失败」当成「0 分」清零界面。
            if (packs.GetArrayLength() == 0)
            {
                AppLog.Error("api", "entitlements 包列表为空，可能会话失效");
                return -1;
            }
            double remaining = 0;
            foreach (var p in packs.EnumerateArray())
            {
                remaining += Math.Max(0, ReadPackLimit(p) - ReadPackUsed(p));
            }
            return remaining;
        }
        catch (Exception ex)
        {
            AppLog.Error("api", "entitlements 解析失败: " + ex.Message);
            return -1;
        }

        static double ReadPackLimit(JsonElement p)
            => p.TryGetProperty("entitlement_base_info", out var info) &&
               info.TryGetProperty("quota", out var quota) &&
               quota.TryGetProperty("credits_limit", out var cl) ? ReadNumber(cl) : 0;

        static double ReadPackUsed(JsonElement p)
            => p.TryGetProperty("usage", out var usage) &&
               usage.TryGetProperty("credits_amount", out var ua) ? ReadNumber(ua) : 0;

        static double ReadNumber(JsonElement el) => el.ValueKind switch
        {
            JsonValueKind.Number => el.GetDouble(),
            JsonValueKind.String when double.TryParse(el.GetString(), out var v) => v,
            _ => 0,
        };
    }

    /// <summary>
    /// 用 X-Cloudide-Session 会话 Cookie 换取全新 JWT（网页端续期接口）。
    /// 仅 Cookie 认证，无 Authorization / x-device-id。
    /// </summary>
    public static async Task<string?> GetUserTokenAsync(string session)
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, BaseUrl + "/cloudide/api/v3/common/GetUserToken");
            req.Headers.TryAddWithoutValidation("Cookie", "X-Cloudide-Session=" + session);
            req.Headers.TryAddWithoutValidation("Referer", "https://www.trae.cn/");
            req.Headers.TryAddWithoutValidation("Origin", "https://www.trae.cn");
            using var resp = await Http.SendAsync(req);
            var json = await resp.Content.ReadAsStringAsync();
            AppLog.Info("api", $"GetUserToken -> HTTP {(int)resp.StatusCode}");
            if (!resp.IsSuccessStatusCode) return null;
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("Result", out var result) &&
                result.TryGetProperty("Token", out var tok) &&
                tok.ValueKind == JsonValueKind.String &&
                !string.IsNullOrEmpty(tok.GetString()))
            {
                return tok.GetString();
            }
            AppLog.Error("api", "GetUserToken 响应缺少 Result.Token: " + Truncate(json));
            return null;
        }
        catch (Exception ex)
        {
            AppLog.Error("api", "GetUserToken 异常: " + ex.Message);
            return null;
        }
    }

    /// <summary>拉取账号资料（GetUserInfo）：UID/昵称/脱敏手机号/头像。</summary>
    public static async Task<(string? ScreenName, string? MobileMasked, string? AvatarUrl, string? UserId)> GetUserProfileAsync(string token, string? session)
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, BaseUrl + "/cloudide/api/v3/trae/GetUserInfo");
            if (!string.IsNullOrEmpty(token))
                req.Headers.TryAddWithoutValidation("Authorization", "Cloud-IDE-JWT " + token);
            if (!string.IsNullOrEmpty(session))
                req.Headers.TryAddWithoutValidation("Cookie", "X-Cloudide-Session=" + session);
            req.Headers.TryAddWithoutValidation("Referer", "https://www.trae.cn/");
            req.Headers.TryAddWithoutValidation("Origin", "https://www.trae.cn");
            req.Content = new StringContent("{}", Encoding.UTF8, "application/json");
            using var resp = await Http.SendAsync(req);
            if (!resp.IsSuccessStatusCode) return default;
            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
            if (!doc.RootElement.TryGetProperty("Result", out var r) || r.ValueKind != JsonValueKind.Object)
                return default;
            return (Str(r, "ScreenName"), Str(r, "NonPlainTextMobile"), Str(r, "AvatarUrl"), Str(r, "UserID"));
        }
        catch (Exception ex)
        {
            AppLog.Error("api", "GetUserInfo 异常: " + ex.Message);
            return default;
        }

        static string? Str(JsonElement el, string name)
            => el.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
    }

    /// <summary>
    /// 查询学生认证状态（POST /trae/api/v3/student_verification/status，body "{}"）：
    /// Result.status == 1 表示已认证。返回 null = 查询失败/未知。
    /// </summary>
    public static async Task<bool?> GetStudentVerifiedAsync(string token, string? session)
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Post,
                BaseUrl + "/trae/api/v3/student_verification/status");
            if (!string.IsNullOrEmpty(token))
                req.Headers.TryAddWithoutValidation("Authorization", "Cloud-IDE-JWT " + token);
            if (!string.IsNullOrEmpty(session))
                req.Headers.TryAddWithoutValidation("Cookie", "X-Cloudide-Session=" + session);
            req.Headers.TryAddWithoutValidation("Referer", "https://www.trae.cn/");
            req.Headers.TryAddWithoutValidation("Origin", "https://www.trae.cn");
            req.Content = new StringContent("{}", Encoding.UTF8, "application/json");
            using var resp = await Http.SendAsync(req);
            if (!resp.IsSuccessStatusCode)
            {
                AppLog.Info("api", $"student_verification HTTP {(int)resp.StatusCode}");
                return null;
            }
            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
            // 真实信封：{"ResponseMetadata":{...},"reward_status":0,"status":0} —— status 在根级。
            var root = doc.RootElement;
            JsonElement st;
            if (!root.TryGetProperty("status", out st))
            {
                if (root.TryGetProperty("Result", out var r) && r.ValueKind == JsonValueKind.Object &&
                    r.TryGetProperty("status", out var st2)) st = st2;
                else if (root.TryGetProperty("data", out var d) && d.ValueKind == JsonValueKind.Object &&
                         d.TryGetProperty("status", out var st3)) st = st3;
                else
                {
                    AppLog.Info("api", "student_verification: 无 status 字段");
                    return null;
                }
            }
            bool? val = st.ValueKind switch
            {
                JsonValueKind.Number => st.GetInt32() == 1,
                JsonValueKind.String => st.GetString() == "1",
                JsonValueKind.True => true,
                JsonValueKind.False => false,
                _ => null,
            };
            AppLog.Info("api", $"student_verification status={st} → {(val == null ? "unknown" : val.Value.ToString())}");
            return val;
        }
        catch (Exception ex)
        {
            AppLog.Error("api", "student_verification 异常: " + ex.Message);
            return null;
        }
    }

    // ==================== 通用 POST ====================

    private static async Task<ApiResponse> PostAsync(string path, string? token, string? deviceId, string body)
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, BaseUrl + path);
            if (!string.IsNullOrEmpty(token))
                req.Headers.TryAddWithoutValidation("Authorization", "Cloud-IDE-JWT " + token);
            req.Headers.TryAddWithoutValidation("X-User-Region", "cn");
            if (!string.IsNullOrEmpty(deviceId))
                req.Headers.TryAddWithoutValidation("x-device-id", deviceId);
            ApplyFallbackHeaders(req);
            req.Content = new StringContent(body, Encoding.UTF8, "application/json");

            using var resp = await Http.SendAsync(req);
            var json = await resp.Content.ReadAsStringAsync();
            AppLog.Info("api", $"{path} -> HTTP {(int)resp.StatusCode} body={Truncate(json)}");

            if (!resp.IsSuccessStatusCode)
                return ApiResponse.Fail($"HTTP {(int)resp.StatusCode} {Truncate(json, 200)}");
            if (string.IsNullOrWhiteSpace(json))
                return ApiResponse.Fail($"HTTP {(int)resp.StatusCode} 空响应");

            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement.Clone();
            int code = root.TryGetProperty("code", out var c) && c.TryGetInt32(out var ci) ? ci : -1;
            string? msg = root.TryGetProperty("message", out var m) && m.ValueKind == JsonValueKind.String ? m.GetString() : null;
            return new ApiResponse
            {
                Ok = code == 0,
                Code = code,
                Message = msg,
                Body = root,
            };
        }
        catch (Exception ex)
        {
            AppLog.Error("api", $"{path} 异常: {ex.Message}");
            return ApiResponse.Fail(ex.Message);
        }
    }

    /// <summary>备用全头集（官方客户端完整头）。默认不启用，仅最小头被拒时打开排查。</summary>
    private static void ApplyFallbackHeaders(HttpRequestMessage req)
    {
        if (!UseFallbackHeaders) return;
        req.Headers.TryAddWithoutValidation("User-Agent", "VSCode 1.107.1 (TRAE SOLO CN)");
        req.Headers.TryAddWithoutValidation("x-market-client-id", "VSCode 1.107.1");
        req.Headers.TryAddWithoutValidation("package-type", "stable_cn");
        req.Headers.TryAddWithoutValidation("app-version", "0.1.45");
        req.Headers.TryAddWithoutValidation("x-lgw-req-sdk-type", "3");
        req.Headers.TryAddWithoutValidation("x-lscbd-aid", "787976");
        req.Headers.TryAddWithoutValidation("x-lscbd-platform", "windows");
        req.Headers.TryAddWithoutValidation("accept-language", "zh-CN");
        var ridBytes = new byte[16];
        Random.Shared.NextBytes(ridBytes);
        req.Headers.TryAddWithoutValidation("x-request-id", Convert.ToHexString(ridBytes).ToLowerInvariant());
        var traceBytes = new byte[8];
        Random.Shared.NextBytes(traceBytes);
        req.Headers.TryAddWithoutValidation("x-tt-trace-id", "00-" + Convert.ToHexString(traceBytes).ToLowerInvariant() + "-01");
    }

    private static string Truncate(string s, int max = 300)
        => s.Length <= max ? s : s[..max] + "…";
}

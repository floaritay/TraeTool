using System.Text;

namespace TraeTool.Tests;

internal static class Helpers
{
    /// <summary>构造一个只带 exp 的伪 JWT（只解 payload，不验签）。</summary>
    public static string JwtWithExpiry(DateTime local)
    {
        var seconds = new DateTimeOffset(DateTime.SpecifyKind(local, DateTimeKind.Local)).ToUnixTimeSeconds();
        var payload = $"{{\"exp\":{seconds}}}";
        var b64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(payload))
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
        return "eyJhbGciOiJub25lIn0." + b64 + ".sig";
    }

    /// <summary>构造一个带 data.id 的伪 JWT。</summary>
    public static string JwtWithUid(string uid)
    {
        var payload = $"{{\"data\":{{\"id\":\"{uid}\"}}}}";
        var b64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(payload))
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
        return "eyJhbGciOiJub25lIn0." + b64 + ".sig";
    }
}

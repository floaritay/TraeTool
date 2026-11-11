using TraeTool.Services;
using Xunit;

namespace TraeTool.Tests;

public class JwtHelperTests
{
    [Fact]
    public void ParseAccountUid_reads_data_id()
    {
        Assert.Equal("12345", JwtHelper.ParseAccountUid(Helpers.JwtWithUid("12345")));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not-a-jwt")]
    [InlineData("a.b")]                 // payload 不是合法 JSON
    public void ParseAccountUid_returns_null_for_unusable_tokens(string? token)
    {
        Assert.Null(JwtHelper.ParseAccountUid(token));
    }

    [Fact]
    public void TryGetExpiry_reads_exp()
    {
        var when = new DateTime(2030, 1, 2, 3, 4, 5);
        var exp = JwtHelper.TryGetExpiry(Helpers.JwtWithExpiry(when));

        Assert.NotNull(exp);
        Assert.Equal(when, exp!.Value, TimeSpan.FromSeconds(1));
    }

    [Fact]
    public void TryGetExpiry_returns_null_without_exp()
    {
        Assert.Null(JwtHelper.TryGetExpiry(Helpers.JwtWithUid("1")));
    }
}

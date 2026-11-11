using System.Text;
using TraeTool.Services;
using Xunit;

namespace TraeTool.Tests;

public class UpdateServiceTests
{
    [Theory]
    [InlineData("1.0.1", "1.0.0", true)]
    [InlineData("1.0.0", "1.0.0", false)]
    [InlineData("0.9.9", "1.0.0", false)]
    [InlineData("2.0", "1.9.9", true)]
    [InlineData("v1.2.0", "1.1.0", true)]
    [InlineData("  1.2.3  ", "1.2.2", true)]
    public void IsNewerVersion_compares_parsed_versions(string candidate, string current, bool expected)
    {
        Assert.Equal(expected, UpdateService.IsNewerVersion(candidate, Version.Parse(current)));
    }

    [Theory]
    [InlineData("")]
    [InlineData("  ")]
    [InlineData("not-a-version")]
    [InlineData("1.0.0-beta")]
    public void IsNewerVersion_returns_false_for_unparsable_candidate(string candidate)
    {
        Assert.False(UpdateService.IsNewerVersion(candidate, new Version(1, 0, 0)));
    }

    [Fact]
    public void Sha256Hex_matches_known_digest()
    {
        // echo -n "abc" | sha256sum
        Assert.Equal(
            "ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad",
            UpdateService.Sha256Hex(Encoding.ASCII.GetBytes("abc")));
    }

    [Fact]
    public void Sha256Hex_of_empty_input()
    {
        Assert.Equal(
            "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855",
            UpdateService.Sha256Hex(Array.Empty<byte>()));
    }

    [Fact]
    public void NormalizeHash_strips_prefix_and_takes_first_token()
    {
        var hex = new string('a', 64);

        Assert.Equal(hex, UpdateService.NormalizeHash("sha256:" + hex));
        Assert.Equal(hex, UpdateService.NormalizeHash(hex + "  TraeTool-1.0.0.zip"));
        Assert.Equal(hex, UpdateService.NormalizeHash(hex.ToUpperInvariant()));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("abc")]                 // 长度不足
    [InlineData("zzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzz")] // 非十六进制
    public void NormalizeHash_rejects_invalid_input(string? raw)
    {
        Assert.Null(UpdateService.NormalizeHash(raw));
    }
}

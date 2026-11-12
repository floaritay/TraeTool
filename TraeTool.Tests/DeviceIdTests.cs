using TraeTool.Models;
using TraeTool.Services;
using Xunit;

namespace TraeTool.Tests;

public class DeviceIdTests
{
    [Fact]
    public void Generated_ids_are_16_digits()
    {
        for (int i = 0; i < 50; i++)
        {
            var id = DeviceIdFactory.Generate();
            Assert.Equal(16, id.Length);
            Assert.All(id, c => Assert.True(char.IsDigit(c)));
        }
    }

    [Fact]
    public void GenerateUnique_avoids_ids_already_in_use()
    {
        var used = new[] { "1000000000000001", "1000000000000002" };
        var id = DeviceIdFactory.GenerateUnique(used);

        Assert.DoesNotContain(id, used);
    }

    [Theory]
    [InlineData("1000000000000001")]
    [InlineData("10000001")]                       // 下限：8 位
    [InlineData("123456789012345678901234")]       // 上限：24 位
    public void IsValid_accepts_digit_ids_within_bounds(string id)
        => Assert.True(DeviceIdFactory.IsValid(id));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("1234567")]                        // 7 位：过短
    [InlineData("1234567890123456789012345")]      // 25 位：过长
    [InlineData("100000000000000a")]               // 含非数字
    [InlineData("1000-0000-0000-0001")]            // GUID 形态会触发服务端风控
    public void IsValid_rejects_anything_else(string? id)
        => Assert.False(DeviceIdFactory.IsValid(id));

    [Fact]
    public void EnsureDeviceIdUnique_fills_missing_device_id()
    {
        var cfg = new AppSettings();
        var account = new Account();
        cfg.Accounts.Add(account);

        CheckinService.EnsureDeviceIdUnique(account, cfg);

        Assert.False(string.IsNullOrWhiteSpace(account.DeviceId));
        Assert.Equal(16, account.DeviceId!.Length);
    }

    [Fact]
    public void EnsureDeviceIdUnique_replaces_collision_with_another_account()
    {
        var shared = "1000000000000009";
        var existing = new Account { DeviceId = shared };
        var account = new Account { DeviceId = shared };
        var cfg = new AppSettings();
        cfg.Accounts.Add(existing);
        cfg.Accounts.Add(account);

        CheckinService.EnsureDeviceIdUnique(account, cfg);

        Assert.NotEqual(shared, account.DeviceId);
    }

    [Fact]
    public void EnsureDeviceIdUnique_keeps_existing_unique_id()
    {
        var account = new Account { DeviceId = "1000000000000007" };
        var cfg = new AppSettings();
        cfg.Accounts.Add(account);

        CheckinService.EnsureDeviceIdUnique(account, cfg);

        Assert.Equal("1000000000000007", account.DeviceId);
    }
}

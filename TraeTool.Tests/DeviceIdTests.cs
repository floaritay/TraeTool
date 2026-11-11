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

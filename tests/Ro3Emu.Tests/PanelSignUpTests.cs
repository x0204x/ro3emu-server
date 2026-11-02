using System.Net;
using Ro3Emu.Panel;

namespace Ro3Emu.Tests;

/// <summary>The panel's sign-up budgets (PanelSignUps): rolling windows, reservations given back, IPv6 by /64.</summary>
public sealed class PanelSignUpTests
{
    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now = new(2026, 10, 31, 23, 30, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private static readonly IPAddress A = IPAddress.Parse("203.0.113.5"), B = IPAddress.Parse("203.0.113.6");

    [Fact]
    public void OffByDefault()
    {
        var signUps = new PanelSignUps(new PanelOptions(), new Clock());
        for (int i = 0; i < 100; i++) Assert.Equal(PanelSignUps.Refusal.None, signUps.TryReserve(A, out _));
    }

    [Fact]
    public void ThePerAddressBudgetIsARolling24HoursNotACalendarDay()
    {
        var clock = new Clock();
        var signUps = new PanelSignUps(new PanelOptions { SignUpsPerAddressPerDay = 2 }, clock);
        Assert.Equal(PanelSignUps.Refusal.None, signUps.TryReserve(A, out _));
        Assert.Equal(PanelSignUps.Refusal.None, signUps.TryReserve(A, out _));
        Assert.Equal(PanelSignUps.Refusal.Address, signUps.TryReserve(A, out _));
        Assert.Equal(PanelSignUps.Refusal.None, signUps.TryReserve(B, out _));
        clock.Now = clock.Now.AddHours(1); // past midnight UTC: still the same 24 hours
        Assert.Equal(PanelSignUps.Refusal.Address, signUps.TryReserve(A, out _));
        clock.Now = clock.Now.AddHours(23);
        Assert.Equal(PanelSignUps.Refusal.None, signUps.TryReserve(A, out _));
    }

    [Fact]
    public void AReleasedSlotIsFreeAgain()
    {
        var signUps = new PanelSignUps(new PanelOptions { SignUpsPerAddressPerDay = 1, SignUpsPerHour = 1 }, new Clock());
        Assert.Equal(PanelSignUps.Refusal.None, signUps.TryReserve(A, out var slot));
        Assert.Equal(PanelSignUps.Refusal.Busy, signUps.TryReserve(B, out _));
        signUps.Release(slot);
        Assert.Equal(PanelSignUps.Refusal.None, signUps.TryReserve(A, out _));
    }

    [Fact]
    public void TheHourlyBudgetCountsEveryAddressTogether()
    {
        var clock = new Clock();
        var signUps = new PanelSignUps(new PanelOptions { SignUpsPerHour = 2 }, clock);
        Assert.Equal(PanelSignUps.Refusal.None, signUps.TryReserve(A, out _));
        Assert.Equal(PanelSignUps.Refusal.None, signUps.TryReserve(B, out _));
        Assert.Equal(PanelSignUps.Refusal.Busy, signUps.TryReserve(IPAddress.Parse("198.51.100.1"), out _));
        clock.Now = clock.Now.AddHours(1);
        Assert.Equal(PanelSignUps.Refusal.None, signUps.TryReserve(A, out _));
    }

    [Fact]
    public void AnIpv6ClientCountsByItsSlash64()
    {
        var signUps = new PanelSignUps(new PanelOptions { SignUpsPerAddressPerDay = 1 }, new Clock());
        Assert.Equal(PanelSignUps.Refusal.None, signUps.TryReserve(IPAddress.Parse("2001:db8:1:2::1"), out _));
        Assert.Equal(PanelSignUps.Refusal.Address, signUps.TryReserve(IPAddress.Parse("2001:db8:1:2:ffff::9"), out _));
        Assert.Equal(PanelSignUps.Refusal.None, signUps.TryReserve(IPAddress.Parse("2001:db8:1:3::1"), out _));
        Assert.Equal(IPAddress.Parse("203.0.113.5"), ClientAddresses.Network(IPAddress.Parse("::ffff:203.0.113.5")));
    }
}

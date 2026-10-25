using System.Net;
using Ro3Emu.Server;

namespace Ro3Emu.Tests;

public sealed class ServerOptionsTests
{
    [Theory]
    [InlineData("127.0.0.1")]
    [InlineData("::1")]
    public void LoopbackPublicHostUsesLoopbackListener(string publicHost)
    {
        var options = new ServerOptions { PublicHost = publicHost };

        Assert.Equal(IPAddress.Loopback, options.ListenAddress());
    }

    [Theory]
    [InlineData("203.0.113.7")]
    [InlineData("invalid-host")]
    public void NonLoopbackOrInvalidPublicHostUsesWildcardListener(string publicHost)
    {
        var options = new ServerOptions { PublicHost = publicHost };

        Assert.Equal(IPAddress.Any, options.ListenAddress());
    }

    [Theory]
    [InlineData("127.0.0.1", "192.0.2.5")]
    [InlineData("127.0.0.1", "0.0.0.0")]
    [InlineData("203.0.113.7", "127.0.0.1")]
    [InlineData("127.0.0.1", "::")]
    public void ExplicitBindAddressOverridesPublicHost(string publicHost, string bindAddress)
    {
        var options = new ServerOptions { PublicHost = publicHost, BindAddress = bindAddress };

        Assert.Equal(IPAddress.Parse(bindAddress), options.ListenAddress());
    }

    [Theory]
    [InlineData("127.0.0.1", "127.0.0.1")]
    [InlineData("203.0.113.7", "0.0.0.0")]
    public void EmptyBindAddressFallsBackToPublicHostRule(string publicHost, string expected)
    {
        var options = new ServerOptions { PublicHost = publicHost, BindAddress = "" };

        Assert.Equal(IPAddress.Parse(expected), options.ListenAddress());
    }

    // Retain the activities branch's interface/NAT cases under master's canonical BindAddress contract.
    [Theory]
    [InlineData("127.0.0.1")]
    [InlineData("::1")]
    [InlineData("192.0.2.10")]
    public void ListenersUseTheConfiguredInterface(string host)
    {
        Assert.Equal(IPAddress.Parse(host), new ServerOptions { BindAddress = host }.ListenAddress());
    }

    [Theory]
    [InlineData("0.0.0.0")]
    [InlineData("::")]
    [InlineData("::ffff:0.0.0.0")]
    public void ExplicitWildcardBindAddressesArePreserved(string host)
    {
        Assert.Equal(IPAddress.Parse(host), new ServerOptions { BindAddress = host }.ListenAddress());
    }

    [Fact]
    public void NatAdvertisementDoesNotBindThePublicAddress()
    {
        var options = new ServerOptions { PublicHost = "203.0.113.10" };
        Assert.Equal(IPAddress.Any, options.ListenAddress());
        options.BindAddress = "192.0.2.10";
        Assert.Equal(IPAddress.Parse("192.0.2.10"), options.ListenAddress());
        Assert.Equal("203.0.113.10", options.PublicHost);
    }

    [Theory]
    [InlineData("not-an-ip")]
    [InlineData("192.0.2.999")]
    [InlineData("127.0.0.1:5380")]
    public void InvalidExplicitBindAddressIsRejected(string bindAddress)
    {
        Assert.Throws<FormatException>(() => new ServerOptions { BindAddress = bindAddress }.ListenAddress());
    }

    [Fact]
    public void HalfRewardRatesBindAndScaleIndependently()
    {
        var options = ServerHost.ResolveOptions([
            "--Ro3Emu:RewardRates:BaseExperience=0.5",
            "--Ro3Emu:RewardRates:JobExperience=0.5",
            "--Ro3Emu:RewardRates:Zeny=0.5",
            "--Ro3Emu:RewardRates:MonsterDrops=0.5"
        ]);

        Assert.Equal(0.5m, options.RewardRates.BaseExperience);
        Assert.Equal(0.5m, options.RewardRates.JobExperience);
        Assert.Equal(0.5m, options.RewardRates.Zeny);
        Assert.Equal(0.5m, options.RewardRates.MonsterDrops);
        Assert.Equal(4, options.RewardRates.ScaleBaseExperience(8));
        Assert.Equal(4, options.RewardRates.ScaleJobExperience(8));
        Assert.Equal(4, options.RewardRates.ScaleZeny(8));
        Assert.Equal(1, options.RewardRates.ScaleMonsterDrop(2, new Random(1)));
        Assert.Equal(1, options.RewardRates.ScaleBaseExperience(1));
        Assert.Equal(1, options.RewardRates.ScaleZeny(1));
    }

    [Theory]
    [InlineData("BaseExperience", "-0.1")]
    [InlineData("JobExperience", "100.1")]
    [InlineData("Zeny", "-1")]
    [InlineData("MonsterDrops", "101")]
    public void InvalidRewardRateIsRejectedAtStartup(string field, string rate)
    {
        Assert.Throws<InvalidOperationException>(() => ServerHost.ResolveOptions([
            $"--Ro3Emu:RewardRates:{field}={rate}"
        ]));
    }

    [Fact]
    public void RewardRateBoundariesAndFractionalMonsterRemainderAreExplicit()
    {
        var disabled = new RewardRateOptions { BaseExperience = 0, JobExperience = 0, Zeny = 0, MonsterDrops = 0 };
        Assert.Equal(0, disabled.ScaleBaseExperience(99));
        Assert.Equal(0, disabled.ScaleJobExperience(99));
        Assert.Equal(0, disabled.ScaleZeny(99));
        Assert.Equal(0, disabled.ScaleMonsterDrop(1, new FixedRandom(0)));

        var half = new RewardRateOptions { MonsterDrops = 0.5m };
        Assert.Equal(1, half.ScaleMonsterDrop(1, new FixedRandom(0.49)));
        Assert.Equal(0, half.ScaleMonsterDrop(1, new FixedRandom(0.50)));

        var maximum = new RewardRateOptions { Zeny = 100m };
        Assert.Equal(int.MaxValue, maximum.ScaleZenyToInt(int.MaxValue));
        Assert.Equal(long.MaxValue, maximum.ScaleZeny(long.MaxValue));
    }

    private sealed class FixedRandom(double value) : Random
    {
        public override double NextDouble() => value;
    }
}

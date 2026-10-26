using Ro3Emu.ClientHooks;

namespace Ro3Emu.Tests;

public sealed class SkillTraceClientLimiterTests
{
    [Fact]
    public void GrantBudgetAllowsRateAndBurstThenRefillsFromMonotonicTime()
    {
        var limiter = new SkillTraceReportLimiter(2, 1, 1000);

        Assert.True(limiter.TryTake(1000));
        Assert.True(limiter.TryTake(1000));
        Assert.True(limiter.TryTake(1000));
        Assert.False(limiter.TryTake(1000));
        Assert.False(limiter.TryTake(1499));
        Assert.True(limiter.TryTake(1500));
        Assert.False(limiter.TryTake(1500));
        Assert.True(limiter.TryTake(2000));
    }

    [Fact]
    public void OnlyTheNineSkillPresentationMessagesAreTelemetry()
    {
        uint[] messages = [31100, 31106, 31121, 31107, 31101, 31119, 31109, 31120, 31108];

        Assert.All(messages, id => Assert.True(SkillTraceProtocol.IsSkillMessage(id)));
        Assert.False(SkillTraceProtocol.IsSkillMessage(0));
        Assert.False(SkillTraceProtocol.IsSkillMessage(31102));

        byte[] token = new byte[SkillTraceProtocol.TokenSize];
        byte[] report = SkillTraceProtocol.EncodeReport(new SkillTraceProtocol.Report(token, 1, 1, 0, 0,
            0, 0, 0, 0, 0, 0, 0, SkillTraceProtocol.Stage.TimelineBuilt, true, ""));
        Assert.False(SkillTraceProtocol.TryDecodeReport(report, out _));
    }
}

using Microsoft.Extensions.DependencyInjection;
using Romsg;
using Ro3Emu.Server.Data;
using Ro3Emu.Server.Data.Config;
using Ro3Emu.Server.Game;

namespace Ro3Emu.Tests;

public sealed partial class SkillCombatTests
{
    [ClientDataFact]
    public async Task BackSlideCooldownNoticeHasOneAuthoritativeEndPerClientCacheKey()
    {
        const int rowId = 1510401, group = 15104;
        var skill = Assert.IsType<SkillConfig>(SkillConfig.Get(_app.Services.GetRequiredService<GameData>(), rowId));
        Assert.Equal(15000, skill.Cd);
        await using var alice = await InWorld("Alice");
        await Gm("alice", "!job 5100"); await Teach("alice", group);

        await alice.SendGameAsync(CombatService.UseSkill, new CastSkillReq { SkillId = rowId });
        Assert.Equal(0, (await Next(alice, CombatService.UseSkill, CastSkillRsp.Parser)).Result);
        var notice = await Next(alice, CombatService.SkillCdUpdate, SkillCdNotify.Parser,
            n => n.Infos.Any(info => info.SkillId == group));
        Assert.Equal(notice.Infos.Count, notice.Infos.Select(info => (info.SkillId, info.SkillMode)).Distinct().Count());
        var own = Assert.Single(notice.Infos, info => info.SkillId == group && info.SkillMode == 0);
        Assert.Equal(skill.Cd, own.CdEndTimestampMs - own.CdStartTimestampMs);
        await Loop.InvokeAsync(() =>
        {
            var player = Session("alice");
            Assert.Equal(Math.Max(player.SkillCds[group], player.SkillModeCds[(group, 0)]), own.CdEndTimestampMs);
            return Task.CompletedTask;
        });
    }

    [ClientDataTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ShortSharedCooldownNoticeKeepsAnExistingLongerGroupOrModeEnd(bool shared)
    {
        const int backSlide = 1510401, group = 15104, sibling = 15330;
        await using var alice = await InWorld("Alice");
        await Gm("alice", "!job 5100"); await Teach("alice", group);
        long longerEnd = MonsterService.NowMs + 60_000;
        await Loop.InvokeAsync(() =>
        {
            var player = Session("alice");
            if (shared) player.SkillCds[sibling] = longerEnd;
            else player.SkillModeCds[(sibling, 0)] = longerEnd;
            return Task.CompletedTask;
        });

        await alice.SendGameAsync(CombatService.UseSkill, new CastSkillReq { SkillId = backSlide });
        Assert.Equal(0, (await Next(alice, CombatService.UseSkill, CastSkillRsp.Parser)).Result);
        var notice = await Next(alice, CombatService.SkillCdUpdate, SkillCdNotify.Parser,
            n => n.Infos.Any(info => info.SkillId == sibling));
        var kept = Assert.Single(notice.Infos, info => info.SkillId == sibling && info.SkillMode == 0);
        Assert.Equal(longerEnd, kept.CdEndTimestampMs);
        await Loop.InvokeAsync(() =>
        {
            var player = Session("alice");
            Assert.Equal(Math.Max(player.SkillCds[sibling], player.SkillModeCds[(sibling, 0)]), kept.CdEndTimestampMs);
            return Task.CompletedTask;
        });
    }
}

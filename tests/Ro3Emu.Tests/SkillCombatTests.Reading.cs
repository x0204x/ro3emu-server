using Microsoft.Extensions.DependencyInjection;
using Romsg;
using Ro3Emu.Server.Data;
using Ro3Emu.Server.Data.Config;
using Ro3Emu.Server.Game;

namespace Ro3Emu.Tests;

public sealed partial class SkillCombatTests
{
    [ClientDataTheory]
    [InlineData(1231201, 3)] // Crimson Rock: "consumes 3 stacks"
    [InlineData(1231601, 5)] // Tetra Vortex
    [InlineData(1230201, 1)] // Soul Expansion
    [InlineData(1234201, 5)] // Gravitational Impact
    public async Task ReadingCostIsWhatTheRowAsks(int skill, int pages)
    {
        await using var alice = await InWorld("Alice");
        Assert.Equal(pages, CombatService.ReadingCost(SkillConfig.Get(_app.Services.GetRequiredService<GameData>(), skill)!.Value.Row));
    }

    private async Task<(int Hits, long Pages)> CrimsonRockWhileReading(long pagesHeld, bool reading)
    {
        await using var alice = await InWorld("Alice");
        await Gm("alice", "!lvl 99"); await Gm("alice", "!job 2400"); await Teach("alice", 12312);
        var a = Session("alice");
        a.GmCastSkill = 1231201;
        _app.Services.GetRequiredService<CombatService>().Rolls = new CritRolls(1);
        var dummy = await Target("alice", stationary: true, npcId: 5000002);
        await Loop.InvokeAsync(async () =>
        {
            dummy.StaysOutOfBattle = true; dummy.Hp = 1000000;
            dummy.X = a.Position.X + 1; dummy.Y = a.Position.Y; dummy.Z = a.Position.Z;
            a.Energy[CombatService.BookPages].Value = pagesHeld;
            a.Energy[CombatService.BookPages].NextRecoverMs = MonsterService.NowMs + 60000;
            if (reading) await _app.Services.GetRequiredService<BuffService>().AddAsync(a, CombatService.ReadingBuff, 60000, a.Role!.Id);
        });
        await alice.SendGameAsync(CombatService.UseSkill, new CastSkillReq { SkillId = 1231201, TargetId = dummy.Uid, TargetPos = dummy.Pos, Face = 90 });
        Assert.Equal(0, (await Next(alice, CombatService.UseSkill, CastSkillRsp.Parser)).Result);
        await Next(alice, CombatService.SkillHit, SkillHitInfo.Parser, h => h.Dsts.Any(d => d.DstID == dummy.Uid));
        int hits = 1;
        long before = dummy.Hp;
        await Task.Delay(1500);
        if (dummy.Hp < before) hits++;
        return (hits, a.Energy[CombatService.BookPages].Value);
    }

    [ClientDataFact] // "triggers the corresponding skill effect one Extra time and consumes the corresponding Floors"
    public async Task ReadingCastsAgainAndSpendsPages()
    {
        var (hits, pages) = await CrimsonRockWhileReading(8, reading: true);
        Assert.Equal(2, hits);
        Assert.Equal(5, pages);
    }

    [ClientDataFact] // "cannot be triggered without enough Book Pages"
    public async Task ReadingWithoutPagesCastsOnce()
    {
        var (hits, pages) = await CrimsonRockWhileReading(2, reading: true);
        Assert.Equal(1, hits);
        Assert.Equal(2, pages);
    }

    [ClientDataFact]
    public async Task WithoutReadingNothingIsSpent()
    {
        var (hits, pages) = await CrimsonRockWhileReading(8, reading: false);
        Assert.Equal(1, hits);
        Assert.Equal(8, pages);
    }
}

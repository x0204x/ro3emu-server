using Microsoft.Extensions.DependencyInjection;
using Ro3Emu.Server.Data;
using Ro3Emu.Server.Game;

namespace Ro3Emu.Tests;

public sealed partial class SkillCombatTests
{
    private const int ShieldPress = 1160801, EarthDrive = 1161101, SoulStrike = 1200801, Magnus = 1410901, HolyCross = 1141101, Ambush = 1500401, BowlingBash = 1110411;

    private async Task<int> BranchOn(GameSession s, int skill, int race, int element, bool playerTarget = false)
    {
        var target = await Target("alice", stationary: true);
        var row = _app.Services.GetRequiredService<GameData>().Row("SkillConfig", skill)!.Value;
        return await Loop.InvokeAsync(() =>
        {
            target.Template = target.Template with { Race = race, Element = element };
            return Task.FromResult(_app.Services.GetRequiredService<CombatService>().DamageBranch(s, playerTarget ? null : target, row));
        });
    }

    [ClientDataFact]
    public async Task AHitTakesTheValueItsTextNames()
    {
        await using var alice = await InWorld("Alice");
        var a = Session("alice");
        Assert.Equal(1, await BranchOn(a, ShieldPress, 1, 1)); // "Against Monsters"
        Assert.Equal(1, await BranchOn(a, EarthDrive, 1, 1));
        Assert.Equal(0, await BranchOn(a, ShieldPress, 1, 1, playerTarget: true));
        Assert.Equal(0, await BranchOn(a, SoulStrike, 1, 1));
        Assert.Equal(1, await BranchOn(a, SoulStrike, 5, 1)); // "Undead enemies": the race
        Assert.Equal(1, await BranchOn(a, SoulStrike, 1, 10)); // or the property
        Assert.Equal(0, await BranchOn(a, Magnus, 1, 1));
        Assert.Equal(1, await BranchOn(a, Magnus, 3, 1)); // Demon Race
        Assert.Equal(1, await BranchOn(a, Magnus, 1, 8)); // Shadow property
        Assert.Equal(0, await BranchOn(a, BowlingBash, 5, 10)); // its text names no condition
        Assert.Equal(0, await BranchOn(a, HolyCross, 1, 1));
        Assert.Equal(0, await BranchOn(a, Ambush, 1, 1));
    }

    [ClientDataFact]
    public async Task HolyCrossReadsATwoHandedSpear()
    {
        await using var alice = await InWorld("Alice");
        var a = Session("alice");
        var data = _app.Services.GetRequiredService<GameData>();
        int spear = data.Table("ItemConfig").Values.Select(r => new Ro3Emu.Server.Data.Config.ItemConfig(r))
            .First(i => i.SubType == 5).Id is var id ? (int)id : 0;
        await Loop.InvokeAsync(() =>
        {
            a.Items.Add(new Ro3Emu.Server.Accounts.InvItem { Guid = 990777, Entry = spear, Stack = 1, Grid = 1 });
            return Task.CompletedTask;
        });
        Assert.Equal(1, await BranchOn(a, HolyCross, 1, 1));
    }

    [ClientDataFact]
    public async Task AmbushTakesItsSecondValueWhileHiding()
    {
        await using var alice = await InWorld("Alice");
        var a = Session("alice");
        await Loop.InvokeAsync(() => _app.Services.GetRequiredService<BuffService>().AddAsync(a, 1500602, 60000, a.Role!.Id));
        Assert.Equal(1, await BranchOn(a, Ambush, 1, 1));
    }

    /// <summary>The whole way: Shield Press hits a monster for 240% of ATK, not the 120% it does to a player.</summary>
    [ClientDataFact]
    public async Task ShieldPressHitsAMonsterForItsSecondValue()
    {
        await using var alice = await InWorld("Alice");
        await Gm("alice", "!job 1100"); await Teach("alice", 11608);
        _app.Services.GetRequiredService<CombatService>().Rolls = new CritRolls(1);
        var a = Session("alice");
        a.GmCombat |= GmCombatSwitches.FixedDamage;
        var target = await Target("alice", stationary: true);
        await Loop.InvokeAsync(() =>
        {
            target.Template = target.Template with { Def = 0, Mdef = 0, Element = 1 };
            target.X = a.Position.X + 1;
            target.Z = a.Position.Z;
            return Task.CompletedTask;
        });
        long atk = Character.Stats(a)[AttributeId.Atk];
        await alice.SendGameAsync(CombatService.UseSkill, new Romsg.CastSkillReq { SkillId = ShieldPress, TargetId = target.Uid, TargetPos = target.Pos.Clone() });
        var accepted = await Next(alice, CombatService.UseSkill, Romsg.CastSkillRsp.Parser);
        Assert.Equal(0, accepted.Result);
        var hit = await Next(alice, CombatService.SkillHit, Romsg.SkillHitInfo.Parser, h => h.SessionId == accepted.SessionId && h.Dsts.Count > 0);
        Assert.InRange(hit.Dsts[0].Damage, atk * 2 * 95 / 100, atk * 2 * 125 / 100);
    }
}

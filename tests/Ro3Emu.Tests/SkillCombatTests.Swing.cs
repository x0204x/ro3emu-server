using Microsoft.Extensions.DependencyInjection;
using Romsg;
using Ro3Emu.Server.Game;

namespace Ro3Emu.Tests;

/// <summary>
/// Issue #121: a normal attack (_bCancelCast 1) waits for the swing before it at the same target, so a burst of clicks
/// does not hit at click speed; the next swing within CombatService.SwingJitterMs of the end is taken, as network jitter can bring a
/// legitimate one that early.
/// </summary>
public sealed partial class SkillCombatTests
{
    private const int NoviceAttack = 1000001;
    private const int ArcherAttack = 1300001;

    [ClientDataFact]
    public async Task ANormalAttackWaitsForTheSwingBeforeIt()
    {
        await using var alice = await InWorld("Alice");
        var target = await Target("alice", stationary: true);
        await Loop.InvokeAsync(() => { target.Hp = 1_000_000; return Task.CompletedTask; });
        var session = Session("alice");

        await alice.SendGameAsync(CombatService.UseSkill, new CastSkillReq { SkillId = NoviceAttack, TargetId = target.Uid });
        Assert.Equal(0, (await Next(alice, CombatService.UseSkill, CastSkillRsp.Parser)).Result);
        long end = await Loop.InvokeAsync(() => Task.FromResult(
            session.SkillCast!.StartMs + session.SkillCast.ChantMs + session.SkillCast.ActionMs));

        // A second click in the middle of the swing is refused.
        await alice.SendGameAsync(CombatService.UseSkill, new CastSkillReq { SkillId = NoviceAttack, TargetId = target.Uid });
        Assert.Equal(ErrorCodes.SkillBusy, (await Next(alice, CombatService.UseSkill, CastSkillRsp.Parser)).Result);

        // Just inside the slack before the swing ends, the next one is taken.
        long wait = end - CombatService.SwingJitterMs + 40 - MonsterService.NowMs;
        if (wait > 0) await Task.Delay(TimeSpan.FromMilliseconds(wait));
        Assert.True(MonsterService.NowMs < end, "the test ran past the swing; nothing left to check");
        await alice.SendGameAsync(CombatService.UseSkill, new CastSkillReq { SkillId = NoviceAttack, TargetId = target.Uid });
        Assert.Equal(0, (await Next(alice, CombatService.UseSkill, CastSkillRsp.Parser)).Result);

        // Turning to another target during that swing is the client's own business (it plays the swing out).
        var other = await Target("alice", stationary: true);
        await Loop.InvokeAsync(() => { other.Hp = 1_000_000; return Task.CompletedTask; });
        await alice.SendGameAsync(CombatService.UseSkill, new CastSkillReq { SkillId = NoviceAttack, TargetId = other.Uid });
        Assert.Equal(0, (await Next(alice, CombatService.UseSkill, CastSkillRsp.Parser)).Result);
    }

    [ClientDataFact]
    public async Task ASwingTakenInTheSlackStartsWhenTheOneBeforeEnds()
    {
        // Holding the key sends a request every 200 ms; started at once, the slack came off every swing.
        await using var alice = await InWorld("Alice");
        var target = await Target("alice", stationary: true);
        await Loop.InvokeAsync(() => { target.Hp = 1_000_000; return Task.CompletedTask; });
        var session = Session("alice");
        await alice.SendGameAsync(CombatService.UseSkill, new CastSkillReq { SkillId = NoviceAttack, TargetId = target.Uid });
        Assert.Equal(0, (await Next(alice, CombatService.UseSkill, CastSkillRsp.Parser)).Result);
        long end = await Loop.InvokeAsync(() => Task.FromResult(
            session.SkillCast!.StartMs + session.SkillCast.ChantMs + session.SkillCast.ActionMs));
        long wait = end - CombatService.SwingJitterMs + 40 - MonsterService.NowMs;
        if (wait > 0) await Task.Delay(TimeSpan.FromMilliseconds(wait));
        Assert.True(MonsterService.NowMs < end, "the test ran past the swing; nothing left to check");
        await alice.SendGameAsync(CombatService.UseSkill, new CastSkillReq { SkillId = NoviceAttack, TargetId = target.Uid });
        Assert.Equal(0, (await Next(alice, CombatService.UseSkill, CastSkillRsp.Parser)).Result);
        long next = await Loop.InvokeAsync(() => Task.FromResult(session.SkillCast!.StartMs));
        Assert.True(next >= end, $"the next swing started {end - next} ms before the one before it ended");
    }

    [ClientDataFact]
    public async Task ClicksAlternatingBetweenTwoTargetsDoNotSwingFaster()
    {
        // A modified client restarted the swing with every click by alternating its target: hits at click speed. A turn
        // to another target is taken, but its swing starts when the one in flight ends (CombatService.SwingTurnLeft).
        await using var alice = await InWorld("Alice");
        var a = await Target("alice", stationary: true);
        var b = await Target("alice", stationary: true);
        await Loop.InvokeAsync(() => { a.Hp = b.Hp = 1_000_000_000; return Task.CompletedTask; });
        long self = await Loop.InvokeAsync(() => Task.FromResult(Session("alice").Role!.Id));
        await alice.SendGameAsync(CombatService.UseSkill, new CastSkillReq { SkillId = NoviceAttack, TargetId = a.Uid });
        Assert.Equal(0, (await Next(alice, CombatService.UseSkill, CastSkillRsp.Parser)).Result);
        int action = await Loop.InvokeAsync(() => Task.FromResult(Session("alice").SkillCast!.ActionMs));
        var started = DateTime.UtcNow;
        for (int click = 1; DateTime.UtcNow - started < TimeSpan.FromSeconds(3); click++)
        {
            await Task.Delay(100);
            await alice.SendGameAsync(CombatService.UseSkill, new CastSkillReq { SkillId = NoviceAttack, TargetId = click % 2 == 0 ? a.Uid : b.Uid });
        }
        await Task.Delay(action + 500);
        int hits = alice.Inbox.Snapshot().Count(e => e.IsGame(CombatService.SkillHit) && SkillHitInfo.Parser.ParseFrom(e.Body).SrcID == self);
        // At most one swing per action time over the 3 s and the wait after them, the first one included.
        Assert.InRange(hits, 0, (int)Math.Ceiling((3000.0 + action + 500) / action) + 1);
    }

    [ClientDataFact]
    public async Task AnArcherNormalAttackWaitsForTheSwingBeforeIt()
    {
        await using var archer = await InWorld("Archer");
        await Gm("archer", "!job 3100");
        // JobAttackConfig[3100] 對無武器選 1000001；1300001 是裝備 subtype 11 弓時的普攻。
        await Gm("archer", $"!item {ArcherBow}");
        var granted = await Next(archer, CharacterService.ItemsSync, DBItemsInfoIncrement.Parser,
            m => m.PackInfo.GetValueOrDefault(2)?.Inc?.Items.Values.Any(i => i.All?.ItemEntry == ArcherBow) == true);
        long bow = granted.PackInfo[2].Inc.Items.Values.Single(i => i.All?.ItemEntry == ArcherBow).All.ItemGuid;
        await archer.SendGameAsync(CharacterService.EquipItemOpr,
            new EquipItemOprReq { Opr = 0, Pos = (EquipGridType)1, ItemGUID = bow });
        Assert.Equal(0, (await Next(archer, CharacterService.EquipItemOpr, EquipItemOprResp.Parser)).Result);
        var target = await Target("archer", stationary: true);
        await Loop.InvokeAsync(() => { target.Hp = 1_000_000; return Task.CompletedTask; });
        var session = Session("archer");
        Assert.Equal(ArcherAttack, _app.Services.GetRequiredService<SkillService>().NormalAttack(session));

        await archer.SendGameAsync(CombatService.UseSkill, new CastSkillReq { SkillId = ArcherAttack, TargetId = target.Uid });
        Assert.Equal(0, (await Next(archer, CombatService.UseSkill, CastSkillRsp.Parser)).Result);
        long end = await Loop.InvokeAsync(() => Task.FromResult(
            session.SkillCast!.StartMs + session.SkillCast.ChantMs + session.SkillCast.ActionMs));

        // A second click in the middle of the swing is refused (SkillBusy 3022), even though HunterAttack has _iIsNormalAtk == 0.
        await archer.SendGameAsync(CombatService.UseSkill, new CastSkillReq { SkillId = ArcherAttack, TargetId = target.Uid });
        Assert.Equal(ErrorCodes.SkillBusy, (await Next(archer, CombatService.UseSkill, CastSkillRsp.Parser)).Result);

        // Just inside the slack before the swing ends, the next one is taken.
        long wait = end - CombatService.SwingJitterMs + 40 - MonsterService.NowMs;
        if (wait > 0) await Task.Delay(TimeSpan.FromMilliseconds(wait));
        Assert.True(MonsterService.NowMs < end, "the test ran past the swing; nothing left to check");
        await archer.SendGameAsync(CombatService.UseSkill, new CastSkillReq { SkillId = ArcherAttack, TargetId = target.Uid });
        Assert.Equal(0, (await Next(archer, CombatService.UseSkill, CastSkillRsp.Parser)).Result);
    }

    /// <summary>LC_Skill_RunTimeContext.SkillCanInterrupted: the running skill decides, not _bCancelCast of the next one.</summary>
    [ClientDataFact]
    public async Task ARunningCastGivesWayByItsOwnInterruptRule()
    {
        const int SwordAttack = 1001001; // JobAttackConfig 職業 1100 的普攻，_iInterruptedType 1。
        const int Bash = 1100101; // _iInterruptedType 2, _bCancelCast 0, its show can be interrupted 400 ms into its 1000 ms
        await using var alice = await InWorld("Alice");
        await Gm("alice", "!lvl 14");
        await Gm("alice", "!job 1100"); await Teach("alice", 11001);
        var target = await Target("alice", stationary: true);
        await Loop.InvokeAsync(() => { target.Hp = 1_000_000; return Task.CompletedTask; });
        var session = Session("alice");
        async Task<int> Cast(int skill)
        {
            await alice.SendGameAsync(CombatService.UseSkill, new CastSkillReq { SkillId = skill, TargetId = target.Uid });
            return (await Next(alice, CombatService.UseSkill, CastSkillRsp.Parser)).Result;
        }

        // A skill cuts a swing at once, whatever its own _bCancelCast.
        Assert.Equal(0, await Cast(SwordAttack));
        Assert.Equal(0, await Cast(Bash));
        long start = await Loop.InvokeAsync(() => Task.FromResult(session.SkillCast!.StartMs));

        // Before Bash's interrupt time neither it nor a swing gets in.
        Assert.Equal(ErrorCodes.SkillBusy, await Cast(Bash));
        Assert.Equal(ErrorCodes.SkillBusy, await Cast(SwordAttack));
        Assert.True(MonsterService.NowMs < start + 400 - CombatService.SwingJitterMs, "the test ran past the interrupt time; nothing left to check");

        // From there it gives way.
        await Task.Delay(TimeSpan.FromMilliseconds(Math.Max(0, start + 420 - MonsterService.NowMs)));
        Assert.Equal(0, await Cast(SwordAttack));
    }
}

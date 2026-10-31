using Microsoft.Extensions.Options;
using Romsg;
using Ro3Emu.Bot;
using Ro3Emu.Server;
using Ro3Emu.Server.Data;
using Ro3Emu.Server.Game;
using Ro3Emu.Server.Maps;

namespace Ro3Emu.Tests;

/// <summary>
/// 虎王原服錄製的12名冒險者（NPC5300007/8/9、5300070–78）是戰鬥同伴；保亞與唔勒仍為場景角色。
/// 驗證同行AI、傷害歸屬、死亡與清理。完整技能表來自本次事件繫結，技能優先序及支援技仍非完整原服AI。
/// </summary>
public sealed partial class DungeonTests
{
    private const long PayonForest = 1022001;
    private const int Eddga = 5300001, Wule = 5300086, Boya = 5300087, Warrior = 5300007;
    private static readonly int[] AdventurerIds = [5300007, 5300008, 5300009, 5300070, 5300071, 5300072, 5300073, 5300074, 5300075, 5300076, 5300077, 5300078];
    /// <summary>The scene's born point (sc_pay_fild10_001_pl01 m_kBornPoints 出生点).</summary>
    private const long BornPoint = 16;

    [ClientDataFact]
    public void OnlyAPlayerSideMonsterNpcWithItsOwnSkillsCanBeACompanion()
    {
        var data = Service<GameData>();
        var monsters = Service<MonsterService>();
        var warrior = Assert.IsType<CompanionUnit.Kit>(CompanionUnit.Build(data, monsters, Warrior));
        // 本次原服actor-bound 5330101–5330401；數值仍讀SkillConfig及AI5300006。
        Assert.Equal([5330101, 5330201, 5330301, 5330401], warrior.Skills.Select(s => s.Id));
        Assert.Equal([0, 3000, 4800, 6000], warrior.Skills.Select(s => s.CooldownMs));
        Assert.Equal([3f, 3f, 3.5f, 5f], warrior.Skills.Select(s => s.Range));
        Assert.Equal((100, 1000, 99999.99f), (warrior.SearchMs, warrior.AttackTickMs, warrior.ViewRange));
        Assert.Equal((1, 20), (warrior.Template.Camp, warrior.Template.Level));
        Assert.Equal(Assert.IsType<MonsterTemplate>(monsters.Template(Warrior)).MaxHp, warrior.Template.MaxHp);
        Assert.All(AdventurerIds, id => Assert.NotNull(CompanionUnit.Build(data, monsters, id)));
        // 舊有明列_kSkills路徑仍成立；不再把此兩人當作本場實際參戰同伴。
        Assert.NotNull(CompanionUnit.Build(data, monsters, Wule));
        Assert.NotNull(CompanionUnit.Build(data, monsters, Boya));
        Assert.Null(CompanionUnit.Build(data, monsters, Eddga)); // camp 2, no skills of its own
        Assert.Null(CompanionUnit.Build(data, monsters, 5300080)); // the hostile Wule, camp 3

        // 12名冒險者的編輯器出生點；保亞/唔勒是另外兩個場景角色。
        var plan = Service<DungeonCatalog>().Resolve(new EnterDungeonReq { SceneEntry = PayonForest })!;
        var step = plan.Waves[1].Step!;
        Assert.Equal([(Warrior, 34L), (5300009, 35L), (5300008, 36L), (5300070, 37L), (5300071, 38L), (5300072, 39L),
            (5300073, 41L), (5300074, 42L), (5300075, 43L), (5300076, 44L), (5300077, 45L), (5300078, 46L)],
            step.Companions.Select(c => (c.NpcId, c.Point)));
        Assert.Equal([5300080, 5300081], step.Actors.Select(a => a.NpcId).Order());
        // 不容許單憑NPC號碼讓未知AI進入fallback。
        ChangeRow("NPCConfig", Warrior.ToString(System.Globalization.CultureInfo.InvariantCulture), row => row["_iAISpecial"] = "UnknownStoryAI");
        Assert.Null(CompanionUnit.Build(data, monsters, Warrior));
        Assert.Null(Service<DungeonCatalog>().Resolve(new EnterDungeonReq { SceneEntry = PayonForest }));
    }

    [ClientDataFact]
    public void CapturedCompanionsKeepSupportSkillsButNeverChooseThemAsEnemyDamage()
    {
        var data = Service<GameData>();
        var monsters = Service<MonsterService>();
        var priest = Assert.IsType<CompanionUnit.Kit>(CompanionUnit.Build(data, monsters, 5300009));
        Assert.Equal([5330501, 5330601, 5330701, 5330801, 5331301], priest.Skills.Select(s => s.Id));
        Assert.All(priest.Skills, skill => Assert.False(skill.CanDamageEnemy));
        Assert.Null(CompanionUnit.Pick(priest, new Dictionary<int, long>(), 1000, 1));
        var target = Assert.IsType<MonsterTemplate>(monsters.Template(Eddga));
        Assert.Equal(0, Service<CombatService>().CompanionDps(priest, target));
        var warrior = Assert.IsType<CompanionUnit.Kit>(CompanionUnit.Build(data, monsters, Warrior));
        Assert.True(Service<CombatService>().CompanionDps(warrior, target) > 0);
        Assert.True(warrior.RotateAttackSkills); // 明示inferred的公平輪替；不是原服優先序驗收。
        Assert.Equal(5330101, Assert.IsType<CompanionUnit.Skill>(CompanionUnit.Pick(warrior, new Dictionary<int, long>(), 1000, 1)).Id);
        Assert.Equal(5330201, Assert.IsType<CompanionUnit.Skill>(CompanionUnit.Pick(warrior, new Dictionary<int, long>(), 1000, 1, 5330101)).Id);
        var hunter = Assert.IsType<CompanionUnit.Kit>(CompanionUnit.Build(data, monsters, 5300070));
        Assert.False(hunter.Skills.Single(s => s.Id == 5331701).CanDamageEnemy);
    }

    [ClientDataFact]
    public async Task CapturedSupportNeverDamagesAnEnemyAndARemovedCasterCannotLandItsPendingHit()
    {
        await using var alice = await InWorld("Alice");
        var (eddga, companions) = await EddgaStep(alice);
        var priest = companions.Single(c => c.NpcId == 5300009);
        var warrior = companions.Single(c => c.NpcId == Warrior);
        long hp = 0;
        await OnLoop(async () =>
        {
            // 隔離本測試的來源，不讓其他已排程同伴傷害影響待驗證的兩條路徑。
            foreach (var unit in companions) unit.Dead = true;
            priest.Dead = false;
            hp = eddga.Hp;
            await Service<CombatService>().CompanionAttackAsync(priest, eddga, priest.Spec.Skills[0]);
            Assert.Equal(hp, eddga.Hp);
            warrior.Dead = false;
            (warrior.X, warrior.Y, warrior.Z) = (eddga.X + 1, eddga.Y, eddga.Z);
            await Service<CombatService>().CompanionAttackAsync(warrior, eddga, warrior.Spec.Skills[0]);
            await Service<World>().RemoveUnitAsync(warrior);
        });
        await Task.Delay(warrior.Spec.Skills[0].ActionMs + 300);
        Assert.Equal(hp, await OnLoopValue(() => eddga.Hp));
        Assert.DoesNotContain(alice.Inbox.Snapshot().Where(e => e.IsGame(CombatService.UseInSkill))
            .Select(e => UseInSkillInfo.Parser.ParseFrom(e.Body)), e => e.SrcID == priest.Uid);
    }

    [ClientDataFact]
    public async Task TheTwelveAdventurersFightEddgaAndTheirKillWinsTheStep()
    {
        await using var alice = await InWorld("Alice");
        var (eddga, companions) = await EddgaStep(alice);
        var map = Service<WorldData>().Map(PayonForest)!;
        Assert.Equal(AdventurerIds, companions.Select(c => c.NpcId).Order());
        Assert.All(companions, c => Assert.Equal((map.Points[c.Point].X, map.Points[c.Point].Z), (c.HomeX, c.HomeZ)));
        var warrior = companions.Single(c => c.NpcId == Warrior);
        long me = Session(alice).Role!.Id;
        var born = map.Points[BornPoint]; // about 20 m from either companion's point: Alice is not beside them
        Assert.All(companions, c => Assert.True(MonsterService.Distance(born.X, born.Z, c.HomeX, c.HomeZ) > 15));

        // A player cannot target a companion: its cast finds no monster there, and nothing hits it.
        await alice.SendGameAsync(CombatService.UseSkill, new CastSkillReq { SkillId = 1000001, TargetId = warrior.Uid });
        await Next(alice, CombatService.UseSkill, CastSkillRsp.Parser);

        // Alice留在出生點，冒險者接近虎王並施放已確認對敵的技能。
        var walked = await Next(alice, GameAoiSink.MovementChange, MovementChangeNtf.Parser,
            m => m.Uid == warrior.Uid && m.MovementInfo.MoveType == MovementType.MoveTypeWalk
                && MonsterService.Distance(m.MovementInfo.Pos.X, m.MovementInfo.Pos.Z, warrior.HomeX, warrior.HomeZ) > 1);
        var cast = await Next(alice, CombatService.UseInSkill, UseInSkillInfo.Parser,
            u => u.SrcID == warrior.Uid && u.TargetId == eddga.Uid && u.StartTime >= walked.MovementInfo.ServerTime);
        Assert.True(cast.PlayAct);
        Assert.True(cast.SkillMs > 0);
        Assert.True(cast.StartTime > 0);
        await Next(alice, CombatService.SkillHit, SkillHitInfo.Parser,
            h => h.SrcID == warrior.Uid && h.SessionId == cast.SessionId
                && h.Dsts.Any(d => d.DstID == eddga.Uid && d.Damage > 0));
        // 十二人有遠程職業，不能把任一人的移動和另一人的施放混接；也不以事後位置代替施放時快照。
        Vec3? castAt = null;
        foreach (var item in alice.Inbox.Snapshot())
        {
            if (item.IsGame(GameAoiSink.MovementChange) && MovementChangeNtf.Parser.ParseFrom(item.Body) is { } move && move.Uid == warrior.Uid)
                castAt = move.MovementInfo.Pos;
            if (item.IsGame(CombatService.UseInSkill) && UseInSkillInfo.Parser.ParseFrom(item.Body) is { } shown
                && shown.SrcID == warrior.Uid && shown.SessionId == cast.SessionId) break;
        }
        Assert.NotNull(castAt);
        Assert.True(MonsterService.Distance(castAt.X, castAt.Z, warrior.HomeX, warrior.HomeZ) > 1,
            "this warrior's movement snapshot before this cast is away from home");
        Assert.True(await OnLoopValue(() => MonsterService.Distance(Session(alice).Position.X, Session(alice).Position.Z, born.X, born.Z) < 1));
        Assert.True(await OnLoopValue(() => eddga.Hp < eddga.Template.MaxHp));
        Assert.DoesNotContain(alice.Inbox.Snapshot().Where(e => e.IsGame(CombatService.SkillHit))
            .Select(e => SkillHitInfo.Parser.ParseFrom(e.Body)), h => h.SrcID == me && h.Dsts.Any(d => d.DstID == warrior.Uid));
        Assert.Equal(warrior.Template.MaxHp, await OnLoopValue(() => warrior.Hp));

        // With no living monster in the room, each companion walks back to its point (its HP restored after).
        long eddgaHp = await OnLoopValue(() => eddga.Hp);
        await OnLoop(() => { eddga.Hp = 0; return Task.CompletedTask; });
        await Until(() => companions.All(c => MonsterService.Distance(c.X, c.Z, c.HomeX, c.HomeZ) < 0.5f), "the adventurers are back at their points");
        await OnLoop(() => { eddga.Hp = eddgaHp; return Task.CompletedTask; });

        // A companion's kill counts like the player's: the step completes, the run is won, the kill is Alice's.
        await OnLoop(() => { eddga.Hp = 1; return Task.CompletedTask; });
        await Next(alice, CombatService.SkillHit, SkillHitInfo.Parser,
            h => companions.Any(c => c.Uid == h.SrcID) && h.Dsts.Any(d => d.DstID == eddga.Uid && d.IsDead));
        Assert.Equal(1, (await Next(alice, DungeonService.ResultNotify, DungeonResultNotify.Parser)).ResultInfo.WinCamp);
        var attempt = Session(alice).DungeonAttempt!;
        Assert.Equal(1, attempt.Stats[me].KillNpcData[Eddga].LongValue);
        Assert.Equal(attempt.Spawned, attempt.Killed.Count); // the companions are not room monsters
        // 結算當下保留虎王屍體；其後由body timer或正式任務轉場清理。
        Assert.Contains(Service<World>().AllUnits(), u => u.Uid == eddga.Uid);
        await Until(() => Service<World>().AllUnits().All(u => u.Uid != eddga.Uid), "the body timer or story exit removes the body");
    }

    [ClientDataFact]
    public async Task EddgaFightsBackAtACompanionThatFallsForGood()
    {
        await using var alice = await InWorld("Alice");
        var (eddga, companions) = await EddgaStep(alice);
        await Next(alice, CombatService.SkillHit, SkillHitInfo.Parser,
            h => companions.Any(c => c.Uid == h.SrcID) && h.Dsts.Any(d => d.DstID == eddga.Uid));

        // No player holds Eddga any more (as when she leaves his view): he turns to the companion that hit him (inferred).
        await OnLoop(() => { eddga.Target = null; return Task.CompletedTask; });
        var answer = await Next(alice, CombatService.SkillHit, SkillHitInfo.Parser,
            h => h.SrcID == eddga.Uid && h.Dsts.Any(d => companions.Any(c => c.Uid == d.DstID) && d.Damage > 0));
        var struck = companions.Single(c => answer.Dsts.Any(d => d.DstID == c.Uid));
        Assert.True(await OnLoopValue(() => struck.Hp < struck.Template.MaxHp));

        // At 0 HP it dies and leaves for good: no respawn.
        await OnLoop(() => { struck.Hp = 1; return Task.CompletedTask; });
        await Next(alice, CombatService.SkillHit, SkillHitInfo.Parser,
            h => h.SrcID == eddga.Uid && h.Dsts.Any(d => d.DstID == struck.Uid && d.IsDead));
        await Until(() => !Session(alice).DungeonAttempt!.Companions.Contains(struck)
            && !Service<World>().AllUnits().Contains(struck), "the fallen companion is removed");
        Assert.True(struck.Dead);

        // The step still completes on Eddga's death.
        await Kill(alice, eddga);
        Assert.Equal(1, (await Next(alice, DungeonService.ResultNotify, DungeonResultNotify.Parser)).ResultInfo.WinCamp);
        Assert.DoesNotContain(struck, Session(alice).DungeonAttempt!.Companions);
    }

    [ClientDataFact]
    public async Task ABossAreaSkillHitsTheCompanionsAndTowerPetsInsideIt()
    {
        await using var alice = await InWorld("Alice");
        var (_, companions) = await EddgaStep(alice);
        var options = Service<IOptions<ServerOptions>>().Value.BossSkills;
        options.DamagePercent = 1; // real damage that leaves the companion standing
        var s = Session(alice);
        var warrior = companions.Single(c => c.NpcId == Warrior);
        // Field Orc Hero's Terrifying Roar 5203001, an 8 m circle around itself, cast 3 m from Wule: its 1 m swing
        // never reaches him, and it casts nothing of its own (every skill spent).
        var orc = await SpawnBeside(s, Service<MonsterService>().Template(5020004)! with
        { AttackRange = 1, SpeedCm = 0, Aggressive = false, RoamDistance = 0 },
            warrior.X + 5, warrior.Y, warrior.Z);
        var roar = Assert.Single(orc.Template.Skills!.Skills, k => k.Id == 5203001);
        Assert.Equal((BossSkillArea.AroundSelf, 8f), (roar.Area, roar.Radius));
        // A tower pet in the circle too (regression: it is hit as before).
        var pet = await OnLoopValue(() => TowerPetUnit.Create(Service<GameData>(),
            new DBPetInfo { Inited = true, ListPet = { [13001] = new PetData { PetId = 13001, Level = 12, Star = 1 } } }, s,
            Service<World>(), Service<MonsterService>(), Service<CombatService>(), TowerPetUnit.UidBase + (1L << 40), 13001,
            s.InstanceId, orc.X, orc.Y, orc.Z)!);
        await OnLoop(() => Service<World>().AddUnitAsync(pet));

        var hit = await RoarAt(alice, orc, roar, warrior, pet);
        long warriorDamage = hit.Dsts.Single(d => d.DstID == warrior.Uid).Damage;
        Assert.True(warriorDamage > 0);
        var petHit = await Next(alice, CombatService.SkillHit, SkillHitInfo.Parser,
            h => h.SrcID == orc.Uid && h.SkillId == roar.Id && h.Dsts.Any(d => d.DstID == pet.Uid && d.Damage > 0));
        Assert.Equal(hit.SessionId, petHit.SessionId); // the same cast hits both
        Assert.True(await OnLoopValue(() => warrior.Hp <= warrior.Template.MaxHp - warriorDamage && pet.Hp < pet.MaxHp));
        Assert.DoesNotContain(alice.Inbox.Snapshot().Where(e => e.IsGame(CombatService.SkillHit)).Select(e => SkillHitInfo.Parser.ParseFrom(e.Body)),
            h => h.SrcID == orc.Uid && h.Dsts.Any(d => d.DstID == s.Role!.Id)); // Alice, about 20 m away, is outside it

        // A companion the roar takes to 0 HP falls as under a normal attack: dead, shown dead, gone from the step.
        await OnLoop(() => { warrior.Hp = 1; return Task.CompletedTask; });
        Assert.Contains((await RoarAt(alice, orc, roar, warrior)).Dsts, d => d.DstID == warrior.Uid && d.IsDead);
        await Until(() => warrior.Dead && !s.DungeonAttempt!.Companions.Contains(warrior), "Wule falls");
        Assert.Contains((warrior.NpcId, warrior.Point), s.DungeonAttempt!.FallenCompanions);
    }

    /// <summary>The boss, set 3 m beside the companion (and the pet 3 m beyond it), casts <paramref name="skill"/> as
    /// MonsterService's scheduler would; its hit on the companion.</summary>
    private async Task<SkillHitInfo> RoarAt(BotClient bot, MonsterUnit boss, BossSkill skill, CompanionUnit companion,
        TowerPetUnit? pet = null)
    {
        long serial = await OnLoopValue(() =>
        {
            var map = Service<World>().Map(boss.WorldScene);
            (boss.X, boss.Z) = (companion.X + 3, companion.Z);
            map.Moved(boss);
            if (pet != null)
            {
                (pet.X, pet.Z) = (companion.X + 6, companion.Z);
                map.Moved(pet);
            }
            var rt = boss.BossSkills ??= new BossSkillRuntime(boss.Template.Skills!) { Engaged = true };
            Array.Fill(rt.ReadyMs, long.MaxValue);
            boss.NextSpecialMs = long.MaxValue;
            return ++rt.CastSerial;
        });
        await OnLoop(() => Service<MonsterService>().CastBossSkill!(boss, null, null, skill, serial));
        return await Next(bot, CombatService.SkillHit, SkillHitInfo.Parser,
            h => h.SrcID == boss.Uid && h.SkillId == skill.Id && h.Dsts.Any(d => d.DstID == companion.Uid));
    }

    [ClientDataFact]
    public async Task AnAggressiveMonsterAnsweringACompanionStillPutsAPlayerFirst()
    {
        await using var alice = await InWorld("Alice");
        var (_, companions) = await EddgaStep(alice);
        var s = Session(alice);
        var warrior = companions.Single(c => c.NpcId == Warrior);
        // An aggressive Eddga beside Wule, with a 6 m view: Alice, at the born point about 20 m away, is out of its sight.
        var probe = await SpawnBeside(s, Service<MonsterService>().Template(Eddga)! with
        { Aggressive = true, ViewRange = 6, LeashDistance = 1000, RoamDistance = 0 }, warrior.X, warrior.Y, warrior.Z);
        await OnLoop(() => { Service<MonsterService>().Engage(probe, warrior); return Task.CompletedTask; });
        await Next(alice, CombatService.SkillHit, SkillHitInfo.Parser,
            h => h.SrcID == probe.Uid && h.Dsts.Any(d => d.DstID == warrior.Uid));
        Assert.Null(await OnLoopValue(() => probe.Target));

        // Alice steps into its view: she becomes its target ahead of the companion, and its swing turns to her.
        await OnLoop(() => Service<SceneService>().TeleportInMapAsync(s, new MapPoint(probe.X + 2, probe.Y, probe.Z, 0)));
        await Until(() => ReferenceEquals(probe.Target, s), "the aggressive monster takes Alice");
        long me = s.Role!.Id;
        // 本次實包的故事虎王走31106；來源／目標仍必須是這隻probe及Alice，不接受任意施放替代。
        await Next(alice, CombatService.UseInSkillHigh, UseInSkillInfo.Parser, u => u.SrcID == probe.Uid && u.TargetId == me);
    }

    [ClientDataFact]
    public async Task CompanionCreditGoesToALivingParticipantFirst()
    {
        await using var alice = await InWorld("Alice");
        await using var bob = await InWorld("Bob");
        var (a, b) = (Session(alice), Session(bob));
        Assert.Same(a, await OnLoopValue(() => DungeonService.CompanionCreditAmong([a, b])));
        // The first participant is dead: the living one is credited.
        await OnLoop(() => { a.Dead = true; return Task.CompletedTask; });
        Assert.Same(b, await OnLoopValue(() => DungeonService.CompanionCreditAmong([a, b])));
        // None is alive: still the first, so a companion's kill can complete the run.
        await OnLoop(() => { b.Dead = true; return Task.CompletedTask; });
        Assert.Same(a, await OnLoopValue(() => DungeonService.CompanionCreditAmong([a, b])));
        Assert.Null(await OnLoopValue(() => DungeonService.CompanionCreditAmong([])));
    }

    [ClientDataFact]
    public async Task AMonsterThatCannotMoveDropsACompanionOutOfItsReach()
    {
        await using var alice = await InWorld("Alice");
        var (_, companions) = await EddgaStep(alice);
        var s = Session(alice);
        var warrior = companions.Single(c => c.NpcId == Warrior);
        // An Eddga that cannot move, 12 m from Wule, whose 2 m swing cannot reach him.
        var probe = await SpawnBeside(s, Service<MonsterService>().Template(Eddga)! with
        { SpeedCm = 0, AttackRange = 2, LeashDistance = 1000, RoamDistance = 0 }, warrior.HomeX + 12, warrior.Y, warrior.HomeZ);
        // It lost its player (hurt, on its way home, as Disengage leaves it) and Wule's hit draws it.
        await OnLoop(() =>
        {
            probe.Hp = probe.Template.MaxHp / 2;
            probe.Returning = true;
            Service<MonsterService>().Engage(probe, warrior);
            return Task.CompletedTask;
        });
        // It does not stand answering a companion it cannot reach: its return home completes (HP restored).
        await Until(() => probe.Hp == probe.Template.MaxHp && !probe.Returning, "the immobile monster finishes its return home");
        Assert.DoesNotContain(alice.Inbox.Snapshot().Where(e => e.IsGame(CombatService.UseInSkill) || e.IsGame(CombatService.UseInSkillHigh))
            .Select(e => UseInSkillInfo.Parser.ParseFrom(e.Body)), u => u.SrcID == probe.Uid);
    }

    private Task<MonsterUnit> SpawnBeside(GameSession s, MonsterTemplate template, float x, float y, float z) =>
        Service<GameLoop>().InvokeAsync(async () =>
            (await Service<MonsterService>().SpawnSingleAsync(s.RuntimeMap, template.NpcId, new MapPoint(x, y, z, 0),
                customTemplate: template))!);

    /// <summary>虎王戰鬥階段，Alice回出生點且12名冒險者全部出現；提高此測試boss HP並關閉特殊技，隔離同行AI測試。</summary>
    private async Task<(MonsterUnit Eddga, IReadOnlyList<CompanionUnit> Companions)> EddgaStep(BotClient alice)
    {
        await Gm(alice, "!lvl 99");
        await Gm(alice, "!quest accept 10102200"); // Payon Forest's condition [1101, 10102200]
        Assert.Equal(0, await Enter(alice, new EnterDungeonReq { SceneEntry = PayonForest }));
        long instance = await Load(alice, PayonForest);
        await Stage(alice, 1, quest: 1);
        var standoff = Service<WorldData>().Map(PayonForest)!.Points[22];
        await Gm(alice, FormattableString.Invariant($"!actors walk {standoff.X} {standoff.Z}"));
        await Stage(alice, 2, quest: 2);
        var born = Service<WorldData>().Map(PayonForest)!.Points[BornPoint];
        await OnLoop(() => Service<SceneService>().TeleportInMapAsync(Session(alice), born));
        var eddga = (await WaveIn(instance, 1)).Single();
        Assert.Equal(Eddga, eddga.Template.NpcId);
        await OnLoop(() =>
        {
            // 測試隔離：十名攻擊者不能在觀察同行／死亡路徑前就結束整場；實際產品數值不變。
            eddga.Template = eddga.Template with { MaxHp = eddga.Template.MaxHp * 100 };
            eddga.Hp = eddga.Template.MaxHp;
            eddga.NextSpecialMs = long.MaxValue;
            if (eddga.BossSkills is { } skills) Array.Fill(skills.ReadyMs, long.MaxValue);
            return Task.CompletedTask;
        });
        CompanionUnit[] shown = [];
        await Until(() => (shown = [.. Session(alice).DungeonAttempt?.Companions ?? []]).Length == AdventurerIds.Length, "the twelve observed adventurers are shown");
        return (eddga, shown);
    }

    private async Task<T> OnLoopValue<T>(Func<T> read) => await Service<GameLoop>().InvokeAsync(() => Task.FromResult(read()));

    private async Task Until(Func<bool> condition, string what)
    {
        for (int i = 0; i < 500; i++)
        {
            if (await OnLoopValue(condition)) return;
            await Task.Delay(20);
        }
        throw new TimeoutException(what);
    }
}

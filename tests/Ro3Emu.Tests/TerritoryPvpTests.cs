using Google.Protobuf;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Romsg;
using Ro3Emu.Bot;
using Ro3Emu.Server;
using Ro3Emu.Server.Game;
using Ro3Emu.Server.Maps;

namespace Ro3Emu.Tests;

/// <summary>Inferred server PvP extension: real 31102 intents remain subject to owner authorization.</summary>
public sealed class TerritoryPvpTests : IAsyncLifetime
{
    private readonly string _dataDir = Path.Combine(Path.GetTempPath(), "ro3emu-pvp-" + Guid.NewGuid().ToString("N"));
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(8);
    private WebApplication _app = null!;
    private string _http = "";
    private T Service<T>() where T : notnull => _app.Services.GetRequiredService<T>();
    private Task OnLoop(Func<Task> work) => Service<GameLoop>().InvokeAsync(work);

    public async Task InitializeAsync()
    {
        var (web, micro, game) = TestPorts.FreePorts();
        _app = ServerHost.Create([], o =>
        {
            o.DataDir = _dataDir;
            o.GameDataDir = Path.Combine(RepoRoot(), "data");
            o.HttpPort = web; o.MicroGatePort = micro; o.GameGatePort = game;
        });
        SureHitRolls.Install(_app); // lethal hits are prepared; a FLEE miss would only be noise
        await _app.StartAsync();
        _http = $"http://127.0.0.1:{web}";
    }

    public async Task DisposeAsync()
    {
        await _app.StopAsync(); await _app.DisposeAsync();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dataDir, true); } catch (IOException) { }
    }

    private async Task<BotClient> InWorld(string name)
    {
        var bot = new BotClient(_http, File.ReadAllText(Path.Combine(_dataDir, "gate_rsa_public.pem")));
        await bot.LoginAsync(name.ToLowerInvariant(), "pw"); await bot.EnterGateAsync();
        long id = (await bot.CreateRoleAsync(name)).RoleBaseInfo.Id;
        await bot.EnterWorldAsync(id);
        return bot;
    }

    private static async Task<T> Next<T>(BotClient bot, uint id, MessageParser<T> parser, Func<T, bool>? match = null) where T : IMessage<T>
    {
        var envelope = await bot.Inbox.WaitAsync(e => e.IsGame(id) && (match == null || match(parser.ParseFrom(e.Body))), Wait);
        return parser.ParseFrom(envelope.Body);
    }

    private GameSession Session(BotClient bot) => Service<SessionRegistry>().All()
        .Single(s => s.Account == bot.Account && s.Role != null);

    private static string RepoRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "Ro3Emu.slnx"))) return dir.FullName;
        throw new DirectoryNotFoundException("repository root");
    }

    [ClientDataFact]
    public async Task ExplicitTerritoryPilesDoNotChangeOrdinaryBuffStacking()
    {
        await using var player = await InWorld("PvpBuffPiles");
        var session = Session(player);
        long source = session.Role!.Id;
        var buffs = Service<BuffService>();

        await OnLoop(() => buffs.AddAsync(session, 1162101, 30000, source));
        var ordinary = await Next(player, BuffService.BuffUpdate, BuffUpdateNotify.Parser,
            n => n.Buffs.Any(b => b.ConfigId == 1162101 && b.PileCount == 1));
        long ordinaryGuid = Assert.Single(ordinary.Buffs).BuffGuid;
        await OnLoop(() => buffs.AddAsync(session, 1162101, 30000, source));
        var stacked = await Next(player, BuffService.BuffUpdate, BuffUpdateNotify.Parser,
            n => n.Buffs.Any(b => b.ConfigId == 1162101 && b.PileCount == 2));
        Assert.Equal(ordinaryGuid, Assert.Single(stacked.Buffs).BuffGuid);
        Assert.Equal(2, session.Buffs.Single(b => b.ConfigId == 1162101).PileCount);

        await OnLoop(() => buffs.SetPileCountAsync(session, 4920001, 30000, source, 4));
        var territory = await Next(player, BuffService.BuffUpdate, BuffUpdateNotify.Parser,
            n => n.Buffs.Any(b => b.ConfigId == 4920001 && b.PileCount == 4));
        long territoryGuid = Assert.Single(territory.Buffs).BuffGuid;
        Assert.Equal(-160d, session.Buffs.Single(b => b.ConfigId == 4920001).Changes.Single(c => c.Attr == 1).Value);

        await OnLoop(() => buffs.SetPileCountAsync(session, 4920001, 30000, source, 3));
        var reduced = await Next(player, BuffService.BuffUpdate, BuffUpdateNotify.Parser,
            n => n.Buffs.Any(b => b.ConfigId == 4920001 && b.PileCount == 3));
        Assert.Equal(territoryGuid, Assert.Single(reduced.Buffs).BuffGuid);
        Assert.Equal(-120d, session.Buffs.Single(b => b.ConfigId == 4920001).Changes.Single(c => c.Attr == 1).Value);
        Assert.Equal(2, session.Buffs.Single(b => b.ConfigId == 1162101).PileCount);
    }

    [ClientDataFact]
    public async Task WireCastDeniesByDefaultAndAlliesButHitsAuthorizedEnemy()
    {
        await using var attacker = await InWorld("PvpAttacker");
        await using var enemy = await InWorld("PvpEnemy");
        await using var ally = await InWorld("PvpAlly");
        long enemyId = (long)enemy.Self!.Uuid, allyId = (long)ally.Self!.Uuid;
        Assert.Equal(Session(attacker).WorldScene, Session(enemy).WorldScene);

        await attacker.SendGameAsync(CombatService.UseSkill, new CastSkillReq { SkillId = 1000001, TargetId = enemyId });
        Assert.Equal(3022, (await Next(attacker, CombatService.UseSkill, CastSkillRsp.Parser)).Result);

        var dealt = new List<(long Amount, bool Lethal)>();
        int killed = 0;
        var killNotified = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await OnLoop(async () =>
        {
            var source = Session(attacker);
            var victim = Session(enemy);
            source.Position = victim.Position.Clone();
            var combat = Service<CombatService>();
            combat.PlayerHostile = (a, b) => a == source && b == victim;
            combat.PvpDealt.Add((a, b, amount, lethal) => { if (a == source && b == victim) dealt.Add((amount, lethal)); });
            combat.PlayerKilled.Add((a, b) =>
            {
                if (a == source && b == victim) { killed++; killNotified.TrySetResult(); }
                return Task.CompletedTask;
            });
            await Service<CharacterService>().SetHpSpAsync(victim, 1, null);
            // This verifies effective damage against exactly 1 HP. Exclude the unrelated five-second
            // out-of-combat regeneration tick while the wire requests below are in flight.
            victim.LastCombatMs = MonsterService.NowMs;
            victim.StatusBits |= 1L << 15;
        });

        await attacker.SendGameAsync(CombatService.UseSkill, new CastSkillReq { SkillId = 1000001, TargetId = allyId });
        Assert.Equal(3022, (await Next(attacker, CombatService.UseSkill, CastSkillRsp.Parser)).Result);
        await attacker.SendGameAsync(CombatService.UseSkill, new CastSkillReq { SkillId = 1000001, TargetId = enemyId });
        Assert.Equal(0, (await Next(attacker, CombatService.UseSkill, CastSkillRsp.Parser)).Result);
        var hit = await Next(attacker, CombatService.SkillHit, SkillHitInfo.Parser,
            h => h.Dsts.Any(d => d.DstID == enemyId && d.IsDead));
        Assert.True(Assert.Single(hit.Dsts).IsDead);
        await Next(enemy, CombatService.EntityBaseChange, EntityBase.Parser, e => e.Uuid == (ulong)enemyId && e.State == EntityState.Dead);
        await killNotified.Task.WaitAsync(Wait);
        Assert.True(Session(enemy).Dead);
        Assert.Single(dealt);
        Assert.True(dealt[0].Amount > 0 && dealt[0].Lethal);
        Assert.Equal(1, dealt[0].Amount); // Effective HP loss, not overkill or absorbed damage.
        Assert.Equal(1, killed);

        await attacker.SendGameAsync(CombatService.UseSkill, new CastSkillReq { SkillId = 1000001, TargetId = enemyId });
        Assert.Equal(3022, (await Next(attacker, CombatService.UseSkill, CastSkillRsp.Parser)).Result);

        await enemy.SendGameAsync(CombatService.Relive, new EntityReliveReq());
        Assert.Equal(0, (await Next(enemy, CombatService.Relive, EntityReliveAck.Parser)).Result);
        await Next(enemy, CombatService.EntityBaseChange, EntityBase.Parser,
            e => e.Uuid == (ulong)enemyId && e.State == EntityState.Alive);
        Assert.True(await Service<GameLoop>().InvokeAsync(() => Service<SceneService>().EnterSceneAsync(Session(enemy), 30303)));
        var scene = await Next(enemy, MsgId.SwitchSceneLine, SwitchSceneLineResp.Parser, s => s.SceneEntry == 30303);
        Assert.True(Session(enemy).SceneLoading);
        await attacker.SendGameAsync(CombatService.UseSkill, new CastSkillReq { SkillId = 1000001, TargetId = enemyId });
        Assert.Equal(3022, (await Next(attacker, CombatService.UseSkill, CastSkillRsp.Parser)).Result);
        await enemy.SendGameAsync(MsgId.SceneLoaded, new SceneLoadedReq { SceneId = scene.SceneEntry });
        Assert.Equal(0, (await Next(enemy, MsgId.SceneLoaded, SceneLoadedResp.Parser)).Result);
        await Next(enemy, MsgId.EntitySelf, EntitySelf.Parser);
        await attacker.SendGameAsync(CombatService.UseSkill, new CastSkillReq { SkillId = 1000001, TargetId = enemyId });
        Assert.Equal(3022, (await Next(attacker, CombatService.UseSkill, CastSkillRsp.Parser)).Result);
    }

    [ClientDataTheory]
    [InlineData(10000, false)]
    [InlineData(1, true)]
    public async Task SupportShieldPublishesAbsorptionAndOnlyEffectivePvpDamage(long shield, bool lethal)
    {
        await using var attacker = await InWorld("PvpShieldA");
        await using var victim = await InWorld("PvpShieldV");
        var source = Session(attacker);
        var target = Session(victim);
        var dealt = new List<(long Amount, bool Lethal)>();
        int kills = 0;
        var killed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await OnLoop(async () =>
        {
            source.Position = target.Position.Clone();
            var combat = Service<CombatService>();
            combat.PlayerHostile = (a, b) => a == source && b == target;
            combat.PvpDealt.Add((a, b, amount, dead) =>
            {
                if (a == source && b == target) dealt.Add((amount, dead));
            });
            combat.PlayerKilled.Add((a, b) =>
            {
                if (a == source && b == target) { kills++; killed.TrySetResult(); }
                return Task.CompletedTask;
            });
            Service<CharacterService>().ExtraAttributes.Add(s => s == source ? [(103, 1000d), (109, 10000d)] : []);
            await Service<CharacterService>().SetHpSpAsync(target, 1, null);
            target.LastCombatMs = MonsterService.NowMs;
            target.StatusBits |= 1L << 15;
            await Service<BuffService>().AddSupportAsync(target, 2354401, 30000, 1L << 51, shield);
            Assert.Equal(shield, Service<CharacterService>().Stats(target)[2003]);
        });

        await attacker.SendGameAsync(CombatService.UseSkill,
            new CastSkillReq { SkillId = 1000001, TargetId = target.Role!.Id });
        Assert.Equal(0, (await Next(attacker, CombatService.UseSkill, CastSkillRsp.Parser)).Result);
        var hit = await Next(victim, CombatService.SkillHit, SkillHitInfo.Parser,
            h => h.Dsts.Any(d => d.DstID == target.Role.Id && d.SheildDamage > 0));
        await OnLoop(() => Service<CombatService>().InterruptAsync(source));
        var damage = Assert.Single(hit.Dsts);
        Assert.True(damage.SheildDamage > 0);
        Assert.Equal(lethal, damage.IsDead);
        if (lethal)
        {
            await killed.Task.WaitAsync(Wait);
            Assert.Equal(1, damage.SheildDamage);
            Assert.True(damage.Damage > 1); // The wire retains overkill; scoring receives only the lost 1 HP.
            Assert.Equal((1L, true), Assert.Single(dealt));
            Assert.Equal(1, kills);
            Assert.True(target.Dead);
            Assert.Equal(0, target.CurrentHp);
        }
        else
        {
            Assert.Equal(0, damage.Damage);
            Assert.Empty(dealt);
            Assert.Equal(0, kills);
            Assert.False(target.Dead);
            Assert.Equal(1, target.CurrentHp);
            Assert.Equal(shield - damage.SheildDamage, Service<CharacterService>().Stats(target)[2003]);
        }
    }

    [ClientDataTheory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task BuffPublicationRevokingHostilityInvalidatesPendingDamage(bool revokeDuringShield)
    {
        await using var attacker = await InWorld("PvpRecheckA");
        await using var victim = await InWorld("PvpRecheckV");
        var source = Session(attacker);
        var target = Session(victim);
        bool hostile = true;
        int scoredHits = 0, kills = 0;
        long maximum = 0;
        var revoked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await OnLoop(async () =>
        {
            source.Position = target.Position.Clone();
            var combat = Service<CombatService>();
            combat.PlayerHostile = (a, b) => hostile && a == source && b == target;
            combat.PvpDealt.Add((a, b, _, _) => { if (a == source && b == target) scoredHits++; });
            combat.PlayerKilled.Add((a, b) =>
            {
                if (a == source && b == target) kills++;
                return Task.CompletedTask;
            });
            var character = Service<CharacterService>();
            character.ExtraAttributes.Add(s => s == source ? [(103, 10000d), (109, 10000d)] : []);
            maximum = character.Stats(target)[101];
            await character.SetHpSpAsync(target, maximum, null);
            target.LastCombatMs = MonsterService.NowMs;
            target.StatusBits |= 1L << 15;
            var buffs = Service<BuffService>();
            await buffs.AddAsync(target, 1430501, 30000, target.Role!.Id);
            if (revokeDuringShield) await buffs.AddSupportAsync(target, 2354401, 30000, 1L << 51, 1);
            int removedConfig = revokeDuringShield ? 2354401 : 1430501;
            buffs.Changed.Add(p =>
            {
                if (p == target && !p.Buffs.Any(b => b.ConfigId == removedConfig))
                {
                    hostile = false;
                    revoked.TrySetResult();
                }
                return Task.CompletedTask;
            });
        });

        await attacker.SendGameAsync(CombatService.UseSkill,
            new CastSkillReq { SkillId = 1000001, TargetId = target.Role!.Id });
        var cast = await Next(attacker, CombatService.UseSkill, CastSkillRsp.Parser);
        Assert.Equal(0, cast.Result);
        await revoked.Task.WaitAsync(Wait);
        var until = DateTime.UtcNow + Wait;
        while (source.SkillCast != null && DateTime.UtcNow < until) await Task.Delay(20);
        Assert.Null(source.SkillCast);
        Assert.False(target.Dead);
        Assert.Equal(0, scoredHits);
        Assert.Equal(0, kills);
        Assert.DoesNotContain(victim.Inbox.Snapshot(), e => e.IsGame(CombatService.SkillHit)
            && SkillHitInfo.Parser.ParseFrom(e.Body).SessionId == cast.SessionId);
        if (revokeDuringShield)
        {
            Assert.Equal(maximum, target.CurrentHp);
            Assert.Contains(target.Buffs, b => b.ConfigId == 1430501); // Invalid shield hit cannot spend lethal protection.
        }
        else
        {
            Assert.Equal(Math.Max(1, maximum * 30 / 100), target.CurrentHp);
            Assert.DoesNotContain(target.Buffs, b => b.ConfigId == 1430501);
        }
    }

    [ClientDataFact]
    public async Task AttackerLeavingAfterLethalReservationStillPublishesVictimDeath()
    {
        await using var attacker = await InWorld("PvLeaveA");
        await using var victim = await InWorld("PvLeaveV");
        long victimId = (long)victim.Self!.Uuid;
        int deaths = 0, attributedKills = 0;
        var deathPublished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await OnLoop(async () =>
        {
            var source = Session(attacker);
            var target = Session(victim);
            source.Position = target.Position.Clone();
            var combat = Service<CombatService>();
            combat.PlayerHostile = (a, b) => a == source && b == target;
            combat.PvpDealt.Add((a, b, _, lethal) =>
            {
                if (a == source && b == target && lethal) source.SceneLoading = true; // departure during hit publication
            });
            combat.PlayerDied.Add(p =>
            {
                if (p == target) { deaths++; deathPublished.TrySetResult(); }
                return Task.CompletedTask;
            });
            combat.PlayerKilled.Add((a, b) =>
            {
                if (a == source && b == target) attributedKills++;
                return Task.CompletedTask;
            });
            await Service<CharacterService>().SetHpSpAsync(target, 1, null);
        });

        await attacker.SendGameAsync(CombatService.UseSkill, new CastSkillReq { SkillId = 1000001, TargetId = victimId });
        Assert.Equal(0, (await Next(attacker, CombatService.UseSkill, CastSkillRsp.Parser)).Result);
        await Next(victim, CombatService.EntityBaseChange, EntityBase.Parser,
            e => e.Uuid == (ulong)victimId && e.State == EntityState.Dead);
        await Next(victim, CombatService.ReviveInfo, SyncClientPlayerReviveInfoNtf.Parser, n => n.BDead);
        await deathPublished.Task.WaitAsync(Wait);
        Assert.True(Session(victim).Dead);
        Assert.Equal(0, Session(victim).CurrentHp);
        Assert.Equal(1, deaths);
        Assert.Equal(0, attributedKills);
    }

    [ClientDataFact]
    public async Task HostilityRevokedWhileVictimLockHeldPreventsPendingPlayerDamage()
    {
        await using var attacker = await InWorld("PvpLockAttacker");
        await using var victim = await InWorld("PvpLockVictim");
        // Each game socket dispatches requests in order. A read-only refusal confirms its earlier
        // SceneLoaded and after-loaded hooks finished before the fixture holds the victim gate.
        foreach (var bot in new[] { attacker, victim })
        {
            await bot.SendGameBytesAsync(DungeonService.Statistics, []);
            Assert.Equal(ErrorCodes.SceneMissing,
                (await Next(bot, DungeonService.Statistics, DungeonStatisticsRes.Parser)).Result);
        }
        var source = Session(attacker);
        var target = Session(victim);
        long victimId = target.Role!.Id;
        bool hostile = true;
        int hits = 0, deaths = 0;
        var landed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await OnLoop(async () =>
        {
            source.Position = target.Position.Clone();
            var combat = Service<CombatService>();
            combat.PlayerHostile = (a, b) => hostile && a == source && b == target;
            combat.CastLanded.Add((a, _) =>
            {
                if (a == source) landed.TrySetResult();
                return Task.CompletedTask;
            });
            combat.PvpDealt.Add((a, b, _, _) => { if (a == source && b == target) hits++; });
            combat.PlayerDied.Add(p =>
            {
                if (p == target) deaths++;
                return Task.CompletedTask;
            });
            target.LastCombatMs = MonsterService.NowMs;
            target.StatusBits |= 1L << 15; // This pending-hit assertion excludes unrelated out-of-combat regeneration.
            await Service<CharacterService>().SetHpSpAsync(target, 1, null);
        });

        await target.StateLock.WaitAsync();
        try
        {
            await attacker.SendGameAsync(CombatService.UseSkill, new CastSkillReq { SkillId = 1000001, TargetId = victimId });
            Assert.Equal(0, (await Next(attacker, CombatService.UseSkill, CastSkillRsp.Parser)).Result);
            await landed.Task.WaitAsync(Wait);
            await OnLoop(() => { hostile = false; return Task.CompletedTask; });
        }
        finally { target.StateLock.Release(); }

        var until = DateTime.UtcNow + Wait;
        while (source.SkillCast != null && DateTime.UtcNow < until) await Task.Delay(20);
        Assert.Null(source.SkillCast);
        Assert.Equal(1, target.CurrentHp);
        Assert.False(target.Dead);
        Assert.Equal(0, hits);
        Assert.Equal(0, deaths);
    }

    [ClientDataFact]
    public async Task OverlappingAoeHitsKeepLethalHpAndNotifyDeathOnce()
    {
        await using var first = await InWorld("PvpAoeFirst");
        await using var second = await InWorld("PvpAoeSecond");
        await using var victim = await InWorld("PvpAoeVictim");
        var firstSession = Session(first);
        var secondSession = Session(second);
        var victimSession = Session(victim);
        long victimId = victimSession.Role!.Id;
        var held = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var probeKilled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var killed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var dealt = new List<(GameSession Attacker, long Amount, bool Lethal)>();
        int deaths = 0;
        await OnLoop(async () =>
        {
            firstSession.Position = victimSession.Position.Clone();
            secondSession.Position = victimSession.Position.Clone();
            firstSession.Progress!.Skills[12013] = 1;
            secondSession.Progress!.Skills[12013] = 1;
            await Service<CharacterService>().SetLevelAsync(victimSession, 99, null);
            await Service<CharacterService>().SetHpSpAsync(victimSession, 180, null);
            victimSession.LastCombatMs = MonsterService.NowMs;
            victimSession.StatusBits |= 1L << 15; // hold off the five-second out-of-combat regen during the overlap fixture
            Assert.Equal(180, victimSession.CurrentHp);
            var position = victimSession.Position;
            var probe = await Service<MonsterService>().SpawnSingleAsync(victimSession.SceneEntry, 5000001,
                new MapPoint(position.X, position.Y, position.Z, 0));
            Assert.NotNull(probe);
            probe.Hp = 1;
            var combat = Service<CombatService>();
            combat.PlayerHostile = (a, b) => (a == firstSession || a == secondSession) && b == victimSession;
            combat.PvpDealt.Add((a, b, amount, lethal) =>
            {
                if (b == victimSession) dealt.Add((a, amount, lethal));
            });
            combat.PlayerKilled.Add((a, b) =>
            {
                if (b == victimSession) { deaths++; killed.TrySetResult(); }
                return Task.CompletedTask;
            });
            combat.MonsterDamageApplied.Add((a, m, _) =>
            {
                if (a != firstSession || m != probe) return Task.CompletedTask;
                held.TrySetResult();
                return release.Task;
            });
            combat.Killed.Insert(0, (a, m) =>
            {
                if (a == firstSession && m == probe) probeKilled.TrySetResult();
                return Task.CompletedTask;
            });
        });

        try
        {
            await first.SendGameAsync(CombatService.UseSkill, new CastSkillReq { SkillId = 1201301, TargetPos = victimSession.Position.Clone() });
            Assert.Equal(0, (await Next(first, CombatService.UseSkill, CastSkillRsp.Parser)).Result);
            await held.Task.WaitAsync(Wait); // the first AoE reserved HP, then its real monster callback yielded
            Assert.True(victimSession.CurrentHp is > 0 and < 180,
                $"first reserved HP={victimSession.CurrentHp}, dead={victimSession.Dead}, dealt={dealt.Count}");
            Assert.False(victimSession.Dead);

            await second.SendGameAsync(CombatService.UseSkill, new CastSkillReq { SkillId = 1201301, TargetPos = victimSession.Position.Clone() });
            Assert.Equal(0, (await Next(second, CombatService.UseSkill, CastSkillRsp.Parser)).Result);
            await Next(victim, CombatService.EntityBaseChange, EntityBase.Parser,
                e => e.Uuid == (ulong)victimId && e.State == EntityState.Dead);
            await killed.Task.WaitAsync(Wait);
            Assert.Equal(0, victimSession.CurrentHp);
            Assert.True(victimSession.Dead);
        }
        finally { release.TrySetResult(); }

        await probeKilled.Task.WaitAsync(Wait); // first AoE completed its post-hook HP/death processing
        Assert.Equal(0, victimSession.CurrentHp);
        Assert.True(victimSession.Dead);
        Assert.Equal(1, deaths);
        Assert.Equal(2, dealt.Count);
        Assert.Contains(dealt, d => d.Attacker == firstSession && d.Amount > 0 && !d.Lethal);
        Assert.Contains(dealt, d => d.Attacker == secondSession && d.Amount > 0 && d.Lethal);
    }

    [ClientDataFact]
    public async Task ThrowingMonsterDamageCallbackStillFinalizesKillAndLeavesCombatUsable()
    {
        await using var attacker = await InWorld("PvpHookThrow");
        var source = Session(attacker);
        MonsterUnit first = null!;
        MonsterUnit second = null!;
        int firstKills = 0;
        var firstKilled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondDamaged = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await OnLoop(async () =>
        {
            var at = source.Position;
            first = (await Service<MonsterService>().SpawnSingleAsync(source.SceneEntry, 5000001,
                new MapPoint(at.X, at.Y, at.Z, 0)))!;
            Assert.NotNull(first);
            first.Hp = 1;
            var combat = Service<CombatService>();
            combat.MonsterDamageApplied.Add((_, m, _) =>
                m == first ? Task.FromException(new InvalidOperationException("test callback failure")) : Task.CompletedTask);
            combat.Killed.Insert(0, (_, m) =>
            {
                if (m == first) { firstKills++; firstKilled.TrySetResult(); }
                return Task.CompletedTask;
            });
            combat.Dealt.Add((_, m, amount) =>
            {
                if (m == second && amount > 0) secondDamaged.TrySetResult();
            });
        });

        await attacker.SendGameAsync(CombatService.UseSkill, new CastSkillReq { SkillId = 1000001, TargetId = first.Uid });
        Assert.Equal(0, (await Next(attacker, CombatService.UseSkill, CastSkillRsp.Parser)).Result);
        await firstKilled.Task.WaitAsync(Wait);
        Assert.True(first.Dead);
        Assert.Equal(0, first.Hp);
        Assert.Equal(1, firstKills);

        await OnLoop(async () =>
        {
            var at = source.Position;
            second = (await Service<MonsterService>().SpawnSingleAsync(source.SceneEntry, 5000001,
                new MapPoint(at.X, at.Y, at.Z, 0)))!;
            Assert.NotNull(second);
        });
        await attacker.SendGameAsync(CombatService.UseSkill, new CastSkillReq { SkillId = 1000001, TargetId = second.Uid });
        Assert.Equal(0, (await Next(attacker, CombatService.UseSkill, CastSkillRsp.Parser)).Result);
        await secondDamaged.Task.WaitAsync(Wait);
        Assert.Equal(1, firstKills);
    }

    [ClientDataFact]
    public async Task SceneExitDuringMonsterDamageCallbackFinalizesKillWithoutStaleCasterEffects()
    {
        await using var attacker = await InWorld("PvpSceneExit");
        var source = Session(attacker);
        var held = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        MonsterUnit target = null!, untouched = null!;
        int attributedKills = 0;
        long lastCombat = source.LastCombatMs;
        long untouchedCombat = 0, untouchedStatus = 0;
        await OnLoop(async () =>
        {
            source.Progress!.Skills[11002] = 1; // 1100201 damages an AoE and applies self buff 1100201.
            var at = source.Position;
            target = (await Service<MonsterService>().SpawnSingleAsync(source.SceneEntry, 5000001,
                new MapPoint(at.X, at.Y, at.Z, 0)))!;
            Assert.NotNull(target);
            target.Hp = 1;
            target.Moving = false;
            target.NextRoamMs = long.MaxValue;
            untouched = (await Service<MonsterService>().SpawnSingleAsync(source.SceneEntry, 5000001,
                new MapPoint(at.X + 1, at.Y, at.Z, 0)))!;
            Assert.NotNull(untouched);
            untouched.Hp = 1;
            // This real AoE orders by distance. Keep the held callback's target first and
            // the second monster inside its 5.5m radius without random idle roams reordering them.
            untouched.Moving = false;
            untouched.NextRoamMs = long.MaxValue;
            Assert.True(MonsterService.Distance(at.X, at.Z, target.X, target.Z)
                < MonsterService.Distance(at.X, at.Z, untouched.X, untouched.Z));
            Assert.InRange(MonsterService.Distance(at.X, at.Z, untouched.X, untouched.Z), 0.5f, 2f);
            untouchedCombat = untouched.LastCombatMs;
            untouchedStatus = untouched.StatusBits;
            var combat = Service<CombatService>();
            combat.MonsterDamageApplied.Add((a, m, _) =>
            {
                if (a != source || m != target) return Task.CompletedTask;
                held.TrySetResult();
                return release.Task;
            });
            combat.Killed.Add((a, m) =>
            {
                if (a == source && m == target) attributedKills++;
                return Task.CompletedTask;
            });
        });

        try
        {
            await attacker.SendGameAsync(CombatService.UseSkill, new CastSkillReq { SkillId = 1100201 });
            Assert.Equal(0, (await Next(attacker, CombatService.UseSkill, CastSkillRsp.Parser)).Result);
            await held.Task.WaitAsync(Wait);
            Assert.Equal(0, target.Hp); // committed before the awaited callback
            Assert.True(await Service<GameLoop>().InvokeAsync(() => Service<SceneService>().EnterSceneAsync(source, 30303)));
            Assert.True(source.SceneLoading);
        }
        finally { release.TrySetResult(); }

        var until = DateTime.UtcNow + Wait;
        while (!target.Dead && DateTime.UtcNow < until) await Task.Delay(20);
        Assert.True(target.Dead);
        Assert.Equal(0, target.Hp);
        Assert.False(untouched.Dead);
        Assert.Equal(1, untouched.Hp);
        Assert.Equal(untouchedCombat, untouched.LastCombatMs);
        Assert.Equal(untouchedStatus, untouched.StatusBits);
        Assert.DoesNotContain(source.Buffs, b => b.ConfigId == 1100201);
        Assert.Equal(lastCombat, source.LastCombatMs);
        Assert.Equal(0, attributedKills);
    }
}

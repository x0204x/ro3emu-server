using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Romsg;
using Ro3Emu.Bot;
using Ro3Emu.Server;
using Ro3Emu.Server.Data;
using Ro3Emu.Server.Data.Config;
using Ro3Emu.Server.Game;
using Ro3Emu.Server.Game.Ownership;
using Ro3Emu.Server.Maps;

namespace Ro3Emu.Tests;

/// <summary>Real game-wire casts with deterministic owned-world fixtures and negative lifecycle checks.</summary>
public sealed partial class SkillCombatTests : IAsyncLifetime
{
    private readonly string _dataDir = Path.Combine(Path.GetTempPath(), "ro3emu-skillcombat-" + Guid.NewGuid().ToString("N"));
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(8);
    private WebApplication _app = null!;
    private string _http = "";
    private GameLoop Loop => _app.Services.GetRequiredService<GameLoop>();
    private CharacterService Character => _app.Services.GetRequiredService<CharacterService>();
    private GameSession Session(string name) => _app.Services.GetRequiredService<SessionRegistry>().All().Single(s => s.Account == name.ToLowerInvariant());

    public async Task InitializeAsync()
    {
        var ports = TestPorts.FreePorts();
        _app = ServerHost.Create([], o =>
        {
            o.DataDir = _dataDir; o.GameDataDir = Path.Combine(RepoRoot(), "data");
            o.HttpPort = ports.Item1; o.MicroGatePort = ports.Item2; o.GameGatePort = ports.Item3;
        });
        SureHitRolls.Install(_app); // lethal hits are prepared; a FLEE miss would only be noise
        await _app.StartAsync(); _http = $"http://127.0.0.1:{ports.Item1}";
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
        var role = await bot.CreateRoleAsync(name); await bot.EnterWorldAsync(role.RoleBaseInfo.Id);
        return bot;
    }
    private static async Task<T> Next<T>(BotClient bot, uint id, Google.Protobuf.MessageParser<T> parser, Func<T, bool>? match = null)
        where T : Google.Protobuf.IMessage<T>
    {
        var e = await bot.Inbox.WaitAsync(e => e.IsGame(id) && (match == null || match(parser.ParseFrom(e.Body))), Wait);
        return parser.ParseFrom(e.Body);
    }
    private async Task Gm(string account, string line)
    {
        using var client = new HttpClient();
        (await client.PostAsync($"{_http}/ro3emu/gm?account={account}", new StringContent(line))).EnsureSuccessStatusCode();
    }
    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "Ro3Emu.slnx"))) dir = dir.Parent;
        return dir?.FullName ?? throw new DirectoryNotFoundException();
    }
    private Task Teach(string name, int group, int level = 1) => Loop.InvokeAsync(() => { Session(name).Progress!.Skills[group] = level; return Task.CompletedTask; });
    private Task<MonsterUnit> Target(string name, bool stationary = false, int npcId = 5000001, bool immobile = false) => Loop.InvokeAsync(async () =>
    {
        var s = Session(name);
        var monsters = _app.Services.GetRequiredService<MonsterService>();
        MonsterTemplate? template = null;
        if (stationary)
        {
            template = monsters.Template(npcId);
            Assert.NotNull(template);
            // A stationary dummy neither roams nor walks out to a surround slot (MonsterService.Surround).
            template = template with
            {
                RoamDistance = 0,
                SurroundDistance = 0,
                SpeedCm = immobile ? 0 : template.SpeedCm
            };
        }
        var result = await monsters.SpawnSingleAsync(s.SceneEntry, npcId,
            new MapPoint(s.Position.X, s.Position.Y, s.Position.Z, 0), s.InstanceId, customTemplate: template);
        Assert.NotNull(result);
        return result;
    });

    [ClientDataFact]
    public async Task CancelledAttachmentDuringFirstAreaHitCannotDamageNextTarget()
    {
        await using var alice = await InWorld("Alice");
        await Gm("alice", "!job 1100"); await Teach("alice", 11002);
        // Stable positions preserve first-then-second AoE ordering while the cast waits.
        var first = await Target("alice", stationary: true);
        var second = await Target("alice", stationary: true);
        var player = Session("alice");
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseCleanup = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int dealt = 0;
        Task? detach = null;
        await Loop.InvokeAsync(() =>
        {
            first.Hp = second.Hp = 100000;
            _app.Services.GetRequiredService<RoleHandover>().BeforeCleanupForTest = session =>
                session == player ? releaseCleanup.Task : Task.CompletedTask;
            _app.Services.GetRequiredService<CombatService>().DealtAsync.Add(async (source, target, _, _) =>
            {
                if (source != player || target != first && target != second) return;
                dealt++;
                if (target == first) { entered.TrySetResult(); await release.Task; }
            });
            return Task.CompletedTask;
        });
        long secondHp = second.Hp;
        long generation = player.SceneGeneration;
        await alice.SendGameAsync(CombatService.UseSkill, new CastSkillReq { SkillId = 1100201 });
        var accepted = await Next(alice, CombatService.UseSkill, CastSkillRsp.Parser);
        Assert.Equal(0, accepted.Result);
        try
        {
            await entered.Task.WaitAsync(Wait);
            await Loop.InvokeAsync(() =>
            {
                detach = _app.Services.GetRequiredService<RoleHandover>().Begin(player);
                Assert.True(player.Attachment!.Cancelled);
                Assert.Equal(generation, player.SceneGeneration); // cleanup has not changed the scene yet
                return Task.CompletedTask;
            });
            release.TrySetResult();
            await Next(alice, CombatService.SkillHit, SkillHitInfo.Parser, x => x.SessionId == accepted.SessionId);
            Assert.Equal(1, dealt);
            Assert.Equal(secondHp, second.Hp);
        }
        finally { release.TrySetResult(); releaseCleanup.TrySetResult(); }
        await detach!.WaitAsync(Wait);
        Assert.True(first.Hp < 100000);
    }

    [ClientDataFact]
    public async Task CancelledLeaseAfterLethalAreaHitKeepsCommittedKillConsequences()
    {
        await using var alice = await InWorld("Alice");
        await Gm("alice", "!job 1100"); await Teach("alice", 11002);
        // AoE sorts by distance then UID; roaming must not move the second target ahead.
        var first = await Target("alice", stationary: true);
        var second = await Target("alice", stationary: true);
        var player = Session("alice");
        var lease = player.Attachment!;
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var killed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int dealt = 0, applied = 0, killCount = 0;
        await Loop.InvokeAsync(() =>
        {
            first.Hp = 1;
            second.Hp = 100000;
            var combat = _app.Services.GetRequiredService<CombatService>();
            combat.MonsterDamageApplied.Add((source, target, _) =>
            {
                if (source == player && target == first) applied++;
                return Task.CompletedTask;
            });
            combat.Killed.Add((source, target) =>
            {
                if (source == player && target == first) { killCount++; killed.TrySetResult(); }
                return Task.CompletedTask;
            });
            combat.DealtAsync.Add(async (source, target, _, _) =>
            {
                if (source != player) return;
                dealt++;
                if (target == first) { entered.TrySetResult(); await release.Task; }
            });
            return Task.CompletedTask;
        });
        long secondHp = second.Hp;
        long generation = player.SceneGeneration;
        await alice.SendGameAsync(CombatService.UseSkill, new CastSkillReq { SkillId = 1100201 });
        var accepted = await Next(alice, CombatService.UseSkill, CastSkillRsp.Parser);
        Assert.Equal(0, accepted.Result);
        try
        {
            await entered.Task.WaitAsync(Wait);
            // Isolate the attachment fence while the owner still admits its already-committed kill callbacks.
            await Loop.InvokeAsync(() =>
            {
                Assert.Equal(1, dealt);
                Assert.Equal(secondHp, second.Hp);
                lease.Cancelled = true;
                Assert.Same(lease, player.Attachment);
                Assert.Equal(generation, player.SceneGeneration);
                return Task.CompletedTask;
            });
            release.TrySetResult();
            await Next(alice, CombatService.SkillHit, SkillHitInfo.Parser, x => x.SessionId == accepted.SessionId);
            await killed.Task.WaitAsync(Wait);
            Assert.Equal(1, dealt);
            Assert.Equal(1, applied);
            Assert.Equal(1, killCount);
            Assert.Equal(secondHp, second.Hp);
            Assert.True(first.Dead);
        }
        finally
        {
            release.TrySetResult();
            await Loop.InvokeAsync(() => { lease.Cancelled = false; return Task.CompletedTask; });
        }
    }

    [ClientDataFact]
    public async Task AcceptedCastHitsAndPassiveUnownedAndWrongActorAreRefused()
    {
        await using var alice = await InWorld("Alice");
        await Gm("alice", "!job 1100");
        var learn = new SkillGrowthSkillPlanUpdateReq();
        learn.LevelUpList[11001] = new SkillData { SkillId = 11001, SkillLevel = 1 };
        await alice.SendGameAsync(SkillService.PlanUpdate, learn);
        Assert.Equal(0, (await Next(alice, SkillService.PlanUpdate, SkillGrowthSkillPlanUpdateResp.Parser)).Result);
        var target = await Target("alice");
        long before = target.Hp;
        await alice.SendGameAsync(CombatService.UseSkill, new CastSkillReq { SkillId = 1100101, TargetId = target.Uid });
        var reply = await Next(alice, CombatService.UseSkill, CastSkillRsp.Parser);
        Assert.Equal(0, reply.Result);
        var use = await Next(alice, CombatService.UseInSkill, UseInSkillInfo.Parser, p => p.SessionId == reply.SessionId);
        Assert.Equal((long)alice.Self!.Uuid, use.SrcID);
        Assert.Equal(1100101, use.SkillID);
        Assert.Equal(target.Uid, use.TargetId);
        Assert.Equal(target.Pos.X, use.TargetPos.X);
        Assert.Equal(target.Pos.Y, use.TargetPos.Y);
        Assert.Equal(target.Pos.Z, use.TargetPos.Z);
        Assert.True(use.PlayAct);
        Assert.True(use.SkillMs > 0);
        Assert.True(use.StartTime > 0);
        var hit = await Next(alice, CombatService.SkillHit, SkillHitInfo.Parser, p => p.SessionId == reply.SessionId);
        Assert.Equal(1100101, hit.ShowPoint); // master's skill-specific EffectData entry for Bash
        Assert.True(Assert.Single(hit.Dsts).Damage > 0);
        Assert.True(target.Hp < before);
        var statistics = await Next(alice, CombatService.DamageStatistics, DamageStatisticsNotify.Parser,
            n => n.Damages.Any(d => d.DamageTargetId == target.Uid));
        Assert.Equal(hit.Dsts[0].Damage, Assert.Single(statistics.Damages).DamageValue);

        await alice.SendGameAsync(CombatService.UseSkill, new CastSkillReq { SkillId = 1100101, OpUnitId = long.MaxValue, TargetId = target.Uid });
        Assert.Equal(ErrorCodes.SkillRequirements, (await Next(alice, CombatService.UseSkill, CastSkillRsp.Parser)).Result);
        await alice.SendGameAsync(CombatService.UseSkill, new CastSkillReq { SkillId = 1200401, TargetId = target.Uid });
        Assert.Equal(ErrorCodes.SkillNotLearned, (await Next(alice, CombatService.UseSkill, CastSkillRsp.Parser)).Result);
        await Teach("alice", 11009);
        await alice.SendGameAsync(CombatService.UseSkill, new CastSkillReq { SkillId = 1100901, TargetId = target.Uid });
        Assert.Equal(ErrorCodes.SkillPassive, (await Next(alice, CombatService.UseSkill, CastSkillRsp.Parser)).Result);
    }

    [ClientDataTheory]
    [InlineData(12004, 5, 1200405, 1200411, 1200404, 400, 5)]
    [InlineData(12012, 5, 1201205, 1201210, 1201209, 167, 1)]
    public async Task FireProjectilesUseAuthoredBulletAndActionHitEffects(
        int group, int level, int skillId, int bulletEffect, int hitEffect, int travelMs, int split)
    {
        await using var alice = await InWorld("Alice");
        await Gm("alice", "!job 2100"); await Teach("alice", group, level);
        var target = await Target("alice", stationary: true);
        await Loop.InvokeAsync(() =>
        {
            var session = Session("alice");
            var start = session.Position;
            var nav = _app.Services.GetRequiredService<WorldData>().Map(session.SceneEntry)!.Nav;
            var offset = new[] { (4f, 0f), (-4f, 0f), (0f, 4f), (0f, -4f) }
                .First(o => nav == null || nav.CanWalk(start.X, start.Y, start.Z, start.X + o.Item1, start.Z + o.Item2));
            target.X = start.X + offset.Item1;
            target.Z = start.Z + offset.Item2;
            target.Y = nav?.WalkHeight(target.X, target.Z, start.Y) ?? start.Y;
            target.Hp = 1_000_000;
            return Task.CompletedTask;
        });

        await alice.SendGameAsync(CombatService.UseSkill, new CastSkillReq { SkillId = skillId, TargetId = target.Uid });
        var accepted = await Next(alice, CombatService.UseSkill, CastSkillRsp.Parser);
        Assert.Equal(0, accepted.Result);
        var timeline = await Next(alice, CombatService.UseInSkill, UseInSkillInfo.Parser,
            x => x.SessionId == accepted.SessionId);
        var bullet = await Next(alice, CombatService.SkillBullet, SkillFixTimeBulletNotify.Parser,
            x => x.SkillConfigId == skillId && x.TargetId == target.Uid);
        Assert.Equal((long)alice.Self!.Uuid, bullet.SourceId);
        Assert.Equal((long)alice.Self.Uuid, bullet.MasterId);
        Assert.Equal(bulletEffect, bullet.EffectId);
        Assert.Equal(travelMs, bullet.FixedTime);
        Assert.Equal(split, bullet.Split);
        Assert.Equal(target.Pos, bullet.TargetPosition);
        Assert.Equal(Session("alice").Position, bullet.SourcePosition);
        var data = _app.Services.GetRequiredService<GameData>();
        var row = SkillConfig.Get(data, skillId)!.Value;
        var template = data.SkillTemplate(row.ScriptShow)!.Value;
        double attackPoint = template.GetProperty("m_kAtkPoints").EnumerateArray().First().GetDouble();
        int scheduledOnset = CombatService.ImpactOffset(template, attackPoint, timeline.ChantMs, timeline.SkillMs);
        Assert.Equal(timeline.StartTime + scheduledOnset, bullet.StartTimestampMs);

        var hit = await Next(alice, CombatService.SkillHit, SkillHitInfo.Parser,
            x => x.SessionId == accepted.SessionId);
        Assert.Equal(hitEffect, hit.ShowPoint);
        Assert.Equal(split, hit.Split);
        var targetDamage = Assert.Single(hit.Dsts, d => d.DstID == target.Uid);
        Assert.True(targetDamage.Damage > 0);
        Assert.Equal(targetDamage.Damage, 1_000_000 - target.Hp); // split is presentation only; total damage is committed once
    }

    [ClientDataFact]
    public async Task InterruptInvalidatesDelayedHitAndEchoesAutoCast()
    {
        await using var alice = await InWorld("Alice");
        await Gm("alice", "!job 2100"); await Teach("alice", 12004);
        var target = await Target("alice");
        long hp = target.Hp;
        await alice.SendGameAsync(CombatService.UseSkill, new CastSkillReq { SkillId = 1200401, TargetId = target.Uid, IsAutoCast = true });
        var accepted = await Next(alice, CombatService.UseSkill, CastSkillRsp.Parser);
        Assert.Equal(0, accepted.Result); Assert.True(accepted.IsAutoCast);
        var timeline = await Next(alice, CombatService.UseInSkill, UseInSkillInfo.Parser, x => x.SessionId == accepted.SessionId);
        Assert.Equal(1200401, timeline.SkillID);
        Assert.Equal(target.Uid, timeline.TargetId);
        Assert.Equal(target.Pos.X, timeline.TargetPos.X);
        Assert.Equal(target.Pos.Y, timeline.TargetPos.Y);
        Assert.Equal(target.Pos.Z, timeline.TargetPos.Z);
        Assert.True(timeline.PlayAct);
        Assert.True(timeline.ChantMs > 0);
        Assert.True(timeline.SkillMs > 0);
        Assert.True(timeline.StartTime > 0);
        await alice.SendGameAsync(CombatService.SkillInterrupt, new SkillInterruptReq { InterruptSkillId = 1200401 });
        Assert.True((await Next(alice, CombatService.SkillInterrupt, SkillInterruptResp.Parser)).IsInterruptSucc);
        var end = await Next(alice, CombatService.SkillEnd, SkillEndNotify.Parser, x => x.SessionId == accepted.SessionId);
        Assert.True(end.ClearEffect);
        await Task.Delay(1600);
        await Loop.InvokeAsync(() => { Assert.Equal(hp, target.Hp); Assert.Null(Session("alice").SkillCast); return Task.CompletedTask; });
    }

    [ClientDataFact]
    public async Task GroundNpcUsesRequestedPositionAndDespawnsOnTeardown()
    {
        await using var alice = await InWorld("Alice"); await using var bob = await InWorld("Bob");
        await Gm("alice", "!job 2100"); await Teach("alice", 12010);
        var point = Session("alice").Position.Clone();
        await alice.SendGameAsync(CombatService.UseSkill, new CastSkillReq { SkillId = 1201001, TargetPos = point });
        Assert.Equal(0, (await Next(alice, CombatService.UseSkill, CastSkillRsp.Parser)).Result);
        var spawn = await Next(bob, GameAoiSink.SyncAoiView, AoIViewChangeData.Parser, x => x.Bases.Any(b => b.EntityTemplateID == 1200004));
        var field = Assert.Single(spawn.Bases, b => b.EntityTemplateID == 1200004);
        Assert.Equal((long)alice.Self!.Uuid, field.Owner); Assert.Equal(point, field.MovementInfo.Pos);
        await Loop.InvokeAsync(() => _app.Services.GetRequiredService<CombatService>().ClearSkillsAsync(Session("alice")));
        await Next(bob, GameAoiSink.SyncAoiView, AoIViewChangeData.Parser, x => x.LeaveAoi.Contains(field.Uuid));
        Assert.Empty(Session("alice").SkillObjects);
    }

    [ClientDataFact]
    public async Task HealSelectsAllyAndInsufficientSpDoesNotStartCooldown()
    {
        await using var alice = await InWorld("Alice"); await using var bob = await InWorld("Bob");
        await Gm("alice", "!job 4100"); await Teach("alice", 14007); await Teach("alice", 14001);
        await Loop.InvokeAsync(() => Character.SetHpSpAsync(Session("bob"), 1, null));
        await alice.SendGameAsync(CombatService.UseSkill, new CastSkillReq { SkillId = 1400701, TargetId = (long)bob.Self!.Uuid });
        var accepted = await Next(alice, CombatService.UseSkill, CastSkillRsp.Parser);
        Assert.Equal(0, accepted.Result);
        var hit = await Next(alice, CombatService.SkillHit, SkillHitInfo.Parser, x => x.SessionId == accepted.SessionId);
        Assert.Equal((long)bob.Self.Uuid, Assert.Single(hit.Dsts).DstID); Assert.Equal(DamageType.Heal, hit.Dsts[0].DamageType);
        await Task.Delay(600);
        await Loop.InvokeAsync(() => Character.SetHpSpAsync(Session("alice"), null, 0));
        await alice.SendGameAsync(CombatService.UseSkill, new CastSkillReq { SkillId = 1400101 });
        Assert.Equal(ErrorCodes.SkillCostShort, (await Next(alice, CombatService.UseSkill, CastSkillRsp.Parser)).Result);
        Assert.False(Session("alice").SkillCds.ContainsKey(14001));
    }

    [ClientDataFact]
    public async Task LethalHitBlocksReviveUntilDeathPublicationCompletes()
    {
        await using var alice = await InWorld("Alice");
        var player = Session("alice");
        var monster = await Target("alice");
        var combat = _app.Services.GetRequiredService<CombatService>();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var settled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await Loop.InvokeAsync(async () =>
        {
            await Character.SetHpSpAsync(player, 1, null);
            combat.TakenAsync.Add(async (_, target, _) =>
            {
                if (target != player || !target.Dead) return;
                entered.TrySetResult(); await release.Task;
            });
            combat.PlayerDied.Add(target =>
            {
                if (target == player) settled.TrySetResult();
                return Task.CompletedTask;
            });
            await _app.Services.GetRequiredService<MonsterService>().Attack!(monster, player);
        });
        try
        {
            await entered.Task.WaitAsync(Wait);
            await alice.SendGameAsync(CombatService.Relive, new EntityReliveReq());
            Assert.Equal(ErrorCodes.FeatureUnavailable, (await Next(alice, CombatService.Relive, EntityReliveAck.Parser)).Result);
            Assert.True(player.Dead);
            Assert.Equal(0, Character.Stats(player)[2000]);
        }
        finally { release.TrySetResult(); }
        await settled.Task.WaitAsync(Wait);
        await Loop.InvokeAsync(() => Task.CompletedTask);
        await alice.SendGameAsync(CombatService.Relive, new EntityReliveReq());
        Assert.Equal(0, (await Next(alice, CombatService.Relive, EntityReliveAck.Parser)).Result);
        await Next(alice, CombatService.ReviveInfo, SyncClientPlayerReviveInfoNtf.Parser, m => !m.BDead);
        Assert.False(player.Dead);
        Assert.True(Character.Stats(player)[2000] > 0);
    }

    [ClientDataFact]
    public async Task PendingLethalHitCannotDeliverDuplicateKillCallbacks()
    {
        await using var alice = await InWorld("Alice");
        await using var bob = await InWorld("Bob");
        await Gm("alice", "!job 1100");
        await Gm("bob", "!job 1100");
        await Teach("alice", 11001);
        await Teach("bob", 11001);
        var target = await Target("alice");
        var combat = _app.Services.GetRequiredService<CombatService>();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var killed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int dealtCount = 0, killCount = 0;
        await Loop.InvokeAsync(() =>
        {
            target.Hp = 1;
            combat.DealtAsync.Add(async (source, monster, _, _) =>
            {
                if (monster != target) return;
                dealtCount++;
                if (source == Session("alice")) { entered.TrySetResult(); await release.Task; }
            });
            combat.Killed.Add((_, monster) =>
            {
                if (monster == target) { killCount++; killed.TrySetResult(); }
                return Task.CompletedTask;
            });
            return Task.CompletedTask;
        });
        await alice.SendGameAsync(CombatService.UseSkill, new CastSkillReq { SkillId = 1100101, TargetId = target.Uid });
        Assert.Equal(0, (await Next(alice, CombatService.UseSkill, CastSkillRsp.Parser)).Result);
        try
        {
            await entered.Task.WaitAsync(Wait);
            await bob.SendGameAsync(CombatService.UseSkill, new CastSkillReq { SkillId = 1100101, TargetId = target.Uid });
            Assert.Equal(0, (await Next(bob, CombatService.UseSkill, CastSkillRsp.Parser)).Result);
            var afterSecondImpact = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            await Loop.InvokeAsync(() =>
            {
                var cast = Session("bob").SkillCast!;
                Loop.Schedule(TimeSpan.FromMilliseconds(cast.ChantMs + cast.ActionMs + 100),
                    () => { afterSecondImpact.TrySetResult(); return Task.CompletedTask; });
                return Task.CompletedTask;
            });
            await afterSecondImpact.Task.WaitAsync(Wait);
            Assert.Equal(1, dealtCount);
            Assert.Equal(0, killCount);
        }
        finally { release.TrySetResult(); }
        await killed.Task.WaitAsync(Wait);
        Assert.Equal(1, killCount);
        Assert.True(target.Dead);
    }

    [ClientDataFact]
    public async Task EventCastRechecksRangeAndUsesTheSharedLethalProtection()
    {
        await using var alice = await InWorld("Alice");
        await using var bob = await InWorld("Bob");
        await Gm("alice", "!job 2100");
        await Teach("alice", 12004);
        var a = Session("alice");
        var b = Session("bob");
        var combat = _app.Services.GetRequiredService<CombatService>();
        var instances = _app.Services.GetRequiredService<InstanceService>();
        var room = await Loop.InvokeAsync(() => Task.FromResult(instances.Create(a.SceneEntry, [a, b])));
        foreach (var (bot, session) in new[] { (alice, a), (bob, b) })
        {
            Assert.True(await Loop.InvokeAsync(() => instances.EnterAsync(room, session, reason: 3)));
            await Next(bot, MsgId.SwitchSceneLine, SwitchSceneLineResp.Parser, m => m.SceneID == room.Id);
            await bot.SendGameAsync(MsgId.SceneLoaded, new SceneLoadedReq { SceneId = room.SceneEntry });
            await Next(bot, MsgId.EntitySelf, EntitySelf.Parser);
        }
        await Loop.InvokeAsync(() =>
        {
            combat.SetEventPvPHandlers((source, target) => source == a && target == b, (_, _) => Task.CompletedTask);
            return Task.CompletedTask;
        });
        long hp = Character.Stats(b)[2000];
        await alice.SendGameAsync(CombatService.UseSkill, new CastSkillReq { SkillId = 1200401, TargetId = b.Role!.Id });
        var accepted = await Next(alice, CombatService.UseSkill, CastSkillRsp.Parser);
        Assert.Equal(0, accepted.Result);
        await Next(alice, CombatService.UseInSkill, UseInSkillInfo.Parser, m => m.SessionId == accepted.SessionId);
        var afterImpact = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await Loop.InvokeAsync(async () =>
        {
            var cast = a.SkillCast!;
            await _app.Services.GetRequiredService<SceneService>().TeleportInMapAsync(b,
                new MapPoint(b.Position.X + 40, b.Position.Y, b.Position.Z, b.Face));
            Loop.Schedule(TimeSpan.FromMilliseconds(cast.ChantMs + cast.ActionMs + 100),
                () => { afterImpact.TrySetResult(); return Task.CompletedTask; });
        });
        await afterImpact.Task.WaitAsync(Wait);
        Assert.Equal(hp, Character.Stats(b)[2000]);
        Assert.False(b.Dead);
        Assert.DoesNotContain(alice.Inbox.Snapshot(), e => e.IsGame(CombatService.SkillHit)
            && SkillHitInfo.Parser.ParseFrom(e.Body) is var hit && hit.SessionId == accepted.SessionId
            && hit.Dsts.Any(d => d.DstID == b.Role.Id));

        await Loop.InvokeAsync(async () =>
        {
            await _app.Services.GetRequiredService<SceneService>().TeleportInMapAsync(b,
                new MapPoint(a.Position.X, a.Position.Y, a.Position.Z, b.Face));
            await _app.Services.GetRequiredService<BuffService>().AddAsync(b, 2052101, 60000, b.Role.Id);
            await Character.SetHpSpAsync(b, 1, null);
            await Character.SetHpSpAsync(a, null, long.MaxValue);
            a.SkillCds.Clear(); a.SkillModeCds.Clear();
        });
        await alice.SendGameAsync(CombatService.UseSkill, new CastSkillReq { SkillId = 1200401, TargetId = b.Role.Id });
        accepted = await Next(alice, CombatService.UseSkill, CastSkillRsp.Parser);
        Assert.Equal(0, accepted.Result);
        Assert.Equal(2052101, (await Next(bob, BuffService.ImmuneDeathNotify, PlayerOnEnterImmuneDeadNotify.Parser)).BuffConfigId);
        var protectedHit = await Next(alice, CombatService.SkillHit, SkillHitInfo.Parser, h => h.SessionId == accepted.SessionId);
        Assert.False(Assert.Single(protectedHit.Dsts).IsDead);
        Assert.False(b.Dead);
        Assert.NotNull(b.ImmuneDeathOffer);
        Assert.Equal(1, Character.Stats(b)[2000]);
    }

    [ClientDataFact]
    public async Task UnifiedCastCallsMasterDamageAndHealingHooksOnce()
    {
        await using var alice = await InWorld("Alice");
        await Gm("alice", "!job 1100");
        await Teach("alice", 11001);
        var target = await Target("alice");
        var combat = _app.Services.GetRequiredService<CombatService>();
        int landed = 0, adjusted = 0, extra = 0, dealt = 0, healed = 0;
        long delivered = 0;
        await Loop.InvokeAsync(() =>
        {
            combat.CastLanded.Add((_, _) => { landed++; return Task.CompletedTask; });
            combat.DamageAdjusters.Add((_, _, _, _, _) => { adjusted++; return 17; });
            combat.ExtraHitDamage.Add((_, _, _, _) => { extra++; return 3; });
            combat.DealtAsync.Add((_, _, _, amount) => { dealt++; delivered = amount; return Task.CompletedTask; });
            combat.ExtraHeal.Add((_, _) => { healed++; return 23; });
            return Task.CompletedTask;
        });
        await alice.SendGameAsync(CombatService.UseSkill, new CastSkillReq { SkillId = 1100101, TargetId = target.Uid });
        var accepted = await Next(alice, CombatService.UseSkill, CastSkillRsp.Parser);
        Assert.Equal(0, accepted.Result);
        var hit = await Next(alice, CombatService.SkillHit, SkillHitInfo.Parser, h => h.SessionId == accepted.SessionId);
        Assert.Equal(20, Assert.Single(hit.Dsts).Damage);
        Assert.Equal(20, delivered);
        Assert.Equal((1, 1, 1, 1), (landed, adjusted, extra, dealt));

        await Gm("alice", "!lvl 6"); // First Aid's configured activation level.
        await Loop.InvokeAsync(async () =>
        {
            await combat.ClearSkillsAsync(Session("alice"));
            await Character.SetHpSpAsync(Session("alice"), 1, null);
        });
        await alice.SendGameAsync(CombatService.UseSkill, new CastSkillReq { SkillId = 1000301 });
        accepted = await Next(alice, CombatService.UseSkill, CastSkillRsp.Parser);
        Assert.Equal(0, accepted.Result);
        hit = await Next(alice, CombatService.SkillHit, SkillHitInfo.Parser, h => h.SessionId == accepted.SessionId);
        Assert.Equal(DamageType.Heal, Assert.Single(hit.Dsts).DamageType);
        Assert.Equal(23, hit.Dsts[0].Damage);
        Assert.Equal(1, healed);
        Assert.Equal(2, landed);
    }

    [ClientDataFact]
    public async Task WorldTreeConsumesOneLeafAndRevivesDeadAlly()
    {
        await using var alice = await InWorld("Alice"); await using var bob = await InWorld("Bob");
        await Gm("alice", "!item 1002004 2");
        await Loop.InvokeAsync(async () => { Session("bob").Dead = true; await Character.SetHpSpAsync(Session("bob"), 0, 0); });
        var combat = _app.Services.GetRequiredService<CombatService>();
        var ownerCheck = combat.ReviveCheck;
        await Loop.InvokeAsync(() => { combat.ReviveCheck = _ => ErrorCodes.FeatureUnavailable; return Task.CompletedTask; });
        await alice.SendGameAsync(CombatService.UseSkill, new CastSkillReq { SkillId = 2200101, TargetId = (long)bob.Self!.Uuid });
        Assert.Equal(ErrorCodes.FeatureUnavailable, (await Next(alice, CombatService.UseSkill, CastSkillRsp.Parser)).Result);
        Assert.True(Session("bob").Dead);
        Assert.Equal(2, Session("alice").Items.Where(i => i.Entry == 1002004).Sum(i => i.Stack));
        await Loop.InvokeAsync(() => { combat.ReviveCheck = ownerCheck; return Task.CompletedTask; });
        await alice.SendGameAsync(CombatService.UseSkill, new CastSkillReq { SkillId = 2200101, TargetId = (long)bob.Self!.Uuid });
        Assert.Equal(0, (await Next(alice, CombatService.UseSkill, CastSkillRsp.Parser)).Result);
        var notice = await Next(alice, BuffService.WorldTreeRevive, ItemWorldTreeReviveNtf.Parser);
        Assert.Equal("Bob", notice.WorldTreePlayerName);
        await Loop.InvokeAsync(() =>
        {
            Assert.False(Session("bob").Dead); Assert.True(Character.Stats(Session("bob"))[2000] > 0);
            Assert.Equal(1, Session("alice").Items.Where(i => i.Entry == 1002004).Sum(i => i.Stack));
            return Task.CompletedTask;
        });
    }

    [ClientDataFact]
    public async Task BackSlideMovesTheActorAndItsAuthoritativePosition()
    {
        await using var alice = await InWorld("Alice");
        await Gm("alice", "!job 5100"); await Teach("alice", 15104);
        var session = Session("alice");
        var start = session.Position.Clone();
        var nav = _app.Services.GetRequiredService<WorldData>().Map(session.SceneEntry)!.Nav;
        int direction = Enumerable.Range(0, 8).Select(i => i * 45).First(angle => nav == null ||
            nav.CanWalk(start.X, start.Y, start.Z, start.X + 2 * MathF.Sin(angle * MathF.PI / 180),
                start.Z + 2 * MathF.Cos(angle * MathF.PI / 180)));
        await alice.SendGameAsync(CombatService.UseSkill, new CastSkillReq { SkillId = 1510401, Face = direction - 180 });
        Assert.Equal(0, (await Next(alice, CombatService.UseSkill, CastSkillRsp.Parser)).Result);
        var moved = await Next(alice, CombatService.SkillMove, SkillStartMoveNotify.Parser,
            m => m.EntityID == alice.Self!.Uuid);
        Assert.Equal(start, moved.StartPos);
        Assert.Equal(1510403, moved.EffectId); // the client's SkillStartMove ignores effectId 0
        Assert.Equal(180, moved.MoveTimeMs);
        Assert.InRange(MonsterService.Distance(start.X, start.Z, moved.EndPos.X, moved.EndPos.Z), 0.1f, 10.01f); // "${1}=10 meters"
        await Loop.InvokeAsync(() => { Assert.Equal(moved.EndPos, session.Position); return Task.CompletedTask; });
    }

    [ClientDataFact]
    public async Task ACasterStillWalkingIntoRangeIsAcceptedAndAStandingOneOutOfRangeIsNot()
    {
        // In-game 11.10: the client sends the attack on reaching range and its stop 13 ms later; the server measured
        // from the last walk's start and refused 3024 on most approaches.
        await using var alice = await InWorld("Alice");
        var target = await Target("alice", stationary: true);
        var start = Session("alice").Position.Clone();
        // 5 m (beyond the 3 m basic attack) in a direction the nav grid lets the attack through.
        var nav = _app.Services.GetRequiredService<WorldData>().Map(Session("alice").SceneEntry)!.Nav!;
        var away = new[] { (5f, 0f), (-5f, 0f), (0f, 5f), (0f, -5f), (3.6f, 3.6f), (-3.6f, -3.6f) }
            .Select(o => new Vec3 { X = start.X + o.Item1, Y = start.Y, Z = start.Z + o.Item2 })
            .First(p => nav.IsWalkable(p.X, p.Y, p.Z) && nav.CanWalk(p.X, p.Y, p.Z, start.X, start.Z));
        await alice.SendGameAsync(MsgId.Walk, new WalkReq { Pos = away, Face = 180, BStop = true, SpeedRate = 1 });
        await alice.SendGameAsync(CombatService.UseSkill, new CastSkillReq { SkillId = 1000001, TargetId = target.Uid, ComboIndex = 1 });
        Assert.Equal(ErrorCodes.SkillDistance, (await Next(alice, CombatService.UseSkill, CastSkillRsp.Parser)).Result);

        long speed = await Loop.InvokeAsync(() => Task.FromResult(Session("alice").Speed));
        Assert.True(speed >= 400, $"speed {speed}"); // 0.5 s below covers the 1.5 m beyond range at ≥ 4 m/s
        await alice.SendGameAsync(MsgId.Walk, new WalkReq { Pos = away, Dir = 180, Face = 180, SpeedRate = 1 });
        await Task.Delay(500);
        await alice.SendGameAsync(CombatService.UseSkill, new CastSkillReq { SkillId = 1000001, TargetId = target.Uid, ComboIndex = 1 });
        var accepted = await Next(alice, CombatService.UseSkill, CastSkillRsp.Parser);
        Assert.Equal(0, accepted.Result);
        // The client's stop follows at once, where it really was: the cast continues and hits.
        var arrived = new Vec3 { X = start.X + (away.X - start.X) * 0.5f, Y = start.Y, Z = start.Z + (away.Z - start.Z) * 0.5f };
        await alice.SendGameAsync(MsgId.Walk, new WalkReq { Pos = arrived, Face = 180, BStop = true, SpeedRate = 1 });
        var hit = await Next(alice, CombatService.SkillHit, SkillHitInfo.Parser, p => p.SessionId == accepted.SessionId);
        Assert.Equal(target.Uid, Assert.Single(hit.Dsts).DstID);
    }

    [ClientDataFact]
    public async Task ACasterOnALongStraightApproachKeepsItsCastWhenItStops()
    {
        // In-game 22.10: the client sends a walk only when its direction changes; Sonic Blow cast after a 0.86 s straight
        // leg was accepted, then its stop 6 m from the leg's start interrupted it (the stop allowed 0.75 s of walking).
        await using var alice = await InWorld("Alice");
        var target = await Target("alice", stationary: true);
        var start = Session("alice").Position.Clone();
        var nav = _app.Services.GetRequiredService<WorldData>().Map(Session("alice").SceneEntry)!.Nav!;
        var away = new[] { (9f, 0f), (-9f, 0f), (0f, 9f), (0f, -9f), (6.4f, 6.4f), (-6.4f, -6.4f) }
            .Select(o => new Vec3 { X = start.X + o.Item1, Y = start.Y, Z = start.Z + o.Item2 })
            .First(p => nav.IsWalkable(p.X, p.Y, p.Z) && nav.CanWalk(p.X, p.Y, p.Z, start.X, start.Z));
        await alice.SendGameAsync(MsgId.Walk, new WalkReq { Pos = away, Face = 180, BStop = true, SpeedRate = 1 });
        long speed = await Loop.InvokeAsync(() => Task.FromResult(Session("alice").Speed));
        // Walk until 2 m from the target: 7 m, longer than 0.75 s at this speed.
        int walkMs = (int)(7 / (speed / 100.0) * 1000);
        Assert.True(walkMs > 900, $"speed {speed}");
        await alice.SendGameAsync(MsgId.Walk, new WalkReq { Pos = away, Dir = 180, Face = 180, SpeedRate = 1 });
        await Task.Delay(walkMs);
        await alice.SendGameAsync(CombatService.UseSkill, new CastSkillReq { SkillId = 1000001, TargetId = target.Uid, ComboIndex = 1 });
        var accepted = await Next(alice, CombatService.UseSkill, CastSkillRsp.Parser);
        Assert.Equal(0, accepted.Result);
        var arrived = new Vec3 { X = start.X + (away.X - start.X) * 2 / 9f, Y = start.Y, Z = start.Z + (away.Z - start.Z) * 2 / 9f };
        await alice.SendGameAsync(MsgId.Walk, new WalkReq { Pos = arrived, Face = 180, BStop = true, SpeedRate = 1 });
        var hit = await Next(alice, CombatService.SkillHit, SkillHitInfo.Parser, p => p.SessionId == accepted.SessionId);
        Assert.Equal(target.Uid, Assert.Single(hit.Dsts).DstID);
    }

    [ClientDataFact]
    public async Task ArcherCastAtClientRangeBoundaryHitsAnExactlyRepresentedFieldMonster()
    {
        // ClientB skill_13000.m_fAtkRange is 18 m. NPCConfig[5000007]._iPrefabRadius is 0,
        // and UnitConfig[31007]._iCollisionType is None, so the client's target radius is 0.
        await using var alice = await InWorld("Alice");
        await Gm("alice", "!job 3100");
        const int Bow = 2110120;
        await Gm("alice", $"!item {Bow}");
        var granted = await Next(alice, CharacterService.ItemsSync, DBItemsInfoIncrement.Parser,
            m => m.PackInfo.GetValueOrDefault(2)?.Inc?.Items.Values.Any(i => i.All?.ItemEntry == Bow) == true);
        long bow = granted.PackInfo[2].Inc.Items.Values.Single(i => i.All?.ItemEntry == Bow).All.ItemGuid;
        await alice.SendGameAsync(CharacterService.EquipItemOpr,
            new EquipItemOprReq { Opr = 0, Pos = (EquipGridType)1, ItemGUID = bow });
        Assert.Equal(0, (await Next(alice, CharacterService.EquipItemOpr, EquipItemOprResp.Parser)).Result);
        var live = Session("alice");
        int normalAttack = _app.Services.GetRequiredService<SkillService>().NormalAttack(live);
        Assert.True(normalAttack == 1300001,
            $"career={live.Progress!.Career}, attack={normalAttack}, items={string.Join(';', live.Items.Select(i => $"{i.Entry}/{i.Pack}/{i.Grid}"))}");
        var target = await Target("alice", stationary: true, npcId: 5000007);
        var aoi = await Next(alice, GameAoiSink.SyncAoiView, AoIViewChangeData.Parser,
            view => view.Bases.Any(b => b.Uuid == (ulong)target.Uid));
        var wireUid = Assert.Single(aoi.Bases, b => b.Uuid == (ulong)target.Uid).Uuid;
        Assert.Equal(target.Uid, (long)(double)wireUid); // the client's shared-memory path uses doubles

        var start = Session("alice").Position.Clone();
        var nav = _app.Services.GetRequiredService<WorldData>().Map(Session("alice").SceneEntry)!.Nav!;
        var approach = Enumerable.Range(0, 32).Select(i => i * MathF.Tau / 32)
            .Select(angle => (
                Far: new Vec3
                {
                    X = start.X + 18.7f * MathF.Cos(angle), Y = start.Y,
                    Z = start.Z + 18.7f * MathF.Sin(angle)
                },
                Near: new Vec3
                {
                    X = start.X + 18f * MathF.Cos(angle), Y = start.Y,
                    Z = start.Z + 18f * MathF.Sin(angle)
                }))
            .First(p => nav.IsWalkable(p.Far.X, p.Far.Y, p.Far.Z)
                && nav.IsWalkable(p.Near.X, p.Near.Y, p.Near.Z)
                && nav.CanWalk(start.X, start.Y, start.Z, p.Far.X, p.Far.Z));

        await alice.SendGameAsync(MsgId.Walk, new WalkReq { Pos = approach.Far, BStop = true, SpeedRate = 1 });
        await alice.SendGameAsync(CombatService.UseSkill, new CastSkillReq { SkillId = 1300001, TargetId = (long)wireUid });
        Assert.Equal(ErrorCodes.SkillDistance, (await Next(alice, CombatService.UseSkill, CastSkillRsp.Parser)).Result);

        await alice.SendGameAsync(MsgId.Walk, new WalkReq { Pos = approach.Near, BStop = true, SpeedRate = 1 });
        long before = target.Hp;
        await alice.SendGameAsync(CombatService.UseSkill, new CastSkillReq { SkillId = 1300001, TargetId = (long)wireUid });
        var accepted = await Next(alice, CombatService.UseSkill, CastSkillRsp.Parser);
        Assert.Equal(0, accepted.Result);
        var hit = await Next(alice, CombatService.SkillHit, SkillHitInfo.Parser, p => p.SessionId == accepted.SessionId);
        var damage = Assert.Single(hit.Dsts);
        Assert.Equal(target.Uid, damage.DstID);
        Assert.True(damage.Damage > 0);
        Assert.True(target.Hp < before);
    }
}

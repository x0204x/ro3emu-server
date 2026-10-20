using System.Collections.Concurrent;
using System.Diagnostics;
using Romsg;
using Ro3Emu.Server.Game;

namespace Ro3Emu.LoadTest;

/// <summary>
/// S5: a castle siege at full size. Two guilds of up to 80 (GuildBuildingConfig _iPeople) fill one siege instance
/// (2230101, city 1001) and fight: walk at the enemy, basic attack the nearest visible enemy, revive after death.
/// Setup uses real protocol paths (guild create, auto-accept edit, apply, 30400 entry) plus two local GM test aids:
/// <c>!lvl 99</c> and <c>!siege seed/open</c>, which stand in for a season of territory battles.
/// </summary>
public sealed partial class ScenarioRunner
{
    private const long SiegeScene = 2230101;
    private const int SiegeCity = 1001, GuildCap = 80;
    private readonly ConcurrentDictionary<long, int> _siegeTeam = new();

    private async Task PrepareSiegeAsync(RunReport report, CancellationToken ct)
    {
        var actors = _actors.OrderBy(a => a.Account).ToArray();
        if (actors.Length < 4) throw new InvalidOperationException("S5 needs at least 4 bots");
        if (actors.Length > 2 * GuildCap) throw new InvalidOperationException($"S5 fits at most {2 * GuildCap} bots (two guilds of {GuildCap})");
        using (var gate = new SemaphoreSlim(16))
            await Task.WhenAll(actors.Select(async a =>
            {
                await gate.WaitAsync(ct);
                try { await S3GmAsync(a, "!lvl 99", ct); } finally { gate.Release(); }
            }));
        var leaders = new[] { actors[0], actors[1] };
        var guilds = new long[2];
        for (int team = 0; team < 2; team++)
        {
            var leader = leaders[team];
            using var created = leader.Bot.Inbox.Expect(e => e.IsGame(GuildService.Create));
            using var full = leader.Bot.Inbox.Expect(e => e.IsGame(GuildService.Full) && GuildFullInfoNtf.Parser.ParseFrom(e.Body).Guid > 0);
            await leader.Bot.SendGameAsync(GuildService.Create, new GuildCreateReq
            {
                Name = $"S5{(char)('A' + team)}{_prefix[2..6]}",
                Icon = new GuildHeadIcon { BackgroundType = 100101, IconType = 200101 }
            });
            int result = GuildCreateResp.Parser.ParseFrom((await created.WaitAsync(TimeSpan.FromSeconds(15), ct)).Body).Result;
            if (result != 0) throw new InvalidOperationException($"S5 guild create refused {result}");
            guilds[team] = GuildFullInfoNtf.Parser.ParseFrom((await full.WaitAsync(TimeSpan.FromSeconds(15), ct)).Body).Guid;
            using var edited = leader.Bot.Inbox.Expect(e => e.IsGame(GuildService.Edit) && GuildEditResp.Parser.ParseFrom(e.Body).Tp == 10);
            await leader.Bot.SendGameAsync(GuildService.Edit, new GuildEditReq
            {
                Tp = (GuildEditReq.Types.EditType)10,
                Announce = "S5", JoinLevelLimit = 0, AutoAcceptApply = true
            });
            result = GuildEditResp.Parser.ParseFrom((await edited.WaitAsync(TimeSpan.FromSeconds(15), ct)).Body).Result;
            if (result != 0) throw new InvalidOperationException($"S5 guild auto-accept refused {result}");
        }
        for (int i = 0; i < actors.Length; i++) _siegeTeam[actors[i].RoleId] = i % 2;
        // Applications are sequential per guild: auto-accept joins inside the apply, and the application list is capped.
        foreach (var member in actors.Skip(2))
        {
            int team = _siegeTeam[member.RoleId];
            using var applied = member.Bot.Inbox.Expect(e => e.IsGame(GuildService.Apply));
            await member.Bot.SendGameAsync(GuildService.Apply, new GuildEnterApplyReq { GuildIds = { guilds[team] } });
            var response = GuildEnterApplyResp.Parser.ParseFrom((await applied.WaitAsync(TimeSpan.FromSeconds(15), ct)).Body);
            int code = response.Result != 0 ? response.Result : response.ApplyResult.FirstOrDefault()?.Result ?? -1;
            if (code != 0) throw new InvalidOperationException($"S5 guild apply refused {code} for {member.Account}");
        }
        await S3GmAsync(leaders[0], $"!siege seed {SiegeCity} {guilds[0]} {guilds[1]}", ct);
        await S3GmAsync(leaders[0], "!siege open", ct);
        var entry = _setupCounts.Action("siege-entry");
        using (var gate = new SemaphoreSlim(10))
            await Task.WhenAll(actors.Select(async actor =>
            {
                await gate.WaitAsync(ct);
                entry.Plan(); entry.Offer();
                var sw = Stopwatch.StartNew();
                try
                {
                    using var reply = actor.Bot.Inbox.Expect(e => e.IsGame(30400));
                    using var switchScene = actor.Bot.Inbox.Expect(e => e.IsGame(MsgId.SwitchSceneLine));
                    using var self = actor.Bot.Inbox.Expect(e => e.IsGame(MsgId.EntitySelf));
                    using var end = actor.Bot.Inbox.Expect(e => e.IsGame(MsgId.SelfInfoEndFrameNotify));
                    await actor.Bot.SendGameAsync(30400, new EnterDungeonReq
                    {
                        SceneEntry = SiegeScene,
                        InitData = new DungeonInitData { SiegeInitData = new SiegeInitData { CityId = SiegeCity } }
                    });
                    int result = EnterDungeonRsp.Parser.ParseFrom((await reply.WaitAsync(TimeSpan.FromSeconds(30), ct)).Body).Result;
                    if (result != 0) throw new InvalidOperationException($"siege entry refused {result}");
                    var scene = SwitchSceneLineResp.Parser.ParseFrom((await switchScene.WaitAsync(TimeSpan.FromSeconds(30), ct)).Body);
                    await LoadSceneAsync(actor, scene.SceneEntry, self, end, ct);
                    if (actor.Scene != SiegeScene) throw new InvalidOperationException($"siege entry reached scene {actor.Scene}");
                    entry.Complete(sw.Elapsed.TotalMilliseconds);
                }
                catch (Exception e) { entry.Fail(); throw new InvalidOperationException($"S5 {actor.Account}: {e.Message}", e); }
                finally { gate.Release(); }
            }));
        report.S3SetupFixtures = [$"S5: {actors.Length} roles at level 99 (GM), guilds {guilds[0]} / {guilds[1]} with auto-accept, " +
            $"!siege seed {SiegeCity} + !siege open (test aids), all in siege scene {SiegeScene}"];
    }

    private async Task RunSiegeAsync(BotActor[] actors, int seconds, CancellationToken ct)
    {
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(ct);
        stop.CancelAfter(TimeSpan.FromSeconds(seconds));
        // Each side marches on the other's entry point; contact happens wherever the columns meet.
        var rally = new (float X, float Z)[2];
        for (int team = 0; team < 2; team++)
        {
            var side = actors.Where(a => _siegeTeam[a.RoleId] == team).ToArray();
            rally[team] = (side.Average(a => a.HomeX), side.Average(a => a.HomeZ));
        }
        var clock = Stopwatch.StartNew();
        var tasks = actors.Select(async (actor, index) =>
        {
            var random = new Random(index + 4242);
            int team = _siegeTeam[actor.RoleId];
            var goal = rally[1 - team];
            var due = TimeSpan.FromMilliseconds(random.Next(1000));
            try
            {
                while (!stop.IsCancellationRequested)
                {
                    if (due > clock.Elapsed) await Task.Delay(due - clock.Elapsed, stop.Token);
                    due += TimeSpan.FromMilliseconds(800 + random.Next(400));
                    var enemy = actor.Reducer.Players()
                        .Where(p => _siegeTeam.TryGetValue((long)p.Uuid, out int t) && t != team)
                        .OrderBy(p => Distance(actor, p)).FirstOrDefault();
                    float tx = enemy?.MovementInfo.Pos.X ?? goal.X + random.Next(-6, 7);
                    float tz = enemy?.MovementInfo.Pos.Z ?? goal.Z + random.Next(-6, 7);
                    float dx = tx - actor.X, dz = tz - actor.Z, distance = MathF.Sqrt(dx * dx + dz * dz);
                    if (enemy == null || distance > 3.5f)
                    {
                        if (distance < 1f) continue;
                        float step = MathF.Min(2.5f, enemy == null ? distance : distance - 2.5f);
                        float x = actor.X + dx / distance * step, z = actor.Z + dz / distance * step;
                        var walk = _counts.Action("siege-walk"); walk.Plan(); walk.Offer();
                        using var walkAttempt = new ActionAttempt(walk);
                        try { await actor.Sender!.SendAsync(() => actor.Bot.WalkAsync(x, actor.Y, z, stop: true), stop.Token); walkAttempt.Complete(); }
                        catch (Exception e) { walkAttempt.Fail(e); throw; }
                        actor.X = x; actor.Z = z;
                        continue;
                    }
                    var tally = _counts.Action("siege-cast"); tally.Plan(); tally.Offer();
                    using var attempt = new ActionAttempt(tally);
                    try
                    {
                        using var reply = actor.Bot.Inbox.Expect(e => e.IsGame(CombatService.UseSkill));
                        await actor.Sender!.SendAsync(() => actor.Bot.SendGameAsync(CombatService.UseSkill,
                            new CastSkillReq { SkillId = 1000001, TargetId = (long)enemy.Uuid }), stop.Token);
                        var response = CastSkillRsp.Parser.ParseFrom((await reply.WaitAsync(TimeSpan.FromSeconds(4), stop.Token)).Body);
                        if (response.Result == 0) { attempt.Complete(); continue; }
                        attempt.Refuse();
                    }
                    catch (Exception error) when (error is not OperationCanceledException) { attempt.Fail(error); throw; }
                    // A refused swing is usually a dead caster: ask to revive (a live caster gets TargetAlive back).
                    var revive = _counts.Action("siege-revive"); revive.Plan(); revive.Offer();
                    using var reviveAttempt = new ActionAttempt(revive);
                    using var ack = actor.Bot.Inbox.Expect(e => e.IsGame(CombatService.Relive));
                    await actor.Sender!.SendAsync(() => actor.Bot.SendGameAsync(CombatService.Relive, new EntityReliveReq()), stop.Token);
                    var relive = EntityReliveAck.Parser.ParseFrom((await ack.WaitAsync(TimeSpan.FromSeconds(4), stop.Token)).Body);
                    if (relive.Result != 0) { reviveAttempt.Refuse(); continue; }
                    reviveAttempt.Complete();
                    if (actor.Reducer.SelfPosition() is { } at) { actor.X = at.X; actor.Z = at.Z; }
                }
            }
            catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
            catch (Exception e) { _failures[300000 + index] = $"siege: {e.GetType().Name}: {e.Message}"; }
        });
        await Task.WhenAll(tasks);
        var remaining = TimeSpan.FromSeconds(seconds) - clock.Elapsed;
        if (remaining > TimeSpan.Zero) await Task.Delay(remaining, ct);
    }
}

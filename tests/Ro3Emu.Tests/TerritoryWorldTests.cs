using System.Text.Json;
using Google.Protobuf;
using Microsoft.AspNetCore.Builder;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Romsg;
using Ro3Emu.Bot;
using Ro3Emu.Server;
using Ro3Emu.Server.Accounts;
using Ro3Emu.Server.Game;
using Ro3Emu.Server.Maps;

namespace Ro3Emu.Tests;

public sealed class TerritoryWorldTests : IAsyncLifetime
{
    private readonly string _dataDir = Path.Combine(Path.GetTempPath(), "ro3emu-territory-world-" + Guid.NewGuid().ToString("N"));
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(20);
    private sealed class TestClock : TimeProvider
    {
        private long _frozenSeconds;
        public void Freeze() => Interlocked.Exchange(ref _frozenSeconds, DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        public override DateTimeOffset GetUtcNow() => Interlocked.Read(ref _frozenSeconds) is > 0 and var frozen
            ? DateTimeOffset.FromUnixTimeSeconds(frozen) : DateTimeOffset.UtcNow;
    }
    private readonly TestClock _clock = new();
    private WebApplication _app = null!;
    private string _http = "";
    private T Service<T>() where T : notnull => _app.Services.GetRequiredService<T>();
    private Task OnLoop(Func<Task> work) => Service<GameLoop>().InvokeAsync(work);
    // World units belong to the loop; a test reads a snapshot taken there (a concurrent spawn tick changes the list).
    private Task<List<WorldUnit>> Units() => Service<GameLoop>().InvokeAsync(() => Task.FromResult(Service<World>().AllUnits().ToList()));

    public Task InitializeAsync() => StartAsync();
    private async Task StartAsync()
    {
        var (web, micro, game) = TestPorts.FreePorts();
        _app = ServerHost.Create([], o =>
        {
            o.DataDir = _dataDir;
            o.GameDataDir = Path.Combine(RepoRoot(), "data");
            o.HttpPort = web; o.MicroGatePort = micro; o.GameGatePort = game;
        }, _clock);
        await _app.StartAsync();
        _http = $"http://127.0.0.1:{web}";
    }

    public async Task DisposeAsync()
    {
        await _app.StopAsync(); await _app.DisposeAsync();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dataDir, true); } catch (IOException) { }
    }

    private static string RepoRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "Ro3Emu.slnx"))) return dir.FullName;
        throw new DirectoryNotFoundException();
    }

    private async Task<BotClient> InWorld(string name, bool create = true)
    {
        var bot = new BotClient(_http, File.ReadAllText(Path.Combine(_dataDir, "gate_rsa_public.pem")));
        await bot.LoginAsync(name.ToLowerInvariant(), "pw"); await bot.EnterGateAsync();
        long role = create ? (await bot.CreateRoleAsync(name)).RoleBaseInfo.Id : bot.Roles.Single().Id;
        await bot.EnterWorldAsync(role);
        return bot;
    }

    private TerritoryWarsService.WorldState DurableWars()
    {
        using var db = new SqliteConnection($"Data Source={Service<AccountStore>().DatabasePath};Mode=ReadOnly");
        db.Open();
        using var command = db.CreateCommand();
        command.CommandText = "SELECT state FROM territory_wars WHERE id=1";
        return JsonSerializer.Deserialize<TerritoryWarsService.WorldState>((byte[])command.ExecuteScalar()!)!;
    }
    private long PickupSources()
    {
        using var db = new SqliteConnection($"Data Source={Service<AccountStore>().DatabasePath};Mode=ReadOnly");
        db.Open();
        using var command = db.CreateCommand();
        command.CommandText = "SELECT count(*) FROM operation_source WHERE source_key LIKE 'territory-world-pickup:%'";
        return (long)command.ExecuteScalar()!;
    }
    private long DropSources()
    {
        using var db = new SqliteConnection($"Data Source={Service<AccountStore>().DatabasePath};Mode=ReadOnly");
        db.Open();
        using var command = db.CreateCommand();
        command.CommandText = "SELECT count(*) FROM operation_source WHERE source_key LIKE 'territory-drop:%:create'";
        return (long)command.ExecuteScalar()!;
    }
    private long DropClaimSources()
    {
        using var db = new SqliteConnection($"Data Source={Service<AccountStore>().DatabasePath};Mode=ReadOnly");
        db.Open();
        using var command = db.CreateCommand();
        command.CommandText = "SELECT count(*) FROM operation_source WHERE source_key LIKE 'territory-drop:%:claim'";
        return (long)command.ExecuteScalar()!;
    }

    private GameSession Session(BotClient bot) => Service<SessionRegistry>().All()
        .Single(s => s.Account == bot.Account && s.Role != null);

    private async Task AwaitSceneReady(BotClient bot)
    {
        for (int i = 0; i < 2000; i++)
        {
            bool ready = false;
            await OnLoop(() =>
            {
                var session = Session(bot);
                ready = !session.SceneLoading && session.Handoff == null;
                return Task.CompletedTask;
            });
            if (ready) return;
            await Task.Delay(10);
        }
        throw new TimeoutException("Territory scene load did not finish its awaited world hooks.");
    }

    private static async Task<T> Next<T>(BotClient bot, uint id, MessageParser<T> parser, Func<T, bool>? match = null) where T : IMessage<T>
    {
        var envelope = await bot.Inbox.WaitAsync(e => e.IsGame(id) && (match == null || match(parser.ParseFrom(e.Body))), Wait);
        return parser.ParseFrom(envelope.Body);
    }

    [ClientDataTheory]
    [InlineData(30311L, 1000001L)]
    [InlineData(30336L, 10001L)]
    public async Task LandActivityUsesExportedEditorPointInsteadOfDefaultOrigin(long mapId, long editorPointId)
    {
        await using var bot = await InWorld("WorldScout");
        await bot.SendGameAsync(SceneService.SceneTeleport, new SceneTeleportReq { TargetMapId = mapId });
        Assert.Equal(0, (await Next(bot, SceneService.SceneTeleport, SceneTeleportRsp.Parser)).Result);
        Assert.Equal(mapId, (await Next(bot, MsgId.SwitchSceneLine, SwitchSceneLineResp.Parser)).SceneEntry);
        await bot.SendGameAsync(MsgId.SceneLoaded, new SceneLoadedReq { SceneId = mapId });
        Assert.Equal(0, (await Next(bot, MsgId.SceneLoaded, SceneLoadedResp.Parser)).Result);
        await Next(bot, MsgId.EntitySelf, EntitySelf.Parser);
        // The loaded-scene handler awaits land spawning before joining AOI; its final callback is asynchronous.
        WorldUnit[] actors = [];
        for (int i = 0; i < 2000 && actors.Length == 0; i++)
        {
            await OnLoop(() =>
            {
                actors = Service<World>().AllUnits().Where(u => u.SceneEntry == mapId
                    && u.Entity().EntityTemplateID == 4907011).ToArray();
                return Task.CompletedTask;
            });
            if (actors.Length == 0) await Task.Delay(10);
        }
        await OnLoop(() =>
        {
            var actor = Assert.Single(actors);
            var point = Service<WorldData>().Map(mapId)!.Points[editorPointId];
            Assert.InRange(Math.Abs(actor.X - point.X), 0, 0.01f);
            Assert.InRange(Math.Abs(actor.Z - point.Z), 0, 0.01f);
            Assert.True(Math.Abs(actor.X) + Math.Abs(actor.Z) > 1);
            return Task.CompletedTask;
        });
    }

    [ClientDataFact]
    public async Task WorldPickupIsFiniteCappedAndDeathDropCanBeRecovered()
    {
        await using var bot = await InWorld("WorldGatherer");
        const long mapId = 30330; // SceneType 8, three 4910101 pickup groups and a PvP mode.
        await bot.SendGameAsync(SceneService.SceneTeleport, new SceneTeleportReq { TargetMapId = mapId });
        Assert.Equal(0, (await Next(bot, SceneService.SceneTeleport, SceneTeleportRsp.Parser)).Result);
        Assert.Equal(mapId, (await Next(bot, MsgId.SwitchSceneLine, SwitchSceneLineResp.Parser)).SceneEntry);
        await bot.SendGameAsync(MsgId.SceneLoaded, new SceneLoadedReq { SceneId = mapId });
        Assert.Equal(0, (await Next(bot, MsgId.SceneLoaded, SceneLoadedResp.Parser)).Result);
        await Next(bot, MsgId.EntitySelf, EntitySelf.Parser);
        await Next(bot, GameAoiSink.SyncAoiView, AoIViewChangeData.Parser,
            view => view.Bases.Any(b => b.EntityTemplateID == 4910101));

        var unit = (await Units()).First(u => u.SceneEntry == mapId && u.Entity().EntityTemplateID == 4910101);
        await OnLoop(() => Service<SceneService>().TeleportInMapAsync(Session(bot), new MapPoint(unit.X, unit.Y, unit.Z, 0)));
        var pickup = new NpcInteractionReq { NpcUid = unit.Uid, InteractionId = 7000004 };
        await bot.SendGameAsync(SceneService.NpcInteraction, pickup);
        Assert.Equal(0, (await Next(bot, SceneService.NpcInteraction, NpcInteractionRsp.Parser)).Result);
        Assert.Equal(10, Session(bot).Progress!.Currency.GetValueOrDefault(133));
        await bot.SendGameAsync(SceneService.NpcInteraction, pickup);
        Assert.Equal(ErrorCodes.NpcMissing, (await Next(bot, SceneService.NpcInteraction, NpcInteractionRsp.Parser)).Result);

        var cappedUnit = (await Units()).First(u => u.SceneEntry == mapId
            && u.Uid != unit.Uid && u.Entity().EntityTemplateID == 4910101);
        await OnLoop(async () =>
        {
            Assert.NotNull(await Service<CharacterService>().ExchangeAsync(Session(bot), [], [(133, 4985)]));
            await Service<SceneService>().TeleportInMapAsync(Session(bot), new MapPoint(cappedUnit.X, cappedUnit.Y, cappedUnit.Z, 0));
        });
        var cappedReq = new NpcInteractionReq { NpcUid = cappedUnit.Uid, InteractionId = 7000004 };
        await bot.SendGameAsync(SceneService.NpcInteraction, cappedReq);
        Assert.Equal(ErrorCodes.BagFull, (await Next(bot, SceneService.NpcInteraction, NpcInteractionRsp.Parser)).Result);
        Assert.Equal(4995, Session(bot).Progress!.Currency.GetValueOrDefault(133));
        Assert.Contains(cappedUnit, (await Units()));
        await OnLoop(async () => Assert.NotNull(await Service<CharacterService>().ExchangeAsync(Session(bot), [(133, 4985)], [])));

        await bot.SendGameAsync(TerritoryWarsService.Camp, new PvpSetRoleCampReq { Type = EPvpRoleCampNum.Free });
        Assert.Equal(0, (await Next(bot, TerritoryWarsService.Camp, PvpSetRoleCampResp.Parser)).Result);
        await OnLoop(async () =>
        {
            var session = Session(bot);
            session.Dead = true;
            await Service<TerritoryWarsService>().WorldDiedAsync(session);
        });
        Assert.Equal(8, Session(bot).Progress!.Currency.GetValueOrDefault(133)); // Free camp loses ceil(10 * 0.2).
        var dropped = (await Units()).Single(u => u.SceneEntry == mapId && u.Entity().EntityTemplateID == 4910104);
        await OnLoop(() => { Session(bot).Dead = false; return Task.CompletedTask; });
        await bot.SendGameAsync(SceneService.NpcInteraction, new NpcInteractionReq { NpcUid = dropped.Uid, InteractionId = 491000301 });
        Assert.Equal(0, (await Next(bot, SceneService.NpcInteraction, NpcInteractionRsp.Parser)).Result);
        Assert.Equal(10, Session(bot).Progress!.Currency.GetValueOrDefault(133));
    }

    [ClientDataFact]
    public async Task StaticPickupKeepsOneNaturalSourceAndRespawnDeadlineAcrossRestart()
    {
        // Keep the configured game deadline fixed while the process restarts. Wall-clock startup
        // can outlast a short respawn timer, which is a valid new occurrence rather than a replay.
        _clock.Freeze();
        const long mapId = 30330;
        int initialCount;
        string slot;
        long due;
        await using (var bot = await InWorld("WorldOnce"))
        {
            await bot.SendGameAsync(SceneService.SceneTeleport, new SceneTeleportReq { TargetMapId = mapId });
            Assert.Equal(0, (await Next(bot, SceneService.SceneTeleport, SceneTeleportRsp.Parser)).Result);
            await Next(bot, MsgId.SwitchSceneLine, SwitchSceneLineResp.Parser, x => x.SceneEntry == mapId);
            await bot.SendGameAsync(MsgId.SceneLoaded, new SceneLoadedReq { SceneId = mapId });
            Assert.Equal(0, (await Next(bot, MsgId.SceneLoaded, SceneLoadedResp.Parser)).Result);
            await Next(bot, MsgId.EntitySelf, EntitySelf.Parser);
            await Next(bot, GameAoiSink.SyncAoiView, AoIViewChangeData.Parser,
                view => view.Bases.Any(b => b.EntityTemplateID == 4910101));
            await AwaitSceneReady(bot);
            var units = (await Units()).Where(u => u.SceneEntry == mapId
                && u.Entity().EntityTemplateID == 4910101).ToArray();
            initialCount = units.Length;
            Assert.True(initialCount > 1);
            var unit = units[0];
            await OnLoop(() => Service<SceneService>().TeleportInMapAsync(Session(bot), new MapPoint(unit.X, unit.Y, unit.Z, 0)));
            var request = new NpcInteractionReq { NpcUid = unit.Uid, InteractionId = 7000004 };
            await bot.SendGameAsync(SceneService.NpcInteraction, request);
            Assert.Equal(0, (await Next(bot, SceneService.NpcInteraction, NpcInteractionRsp.Parser)).Result);
            Assert.Equal(1, PickupSources());
            var occurrence = Assert.Single(DurableWars().WorldPickupOccurrences);
            slot = occurrence.Key;
            Assert.Equal(1, occurrence.Value.Ordinal);
            due = occurrence.Value.NextEligible;
            Assert.True(due > _clock.GetUtcNow().ToUnixTimeSeconds());
            await bot.SendGameAsync(SceneService.NpcInteraction, request);
            Assert.Equal(ErrorCodes.NpcMissing, (await Next(bot, SceneService.NpcInteraction, NpcInteractionRsp.Parser)).Result);
            Assert.Equal(1, PickupSources());
        }
        await _app.StopAsync(); await _app.DisposeAsync();
        SqliteConnection.ClearAllPools();
        await StartAsync();
        await using var restored = await InWorld("WorldOnce", create: false);
        await restored.SendGameAsync(SceneService.SceneTeleport, new SceneTeleportReq { TargetMapId = mapId });
        Assert.Equal(0, (await Next(restored, SceneService.SceneTeleport, SceneTeleportRsp.Parser)).Result);
        await Next(restored, MsgId.SwitchSceneLine, SwitchSceneLineResp.Parser, x => x.SceneEntry == mapId);
        await restored.SendGameAsync(MsgId.SceneLoaded, new SceneLoadedReq { SceneId = mapId });
        Assert.Equal(0, (await Next(restored, MsgId.SceneLoaded, SceneLoadedResp.Parser)).Result);
        await Next(restored, MsgId.EntitySelf, EntitySelf.Parser);
        // Spawn points are shuffled on load; the restored role can be outside every pickup's AOI.
        await AwaitSceneReady(restored);
        Assert.Equal(initialCount - 1, (await Units()).Count(u => u.SceneEntry == mapId
            && u.Entity().EntityTemplateID == 4910101));
        var saved = DurableWars().WorldPickupOccurrences[slot];
        Assert.Equal(1, saved.Ordinal);
        Assert.Equal(due, saved.NextEligible);
        Assert.Equal(1, PickupSources());
    }

    [ClientDataFact]
    public async Task CommittedStaticPickupApplyFaultRetiresActorAndCannotRemintAfterRestart()
    {
        const long mapId = 30330;
        await using (var bot = await InWorld("WorldFault"))
        {
            await bot.SendGameAsync(SceneService.SceneTeleport, new SceneTeleportReq { TargetMapId = mapId });
            Assert.Equal(0, (await Next(bot, SceneService.SceneTeleport, SceneTeleportRsp.Parser)).Result);
            await Next(bot, MsgId.SwitchSceneLine, SwitchSceneLineResp.Parser, x => x.SceneEntry == mapId);
            await bot.SendGameAsync(MsgId.SceneLoaded, new SceneLoadedReq { SceneId = mapId });
            Assert.Equal(0, (await Next(bot, MsgId.SceneLoaded, SceneLoadedResp.Parser)).Result);
            await Next(bot, MsgId.EntitySelf, EntitySelf.Parser);
            await Next(bot, GameAoiSink.SyncAoiView, AoIViewChangeData.Parser,
                view => view.Bases.Any(b => b.EntityTemplateID == 4910101));
            var unit = (await Units()).First(u => u.SceneEntry == mapId && u.Entity().EntityTemplateID == 4910101);
            await OnLoop(() => Service<SceneService>().TeleportInMapAsync(Session(bot), new MapPoint(unit.X, unit.Y, unit.Z, 0)));
            var wars = Service<TerritoryWarsService>();
            wars.BeforeWorldPickupApplyForTest = () => throw new IOException("injected committed pickup apply fault");
            try
            {
                await Assert.ThrowsAnyAsync<Exception>(() => OnLoop(async () =>
                {
                    await wars.WorldSharedAsync(Session(bot), SceneService.NpcInteraction,
                        new NpcInteractionReq { NpcUid = unit.Uid, InteractionId = 7000004 }.ToByteArray());
                }));
            }
            finally { wars.BeforeWorldPickupApplyForTest = null; }
            Assert.Equal(1, PickupSources());
            Assert.Equal(1, Assert.Single(DurableWars().WorldPickupOccurrences).Value.Ordinal);
            Assert.DoesNotContain((await Units()), current => current.Uid == unit.Uid);
        }
        await _app.StopAsync(); await _app.DisposeAsync(); SqliteConnection.ClearAllPools();
        await StartAsync();
        Assert.Equal(1, PickupSources());
        Assert.Equal(1, Assert.Single(DurableWars().WorldPickupOccurrences).Value.Ordinal);
    }

    [ClientDataFact]
    public async Task CommittedDeathDropApplyFaultPublishesExactlyOneDurableDrop()
    {
        const long mapId = 30330;
        await using (var bot = await InWorld("DropFault"))
        {
            await bot.SendGameAsync(SceneService.SceneTeleport, new SceneTeleportReq { TargetMapId = mapId });
            Assert.Equal(0, (await Next(bot, SceneService.SceneTeleport, SceneTeleportRsp.Parser)).Result);
            await Next(bot, MsgId.SwitchSceneLine, SwitchSceneLineResp.Parser, x => x.SceneEntry == mapId);
            await bot.SendGameAsync(MsgId.SceneLoaded, new SceneLoadedReq { SceneId = mapId });
            Assert.Equal(0, (await Next(bot, MsgId.SceneLoaded, SceneLoadedResp.Parser)).Result);
            await Next(bot, MsgId.EntitySelf, EntitySelf.Parser);
            await OnLoop(async () => Assert.NotNull(await Service<CharacterService>().ExchangeAsync(Session(bot), [], [(133, 10)])));
            await bot.SendGameAsync(TerritoryWarsService.Camp, new PvpSetRoleCampReq { Type = EPvpRoleCampNum.Free });
            Assert.Equal(0, (await Next(bot, TerritoryWarsService.Camp, PvpSetRoleCampResp.Parser)).Result);
            var wars = Service<TerritoryWarsService>();
            wars.BeforeWorldDeathApplyForTest = () => throw new IOException("injected committed drop apply fault");
            try
            {
                await Assert.ThrowsAnyAsync<Exception>(() => OnLoop(async () =>
                {
                    var session = Session(bot);
                    session.Dead = true;
                    await wars.WorldDiedAsync(session);
                }));
            }
            finally { wars.BeforeWorldDeathApplyForTest = null; }
            var durable = DurableWars();
            var drop = Assert.Single(durable.WorldDrops).Value;
            Assert.Equal(2, drop.Amount);
            Assert.Equal(drop.Id + 1, durable.NextWorldDropId);
            Assert.Equal(1, DropSources());
            Assert.Single((await Units()), u => u.SceneEntry == mapId && u.Entity().EntityTemplateID == 4910104);
        }
        await _app.StopAsync(); await _app.DisposeAsync(); SqliteConnection.ClearAllPools();
        await StartAsync();
        Assert.Single(DurableWars().WorldDrops);
        Assert.Equal(1, DropSources());
        await using var restored = await InWorld("DropFault", create: false);
        await restored.SendGameAsync(SceneService.SceneTeleport, new SceneTeleportReq { TargetMapId = mapId });
        Assert.Equal(0, (await Next(restored, SceneService.SceneTeleport, SceneTeleportRsp.Parser)).Result);
        await Next(restored, MsgId.SwitchSceneLine, SwitchSceneLineResp.Parser, x => x.SceneEntry == mapId);
        await restored.SendGameAsync(MsgId.SceneLoaded, new SceneLoadedReq { SceneId = mapId });
        Assert.Equal(0, (await Next(restored, MsgId.SceneLoaded, SceneLoadedResp.Parser)).Result);
        await Next(restored, MsgId.EntitySelf, EntitySelf.Parser);
        await Next(restored, GameAoiSink.SyncAoiView, AoIViewChangeData.Parser,
            view => view.Bases.Any(b => b.EntityTemplateID == 4910104));
        var actor = (await Units()).Single(u => u.SceneEntry == mapId && u.Entity().EntityTemplateID == 4910104);
        await OnLoop(() => Service<SceneService>().TeleportInMapAsync(Session(restored), new MapPoint(actor.X, actor.Y, actor.Z, 0)));
        var currentWars = Service<TerritoryWarsService>();
        currentWars.BeforeWorldPickupApplyForTest = () => throw new IOException("injected committed drop claim apply fault");
        try
        {
            await Assert.ThrowsAnyAsync<Exception>(() => OnLoop(async () =>
            {
                await currentWars.WorldSharedAsync(Session(restored), SceneService.NpcInteraction,
                    new NpcInteractionReq { NpcUid = actor.Uid, InteractionId = 491000301 }.ToByteArray());
            }));
        }
        finally { currentWars.BeforeWorldPickupApplyForTest = null; }
        Assert.Empty(DurableWars().WorldDrops);
        Assert.Equal(1, DropClaimSources());
        Assert.DoesNotContain((await Units()), unit => unit.Uid == actor.Uid);
    }
}

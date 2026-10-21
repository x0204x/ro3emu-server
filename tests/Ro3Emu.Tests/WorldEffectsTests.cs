using System.Net.Sockets;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Romsg;
using Ro3Emu.Bot;
using Ro3Emu.Server;
using Ro3Emu.Server.Game;
using Ro3Emu.Server.Maps;

namespace Ro3Emu.Tests;

/// <summary>Live 31025/28/85/86/91 effects, replay and refusal over the game wire.</summary>
public sealed class WorldEffectsTests : IAsyncLifetime
{
    private readonly string _dataDir = Path.Combine(Path.GetTempPath(), "ro3emu-effects-" + Guid.NewGuid().ToString("N"));
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(5);
    private WebApplication _app = null!;
    private string _http = "";

    public async Task InitializeAsync()
    {
        var (httpPort, microPort, gamePort) = TestPorts.FreePorts();
        _app = ServerHost.Create([], o =>
        {
            o.DataDir = _dataDir;
            o.GameDataDir = Path.Combine(RepoRoot(), "data");
            o.HttpPort = httpPort;
            o.MicroGatePort = microPort;
            o.GameGatePort = gamePort;
        });
        await _app.StartAsync();
        _http = $"http://127.0.0.1:{httpPort}";
    }

    public async Task DisposeAsync()
    {
        await _app.StopAsync();
        await _app.DisposeAsync();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dataDir, true); } catch (IOException) { }
    }

    private async Task<BotClient> InWorld(string name)
    {
        var bot = new BotClient(_http, File.ReadAllText(Path.Combine(_dataDir, "gate_rsa_public.pem")));
        await bot.LoginAsync(name.ToLowerInvariant(), "pw");
        await bot.EnterGateAsync();
        long id = (await bot.CreateRoleAsync(name)).RoleBaseInfo.Id;
        await bot.EnterWorldAsync(id);
        // SelfInfoEndFrame precedes the initial AOI join; finish scene publication before adding effects.
        await bot.Inbox.WaitAsync(e => e.IsGame(GuildService.SceneGuildId), Wait);
        return bot;
    }

    private static async Task<T> Next<T>(BotClient bot, uint msgId, Google.Protobuf.MessageParser<T> parser, Func<T, bool>? match = null)
        where T : Google.Protobuf.IMessage<T>
    {
        var e = await bot.Inbox.WaitAsync(e => e.IsGame(msgId) && (match == null || match(parser.ParseFrom(e.Body))), Wait);
        return parser.ParseFrom(e.Body);
    }

    private async Task Gm(string account, string line)
    {
        using var client = new HttpClient();
        var response = await client.PostAsync($"{_http}/ro3emu/gm?account={account}", new StringContent(line));
        response.EnsureSuccessStatusCode();
    }

    [ClientDataFact]
    public async Task ForceFieldAndLatencyReplayOnLateSceneEntryAndExpire()
    {
        await using var alice = await InWorld("Alice");
        await using var bob = await InWorld("Bob");
        await Gm("alice", "!worldlatency 25");
        Assert.Equal(25, (await Next(alice, WorldEffectsService.MovementConfig, MovementConfigSyncNtf.Parser)).Params["movement_latency_ms"]);
        Assert.Equal(25, (await Next(bob, WorldEffectsService.MovementConfig, MovementConfigSyncNtf.Parser)).Params["movement_latency_ms"]);

        await Gm("alice", "!forcefield 17 5 90 3 30");
        var added = await Next(alice, WorldEffectsService.ForceFields, ForceFieldNotify.Parser);
        var field = Assert.Single(added.ForceFields);
        Assert.Equal((uint)17, field.Uuid);
        Assert.NotNull(field.CustomField);
        Assert.Equal(5, field.CustomField.ForceRangeInfo.CircleRange.Radius);
        Assert.False(field.Remove);
        Assert.Single((await Next(bob, WorldEffectsService.ForceFields, ForceFieldNotify.Parser)).ForceFields);

        await using var charlie = await InWorld("Charlie");
        Assert.Equal(25, (await Next(charlie, WorldEffectsService.MovementConfig, MovementConfigSyncNtf.Parser)).Params["movement_latency_ms"]);
        var replay = await Next(charlie, WorldEffectsService.ForceFields, ForceFieldNotify.Parser);
        Assert.Equal((uint)17, Assert.Single(replay.ForceFields).Uuid);

        await Gm("alice", "!forcefield 17 5 90 3 30");
        var refusal = await Next(alice, GmCommands.BroadcastNotify, BroadCastNotify.Parser,
            p => p.Content.Contains("Usage: !forcefield", StringComparison.Ordinal));
        Assert.Contains("Usage:", refusal.Content);
        await Gm("alice", "!removeforcefield 17");
        Assert.True(Assert.Single((await Next(alice, WorldEffectsService.ForceFields, ForceFieldNotify.Parser,
            p => p.ForceFields.Count == 1 && p.ForceFields[0].Remove)).ForceFields).Remove);
        await Gm("alice", "!forcefield 18 5 90 3 1");
        Assert.Equal((uint)18, Assert.Single((await Next(alice, WorldEffectsService.ForceFields, ForceFieldNotify.Parser,
            p => p.ForceFields.Count == 1 && !p.ForceFields[0].Remove)).ForceFields).Uuid);
        Assert.Equal((uint)18, Assert.Single((await Next(alice, WorldEffectsService.ForceFields, ForceFieldNotify.Parser,
            p => p.ForceFields.Count == 1 && p.ForceFields[0].Remove)).ForceFields).Uuid);
    }

    [ClientDataFact]
    public async Task BossShoutAndTurningRequireVisibleLiveBoss()
    {
        await using var alice = await InWorld("Alice");
        var loop = _app.Services.GetRequiredService<GameLoop>();
        var monsters = _app.Services.GetRequiredService<MonsterService>();
        var effects = _app.Services.GetRequiredService<WorldEffectsService>();
        var sessions = _app.Services.GetRequiredService<SessionRegistry>();
        var player = sessions.All().Single(s => s.Account == "alice");
        var boss = await loop.InvokeAsync(() => monsters.SpawnSingleAsync(player.SceneEntry, 5020001,
            new MapPoint(player.Position.X + 2, player.Position.Y, player.Position.Z, 0)));
        Assert.NotNull(boss);
        var basePacket = await Next(alice, GameAoiSink.SyncAoiView, AoIViewChangeData.Parser,
            p => p.Bases.Any(b => b.Uuid == (ulong)boss.Uid));
        Assert.Equal((ulong)boss.Uid, Assert.Single(basePacket.Bases).Uuid);

        await Gm("alice", $"!bossshout {boss.Uid} 12035");
        var shout = await Next(alice, WorldEffectsService.BossShout, SceneBossShoutNotify.Parser);
        Assert.Equal(5020001, shout.MonsterId);
        Assert.Equal(12035, shout.ShoutId);
        await Gm("alice", $"!bossshout {boss.Uid} -1");
        await Next(alice, GmCommands.BroadcastNotify, BroadCastNotify.Parser,
            p => p.Content.Contains("Usage: !bossshout", StringComparison.Ordinal));

        Assert.True(await loop.InvokeAsync(() => effects.TurnAsync(boss, 120)));
        var turn = await Next(alice, WorldEffectsService.Turning, ContinueTurningChangeNtf.Parser);
        Assert.Equal(boss.Uid, turn.Uid);
        Assert.Equal(120, turn.TurningInfo.Face);
        Assert.False(await loop.InvokeAsync(() => effects.TurnAsync(boss, float.NaN)));
    }

    [ClientDataFact]
    public async Task LocalFaceIsSentOnlyToOwner()
    {
        await using var alice = await InWorld("Alice");
        await using var bob = await InWorld("Bob");
        await Gm("alice", "!worldface 135");
        Assert.Equal(135, (await Next(alice, WorldEffectsService.LocalFace, LocalPlayerFaceChangeNtf.Parser)).Face);
        var remote = await Next(bob, GameAoiSink.EntityFaceToAllId, EntityFaceToAll.Parser,
            p => p.EntityID == alice.Self!.Uuid);
        Assert.Equal(135, remote.Face);
        await Gm("alice", "!worldface NaN");
        await Next(alice, GmCommands.BroadcastNotify, BroadCastNotify.Parser,
            p => p.Content.Contains("Usage: !worldface", StringComparison.Ordinal));
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !Directory.Exists(Path.Combine(dir.FullName, "data", "config"))) dir = dir.Parent;
        return dir?.FullName ?? throw new DirectoryNotFoundException("repository data/config");
    }
}

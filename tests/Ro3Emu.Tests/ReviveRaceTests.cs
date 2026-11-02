using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Romsg;
using Ro3Emu.Bot;
using Ro3Emu.Server;
using Ro3Emu.Server.Game;

namespace Ro3Emu.Tests;

/// <summary>Revive requests cannot pass one another during an awaited reply or payment.</summary>
public sealed class ReviveRaceTests : IAsyncLifetime
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "ro3emu-revive-race-" + Guid.NewGuid().ToString("N"));
    private WebApplication _app = null!;
    private string _http = "";

    public async Task InitializeAsync()
    {
        var (http, micro, game) = TestPorts.FreePorts();
        _app = ServerHost.Create([], o =>
        {
            o.DataDir = _dir;
            o.GameDataDir = Path.Combine(RepoRoot(), "data");
            o.HttpPort = http;
            o.MicroGatePort = micro;
            o.GameGatePort = game;
        });
        await _app.StartAsync();
        _http = $"http://127.0.0.1:{http}";
    }

    public async Task DisposeAsync()
    {
        await _app.StopAsync();
        await _app.DisposeAsync();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }

    [ClientDataFact]
    public async Task HeldClaimRejectsWireRequestAndReplayRevivesOnlyOnce()
    {
        await using var bot = new BotClient(_http, File.ReadAllText(Path.Combine(_dir, "gate_rsa_public.pem")));
        await bot.LoginAsync("alice", "pw");
        await bot.EnterGateAsync();
        long id = (await bot.CreateRoleAsync("Alice")).RoleBaseInfo.Id;
        await bot.EnterWorldAsync(id);
        var session = _app.Services.GetRequiredService<SessionRegistry>().All().Single(s => s.Role?.Id == id);
        var combat = _app.Services.GetRequiredService<CombatService>();
        await _app.Services.GetRequiredService<GameLoop>().InvokeAsync(async () =>
        {
            session.Dead = true;
            await _app.Services.GetRequiredService<CharacterService>().SetHpSpAsync(session, 0, 0);
        });

        Assert.True(combat.TryClaimRevive(session));
        try
        {
            await bot.SendGameAsync(CombatService.Relive, new EntityReliveReq());
            var refused = await bot.Inbox.WaitAsync(e => e.IsGame(CombatService.Relive), TimeSpan.FromSeconds(8));
            Assert.NotEqual(0, EntityReliveAck.Parser.ParseFrom(refused.Body).Result);
            Assert.True(session.Dead);
            Assert.True(combat.CommitReviveClaim(session, 5000, 2500));
            long hp = _app.Services.GetRequiredService<CharacterService>().Stats(session)[2000];
            Assert.True(hp > 0);
            Assert.False(session.Dead);
            Assert.Contains(session.EntityProperties, property => property.Id == 2000 && property.Value == hp);
            Assert.False(combat.TryClaimRevive(session));
        }
        finally { combat.ReleaseReviveClaim(session); }

        await _app.Services.GetRequiredService<GameLoop>().InvokeAsync(async () =>
        {
            session.Dead = true;
            await _app.Services.GetRequiredService<CharacterService>().SetHpSpAsync(session, 0, 0);
        });
        await bot.SendGameAsync(CombatService.Relive, new EntityReliveReq());
        await bot.SendGameAsync(CombatService.Relive, new EntityReliveReq());
        var first = await bot.Inbox.WaitAsync(e => e.IsGame(CombatService.Relive), TimeSpan.FromSeconds(8));
        var second = await bot.Inbox.WaitAsync(e => e.IsGame(CombatService.Relive), TimeSpan.FromSeconds(8));
        Assert.Equal([0, ErrorCodes.TargetAlive], [.. new[]
        {
            EntityReliveAck.Parser.ParseFrom(first.Body).Result,
            EntityReliveAck.Parser.ParseFrom(second.Body).Result,
        }.OrderBy(x => x)]);
        await bot.Inbox.WaitAsync(e => e.IsGame(CombatService.ReviveInfo)
            && !SyncClientPlayerReviveInfoNtf.Parser.ParseFrom(e.Body).BDead, TimeSpan.FromSeconds(8));
        Assert.False(session.Dead);
        Assert.True(_app.Services.GetRequiredService<CharacterService>().Stats(session)[2000] > 0);
    }

    private static string RepoRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "Ro3Emu.slnx"))) return dir.FullName;
        throw new DirectoryNotFoundException("repository root");
    }
}

using System.Text.Json;
using Google.Protobuf;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Romsg;
using Ro3Emu.Bot;
using Ro3Emu.Server;
using Ro3Emu.Server.Accounts;
using Ro3Emu.Server.Game;

namespace Ro3Emu.Tests;

/// <summary>PLAYER_ASSET replays the exact client-safe full encodings before acknowledging reconnect.</summary>
public sealed class AssetReconnectTests : IAsyncLifetime
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "ro3-asset-reconnect-" + Guid.NewGuid().ToString("N"));
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(5);
    private WebApplication _app = null!;
    private string _http = "";

    public async Task InitializeAsync()
    {
        var (hp, mp, gp) = TestPorts.FreePorts();
        _app = ServerHost.Create([], o =>
        {
            o.DataDir = _directory;
            o.GameDataDir = Path.Combine(RepoRoot(), "data");
            o.HttpPort = hp;
            o.MicroGatePort = mp;
            o.GameGatePort = gp;
        });
        await _app.StartAsync();
        _http = $"http://127.0.0.1:{hp}";
    }

    public async Task DisposeAsync()
    {
        await _app.StopAsync();
        await _app.DisposeAsync();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_directory, true); } catch (IOException) { }
    }

    [ClientDataFact]
    public async Task EqualClientVersionsStillReplayEmptyClaimsAndRefineMapsBeforeAcknowledgment()
    {
        await using var bot = new BotClient(_http, File.ReadAllText(Path.Combine(_directory, "gate_rsa_public.pem")));
        await bot.LoginAsync("alice", "pw");
        await bot.EnterGateAsync();
        long id = (await bot.CreateRoleAsync("Alice")).RoleBaseInfo.Id;
        await bot.EnterWorldAsync(id);

        var session = _app.Services.GetRequiredService<SessionRegistry>().All().Single(s => s.Account == "alice" && s.Role != null);
        var roles = _app.Services.GetRequiredService<RoleStore>();
        Assert.Empty(session.Daily!.Claims);
        Assert.True(session.SeasonRefine!.EquipParts[1].RefineSlots[1].CanSelectedAttr.Count == 0);
        var dailyBefore = JsonSerializer.SerializeToUtf8Bytes(session.Daily);
        var refineBefore = session.SeasonRefine.ToByteArray();
        var savedDailyBefore = roles.LoadDaily(id);
        var savedRefineBefore = roles.LoadSeasonRefine(id);
        long claimVersion = session.SyncVersions[DailyActivityService.StageSync];
        long refineVersion = session.SyncVersions[SeasonRefineService.SeasonRefineSync];
        int start = bot.Inbox.Snapshot().Count;

        var request = new PlayerAssetReq { AssetType = { 12, 54 } };
        request.AssetVersions[12] = claimVersion;
        request.AssetVersions[54] = refineVersion;
        await bot.SendGameAsync(MsgId.PlayerAsset, request);
        var ackFrame = await bot.Inbox.WaitAsync(e => e.IsGame(MsgId.PlayerAsset), Wait);
        var ack = PlayerAssetResp.Parser.ParseFrom(ackFrame.Body);
        Assert.Equal(new long[] { 12, 54 }, ack.AssetType);

        var frames = bot.Inbox.Snapshot().Skip(start)
            .Where(e => e.Kind == BotEventKind.Game &&
                (e.MsgId == DailyActivityService.StageSync || e.MsgId == SeasonRefineService.SeasonRefineSync || e.MsgId == MsgId.PlayerAsset))
            .ToArray();
        Assert.Equal(new uint[] { DailyActivityService.StageSync, SeasonRefineService.SeasonRefineSync,
            SeasonRefineService.SeasonRefineSync, MsgId.PlayerAsset }, frames.Select(e => e.MsgId));

        var claims = DBStageRewardInfoIncrement.Parser.ParseFrom(frames[0].Body);
        Assert.True(claims.SyncAllMark);
        Assert.Empty(claims.AllStageRewardsFinished.All.Value);
        Assert.True(frames[0].Body.AsSpan().EndsWith(new byte[] { 0x52, 0x04, 0x0A, 0x02, 0x0A, 0x00 }));

        var seed = DBSeasonRefineIncrement.Parser.ParseFrom(frames[1].Body);
        var clear = DBSeasonRefineIncrement.Parser.ParseFrom(frames[2].Body);
        Assert.True(seed.SyncAllMark);
        Assert.Equal(0, seed.EquipParts[1].All.RefineSlots[1].CanSelectedAttr[0]);
        Assert.False(clear.SyncAllMark);
        Assert.True(clear.EquipParts[1].Inc.RefineSlots[1].Inc.CanSelectedAttr[0].Del);

        Assert.True(dailyBefore.SequenceEqual(JsonSerializer.SerializeToUtf8Bytes(session.Daily)));
        Assert.True(refineBefore.SequenceEqual(session.SeasonRefine.ToByteArray()));
        Assert.Equal(savedDailyBefore, roles.LoadDaily(id));
        Assert.Equal(savedRefineBefore, roles.LoadSeasonRefine(id));
        Assert.True(session.SyncVersions[DailyActivityService.StageSync] > claimVersion);
        Assert.True(session.SyncVersions[SeasonRefineService.SeasonRefineSync] > refineVersion);
    }

    private static string RepoRoot()
    {
        var p = new DirectoryInfo(AppContext.BaseDirectory);
        while (p != null && !Directory.Exists(Path.Combine(p.FullName, "data", "config"))) p = p.Parent;
        return p?.FullName ?? throw new DirectoryNotFoundException("data/config");
    }
}

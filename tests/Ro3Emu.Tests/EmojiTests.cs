using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Romsg;
using Ro3Emu.Bot;
using Ro3Emu.Server;
using Ro3Emu.Server.Game;

namespace Ro3Emu.Tests;

/// <summary>
/// Emotes and sitting (EmojiService): 5006 unlocks/favourites/idle, the two-phase state machine
/// on 31048 (17 Prepare, the client's ready START_PLAY, 14 Play, 13 end), what viewers see, and duo emotes.
/// </summary>
public sealed class EmojiTests : IAsyncLifetime
{
    private readonly string _dataDir = Path.Combine(Path.GetTempPath(), "ro3emu-emoji-" + Guid.NewGuid().ToString("N"));
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(10);
    private WebApplication _app = null!;
    private string _http = "";
    private const int Meditate = 10001, Duo = 20205, Sit = 50002, HandsOnHips = 50003;

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

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "Ro3Emu.slnx"))) dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("repo root not found");
    }

    private async Task<BotClient> InWorld(string name)
    {
        var bot = new BotClient(_http, File.ReadAllText(Path.Combine(_dataDir, "gate_rsa_public.pem")));
        await bot.LoginAsync(name.ToLowerInvariant(), "pw");
        await bot.EnterGateAsync();
        var role = await bot.CreateRoleAsync(name);
        await bot.EnterWorldAsync(role.RoleBaseInfo.Id);
        return bot;
    }

    private static async Task<T> Next<T>(BotClient bot, uint msgId, Google.Protobuf.MessageParser<T> parser, Func<T, bool>? match = null)
        where T : Google.Protobuf.IMessage<T>
    {
        var e = await bot.Inbox.WaitAsync(e => e.IsGame(msgId) && (match == null || match(parser.ParseFrom(e.Body))), Wait);
        return parser.ParseFrom(e.Body);
    }

    private static Task<AOIAttrChange> State(BotClient bot, ulong owner, ObjectState state) =>
        Next(bot, 31048, AOIAttrChange.Parser, a => a.EntityUuid == owner && a.BehaviorStateMachine?.CurState == state);

    private async Task Gm(string account, string line)
    {
        using var http = new HttpClient();
        (await http.PostAsync($"{_http}/ro3emu/gm?account={account}", Operator.Command(line))).EnsureSuccessStatusCode();
    }

    private static ulong Id(BotClient bot) => bot.Self!.Uuid;

    /// <summary>The server finished the role's scene load (an AOI frame can arrive before SceneLoaded is processed).</summary>
    private async Task LoadedAsync(string name)
    {
        var loop = _app.Services.GetRequiredService<GameLoop>();
        var registry = _app.Services.GetRequiredService<SessionRegistry>();
        var deadline = DateTime.UtcNow + Wait;
        while (!await loop.InvokeAsync(() => Task.FromResult(registry.All().Any(s => s.Role?.Name == name && !s.SceneLoading && s.Handoff == null))))
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException(name + " did not finish loading");
            await Task.Delay(20);
        }
    }

    /// <summary>START_PLAY, the server's 17 Prepare, the client's ready START_PLAY, then 14 Play.</summary>
    private static async Task<AOIAttrChange> PlayAsync(BotClient bot, int emojiId)
    {
        await bot.SendGameAsync(EmojiService.StartPlay, new EmojiStartPlayReq { EmojiId = emojiId });
        Assert.Equal(0, (await Next(bot, EmojiService.StartPlay, EmojiStartPlayRsp.Parser)).Result);
        var prepare = await State(bot, Id(bot), ObjectState.EObjectStateSmemojiPlayPrepare);
        Assert.Equal(emojiId, prepare.BehaviorStateMachine.Param.EmojiPlay.EmojiId);
        await bot.SendGameAsync(EmojiService.StartPlay, new EmojiStartPlayReq { EmojiId = emojiId });
        return await State(bot, Id(bot), ObjectState.EObjectStateSmemojiPlay);
    }

    [ClientDataFact]
    public async Task LoginSendsTheUnlockedEmotesAndLevelsUnlockMore()
    {
        await using var alice = await InWorld("Alice");
        var full = await Next(alice, EmojiService.Sync, DBEmojiPlayInfoIncrement.Parser, m => m.SyncAllMark);
        Assert.Equal([Meditate, 50001, Sit], full.EmojiUnlocks.All.Value.Order());
        Assert.False(full.HasIdleEmojiID && full.IdleEmojiID != 0);
        await Gm("alice", "!lvl 20");
        var more = await Next(alice, EmojiService.Sync, DBEmojiPlayInfoIncrement.Parser, m => !m.SyncAllMark && m.EmojiUnlocks != null);
        Assert.Contains(HandsOnHips, more.EmojiUnlocks.All.Value);
        Assert.Contains(50006, more.EmojiUnlocks.All.Value);
    }

    [ClientDataFact]
    public async Task SittingPlaysInTwoPhasesForTheOwnerAndViewersAndStopEndsIt()
    {
        await using var alice = await InWorld("Alice");
        await using var bob = await InWorld("Bob");
        await bob.Inbox.WaitAsync(e => e.IsGame(31037), Wait); // bob sees alice (same born point)
        long before = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var play = await PlayAsync(alice, Meditate);
        Assert.InRange(play.BehaviorStateMachine.StartStateTime, before, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        Assert.Empty(play.BehaviorStateMachine.Param.EmojiPlay.TargetPlayerIds);
        var seen = await State(bob, Id(alice), ObjectState.EObjectStateSmemojiPlay);
        Assert.Equal(Meditate, seen.BehaviorStateMachine.Param.EmojiPlay.EmojiId);

        await alice.SendGameAsync(EmojiService.StopPlay, new EmojiStopPlayReq());
        var end = await State(alice, Id(alice), ObjectState.EObjectStateSmidle);
        Assert.Equal(ObjectState.EObjectStateSmemojiPlay, end.BehaviorStateMachine.LastState);
        Assert.Equal(Meditate, end.BehaviorStateMachine.LastParam.EmojiPlay.EmojiId); // "Stopped meditating"
        await State(bob, Id(alice), ObjectState.EObjectStateSmidle);
    }

    [ClientDataFact]
    public async Task WalkingEndsAPlayingEmoteButNotAPreparedOne()
    {
        await using var alice = await InWorld("Alice");
        await alice.SendGameAsync(EmojiService.StartPlay, new EmojiStartPlayReq { EmojiId = Sit });
        await State(alice, Id(alice), ObjectState.EObjectStateSmemojiPlayPrepare);
        var pos = alice.Self!.MovementInfo.Pos;
        // The client syncs its position right before START_PLAY; a walk in Prepare keeps the emote.
        await alice.SendGameAsync(MsgId.Walk, new WalkReq { Pos = new Vec3 { X = pos.X + 0.5f, Y = pos.Y, Z = pos.Z }, SpeedRate = 1 });
        await alice.SendGameAsync(EmojiService.StartPlay, new EmojiStartPlayReq { EmojiId = Sit });
        await State(alice, Id(alice), ObjectState.EObjectStateSmemojiPlay);
        await alice.SendGameAsync(MsgId.Walk, new WalkReq { Pos = new Vec3 { X = pos.X + 1.5f, Y = pos.Y, Z = pos.Z }, SpeedRate = 1 });
        var end = await State(alice, Id(alice), ObjectState.EObjectStateSmidle);
        Assert.Equal(Sit, end.BehaviorStateMachine.LastParam.EmojiPlay.EmojiId);
    }

    [ClientDataFact]
    public async Task LockedUnknownAndTooFrequentEmotesAreRefused()
    {
        await using var alice = await InWorld("Alice");
        await alice.SendGameAsync(EmojiService.StartPlay, new EmojiStartPlayReq { EmojiId = HandsOnHips }); // level 20
        Assert.Equal(3602, (await Next(alice, EmojiService.StartPlay, EmojiStartPlayRsp.Parser)).Result);
        await alice.SendGameAsync(EmojiService.StartPlay, new EmojiStartPlayReq { EmojiId = 12345 });
        Assert.Equal(3601, (await Next(alice, EmojiService.StartPlay, EmojiStartPlayRsp.Parser)).Result);
        await alice.SendGameAsync(EmojiService.StartPlay, new EmojiStartPlayReq { EmojiId = Duo }); // duo needs an invite
        Assert.Equal(3601, (await Next(alice, EmojiService.StartPlay, EmojiStartPlayRsp.Parser)).Result);
        await alice.SendGameAsync(EmojiService.StartPlay, new EmojiStartPlayReq { EmojiId = Sit });
        Assert.Equal(0, (await Next(alice, EmojiService.StartPlay, EmojiStartPlayRsp.Parser)).Result);
        await alice.SendGameAsync(EmojiService.StartPlay, new EmojiStartPlayReq { EmojiId = Meditate }); // within 500 ms
        Assert.Equal(1211, (await Next(alice, EmojiService.StartPlay, EmojiStartPlayRsp.Parser)).Result);
    }

    [ClientDataFact]
    public async Task FavouritesAndTheIdleEmoteAreSavedSyncedAndShownToViewers()
    {
        await using var alice = await InWorld("Alice");
        await alice.SendGameAsync(EmojiService.AddCollection, new AddEmojiCollectionReq { EmojiId = Sit });
        Assert.Equal(0, (await Next(alice, EmojiService.AddCollection, AddEmojiCollectionRsp.Parser)).Result);
        var sync = await Next(alice, EmojiService.Sync, DBEmojiPlayInfoIncrement.Parser, m => m.EmojiCollections != null);
        Assert.Equal([Sit], sync.EmojiCollections.All.Value);
        await alice.SendGameAsync(EmojiService.AddCollection, new AddEmojiCollectionReq { EmojiId = HandsOnHips });
        Assert.Equal(3602, (await Next(alice, EmojiService.AddCollection, AddEmojiCollectionRsp.Parser)).Result);
        // Idle rows (_iSort 4) need the Nine-Tone Mantra appearance; a level row is not an idle emote.
        await alice.SendGameAsync(EmojiService.SetIdle, new SetIdleEmojiReq { EmojiId = Sit });
        Assert.Equal(3601, (await Next(alice, EmojiService.SetIdle, SetIdleEmojiRsp.Parser)).Result);
        await alice.SendGameAsync(EmojiService.SetIdle, new SetIdleEmojiReq { EmojiId = 40605 });
        Assert.Equal(3602, (await Next(alice, EmojiService.SetIdle, SetIdleEmojiRsp.Parser)).Result);
        await alice.SendGameAsync(EmojiService.DelCollection, new DelEmojiCollectionReq { EmojiId = Sit });
        Assert.Equal(0, (await Next(alice, EmojiService.DelCollection, DelEmojiCollectionRsp.Parser)).Result);
        var saved = _app.Services.GetRequiredService<Ro3Emu.Server.Accounts.RoleStore>().LoadEmojiPlay((long)Id(alice));
        Assert.NotNull(saved);
        Assert.Empty(DBEmojiPlayInfo.Parser.ParseFrom(saved).EmojiCollections);
    }

    [ClientDataFact]
    public async Task ADuoEmoteNeedsBothPartnersReadyAndEndsForBoth()
    {
        await using var alice = await InWorld("Alice");
        await using var bob = await InWorld("Bob");
        await LoadedAsync("Bob");
        // 20205 needs the Nine-Tone Mantra appearance (condition [1104,40050030]): light it for Alice.
        var loop = _app.Services.GetRequiredService<GameLoop>();
        var registry = _app.Services.GetRequiredService<SessionRegistry>();
        var emoji = _app.Services.GetRequiredService<EmojiService>();
        await loop.InvokeAsync(async () =>
        {
            var s = registry.All().Single(x => x.Role?.Name == "Alice");
            s.Wardrobe!.AllLightUpAppearances[40050030] = new DBAppearanceInfo { AppearanceId = 40050030 };
            await emoji.LoginAsync(s);
        });
        await alice.SendGameAsync(EmojiService.Invite, new EmojiInviteReq { Uuids = { Id(bob) }, EmojiId = Duo });
        var invited = await Next(alice, EmojiService.Invite, EmojiInviteRsp.Parser);
        Assert.Equal(0, invited.Result);
        Assert.Equal([Id(bob)], invited.InviteeId);
        Assert.InRange(invited.ExpireTime - DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), 25_000, 31_000);
        var ntf = await Next(bob, EmojiService.InviteNtf, EmojiInviteNtf.Parser);
        Assert.Equal((long)Id(alice), ntf.OwnerId);
        Assert.Equal("Alice", ntf.OwnerName);
        Assert.Equal(invited.ExpireTime, ntf.ExpireTime);
        // A second invite for the same emote while the first is pending is refused.
        await Task.Delay(600); // past PerformanceConfig._iPerformanceCD (500 ms)
        await alice.SendGameAsync(EmojiService.Invite, new EmojiInviteReq { Uuids = { Id(bob) }, EmojiId = Duo });
        Assert.Equal(3607, (await Next(alice, EmojiService.Invite, EmojiInviteRsp.Parser, r => r.Result != 0)).Result);

        await bob.SendGameAsync(EmojiService.Accept, new EmojiInviteAcceptReq { OwnerId = (long)Id(alice), EmojiId = Duo });
        Assert.Equal(0, (await Next(bob, EmojiService.Accept, EmojiInviteAcceptRsp.Parser)).Result);
        var response = await Next(alice, EmojiService.InviteResponseNtf, EmojiInviteResponseNtf.Parser);
        Assert.True(response.IsAccept);
        Assert.Equal((long)Id(bob), response.InviteeId);
        var alicePrep = await State(alice, Id(alice), ObjectState.EObjectStateSmemojiPlayPrepare);
        var bobPrep = await State(bob, Id(bob), ObjectState.EObjectStateSmemojiPlayPrepare);
        Assert.Equal(1, alicePrep.BehaviorStateMachine.Param.EmojiPlay.PlayIndex);
        Assert.Equal([(long)Id(bob)], alicePrep.BehaviorStateMachine.Param.EmojiPlay.TargetPlayerIds);
        Assert.Equal(2, bobPrep.BehaviorStateMachine.Param.EmojiPlay.PlayIndex);
        Assert.Equal([(long)Id(alice)], bobPrep.BehaviorStateMachine.Param.EmojiPlay.TargetPlayerIds);

        // Only Alice ready: nothing plays. Bob ready too: both play with the same start time.
        await alice.SendGameAsync(EmojiService.StartPlay, new EmojiStartPlayReq { EmojiId = Duo });
        await Next(alice, EmojiService.StartPlay, EmojiStartPlayRsp.Parser);
        Assert.False(await loop.InvokeAsync(() => Task.FromResult(registry.All().Single(x => x.Role?.Name == "Alice").Emote!.State
            == ObjectState.EObjectStateSmemojiPlay)));
        await bob.SendGameAsync(EmojiService.StartPlay, new EmojiStartPlayReq { EmojiId = Duo });
        var a = await State(alice, Id(alice), ObjectState.EObjectStateSmemojiPlay);
        var b = await State(alice, Id(bob), ObjectState.EObjectStateSmemojiPlay);
        Assert.Equal(a.BehaviorStateMachine.StartStateTime, b.BehaviorStateMachine.StartStateTime);

        // Bob stops: both end.
        await bob.SendGameAsync(EmojiService.StopPlay, new EmojiStopPlayReq());
        await State(alice, Id(alice), ObjectState.EObjectStateSmidle);
        await State(alice, Id(bob), ObjectState.EObjectStateSmidle);
    }

    [ClientDataFact]
    public async Task RefusingAnInviteTellsTheInviter()
    {
        await using var alice = await InWorld("Alice");
        await using var bob = await InWorld("Bob");
        await LoadedAsync("Bob");
        var loop = _app.Services.GetRequiredService<GameLoop>();
        var registry = _app.Services.GetRequiredService<SessionRegistry>();
        await loop.InvokeAsync(async () =>
        {
            var s = registry.All().Single(x => x.Role?.Name == "Alice");
            s.Wardrobe!.AllLightUpAppearances[40050030] = new DBAppearanceInfo { AppearanceId = 40050030 };
            await _app.Services.GetRequiredService<EmojiService>().LoginAsync(s);
        });
        await alice.SendGameAsync(EmojiService.Invite, new EmojiInviteReq { Uuids = { Id(bob) }, EmojiId = Duo });
        Assert.Equal(0, (await Next(alice, EmojiService.Invite, EmojiInviteRsp.Parser)).Result);
        await Next(bob, EmojiService.InviteNtf, EmojiInviteNtf.Parser);
        await bob.SendGameAsync(EmojiService.Refuse, new EmojiInviteRefuseReq { OwnerId = (long)Id(alice), EmojiId = Duo });
        Assert.Equal(0, (await Next(bob, EmojiService.Refuse, EmojiInviteRefuseRsp.Parser)).Result);
        var response = await Next(alice, EmojiService.InviteResponseNtf, EmojiInviteResponseNtf.Parser);
        Assert.False(response.IsAccept);
        Assert.Equal(0, response.ErrorCode);
        // The invite is gone: a late accept finds nothing.
        await bob.SendGameAsync(EmojiService.Accept, new EmojiInviteAcceptReq { OwnerId = (long)Id(alice), EmojiId = Duo });
        Assert.Equal(3608, (await Next(bob, EmojiService.Accept, EmojiInviteAcceptRsp.Parser)).Result);
    }

    [ClientDataFact]
    public async Task MeditatingInThePlazaRegionCompletesTheAreaActionQuest()
    {
        await using var alice = await InWorld("Alice");
        await Gm("alice", "!quest accept 500211");
        // Region 12 of Prontera (10100): centre (156.17, 180.26), 10 x 10.
        await Gm("alice", "!warp 10100 156 180");
        await alice.Inbox.WaitAsync(e => e.IsGame(MsgId.SwitchSceneLine), Wait);
        await alice.SendGameAsync(MsgId.SceneLoaded, new SceneLoadedReq { SceneId = 10100 });
        await alice.Inbox.WaitAsync(e => e.IsGame(MsgId.EntitySelf), Wait);
        var loop = _app.Services.GetRequiredService<GameLoop>();
        var registry = _app.Services.GetRequiredService<SessionRegistry>();
        Task<long> Progress() => loop.InvokeAsync(() => Task.FromResult(
            registry.All().Single(x => x.Role?.Name == "Alice").Tasks is { } t && t.Infos.TryGetValue(500211, out var info)
                ? (info.Status == TaskState.Running ? info.CompleteNum.FirstOrDefault() : 1) : -1));
        Assert.Equal(0, await Progress()); // accepted, not done
        // Another emote in the region does not count; meditating there does.
        await PlayAsync(alice, Sit);
        Assert.Equal(0, await Progress());
        await alice.SendGameAsync(EmojiService.StopPlay, new EmojiStopPlayReq());
        await State(alice, Id(alice), ObjectState.EObjectStateSmidle);
        await Task.Delay(600); // PerformanceConfig._iPerformanceCD 500 ms
        await PlayAsync(alice, Meditate);
        var deadline = DateTime.UtcNow + Wait;
        while (await Progress() < 1 && DateTime.UtcNow < deadline) await Task.Delay(50);
        Assert.Equal(1, await Progress());
    }
}

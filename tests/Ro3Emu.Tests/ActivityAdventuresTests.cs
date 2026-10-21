using Microsoft.AspNetCore.Builder;
using Google.Protobuf;
using Microsoft.Extensions.DependencyInjection;
using System.Globalization;
using System.Text.Json;
using Romsg;
using Ro3Emu.Bot;
using Ro3Emu.Server;
using Ro3Emu.Server.Accounts;
using Ro3Emu.Server.Game;
using Ro3Emu.Server.Game.Ownership;

namespace Ro3Emu.Tests;

/// <summary>Real game-gate NewActivity adventure requests, persistence and replay boundaries.</summary>
public sealed class ActivityAdventuresTests : IAsyncLifetime
{
    private readonly string _dataDir = Path.Combine(Path.GetTempPath(), "ro3emu-adventure-" + Guid.NewGuid().ToString("N"));
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(5);
    private WebApplication _app = null!;
    private string _http = "";
    private readonly Dictionary<string, BotClient> _clients = new();
    private readonly FixedClock _clock = new();

    private sealed class FixedClock : TimeProvider
    {
        private long _seconds = new DateTimeOffset(2026, 10, 8, 12, 0, 0, TimeSpan.Zero).ToUnixTimeSeconds();
        public override DateTimeOffset GetUtcNow() => DateTimeOffset.FromUnixTimeSeconds(Interlocked.Read(ref _seconds));
        public void Set(long value) => Interlocked.Exchange(ref _seconds, value);
    }

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
            o.PlayBook = 101;
            o.SeasonStart = "2026-10-05";
        }, _clock);
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

    private async Task<BotClient> InWorld(string name, bool create = true)
    {
        var bot = new BotClient(_http, File.ReadAllText(Path.Combine(_dataDir, "gate_rsa_public.pem")));
        await bot.LoginAsync(name.ToLowerInvariant(), "pw");
        await bot.EnterGateAsync();
        long id = create ? (await bot.CreateRoleAsync(name)).RoleBaseInfo.Id : bot.Roles.Single().Id;
        await bot.EnterWorldAsync(id);
        _clients[name.ToLowerInvariant()] = bot;
        return bot;
    }

    private static async Task<T> Next<T>(BotClient bot, uint msgId, Google.Protobuf.MessageParser<T> parser, Func<T, bool>? match = null)
        where T : Google.Protobuf.IMessage<T>
    {
        var packet = await bot.Inbox.WaitAsync(e => e.IsGame(msgId) && (match == null || match(parser.ParseFrom(e.Body))), Wait);
        return parser.ParseFrom(packet.Body);
    }

    private async Task Gm(string account, string line)
    {
        using var client = new HttpClient();
        var response = await client.PostAsync($"{_http}/ro3emu/gm?account={account}", new StringContent(line));
        response.EnsureSuccessStatusCode();
        if (line.StartsWith("!warp ", StringComparison.Ordinal))
        {
            // A real client completes each handoff before issuing gameplay or requesting another map.
            var bot = _clients[account];
            var change = await Next(bot, MsgId.SwitchSceneLine, SwitchSceneLineResp.Parser);
            await bot.SendGameAsync(MsgId.SceneLoaded, new SceneLoadedReq { SceneId = change.SceneEntry });
            Assert.Equal(0, (await Next(bot, MsgId.SceneLoaded, SceneLoadedResp.Parser)).Result);
            await Next(bot, MsgId.EntitySelf, EntitySelf.Parser);
        }
    }

    [ClientDataFact]
    public async Task SeasonEveCluesAndCompletedChaptersContributeToOneDurableGlobalCycle()
    {
        var window = _app.Services.GetRequiredService<ActivityClock>()
            .Segments(1011, _clock.GetUtcNow().ToUnixTimeSeconds()).Single();
        _clock.Set(window.Start + 60);
        var alice = await InWorld("Alice");
        var bob = await InWorld("Bob");
        await Gm("alice", "!lvl 99");
        await Gm("bob", "!lvl 99");
        var scene = _app.Services.GetRequiredService<SceneService>();
        long requestId = 600;

        async Task<CommonActivityRes> Request(BotClient bot, Action<CommonActivityReq> fill)
        {
            var req = new CommonActivityReq { ActivityId = 117, RequestId = ++requestId };
            fill(req);
            await bot.SendGameAsync(NewActivityService.Common, req);
            return await Next(bot, NewActivityService.Common, CommonActivityRes.Parser, r => r.RequestId == req.RequestId);
        }

        async Task<SeasonEveGetGlobalInfoResp> Global(BotClient bot)
        {
            var response = await Request(bot, r => r.SeasonEveGetInfo = new SeasonEveGetGlobalInfoReq());
            Assert.Equal(0, response.Result);
            return response.SeasonEveGetInfo;
        }

        var initial = await Global(alice);
        Assert.InRange(initial.Progress, 1, 500);
        foreach (long forged in new long[] { 103, 104 })
        {
            var wrongInfo = new CommonActivityReq
            { ActivityId = forged, RequestId = ++requestId, SeasonEveGetInfo = new SeasonEveGetGlobalInfoReq() };
            await alice.SendGameAsync(NewActivityService.Common, wrongInfo);
            Assert.NotEqual(0, (await Next(alice, NewActivityService.Common, CommonActivityRes.Parser,
                r => r.RequestId == wrongInfo.RequestId)).Result);
            var wrongClue = new CommonActivityReq
            { ActivityId = forged, RequestId = ++requestId, SeasonEveFindClue = new SeasonEveFindClueReq { ClueId = 1 } };
            await alice.SendGameAsync(NewActivityService.Common, wrongClue);
            Assert.NotEqual(0, (await Next(alice, NewActivityService.Common, CommonActivityRes.Parser,
                r => r.RequestId == wrongClue.RequestId)).Result);
        }
        Assert.Equal(window.Start, initial.ChapterInfo[1].OpenTime);
        Assert.Equal(window.Start + 86400, initial.ChapterInfo[2].OpenTime);
        Assert.Equal(window.Start + 259200, initial.ChapterInfo[4].OpenTime);
        await Gm("alice", "!warp 10500");
        var earlyPoint = scene.ConfiguredPoint(10500, 545);
        Assert.NotNull(earlyPoint);
        await Gm("alice", $"!jump {earlyPoint.Value.X.ToString(CultureInfo.InvariantCulture)} {earlyPoint.Value.Z.ToString(CultureInfo.InvariantCulture)}");
        var early = await Request(alice, r => r.SeasonEveFindClue = new SeasonEveFindClueReq { ClueId = 8 });
        Assert.Equal(ErrorCodes.TaskRequirements, early.Result);

        async Task FindFirstThree(BotClient bot, string account)
        {
            await Gm(account, "!warp 10100");
            for (int clue = 1; clue <= 3; clue++)
            {
                var point = scene.ConfiguredPoint(10100, clue switch { 1 => 535, 2 => 597, _ => 873 });
                Assert.NotNull(point);
                await Gm(account, $"!jump {point.Value.X.ToString(CultureInfo.InvariantCulture)} {point.Value.Z.ToString(CultureInfo.InvariantCulture)}");
                var response = await Request(bot, r => r.SeasonEveFindClue = new SeasonEveFindClueReq { ClueId = clue });
                Assert.Equal(0, response.Result);
                Assert.True(response.SeasonEveFindClue.IsFirst);
                Assert.Equal(clue, response.SeasonEveFindClue.Index);
                Assert.Equal(clue, response.SeasonEveFindClue.NoteId);
            }
            var duplicate = await Request(bot, r => r.SeasonEveFindClue = new SeasonEveFindClueReq { ClueId = 3 });
            Assert.Equal(0, duplicate.Result);
            Assert.False(duplicate.SeasonEveFindClue.IsFirst);
            Assert.Equal(3, duplicate.SeasonEveFindClue.Index);
        }

        async Task<CommonActivityRes> Play(BotClient bot, bool correct)
        {
            var play = new SeasonEvePlayChapterReq { ChapterId = 1 };
            for (int index = 1; index <= 7; index++) play.NoteIds[index] = 1 + (index - 1) % 4;
            if (!correct) play.NoteIds[1] = 2;
            return await Request(bot, r => r.SeasonEvePlayChapter = play);
        }

        await FindFirstThree(alice, "alice");
        Assert.Equal(1, (await Global(bob)).ChapterInfo[1].Clues[1]);
        Assert.NotEqual(0, (await Play(alice, false)).Result);
        Assert.Equal(initial.Progress, (await Global(alice)).Progress);
        Assert.Equal(0, (await Play(alice, true)).Result);
        Assert.Equal(initial.Progress + 500, (await Global(bob)).Progress);
        Assert.NotEqual(0, (await Play(alice, true)).Result);

        await FindFirstThree(bob, "bob");
        Assert.Equal(0, (await Play(bob, true)).Result);
        var shared = await Global(alice);
        Assert.Equal(initial.Progress + 1000, shared.Progress);
        Assert.Equal(2, shared.ChapterInfo[1].Clues[1]);
        Assert.Equal(2, shared.BulletInfo.Count);
        Assert.Equal(shared.Progress, (await Global(bob)).Progress);
        Assert.NotEqual(0, (await Request(alice, r => r.SeasonEveClaimAward = new SeasonEveClaimAwardReq
        { ClaimId = { [1] = true } })).Result);
        Assert.NotEqual(0, (await Request(alice, r => r.SeasonEveSelectWish = new SeasonEveSelectWishReq
        { WishId = 1 })).Result);

        _clock.Set(window.Start + 1800);
        long paced = (await Global(alice)).Progress;
        Assert.True(paced > shared.Progress);
        Assert.Equal(paced, (await Global(bob)).Progress);
        await alice.DisposeAsync();
        await using var relog = await InWorld("Alice", create: false);
        var restored = await Global(relog);
        Assert.Equal(paced, restored.Progress);
        Assert.Equal(2, restored.ChapterInfo[1].Clues[1]);
        Assert.Equal(2, restored.BulletInfo.Count);
        Assert.NotEqual(0, (await Play(relog, true)).Result);
        _clock.Set(window.End - 3600);
        Assert.Equal(1_000_000, (await Global(relog)).Progress);
        Assert.Equal(1_000_000, (await Global(bob)).Progress);
        Assert.Equal(0, (await Request(relog, r => r.SeasonEveClaimAward = new SeasonEveClaimAwardReq
        { ClaimId = { [1] = true } })).Result);
        Assert.NotEqual(0, (await Request(relog, r => r.SeasonEveClaimAward = new SeasonEveClaimAwardReq
        { ClaimId = { [1] = true } })).Result);
        Assert.Equal(0, (await Request(relog, r => r.SeasonEveSelectWish = new SeasonEveSelectWishReq
        { WishId = 1 })).Result);
        Assert.NotEqual(0, (await Request(relog, r => r.SeasonEveSelectWish = new SeasonEveSelectWishReq
        { WishId = 2 })).Result);

        var registry = _app.Services.GetRequiredService<SessionRegistry>();
        var roles = _app.Services.GetRequiredService<Ro3Emu.Server.Accounts.RoleStore>();
        long aliceId = registry.All().Single(s => s.Role?.Name == "Alice").Role!.Id;
        long bobId = registry.All().Single(s => s.Role?.Name == "Bob").Role!.Id;
        await bob.DisposeAsync();
        _clock.Set(window.End + 1);
        var activities = _app.Services.GetRequiredService<NewActivityService>();
        // The saved Eve cycle settles from its deadline without a client request.
        MailService.Mailbox Box(long id) => System.Text.Json.JsonSerializer.Deserialize<MailService.Mailbox>(roles.LoadMailbox(id)!)!;
        for (int i = 0; i < 120 && (roles.LoadMailbox(aliceId) is not { } bytes
            || System.Text.Json.JsonSerializer.Deserialize<MailService.Mailbox>(bytes)!.Letters.Count < 2); i++)
            await Task.Delay(25);
        Assert.Equal(2, Box(aliceId).Letters.Count);
        // A later explicit reconciliation must not duplicate the durable marker or reward.
        await activities.ReconcileAsync(registry.All().Single(s => s.Role?.Id == aliceId));
        await using var bobRelog = await InWorld("Bob", create: false);

        var aliceMail = Box(aliceId).Letters;
        var bobMail = Box(bobId).Letters;
        Assert.Equal(2, aliceMail.Count);
        Assert.Equal(2, bobMail.Count);
        Assert.Contains(aliceMail, m => m.TemplateId == 11450
            && m.Attachments.Any(a => a.Entry == 1254013 && a.Count == 2));
        Assert.Contains(bobMail, m => m.TemplateId == 11450
            && m.Attachments.Any(a => a.Entry == 3003103 && a.Count == 1));
        Assert.Contains(aliceMail, m => m.TemplateId == 11451
            && m.Attachments.Any(a => a.Entry == 2 && a.Count == 9000));
        Assert.Contains(bobMail, m => m.TemplateId == 11451
            && m.Attachments.Any(a => a.Entry == 2 && a.Count == 10000));
        // Alice claimed the first 1,000-point milestone before expiry; Bob claimed none.
        var wishLetter = aliceMail.Single(m => m.TemplateId == 11450);
        await relog.SendGameAsync(MailService.ClaimMail, new ClaimMailReq { MailUid = { wishLetter.Id } });
        Assert.Equal(0, (await Next(relog, MailService.ClaimMail, ClaimMailRsp.Parser)).Result);
        Assert.Contains(registry.All().Single(s => s.Role?.Id == aliceId).Items, i => i.Entry == 1254013);
        await relog.SendGameAsync(MailService.ClaimMail, new ClaimMailReq { MailUid = { wishLetter.Id } });
        Assert.NotEqual(0, (await Next(relog, MailService.ClaimMail, ClaimMailRsp.Parser)).Result);
        await activities.ReconcileAsync(registry.All().Single(s => s.Role?.Id == bobId));
        Assert.Equal(2, Box(aliceId).Letters.Count);
        Assert.Equal(2, Box(bobId).Letters.Count);
    }

    [ClientDataFact]
    public async Task ExplorationFillConsumesTheConfiguredNoteOnceAndPersistsAcrossReconnect()
    {
        var season = _app.Services.GetRequiredService<SeasonService>();
        var gameData = _app.Services.GetRequiredService<Ro3Emu.Server.Data.GameData>();
        long phaseStart = new SeasonClock(gameData, season.Start, 101, season.Zone).Build().StageTimeInfo[101002].StartTime;
        _clock.Set(phaseStart + 60);
        var alice = await InWorld("Alice");
        var initial = await Next(alice, NewActivityService.ActivitySync, DBNewActivityInfoIncrement.Parser);
        Assert.True(initial.SyncAllMark);
        Assert.Contains(111L, initial.ActivityInfo.Keys);

        await Gm("alice", "!lvl 99");
        await Gm("alice", "!item 1113001 2");
        await Gm("alice", "!item 1113002 1");
        await Gm("alice", "!item 1113005 1");
        var session = _app.Services.GetRequiredService<SessionRegistry>().All().Single(s => s.Role?.Name == "Alice");
        // ExplorePhaseConfig 101 contains node 1011; its first _kPiecesID is item 1113001.
        var fill = new CommonActivityReq
        {
            ActivityId = 111, RequestId = 1,
            FillPhaseNodeReq = new WorldExplorationFillPhaseNodeReq { PhaseCfgId = 101, NodeCfgId = 1011, ItemId = 1113001, Position = 1 },
        };
        await alice.SendGameAsync(NewActivityService.Common, fill);
        var success = await Next(alice, NewActivityService.Common, CommonActivityRes.Parser, r => r.RequestId == 1);
        Assert.Equal(0, success.Result);
        Assert.Equal(0, success.FillPhaseNodeRes.Err);
        await Next(alice, NewActivityService.WorldExplorationSync, DBWorldExplorationIncrement.Parser,
            m => m.PhaseInfos.ContainsKey(101));
        Assert.Equal(555, session.NewActivities!.ActivityInfo[111].ActivityParam.WorldExploration.PhaseInfos[101].TotalRate);

        fill.RequestId = 2;
        fill.FillPhaseNodeReq.Position = 2;
        fill.FillPhaseNodeReq.ItemId = 1113002;
        await alice.SendGameAsync(NewActivityService.Common, fill);
        Assert.Equal(0, (await Next(alice, NewActivityService.Common, CommonActivityRes.Parser, r => r.RequestId == 2)).Result);
        Assert.Equal(1111, session.NewActivities.ActivityInfo[111].ActivityParam.WorldExploration.PhaseInfos[101].TotalRate);

        fill.RequestId = 3;
        await alice.SendGameAsync(NewActivityService.Common, fill);
        Assert.NotEqual(0, (await Next(alice, NewActivityService.Common, CommonActivityRes.Parser, r => r.RequestId == 3)).Result);

        fill.RequestId = 4;
        fill.FillPhaseNodeReq.PhaseCfgId = 102;
        fill.FillPhaseNodeReq.NodeCfgId = 1021;
        fill.FillPhaseNodeReq.Position = 1;
        fill.FillPhaseNodeReq.ItemId = 1113005;
        await alice.SendGameAsync(NewActivityService.Common, fill);
        Assert.NotEqual(0, (await Next(alice, NewActivityService.Common, CommonActivityRes.Parser, r => r.RequestId == 4)).Result);
        Assert.Equal(1, session.Items.Where(i => i.Entry == 1113005).Sum(i => i.Stack));
        _clock.Set(phaseStart + 172800 + 60);
        fill.RequestId = 5;
        await alice.SendGameAsync(NewActivityService.Common, fill);
        Assert.Equal(0, (await Next(alice, NewActivityService.Common, CommonActivityRes.Parser, r => r.RequestId == 5)).Result);
        Assert.Equal(0, session.Items.Where(i => i.Entry == 1113005).Sum(i => i.Stack));

        await alice.DisposeAsync();
        await using var relog = await InWorld("Alice", create: false);
        var saved = await Next(relog, NewActivityService.ActivitySync, DBNewActivityInfoIncrement.Parser);
        Assert.Contains(111L, saved.ActivityInfo.Keys);
        fill.RequestId = 6;
        fill.FillPhaseNodeReq.PhaseCfgId = 101;
        fill.FillPhaseNodeReq.NodeCfgId = 1011;
        fill.FillPhaseNodeReq.Position = 1;
        fill.FillPhaseNodeReq.ItemId = 1113001;
        await relog.SendGameAsync(NewActivityService.Common, fill);
        Assert.NotEqual(0, (await Next(relog, NewActivityService.Common, CommonActivityRes.Parser, r => r.RequestId == 6)).Result);
    }

    [ClientDataFact]
    public async Task ClosedPreAndInvalidExplorationCoordinatesAreRefused()
    {
        await using var alice = await InWorld("Alice");
        await Gm("alice", "!lvl 99");
        await alice.SendGameAsync(82960, new NewActivityPreSubmitItemReq { ActivityID = 102, Num = 1 });
        Assert.NotEqual(0, (await Next(alice, 82960, NewActivityPreSubmitItemResp.Parser)).Result);

        await alice.SendGameAsync(NewActivityService.Common, new CommonActivityReq
        {
            ActivityId = 111, RequestId = 5,
            FillPhaseNodeReq = new WorldExplorationFillPhaseNodeReq { PhaseCfgId = 101, NodeCfgId = 1011, ItemId = 1113002, Position = 1 },
        });
        Assert.NotEqual(0, (await Next(alice, NewActivityService.Common, CommonActivityRes.Parser, r => r.RequestId == 5)).Result);
    }

    [ClientDataFact]
    public async Task JourneyDialogueBranchesUseOwnedAttributesAndCompleteFirstNode()
    {
        var window = _app.Services.GetRequiredService<ActivityClock>()
            .Segments(1037, _clock.GetUtcNow().ToUnixTimeSeconds()).Single();
        _clock.Set(window.Start + 60);
        await using var alice = await InWorld("Alice");
        var registry = _app.Services.GetRequiredService<SessionRegistry>();
        var session = registry.All().Single(s => s.Role?.Name == "Alice");
        // NewActivityConfig 112 requires base level 999. This controlled fixture satisfies only
        // that authored eligibility gate; Journey acts remain server-owned and are advanced over 82976.
        await _app.Services.GetRequiredService<GameLoop>().InvokeAsync(() =>
            _app.Services.GetRequiredService<OwnerRegistry>().RunSessionAsync("journey level fixture",
                [ReservationKey.Role(session.Role!.Id)], session, () =>
                {
                    session.Progress!.Level = 999;
                    return Task.CompletedTask;
                })).WaitAsync(Wait);
        await _app.Services.GetRequiredService<NewActivityService>().ReconcileAsync(session);

        async Task<CommonActivityRes> Complete(long act, EJourneyCompleteType type, long requestId)
        {
            await alice.SendGameAsync(NewActivityService.Common, new CommonActivityReq
            {
                ActivityId = 112, RequestId = requestId,
                JourneyComplete = new JourneyCompleteReq { CompleteId = act, Type = type },
            });
            return await Next(alice, NewActivityService.Common, CommonActivityRes.Parser, r => r.RequestId == requestId);
        }

        Assert.NotEqual(0, (await Complete(10102, EJourneyCompleteType.Act, 100)).Result);
        Assert.Equal(0, (await Complete(10101, EJourneyCompleteType.Act, 101)).Result);
        Assert.True(session.NewActivities!.ActivityInfo[112].ActivityParam.Journey.CurActIds.ContainsKey(10102));
        Assert.NotEqual(0, (await Complete(10103, EJourneyCompleteType.Act, 102)).Result);
        Assert.Equal(0, (await Complete(10102, EJourneyCompleteType.Act, 103)).Result);
        var changed = await Next(alice, NewActivityService.Notify, CommonActivityNotify.Parser,
            n => n.ActivityId == 112 && n.JourneyAttrChange?.CompleteId == 10102);
        Assert.Equal(EJourneyCompleteType.Act, changed.JourneyAttrChange.Type);
        Assert.Equal(1, changed.JourneyAttrChange.AttrChange[1]);
        var afterChoice = session.NewActivities!.ActivityInfo[112].ActivityParam.Journey;
        Assert.False(afterChoice.CurActIds.ContainsKey(10103));
        Assert.True(afterChoice.CurActIds.ContainsKey(10104));
        Assert.Equal(101, afterChoice.Attributes[1]);
        Assert.Equal(1, afterChoice.GainCoinCount);
        Assert.Equal(0, (await Complete(10104, EJourneyCompleteType.Act, 104)).Result);
        Assert.Equal(0, (await Complete(101, EJourneyCompleteType.Node, 105)).Result);
        Assert.True(session.NewActivities!.ActivityInfo[112].ActivityParam.Journey.CompleteNode.ContainsKey(101));
        Assert.NotEqual(0, (await Complete(101, EJourneyCompleteType.Node, 106)).Result);
    }

    [ClientDataFact]
    public async Task JourneyTravelConsumesHoursAndResolvesAuthoredEventBranchesBeforeNextDay()
    {
        var window = _app.Services.GetRequiredService<ActivityClock>()
            .Segments(1037, _clock.GetUtcNow().ToUnixTimeSeconds()).Single();
        _clock.Set(window.Start + 60);
        await using var alice = await InWorld("Alice");
        var session = _app.Services.GetRequiredService<SessionRegistry>().All().Single(s => s.Role?.Name == "Alice");
        await _app.Services.GetRequiredService<GameLoop>().InvokeAsync(() =>
            _app.Services.GetRequiredService<OwnerRegistry>().RunSessionAsync("journey travel level fixture",
                [ReservationKey.Role(session.Role!.Id)], session, () =>
                {
                    session.Progress!.Level = 999;
                    return Task.CompletedTask;
                })).WaitAsync(Wait);
        await _app.Services.GetRequiredService<NewActivityService>().ReconcileAsync(session);
        long sequence = 400;
        async Task<int> Complete(long id, EJourneyCompleteType type)
        {
            long requestId = ++sequence;
            await alice.SendGameAsync(NewActivityService.Common, new CommonActivityReq
            {
                ActivityId = 112, RequestId = requestId,
                JourneyComplete = new JourneyCompleteReq { CompleteId = id, Type = type },
            });
            return (await Next(alice, NewActivityService.Common, CommonActivityRes.Parser, r => r.RequestId == requestId)).Result;
        }
        DBJourneyInfo State() => session.NewActivities!.ActivityInfo[112].ActivityParam.Journey;
        async Task ResolveEvent()
        {
            var config = _app.Services.GetRequiredService<Ro3Emu.Server.Data.GameData>();
            while (State().EventInfo.EventId is var current and > 0)
            {
                var row = config.Row("JourneyEventConfig", current)!.Value;
                long choice = row.TryGetProperty("_kBtn", out var options)
                    && options.ValueKind == System.Text.Json.JsonValueKind.Array && options.GetArrayLength() > 0
                        ? options[0].GetInt64() : current;
                Assert.NotEqual(0, (await Complete(999999, EJourneyCompleteType.Event)));
                Assert.Equal(current, State().EventInfo.EventId);
                Assert.Equal(0, await Complete(choice, EJourneyCompleteType.Event));
            }
        }

        Assert.NotEqual(0, await Complete(1, EJourneyCompleteType.Travel));
        Assert.Equal(0, await Complete(10101, EJourneyCompleteType.Act));
        Assert.Equal(0, await Complete(10102, EJourneyCompleteType.Act));
        Assert.Equal(0, await Complete(10104, EJourneyCompleteType.Act));
        Assert.Equal(0, await Complete(101, EJourneyCompleteType.Node));
        Assert.Equal(0, await Complete(10113, EJourneyCompleteType.Act));
        Assert.NotEqual(0, await Complete(10997, EJourneyCompleteType.Act));
        Assert.NotEqual(0, await Complete(2, EJourneyCompleteType.Game));
        Assert.Equal(0, await Complete(1, EJourneyCompleteType.Game));
        Assert.Equal(0, await Complete(2, EJourneyCompleteType.Game));
        Assert.Equal(0, await Complete(3, EJourneyCompleteType.Game));
        Assert.Equal(0, await Complete(11000, EJourneyCompleteType.Act));
        Assert.Equal(0, await Complete(106, EJourneyCompleteType.Node));
        Assert.Equal(0, await Complete(10110, EJourneyCompleteType.Act));
        Assert.True(State().CurActIds.ContainsKey(10995));
        Assert.Equal(1, State().TravelInfo.CurDay);
        Assert.Equal(5, State().TravelInfo.RemainHour);
        Assert.Equal(0, await Complete(1, EJourneyCompleteType.Travel));
        Assert.Equal(4, State().TravelInfo.RemainHour);
        Assert.Equal(10101, State().EventInfo.EventId);
        Assert.Equal(-1, State().EventInfo.AttrChange[1]);
        Assert.NotEqual(0, await Complete(2, EJourneyCompleteType.Travel));
        Assert.NotEqual(0, await Complete(0, EJourneyCompleteType.TravelDay));
        await ResolveEvent();
        Assert.Equal(0, State().EventInfo.EventId);
        Assert.Equal(0, await Complete(1, EJourneyCompleteType.Travel));
        Assert.Equal(2, State().CompleteTravel[1].Count);
        await ResolveEvent();
        for (int i = 0; i < 3; i++)
        {
            Assert.Equal(0, await Complete(1, EJourneyCompleteType.Travel));
            await ResolveEvent();
        }
        Assert.Equal(0, State().TravelInfo.RemainHour);
        Assert.NotEqual(0, await Complete(1, EJourneyCompleteType.Travel));
        for (int day = 1; day <= 7; day++)
            Assert.Equal(0, await Complete(0, EJourneyCompleteType.TravelDay));
        Assert.Equal(8, State().TravelInfo.CurDay);
        Assert.True(State().CompleteAct.ContainsKey(10995));
        Assert.True(State().CurActIds.ContainsKey(10996));
        Assert.NotEqual(0, await Complete(0, EJourneyCompleteType.TravelDay));
        Assert.NotEqual(0, await Complete(1, EJourneyCompleteType.Travel));
    }

    [ClientDataTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DungeonJourneyContinuationRecoversItsExactActAndAcknowledgesWithTheAward(bool expiresBeforeRecovery)
    {
        var window = _app.Services.GetRequiredService<ActivityClock>()
            .Segments(1037, _clock.GetUtcNow().ToUnixTimeSeconds()).Single();
        _clock.Set(window.Start + 60);
        await using var bot = await InWorld("Alice");
        var session = _app.Services.GetRequiredService<SessionRegistry>().All().Single(s => s.Role?.Name == "Alice");
        var loop = _app.Services.GetRequiredService<GameLoop>();
        var roles = _app.Services.GetRequiredService<RoleStore>();
        var accounts = _app.Services.GetRequiredService<AccountStore>();
        var dungeons = _app.Services.GetRequiredService<DungeonService>();
        const string source = "journey/dungeon/test/41/1";
        Exception? recoveryFailure = null;
        var continuation = dungeons.JourneyWon!;
        dungeons.JourneyWon = async (recipient, victory, persist, apply) =>
        {
            try { return await continuation(recipient, victory, persist, apply); }
            catch (Exception e) { recoveryFailure = e; throw; }
        };
        await loop.InvokeAsync(() => _app.Services.GetRequiredService<OwnerRegistry>().RunSessionAsync(
            "journey continuation fixture", [ReservationKey.Realm], session, async () =>
        {
            session.Progress!.Level = 999; // The authored Journey gate, as in the other Journey fixtures.
            roles.SaveProgress(session.Role!.Id, session.Progress); // The economic receipt verifies its durable starting level.
            await _app.Services.GetRequiredService<NewActivityService>().ReconcileAsync(session);
            var state = session.NewActivities!.Clone();
            var activity = state.ActivityInfo[112];
            var journey = activity.ActivityParam.Journey;
            journey.CurNodeId = 107;
            journey.CurActIds.Clear(); journey.CurActIds[10114] = true;
            journey.CompleteAct.Remove(10114);
            roles.SaveNewActivities(session.Role!.Id, state.ToByteArray());
            session.NewActivities = state;
            // A frozen delivery fixture supplies the same durable marker as the dungeon recipient plan.
            session.Dungeons!.PendingJourney[source] = new DungeonService.JourneyVictory(source,
                8100004, 10114, 107, activity.CurStartTime, activity.CurEndTime, activity.Playbook);
            roles.SaveDungeons(session.Role.Id, JsonSerializer.SerializeToUtf8Bytes(session.Dungeons));
        }));
        FixtureSql.Execute(accounts, "CREATE TRIGGER fail_journey_receipt BEFORE INSERT ON operation_receipt WHEN NEW.command_kind='cd13e/journey-victory' BEGIN SELECT RAISE(ABORT,'journey rollback'); END");
        try
        {
            await loop.InvokeAsync(() => dungeons.DeliverJourneyPendingAsync(session));
            Assert.Contains(source, session.Dungeons!.PendingJourney.Keys);
            Assert.False(session.NewActivities!.ActivityInfo[112].ActivityParam.Journey.CompleteAct.ContainsKey(10114));
            Assert.Contains(source, JsonSerializer.Deserialize<DungeonService.State>(roles.LoadDungeons(session.Role!.Id)!)!.PendingJourney.Keys);
        }
        finally { FixtureSql.Execute(accounts, "DROP TRIGGER fail_journey_receipt"); }
        recoveryFailure = null;
        if (expiresBeforeRecovery) _clock.Set(window.End + 60);
        await loop.InvokeAsync(() => _app.Services.GetRequiredService<OwnerRegistry>().RunSessionAsync(
            "journey continuation reload fixture", [ReservationKey.Realm], session, async () =>
        {
            // Reload both consumers from their durable snapshots before retrying the source.
            session.Dungeons = JsonSerializer.Deserialize<DungeonService.State>(roles.LoadDungeons(session.Role!.Id)!)!;
            session.NewActivities = DBNewActivityInfo.Parser.ParseFrom(roles.LoadNewActivities(session.Role.Id)!);
            if (expiresBeforeRecovery) await _app.Services.GetRequiredService<NewActivityService>().ReconcileAsync(session);
            await dungeons.DeliverJourneyPendingAsync(session);
            await dungeons.DeliverJourneyPendingAsync(session);
        }));
        Assert.Null(recoveryFailure);
        Assert.Empty(session.Dungeons!.PendingJourney);
        Assert.True(session.NewActivities!.ActivityInfo[112].ActivityParam.Journey.CompleteAct.ContainsKey(10114));
        Assert.Empty(JsonSerializer.Deserialize<DungeonService.State>(roles.LoadDungeons(session.Role!.Id)!)!.PendingJourney);
        using var db = accounts.Open(); using var command = db.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM operation_receipt WHERE command_kind='cd13e/journey-victory'";
        Assert.Equal(1L, command.ExecuteScalar());
    }

    [ClientDataFact]
    public async Task JourneyBossUsesAuthoredSpawnAndRejectsEntryBeforeItsAct()
    {
        var catalog = _app.Services.GetRequiredService<DungeonCatalog>();
        var plan = catalog.Resolve(new EnterDungeonReq { SceneEntry = 8100004 });
        Assert.NotNull(plan);
        Assert.Single(plan.Waves);
        Assert.Contains(plan.Waves[0].Spawns, spawn => spawn.NpcId == 81000004);

        await using var alice = await InWorld("Alice");
        await Gm("alice", "!lvl 99");
        await alice.SendGameAsync(DungeonService.Enter, new EnterDungeonReq { SceneEntry = 8100004 });
        Assert.NotEqual(0, (await Next(alice, DungeonService.Enter, EnterDungeonRsp.Parser)).Result);
    }

    [ClientDataFact]
    public async Task TreasureMonsterAwardAndMapUseDoNotInvertActivityAndOwnerGates()
    {
        var window = _app.Services.GetRequiredService<ActivityClock>()
            .Segments(1017, _clock.GetUtcNow().ToUnixTimeSeconds()).Single();
        _clock.Set(window.Start + 60);
        await using var alice = await InWorld("Alice");
        // Finish the socket's SceneLoaded handler before holding owners and awaiting its next request.
        await alice.Inbox.WaitAsync(e => e.IsGame(GuildService.SceneGuildId), Wait);
        await Gm("alice", "!lvl 99");
        await Gm("alice", "!item 1112001 1");
        var session = _app.Services.GetRequiredService<SessionRegistry>().All().Single(s => s.Role?.Name == "Alice");
        var loop = _app.Services.GetRequiredService<GameLoop>();
        var owners = _app.Services.GetRequiredService<Ro3Emu.Server.Game.Ownership.OwnerRegistry>();
        var combat = _app.Services.GetRequiredService<CombatService>();
        var award = combat.Killed.Single(h => h.Target is NewActivityService);
        var monster = _app.Services.GetRequiredService<Ro3Emu.Server.Maps.World>().Map(session.WorldScene)
            .Units.OfType<Ro3Emu.Server.Maps.MonsterUnit>().First();
        long guid = session.Items.Single(i => i.Entry == 1112001).Guid;
        var deferred = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task kill = Task.CompletedTask;
        bool stateHeld = false;
        try
        {
            await loop.InvokeAsync(async () =>
            {
                // A queued activity refresh can own Realm while awaiting StateLock. Admit
                // Realm first, then hold StateLock and start the real kill in this turn.
                using (await owners.EnterAsync("treasure race setup", Ro3Emu.Server.Game.Ownership.ReservationKey.Realm))
                {
                    Assert.True(session.StateLock.Wait(0));
                    stateHeld = true;
                    session.ActivityRuntime.AdventureTreasure = new AdventureTreasureCursor
                    { PendingMonsterUid = monster.Uid, PendingMonsterAward = 100001, GroupId = 1 };
                    owners.DeferredForTest = origin => { if (origin == "dispatcher") deferred.TrySetResult(); };
                }
                // The independent kill needs a fresh runtime permit, not the setup's owner scope.
                kill = loop.TrackAsync("treasure race award", () => owners.RunRuntimeAsync("treasure race", () => award(session, monster)));
                Assert.Equal(0, session.ActivityGate.CurrentCount);
                Assert.False(kill.IsCompleted);
            });
            await alice.SendGameAsync(CharacterService.ItemUse, new ItemUseReq { ItemGuid = guid, ItemCount = 1 });
            await deferred.Task.WaitAsync(Wait);
            session.StateLock.Release(); stateHeld = false;
            await kill.WaitAsync(Wait);
            Assert.Equal(0, (await Next(alice, CharacterService.ItemUse, ItemUseResp.Parser)).Result);
            Assert.DoesNotContain(session.Items, i => i.Guid == guid);
            Assert.True(session.ActivityRuntime.AdventureTreasure.PointId > 0);
            Assert.Equal(0, session.ActivityRuntime.AdventureTreasure.PendingMonsterAward);
        }
        finally
        {
            if (stateHeld) session.StateLock.Release();
            await loop.InvokeAsync(() => { owners.DeferredForTest = null; return Task.CompletedTask; });
            await kill.WaitAsync(Wait);
        }
    }

    [ClientDataFact]
    public async Task TreasureMapCreatesAnInteractableHuntAndConsumesOnlyOnce()
    {
        var window = _app.Services.GetRequiredService<ActivityClock>()
            .Segments(1017, _clock.GetUtcNow().ToUnixTimeSeconds()).Single();
        _clock.Set(window.Start + 60);
        await using var alice = await InWorld("Alice");
        await Gm("alice", "!lvl 99");
        await Gm("alice", "!item 1112001 2");
        var session = _app.Services.GetRequiredService<SessionRegistry>().All()
            .Single(s => s.Role?.Name == "Alice");
        long guid = session.Items.Single(i => i.Entry == 1112001).Guid;
        await alice.SendGameAsync(CharacterService.ItemUse, new ItemUseReq { ItemGuid = guid, ItemCount = 1 });
        Assert.Equal(0, (await Next(alice, CharacterService.ItemUse, ItemUseResp.Parser)).Result);
        var cursor = await Next(alice, 5064, DBTreasureInfoIncrement.Parser, i => i.PointId > 0);
        Assert.True(cursor.PointId > 0);

        var target = _app.Services.GetRequiredService<SceneService>().TreasurePoint(cursor.PointId);
        Assert.NotNull(target);
        Assert.Equal(session.SceneEntry, target.Value.Scene);
        string x = target.Value.Position.X.ToString(CultureInfo.InvariantCulture);
        string z = target.Value.Position.Z.ToString(CultureInfo.InvariantCulture);
        await Gm("alice", $"!jump {x} {z}");
        await alice.SendGameAsync(SceneService.NpcInteraction, new NpcInteractionReq
        {
            NpcUid = SceneService.TreasureUidPrefix | session.Role!.Id, InteractionId = 4,
        });
        var interaction = await Next(alice, SceneService.NpcInteraction, NpcInteractionRsp.Parser);
        Assert.Equal(0, interaction.Result);
        var eventResult = await Next(alice, 83251, TreasureEventNotify.Parser);
        Assert.Contains((int)eventResult.Type, new[] { 1, 4, 5 });

        await alice.SendGameAsync(SceneService.NpcInteraction, new NpcInteractionReq
        {
            NpcUid = SceneService.TreasureUidPrefix | session.Role.Id, InteractionId = 4,
        });
        Assert.NotEqual(0, (await Next(alice, SceneService.NpcInteraction, NpcInteractionRsp.Parser,
            i => i.Result != 0)).Result);
    }

    [ClientDataFact]
    public async Task TreasureShortcutUsesCommonValidationCooldownAndReplyContract()
    {
        var window = _app.Services.GetRequiredService<ActivityClock>()
            .Segments(1017, _clock.GetUtcNow().ToUnixTimeSeconds()).Single();
        _clock.Set(window.Start + 60);
        await using var alice = await InWorld("Alice");
        await Gm("alice", "!lvl 99");
        await Gm("alice", "!item 1112001 2");
        var session = _app.Services.GetRequiredService<SessionRegistry>().All().Single(s => s.Role?.Name == "Alice");
        var loop = _app.Services.GetRequiredService<GameLoop>();
        var character = _app.Services.GetRequiredService<CharacterService>();
        var owners = _app.Services.GetRequiredService<OwnerRegistry>();
        var itemStore = _app.Services.GetRequiredService<ItemStore>();
        Task FixtureOwned(string origin, Func<Task> work) => loop.InvokeAsync(() =>
            owners.RunSessionAsync(origin, [ReservationKey.Role(session.Role!.Id)], session, work));
        long guid = session.Items.Single(i => i.Entry == 1112001).Guid;

        await FixtureOwned("treasure shortcut dead fixture", () => { session.Dead = true; return Task.CompletedTask; });
        await alice.SendGameAsync(CharacterService.ItemUse, new ItemUseReq { ItemGuid = guid, ItemCount = 1 });
        Assert.Equal(ErrorCodes.UseWhileDead, (await Next(alice, CharacterService.ItemUse, ItemUseResp.Parser)).Result);
        await FixtureOwned("treasure shortcut expiry fixture", () =>
        {
            session.Dead = false;
            var item = session.Items.Single(i => i.Guid == guid);
            item.BindExpire = DateTimeOffset.UtcNow.ToUnixTimeSeconds() - 1;
            itemStore.Update(item);
            return Task.CompletedTask;
        });
        await alice.SendGameAsync(CharacterService.ItemUse, new ItemUseReq { ItemGuid = guid, ItemCount = 1 });
        Assert.Equal(ErrorCodes.ItemMissing, (await Next(alice, CharacterService.ItemUse, ItemUseResp.Parser)).Result);
        Assert.Equal(2, session.Items.Single(i => i.Guid == guid).Stack);
        Assert.Equal(0, session.ActivityRuntime.AdventureTreasure.PointId);

        int observations = 0;
        bool firstHookSawCommit = false;
        string firstObservation = "No consumption observer ran.";
        void Observe(GameSession changed)
        {
            // StateChanged also reports queued quest/activity grants; inspect the shortcut's dispatcher callback.
            if (!ReferenceEquals(changed, session) || owners.Current?.Origin != "dispatcher" || ++observations != 1) return;
            firstObservation = $"origin={owners.Current.Origin}, point={session.ActivityRuntime.AdventureTreasure.PointId}, stack={session.Items.Single(i => i.Guid == guid).Stack}, "
                + $"cooldown={session.ItemCds.GetValueOrDefault(1112001)}, bag={session.BagState.Info.ItemCds?.Data.GetValueOrDefault(1112001)}";
            firstHookSawCommit = session.ActivityRuntime.AdventureTreasure.PointId > 0
                && session.Items.Single(i => i.Guid == guid).Stack == 1
                && session.ItemCds.GetValueOrDefault(1112001) > 0
                && session.ItemCds[1112001] == session.BagState.Info.ItemCds?.Data.GetValueOrDefault(1112001);
        }
        await FixtureOwned("treasure shortcut active fixture", () =>
        {
            var item = session.Items.Single(i => i.Guid == guid);
            item.BindExpire = 0;
            itemStore.Update(item);
            var table = (IDictionary<string, System.Text.Json.JsonElement>)_app.Services
                .GetRequiredService<Ro3Emu.Server.Data.GameData>().Table("ItemConfig");
            var row = System.Text.Json.Nodes.JsonNode.Parse(table["1112001"].GetRawText())!;
            row["_iCD"] = 60000;
            table["1112001"] = System.Text.Json.JsonSerializer.SerializeToElement(row);
            character.StateChanged.Add(Observe);
            return Task.CompletedTask;
        });
        try
        {
            await alice.SendGameAsync(CharacterService.ShortcutBarSave, new ShortcutBarSaveReq { SlotIndex = 0, ItemGuid = guid });
            Assert.Equal(0, (await Next(alice, CharacterService.ShortcutBarSave, ShortcutBarSaveRes.Parser)).Result);
            int directReplies = alice.Inbox.Snapshot().Count(p => p.IsGame(CharacterService.ItemUse));
            await alice.SendGameAsync(CharacterService.ShortcutBarUse, new ShortcutBarUseReq { SlotIndex = 0 });
            Assert.Equal(0, (await Next(alice, CharacterService.ShortcutBarUse, ShortcutBarUseRes.Parser)).Result);
            var cooldown = Assert.Single((await Next(alice, CharacterService.ItemsCdNotify, ItemsCDNotify.Parser)).Items);
            Assert.Equal(1112001, cooldown.ItemId);
            Assert.True(cooldown.ItemCd > DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
            Assert.Equal(directReplies, alice.Inbox.Snapshot().Count(p => p.IsGame(CharacterService.ItemUse)));
            Assert.True(firstHookSawCommit, firstObservation);
            var roles = _app.Services.GetRequiredService<Ro3Emu.Server.Accounts.RoleStore>();
            Assert.Equal(cooldown.ItemCd, DBItemsInfo.Parser.ParseFrom(roles.LoadBagState(session.Role!.Id)!).ItemCds.Data[1112001]);
            var runtime = System.Text.Json.JsonSerializer.Deserialize<ActivityRuntimeState>(roles.LoadActivityRuntime(session.Role.Id)!)!;
            Assert.Equal(session.ActivityRuntime.AdventureTreasure.PointId, runtime.AdventureTreasure.PointId);

            await alice.SendGameAsync(CharacterService.ShortcutBarUse, new ShortcutBarUseReq { SlotIndex = 0 });
            Assert.Equal(161, (await Next(alice, CharacterService.ShortcutBarUse, ShortcutBarUseRes.Parser)).Result);
            Assert.Equal(1, session.Items.Single(i => i.Guid == guid).Stack);
        }
        finally
        {
            await loop.InvokeAsync(() => { character.StateChanged.Remove(Observe); return Task.CompletedTask; });
        }
    }

    [ClientDataFact]
    public async Task TreasureCooldownWriteFailureRollsBackSelectedMapAndHunt()
    {
        var window = _app.Services.GetRequiredService<ActivityClock>()
            .Segments(1017, _clock.GetUtcNow().ToUnixTimeSeconds()).Single();
        _clock.Set(window.Start + 60);
        await using var alice = await InWorld("Alice");
        await Gm("alice", "!lvl 99");
        await Gm("alice", "!item 1112001 1");
        var session = _app.Services.GetRequiredService<SessionRegistry>().All().Single(s => s.Role?.Name == "Alice");
        var roles = _app.Services.GetRequiredService<Ro3Emu.Server.Accounts.RoleStore>();
        long role = session.Role!.Id, guid = session.Items.Single(i => i.Entry == 1112001).Guid;
        var owners = _app.Services.GetRequiredService<Ro3Emu.Server.Game.Ownership.OwnerRegistry>();
        // Keep the snapshot, failure and byte-exact observations in one dispatcher-equivalent scope;
        // unrelated queued activity refreshes may legitimately update this blob between separate turns.
        await _app.Services.GetRequiredService<GameLoop>().InvokeAsync(() => owners.RunSessionAsync("treasure rollback test",
            [Ro3Emu.Server.Game.Ownership.ReservationKey.Role(role)], session, async () =>
        {
            byte[]? beforeRuntime = roles.LoadActivityRuntime(role), beforeBag = roles.LoadBagState(role);
            FixtureSql.Execute(_app.Services.GetRequiredService<AccountStore>(),
                "CREATE TRIGGER reject_treasure_cd BEFORE UPDATE OF bag_state ON role BEGIN SELECT RAISE(ABORT,'treasure cooldown rollback'); END");
            try
            {
                await Assert.ThrowsAsync<Microsoft.Data.Sqlite.SqliteException>(() =>
                    _app.Services.GetRequiredService<CharacterService>().UseItemAsync(session, new ItemUseReq { ItemGuid = guid, ItemCount = 1 }));
                Assert.Equal(1, session.Items.Single(i => i.Guid == guid).Stack);
                Assert.Equal(1, _app.Services.GetRequiredService<Ro3Emu.Server.Accounts.ItemStore>().Load(role).Single(i => i.Guid == guid).Stack);
                Assert.Equal(0, session.ActivityRuntime.AdventureTreasure.PointId);
                Assert.Equal(0, session.ItemCds.GetValueOrDefault(1112001));
                Assert.Equal(beforeRuntime, roles.LoadActivityRuntime(role));
                Assert.Equal(beforeBag, roles.LoadBagState(role));
            }
            finally { FixtureSql.Execute(_app.Services.GetRequiredService<AccountStore>(), "DROP TRIGGER reject_treasure_cd"); }
        }));
        await alice.SendGameAsync(CharacterService.ItemUse, new ItemUseReq { ItemGuid = guid, ItemCount = 1 });
        Assert.Equal(0, (await Next(alice, CharacterService.ItemUse, ItemUseResp.Parser)).Result);
        Assert.DoesNotContain(session.Items, i => i.Guid == guid);
        Assert.True(session.ActivityRuntime.AdventureTreasure.PointId > 0);
    }

    [ClientDataFact]
    public async Task FragmentCraftWithoutCrystalSetReportsFailureWithoutProgress()
    {
        var window = _app.Services.GetRequiredService<ActivityClock>()
            .Segments(1014, _clock.GetUtcNow().ToUnixTimeSeconds()).Single();
        _clock.Set(window.Start + 60);
        await using var alice = await InWorld("Alice");
        await Gm("alice", "!lvl 99");
        var session = _app.Services.GetRequiredService<SessionRegistry>().All().Single(s => s.Role?.Name == "Alice");

        await alice.SendGameAsync(82965, new NewActivityFragmentGatherCraftReq());
        Assert.Equal(ErrorCodes.ItemsShort, (await Next(alice, 2, CommonErrorNtf.Parser)).ErrorCode);
        Assert.Equal(0, (await Next(alice, 82965, NewActivityFragmentGatherCraftResp.Parser)).Level);
        Assert.Equal(0, session.NewActivities!.ActivityInfo[105].ActivityParam.FragmentGather.ProgressFinish);

        await alice.SendGameAsync(NewActivityService.Common, new CommonActivityReq
        {
            ActivityId = 105, RequestId = 200, FragmentGatherCraft = new NewActivityFragmentGatherCraftReq(),
        });
        var common = await Next(alice, NewActivityService.Common, CommonActivityRes.Parser, r => r.RequestId == 200);
        Assert.Equal(ErrorCodes.ItemsShort, common.Result);
        Assert.Equal(0, common.FragmentGatherCraft.Level);
        Assert.Equal(0, session.NewActivities!.ActivityInfo[105].ActivityParam.FragmentGather.ProgressFinish);
    }

    [ClientDataFact]
    public async Task FragmentGiftTransfersOneCrystalAndCannotBeReplayed()
    {
        var window = _app.Services.GetRequiredService<ActivityClock>()
            .Segments(1014, _clock.GetUtcNow().ToUnixTimeSeconds()).Single();
        _clock.Set(window.Start + 60);
        var alice = await InWorld("Alice");
        await using var bob = await InWorld("Bob");
        await Gm("alice", "!lvl 99");
        await Gm("bob", "!lvl 99");
        await Gm("bob", "!item 1111001 2");
        var registry = _app.Services.GetRequiredService<SessionRegistry>();
        var owner = registry.All().Single(s => s.Role?.Name == "Alice");
        var donor = registry.All().Single(s => s.Role?.Name == "Bob");

        await alice.SendGameAsync(82963, new NewActivityFragmentGatherAskReq { ItemConfigID = 1111001 });
        var ask = await Next(alice, 82963, NewActivityFragmentGatherAskResp.Parser, r => r.AskID > 0);
        Assert.Equal(1111001, ask.ItemConfigID);
        var send = new NewActivityFragmentGatherSendReq { ToUserID = owner.Role!.Id, AskID = ask.AskID, ItemID = 1111001 };
        await bob.SendGameAsync(82964, send);
        await Next(bob, 82964, NewActivityFragmentGatherSendResp.Parser);
        Assert.Equal(1, donor.Items.Where(i => i.Entry == 1111001).Sum(i => i.Stack));
        Assert.Equal(1, owner.Items.Where(i => i.Entry == 1111001).Sum(i => i.Stack));

        await bob.SendGameAsync(82964, send);
        await Next(bob, 82964, NewActivityFragmentGatherSendResp.Parser);
        Assert.Equal(1, donor.Items.Where(i => i.Entry == 1111001).Sum(i => i.Stack));
        Assert.Equal(1, owner.Items.Where(i => i.Entry == 1111001).Sum(i => i.Stack));
        await alice.DisposeAsync();
        await using var relog = await InWorld("Alice", create: false);
        var persisted = registry.All().Single(s => s.Role?.Name == "Alice" && !ReferenceEquals(s, owner));
        Assert.Equal(1, persisted.Items.Where(i => i.Entry == 1111001).Sum(i => i.Stack));
    }

    [ClientDataFact]
    public async Task ReciprocalFragmentGiftsCommitWithoutCrossGateWait()
    {
        var window = _app.Services.GetRequiredService<ActivityClock>()
            .Segments(1014, _clock.GetUtcNow().ToUnixTimeSeconds()).Single();
        _clock.Set(window.Start + 60);
        await using var alice = await InWorld("Alice");
        await using var bob = await InWorld("Bob");
        await Gm("alice", "!lvl 99"); await Gm("bob", "!lvl 99");
        await Gm("alice", "!item 1111001 1"); await Gm("bob", "!item 1111001 1");
        await alice.SendGameAsync(82963, new NewActivityFragmentGatherAskReq { ItemConfigID = 1111001 });
        var aliceAsk = await Next(alice, 82963, NewActivityFragmentGatherAskResp.Parser, r => r.AskID > 0);
        await bob.SendGameAsync(82963, new NewActivityFragmentGatherAskReq { ItemConfigID = 1111001 });
        var bobAsk = await Next(bob, 82963, NewActivityFragmentGatherAskResp.Parser, r => r.AskID > 0);
        var aliceId = (long)alice.Self!.Uuid; var bobId = (long)bob.Self!.Uuid;
        var fromAlice = new NewActivityFragmentGatherSendReq
        { ToUserID = bobId, AskID = bobAsk.AskID, ItemID = 1111001 };
        var fromBob = new NewActivityFragmentGatherSendReq
        { ToUserID = aliceId, AskID = aliceAsk.AskID, ItemID = 1111001 };
        await Task.WhenAll(
            alice.SendGameAsync(NewActivityService.Common, new CommonActivityReq
            { ActivityId = 105, RequestId = 701, FragmentGatherSend = fromAlice.Clone() }),
            bob.SendGameAsync(NewActivityService.Common, new CommonActivityReq
            { ActivityId = 105, RequestId = 702, FragmentGatherSend = fromBob.Clone() }));
        var outcomes = await Task.WhenAll(
            Next(alice, NewActivityService.Common, CommonActivityRes.Parser, r => r.RequestId == 701),
            Next(bob, NewActivityService.Common, CommonActivityRes.Parser, r => r.RequestId == 702));
        Assert.All(outcomes, result => Assert.True(result.Result is 0 or ErrorCodes.Cooldown));
        if (outcomes[0].Result != 0)
        {
            await alice.SendGameAsync(NewActivityService.Common, new CommonActivityReq
            { ActivityId = 105, RequestId = 703, FragmentGatherSend = fromAlice });
            Assert.Equal(0, (await Next(alice, NewActivityService.Common, CommonActivityRes.Parser,
                r => r.RequestId == 703)).Result);
        }
        if (outcomes[1].Result != 0)
        {
            await bob.SendGameAsync(NewActivityService.Common, new CommonActivityReq
            { ActivityId = 105, RequestId = 704, FragmentGatherSend = fromBob });
            Assert.Equal(0, (await Next(bob, NewActivityService.Common, CommonActivityRes.Parser,
                r => r.RequestId == 704)).Result);
        }
        var registry = _app.Services.GetRequiredService<SessionRegistry>();
        Assert.Equal(1, registry.All().Single(s => s.Role?.Id == aliceId).Items.Sum(i => i.Entry == 1111001 ? i.Stack : 0));
        Assert.Equal(1, registry.All().Single(s => s.Role?.Id == bobId).Items.Sum(i => i.Entry == 1111001 ? i.Stack : 0));
    }

    [ClientDataFact]
    public async Task TwoDonorsCannotConsumeTheSameFragmentAskTwice()
    {
        var window = _app.Services.GetRequiredService<ActivityClock>()
            .Segments(1014, _clock.GetUtcNow().ToUnixTimeSeconds()).Single();
        _clock.Set(window.Start + 60);
        await using var alice = await InWorld("Alice");
        await using var bob = await InWorld("Bob");
        await using var carol = await InWorld("Carol");
        foreach (string account in new[] { "alice", "bob", "carol" }) await Gm(account, "!lvl 99");
        await Gm("bob", "!item 1111001 1"); await Gm("carol", "!item 1111001 1");
        await alice.SendGameAsync(82963, new NewActivityFragmentGatherAskReq { ItemConfigID = 1111001 });
        var ask = await Next(alice, 82963, NewActivityFragmentGatherAskResp.Parser, r => r.AskID > 0);
        var send = new NewActivityFragmentGatherSendReq
        { ToUserID = (long)alice.Self!.Uuid, AskID = ask.AskID, ItemID = 1111001 };
        await Task.WhenAll(bob.SendGameAsync(NewActivityService.Common, new CommonActivityReq
        { ActivityId = 105, RequestId = 711, FragmentGatherSend = send.Clone() }),
            carol.SendGameAsync(NewActivityService.Common, new CommonActivityReq
            { ActivityId = 105, RequestId = 712, FragmentGatherSend = send.Clone() }));
        var outcomes = await Task.WhenAll(
            Next(bob, NewActivityService.Common, CommonActivityRes.Parser, r => r.RequestId == 711),
            Next(carol, NewActivityService.Common, CommonActivityRes.Parser, r => r.RequestId == 712));
        Assert.Single(outcomes, result => result.Result == 0);
        Assert.Single(outcomes, result => result.Result != 0);
        var registry = _app.Services.GetRequiredService<SessionRegistry>();
        Assert.Equal(1, registry.All().Single(s => s.Role?.Name == "Alice").Items.Sum(i => i.Entry == 1111001 ? i.Stack : 0));
        Assert.Equal(1, registry.All().Where(s => s.Role?.Name is "Bob" or "Carol")
            .SelectMany(s => s.Items).Sum(i => i.Entry == 1111001 ? i.Stack : 0));
    }

    [ClientDataFact]
    public async Task FragmentGiftRefusesBusyReceiverWithoutChangingEitherOwner()
    {
        var window = _app.Services.GetRequiredService<ActivityClock>()
            .Segments(1014, _clock.GetUtcNow().ToUnixTimeSeconds()).Single();
        _clock.Set(window.Start + 60);
        await using var alice = await InWorld("Alice");
        await using var bob = await InWorld("Bob");
        await Gm("alice", "!lvl 99"); await Gm("bob", "!lvl 99");
        await Gm("bob", "!item 1111001 1");
        await alice.SendGameAsync(82963, new NewActivityFragmentGatherAskReq { ItemConfigID = 1111001 });
        var ask = await Next(alice, 82963, NewActivityFragmentGatherAskResp.Parser, r => r.AskID > 0);
        var registry = _app.Services.GetRequiredService<SessionRegistry>();
        var receiver = registry.All().Single(s => s.Role?.Name == "Alice");
        var sender = registry.All().Single(s => s.Role?.Name == "Bob");
        var loop = _app.Services.GetRequiredService<GameLoop>();
        var activities = _app.Services.GetRequiredService<NewActivityService>();
        // Synthetic busy state must follow owner-before-gate ordering: hold Realm while taking the receiver's gate and
        // sending, so no queued reconciliation (activity, lottery, ...) can own the receiver and wait on this gate.
        await loop.InvokeAsync(() => _app.Services.GetRequiredService<Ro3Emu.Server.Game.Ownership.OwnerRegistry>().RunAsync(
            "busy fragment receiver fixture", [Ro3Emu.Server.Game.Ownership.ReservationKey.Realm], async () =>
        {
            await receiver.ActivityGate.WaitAsync();
            try
            {
                await activities.FragmentSendAsync(sender, new NewActivityFragmentGatherSendReq
                { ToUserID = receiver.Role!.Id, AskID = ask.AskID, ItemID = 1111001 });
            }
            finally { receiver.ActivityGate.Release(); }
        }));
        Assert.Equal(ErrorCodes.Cooldown, (await Next(bob, 2, CommonErrorNtf.Parser)).ErrorCode);
        await Next(bob, 82964, NewActivityFragmentGatherSendResp.Parser);
        Assert.Equal(1, sender.Items.Sum(i => i.Entry == 1111001 ? i.Stack : 0));
        Assert.Equal(0, receiver.Items.Sum(i => i.Entry == 1111001 ? i.Stack : 0));
        Assert.False(receiver.ActivityRuntime.AdventureFragmentAsks[ask.AskID].Finished);
        Assert.Equal(0, receiver.NewActivities!.ActivityInfo[105].ActivityParam.FragmentGather.DailyData.ReceiveTimes);
    }

    [ClientDataFact]
    public async Task DeletedOfflineAskOwnerCannotConsumeDonorFragment()
    {
        var window = _app.Services.GetRequiredService<ActivityClock>()
            .Segments(1014, _clock.GetUtcNow().ToUnixTimeSeconds()).Single();
        _clock.Set(window.Start + 60);
        var alice = await InWorld("Alice");
        await using var bob = await InWorld("Bob");
        await Gm("alice", "!lvl 99"); await Gm("bob", "!lvl 99");
        await Gm("bob", "!item 1111001 1");
        await alice.SendGameAsync(82963, new NewActivityFragmentGatherAskReq { ItemConfigID = 1111001 });
        var ask = await Next(alice, 82963, NewActivityFragmentGatherAskResp.Parser, r => r.AskID > 0);
        long aliceId = (long)alice.Self!.Uuid;
        var oldSession = _app.Services.GetRequiredService<SessionRegistry>().All()
            .Single(s => s.Role?.Id == aliceId);
        var handover = _app.Services.GetRequiredService<RoleHandover>();
        var detached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        handover.PhaseForTest = (phase, session) =>
        {
            if (phase == "Detached" && ReferenceEquals(session, oldSession)) detached.TrySetResult();
        };
        try
        {
            await alice.DisposeAsync();
            await detached.Task.WaitAsync(Wait);
        }
        finally { handover.PhaseForTest = null; }
        var loop = _app.Services.GetRequiredService<GameLoop>();
        var owners = _app.Services.GetRequiredService<OwnerRegistry>();
        await loop.InvokeAsync(() => { Assert.Equal(RoleOwnerState.Detached, owners.Get(aliceId).State); return Task.CompletedTask; });
        using (var client = new HttpClient())
        using (var response = await client.PostAsync($"{_http}/ro3emu/account/delete-role",
            new FormUrlEncodedContent(new Dictionary<string, string>
            { ["identity"] = "alice", ["credential"] = "pw", ["role"] = "Alice" })))
        using (var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync()))
            Assert.Equal(0, json.RootElement.GetProperty("code").GetInt32());
        await bob.SendGameAsync(NewActivityService.Common, new CommonActivityReq
        {
            ActivityId = 105, RequestId = 713,
            FragmentGatherSend = new() { ToUserID = aliceId, AskID = ask.AskID, ItemID = 1111001 },
        });
        Assert.Equal(ErrorCodes.Parameter,
            (await Next(bob, NewActivityService.Common, CommonActivityRes.Parser, r => r.RequestId == 713)).Result);
        var donor = _app.Services.GetRequiredService<SessionRegistry>().All().Single(s => s.Role?.Name == "Bob");
        Assert.Equal(1, donor.Items.Sum(i => i.Entry == 1111001 ? i.Stack : 0));
        var roles = _app.Services.GetRequiredService<RoleStore>();
        var runtime = JsonSerializer.Deserialize<ActivityRuntimeState>(roles.LoadActivityRuntime(aliceId)!)!;
        Assert.False(runtime.AdventureFragmentAsks[ask.AskID].Finished);
        Assert.Equal(0, _app.Services.GetRequiredService<ItemStore>().Load(aliceId)
            .Sum(i => i.Entry == 1111001 ? i.Stack : 0));
    }

    [ClientDataTheory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task OfflineRecipientLoginOrDeleteWaitsForFragmentCommit(bool delete, bool afterCommit)
    {
        var window = _app.Services.GetRequiredService<ActivityClock>()
            .Segments(1014, _clock.GetUtcNow().ToUnixTimeSeconds()).Single();
        _clock.Set(window.Start + 60);
        var alice = await InWorld("Alice");
        await using var bob = await InWorld("Bob");
        await Gm("alice", "!lvl 99"); await Gm("bob", "!lvl 99");
        await Gm("bob", "!item 1111001 1");
        await alice.SendGameAsync(82963, new NewActivityFragmentGatherAskReq { ItemConfigID = 1111001 });
        var ask = await Next(alice, 82963, NewActivityFragmentGatherAskResp.Parser, r => r.AskID > 0);
        long aliceId = (long)alice.Self!.Uuid;
        var oldSession = _app.Services.GetRequiredService<SessionRegistry>().All()
            .Single(s => s.Role?.Id == aliceId);
        var handover = _app.Services.GetRequiredService<RoleHandover>();
        var detached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        handover.PhaseForTest = (phase, session) =>
        {
            if (phase == "Detached" && ReferenceEquals(session, oldSession)) detached.TrySetResult();
        };
        await alice.DisposeAsync();
        await detached.Task.WaitAsync(Wait);
        handover.PhaseForTest = null;
        var loop = _app.Services.GetRequiredService<GameLoop>();
        var owners = _app.Services.GetRequiredService<OwnerRegistry>();
        await loop.InvokeAsync(() => { Assert.Equal(RoleOwnerState.Detached, owners.Get(aliceId).State); return Task.CompletedTask; });
        var accounts = _app.Services.GetRequiredService<AccountStore>();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var deferred = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task Hold(string label)
        {
            if (label != "activity/fragment-transfer") return Task.CompletedTask;
            entered.TrySetResult(); return release.Task;
        }
        if (afterCommit) accounts.BeforeOwnedApplyForTest = Hold;
        else accounts.BeforeOwnedCommitForTest = Hold;
        owners.DeferredForTest = origin =>
        {
            if (origin == (delete ? "HTTP delete" : "role select")) deferred.TrySetResult();
        };
        BotClient? relog = null;
        Task? pending = null;
        try
        {
            await bob.SendGameAsync(82964, new NewActivityFragmentGatherSendReq
            { ToUserID = aliceId, AskID = ask.AskID, ItemID = 1111001 });
            await entered.Task.WaitAsync(Wait);
            if (delete)
            {
                pending = Task.Run(async () =>
                {
                    using var client = new HttpClient();
                    using var response = await client.PostAsync($"{_http}/ro3emu/account/delete-role",
                        new FormUrlEncodedContent(new Dictionary<string, string>
                        { ["identity"] = "alice", ["credential"] = "pw", ["role"] = "Alice" }));
                    using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
                    Assert.Equal(0, json.RootElement.GetProperty("code").GetInt32());
                });
            }
            else
            {
                relog = new BotClient(_http, File.ReadAllText(Path.Combine(_dataDir, "gate_rsa_public.pem")));
                await relog.LoginAsync("alice", "pw"); await relog.EnterGateAsync();
                pending = relog.EnterWorldAsync(aliceId);
            }
            await deferred.Task.WaitAsync(Wait);
            Assert.False(pending.IsCompleted);
        }
        finally
        {
            release.TrySetResult(); accounts.BeforeOwnedCommitForTest = null;
            accounts.BeforeOwnedApplyForTest = null;
            owners.DeferredForTest = null;
        }
        await Next(bob, 82964, NewActivityFragmentGatherSendResp.Parser);
        await pending!.WaitAsync(Wait);
        var roles = _app.Services.GetRequiredService<RoleStore>();
        Assert.Equal(1, _app.Services.GetRequiredService<ItemStore>().Load(aliceId)
            .Sum(i => i.Entry == 1111001 ? i.Stack : 0));
        if (delete) Assert.Null(roles.Get(aliceId));
        else await relog!.DisposeAsync();
    }

    private static string RepoRoot()
    {
        var path = new DirectoryInfo(AppContext.BaseDirectory);
        while (path != null && !Directory.Exists(Path.Combine(path.FullName, "data", "config"))) path = path.Parent;
        return path?.FullName ?? throw new DirectoryNotFoundException("Repository data/config");
    }

    /// <summary>106802 counts a committed dig at the Treasure Map's point (growth title 4's 704090 [106802,20], the
    /// activity quest 110008 [106802,1]); using the map alone is not a dig. The count survives a relogin.</summary>
    [ClientDataFact]
    public async Task TreasureDigCountsTowardTheTreasureHuntQuests()
    {
        var window = _app.Services.GetRequiredService<ActivityClock>()
            .Segments(1017, _clock.GetUtcNow().ToUnixTimeSeconds()).Single();
        _clock.Set(window.Start + 60);
        var alice = await InWorld("Alice");
        await Gm("alice", "!lvl 99");
        await QuestProbe.AcceptAsync(_app, _http, "alice", 704090);
        await QuestProbe.AcceptAsync(_app, _http, "alice", 110008);
        await Gm("alice", "!item 1112001 1");
        var session = _app.Services.GetRequiredService<SessionRegistry>().All().Single(s => s.Role?.Name == "Alice");
        long guid = session.Items.Single(i => i.Entry == 1112001).Guid;
        await alice.SendGameAsync(CharacterService.ItemUse, new ItemUseReq { ItemGuid = guid, ItemCount = 1 });
        Assert.Equal(0, (await Next(alice, CharacterService.ItemUse, ItemUseResp.Parser)).Result);
        var cursor = await Next(alice, 5064, DBTreasureInfoIncrement.Parser, i => i.PointId > 0);
        Assert.Equal(0, (await QuestProbe.ReadAsync(_app, "alice", 704090))!.CompleteNum[0]);

        var target = _app.Services.GetRequiredService<SceneService>().TreasurePoint(cursor.PointId)!.Value;
        string x = target.Position.X.ToString(CultureInfo.InvariantCulture);
        string z = target.Position.Z.ToString(CultureInfo.InvariantCulture);
        await Gm("alice", $"!jump {x} {z}");
        await alice.SendGameAsync(SceneService.NpcInteraction, new NpcInteractionReq
        {
            NpcUid = SceneService.TreasureUidPrefix | session.Role!.Id, InteractionId = 4,
        });
        Assert.Equal(0, (await Next(alice, SceneService.NpcInteraction, NpcInteractionRsp.Parser)).Result);
        await Next(alice, 83251, TreasureEventNotify.Parser);
        await QuestProbe.AtAsync(_app, "alice", 704090, 1);
        await QuestProbe.DoneAsync(_app, "alice", 110008);

        await alice.DisposeAsync();
        await QuestProbe.OfflineAsync(_app, "alice");
        await using var again = await InWorld("Alice", create: false);
        await QuestProbe.AtAsync(_app, "alice", 704090, 1);
    }

    /// <summary>
    /// Lost Treasure (activity 106, _kTaskRenew [1013,13,132,..] and [0,13,131,..]) offers its season tasks while open:
    /// 110007 "Log in to the game" [106801,1] is met by the login it is accepted in and claimed in the panel with 31205;
    /// the daily group (TimeLimit 1013 "refreshTaskDaily") starts over the next game day, the other group does not.
    /// </summary>
    [ClientDataFact]
    public async Task LostTreasureOffersItsTasksAndTheDailyOnesStartOverNextDay()
    {
        var window = _app.Services.GetRequiredService<ActivityClock>()
            .Segments(1017, _clock.GetUtcNow().ToUnixTimeSeconds()).Single();
        long start = window.Start + 60;
        Assert.True(window.End > start + 86400 + 600);
        _clock.Set(start);
        var alice = await InWorld("Alice");
        await Gm("alice", "!lvl 99");
        var login = await QuestProbe.DoneAsync(_app, "alice", 110007);
        Assert.Equal(TaskState.Finish, login.Status);
        await QuestProbe.AtAsync(_app, "alice", 110008, 0); // a dig is still to come
        var other = await QuestProbe.AtAsync(_app, "alice", 110001, 0); // [103022,1] the non-daily group
        await alice.SendGameAsync(TaskService.TaskDelivery, new TaskDeliveryReq { Req = { new TaskDeliverySingleReq { Id = 110007 } } });
        Assert.Single((await Next(alice, TaskService.TaskDelivery, TaskDeliveryResp.Parser)).Data);
        await QuestProbe.WaitAsync(_app, "alice", 110007, i => i.Status == TaskState.Delivery, "claimed");
        // A running task cannot be claimed.
        await alice.SendGameAsync(TaskService.TaskDelivery, new TaskDeliveryReq { Req = { new TaskDeliverySingleReq { Id = 110001 } } });
        Assert.Empty((await Next(alice, TaskService.TaskDelivery, TaskDeliveryResp.Parser)).Data);

        await alice.DisposeAsync();
        await QuestProbe.OfflineAsync(_app, "alice");
        _clock.Set(start + 86400);
        await using var again = await InWorld("Alice", create: false);
        var renewed = await QuestProbe.WaitAsync(_app, "alice", 110007,
            i => i.Status == TaskState.Finish && i.StartTime > login.StartTime, "renewed for the new day");
        Assert.Equal(1, renewed.CompleteNum[0]);
        Assert.Equal(other.StartTime, (await QuestProbe.ReadAsync(_app, "alice", 110001))!.StartTime); // never renews
    }
}

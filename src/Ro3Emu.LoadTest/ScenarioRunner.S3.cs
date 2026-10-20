using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using Google.Protobuf;
using Microsoft.Data.Sqlite;
using Romsg;
using Ro3Emu.Bot;
using Ro3Emu.Server;
using Ro3Emu.Server.Game;
using Ro3Emu.Server.Maps;
using Ro3Emu.Server.Economy;

namespace Ro3Emu.LoadTest;

public sealed partial class ScenarioRunner
{
    private readonly ConcurrentDictionary<int, long> _s3Expected = new();
    private readonly ConcurrentDictionary<string, byte> _s3Unknown = new();
    private readonly ConcurrentDictionary<ulong, byte> _s3GatherClaims = new();
    private readonly List<string> _s3Fixtures = new();
    private int _s3ShopPayment, _s3ShopGoods, _s3ShopGoodsCount;
    private int _s3GatherCompletions, _s3TowerEntries, _s3TowerSettlements;
    private bool _s3Prepared;
    private EconomyCatalog? _s3EconomyCatalog;

    private static (BotActor[] Town, BotActor[] Field, BotActor[] Tower, BotActor[] Menu) S3Cohorts(BotActor[] actors)
    {
        int town = actors.Length * 200 / 500, field = actors.Length * 200 / 500;
        int tower = actors.Length * 50 / 500;
        return (actors.Take(town).ToArray(), actors.Skip(town).Take(field).ToArray(),
            actors.Skip(town + field).Take(tower).ToArray(), actors.Skip(town + field + tower).ToArray());
    }

    private void S3Unsupported(RunReport report, string reason) =>
        report.UnsupportedBehaviours = report.UnsupportedBehaviours.Append(reason).Distinct().ToArray();

    /// <summary>Only fixture work and the opening snapshot run before the measured epoch.</summary>
    private async Task PrepareS3Async(RunReport report, CancellationToken ct)
    {
        var c = S3Cohorts(_actors.OrderBy(a => a.Account).ToArray());
        // Player mail compose/send is absent from MailService's micro router and game dispatcher.
        S3Unsupported(report, "player mail send: no registered client protocol route");
        S3Unsupported(report, "paid equipment refine: no finite equipment/material fixture");
        S3Unsupported(report, "pet actions and raid operations: no S3 protocol fixture");
        if (report.ServerDataDir == null)
        {
            S3Unsupported(report, "local SQLite ledger and GM fixtures unavailable for remote server");
            return;
        }
        _s3DataDir = report.ServerDataDir;
        foreach (var actor in _actors)
        {
            actor.Reducer.OnReward = reward =>
            {
                // Exchange/shop popup uses the same 31322 envelope with manifest type 1.
                // Only RewardService's type-8 world drop is an independent faucet observation.
                if (!_measureOffers || reward.TargetUid != actor.RoleId || !reward.RewardType.Contains(8)) return;
                foreach (var item in reward.Rewards)
                    if (item.ItemEntry is not (100 or 101) && item.Stack > 0)
                        _s3Expected.AddOrUpdate(item.ItemEntry, item.Stack, (_, n) => checked(n + item.Stack));
            };
            actor.Reducer.OnTowerEnd = outcome =>
            {
                if (!_measureOffers || outcome.Result != 0 || !outcome.ClimTowerResult ||
                    outcome.RewardNotify?.TargetUid != actor.RoleId) return;
                Interlocked.Increment(ref _s3TowerSettlements);
                foreach (var item in outcome.RewardNotify.Rewards)
                    if (item.ItemEntry is not (100 or 101) && item.Stack > 0)
                        _s3Expected.AddOrUpdate(item.ItemEntry, item.Stack, (_, n) => checked(n + item.Stack));
            };
        }
        if (c.Town.Length < 8 || c.Field.Length < 4 || c.Tower.Length == 0)
        {
            S3Unsupported(report, "S3 economy/gathering/tower sample needs at least 20 bots");
            return;
        }
        var fixtures = new List<(BotActor Actor, string Command)>();
        foreach (var actor in c.Town.Take(4))
        {
            fixtures.Add((actor, "!lvl 30"));
            fixtures.Add((actor, "!item 1254013 2"));
            fixtures.Add((actor, "!item 2 700"));
            fixtures.Add((actor, "!mail S3_claim_probe 1000000 1"));
        }
        foreach (var actor in c.Town.Skip(4).Take(4))
        {
            fixtures.Add((actor, "!lvl 30"));
            fixtures.Add((actor, "!item 1 10000"));
        }
        foreach (var actor in c.Tower)
        {
            fixtures.Add((actor, "!lvl 99"));
            fixtures.Add((actor, "!quest done 10105020"));
        }
        // A finite buyer purse and finite seller stock are provisioned once, outside measured traffic.
        foreach (var (actor, command) in fixtures)
            await S3GmAsync(actor, command, ct);
        _s3Fixtures.Add($"{fixtures.Count} local GM commands for four listings, four buyers, four claimable mails and {c.Tower.Length} tower unlocks");
        foreach (var actor in c.Field.Take(4))
        {
            await S3GmAsync(actor, "!item 1291000 1", ct);
            await S3WarpAsync(actor, 10522, 245.93f, 220.27f, ct);
            using var reply = actor.Bot.Inbox.Expect(e => e.IsGame(LifeJobService.SwitchLife));
            await actor.Sender!.SendAsync(() => actor.Bot.SendGameAsync(LifeJobService.SwitchLife,
                new SwitchLifeJobReq { NewLifejobId = (LifeJobType)1, OpJobType = 1 }), ct);
            var switched = SwitchLifeJobResp.Parser.ParseFrom((await reply.WaitAsync(TimeSpan.FromSeconds(10), ct)).Body);
            if (switched.Result != 0) throw new InvalidOperationException($"S3 miner setup refused {switched.Result}");
        }
        _s3Fixtures.Add("four field roles received a Miner certificate and were positioned at the authored 10522 collection area");
        // ShopConfig 10: NPC 100002, class 301, level-10 unlock; commodity 113 costs 100 zeny.
        var commodity = new Ro3Emu.Server.Data.GameData(
            Microsoft.Extensions.Options.Options.Create(new Ro3Emu.Server.ServerOptions { GameDataDir = Path.Combine(options.RepositoryRoot, "data") }),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<Ro3Emu.Server.Data.GameData>.Instance).Row("ShopCommodityConfig", 113);
        if (commodity is { } row)
        {
            _s3ShopPayment = (int)Ro3Emu.Server.Data.GameData.Number(row.GetProperty("_kItemPerPrice")[0])!;
            _s3ShopGoods = (int)Ro3Emu.Server.Data.GameData.Int(row, "_iItemId");
            _s3ShopGoodsCount = (int)Math.Max(1, Ro3Emu.Server.Data.GameData.Int(row, "_iItemPerMount"));
        }
        report.S3SetupFixtures = _s3Fixtures.ToArray();
        _s3EconomyCatalog = EconomyCatalog.Load(Path.Combine(options.RepositoryRoot, "data"));
        report.S3OpeningEconomy = S3EconomyReader.Read(report.ServerDataDir, _s3EconomyCatalog);
        _s3Prepared = true;
    }

    private async Task S3GmAsync(BotActor actor, string command, CancellationToken ct)
    {
        using var response = await _http.PostAsync("/ro3emu/gm?account=" + Uri.EscapeDataString(actor.Account),
            new StringContent(command), ct);
        response.EnsureSuccessStatusCode();
    }

    private async Task S3WarpAsync(BotActor actor, long scene, float x, float z, CancellationToken ct)
    {
        using var switchScene = actor.Bot.Inbox.Expect(e => e.IsGame(MsgId.SwitchSceneLine));
        using var self = actor.Bot.Inbox.Expect(e => e.IsGame(MsgId.EntitySelf));
        using var end = actor.Bot.Inbox.Expect(e => e.IsGame(MsgId.SelfInfoEndFrameNotify));
        await S3GmAsync(actor, string.Create(CultureInfo.InvariantCulture, $"!warp {scene} {x:F2} {z:F2}"), ct);
        var switched = SwitchSceneLineResp.Parser.ParseFrom((await switchScene.WaitAsync(TimeSpan.FromSeconds(30), ct)).Body);
        if (switched.SceneEntry != scene) throw new InvalidOperationException("S3 gathering warp reached another scene");
        await LoadSceneAsync(actor, switched.SceneEntry, self, end, ct);
    }

    private async Task CloseS3Async(RunReport report, CancellationToken ct)
    {
        // Delivery-diagnostic grace period only. This is NOT a writer fence or economic commit watermark.
        // The reader below binds holdings to its actual durable sequence, not to this delay.
        await Task.Delay(500, ct);
        if (report.ServerDataDir != null)
        {
            _s3EconomyCatalog ??= EconomyCatalog.Load(Path.Combine(options.RepositoryRoot, "data"));
            report.S3ClosingEconomy = S3EconomyReader.Read(report.ServerDataDir, _s3EconomyCatalog);
            if (report.S3OpeningEconomy is { } opening)
                report.S3CommittedLedger = S3EconomyReader.ReadLedger(report.ServerDataDir, _s3EconomyCatalog,
                    opening, report.S3ClosingEconomy);
        }
        report.S3ExpectedExternalChange = _s3Expected.OrderBy(x => x.Key).ToDictionary(x => x.Key, x => x.Value);
        report.S3UntrackedEconomySources = _s3Unknown.Keys.Order().ToArray();
        report.S3GatherCompletions = Volatile.Read(ref _s3GatherCompletions);
        report.S3TowerEntries = Volatile.Read(ref _s3TowerEntries);
        report.S3TowerSettlements = Volatile.Read(ref _s3TowerSettlements);
        report.S3CompletedRun = true;
        _ = report.EvaluateLedgerRow();
    }

    private async Task RunS3Async(RunReport report, CancellationToken ct)
    {
        var c = S3Cohorts(_actors.OrderBy(a => a.Account).ToArray());
        var tasks = new List<Task>
        {
            RunTrafficAsync(c.Town, options.DurationSeconds, chat: true, ct),
            RunFighterTrafficAsync(c.Field.Skip(Math.Min(4, c.Field.Length)).ToArray(), options.DurationSeconds, ct),
            RunMenuTrafficAsync(c.Menu, options.DurationSeconds, ct),
        };
        if (_s3Prepared)
        {
            tasks.Add(RunS3EconomyAsync(c.Town, c.Field, options.DurationSeconds, ct));
            tasks.Add(RunS3GatheringAsync(c.Field.Take(4).ToArray(), options.DurationSeconds, ct));
            tasks.Add(RunS3TowerAsync(c.Tower, options.DurationSeconds, ct));
        }
        else tasks.Add(RunHeartbeatsAsync(c.Tower, options.DurationSeconds, ct));
        await Task.WhenAll(tasks);
        if (c.Town.Length == 0 || c.Field.Length == 0 || c.Tower.Length == 0 || c.Menu.Length == 0)
            S3Unsupported(report, "one or more S3 cohorts empty");
        if (c.Field.Length >= 4 && Volatile.Read(ref _s3GatherCompletions) == 0)
            S3Unsupported(report, "gathering produced no completed collection");
        if (Volatile.Read(ref _s3TowerEntries) < c.Tower.Length)
            S3Unsupported(report, $"tower instance entry completed for {Volatile.Read(ref _s3TowerEntries)}/{c.Tower.Length}");
        if (_counts.Action("observed-loot-sync").Snapshot().Completed == 0)
            S3Unsupported(report, "field loot: no committed item sync observed");
    }

    private async Task<T> S3RequestAsync<T>(BotActor actor, string action, uint id, IMessage request,
        MessageParser<T> parser, Func<T, bool> accepted, CancellationToken ct) where T : IMessage<T>
    {
        var tally = _counts.Action(action); tally.Plan(); tally.Offer(); _counts.Message(id).Plan();
        using var attempt = new ActionAttempt(tally);
        try
        {
            using var reply = actor.Bot.Inbox.Expect(e => e.IsGame(id));
            await actor.Sender!.SendAsync(() => actor.Bot.SendGameAsync(id, request), ct);
            var response = parser.ParseFrom((await reply.WaitAsync(TimeSpan.FromSeconds(10), ct)).Body);
            if (accepted(response)) attempt.Complete(); else attempt.Refuse();
            return response;
        }
        catch (Exception e) { attempt.Fail(e); throw; }
    }

    private async Task RunS3EconomyAsync(BotActor[] town, BotActor[] field, int seconds, CancellationToken ct)
    {
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(ct);
        stop.CancelAfter(TimeSpan.FromSeconds(seconds));
        var work = new List<Task>();
        for (int i = 0; i < Math.Min(4, town.Length / 2); i++)
        {
            int index = i;
            work.Add(Task.Run(async () =>
            {
                try { await S3MarketPairAsync(town[index], town[index + 4], seconds, stop.Token); }
                catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
                catch (Exception e) { _failures[500000 + index] = $"market: {e.GetType().Name}: {e.Message}"; }
            }, ct));
        }
        foreach (var (actor, index) in town.Take(4).Select((a, i) => (a, i)))
            work.Add(Task.Run(async () =>
            {
                try { await S3ClaimFixtureAsync(actor, stop.Token); }
                catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
                catch (Exception e) { _failures[510000 + index] = $"mail: {e.GetType().Name}: {e.Message}"; }
            }, ct));
        foreach (var (actor, index) in town.Skip(4).Take(4).Select((a, i) => (a, i)))
            work.Add(Task.Run(async () =>
            {
                try { await S3ShopAsync(actor, stop.Token); }
                catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
                catch (Exception e) { _failures[520000 + index] = $"shop: {e.GetType().Name}: {e.Message}"; }
            }, ct));
        // Repeated read routes exercise the live market and stall projections without minting resources.
        foreach (var (actor, index) in town.Skip(8).Take(20).Select((a, i) => (a, i)))
            work.Add(Task.Run(async () =>
            {
                try
                {
                    int slots = Math.Max(1, seconds / 30);
                    for (int slot = 0; slot < slots && !stop.IsCancellationRequested; slot++)
                    {
                        await S3RequestAsync(actor, "exchange-browse", TradeService.QueryProduct,
                            new SQueryProductByParamRequest { ItemId = "1254013", PageNo = 1, PageSize = 10 },
                            CQueryProductByParamWithPageResp.Parser, r => r.Errno == 0, stop.Token);
                        await Task.Delay(TimeSpan.FromSeconds(30), stop.Token);
                    }
                }
                catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
                catch (Exception e) { _failures[530000 + index] = $"browse: {e.GetType().Name}: {e.Message}"; }
            }, ct));
        foreach (var (actor, index) in field.Skip(4).Take(20).Select((a, i) => (a, i)))
            work.Add(Task.Run(async () =>
            {
                try
                {
                    int slots = Math.Max(1, seconds / 30);
                    for (int slot = 0; slot < slots && !stop.IsCancellationRequested; slot++)
                    {
                        await S3RequestAsync(actor, "stall-browse", StallsService.View,
                            new PlayerStallsSyncBuyerStallsInfoReq
                            { SystemStallID = 1, SceneEntryID = 10111, SceneLineNum = actor.LineNumber },
                            PlayerStallsSyncBuyerStallsInfoResp.Parser, r => r.Result == 0, stop.Token);
                        await Task.Delay(TimeSpan.FromSeconds(30), stop.Token);
                    }
                }
                catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
                catch (Exception e) { _failures[535000 + index] = $"stall browse: {e.GetType().Name}: {e.Message}"; }
            }, ct));
        await Task.WhenAll(work);
    }

    private async Task S3MarketPairAsync(BotActor seller, BotActor buyer, int seconds, CancellationToken ct)
    {
        long guid = S3ItemGuid(seller.RoleId, 1254013);
        var listed = await S3RequestAsync(seller, "exchange-list", TradeService.PutOn,
            new TradePutOnItemReq { ItemGuid = guid, ItemPrice = 600, CurrencyType = 1, Quantity = 2 },
            TradePutOnItemResp.Parser, r => r.Result == 0, ct);
        if (listed.Result != 0) return;
        _s3Expected.AddOrUpdate(2, -(long)listed.Tax, (_, n) => n - (long)listed.Tax);
        var commodity = listed.BoothInfos.FirstOrDefault(x => x.ItemId == "1254013");
        if (commodity == null) throw new InvalidDataException("Successful listing omitted 1254013 booth information");
        var page = await S3RequestAsync(buyer, "exchange-browse", TradeService.QueryProduct,
            new SQueryProductByParamRequest { ItemId = "1254013", PageNo = 1, PageSize = 10 },
            CQueryProductByParamWithPageResp.Parser, r => r.Errno == 0, ct);
        if (page.Errno != 0) return;
        for (int unit = 0; unit < 2; unit++)
        {
            if (unit == 1) await Task.Delay(TimeSpan.FromSeconds(Math.Max(1, seconds / 2)), ct);
            var bought = await S3RequestAsync(buyer, "exchange-buy", TradeService.BuyNormal,
                new TradeBuyNormalItemsReq
                {
                    ItemId = 1254013, SpuId = commodity.SpuId,
                    Price = 600, TotalPrice = 600, TotalQuantity = 1
                },
                TradeBuyNormalItemsResp.Parser, r => r.Result == 0, ct);
            if (bought.Result != 0) break;
            // Currency 1 has zero MarketFee; the purchase moves 600 to seller mail and one item to buyer mail.
        }
    }

    private long S3ItemGuid(long role, int entry)
    {
        string? dir = options.ServerLocal ? _s3DataDir : null;
        if (dir == null) throw new InvalidOperationException("Trade listing needs local fixture database");
        using var db = new SqliteConnection(new SqliteConnectionStringBuilder
        { DataSource = Path.Combine(dir, "ro3emu.db"), Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
        db.Open();
        using var cmd = db.CreateCommand();
        cmd.CommandText = "SELECT guid FROM item WHERE role_id=$role AND entry=$entry ORDER BY guid LIMIT 1";
        cmd.Parameters.AddWithValue("$role", role); cmd.Parameters.AddWithValue("$entry", entry);
        return Convert.ToInt64(cmd.ExecuteScalar() ?? throw new InvalidDataException("S3 seller item missing"), CultureInfo.InvariantCulture);
    }

    private string? _s3DataDir;

    private async Task S3ClaimFixtureAsync(BotActor actor, CancellationToken ct)
    {
        var response = await actor.Bot.MicroAsync("MailService", "getUserMailList", new()
        { ["Uid"] = actor.RoleId.ToString(CultureInfo.InvariantCulture), ["Start"] = 0, ["Limit"] = 150, ["MailType"] = 1 });
        if (response.GetValueOrDefault("MailList") is not object?[] letters)
            throw new InvalidDataException("Mail list response missing MailList");
        string? id = letters.OfType<Dictionary<object, object?>>()
            .Where(x => x.GetValueOrDefault("Title")?.ToString() == "S3_claim_probe")
            .Select(x => x.GetValueOrDefault("MailId")?.ToString()).FirstOrDefault();
        if (id == null) throw new InvalidDataException("S3 fixture mail missing");
        await S3RequestAsync(actor, "mail-claim", MailService.ClaimMail,
            new ClaimMailReq { MailUid = { id } }, ClaimMailRsp.Parser, r => r.Result == 0 && r.MailUid.Contains(id), ct);
    }

    private async Task S3ShopAsync(BotActor actor, CancellationToken ct)
    {
        var result = await S3RequestAsync(actor, "npc-shop-buy", ShopService.BuyCommodity,
            new BuyCommodityReq { CommodityId = 113, Count = 1 }, BuyCommodityResp.Parser, r => r.Result == 0, ct);
        if (result.Result != 0) return;
        if (_s3ShopPayment == 0 || _s3ShopGoods == 0) { _s3Unknown.TryAdd("shop commodity configuration missing", 0); return; }
        _s3Expected.AddOrUpdate(_s3ShopPayment, -result.Price, (_, n) => n - result.Price);
        _s3Expected.AddOrUpdate(_s3ShopGoods, _s3ShopGoodsCount, (_, n) => n + _s3ShopGoodsCount);
    }

    private async Task RunS3GatheringAsync(BotActor[] gatherers, int seconds, CancellationToken ct)
    {
        if (gatherers.Length == 0) return;
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(ct);
        stop.CancelAfter(TimeSpan.FromSeconds(seconds));
        // Stop initiating collection with enough time for the final authored interaction to settle.
        // A cancelled wait can leave a server-side reward pending beyond the closing SQLite snapshot.
        var collectionClock = Stopwatch.StartNew();
        var lastStart = TimeSpan.FromSeconds(Math.Max(0, seconds - 25));
        var tasks = gatherers.Select(async (actor, index) =>
        {
            bool awaitingCollection = false;
            try
            {
                while (!stop.IsCancellationRequested && collectionClock.Elapsed < lastStart)
                {
                    var node = actor.Reducer.GatheringNodes().Where(x => x.EntityTemplateID == 7010001)
                        .OrderBy(x => Distance(actor, x)).FirstOrDefault(x => _s3GatherClaims.TryAdd(x.Uuid, 0));
                    if (node == null) { await Task.Delay(2000, stop.Token); continue; }
                    try
                    {
                        float distance = Distance(actor, node);
                        while (distance > 2.5f && !stop.IsCancellationRequested)
                        {
                            var pos = node.MovementInfo.Pos;
                            float step = MathF.Min(distance - 1.5f, 2.5f);
                            float x = actor.X + (pos.X - actor.X) / distance * step;
                            float z = actor.Z + (pos.Z - actor.Z) / distance * step;
                            await actor.Sender!.SendAsync(() => actor.Bot.WalkAsync(x, actor.Y, z, stop: true), stop.Token);
                            actor.X = x; actor.Z = z;
                            await Task.Delay(600, stop.Token);
                            distance = Distance(actor, node);
                        }
                        if (collectionClock.Elapsed >= lastStart) break;
                        using var complete = actor.Bot.Inbox.Expect(e => e.IsGame(GatheringService.StateChange) &&
                            InteractChangeNtf.Parser.ParseFrom(e.Body).DataInfo.EType == InteractInfo.Types.EState.Idle);
                        var interaction = await S3RequestAsync(actor, "gather-start", GatheringService.Interaction,
                            new NpcInteractionReq
                            {
                                NpcUid = (long)node.Uuid, InteractionId = 70100001,
                                SceneConfigId = actor.Scene, Pos = node.MovementInfo.Pos
                            },
                            NpcInteractionRsp.Parser, r => r.Result == 0, stop.Token);
                        if (interaction.Result == 0)
                        {
                            awaitingCollection = true;
                            var done = InteractChangeNtf.Parser.ParseFrom((await complete.WaitAsync(TimeSpan.FromSeconds(20), stop.Token)).Body);
                            awaitingCollection = false;
                            if (!done.Interrupt)
                            {
                                Interlocked.Increment(ref _s3GatherCompletions);
                                _s3Unknown.TryAdd("gathering rewards: random award choice has no independent event journal", 0);
                            }
                        }
                    }
                    finally { _s3GatherClaims.TryRemove(node.Uuid, out _); }
                    await Task.Delay(2500, stop.Token);
                }
            }
            catch (OperationCanceledException) when (stop.IsCancellationRequested)
            {
                if (awaitingCollection) _s3Unknown.TryAdd("gathering interaction still in flight at closing snapshot", 0);
            }
            catch (Exception e)
            {
                if (awaitingCollection) _s3Unknown.TryAdd("gathering interaction may settle after closing snapshot", 0);
                _failures[540000 + index] = $"gather: {e.GetType().Name}: {e.Message}";
            }
        });
        await Task.WhenAll(tasks);
    }

    private async Task RunS3TowerAsync(BotActor[] actors, int seconds, CancellationToken ct)
    {
        if (actors.Length == 0) return;
        using var gate = new SemaphoreSlim(8);
        var entries = actors.Select(async (actor, index) =>
        {
            await gate.WaitAsync(ct);
            try
            {
                using var switched = actor.Bot.Inbox.Expect(e => e.IsGame(MsgId.SwitchSceneLine));
                using var self = actor.Bot.Inbox.Expect(e => e.IsGame(MsgId.EntitySelf));
                using var end = actor.Bot.Inbox.Expect(e => e.IsGame(MsgId.SelfInfoEndFrameNotify));
                var reply = await S3RequestAsync(actor, "tower-entry", TowerService.Start,
                    new ClimTowerStartReq { Layer = 1, IsInClimTower = false }, ClimTowerStartRes.Parser,
                    r => r.Result == 0, ct);
                if (reply.Result != 0) return;
                var scene = SwitchSceneLineResp.Parser.ParseFrom((await switched.WaitAsync(TimeSpan.FromSeconds(30), ct)).Body);
                if (scene.SceneEntry != 2100001) throw new InvalidDataException("Tower entry switched to unexpected map");
                await LoadSceneAsync(actor, scene.SceneEntry, self, end, ct);
                Interlocked.Increment(ref _s3TowerEntries);
            }
            catch (Exception e) { _failures[550000 + index] = $"tower: {e.GetType().Name}: {e.Message}"; }
            finally { gate.Release(); }
        });
        await Task.WhenAll(entries);
        await RunFighterTrafficAsync(actors, seconds, ct);
    }
}

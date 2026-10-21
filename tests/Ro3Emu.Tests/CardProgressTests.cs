using System.Net.Sockets;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Romsg;
using Ro3Emu.Bot;
using Ro3Emu.Server;
using Ro3Emu.Server.Accounts;
using Ro3Emu.Server.Game;

namespace Ro3Emu.Tests;

/// <summary>Real-wire card progression and saved socket plans.</summary>
public sealed class CardProgressTests : IAsyncLifetime
{
    private const int Lunatic = 1420100, Fragment = 1200500, Dagger = 2010121;
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(5);
    private readonly string _dataDir = Path.Combine(Path.GetTempPath(), "ro3emu-card-" + Guid.NewGuid().ToString("N"));
    private WebApplication _app = null!;
    private string _httpUrl = "";

    public async Task InitializeAsync()
    {
        var (http, micro, game) = TestPorts.FreePorts();
        _app = ServerHost.Create([], o =>
        {
            o.DataDir = _dataDir;
            o.GameDataDir = Path.Combine(RepoRoot(), "data");
            o.HttpPort = http;
            o.MicroGatePort = micro;
            o.GameGatePort = game;
        });
        await _app.StartAsync();
        _httpUrl = $"http://127.0.0.1:{http}";
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
        var bot = new BotClient(_httpUrl, File.ReadAllText(Path.Combine(_dataDir, "gate_rsa_public.pem")));
        await bot.LoginAsync(name.ToLowerInvariant(), "pw");
        await bot.EnterGateAsync();
        long id = create ? (await bot.CreateRoleAsync(name)).RoleBaseInfo.Id : bot.Roles.Single().Id;
        await bot.EnterWorldAsync(id);
        return bot;
    }

    private static async Task<T> Next<T>(BotClient bot, uint id, Google.Protobuf.MessageParser<T> parser, Func<T, bool>? match = null)
        where T : Google.Protobuf.IMessage<T>
    {
        var evt = await bot.Inbox.WaitAsync(e => e.IsGame(id) && (match == null || match(parser.ParseFrom(e.Body))), Wait);
        return parser.ParseFrom(evt.Body);
    }

    private async Task Gm(string account, string command)
    {
        using var http = new HttpClient();
        (await http.PostAsync($"{_httpUrl}/ro3emu/gm?account={account}", new StringContent(command))).EnsureSuccessStatusCode();
    }

    private static ItemBaseInfo FullCard(DBItemsInfoIncrement sync, long guid = 0)
    {
        var cards = sync.PackInfo[3].All.Items.Values.Where(i => i.ItemEntry == Lunatic);
        return guid == 0 ? cards.First() : cards.Single(i => i.ItemGuid == guid);
    }

    [ClientDataFact]
    public async Task HeldExchangeCommitKeepsCardGrantAndLibraryInvisibleWhileLoopAdvances()
    {
        await using var alice = await InWorld("Alice");
        long role = (long)alice.Self!.Uuid;
        var accounts = _app.Services.GetRequiredService<AccountStore>();
        var roles = _app.Services.GetRequiredService<RoleStore>();
        var items = _app.Services.GetRequiredService<ItemStore>();
        var loop = _app.Services.GetRequiredService<GameLoop>();
        var session = _app.Services.GetRequiredService<SessionRegistry>().All().Single(s => s.Role?.Id == role);
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        accounts.BeforeCommitForTest = label =>
        {
            if (label != "character/exchange") return;
            entered.Set();
            if (!release.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException("exchange commit barrier");
        };
        try
        {
            var exchange = loop.InvokeAsync(() => _app.Services.GetRequiredService<CharacterService>()
                .ExchangeAsync(session, [], [(Lunatic, 1)], popup: false, sync: false));
            Assert.True(await Task.Run(() => entered.Wait(Wait)));
            Assert.False(exchange.IsCompleted);
            Assert.False(await loop.InvokeAsync(() => Task.FromResult(session.Items.Any(i => i.Entry == Lunatic))));
            Assert.DoesNotContain(items.Load(role), i => i.Entry == Lunatic);
            Assert.False(roles.LoadLibrary(role) is { } bytes && DBLibraryInfo.Parser.ParseFrom(bytes).Cards.ContainsKey(Lunatic));
            Assert.Equal(role, await loop.InvokeAsync(() => Task.FromResult(session.Role!.Id)));
            release.Set();
            Assert.Single((await exchange)!);
            Assert.Contains(items.Load(role), i => i.Entry == Lunatic);
            Assert.True(DBLibraryInfo.Parser.ParseFrom(roles.LoadLibrary(role)).Cards.ContainsKey(Lunatic));
        }
        finally { release.Set(); accounts.BeforeCommitForTest = null; }
    }

    [ClientDataFact]
    public async Task HeldTaskFollowupClearKeepsDebtAndUnlockReplyPendingWhileLoopAdvances()
    {
        await using var alice = await InWorld("Alice");
        var data = _app.Services.GetRequiredService<Ro3Emu.Server.Data.GameData>();
        int card = (int)Ro3Emu.Server.Data.GameData.Int(data.Table("ItemConfig").Values.First(row =>
            Ro3Emu.Server.Data.GameData.Int(row, "_iType") == 4 &&
            Ro3Emu.Server.Data.GameData.Int(row, "_iSubType") == 400 &&
            Ro3Emu.Server.Data.GameData.Int(row, "_iQuality") >= 4), "_iID");
        await Gm("alice", "!quest accept 1700212");
        await Gm("alice", $"!item {card} 1");
        long role = (long)alice.Self!.Uuid;
        var accounts = _app.Services.GetRequiredService<AccountStore>();
        var roles = _app.Services.GetRequiredService<RoleStore>();
        var loop = _app.Services.GetRequiredService<GameLoop>();
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        accounts.BeforeCommitForTest = label =>
        {
            if (label != "task/settled-followup") return;
            entered.Set();
            if (!release.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException("task followup clear barrier");
        };
        try
        {
            var reply = Next(alice, CardService.LibraryCardUnlock, LibraryCardUnlockResp.Parser);
            await alice.SendGameAsync(CardService.LibraryCardUnlock, new LibraryCardUnlockReq { CardId = card, IsTreasure = true });
            Assert.True(await Task.Run(() => entered.Wait(Wait)));
            Assert.False(reply.IsCompleted);
            Assert.True(roles.HasTaskFollowup(role));
            Assert.Equal(role, await loop.InvokeAsync(() => Task.FromResult(role)));
            release.Set();
            Assert.Equal(0, (await reply).Result);
            Assert.False(roles.HasTaskFollowup(role));
        }
        finally { release.Set(); accounts.BeforeCommitForTest = null; }
    }

    [ClientDataFact]
    public async Task CommittedLibraryUnlockStillAcknowledgesWhenCollectionSubscriberFails()
    {
        await using var alice = await InWorld("Alice");
        await Gm("alice", $"!item {Lunatic} 1");
        var cards = _app.Services.GetRequiredService<CardService>();
        int callbacks = 0;
        Func<GameSession, Task> failing = _ =>
        {
            Interlocked.Increment(ref callbacks);
            return Task.FromException(new InvalidOperationException("injected collection failure"));
        };
        cards.CollectionChanged.Add(failing);
        try
        {
            await alice.SendGameAsync(CardService.LibraryCardUnlock, new LibraryCardUnlockReq { CardId = Lunatic });
            Assert.Equal(0, (await Next(alice, CardService.LibraryCardUnlock, LibraryCardUnlockResp.Parser)).Result);
            Assert.Equal(1, Volatile.Read(ref callbacks));
            var bytes = _app.Services.GetRequiredService<RoleStore>().LoadLibrary((long)alice.Self!.Uuid);
            Assert.NotNull(bytes);
            Assert.True(DBLibraryInfo.Parser.ParseFrom(bytes).Cards[Lunatic].Unlock);
        }
        finally { cards.CollectionChanged.Remove(failing); }
    }

    [ClientDataFact]
    public async Task FlashSynthesisConsumesFourCopiesAndPersistsFlashLibraryAcrossRelog()
    {
        const int ingredient = 1440101; // eligible quality 4, FlashCardOutputConfig 10001
        long outputGuid;
        int outputEntry;
        await using (var alice = await InWorld("Alice"))
        {
            await Gm("alice", $"!item {ingredient} 7");
            var bag = await Next(alice, CharacterService.ItemsSync, DBItemsInfoIncrement.Parser,
                m => m.PackInfo.TryGetValue(3, out var p) && p.All?.Items.Values.Any(i => i.ItemEntry == ingredient) == true);
            long normalGuid = bag.PackInfo[3].All!.Items.Values.Single(i => i.ItemEntry == ingredient).ItemGuid;
            var four = new CardTreasuredReq { SourceType = (CardLevelSourceType)1 };
            four.Guids.Add(new long[] { normalGuid, normalGuid, normalGuid, normalGuid });
            await alice.SendGameAsync(CardService.Treasured, four);
            Assert.Equal(ErrorCodes.FeatureUnavailable, (await Next(alice, CardService.Treasured, CardTreasuredResp.Parser)).Result);
            await Gm("alice", "!lvl 80");
            await alice.SendGameAsync(CardService.Treasured, four);
            var changed = await Next(alice, CharacterService.ItemsSync, DBItemsInfoIncrement.Parser,
                m => m.PackInfo.TryGetValue(3, out var p) && p.Inc?.Items.ContainsKey(normalGuid) == true);
            Assert.True(changed.PackInfo[3].Inc.Items.ContainsKey(normalGuid));
            var result = await Next(alice, CardService.Treasured, CardTreasuredResp.Parser);
            Assert.Equal(0, result.Result);
            outputGuid = result.Guid;
            outputEntry = (int)result.Itemid;
            Assert.True(outputGuid > 0);
            var pool = _app.Services.GetRequiredService<Ro3Emu.Server.Data.GameData>().Table("FlashCardPoolConfig").Values
                .Where(row => Ro3Emu.Server.Data.GameData.Int(row, "_iGroupID") == 1004)
                .SelectMany(row => row.GetProperty("_kCardList").EnumerateArray().Select(x => x.GetInt32())).ToHashSet();
            Assert.Contains(outputEntry, pool);
            Assert.Equal(3, _app.Services.GetRequiredService<SessionRegistry>().All()
                .Single(s => s.Role?.Id == (long)alice.Self!.Uuid).Items.Single(i => i.Guid == normalGuid).Stack);

            // Lua presents a confirmation for a Flash ingredient, then sends the same 31469 request.
            var withFlash = new CardTreasuredReq { SourceType = (CardLevelSourceType)1 };
            withFlash.Guids.Add(new long[] { outputGuid, normalGuid, normalGuid, normalGuid });
            await alice.SendGameAsync(CardService.Treasured, withFlash);
            Assert.Equal(0, (await Next(alice, CardService.Treasured, CardTreasuredResp.Parser,
                x => x.Guid != outputGuid)).Result);
        }
        await using var relog = await InWorld("Alice", create: false);
        var saved = await Next(relog, CharacterService.ItemsSync, DBItemsInfoIncrement.Parser);
        var flashes = saved.PackInfo[3].All.Items.Values.Where(i => i.ItemInfos.GetValueOrDefault(530)?.LongVal == 1).ToArray();
        Assert.Single(flashes);
        Assert.Equal(1, flashes[0].Stack);
        Assert.Equal(0, flashes[0].ItemInfos[501].LongVal);
        Assert.Equal(0, flashes[0].ItemInfos[502].LongVal);
        Assert.DoesNotContain(saved.PackInfo[3].All.Items.Values, i => i.ItemEntry == ingredient && i.ItemInfos.GetValueOrDefault(530)?.LongVal != 1);
        var library = _app.Services.GetRequiredService<RoleStore>().LoadLibrary((long)relog.Self!.Uuid);
        Assert.NotNull(library);
        Assert.True(DBLibraryInfo.Parser.ParseFrom(library).Cards.ContainsKey(flashes[0].ItemEntry));
    }

    [ClientDataFact]
    public async Task FlashSynthesisRejectsInvalidInputsAndRollsBackSqlFailureForReplay()
    {
        const int ingredient = 1440101, otherQuality = 1450100;
        await using var alice = await InWorld("Alice");
        await Gm("alice", "!lvl 80");
        await Gm("alice", $"!item {ingredient} 4");
        await Gm("alice", $"!item {otherQuality} 1");
        var bag = await Next(alice, CharacterService.ItemsSync, DBItemsInfoIncrement.Parser,
            m => m.PackInfo.TryGetValue(3, out var p) && p.All?.Items.Values.Any(i => i.ItemEntry == ingredient) == true);
        var session = _app.Services.GetRequiredService<SessionRegistry>().All().Single(s => s.Role?.Id == (long)alice.Self!.Uuid);
        long normalGuid = session.Items.Single(i => i.Entry == ingredient).Guid;
        long mixedGuid = session.Items.Single(i => i.Entry == otherQuality).Guid;
        async Task<int> Send(int source, params long[] guids)
        {
            var req = new CardTreasuredReq { SourceType = (CardLevelSourceType)source };
            req.Guids.Add(guids);
            await alice.SendGameAsync(CardService.Treasured, req);
            return (await Next(alice, CardService.Treasured, CardTreasuredResp.Parser)).Result;
        }
        Assert.NotEqual(0, await Send(4, normalGuid, normalGuid, normalGuid, normalGuid));
        Assert.NotEqual(0, await Send(1, normalGuid, normalGuid, normalGuid));
        Assert.Equal(ErrorCodes.ItemMissing, await Send(1, long.MaxValue, normalGuid, normalGuid, normalGuid));
        Assert.Equal(ErrorCodes.ItemsShort, await Send(1, mixedGuid, mixedGuid, mixedGuid, mixedGuid));
        Assert.NotEqual(0, await Send(1, normalGuid, normalGuid, normalGuid, mixedGuid));
        await _app.Services.GetRequiredService<GameLoop>().InvokeAsync(() =>
        {
            var card = session.Items.Single(i => i.Guid == normalGuid);
            card.IsLocked = true;
            _app.Services.GetRequiredService<ItemStore>().Update(card);
            return Task.CompletedTask;
        });
        Assert.Equal(ErrorCodes.ItemLocked, await Send(1, normalGuid, normalGuid, normalGuid, normalGuid));
        await _app.Services.GetRequiredService<GameLoop>().InvokeAsync(() =>
        {
            var card = session.Items.Single(i => i.Guid == normalGuid);
            card.IsLocked = false;
            card.Attrs ??= new();
            card.Attrs[502] = 1;
            _app.Services.GetRequiredService<ItemStore>().Update(card);
            return Task.CompletedTask;
        });
        Assert.NotEqual(0, await Send(1, normalGuid, normalGuid, normalGuid, normalGuid));
        await _app.Services.GetRequiredService<GameLoop>().InvokeAsync(() =>
        {
            var card = session.Items.Single(i => i.Guid == normalGuid);
            card.Attrs![502] = 0;
            _app.Services.GetRequiredService<ItemStore>().Update(card);
            return Task.CompletedTask;
        });
        FixtureSql.Execute(_app.Services.GetRequiredService<AccountStore>(),
            "CREATE TRIGGER fail_flash BEFORE INSERT ON item WHEN json_extract(NEW.attrs, '$.530') = 1 BEGIN SELECT RAISE(ABORT, 'flash grant fault'); END");
        Assert.Equal(ErrorCodes.Unknown, await Send(1, normalGuid, normalGuid, normalGuid, normalGuid));
        Assert.Equal(4, session.Items.Single(i => i.Guid == normalGuid).Stack);
        FixtureSql.Execute(_app.Services.GetRequiredService<AccountStore>(), "DROP TRIGGER fail_flash");
        var roles = _app.Services.GetRequiredService<RoleStore>();
        byte[]? beforeLibrary = roles.LoadLibrary((long)alice.Self!.Uuid);
        FixtureSql.Execute(_app.Services.GetRequiredService<AccountStore>(),
            "CREATE TRIGGER fail_flash_library BEFORE UPDATE OF library ON role BEGIN SELECT RAISE(ABORT, 'flash library fault'); END");
        Assert.Equal(ErrorCodes.Unknown, await Send(1, normalGuid, normalGuid, normalGuid, normalGuid));
        Assert.Equal(4, session.Items.Single(i => i.Guid == normalGuid).Stack);
        Assert.Equal(beforeLibrary, roles.LoadLibrary((long)alice.Self.Uuid));
        FixtureSql.Execute(_app.Services.GetRequiredService<AccountStore>(), "DROP TRIGGER fail_flash_library");
        Assert.Equal(0, await Send(1, normalGuid, normalGuid, normalGuid, normalGuid));
        Assert.DoesNotContain(session.Items, i => i.Guid == normalGuid);
        await Gm("alice", $"!item {ingredient} 4");
        var gray = session.Items.Single(i => i.Entry == ingredient && (i.Attrs?.GetValueOrDefault(530) ?? 0) == 0);
        await _app.Services.GetRequiredService<GameLoop>().InvokeAsync(() =>
        {
            gray.Attrs ??= new();
            gray.Attrs[530] = 2; // Client selection has no card-type filter (LC_CardViewHelper.lua:528-608).
            gray.BindType = 1;
            _app.Services.GetRequiredService<ItemStore>().Update(gray);
            return Task.CompletedTask;
        });
        var grayReq = new CardTreasuredReq { SourceType = (CardLevelSourceType)1 };
        grayReq.Guids.Add(new long[] { gray.Guid, gray.Guid, gray.Guid, gray.Guid });
        await alice.SendGameAsync(CardService.Treasured, grayReq);
        var grayResult = await Next(alice, CardService.Treasured, CardTreasuredResp.Parser);
        Assert.Equal(0, grayResult.Result);
        var boundFlash = session.Items.Single(i => i.Guid == grayResult.Guid);
        Assert.Equal(1, boundFlash.BindType); // permanent input remains permanent after synthesis
        Assert.Equal(1, boundFlash.Attrs![530]);
    }

    [ClientDataFact]
    public async Task LevelStarAndRevertPayOnceAndRememberHistoricalCosts()
    {
        long guid;
        await using (var alice = await InWorld("Alice"))
        {
            await Gm("alice", $"!item {Lunatic} 3");
            await Gm("alice", $"!item {Fragment} 170");
            await Gm("alice", "!item 2 10000");
            var gained = await Next(alice, CharacterService.ItemsSync, DBItemsInfoIncrement.Parser,
                m => m.PackInfo.TryGetValue(3, out var p) && (p.All?.Items.Values.Any(i => i.ItemEntry == Lunatic) == true
                    || p.Inc?.Items.Values.Any(i => i.All?.ItemEntry == Lunatic) == true));
            guid = gained.PackInfo[3].All?.Items.Values.First(i => i.ItemEntry == Lunatic).ItemGuid
                ?? gained.PackInfo[3].Inc.Items.Values.First(i => i.All?.ItemEntry == Lunatic).All.ItemGuid;

            await alice.SendGameAsync(CardService.LevelUp, new CardLevelUpReq { SourceType = (CardLevelSourceType)1, Guid = guid });
            Assert.Equal(0, (await Next(alice, CardService.LevelUp, CardLevelUpResp.Parser)).Result);
            var paid = await Next(alice, CharacterService.ItemsSync, DBItemsInfoIncrement.Parser,
                m => m.PackInfo.TryGetValue(3, out var p) && p.Inc?.Items.ContainsKey(guid) == true);
            Assert.True(paid.PackInfo[3].Inc.Items.ContainsKey(guid));

            await alice.SendGameAsync(CardService.StarUp, new CardStarUpReq { SourceType = (CardLevelSourceType)1, Guid = guid });
            Assert.Equal(1, (await Next(alice, CardService.StarUp, CardStarUpResp.Parser)).NewStarNum);
            await Next(alice, CharacterService.ItemsSync, DBItemsInfoIncrement.Parser,
                m => m.PackInfo.TryGetValue(3, out var p) && p.Inc?.Items.ContainsKey(guid) == true);

            // A failed second level-up cannot take 225 fragments or change the target.
            await alice.SendGameAsync(CardService.LevelUp, new CardLevelUpReq { SourceType = (CardLevelSourceType)1, Guid = guid });
            Assert.Equal(ErrorCodes.ItemsShort, (await Next(alice, CardService.LevelUp, CardLevelUpResp.Parser)).Result);
        }

        await using (var relog = await InWorld("Alice", create: false))
        {
            var bag = await Next(relog, CharacterService.ItemsSync, DBItemsInfoIncrement.Parser);
            var upgraded = FullCard(bag, guid);
            Assert.Equal(1, upgraded.ItemInfos[501].LongVal);
            Assert.Equal(1, upgraded.ItemInfos[502].LongVal);
            Assert.Equal($"{Fragment}|170|0|0|0", upgraded.ItemInfos[510].StrVal);
            Assert.Equal($"{Lunatic}|1|0|0|0", upgraded.ItemInfos[520].StrVal);
            Assert.Equal(2, bag.PackInfo[3].All.Items.Values.Where(i => i.ItemEntry == Lunatic).Sum(i => i.Stack));
            Assert.DoesNotContain(bag.PackInfo.Values.SelectMany(p => p.All?.Items.Values ?? []), i => i.ItemEntry == Fragment);

            await relog.SendGameAsync(CardService.Revert, new CardRevertReq { CardGuid = guid });
            var result = await Next(relog, CardService.Revert, CardRevertResp.Parser);
            Assert.Equal(0, result.Result);
            Assert.Contains(result.RevertCards, i => i.ItemEntry == Fragment && i.Stack == 170);
            Assert.Contains(result.RevertCards, i => i.ItemEntry == Lunatic && i.Stack == 1);
            Assert.Equal(2, result.RevertCards.Where(i => i.ItemEntry == Lunatic).Sum(i => i.Stack)); // copy + original
            await Next(relog, CharacterService.ItemsSync, DBItemsInfoIncrement.Parser,
                m => m.Currency.ContainsKey(2) && m.Currency[2].All == 0 || m.Currency.ContainsKey(2) && m.Currency[2].Inc == 0);
        }

        await using var again = await InWorld("Alice", create: false);
        var final = await Next(again, CharacterService.ItemsSync, DBItemsInfoIncrement.Parser);
        Assert.Equal(3, final.PackInfo[3].All.Items.Values.Where(i => i.ItemEntry == Lunatic).Sum(i => i.Stack));
        Assert.Equal(170, final.PackInfo.Values.SelectMany(p => p.All?.Items.Values ?? []).Where(i => i.ItemEntry == Fragment).Sum(i => i.Stack));
        Assert.Equal(0, final.Currency[2].All);
        Assert.Equal(0, FullCard(final, guid).ItemInfos[501].LongVal);
        Assert.False(FullCard(final, guid).ItemInfos.ContainsKey(510));
    }

    [ClientDataFact]
    public async Task GenreCardSelectionKeepsMastersMissingCopyPolicy()
    {
        await using var alice = await InWorld("Alice");
        var session = _app.Services.GetRequiredService<SessionRegistry>().All().Single(s => s.Role?.Name == "Alice");
        long dagger = session.Items.Single(i => i.Entry == Dagger).Guid;
        await Gm("alice", "!lvl 40");
        await Gm("alice", $"!item {Lunatic} 1");
        long guid = session.Items.Single(i => i.Entry == Lunatic).Guid;
        await alice.SendGameAsync(CharacterService.EquipItemOpr,
            new EquipItemOprReq { Opr = 0, Pos = (EquipGridType)1, ItemGUID = dagger });
        Assert.Equal(0, (await Next(alice, CharacterService.EquipItemOpr, EquipItemOprResp.Parser)).Result);
        await alice.SendGameAsync(CardService.SlotInsert,
            new EquipmentCardInsertReq { EquipPos = (EquipGridType)1, SlotIndex = 1, CardGuid = guid });
        Assert.Equal(0, (await Next(alice, CardService.SlotInsert, EquipmentCardInsertResp.Parser)).Result);
        await alice.SendGameAsync(CardService.SwitchPlan, new CardSwitchPlanReq { TargetIndex = 2 });
        Assert.Equal(0, (await Next(alice, CardService.SwitchPlan, CardSwitchPlanResp.Parser)).Result);
        Assert.Equal(Lunatic, session.CardPlans!.StoragePlans[1].CardInfo[1].CardSlotInfo[1].ItemId);
        await Gm("alice", $"!delitem {Lunatic} 1");
        Assert.DoesNotContain(session.Items, i => i.Entry == Lunatic);

        await alice.SendGameAsync(82806, new SwitchGenrePlanReq
        { TargetPlan = 1, IsApply = true, ChildSystem = { [4] = 1 } });
        Assert.Equal(0, (await Next(alice, 82806, SwitchGenrePlanResp.Parser)).Result);
        Assert.Equal(1, session.CardPlans.CurrentPlan);
        Assert.DoesNotContain(session.Items, i => i.Grid < 0);
        Assert.All(session.CardPlans.StoragePlans[1].CardInfo.Values.SelectMany(g => g.CardSlotInfo.Values),
            slot => Assert.Equal(0, slot.ItemId));
        var saved = _app.Services.GetRequiredService<RoleStore>().LoadCardPlans(session.Role!.Id);
        Assert.NotNull(saved);
        Assert.Equal(1, DBStorageHoleInfo.Parser.ParseFrom(saved).CurrentPlan);
    }

    [ClientDataFact]
    public async Task PlanSwitchMovesOneCardAndNameAndSelectionSurviveRelog()
    {
        await using (var alice = await InWorld("Alice"))
        {
            var initial = await Next(alice, CardService.StorageHoleSync, DBStorageHoleInfoIncrement.Parser);
            Assert.Equal(5, initial.StoragePlans.Count);
            var bag = await Next(alice, CharacterService.ItemsSync, DBItemsInfoIncrement.Parser);
            long dagger = bag.PackInfo[2].All.Items.Values.Single(i => i.ItemEntry == Dagger).ItemGuid;
            await Gm("alice", "!lvl 20");
            await Gm("alice", $"!item {Lunatic} 2");
            var cards = await Next(alice, CharacterService.ItemsSync, DBItemsInfoIncrement.Parser,
                m => m.PackInfo.TryGetValue(3, out var p) && p.All?.Items.Values.Any(i => i.ItemEntry == Lunatic) == true);
            long guid = FullCard(cards).ItemGuid;
            await alice.SendGameAsync(CharacterService.EquipItemOpr, new EquipItemOprReq { Opr = 0, Pos = (EquipGridType)1, ItemGUID = dagger });
            Assert.Equal(0, (await Next(alice, CharacterService.EquipItemOpr, EquipItemOprResp.Parser)).Result);
            await alice.SendGameAsync(CardService.SlotInsert, new EquipmentCardInsertReq { EquipPos = (EquipGridType)1, SlotIndex = 1, CardGuid = guid });
            Assert.Equal(0, (await Next(alice, CardService.SlotInsert, EquipmentCardInsertResp.Parser)).Result);
            var inserted = await Next(alice, CharacterService.ItemsSync, DBItemsInfoIncrement.Parser, m => m.StoragePack?.All?.CardInfo.ContainsKey(1) == true);
            Assert.Equal(Lunatic, inserted.StoragePack.All.CardInfo[1].CardSlotInfo[1].ItemEntry);

            await alice.SendGameAsync(CardService.ChangePlanName, new StorageholeChangeNameReq { TargetIndex = 2, Name = "Raid" });
            Assert.Equal(0, (await Next(alice, CardService.ChangePlanName, StorageholeChangeNameResp.Parser)).Result);
            await alice.SendGameAsync(CardService.SwitchPlan, new CardSwitchPlanReq { TargetIndex = 2 });
            Assert.Equal(2, (await Next(alice, CardService.SwitchPlan, CardSwitchPlanResp.Parser)).CurrentPlan);
            var empty = await Next(alice, CharacterService.ItemsSync, DBItemsInfoIncrement.Parser, m => m.StoragePack?.Inc?.CardInfo.ContainsKey(1) == true);
            Assert.True(empty.StoragePack.Inc.CardInfo[1].Del);
            await Next(alice, CardService.StorageHoleSync, DBStorageHoleInfoIncrement.Parser, m => m.CurrentPlan == 2);

            await alice.SendGameAsync(CardService.SlotInsert, new EquipmentCardInsertReq { EquipPos = (EquipGridType)1, SlotIndex = 2, CardGuid = guid });
            Assert.Equal(0, (await Next(alice, CardService.SlotInsert, EquipmentCardInsertResp.Parser)).Result);

            await alice.SendGameAsync(CardService.SwitchPlan, new CardSwitchPlanReq { TargetIndex = 1 });
            Assert.Equal(1, (await Next(alice, CardService.SwitchPlan, CardSwitchPlanResp.Parser)).CurrentPlan);
            await Next(alice, CharacterService.ItemsSync, DBItemsInfoIncrement.Parser, m => m.StoragePack?.All?.CardInfo.ContainsKey(1) == true || m.StoragePack?.Inc?.CardInfo.ContainsKey(1) == true);
        }
        await using var again = await InWorld("Alice", create: false);
        var full = await Next(again, CharacterService.ItemsSync, DBItemsInfoIncrement.Parser);
        Assert.Equal(Lunatic, full.StoragePack.All.CardInfo[1].CardSlotInfo[1].ItemEntry);
        Assert.Equal(2, full.PackInfo[3].All.Items.Values.Where(i => i.ItemEntry == Lunatic).Sum(i => i.Stack)
            + full.StoragePack.All.CardInfo[1].CardSlotInfo[1].Stack);
        var saved = await Next(again, CardService.StorageHoleSync, DBStorageHoleInfoIncrement.Parser);
        Assert.Equal("Raid", saved.StoragePlans[2].All.PlanName);
        Assert.Equal(Lunatic, saved.StoragePlans[1].All.CardInfo[1].CardSlotInfo[1].ItemId);
        Assert.Equal(Lunatic, saved.StoragePlans[2].All.CardInfo[1].CardSlotInfo[2].ItemId);
    }

    [ClientDataFact]
    public async Task SocketedCardUsesItsActualLevelAndTierInStats()
    {
        await using var alice = await InWorld("Alice");
        var bag = await Next(alice, CharacterService.ItemsSync, DBItemsInfoIncrement.Parser);
        long dagger = bag.PackInfo[2].All.Items.Values.Single(i => i.ItemEntry == Dagger).ItemGuid;
        await Gm("alice", "!lvl 10");
        await Gm("alice", $"!item {Lunatic} 2");
        await Gm("alice", $"!item {Fragment} 170");
        var gained = await Next(alice, CharacterService.ItemsSync, DBItemsInfoIncrement.Parser,
            m => m.PackInfo.TryGetValue(3, out var p) && p.All?.Items.Values.Any(i => i.ItemEntry == Lunatic) == true);
        long card = FullCard(gained).ItemGuid;
        await alice.SendGameAsync(CharacterService.EquipItemOpr, new EquipItemOprReq { Opr = 0, Pos = (EquipGridType)1, ItemGUID = dagger });
        Assert.Equal(0, (await Next(alice, CharacterService.EquipItemOpr, EquipItemOprResp.Parser)).Result);
        await alice.SendGameAsync(CardService.SlotInsert, new EquipmentCardInsertReq { EquipPos = (EquipGridType)1, SlotIndex = 1, CardGuid = card });
        var insert = await Next(alice, CardService.SlotInsert, EquipmentCardInsertResp.Parser);
        Assert.Equal(0, insert.Result);
        long socketGuid = insert.CardGuid;
        var atSocket = await Next(alice, CharacterService.OnePropertyChange, OnePropertyChange.Parser,
            m => m.Property.Propertys.Any(p => p.PropertyId == 6 && p.Data == 2));
        long atk = atSocket.Property.Propertys.Single(p => p.PropertyId == 103).Data;

        await alice.SendGameAsync(CardService.LevelUp, new CardLevelUpReq
        { SourceType = (CardLevelSourceType)4, Guid = socketGuid, GridType = (EquipGridType)1, SlotIndex = 1 });
        Assert.Equal(1, (await Next(alice, CardService.LevelUp, CardLevelUpResp.Parser)).NewLevel);
        await Next(alice, CharacterService.OnePropertyChange, OnePropertyChange.Parser,
            m => m.Property.Propertys.Any(p => p.PropertyId == 103 && p.Data == atk + 2));
        await alice.SendGameAsync(CardService.StarUp, new CardStarUpReq
        { SourceType = (CardLevelSourceType)4, Guid = socketGuid, GridType = (EquipGridType)1, SlotIndex = 1 });
        Assert.Equal(1, (await Next(alice, CardService.StarUp, CardStarUpResp.Parser)).NewStarNum);
        await Next(alice, CharacterService.OnePropertyChange, OnePropertyChange.Parser,
            m => m.Property.Propertys.Any(p => p.PropertyId == 103 && p.Data == atk + 16));
        var plan = await Next(alice, CardService.StorageHoleSync, DBStorageHoleInfoIncrement.Parser,
            m => m.StoragePlans.TryGetValue(1, out var p) && p.Inc?.CardInfo.TryGetValue(1, out var grid) == true
                && grid.Inc?.CardSlotInfo.TryGetValue(1, out var slot) == true && slot.Inc?.StarLevel == 1);
        Assert.Equal(1, plan.StoragePlans[1].Inc.CardInfo[1].Inc.CardSlotInfo[1].Inc.StarLevel);
    }

    [ClientDataFact]
    public async Task StarUpReservesTargetAndCapsAtLastTier()
    {
        long guid;
        await using (var alice = await InWorld("Alice"))
        {
            await Gm("alice", $"!item {Lunatic} 1");
            var gained = await Next(alice, CharacterService.ItemsSync, DBItemsInfoIncrement.Parser,
                m => m.PackInfo.TryGetValue(3, out var p) && p.All?.Items.Values.Any(i => i.ItemEntry == Lunatic) == true);
            guid = FullCard(gained).ItemGuid;
            await alice.SendGameAsync(CardService.StarUp, new CardStarUpReq { SourceType = (CardLevelSourceType)1, Guid = guid });
            Assert.Equal(ErrorCodes.ItemsShort, (await Next(alice, CardService.StarUp, CardStarUpResp.Parser)).Result);
            await Gm("alice", $"!item {Fragment} 999");
            await Gm("alice", $"!item {Fragment} 541");
            for (int level = 1; level <= 5; level++)
            {
                await alice.SendGameAsync(CardService.LevelUp, new CardLevelUpReq { SourceType = (CardLevelSourceType)1, Guid = guid });
                var leveled = await Next(alice, CardService.LevelUp, CardLevelUpResp.Parser, r => r.NewLevel == level);
                Assert.Equal(0, leveled.Result);
            }
            await alice.SendGameAsync(CardService.LevelUp, new CardLevelUpReq { SourceType = (CardLevelSourceType)1, Guid = guid });
            Assert.Equal(ErrorCodes.CardRankFirst, (await Next(alice, CardService.LevelUp, CardLevelUpResp.Parser,
                r => r.Result == ErrorCodes.CardRankFirst)).Result);
            await Gm("alice", $"!item {Lunatic} 7");
            for (int tier = 1; tier <= 3; tier++)
            {
                await alice.SendGameAsync(CardService.StarUp, new CardStarUpReq { SourceType = (CardLevelSourceType)1, Guid = guid });
                var reply = await Next(alice, CardService.StarUp, CardStarUpResp.Parser, r => r.NewStarNum == tier);
                Assert.Equal(0, reply.Result);
            }
            await alice.SendGameAsync(CardService.StarUp, new CardStarUpReq { SourceType = (CardLevelSourceType)1, Guid = guid });
            Assert.Equal(ErrorCodes.CardMaxStar, (await Next(alice, CardService.StarUp, CardStarUpResp.Parser,
                r => r.Result == ErrorCodes.CardMaxStar)).Result);
        }
        await using var again = await InWorld("Alice", create: false);
        var bag = await Next(again, CharacterService.ItemsSync, DBItemsInfoIncrement.Parser);
        Assert.Equal(1, bag.PackInfo[3].All.Items.Values.Where(i => i.ItemEntry == Lunatic).Sum(i => i.Stack));
        Assert.Equal(3, FullCard(bag, guid).ItemInfos[502].LongVal);
        Assert.Equal(5, FullCard(bag, guid).ItemInfos[501].LongVal);
        Assert.Equal($"{Fragment}|1540|0|0|0", FullCard(bag, guid).ItemInfos[510].StrVal);
        Assert.Equal($"{Lunatic}|7|0|0|0", FullCard(bag, guid).ItemInfos[520].StrVal);
    }

    [ClientDataFact]
    public async Task RevertKeepsBoundFlashRefundSeparateFromNormalStack()
    {
        long target;
        await using (var alice = await InWorld("Alice"))
        {
            await Gm("alice", $"!item {Lunatic} 2");
            await Gm("alice", $"!item {Fragment} 170");
            await Gm("alice", "!item 2 10000");
            var gained = await Next(alice, CharacterService.ItemsSync, DBItemsInfoIncrement.Parser,
                m => m.PackInfo.TryGetValue(3, out var p) && p.All?.Items.Values.Any(i => i.ItemEntry == Lunatic) == true);
            target = FullCard(gained).ItemGuid;
            await alice.SendGameAsync(CardService.LevelUp, new CardLevelUpReq { SourceType = (CardLevelSourceType)1, Guid = target });
            Assert.Equal(0, (await Next(alice, CardService.LevelUp, CardLevelUpResp.Parser)).Result);
        }
        // Fixture for a historical bound flash cost: no current RPC produces flash cards in this packet.
        Assert.Equal(1, FixtureSql.Execute(_app.Services.GetRequiredService<AccountStore>(),
            "UPDATE item SET str_attrs = $a WHERE guid = $g",
            ("$a", System.Text.Json.JsonSerializer.Serialize(new Dictionary<int, string>
            {
                [510] = $"{Fragment}|170|0|0|0",
                [520] = $"{Lunatic}|1|1|12345|1",
            })), ("$g", target)));
        await using (var alice = await InWorld("Alice", create: false))
        {
            await alice.SendGameAsync(CardService.Revert, new CardRevertReq { CardGuid = target });
            var reply = await Next(alice, CardService.Revert, CardRevertResp.Parser);
            Assert.Equal(0, reply.Result);
            var flash = Assert.Single(reply.RevertCards, i => i.ItemEntry == Lunatic && i.ItemInfos[530].LongVal == 1);
            Assert.Equal(1, flash.Stack);
            Assert.Equal(ItemBindType.EitemBindTypeIsBind, flash.ItemBindInfo.ItemBindType);
            Assert.Equal(12345, flash.ItemBindInfo.ExpireTimeStamp);
        }
        await using var again = await InWorld("Alice", create: false);
        var full = await Next(again, CharacterService.ItemsSync, DBItemsInfoIncrement.Parser);
        var cards = full.PackInfo[3].All.Items.Values.Where(i => i.ItemEntry == Lunatic).ToList();
        Assert.Equal(3, cards.Sum(i => i.Stack)); // original, existing normal, bound flash refund
        Assert.Single(cards, i => i.ItemInfos[530].LongVal == 1 && i.ItemBindInfo.ItemBindType == ItemBindType.EitemBindTypeIsBind);
        Assert.Equal(2, cards.Where(i => i.ItemInfos[530].LongVal == 0).Sum(i => i.Stack));
    }

    [ClientDataFact]
    public async Task RevertRefusalsPreserveProgressAndCannotRefundTwice()
    {
        long guid;
        await using (var alice = await InWorld("Alice"))
        {
            await Gm("alice", $"!item {Lunatic} 1");
            await Gm("alice", $"!item {Fragment} 170");
            var gained = await Next(alice, CharacterService.ItemsSync, DBItemsInfoIncrement.Parser,
                m => m.PackInfo.TryGetValue(3, out var p) && p.All?.Items.Values.Any(i => i.ItemEntry == Lunatic) == true);
            guid = FullCard(gained).ItemGuid;

            await alice.SendGameAsync(CardService.Revert, new CardRevertReq { CardGuid = guid });
            Assert.Equal(ErrorCodes.CardLevelReturn, (await Next(alice, CardService.Revert, CardRevertResp.Parser)).Result);
            await alice.SendGameAsync(CardService.Revert, new CardRevertReq { CardGuid = long.MaxValue });
            Assert.Equal(ErrorCodes.ItemMissing, (await Next(alice, CardService.Revert, CardRevertResp.Parser)).Result);
            await alice.SendGameAsync(CardService.LevelUp, new CardLevelUpReq { SourceType = (CardLevelSourceType)1, Guid = guid });
            Assert.Equal(0, (await Next(alice, CardService.LevelUp, CardLevelUpResp.Parser)).Result);
            await alice.SendGameAsync(CardService.Revert, new CardRevertReq { CardGuid = guid });
            Assert.Equal(ErrorCodes.CurrencyShort, (await Next(alice, CardService.Revert, CardRevertResp.Parser)).Result);
        }

        await using (var again = await InWorld("Alice", create: false))
        {
            var bag = await Next(again, CharacterService.ItemsSync, DBItemsInfoIncrement.Parser);
            Assert.Equal(1, FullCard(bag, guid).ItemInfos[501].LongVal);
            Assert.Equal($"{Fragment}|170|0|0|0", FullCard(bag, guid).ItemInfos[510].StrVal);
            Assert.DoesNotContain(bag.PackInfo.Values.SelectMany(p => p.All?.Items.Values ?? []), i => i.ItemEntry == Fragment);
            await Gm("alice", "!item 2 20000");
            await again.SendGameAsync(CardService.Revert, new CardRevertReq { CardGuid = guid });
            var reverted = await Next(again, CardService.Revert, CardRevertResp.Parser);
            Assert.Equal(0, reverted.Result);
            var original = Assert.Single(reverted.RevertCards, i => i.ItemEntry == Lunatic);
            Assert.Equal(guid, original.ItemGuid);
            Assert.Equal(1, original.Stack);
            Assert.Equal(0, original.ItemInfos[501].LongVal);
            await again.SendGameAsync(CardService.Revert, new CardRevertReq { CardGuid = guid });
            Assert.Equal(ErrorCodes.CardLevelReturn, (await Next(again, CardService.Revert, CardRevertResp.Parser)).Result);
        }

        await using var final = await InWorld("Alice", create: false);
        var full = await Next(final, CharacterService.ItemsSync, DBItemsInfoIncrement.Parser);
        Assert.Equal(10000, full.Currency[2].All);
        Assert.Equal(170, full.PackInfo.Values.SelectMany(p => p.All?.Items.Values ?? []).Where(i => i.ItemEntry == Fragment).Sum(i => i.Stack));
        Assert.Equal(1, FullCard(full, guid).Stack);
        Assert.Equal(0, FullCard(full, guid).ItemInfos[501].LongVal);
        Assert.False(FullCard(full, guid).ItemInfos.ContainsKey(510));
    }

    [ClientDataFact]
    public async Task BadSocketAndCardRequestsUseSpecificResultsWithoutMutation()
    {
        await using var alice = await InWorld("Alice");
        var starter = await Next(alice, CharacterService.ItemsSync, DBItemsInfoIncrement.Parser);
        long potion = starter.PackInfo[1].All.Items.Values.Single().ItemGuid;
        await alice.SendGameAsync(CardService.SlotRemove, new EquipmentCardRemoveReq { EquipPos = (EquipGridType)0, SlotIndex = 11 });
        Assert.Equal(ErrorCodes.SlotIndex, (await Next(alice, CardService.SlotRemove, EquipmentCardRemoveResp.Parser)).Result);
        await alice.SendGameAsync(CardService.SlotRemove, new EquipmentCardRemoveReq { EquipPos = (EquipGridType)1, SlotIndex = 1 });
        Assert.Equal(ErrorCodes.SlotEmpty, (await Next(alice, CardService.SlotRemove, EquipmentCardRemoveResp.Parser)).Result);
        await alice.SendGameAsync(CardService.LevelUp, new CardLevelUpReq { SourceType = (CardLevelSourceType)1, Guid = 123456789 });
        Assert.Equal(ErrorCodes.ItemMissing, (await Next(alice, CardService.LevelUp, CardLevelUpResp.Parser)).Result);
        await alice.SendGameAsync(CardService.SlotInsert, new EquipmentCardInsertReq { EquipPos = (EquipGridType)1, SlotIndex = 1, CardGuid = potion });
        Assert.Equal(ErrorCodes.Parameter, (await Next(alice, CardService.SlotInsert, EquipmentCardInsertResp.Parser)).Result);
        await alice.SendGameAsync(CardService.SlotTransfer, new EquipmentCardTransferReq
        { SrcEquipPos = (EquipGridType)1, SrcEquipSlot = 1, DestEquipPos = (EquipGridType)1, DestEquipSlot = 2 });
        Assert.Equal(ErrorCodes.SlotEmpty, (await Next(alice, CardService.SlotTransfer, EquipmentCardTransferResp.Parser)).Result);
        await alice.SendGameAsync(CardService.SwitchPlan, new CardSwitchPlanReq { TargetIndex = 99 });
        var bad = await Next(alice, CardService.SwitchPlan, CardSwitchPlanResp.Parser);
        Assert.Equal(ErrorCodes.Parameter, bad.Result);
        Assert.Equal(1, bad.CurrentPlan);
        foreach (var name in new[] { "", "   ", "123456789" })
        {
            await alice.SendGameAsync(CardService.ChangePlanName, new StorageholeChangeNameReq { TargetIndex = 1, Name = name });
            Assert.Equal(ErrorCodes.Parameter, (await Next(alice, CardService.ChangePlanName, StorageholeChangeNameResp.Parser)).Result);
        }
        await alice.SendGameAsync(CardService.ChangePlanName, new StorageholeChangeNameReq { TargetIndex = 99, Name = "Raid" });
        Assert.Equal(ErrorCodes.Parameter, (await Next(alice, CardService.ChangePlanName, StorageholeChangeNameResp.Parser)).Result);
        foreach (int source in new[] { 0, 2, 3, 4 })
        {
            int error = source == 0 ? ErrorCodes.Parameter : source == 4 ? ErrorCodes.ItemMissing : ErrorCodes.FeatureUnavailable;
            await alice.SendGameAsync(CardService.LevelUp, new CardLevelUpReq { SourceType = (CardLevelSourceType)source, Guid = potion });
            Assert.Equal(error, (await Next(alice, CardService.LevelUp, CardLevelUpResp.Parser)).Result);
            await alice.SendGameAsync(CardService.StarUp, new CardStarUpReq { SourceType = (CardLevelSourceType)source, Guid = potion });
            Assert.Equal(error, (await Next(alice, CardService.StarUp, CardStarUpResp.Parser)).Result);
        }
        await alice.SendGameAsync(CardService.LibraryCardUnlock, new LibraryCardUnlockReq { CardId = Lunatic });
        Assert.Equal(ErrorCodes.ItemMissing, (await Next(alice, CardService.LibraryCardUnlock, LibraryCardUnlockResp.Parser)).Result);
        await Gm("alice", $"!item {Lunatic} 1");
        await alice.SendGameAsync(CardService.LibraryCardUnlock, new LibraryCardUnlockReq { CardId = Lunatic });
        Assert.Equal(0, (await Next(alice, CardService.LibraryCardUnlock, LibraryCardUnlockResp.Parser)).Result);
        await alice.SendGameAsync(CardService.LibraryCardUnlock, new LibraryCardUnlockReq { CardId = Lunatic });
        Assert.Equal(ErrorCodes.AlreadyUnlocked, (await Next(alice, CardService.LibraryCardUnlock, LibraryCardUnlockResp.Parser)).Result);
    }


    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !Directory.Exists(Path.Combine(dir.FullName, "data"))) dir = dir.Parent;
        return dir?.FullName ?? throw new DirectoryNotFoundException("repo data");
    }
}

using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Romsg;
using Ro3Emu.Bot;
using Ro3Emu.LoadTest;
using Ro3Emu.Server;
using Ro3Emu.Server.Economy;
using Ro3Emu.Server.Game;

namespace Ro3Emu.Tests;

/// <summary>Season refinement over 5023 and 83220-83222.</summary>
public sealed class SeasonRefineTests : IAsyncLifetime
{
    private readonly string _dataDir = Path.Combine(Path.GetTempPath(), "ro3emu-season-refine-" + Guid.NewGuid().ToString("N"));
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(5);
    private WebApplication _app = null!;
    private string _http = "";
    private T Service<T>() where T : notnull => _app.Services.GetRequiredService<T>();
    private GameSession Session(BotClient bot) => Service<SessionRegistry>().All().Single(s => s.Account == bot.Account && s.Role != null);

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

    private async Task<BotClient> InWorld(string name, bool create = true)
    {
        var bot = new BotClient(_http, File.ReadAllText(Path.Combine(_dataDir, "gate_rsa_public.pem")));
        await bot.LoginAsync(name.ToLowerInvariant(), "pw");
        await bot.EnterGateAsync();
        long id = create ? (await bot.CreateRoleAsync(name)).RoleBaseInfo.Id : bot.Roles.Single().Id;
        await bot.EnterWorldAsync(id);
        return bot;
    }

    private static async Task<T> Next<T>(BotClient bot, uint msgId, Google.Protobuf.MessageParser<T> parser, Func<T, bool>? match = null)
        where T : Google.Protobuf.IMessage<T>
    {
        var packet = await bot.Inbox.WaitAsync(p => p.IsGame(msgId) && (match == null || match(parser.ParseFrom(p.Body))), Wait);
        return parser.ParseFrom(packet.Body);
    }

    private async Task Gm(string account, string line)
    {
        using var client = new HttpClient();
        var reply = await client.PostAsync($"{_http}/ro3emu/gm?account={account}", new StringContent(line));
        reply.EnsureSuccessStatusCode();
    }

    /// <summary>
    /// The paid recast's offer: the server syncs 5023 before the 83220 reply, but sends nothing when the reroll drew the
    /// same three groups (no change), so read whatever increment arrived before the reply instead of waiting for one.
    /// </summary>
    private static async Task<(int Result, int[] Offer, DBSeasonRefineIncrement? Change)> RecastOffer(BotClient bot, int[] before)
    {
        int syncs = bot.Inbox.Snapshot().Count(e => e.IsGame(SeasonRefineService.SeasonRefineSync));
        await bot.SendGameAsync(SeasonRefineService.Recast, new SeasonRefineRecastReq { EquipType = 1, RefineId = 1 });
        int result = (await Next(bot, SeasonRefineService.Recast, SeasonRefineRecastResp.Parser)).Result;
        var arrived = bot.Inbox.Snapshot().Where(e => e.IsGame(SeasonRefineService.SeasonRefineSync)).Skip(syncs)
            .Select(e => DBSeasonRefineIncrement.Parser.ParseFrom(e.Body)).ToList();
        foreach (var message in arrived) await Next(bot, SeasonRefineService.SeasonRefineSync, DBSeasonRefineIncrement.Parser, m => m.Equals(message));
        var after = arrived.Where(m => !m.SyncAllMark).LastOrDefault();
        var offer = after is { } change && change.EquipParts.TryGetValue(1, out var part) && part.Inc.RefineSlots.TryGetValue(1, out var slot)
            ? Offer(before, slot.Inc.CanSelectedAttr) : before;
        return (result, offer, after);
    }

    /// <summary>An offer after a 5023 increment: the previous keys minus deleted ones plus added ones.</summary>
    private static int[] Offer(IEnumerable<int> before,
        Google.Protobuf.Collections.MapField<int, DBSeasonRefineSlotIncrement.Types.canSelectedAttrFieldIncrement> change) =>
        before.Where(id => !(change.TryGetValue(id, out var v) && v.Del))
            .Concat(change.Where(kv => !kv.Value.Del).Select(kv => kv.Key)).Distinct().ToArray();

    [Fact]
    public async Task RequestsBeforeRoleSelectionAreRefusedWithoutDroppingTheConnection()
    {
        await using var alice = new BotClient(_http, File.ReadAllText(Path.Combine(_dataDir, "gate_rsa_public.pem")));
        await alice.LoginAsync("alice", "pw");
        await alice.EnterGateAsync();
        await StageGate.AssertUnansweredAsync(alice, SeasonRefineService.Recast, new SeasonRefineRecastReq { EquipType = 1, RefineId = 1 });
        await StageGate.AssertUnansweredAsync(alice, SeasonRefineService.SelectAttr, new SeasonRefineSelectAttrReq { EquipType = 1, RefineId = 1, GroupId = 1 });
        await StageGate.AssertUnansweredAsync(alice, SeasonRefineService.GiveUp, new SeasonRefineGiveUpReq { EquipType = 1, RefineId = 1 });
        Assert.Equal(0, (await alice.CreateRoleAsync("Alice")).Result);
    }

    [ClientDataFact]
    public async Task RecastSelectAndGiveUpUseTheExactUnlockedSlotAndPersistTheActiveGroup()
    {
        int selected;
        await using (var alice = await InWorld("Alice"))
        {
            var initial = await Next(alice, SeasonRefineService.SeasonRefineSync, DBSeasonRefineIncrement.Parser);
            Assert.True(initial.SyncAllMark);
            Assert.Equal(7, initial.EquipParts.Count);
            Assert.Equal(5, initial.EquipParts[1].All.RefineSlots.Count);
            Assert.Equal(0, initial.EquipParts[1].All.RefineSlots[1].ActiveAttr);
            Assert.Equal(0, initial.EquipParts[1].All.RefineSlots[1].CanSelectedAttr[0]);
            var empty = await Next(alice, SeasonRefineService.SeasonRefineSync, DBSeasonRefineIncrement.Parser, m => !m.SyncAllMark);
            Assert.True(empty.EquipParts[1].Inc.RefineSlots[1].Inc.CanSelectedAttr[0].Del);

            // Slot 1 needs grid 1 refined to +1; ID 1 belongs to grid 1, not grid 2.
            await alice.SendGameAsync(SeasonRefineService.Recast, new SeasonRefineRecastReq { EquipType = 1, RefineId = 1 });
            Assert.Equal(ErrorCodes.SeasonRefineLevel, (await Next(alice, SeasonRefineService.Recast, SeasonRefineRecastResp.Parser)).Result);
            await alice.SendGameAsync(SeasonRefineService.Recast, new SeasonRefineRecastReq { EquipType = 2, RefineId = 1 });
            Assert.Equal(ErrorCodes.SeasonRefineConfig, (await Next(alice, SeasonRefineService.Recast, SeasonRefineRecastResp.Parser)).Result);

            // RefiningTable[1001] guarantees +1 for 3 ore and 24000 currency 2.
            await Gm("alice", "!item 1200000 3");
            await Gm("alice", "!item 2 24000");
            await alice.SendGameAsync(EquipmentService.Refine, new RefineEquipmentReq { EquipPos = (EquipGridType)1 });
            var refined = await Next(alice, EquipmentService.Refine, RefineEquipmentRsp.Parser);
            Assert.Equal((0, 1), (refined.Result, refined.RefineLv));
            // Unlocking slot 1 gives it a free first offer of three groups (the activation panel opens without a request).
            var free = await Next(alice, SeasonRefineService.SeasonRefineSync, DBSeasonRefineIncrement.Parser,
                m => !m.SyncAllMark && m.EquipParts.TryGetValue(1, out var part) && part.Inc.RefineSlots.TryGetValue(1, out var slot)
                    && slot.Inc.CanSelectedAttr.Count == 3);
            var freeOffer = free.EquipParts[1].Inc.RefineSlots[1].Inc.CanSelectedAttr.Keys.ToArray();
            Assert.All(freeOffer, id => Assert.Contains(id, new[] { 1, 2, 3, 11, 12, 13 }));

            await alice.SendGameAsync(SeasonRefineService.Recast, new SeasonRefineRecastReq { EquipType = 1, RefineId = 1 });
            Assert.Equal(ErrorCodes.ItemsShort, (await Next(alice, SeasonRefineService.Recast, SeasonRefineRecastResp.Parser)).Result);
            await Gm("alice", "!item 1200000 4");
            await Gm("alice", "!item 2 32000");
            var catalog = EconomyCatalog.Load(Path.Combine(RepoRoot(), "data"));
            var beforeRecast = S3EconomyReader.Read(_dataDir, catalog);
            var (recastResult, candidates, offer) = await RecastOffer(alice, freeOffer); // replaces the free offer
            Assert.Equal(3, candidates.Length);
            Assert.Equal(3, candidates.Distinct().Count());
            Assert.All(candidates, id => Assert.Contains(id, new[] { 1, 2, 3, 11, 12, 13 }));
            if (offer != null) // DAL displays map keys, not values.
                Assert.All(offer.EquipParts[1].Inc.RefineSlots[1].Inc.CanSelectedAttr.Values.Where(v => !v.Del), v => Assert.Equal(0, v.All));
            Assert.Equal(0, recastResult);
            var recastWindow = S3EconomyReader.ReadLedger(_dataDir, catalog, beforeRecast,
                S3EconomyReader.Read(_dataDir, catalog))!;
            var recastReceipt = Assert.Single(recastWindow.Receipts);
            Assert.Equal("cd13c/season-refine-recast",
                EconomyReceiptReader.Read(Path.Combine(_dataDir, "ro3emu.db"), recastReceipt.OperationId)!.CommandKind);
            Assert.Contains(recastWindow.Legs, leg => leg.Kind == EconomyLegKind.Sink &&
                leg.Asset.Id == 1200000 && leg.Delta == -2);
            Assert.Contains(recastWindow.Legs, leg => leg.Kind == EconomyLegKind.Sink &&
                leg.Asset.Id == 2 && leg.Delta == -16000);
            var paid = await Next(alice, CharacterService.ItemsSync, DBItemsInfoIncrement.Parser,
                m => m.Currency.TryGetValue(2, out var amount) && amount.All == 16000);
            Assert.Equal(16000, paid.Currency[2].All); // RefiningSeasonTable[1]: 16000 currency 2 per recast.

            // A candidate from a different slot or a fabricated group cannot be activated.
            await alice.SendGameAsync(SeasonRefineService.SelectAttr,
                new SeasonRefineSelectAttrReq { EquipType = 2, RefineId = 2, GroupId = candidates[0] });
            Assert.Equal(ErrorCodes.SeasonRefineLevel, (await Next(alice, SeasonRefineService.SelectAttr, SeasonRefineSelectAttrResp.Parser)).Result);
            await alice.SendGameAsync(SeasonRefineService.SelectAttr,
                new SeasonRefineSelectAttrReq { EquipType = 1, RefineId = 1, GroupId = 999 });
            Assert.Equal(ErrorCodes.SeasonRefineChoice, (await Next(alice, SeasonRefineService.SelectAttr, SeasonRefineSelectAttrResp.Parser)).Result);
            await alice.SendGameAsync(SeasonRefineService.SelectAttr,
                new SeasonRefineSelectAttrReq { EquipType = 1, RefineId = 1, GroupId = 0 });
            Assert.Equal(ErrorCodes.SeasonRefineChoice, (await Next(alice, SeasonRefineService.SelectAttr, SeasonRefineSelectAttrResp.Parser)).Result);

            selected = candidates[0];
            await alice.SendGameAsync(SeasonRefineService.SelectAttr,
                new SeasonRefineSelectAttrReq { EquipType = 1, RefineId = 1, GroupId = selected });
            var active = await Next(alice, SeasonRefineService.SeasonRefineSync, DBSeasonRefineIncrement.Parser,
                m => !m.SyncAllMark && m.EquipParts.TryGetValue(1, out var part) && part.Inc.RefineSlots.TryGetValue(1, out var slot)
                    && slot.Inc.ActiveAttr != 0);
            var changed = active.EquipParts[1].Inc.RefineSlots[1].Inc;
            Assert.Equal(selected, changed.ActiveAttr);
            Assert.All(changed.CanSelectedAttr.Values, v => Assert.True(v.Del));
            Assert.Equal(0, (await Next(alice, SeasonRefineService.SelectAttr, SeasonRefineSelectAttrResp.Parser)).Result);

            // Recast opens a new offer while keeping the selected group's stats active; giveup clears only that offer.
            await alice.SendGameAsync(SeasonRefineService.SelectAttr,
                new SeasonRefineSelectAttrReq { EquipType = 1, RefineId = 1, GroupId = selected });
            Assert.Equal(ErrorCodes.SeasonRefineChoice, (await Next(alice, SeasonRefineService.SelectAttr, SeasonRefineSelectAttrResp.Parser)).Result);
            await alice.SendGameAsync(SeasonRefineService.GiveUp, new SeasonRefineGiveUpReq { EquipType = 1, RefineId = 1 });
            Assert.Equal(ErrorCodes.Parameter, (await Next(alice, SeasonRefineService.GiveUp, SeasonRefineGiveUpResp.Parser)).Result);

            await Gm("alice", "!item 1200000 2");
            await Gm("alice", "!item 2 16000");
            await alice.SendGameAsync(SeasonRefineService.Recast, new SeasonRefineRecastReq { EquipType = 1, RefineId = 1 });
            var second = await Next(alice, SeasonRefineService.SeasonRefineSync, DBSeasonRefineIncrement.Parser, m => !m.SyncAllMark);
            Assert.Equal(3, second.EquipParts[1].Inc.RefineSlots[1].Inc.CanSelectedAttr.Count);
            Assert.Equal(0, (await Next(alice, SeasonRefineService.Recast, SeasonRefineRecastResp.Parser)).Result);

            // The client's Again action can pay for another roll while an offer is still pending.
            await Gm("alice", "!item 1200000 2");
            await Gm("alice", "!item 2 16000");
            await alice.SendGameAsync(SeasonRefineService.Recast, new SeasonRefineRecastReq { EquipType = 1, RefineId = 1 });
            Assert.Equal(0, (await Next(alice, SeasonRefineService.Recast, SeasonRefineRecastResp.Parser)).Result);
            await alice.SendGameAsync(SeasonRefineService.GiveUp, new SeasonRefineGiveUpReq { EquipType = 1, RefineId = 1 });
            var dropped = await Next(alice, SeasonRefineService.SeasonRefineSync, DBSeasonRefineIncrement.Parser,
                m => !m.SyncAllMark && m.EquipParts.TryGetValue(1, out var part)
                    && part.Inc?.RefineSlots.ContainsKey(1) == true
                    && part.Inc.RefineSlots[1].Inc?.CanSelectedAttr.Count > 0
                    && part.Inc.RefineSlots[1].Inc.CanSelectedAttr.Values.All(v => v.Del));
            Assert.All(dropped.EquipParts[1].Inc.RefineSlots[1].Inc.CanSelectedAttr.Values, v => Assert.True(v.Del));
            Assert.Equal(0, (await Next(alice, SeasonRefineService.GiveUp, SeasonRefineGiveUpResp.Parser)).Result);
        }

        await using var relog = await InWorld("Alice", create: false);
        var saved = await Next(relog, SeasonRefineService.SeasonRefineSync, DBSeasonRefineIncrement.Parser);
        Assert.Equal(selected, saved.EquipParts[1].All.RefineSlots[1].ActiveAttr);
        var relogSession = Session(relog);
        var bonus = Service<SeasonRefineService>().ActiveAttributes(relogSession).ToArray();
        Assert.NotEmpty(bonus);
        var initialEntity = relog.Self!.Property.ToDictionary(p => p.Idx, p => p.Data);
        await Service<GameLoop>().InvokeAsync(() =>
        {
            var slot = relogSession.SeasonRefine!.EquipParts[1].RefineSlots[1];
            try
            {
                slot.ActiveAttr = 0;
                Service<CharacterService>().Refresh(relogSession);
                foreach (var (id, value) in bonus)
                    Assert.Equal((long)value, initialEntity[id] - relogSession.EntityProperties.Single(p => p.Id == id).Value);
            }
            finally
            {
                slot.ActiveAttr = selected;
                Service<CharacterService>().Refresh(relogSession);
            }
            return Task.CompletedTask;
        });
        Assert.Equal(0, saved.EquipParts[1].All.RefineSlots[1].CanSelectedAttr[0]);
        var relogEmpty = await Next(relog, SeasonRefineService.SeasonRefineSync, DBSeasonRefineIncrement.Parser, m => !m.SyncAllMark);
        Assert.True(relogEmpty.EquipParts[1].Inc.RefineSlots[1].Inc.CanSelectedAttr[0].Del);
        Assert.True(relogEmpty.EquipParts[2].Inc.RefineSlots[2].Inc.CanSelectedAttr[0].Del);
    }

    [ClientDataFact]
    public async Task SelectingAnOfferedGroupUpdatesConfiguredStats()
    {
        await using var alice = await InWorld("Alice");
        await Next(alice, SeasonRefineService.SeasonRefineSync, DBSeasonRefineIncrement.Parser);
        await Next(alice, SeasonRefineService.SeasonRefineSync, DBSeasonRefineIncrement.Parser, m => !m.SyncAllMark);
        long initialAttack = alice.Self!.Property.Single(p => p.Idx == 103).Data;
        var bag = await Next(alice, CharacterService.ItemsSync, DBItemsInfoIncrement.Parser);
        long dagger = bag.PackInfo[2].All.Items.Values.Single(i => i.ItemEntry == 2010121).ItemGuid;
        await alice.SendGameAsync(CharacterService.EquipItemOpr,
            new EquipItemOprReq { Opr = 0, Pos = (EquipGridType)1, ItemGUID = dagger });
        Assert.Equal(0, (await Next(alice, CharacterService.EquipItemOpr, EquipItemOprResp.Parser)).Result);
        await Gm("alice", "!item 1200000 5");
        await Gm("alice", "!item 2 40000");
        await alice.SendGameAsync(EquipmentService.Refine, new RefineEquipmentReq { EquipPos = (EquipGridType)1 });
        Assert.Equal(1, (await Next(alice, EquipmentService.Refine, RefineEquipmentRsp.Parser)).RefineLv);
        var before = await Next(alice, CharacterService.OnePropertyChange, OnePropertyChange.Parser,
            m => m.Property.Propertys.Any(p => p.PropertyId == 103 && p.Data == initialAttack + 27));
        // The refine to +1 unlocked slot 1 with a free first offer; activate one of its groups.
        var pending = await Next(alice, SeasonRefineService.SeasonRefineSync, DBSeasonRefineIncrement.Parser,
            m => !m.SyncAllMark && m.EquipParts.TryGetValue(1, out var part) && part.Inc.RefineSlots.TryGetValue(1, out var slot)
                && slot.Inc.CanSelectedAttr.Count == 3);
        int groupId = pending.EquipParts[1].Inc.RefineSlots[1].Inc.CanSelectedAttr.Keys.First();
        using var groups = JsonDocument.Parse(File.ReadAllText(Path.Combine(RepoRoot(), "data", "config", "RefiningSeasonAttrConfig.json")));
        var effects = groups.RootElement.GetProperty(groupId.ToString()).GetProperty("_kAttrs").EnumerateArray()
            .Select(e => (Property: e[0].GetInt32(), Bonus: e[1].GetInt32())).ToArray();
        await alice.SendGameAsync(SeasonRefineService.SelectAttr,
            new SeasonRefineSelectAttrReq { EquipType = 1, RefineId = 1, GroupId = groupId });
        var after = await Next(alice, CharacterService.OnePropertyChange, OnePropertyChange.Parser,
            m => effects.All(e => m.Property.Propertys.Any(p => p.PropertyId == e.Property
                && p.Data == before.Property.Propertys.Single(old => old.PropertyId == e.Property).Data + e.Bonus)));
        foreach (var effect in effects)
        {
            long oldValue = before.Property.Propertys.Single(p => p.PropertyId == effect.Property).Data;
            long newValue = after.Property.Propertys.Single(p => p.PropertyId == effect.Property).Data;
            Assert.Equal(oldValue + effect.Bonus, newValue);
        }
        Assert.Equal(0, (await Next(alice, SeasonRefineService.SelectAttr, SeasonRefineSelectAttrResp.Parser)).Result);

        // A GM switch affects subsequent logins; this session keeps its selected bonus and refine access.
        await Gm("alice", "!season 102");
        Assert.Equal(102, Service<SeasonService>().PlayBook);
        Assert.Equal(101, Session(alice).SeasonState!.PlayBookId);
        await Service<GameLoop>().InvokeAsync(() => Service<CharacterService>().RefreshStatsAsync(Session(alice)));
        var stillActive = await Next(alice, CharacterService.OnePropertyChange, OnePropertyChange.Parser,
            m => effects.All(e => m.Property.Propertys.Any(p => p.PropertyId == e.Property
                && p.Data == after.Property.Propertys.Single(selected => selected.PropertyId == e.Property).Data)));
        foreach (var effect in effects)
            Assert.Equal(after.Property.Propertys.Single(p => p.PropertyId == effect.Property).Data,
                stillActive.Property.Propertys.Single(p => p.PropertyId == effect.Property).Data);
        await Gm("alice", "!item 1200000 2");
        await Gm("alice", "!item 2 16000");
        await alice.SendGameAsync(SeasonRefineService.Recast, new SeasonRefineRecastReq { EquipType = 1, RefineId = 1 });
        Assert.Equal(0, (await Next(alice, SeasonRefineService.Recast, SeasonRefineRecastResp.Parser)).Result);
    }

    [ClientDataFact]
    public async Task LoginOnPlaybook102StaysUnavailableAfterGlobalSwitchBackTo101()
    {
        Service<SeasonService>().SetPlayBook(102);
        await using var bob = await InWorld("Bob");
        var season = await Next(bob, SeasonService.SeasonSync, DBSeasonInfoIncrement.Parser);
        Assert.Equal(102, season.Current.All.PlayBookId);
        Assert.Equal(102, Session(bob).SeasonState!.PlayBookId);
        Assert.False(Session(bob).SyncVersions.ContainsKey(SeasonRefineService.SeasonRefineSync));
        Service<SeasonService>().SetPlayBook(101);
        await Service<GameLoop>().InvokeAsync(() =>
        {
            var session = Session(bob);
            var slot = session.SeasonRefine!.EquipParts[1].RefineSlots[1];
            int oldLevel = session.EquipLevels.Refine.GetValueOrDefault(1), oldActive = slot.ActiveAttr;
            try
            {
                session.EquipLevels.Refine[1] = 1;
                slot.ActiveAttr = 1;
                Assert.Empty(Service<SeasonRefineService>().ActiveAttributes(session));
            }
            finally
            {
                session.EquipLevels.Refine[1] = oldLevel;
                slot.ActiveAttr = oldActive;
            }
            return Task.CompletedTask;
        });
        await bob.SendGameAsync(SeasonRefineService.Recast, new SeasonRefineRecastReq { EquipType = 1, RefineId = 1 });
        Assert.Equal(ErrorCodes.SeasonRefineConfig, (await Next(bob, SeasonRefineService.Recast, SeasonRefineRecastResp.Parser)).Result);
        await bob.SendGameAsync(SeasonRefineService.SelectAttr, new SeasonRefineSelectAttrReq { EquipType = 1, RefineId = 1, GroupId = 1 });
        Assert.Equal(ErrorCodes.SeasonRefineConfig, (await Next(bob, SeasonRefineService.SelectAttr, SeasonRefineSelectAttrResp.Parser)).Result);
        await bob.SendGameAsync(SeasonRefineService.GiveUp, new SeasonRefineGiveUpReq { EquipType = 1, RefineId = 1 });
        Assert.Equal(ErrorCodes.SeasonRefineConfig, (await Next(bob, SeasonRefineService.GiveUp, SeasonRefineGiveUpResp.Parser)).Result);
    }

    [ClientDataFact]
    public async Task PendingOfferSurvivesRelogAndCannotBeUsedOnAnotherPart()
    {
        int[] offered;
        await using (var alice = await InWorld("Alice"))
        {
            await Next(alice, SeasonRefineService.SeasonRefineSync, DBSeasonRefineIncrement.Parser);
            await Next(alice, SeasonRefineService.SeasonRefineSync, DBSeasonRefineIncrement.Parser, m => !m.SyncAllMark);
            await Gm("alice", "!item 1200000 5");
            await Gm("alice", "!item 2 40000");
            await alice.SendGameAsync(EquipmentService.Refine, new RefineEquipmentReq { EquipPos = (EquipGridType)1 });
            Assert.Equal(1, (await Next(alice, EquipmentService.Refine, RefineEquipmentRsp.Parser)).RefineLv);
            var free = await Next(alice, SeasonRefineService.SeasonRefineSync, DBSeasonRefineIncrement.Parser, m => !m.SyncAllMark);
            var freeOffer = free.EquipParts[1].Inc.RefineSlots[1].Inc.CanSelectedAttr.Keys.ToArray();
            var (recastResult, recastOffer, _) = await RecastOffer(alice, freeOffer);
            Assert.Equal(0, recastResult);
            offered = recastOffer.Order().ToArray();
        }

        await using var relog = await InWorld("Alice", create: false);
        var full = await Next(relog, SeasonRefineService.SeasonRefineSync, DBSeasonRefineIncrement.Parser);
        Assert.Equal(offered, full.EquipParts[1].All.RefineSlots[1].CanSelectedAttr.Keys.Order());
        Assert.Equal(0, full.EquipParts[1].All.RefineSlots[1].ActiveAttr);
        await Next(relog, SeasonRefineService.SeasonRefineSync, DBSeasonRefineIncrement.Parser, m => !m.SyncAllMark);
        await relog.SendGameAsync(SeasonRefineService.SelectAttr,
            new SeasonRefineSelectAttrReq { EquipType = 2, RefineId = 1, GroupId = offered[0] });
        Assert.Equal(ErrorCodes.SeasonRefineConfig, (await Next(relog, SeasonRefineService.SelectAttr, SeasonRefineSelectAttrResp.Parser)).Result);
        await relog.SendGameAsync(SeasonRefineService.SelectAttr,
            new SeasonRefineSelectAttrReq { EquipType = 1, RefineId = 1, GroupId = offered[0] });
        Assert.Equal(0, (await Next(relog, SeasonRefineService.SelectAttr, SeasonRefineSelectAttrResp.Parser)).Result);
    }

    private static string RepoRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "Ro3Emu.slnx"))) return dir.FullName;
        throw new DirectoryNotFoundException("repository root");
    }

}

using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Romsg;
using Ro3Emu.Bot;
using Ro3Emu.Server;
using Ro3Emu.Server.Accounts;
using Ro3Emu.Server.Data;
using Ro3Emu.Server.Game;
using Ro3Emu.Server.Maps;

namespace Ro3Emu.Tests;

/// <summary>Card collection star/Flash state, socket insert rules, card special effects and craft quality pity.</summary>
public sealed partial class CardRulesTests : IAsyncLifetime
{
    private const int Lunatic = 1420100, Dagger = 2010121, TwoHander = 2030120, OffHand = 2500120, Armor = 2550120, Ring = 2700120;
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(5);
    private readonly string _dataDir = Path.Combine(Path.GetTempPath(), "ro3emu-card-rules-" + Guid.NewGuid().ToString("N"));
    private WebApplication _app = null!;
    private string _httpUrl = "";

    public async Task InitializeAsync()
    {
        var (http, micro, game) = TestPorts.FreePorts();
        _app = ServerHost.Create([], o =>
        {
            o.DataDir = _dataDir;
            o.GameDataDir = Path.Combine(ClientData.RepoRoot, "data");
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

    private T Service<T>() where T : notnull => _app.Services.GetRequiredService<T>();

    private async Task<BotClient> InWorld(string name, bool create = true)
    {
        var bot = new BotClient(_httpUrl, File.ReadAllText(Path.Combine(_dataDir, "gate_rsa_public.pem")));
        await bot.LoginAsync(name.ToLowerInvariant(), "pw");
        await bot.EnterGateAsync();
        long id = create ? (await bot.CreateRoleAsync(name)).RoleBaseInfo.Id : bot.Roles.Single().Id;
        await bot.EnterWorldAsync(id);
        return bot;
    }

    private GameSession Session(string name) => Service<SessionRegistry>().All().Single(s => s.Role?.Name == name);

    private static async Task<T> Next<T>(BotClient bot, uint id, Google.Protobuf.MessageParser<T> parser, Func<T, bool>? match = null)
        where T : Google.Protobuf.IMessage<T>
    {
        var evt = await bot.Inbox.WaitAsync(e => e.IsGame(id) && (match == null || match(parser.ParseFrom(e.Body))), Wait);
        return parser.ParseFrom(evt.Body);
    }

    private async Task Gm(string account, string command)
    {
        using var http = new HttpClient();
        (await http.PostAsync($"{_httpUrl}/ro3emu/gm?account={account}", Operator.Command(command))).EnsureSuccessStatusCode();
    }

    private Task<T> OnLoop<T>(Func<T> action) => Service<GameLoop>().InvokeAsync(() => Task.FromResult(action()));

    /// <summary>Socket observers run after the reply; poll the loop-owned state.</summary>
    private async Task Eventually(Func<bool> condition, string what)
    {
        for (int i = 0; i < 50; i++)
        {
            if (await OnLoop(condition)) return;
            await Task.Delay(100);
        }
        Assert.Fail("timed out: " + what);
    }

    private async Task<int> Insert(BotClient bot, int grid, int socket, long guid)
    {
        await bot.SendGameAsync(CardService.SlotInsert, new EquipmentCardInsertReq { EquipPos = (EquipGridType)grid, SlotIndex = socket, CardGuid = guid });
        return (await Next(bot, CardService.SlotInsert, EquipmentCardInsertResp.Parser)).Result;
    }

    private async Task WearDagger(BotClient bot, GameSession session)
    {
        long dagger = session.Items.Single(i => i.Entry == Dagger).Guid;
        await bot.SendGameAsync(CharacterService.EquipItemOpr, new EquipItemOprReq { Opr = 0, Pos = (EquipGridType)1, ItemGUID = dagger });
        Assert.Equal(0, (await Next(bot, CharacterService.EquipItemOpr, EquipItemOprResp.Parser)).Result);
    }

    private async Task WearRing(BotClient bot, GameSession session)
    {
        await Gm("alice", $"!item {Ring} 1");
        await bot.SendGameAsync(CharacterService.EquipItemOpr, new EquipItemOprReq { Opr = 0, Pos = (EquipGridType)6, ItemGUID = session.Items.Single(i => i.Entry == Ring).Guid });
        Assert.Equal(0, (await Next(bot, CharacterService.EquipItemOpr, EquipItemOprResp.Parser)).Result);
    }

    private Task<KeyValuePair<int, long>[]> Sheet(GameSession session) =>
        OnLoop(() => Service<CharacterService>().Stats(session).Data.OrderBy(p => p.Key).ToArray());

    // UI_Sys_CardSelect_CellWidgetCtrl._getSlotState: IsSlotValid false is enum_CardStatus.NotEffect, "Cards are inactive
    // when no gear is equipped". A card stays socketed, durably, so only the gear's return brings its stats back.
    [ClientDataFact]
    public async Task CardsStopCountingWhileTheirGearIsOffAndStaySocketedAcrossARelog()
    {
        const int passive = 1420101; // [[2, 2000001], [2, 2000101]]
        KeyValuePair<int, long>[] clean, withCards;
        await using (var alice = await InWorld("Alice"))
        {
            var session = Session("Alice");
            await Gm("alice", "!lvl 80");
            await Gm("alice", $"!item {Lunatic} 1");
            await Gm("alice", $"!item {passive} 1");
            clean = await Sheet(session);
            await WearDagger(alice, session);
            var gearOnly = await Sheet(session);
            Assert.Equal(0, await Insert(alice, 1, 1, session.Items.Single(i => i.Entry == Lunatic).Guid));
            Assert.Equal(0, await Insert(alice, 1, 2, session.Items.Single(i => i.Entry == passive).Guid));
            await Eventually(() => session.Buffs.Any(b => b.IsPassive && !b.IsTotemOwned), "the card's passive");
            withCards = await Sheet(session);
            Assert.NotEqual(gearOnly, withCards);

            long dagger = session.Items.Single(i => i.Entry == Dagger).Guid;
            await alice.SendGameAsync(CharacterService.EquipItemOpr, new EquipItemOprReq { Opr = (EquipItemOprType)1, ItemGUID = dagger });
            Assert.Equal(0, (await Next(alice, CharacterService.EquipItemOpr, EquipItemOprResp.Parser)).Result);
            await Eventually(() => !session.Buffs.Any(b => b.IsPassive && !b.IsTotemOwned), "the passive leaves with the gear");
            Assert.Equal(clean, await Sheet(session));
            Assert.Equal(2, session.Items.Count(i => i.Grid < 0));
        }
        await using var again = await InWorld("Alice", create: false);
        var back = Session("Alice");
        Assert.Equal(2, back.Items.Count(i => i.Grid < 0));
        Assert.Equal(clean, await Sheet(back));
        await WearDagger(again, back);
        await Eventually(() => back.Buffs.Any(b => b.IsPassive && !b.IsTotemOwned), "the passive returns with the gear");
        Assert.Equal(withCards, await Sheet(back));
    }

    // LC_CardViewHelper.IsActive per enum_WeaponStatusType and enum_GridType.Fashion.
    [ClientDataFact]
    public async Task ActiveCardsFollowTheClientsSocketRules()
    {
        await using var alice = await InWorld("Alice");
        var session = Session("Alice");
        var data = Service<GameData>();
        var bag = session.Items;
        int job = session.Progress!.Career;
        long guid = 880100;
        InvItem Card(int grid, int socket) => new() { Guid = ++guid, Entry = Lunatic, Stack = 1, Pack = 3, Grid = CharacterService.CardGrid(grid, socket) };
        InvItem Worn(int entry, int grid) => new() { Guid = ++guid, Entry = entry, Stack = 1, Pack = 2, Grid = grid };
        (int, int)[] Active(int career, int grid, params InvItem[] worn)
        {
            session.Progress!.Career = career;
            session.Items = [.. worn, .. Enumerable.Range(1, 4).Select(socket => Card(grid, socket))];
            return [.. EquipmentStunts.ActiveCards(session, data).Select(CharacterService.CardSocket)];
        }
        (int, int)[] Sockets(int grid, params int[] sockets) => [.. sockets.Select(socket => (grid, socket))];
        await OnLoop(() =>
        {
            Assert.Empty(Active(1000, 3));
            Assert.Equal(Sockets(3, 1, 2, 3, 4), Active(1000, 3, Worn(Armor, 3)));
            Assert.Equal(Sockets(8, 1, 2, 3, 4), Active(1000, 8)); // costume grids need nothing worn
            Assert.Empty(Active(1000, 1));
            Assert.Equal(Sockets(1, 1, 2, 3), Active(1000, 1, Worn(Dagger, 1))); // OnlySingleHand
            Assert.Equal(Sockets(1, 1, 2, 3), Active(1100, 1, Worn(Dagger, 1))); // SingleHandAndTwoHand
            Assert.Equal(Sockets(1, 1, 2, 3, 4), Active(1100, 1, Worn(TwoHander, 1)));
            Assert.Equal(Sockets(2, 1), Active(1100, 2, Worn(OffHand, 2)));
            Assert.Equal(Sockets(1, 1, 2, 3, 4), Active(3100, 1, Worn(TwoHander, 1))); // OnlyTwoHand
            Assert.Empty(Active(3100, 2, Worn(TwoHander, 1)));
            Assert.Equal(Sockets(1, 1, 2), Active(5400, 1, Worn(Dagger, 1))); // TwoHandAndDoubleHand
            Assert.Equal(Sockets(1, 1, 2, 3, 4), Active(5400, 1, Worn(Dagger, 1), Worn(Dagger, 2)));
            Assert.Equal(Sockets(1, 3, 4), Active(5400, 1, Worn(Dagger, 2)));
            Assert.Empty(Active(5400, 2, Worn(Dagger, 1), Worn(Dagger, 2)));
            session.Items = bag;
            session.Progress.Career = job;
            return 0;
        });
    }

    [ClientDataFact]
    public async Task CollectionKeepsTheReachedStarAndFlashOwnershipAcrossReturnAndRelog()
    {
        const int flashable = 1440101;
        long role;
        await using (var alice = await InWorld("Alice"))
        {
            role = (long)alice.Self!.Uuid;
            var session = Session("Alice");
            await Gm("alice", $"!item {Lunatic} 2");
            await Gm("alice", "!item 2 10000");
            long guid = session.Items.Single(i => i.Entry == Lunatic).Guid;
            Assert.Equal(0, session.Library!.Cards[Lunatic].CardStar);

            // CardConfig tier 0 of Lunatic costs one pristine copy; the library reaches star 1 in the same commit.
            await alice.SendGameAsync(CardService.StarUp, new CardStarUpReq { SourceType = (CardLevelSourceType)1, Guid = guid });
            Assert.Equal(1, (await Next(alice, CardService.StarUp, CardStarUpResp.Parser)).NewStarNum);
            Assert.Equal(1, session.Library.Cards[Lunatic].CardStar);
            Assert.Equal(1, DBLibraryInfo.Parser.ParseFrom(Service<RoleStore>().LoadLibrary(role)).Cards[Lunatic].CardStar);

            // Returning the copy resets the copy, not the collection.
            await alice.SendGameAsync(CardService.Revert, new CardRevertReq { CardGuid = guid });
            Assert.Equal(0, (await Next(alice, CardService.Revert, CardRevertResp.Parser)).Result);
            Assert.Equal(0, session.Items.Single(i => i.Guid == guid).Attrs![502]);
            Assert.Equal(1, session.Library.Cards[Lunatic].CardStar);

            // A Flash copy (attr 530 = 1) owned before a relog: the login reconciliation records it.
            await Gm("alice", $"!item {flashable} 1");
            Assert.Equal(0, session.Library.Cards[flashable].Treasured);
            await Service<GameLoop>().InvokeAsync(() =>
            {
                var card = session.Items.Single(i => i.Entry == flashable);
                card.Attrs ??= [];
                card.Attrs[530] = 1;
                Service<ItemStore>().Update(card);
                return Task.CompletedTask;
            });
        }
        await using var again = await InWorld("Alice", create: false);
        var library = DBLibraryInfo.Parser.ParseFrom(Service<RoleStore>().LoadLibrary(role));
        Assert.Equal(1, library.Cards[flashable].Treasured);
        Assert.Equal(1, library.Cards[Lunatic].CardStar);
        Assert.Equal(0, library.Cards[Lunatic].Treasured);
    }

    [ClientDataFact]
    public async Task InsertRulesRefuseASecondSameNameOrElementCardButAllowReplacingIt()
    {
        const int sameName = 1450100, earth = 1440300, water = 1440302; // _iIfSameCard 1; two _iIsEleCard armor cards
        await using var alice = await InWorld("Alice");
        var session = Session("Alice");
        await Gm("alice", "!lvl 80");
        await Gm("alice", $"!item {sameName} 2");
        await Gm("alice", $"!item {earth} 1");
        await Gm("alice", $"!item {water} 1");
        await WearDagger(alice, session);

        long copies = session.Items.First(i => i.Entry == sameName && i.Grid == 0).Guid;
        Assert.Equal(0, await Insert(alice, 1, 1, copies));
        long rest = session.Items.First(i => i.Entry == sameName && i.Grid == 0).Guid;
        Assert.Equal(ErrorCodes.CardSameName, await Insert(alice, 1, 2, rest));
        Assert.Single(session.Items, i => i.Entry == sameName && i.Grid < 0);
        Assert.Equal(0, await Insert(alice, 1, 1, rest)); // the target socket itself is not "another" socket

        Assert.Equal(0, await Insert(alice, 3, 1, session.Items.Single(i => i.Entry == earth).Guid));
        Assert.Equal(ErrorCodes.CardSameElement, await Insert(alice, 3, 2, session.Items.Single(i => i.Entry == water).Guid));
        Assert.Equal(0, await Insert(alice, 3, 1, session.Items.Single(i => i.Entry == water).Guid));
        Assert.Equal(water, session.Items.Single(i => i.Grid == CharacterService.CardGrid(3, 1)).Entry);

        // A saved plan that names two same-name copies places only the first; the second socket stays empty.
        await Service<GameLoop>().InvokeAsync(() =>
        {
            var plan = session.CardPlans!.StoragePlans[3];
            plan.CardInfo.Clear();
            plan.CardInfo[1] = new DBInsertCardInfo();
            plan.CardInfo[1].CardSlotInfo[1] = new DBSlotItemInfo { ItemId = sameName, ItemBindInfo = new ItemBindInfo() };
            plan.CardInfo[1].CardSlotInfo[2] = new DBSlotItemInfo { ItemId = sameName, ItemBindInfo = new ItemBindInfo() };
            return Task.CompletedTask;
        });
        await alice.SendGameAsync(CardService.SwitchPlan, new CardSwitchPlanReq { TargetIndex = 3 });
        Assert.Equal(3, (await Next(alice, CardService.SwitchPlan, CardSwitchPlanResp.Parser)).CurrentPlan);
        Assert.Single(session.Items, i => i.Entry == sameName && i.Grid < 0);
        Assert.Equal(2, session.Items.Where(i => i.Entry == sameName).Sum(i => i.Stack));
    }

    [ClientDataFact]
    public async Task CostumeSocketsOpenAtTheirConditionAndTakeTheirCardParts()
    {
        // EquipGridConfig _iID 8/9/10 (Head/Face/Mouth, category 3) open socket 1 by 4000007/8/9 = base level 20;
        // card parts 7/8/9 (HeadAccessory/FaceAccessory/MouthAccessory) go there.
        const int head = 1420700, face = 1430800, mouth = 1430900; // Card: Willow, Sketch Card: Orc Hero / Moonlight Flower
        await using var alice = await InWorld("Alice");
        var session = Session("Alice");
        await Gm("alice", $"!item {head} 1");
        await Gm("alice", $"!item {face} 1");
        await Gm("alice", $"!item {mouth} 1");
        long Guid(int entry) => session.Items.Single(i => i.Entry == entry && i.Grid == 0).Guid;

        Assert.Equal(ErrorCodes.SlotSealed, await Insert(alice, 8, 1, Guid(head)));
        await Gm("alice", "!lvl 20");
        Assert.Equal(ErrorCodes.EquipSlot, await Insert(alice, 9, 1, Guid(head))); // a head card does not fit the face grid
        Assert.Equal(ErrorCodes.SlotSealed, await Insert(alice, 8, 2, Guid(head))); // _kSocketOpen2 0: never
        Assert.Equal(0, await Insert(alice, 8, 1, Guid(head)));
        Assert.Equal(0, await Insert(alice, 9, 1, Guid(face)));
        Assert.Equal(0, await Insert(alice, 10, 1, Guid(mouth)));
        Assert.Equal(head, session.Items.Single(i => i.Grid == CharacterService.CardGrid(8, 1)).Entry);
        Assert.Equal(face, session.Items.Single(i => i.Grid == CharacterService.CardGrid(9, 1)).Entry);
        Assert.Equal(mouth, session.Items.Single(i => i.Grid == CharacterService.CardGrid(10, 1)).Entry);
    }

    [ClientDataFact]
    public async Task SocketedCardAppliesFlashAttributesAndItsAttributeConversion()
    {
        const int card = 1450101; // _kSkillAttribute 11000062 [8, 109, 1, 1304, 1, 650]; Flash 11001201
        await using var alice = await InWorld("Alice");
        var session = Session("Alice");
        var data = Service<GameData>();
        var character = Service<CharacterService>();
        await WearDagger(alice, session);
        var copy = new InvItem
        {
            Guid = 880001, Entry = card, Stack = 1, Pack = 3, Grid = CharacterService.CardGrid(1, 1),
            Attrs = new() { [501] = 0, [502] = 0, [530] = 0 },
        };
        var (without, normal, flashFlat, normalFlat) = await OnLoop(() =>
        {
            var before = character.Stats(session);
            session.Items.Add(copy);
            var with = character.Stats(session);
            var plain = EquipmentStunts.FlatAttributes(session, data).ToList();
            copy.Attrs![530] = 1;
            var flash = EquipmentStunts.FlatAttributes(session, data).ToList();
            session.Items.Remove(copy);
            return (before, with, flash, plain);
        });
        Assert.Equal(without[1304] + Math.Min(normal[109], 650), normal[1304]);
        Assert.DoesNotContain(normalFlat, a => a is (209, 100) or (215, 80) or (221, 12));
        Assert.Contains((209L, 100d), flashFlat);
        Assert.Contains((215L, 80d), flashFlat);
        Assert.Contains((221L, 12d), flashFlat);
    }

    // Card 1430601's SpecialEffect 11000001 is [[3, 2004001]] (enum_SpecialEffectType.Skill): Disguise, a card skill
    // (_iSystemType 3). While socketed, its group 20040 is in the plan's skillAssigns at level 1, where the skill panel
    // lists card skills, and it can be cast; unsocketed, it is gone.
    [ClientDataFact]
    public async Task ASocketedSkillCardGrantsItsCardSkill()
    {
        const int card = 1430601;
        await using var alice = await InWorld("Alice");
        var session = Session("Alice");
        await Gm("alice", "!lvl 80");
        await Gm("alice", $"!item {card} 1");
        await WearRing(alice, session);
        Assert.Equal(0, await Insert(alice, 6, 1, session.Items.Single(i => i.Entry == card).Guid));
        await Eventually(() => session.Progress!.GrantedSkills.GetValueOrDefault(20040) == 1, "granted");
        await Next(alice, SkillService.SkillDataSync, DBSkillDataIncrement.Parser,
            m => m.SkillAssignPlans?.Inc?.Value.Any(p => p.SkillAssigns.ContainsKey(20040)) == true);

        await alice.SendGameAsync(CombatService.UseSkill, new CastSkillReq { SkillId = 2004001, Face = 0 });
        Assert.Equal(0, (await Next(alice, CombatService.UseSkill, CastSkillRsp.Parser)).Result);
        await Eventually(() => session.Buffs.Any(b => b.ConfigId == 1500601), "camouflage");

        await alice.SendGameAsync(CardService.SlotRemove, new EquipmentCardRemoveReq { EquipPos = (EquipGridType)6, SlotIndex = 1 });
        Assert.Equal(0, (await Next(alice, CardService.SlotRemove, EquipmentCardRemoveResp.Parser)).Result);
        await Eventually(() => session.Progress!.GrantedSkills.Count == 0, "revoked");
        await OnLoop(() => { session.SkillCds.Clear(); session.SkillModeCds.Clear(); return 0; });
        await Task.Delay(800);
        await alice.SendGameAsync(CombatService.UseSkill, new CastSkillReq { SkillId = 2004001, Face = 0 });
        Assert.NotEqual(0, (await Next(alice, CombatService.UseSkill, CastSkillRsp.Parser)).Result);
    }

    [ClientDataFact]
    public async Task CardPassivesFollowTheSocketAndArmTheLethalRevive()
    {
        const int card = 1450600; // _kSkillAttribute 11000... [[2, 2052001], [2, 2091501]]: group 20520 revive choice
        await using var alice = await InWorld("Alice");
        var session = Session("Alice");
        await Gm("alice", "!lvl 80");
        await Gm("alice", $"!item {card} 1");
        await WearRing(alice, session);
        Assert.Equal(0, await Insert(alice, 6, 1, session.Items.Single(i => i.Entry == card).Guid));
        long socketed = session.Items.Single(i => i.Entry == card).Guid;
        await Eventually(() => session.Buffs.Any(b => b.IsPassive && !b.IsTotemOwned && b.SourceId == socketed && b.ConfigId == 2052001), "registered");

        await alice.SendGameAsync(CardService.SlotRemove, new EquipmentCardRemoveReq { EquipPos = (EquipGridType)6, SlotIndex = 1 });
        Assert.Equal(0, (await Next(alice, CardService.SlotRemove, EquipmentCardRemoveResp.Parser)).Result);
        await Eventually(() => !session.Buffs.Any(b => b.IsPassive && !b.IsTotemOwned), "removed");

        Assert.Equal(0, await Insert(alice, 6, 1, session.Items.Single(i => i.Entry == card).Guid));
        await Eventually(() => session.Buffs.Any(b => b.IsPassive && !b.IsTotemOwned && b.ConfigId == 2052001), "registered again");
        Assert.True(await Service<GameLoop>().InvokeAsync(() => Service<BuffService>().TryPreventLethalAsync(session)));
        Assert.Equal(2052001, session.ImmuneDeathOffer!.BuffConfigId);
    }

    [ClientDataFact]
    public async Task CardProcsInflictTheirStatusAndSizeCardsRaiseDamage()
    {
        const int stun = 1420101, size = 1460100; // [[2, 2000001], [2, 2000101]]; [[2, 2050101]] (+7.5%)
        await using var alice = await InWorld("Alice");
        var session = Session("Alice");
        var data = Service<GameData>();
        var effects = Service<CardEffects>();
        var buffs = Service<BuffService>();
        await WearDagger(alice, session);
        int npc = (int)data.Table("NPCConfig").Values.Where(r => GameData.Int(r, "_iNPCType") == 1 && GameData.Int(r, "_iSize") == 2)
            .Select(r => GameData.Int(r, "_iID")).First(id => Service<MonsterService>().Template((int)id) != null);
        var template = Service<MonsterService>().Template(npc)!;
        var point = new SpawnPoint(-1, 0, 0, 0, 0, npc);
        var monster = new MonsterUnit
        {
            Uid = 1L << 48, SceneEntry = session.SceneEntry, Template = template with { Size = 2 }, Hp = template.MaxHp,
            Group = new SpawnGroup(-1, false, 1, 0, [], [point]), Home = point,
        };
        var normal = data.Row("SkillConfig", 1000001)!.Value;
        effects.Roll = _ => 0;
        try
        {
            var (plain, raised, entity) = await Service<GameLoop>().InvokeAsync(async () =>
            {
                long before = effects.AdjustDamage(session, monster, normal, DamageHitResult.Normal, 10000);
                session.Items.Add(new InvItem { Guid = 880011, Entry = stun, Stack = 1, Pack = 3, Grid = CharacterService.CardGrid(1, 1) });
                session.Items.Add(new InvItem { Guid = 880012, Entry = size, Stack = 1, Pack = 3, Grid = CharacterService.CardGrid(1, 2) });
                await effects.RefreshAsync(session);
                long after = effects.AdjustDamage(session, monster, normal, DamageHitResult.Normal, 10000);
                await effects.OnDealtAsync(session, monster, normal, 100);
                var shown = new EntityBase();
                buffs.PopulateEntity(monster, shown);
                return (before, after, shown);
            });
            Assert.Equal(10000, plain);
            Assert.Equal(10750, raised);
            Assert.Contains(entity.BuffData.AddBuffs, b => b.ConfigId == 901601 && b.Duration == 500);
            Assert.Contains(session.Buffs, b => b.IsPassive && b.SourceId == 880011 && b.ConfigId == 2000101);

            await Service<GameLoop>().InvokeAsync(async () =>
            {
                session.Items.RemoveAll(i => i.Guid is 880011 or 880012);
                await effects.RefreshAsync(session);
            });
            Assert.DoesNotContain(session.Buffs, b => b.IsPassive && !b.IsTotemOwned);
        }
        finally { effects.Roll = bound => Random.Shared.Next(bound); }
    }

    // Issue #376. A normal attack never misses here: the players' swings roll against FLEE.
    private sealed class SureHits : CombatRolls
    {
        public override double Hit() => 0;
    }

    private const int BaphometCard = 1460104, ShadowBaphometCard = 1450104;
    private const float SplashEdge = 4; // from the primary: past the 3 m Splash, within the 5 m one

    /// <summary>Monsters only of this test's, out of battle: a primary 1 m from Alice, 6 close to it and one at
    /// <see cref="SplashEdge"/>.</summary>
    private async Task<(MonsterUnit Primary, MonsterUnit[] Near, MonsterUnit Far)> SplashTargets(GameSession session)
    {
        const int npc = 5000002;
        var monsters = Service<MonsterService>();
        var units = new MonsterUnit[8];
        var template = monsters.Template(npc)! with { RoamDistance = 0, SurroundDistance = 0 };
        for (int i = 0; i < units.Length; i++)
            units[i] = await Service<GameLoop>().InvokeAsync(async () => (await monsters.SpawnSingleAsync(session.SceneEntry, npc,
                new MapPoint(session.Position.X, session.Position.Y, session.Position.Z, 0), session.InstanceId, customTemplate: template))!);
        await Service<GameLoop>().InvokeAsync(() =>
        {
            foreach (var other in Service<World>().Map(session.WorldScene).Units.OfType<MonsterUnit>().Where(m => !units.Contains(m)))
            { other.X += 200; other.Z += 200; }
            for (int i = 0; i < units.Length; i++)
            {
                units[i].StaysOutOfBattle = true;
                units[i].Hp = 100_000_000;
                float offset = i == 0 ? 0 : i == 7 ? SplashEdge : 1 + i * 0.25f;
                units[i].X = session.Position.X + 1 + offset;
                units[i].Z = session.Position.Z;
            }
            Service<CombatService>().Rolls = new SureHits();
            return Task.CompletedTask;
        });
        return (units[0], units[1..7], units[7]);
    }

    /// <summary>A real swing on the wire (31102), waiting out the swing lock of the one before; its hit on the primary.</summary>
    private async Task<SkillHitInfo> Swing(BotClient bot, GameSession session, MonsterUnit primary)
    {
        int attack = await OnLoop(() => Service<SkillService>().CurrentAttack(session));
        for (int attempt = 0; ; attempt++)
        {
            await bot.SendGameAsync(CombatService.UseSkill, new CastSkillReq { SkillId = attack, TargetId = primary.Uid, Face = session.Face });
            var accepted = await Next(bot, CombatService.UseSkill, CastSkillRsp.Parser);
            if (accepted.Result == 0)
                return await Next(bot, CombatService.SkillHit, SkillHitInfo.Parser, h => h.SessionId == accepted.SessionId && h.SkillId == attack);
            Assert.True(attempt < 30, "the swing was refused " + accepted.Result);
            await Task.Delay(100);
        }
    }

    private async Task<(BotClient Bot, GameSession Session)> Socketed(int card)
    {
        var alice = await InWorld("Alice");
        var session = Session("Alice");
        await Gm("alice", "!lvl 80");
        await Gm("alice", $"!item {card} 1");
        await WearDagger(alice, session);
        Assert.Equal(0, await Insert(alice, 1, 1, session.Items.Single(i => i.Entry == card).Guid));
        return (alice, session);
    }

    private static List<SkillHitInfo> SplashHits(BotClient bot, params int[] rows) =>
    [
        .. bot.Inbox.Snapshot().Where(e => e.IsGame(CombatService.SkillHit)).Select(e => SkillHitInfo.Parser.ParseFrom(e.Body))
            .Where(h => rows.Contains(h.SkillId)),
    ];

    // BuffConfig 2050901 (Card: Baphomet, star 0), row 2051001: "Melee Basic Attacks trigger a Splash effect within a
    // 3-meter radius ... up to 5 Extra targets, with damage reduced to 15% of the original."
    [ClientDataFact]
    public async Task BaphometCardSplashesAMeleeNormalAttackOnUpToFiveExtraTargets()
    {
        var (alice, session) = await Socketed(BaphometCard);
        await using (alice)
        {
            await Eventually(() => session.Buffs.Any(b => b.IsPassive && b.ConfigId == 2050901), "the card's passive");
            var (primary, near, far) = await SplashTargets(session);

            var hit = await Swing(alice, session, primary);
            var original = Assert.Single(hit.Dsts);
            Assert.Equal(primary.Uid, original.DstID);
            Assert.True(original.Damage > 0);

            var splash = await Next(alice, CombatService.SkillHit, SkillHitInfo.Parser, h => h.SkillId == 2051001);
            Assert.Equal(5, splash.Dsts.Count);
            Assert.All(splash.Dsts, d =>
            {
                Assert.Contains(near, m => m.Uid == d.DstID);
                Assert.Equal(Math.Max(1, (long)Math.Floor(original.Damage * 0.15)), d.Damage);
            });
            Assert.DoesNotContain(splash.Dsts, d => d.DstID == primary.Uid || d.DstID == far.Uid);
            var ring = await Next(alice, CombatService.SkillEffect, SkillEffectDisplayNotify.Parser, e => e.EffectId == 2050802);
            Assert.Equal((primary.Uid, session.Role!.Id, 2), (ring.SourceId, ring.MasterId, ring.EffectType));
        }
    }

    // Star 3: 2050904, row 2051901: 5 m, 25%, gfx 2051102 (the same prefab at 1.67).
    [ClientDataFact]
    public async Task BaphometCardsStarRaisesTheSplashAreaAndDamage()
    {
        var (alice, session) = await Socketed(BaphometCard);
        await using (alice)
        {
            await Service<GameLoop>().InvokeAsync(async () =>
            {
                var card = session.Items.Single(i => i.Entry == BaphometCard);
                (card.Attrs ??= [])[502] = 3;
                await Service<CardEffects>().RefreshAsync(session);
            });
            Assert.Contains(session.Buffs, b => b.IsPassive && b.ConfigId == 2050904);
            var (primary, near, far) = await SplashTargets(session);
            await Service<GameLoop>().InvokeAsync(() => // only the far one and one other left in the area
            {
                foreach (var m in near.Skip(1)) m.X += 50;
                return Task.CompletedTask;
            });

            var original = Assert.Single((await Swing(alice, session, primary)).Dsts);
            var splash = await Next(alice, CombatService.SkillHit, SkillHitInfo.Parser, h => h.SkillId == 2051901);
            Assert.Equal(new[] { near[0].Uid, far.Uid }.Order(), splash.Dsts.Select(d => d.DstID).Order());
            Assert.All(splash.Dsts, d => Assert.Equal(Math.Max(1, (long)Math.Floor(original.Damage * 0.25)), d.Damage));
            await Next(alice, CombatService.SkillEffect, SkillEffectDisplayNotify.Parser, e => e.EffectId == 2051102);
        }
    }

    // 2050801 (Shadow Card: Baphomet, star 0), row 2050801: "25% Chance ... 3-meter ... 50% of the original. Internal
    // Cooldown: 3 seconds."
    [ClientDataFact]
    public async Task ShadowBaphometCardSplashesOnItsChanceAndThenWaitsItsCooldown()
    {
        var (alice, session) = await Socketed(ShadowBaphometCard);
        await using (alice)
        {
            await Eventually(() => session.Buffs.Any(b => b.IsPassive && b.ConfigId == 2050801), "the card's passive");
            var (primary, _, _) = await SplashTargets(session);
            var effects = Service<CardEffects>();
            var rolled = new List<int>();
            effects.Roll = bound => { rolled.Add(bound); return 2500; };
            await Swing(alice, session, primary); // a roll of 25.00% is the first that fails
            await Eventually(() => rolled.Contains(10000), "the chance roll");
            Assert.Empty(SplashHits(alice, 2050801));

            effects.Roll = _ => 2499;
            var original = Assert.Single((await Swing(alice, session, primary)).Dsts);
            var splash = await Next(alice, CombatService.SkillHit, SkillHitInfo.Parser, h => h.SkillId == 2050801);
            Assert.All(splash.Dsts, d => Assert.Equal(Math.Max(1, (long)Math.Floor(original.Damage * 0.5)), d.Damage));

            await Swing(alice, session, primary); // inside the 3 s
            await Task.Delay(100);
            Assert.Single(SplashHits(alice, 2050801));
        }
    }

    // "Melee Basic Attacks": the Archer series' bow attack 1300001 (_iDistanceType 2) keeps one target.
    [ClientDataFact]
    public async Task BaphometCardLeavesARangedNormalAttackAlone()
    {
        const int bowItem = 2110120;
        await using var alice = await InWorld("Alice");
        var session = Session("Alice");
        await Gm("alice", "!lvl 80");
        await Gm("alice", "!job 3100");
        await Gm("alice", $"!item {bowItem} 1");
        await Gm("alice", $"!item {BaphometCard} 1");
        await Eventually(() => session.Items.Any(i => i.Entry == bowItem), "the bow");
        await alice.SendGameAsync(CharacterService.EquipItemOpr, new EquipItemOprReq { Opr = 0, Pos = (EquipGridType)1, ItemGUID = session.Items.Single(i => i.Entry == bowItem).Guid });
        Assert.Equal(0, (await Next(alice, CharacterService.EquipItemOpr, EquipItemOprResp.Parser)).Result);
        Assert.Equal(0, await Insert(alice, 1, 1, session.Items.Single(i => i.Entry == BaphometCard).Guid));
        await Eventually(() => session.Buffs.Any(b => b.IsPassive && b.ConfigId == 2050901), "the card's passive");
        var (primary, near, _) = await SplashTargets(session);

        Assert.Equal(1300001, await OnLoop(() => Service<SkillService>().CurrentAttack(session)));
        Assert.Single((await Swing(alice, session, primary)).Dsts);
        await Task.Delay(300);
        Assert.Empty(SplashHits(alice, 2051001));
        Assert.All(near, m => Assert.Equal(100_000_000, m.Hp));
    }

    [ClientDataFact]
    public async Task ANormalAttackWithoutTheCardHitsOneTarget()
    {
        await using var alice = await InWorld("Alice");
        var session = Session("Alice");
        await Gm("alice", "!lvl 80");
        await WearDagger(alice, session);
        var (primary, near, far) = await SplashTargets(session);

        var original = Assert.Single((await Swing(alice, session, primary)).Dsts);
        Assert.Equal(primary.Uid, original.DstID);
        await Task.Delay(300);
        Assert.Empty(SplashHits(alice, 2050801, 2050901, 2051001, 2051101, 2051801, 2051901));
        Assert.All(near.Append(far), m => Assert.Equal(100_000_000, m.Hp));
    }

    // The numbers the proc rows give agree with what every tier of both cards says.
    [ClientDataTheory]
    [InlineData(2050801, 2500, 3, 5, 5000, 3000, 2050802)]
    [InlineData(2050802, 4000, 3, 5, 5000, 3000, 2050802)]
    [InlineData(2050803, 4000, 3, 5, 5000, 2500, 2050802)]
    [InlineData(2050804, 4000, 3, 5, 5500, 2500, 2050802)]
    [InlineData(2050901, 10000, 3, 5, 1500, 200, 2050802)]
    [InlineData(2050902, 10000, 3, 5, 2000, 200, 2050802)]
    [InlineData(2050903, 10000, 3, 5, 2500, 200, 2050802)]
    [InlineData(2050904, 10000, 5, 5, 2500, 200, 2051102)]
    public void EveryBaphometCardTierSplashesAsItsTextSays(int id, long chance, float radius, int extra, long ratio, long cooldownMs, long effect)
    {
        var data = Service<GameData>();
        var buff = Ro3Emu.Server.Data.Config.BuffConfig.Get(data, id)!.Value;
        var row = Ro3Emu.Server.Data.Config.SkillConfig.Get(data, buff.LogicData[^1])!.Value;
        Assert.Equal(new CardEffects.Splash(chance, cooldownMs, radius, extra, ratio, effect), CardEffects.SplashOf(buff, row));
        string[] text = buff.DescData;
        Assert.Equal(radius, float.Parse(text[^4], System.Globalization.CultureInfo.InvariantCulture));
        Assert.Equal(extra, int.Parse(text[^3], System.Globalization.CultureInfo.InvariantCulture));
        Assert.Equal(ratio, long.Parse(text[^2], System.Globalization.CultureInfo.InvariantCulture) * 100);
    }

    [ClientDataFact]
    public async Task EleventhCraftOfARecipeIsAtLeastOrangeAndTheCounterPersists()
    {
        // EquipCraftConfig 2001 (rank 2, subtype 1): rank-2 weights make quality 4 (2010240) certain, so only the
        // _iOrangeGuarantee 11 can make the quality-5 output 2010250 (weight 0, drawn evenly among the forced outputs).
        await using var alice = await InWorld("Alice");
        var session = Session("Alice");
        await Gm("alice", "!quest done 10103020");
        await Gm("alice", "!item 1254001 12");
        await Gm("alice", "!item 1200704 24");
        for (int craft = 1; craft <= 12; craft++)
        {
            await alice.SendGameAsync(EquipmentService.Craft, new EquipCraftReq { Rank = 2, Subtype = 1 });
            var made = await Next(alice, EquipmentService.Craft, EquipCraftResp.Parser);
            Assert.Equal(0, made.Result);
            Assert.Equal(craft == 11 ? 2010250 : 2010240, made.Item.ItemEntry);
        }
        var counter = session.EquipmentPlans.CraftInfo[2001];
        Assert.Equal((2, 1, 1, 0, 12), (counter.Rank, counter.EquipType, counter.OrangeGuarantee, counter.RedGuarantee, counter.CraftCount));
        Assert.Equal(12, session.EquipmentPlans.CraftRankCount[2]);
        var saved = DBEquipInfo.Parser.ParseFrom(Service<RoleStore>().LoadEquipPlans(session.Role!.Id));
        Assert.Equal(counter, saved.CraftInfo[2001]);
        Assert.Equal(12, saved.CraftRankCount[2]);
    }

    [ClientDataFact]
    public async Task NaturalOrangeResetsTheOrangeCounterAndPostponesTheForcedOne()
    {
        // EquipCraftConfig 3001 (rank 3, subtype 1): [[4, 2010340], [5, 2010350]], rank-3 weights 7000 / 3000.
        // A roll of 0 draws quality 4, the last roll quality 5 (orange).
        await using var alice = await InWorld("Alice");
        var session = Session("Alice");
        var crafting = Service<EquipmentService>();
        await Gm("alice", "!quest done 10103020");
        await Gm("alice", "!item 1254007 17");
        await Gm("alice", "!item 1200704 85");
        try
        {
            for (int craft = 1; craft <= 17; craft++)
            {
                crafting.CraftRoll = craft == 6 ? bound => bound - 1 : _ => 0;
                await alice.SendGameAsync(EquipmentService.Craft, new EquipCraftReq { Rank = 3, Subtype = 1 });
                var made = await Next(alice, EquipmentService.Craft, EquipCraftResp.Parser);
                Assert.Equal(0, made.Result);
                // Craft 6 is a natural orange: the forced one moves from craft 11 to craft 17 (11 crafts later).
                Assert.Equal(craft is 6 or 17 ? 2010350 : 2010340, made.Item.ItemEntry);
                int expected = craft is >= 6 and < 17 ? craft - 6 : craft == 17 ? 0 : craft;
                Assert.Equal(expected, (await OnLoop(() => session.EquipmentPlans.CraftInfo[3001].Clone())).OrangeGuarantee);
            }
        }
        finally { crafting.CraftRoll = bound => Random.Shared.NextInt64(bound); }
        Assert.Equal(17, session.EquipmentPlans.CraftInfo[3001].CraftCount);
        Assert.Equal(0, DBEquipInfo.Parser.ParseFrom(Service<RoleStore>().LoadEquipPlans(session.Role!.Id)).CraftInfo[3001].OrangeGuarantee);
    }
}

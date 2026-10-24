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
public sealed class CardRulesTests : IAsyncLifetime
{
    private const int Lunatic = 1420100, Dagger = 2010121;
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
        (await http.PostAsync($"{_httpUrl}/ro3emu/gm?account={account}", new StringContent(command))).EnsureSuccessStatusCode();
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
                card.Attrs ??= new();
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
    public async Task SocketedCardAppliesFlashAttributesAndItsAttributeConversion()
    {
        const int card = 1450101; // _kSkillAttribute 11000062 [8, 109, 1, 1304, 1, 650]; Flash 11001201
        await using var alice = await InWorld("Alice");
        var session = Session("Alice");
        var data = Service<GameData>();
        var character = Service<CharacterService>();
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

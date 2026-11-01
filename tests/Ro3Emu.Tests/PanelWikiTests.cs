using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Ro3Emu.Tests;

/// <summary>
/// The panel's game database beyond items, monsters and maps (docs/guide/panel.md): cards, crafting, classes and skill
/// trees, pets, mounts, shops, the element table, a map's world, a monster's EXP, the event calendar and the MVP timers.
/// The synthetic fixture is copied and given the extra tables these pages read; every value is invented.
/// </summary>
public sealed class PanelWikiTests : IAsyncLifetime
{
    private SyntheticHost _host = null!;
    private string _panel = "", _data = "";

    public async Task InitializeAsync()
    {
        _data = WikiData();
        (_host, _panel) = await PanelTests.StartAsync(o => o.GameDataDir = _data);
    }

    public async Task DisposeAsync()
    {
        await _host.DisposeAsync();
        try { Directory.Delete(_data, recursive: true); } catch (IOException) { }
    }

    private static string Text(JsonElement runs) => string.Concat(runs.EnumerateArray().Select(r => r.GetProperty("text").GetString()));

    private static string[] Marked(JsonElement runs) =>
        [.. runs.EnumerateArray().Where(r => r.GetProperty("mark").GetBoolean()).Select(r => r.GetProperty("text").GetString()!)];

    [Fact]
    public async Task EveryNewPageRendersAndUnknownIdsAreNotFound()
    {
        using var browser = new PanelTests.PanelBrowser(_panel);
        foreach (string page in new[] { "/database", "/cards", "/cards/3995001", "/crafting", "/classes", "/classes/1100", "/skills/9101",
            "/pets", "/pets/9201", "/mounts", "/shops", "/shops/9701", "/elements", "/events", "/mvps" })
            Assert.Equal(HttpStatusCode.OK, (await browser.Http.GetAsync(page)).StatusCode);
        foreach (string api in new[] { "/api/cards/1", "/api/classes/1", "/api/skills/1", "/api/pets/1", "/api/shops/1", "/api/monsters/1/combat",
            "/api/maps/1/world", "/api/cards/x" })
            Assert.Equal(HttpStatusCode.NotFound, (await browser.Http.GetAsync(api)).StatusCode);
    }

    /// <summary>A star's effects as the client writes them (_iNameEquip, display type 2 = value / 100), the level table of
    /// the card's part and quality, the copies a star costs, and the special effect's text with its coloured value.</summary>
    [Fact]
    public async Task ACardListsEveryStarItsLevelsAndItsEffectTexts()
    {
        using var browser = new PanelTests.PanelBrowser(_panel);
        var list = await browser.GetJsonAsync("/api/cards");
        var summary = Assert.Single(list.GetProperty("cards").EnumerateArray());
        Assert.Equal(("Card: Synthetic Lunatic", "Weapon"), (summary.GetProperty("name").GetString(), summary.GetProperty("partName").GetString()));

        var card = await browser.GetJsonAsync("/api/cards/3995001");
        var tiers = card.GetProperty("tiers").EnumerateArray().ToList();
        Assert.Equal([0, 1], tiers.Select(t => t.GetProperty("tier").GetInt32()));
        Assert.Equal([2L, 0L], tiers.Select(t => t.GetProperty("copies").GetInt64()));
        Assert.Equal(["Sharpness +1", "Crit Rate +1.5"], tiers[0].GetProperty("effects").EnumerateArray().Select(Text));
        var top = tiers[1].GetProperty("effects").EnumerateArray().ToList();
        Assert.Equal("Hits stun for 3 s.", Text(top[^1]));
        Assert.Equal(["3"], Marked(top[^1]));
        var levels = card.GetProperty("levels").EnumerateArray().ToList();
        Assert.Equal([0, 1], levels.Select(l => l.GetProperty("level").GetInt32()));
        Assert.Equal("+2", levels[1].GetProperty("bonus")[0].GetProperty("value").GetString());
        Assert.Equal(40, levels[0].GetProperty("cost")[0].GetProperty("count").GetInt64());
        Assert.Equal(1234, card.GetProperty("returnCost").GetProperty("count").GetInt64());
    }

    /// <summary>A tree: each _iXn a cell of its row, the class's _kSkillReplace applied, Novice's first row centred; a
    /// level's description filled ^{n} with the tags, @{n} with the other params and ${n} with _kDescData.</summary>
    [Fact]
    public async Task ClassesLayTheirTreesOutAsTheSkillWindowAndSkillsFillTheirDescriptions()
    {
        using var browser = new PanelTests.PanelBrowser(_panel);
        var swordie = await browser.GetJsonAsync("/api/classes/1100");
        Assert.Equal(["Novice", "Swordie"], swordie.GetProperty("line").EnumerateArray().Select(c => c.GetProperty("name").GetString()));
        var trees = swordie.GetProperty("trees").EnumerateArray().ToList();
        Assert.Equal([1100L, 1000L], trees.Select(t => t.GetProperty("class").GetProperty("id").GetInt64()));
        var own = trees[0].GetProperty("cells").EnumerateArray().Select(c => (c.GetProperty("row").GetInt32(), c.GetProperty("col").GetInt32(),
            c.GetProperty("skill").GetProperty("id").GetInt64())).ToList();
        Assert.Equal([(1, 2, 9101L), (2, 7, 9102L)], own);
        // Novice's one skill sits in the middle of its first row; Swordie sees its replacement.
        var novice = Assert.Single(trees[1].GetProperty("cells").EnumerateArray());
        Assert.Equal((1, 4, 9103L), (novice.GetProperty("row").GetInt32(), novice.GetProperty("col").GetInt32(), novice.GetProperty("skill").GetProperty("id").GetInt64()));

        var bash = await browser.GetJsonAsync("/api/skills/9101");
        var levels = bash.GetProperty("levels").EnumerateArray().ToList();
        Assert.Equal(2, levels.Count); // _iMaxLevel 2 caps the three rows
        Assert.Equal("Strikes for 46% + 26 damage within 3 m.", Text(levels[0].GetProperty("description")));
        Assert.Equal(["46% + 26"], Marked(levels[0].GetProperty("description")));
        Assert.Equal(("SP 12", 1.5, 0.5), (levels[1].GetProperty("cost").GetString(), levels[1].GetProperty("cooldownSeconds").GetDouble(),
            levels[1].GetProperty("castSeconds").GetDouble()));
        Assert.Equal("Fire", bash.GetProperty("element").GetString());
        Assert.Equal(["Synthetic Pierce"], bash.GetProperty("unlocks").EnumerateArray().Select(u => u.GetProperty("name").GetString()));
        var pierce = await browser.GetJsonAsync("/api/skills/9102");
        var need = Assert.Single(pierce.GetProperty("levels")[0].GetProperty("requires").EnumerateArray());
        Assert.Equal(("Synthetic Bash", 2), (need.GetProperty("name").GetString(), need.GetProperty("level").GetInt32()));
    }

    /// <summary>Equipment crafting gives each quality it offers by its rank's weights among them; the item pages list the
    /// recipes an item comes out of and goes into; a formula names its life job and level from its unlock condition.</summary>
    [Fact]
    public async Task RecipesShowTheirChancesAndTheItemsLinkBothWays()
    {
        using var browser = new PanelTests.PanelBrowser(_panel);
        var book = await browser.GetJsonAsync("/api/crafting");
        var recipes = book.GetProperty("recipes").EnumerateArray().ToList();
        var forge = recipes.Single(r => r.GetProperty("kind").GetString() == "equipment");
        Assert.Equal([(3990002L, 0.75), (3990005L, 0.25)], forge.GetProperty("products").EnumerateArray()
            .Select(p => (p.GetProperty("itemId").GetInt64(), p.GetProperty("chance").GetDouble())));
        var steak = recipes.Single(r => r.GetProperty("kind").GetString() == "cooking");
        Assert.Equal(("Synthetic Steak", "Grill Master Lv 3"), (steak.GetProperty("name").GetString(), steak.GetProperty("requirement").GetString()));

        var jellopy = await browser.GetJsonAsync("/api/items/3990001/crafting");
        Assert.Empty(jellopy.GetProperty("from").EnumerateArray());
        Assert.Equal(2, jellopy.GetProperty("into").GetArrayLength());
        var blade = await browser.GetJsonAsync("/api/items/3990002/crafting");
        Assert.Equal(9001, Assert.Single(blade.GetProperty("from").EnumerateArray()).GetProperty("id").GetInt64());
    }

    /// <summary>EleConfig per 10000 as percentages, the level-gap table folded into ranges, the party bonus, and a monster's
    /// row of the table by its element.</summary>
    [Fact]
    public async Task TheElementTableAndAMonstersWeaknessesComeFromEleConfig()
    {
        using var browser = new PanelTests.PanelBrowser(_panel);
        var rules = await browser.GetJsonAsync("/api/elements");
        var fire = rules.GetProperty("elements").EnumerateArray().Single(e => e.GetProperty("defender").GetInt32() == 4);
        Assert.Equal(50, fire.GetProperty("attacks")[4].GetProperty("percent").GetInt32()); // Water hits Fire for +50 %
        Assert.Equal([(-2, -1, 70), (0, 0, 100), (1, 2, 70)], rules.GetProperty("levelGap").EnumerateArray()
            .Select(g => (g.GetProperty("fromGap").GetInt32(), g.GetProperty("toGap").GetInt32(), g.GetProperty("percent").GetInt32())));
        Assert.Equal([100, 125, 150, 175, 200], rules.GetProperty("party").EnumerateArray().Select(p => p.GetProperty("percent").GetInt32()));

        var poring = await browser.GetJsonAsync("/api/monsters/16320002/combat");
        Assert.Equal(-25, poring.GetProperty("taken").EnumerateArray().Single(t => t.GetProperty("name").GetString() == "Wind").GetProperty("percent").GetInt32());
    }

    /// <summary>A map's NPCs with their title, warp and shop; its portal and floor; its levels from OnHookMonsterRecommendConfig.</summary>
    [Fact]
    public async Task AMapsWorldListsItsNpcsLinksAndRecommendedLevels()
    {
        using var browser = new PanelTests.PanelBrowser(_panel);
        var world = await browser.GetJsonAsync("/api/maps/10112/world");
        Assert.Equal(("Field", 10, 20), (world.GetProperty("type").GetString(), world.GetProperty("minLevel").GetInt32(), world.GetProperty("maxLevel").GetInt32()));
        var npc = Assert.Single(world.GetProperty("npcs").EnumerateArray());
        Assert.Equal(("Synthetic Guide", 2, 9701L), (npc.GetProperty("name").GetString(), npc.GetProperty("count").GetInt32(), npc.GetProperty("shopId").GetInt64()));
        Assert.Equal(["Field Guide", "Teleport to Synthetic Cave", "Shop: Test Shop"], npc.GetProperty("functions").EnumerateArray().Select(f => f.GetString()));
        var link = Assert.Single(world.GetProperty("links").EnumerateArray());
        Assert.Equal(("Synthetic Cave", "Portal"), (link.GetProperty("map").GetString(), link.GetProperty("via")[0].GetString()));
        Assert.Equal(10113, Assert.Single(world.GetProperty("floors").EnumerateArray()).GetProperty("mapId").GetInt64());
        var cave = await browser.GetJsonAsync("/api/maps/10113/world");
        Assert.Equal(("Dungeon floor", 10112L), (cave.GetProperty("type").GetString(), cave.GetProperty("parent").GetProperty("mapId").GetInt64()));
    }

    /// <summary>A pet's stats at a level are its AttriMonsterConfig row times _iBaseAttr / 10000, its power the star's plus
    /// the level's share; the fight and collection bonuses are each star's stage 0. A mount's 1131 is its speed in percent.</summary>
    [Fact]
    public async Task PetsAndMountsShowTheirGrowthStarsAndSpeed()
    {
        using var browser = new PanelTests.PanelBrowser(_panel);
        var pet = await browser.GetJsonAsync("/api/pets/9201");
        var level1 = pet.GetProperty("growth")[0];
        Assert.Equal(("HP", "200"), (level1.GetProperty("stats")[0].GetProperty("name").GetString(), level1.GetProperty("stats")[0].GetProperty("value").GetString()));
        Assert.Equal(100 + 50 * 20000 / 10000, level1.GetProperty("fightPower").GetInt32());
        var stars = pet.GetProperty("stars").EnumerateArray().ToList();
        Assert.Equal([(1, 0), (1, 1)], stars.Select(s => (s.GetProperty("star").GetInt32(), s.GetProperty("stage").GetInt32())));
        Assert.Equal("+30", stars[1].GetProperty("fight")[0].GetProperty("value").GetString()); // stage 1 shows its star's stage 0
        Assert.Equal("Synthetic Bash", Assert.Single(stars[0].GetProperty("skills").EnumerateArray()).GetProperty("name").GetString());

        var mount = Assert.Single((await browser.GetJsonAsync("/api/mounts")).GetProperty("mounts").EnumerateArray());
        Assert.Equal(("Synthetic Peco", 50.0), (mount.GetProperty("name").GetString(), mount.GetProperty("speedPercent").GetDouble()));
        var item = Assert.Single(mount.GetProperty("items").EnumerateArray());
        Assert.Equal((3990001L, 600L), (item.GetProperty("itemId").GetInt64(), item.GetProperty("durationSeconds").GetInt64()));
    }

    /// <summary>The calendar answers with the server's zone and its 05:00 day; a timed To-Do row lists its openings within the
    /// week. The MVP timers list each field MVP of MVPConfig with its map, from the loop.</summary>
    [Fact]
    public async Task TheCalendarAndTheMvpTimersComeFromTheServersClocks()
    {
        using var browser = new PanelTests.PanelBrowser(_panel);
        var calendar = await browser.GetJsonAsync("/api/events");
        Assert.Equal(5, calendar.GetProperty("dayStartHour").GetInt32());
        Assert.False(string.IsNullOrEmpty(calendar.GetProperty("zone").GetString()));
        var hunt = Assert.Single(calendar.GetProperty("events").EnumerateArray());
        Assert.Equal("Synthetic Hunt", hunt.GetProperty("name").GetString());
        var now = calendar.GetProperty("now").GetDateTimeOffset();
        var times = hunt.GetProperty("times").EnumerateArray().Select(t => (t.GetProperty("start").GetDateTimeOffset(), t.GetProperty("end").GetDateTimeOffset())).ToList();
        Assert.NotEmpty(times);
        Assert.All(times, t => Assert.True(t.Item2 > now && t.Item1 < now.AddDays(7) && t.Item1 < t.Item2));

        var mvp = Assert.Single((await browser.GetJsonAsync("/api/mvps")).GetProperty("mvps").EnumerateArray());
        Assert.Equal(("Synthetic Angeling", "Synthetic Field", 3600), (mvp.GetProperty("monster").GetProperty("name").GetString(),
            mvp.GetProperty("map").GetString(), mvp.GetProperty("respawnSeconds").GetInt32()));
        Assert.True(mvp.GetProperty("state").GetString() is "alive" or "spawning" or "respawning");
    }

    [Fact]
    public async Task ShopsListTheirGoods()
    {
        using var browser = new PanelTests.PanelBrowser(_panel);
        var shop = await browser.GetJsonAsync("/api/shops/9701");
        Assert.Equal("Test Shop", shop.GetProperty("shop").GetProperty("name").GetString());
        var good = Assert.Single(shop.GetProperty("goods").EnumerateArray());
        Assert.Equal((3990001L, 25L), (good.GetProperty("item").GetProperty("itemId").GetInt64(), good.GetProperty("price").GetInt64()));
    }

    // ---------------------------------------------------------------- the data set

    /// <summary>The synthetic fixture with the tables the database pages read on top (invented rows).</summary>
    private static string WikiData()
    {
        string dir = Directory.CreateTempSubdirectory("ro3emu-panel-wiki-").FullName;
        foreach (string file in Directory.EnumerateFiles(SyntheticData.Dir, "*", SearchOption.AllDirectories))
        {
            string target = Path.Combine(dir, Path.GetRelativePath(SyntheticData.Dir, file));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target);
        }
        foreach (var (table, rows) in Tables) Merge(Path.Combine(dir, "config", table + ".json"), rows);
        Merge(Path.Combine(dir, "sceneeditor", "synthetic_field.json"), """
            { "kNpcList": {
              "1": { "Id": 1, "NpcId": 16330001, "NpcTag": 2, "x": 1, "y": 0, "z": 1 },
              "2": { "Id": 2, "NpcId": 16330001, "NpcTag": 2, "x": 2, "y": 0, "z": 2 },
              "3": { "NpcId": 300007, "NpcTag": 26, "TravelTargetInfoArray": [ { "MapID": 10113, "TravelPointID": 1 } ], "x": 3, "y": 0, "z": 3 } } }
            """);
        return dir;
    }

    /// <summary>Adds <paramref name="json"/>'s rows to a table (or top-level fields to a file), a row's fields over the old ones.</summary>
    private static void Merge(string path, string json)
    {
        var target = File.Exists(path) ? JsonNode.Parse(File.ReadAllText(path))!.AsObject() : [];
        foreach (var (key, value) in JsonNode.Parse(json)!.AsObject().ToList())
        {
            if (target[key] is JsonObject old && value is JsonObject fields)
                foreach (var (field, v) in fields.ToList()) old[field] = v?.DeepClone();
            else target[key] = value?.DeepClone();
        }
        File.WriteAllText(path, target.ToJsonString());
    }

    private static readonly (string Table, string Rows)[] Tables =
    [
        ("Localization_en", """
            { "991000001": { "_iID": 991000001, "_kDes": "Card: Synthetic Lunatic" }, "991000002": { "_iID": 991000002, "_kDes": "Sharpness" },
              "991000003": { "_iID": 991000003, "_kDes": "Crit Rate" }, "991000004": { "_iID": 991000004, "_kDes": "Hits stun for ^{1}@{1}^{2} s." },
              "991000010": { "_iID": 991000010, "_kDes": "Novice" }, "991000011": { "_iID": 991000011, "_kDes": "Swordie" },
              "991000012": { "_iID": 991000012, "_kDes": "Synthetic Bash" }, "991000013": { "_iID": 991000013, "_kDes": "Synthetic Pierce" },
              "991000014": { "_iID": 991000014, "_kDes": "Synthetic First Aid" }, "991000015": { "_iID": 991000015, "_kDes": "Synthetic Swing" },
              "991000016": { "_iID": 991000016, "_kDes": "Strikes for ^{1}${1}% + ${2}^{2} damage within @{1} m." },
              "991000020": { "_iID": 991000020, "_kDes": "Synthetic Steak" }, "991000021": { "_iID": 991000021, "_kDes": "Grill Master" },
              "991000030": { "_iID": 991000030, "_kDes": "Synthetic Guide" }, "991000031": { "_iID": 991000031, "_kDes": "Field Guide" },
              "991000032": { "_iID": 991000032, "_kDes": "Synthetic Cave" }, "991000040": { "_iID": 991000040, "_kDes": "Synthetic Choco" },
              "991000041": { "_iID": 991000041, "_kDes": "Synthetic Peco" }, "991000050": { "_iID": 991000050, "_kDes": "Synthetic Hunt" } }
            """),
        ("ItemConfig", """
            { "3995001": { "_iID": 3995001, "_iType": 4, "_iSubType": 400, "_iQuality": 2, "_iCardPart": 1, "_iName": [ 991000001 ], "_kIcon": "item_3995001.png" },
              "3995002": { "_iID": 3995002, "_iType": 2, "_iQuality": 3, "_iName": [ 991000020 ] },
              "3990001": { "_iUseEffect": [ [ "201", "600", "9301" ] ] } }
            """),
        ("AttributeConfig", """
            { "103": { "_iID": 103, "_iName": [ 991000002 ], "_iNameEquip": [ 991000002 ], "_iTextValueDisplayType": 0, "_iDataType": 0 },
              "111": { "_iID": 111, "_iName": [ 991000003 ], "_iNameEquip": [ 991000003 ], "_iTextValueDisplayType": 2, "_iDataType": 0 } }
            """),
        ("CardConfig", """
            { "1": { "_iID": 1, "_iCardID": 3995001, "_iTier": 0, "_iLevel": 5, "_iPower": 10, "_iIsOpen": 1, "_iShowLibrary": 1,
                     "_kBasicAttribute": [ [ 103, 1 ], [ 111, 150 ] ], "_kCost": [ [ 3995001, 2 ] ] },
              "2": { "_iID": 2, "_iCardID": 3995001, "_iTier": 1, "_iLevel": 0, "_iPower": 20, "_iIsOpen": 1, "_iShowLibrary": 1,
                     "_kBasicAttribute": [ [ 103, 2 ] ], "_kSkillAttribute": [ 9401 ] } }
            """),
        ("SpecialEffectConfig", """{ "9401": { "_iID": 9401, "_iIsOtherDesc": 0, "_iEffectDesc": [ 991000004, "<color=#cc762a>", "</color>", "3" ] } }"""),
        ("CardUpgradeConfig", """
            { "1": { "_iID": 1, "_iLevel": 0, "_iParts": 1, "_iQuality": 2, "_iAttr": [], "_iCost": [ [ 3990001, 40 ] ] },
              "2": { "_iID": 2, "_iLevel": 1, "_iParts": 1, "_iQuality": 2, "_iAttr": [ [ 103, 2 ] ], "_iCost": [] },
              "3": { "_iID": 3, "_iLevel": 1, "_iParts": 2, "_iQuality": 2, "_iAttr": [ [ 103, 9 ] ], "_iCost": [] } }
            """),
        ("CardReturnCostConfig", """{ "2": { "_iQuality": 2, "_iCost": [ [ 2, 1234 ] ] } }"""),
        ("JobConfig", """
            { "1000": { "_iID": 1000, "_iJob": 1000, "_iJobRank": 1, "_iParentId": 0, "_iName": [ 991000010 ] },
              "1100": { "_iID": 1100, "_iJob": 1100, "_iJobRank": 2, "_iParentId": 1000, "_iName": [ 991000011 ], "_kSkillReplace": [ [ 9104, 9103 ] ] } }
            """),
        ("SkillTreeConfig", """
            { "1001": { "_iID": 1001, "_iJob": 1000, "_iY": 1, "_iX1": 9104, "_iX2": 0, "_iX3": 0, "_iX4": 0, "_iX5": 0, "_iX6": 0, "_iX7": 0 },
              "1101": { "_iID": 1101, "_iJob": 1100, "_iY": 1, "_iX1": 0, "_iX2": 9101, "_iX3": 0, "_iX4": 0, "_iX5": 0, "_iX6": 0, "_iX7": 0 },
              "1102": { "_iID": 1102, "_iJob": 1100, "_iY": 2, "_iX1": 0, "_iX2": 0, "_iX3": 0, "_iX4": 0, "_iX5": 0, "_iX6": 0, "_iX7": 9102 } }
            """),
        ("SkillConfig", """
            { "910101": { "_iID": 910101, "_iSkillID": 9101, "_iLevel": 1, "_iMaxLevel": 2, "_iMode": 0, "_iJob": 1100, "_iBasicType": 1, "_iDmgType": 1,
                          "_iEleType": 4, "_iDistanceMax": 300, "_iName": [ 991000012 ], "_iDescription": [ 991000016, "<color=#cc762a>", "</color>", "3" ],
                          "_kDescData": [ "46", "26" ] },
              "910102": { "_iID": 910102, "_iSkillID": 9101, "_iLevel": 2, "_iMaxLevel": 2, "_iMode": 0, "_iJob": 1100, "_iBasicType": 1, "_iDmgType": 1,
                          "_iEleType": 4, "_iCD": 1500, "_iChantFix": 200, "_iChantVar": 300, "_kCost": [ [ 1, 2001, 0, 12 ] ], "_iName": [ 991000012 ],
                          "_iDescription": [ 991000016, "<color=#cc762a>", "</color>", "3" ], "_kDescData": [ "47", "27" ] },
              "910103": { "_iID": 910103, "_iSkillID": 9101, "_iLevel": 3, "_iMaxLevel": 2, "_iMode": 0, "_iJob": 1100, "_iName": [ 991000012 ] },
              "910201": { "_iID": 910201, "_iSkillID": 9102, "_iLevel": 1, "_iMaxLevel": 1, "_iMode": 0, "_iJob": 1100, "_iBasicType": 1,
                          "_iName": [ 991000013 ], "_kPreSkillRequired": [ [ 9101, 2 ] ] },
              "910301": { "_iID": 910301, "_iSkillID": 9103, "_iLevel": 1, "_iMaxLevel": 1, "_iMode": 0, "_iJob": 1000, "_iName": [ 991000014 ] },
              "910401": { "_iID": 910401, "_iSkillID": 9104, "_iLevel": 1, "_iMaxLevel": 1, "_iMode": 0, "_iJob": 1000, "_iName": [ 991000015 ] } }
            """),
        ("EquipCraftConfig", """
            { "9001": { "_iID": 9001, "_iRank": 2, "_iEquipType": 2, "_iOrangeGuarantee": 11, "_iRedGuarantee": 0,
                        "_kCost": [ [ 3990001, 3 ] ], "_kQualityEquipID": [ [ 4, 3990002 ], [ 5, 3990005 ] ] } }
            """),
        ("EquipCraftQualityConfig", """
            { "1": { "_iID": 1, "_iRank": 2, "_iQuality": 4, "_iWeight": 300 }, "2": { "_iID": 2, "_iRank": 2, "_iQuality": 5, "_iWeight": 100 },
              "3": { "_iID": 3, "_iRank": 2, "_iQuality": 6, "_iWeight": 600 } }
            """),
        ("LifeJobConfig", """{ "2": { "_iID": 2, "_iType": 2, "_iName": [ 991000021 ] } }"""),
        ("LC_ConditionList", """{ "9500": { "m_eType": 1, "m_kSubConditionList": [ { "m_eType": 3, "m_kData": [ 11001, 2, 3 ] } ] } }"""),
        ("LifeFormulaConfig", """
            { "9601": { "_iFormulaId": 9601, "_iJobId": 2, "_iFormulaName": [ 991000020 ], "_iUnlockCondition": 9500, "_iVitalitycost": 20,
                        "_iTime": 1000, "_iEXPgained": 150, "_kcost": [ [ 3990001, 8 ] ], "_iSuccessOutput": [] } }
            """),
        ("EleConfig", """
            { "4": { "_iID": 4, "_iNone": 0, "_iWind": -2500, "_iEarth": 0, "_iFire": -5000, "_iWater": 5000, "_iPoison": 0, "_iHoly": 0,
                     "_iShadow": 0, "_iGhost": 0, "_iUndead": 0 } }
            """),
        ("LvExpCorrectionConfig", """
            { "1": { "_iBaseLvGap": -2, "_iLvDifferenceCorrection": 7000 }, "2": { "_iBaseLvGap": -1, "_iLvDifferenceCorrection": 7000 },
              "3": { "_iBaseLvGap": 0, "_iLvDifferenceCorrection": 10000 }, "4": { "_iBaseLvGap": 1, "_iLvDifferenceCorrection": 7000 },
              "5": { "_iBaseLvGap": 2, "_iLvDifferenceCorrection": 7000 } }
            """),
        ("MapConfig", """
            { "10112": { "_iSceneType": 1, "_kCaveEntrance": [ 10113 ] },
              "10113": { "_iID": 10113, "_iName": [ 991000032 ], "_iSceneType": 6, "_iCaveBelongsTo": 10112 } }
            """),
        ("NPCConfig", """
            { "16330001": { "_iID": 16330001, "_iNPCType": 2, "_kName": [ 991000030 ], "_kTitle": [ 991000031 ], "_iInteractID": [ 9701 ] },
              "9202": { "_iID": 9202, "_iNPCType": 7, "_iAttributeVariated": 9203 } }
            """),
        ("NPCInteractConfig", """{ "9701": { "_iEXECAction": [ [ "141", "10113", "1" ] ] } }"""),
        ("ShopConfig", """{ "9701": { "_iNpcId": 16330001 } }"""),
        ("OnHookMonsterRecommendConfig", """{ "1": { "_iMonsterID": 16320002, "_iMapID": [ 10112 ], "_iRecommendationLevel": [ 10, 20 ] } }"""),
        ("PetBaseConfig", """{ "9201": { "_iID": 9201, "_iMonster": 9202, "_iQuality": 3, "_iCamp": 1, "_iName": [ 991000040 ], "_kDecompose": [ 3990001, 20 ] } }"""),
        ("PetStarConfig", """
            { "1": { "_iPetID": 9201, "_iStar": 1, "_iStage": 0, "_iBaseAttr": 20000, "_iFightOrAssistStrengthMultiple": 20000, "_iStarFightStrength": 100,
                     "_iStarAssistStrength": 10, "_kFightSkill": [ [ 103, 30 ] ], "_kCollectAttr": [ [ 103, 5 ] ], "_kPiece": [ 3990001, 20 ],
                     "_kActiveSkill": [ 910101 ] },
              "2": { "_iPetID": 9201, "_iStar": 1, "_iStage": 1, "_iBaseAttr": 21000, "_iFightOrAssistStrengthMultiple": 21000, "_iStarFightStrength": 110,
                     "_kPiece": [ 3990001, 5 ] } }
            """),
        ("PetExpConfig", """{ "1": { "_iQuality": 3, "_iLv": 1, "_iExp": 600, "_iLvFightStrength": 50, "_iLvAssistStrength": 20 } }"""),
        ("AttriMonsterConfig", """{ "9203001": { "_iID": 9203001, "_iLevel": 1, "_kAttri": [ [ 101, 100 ], [ 103, 11 ] ] } }"""),
        ("MountsConfig", """{ "9301": { "_iID": 9301, "_iName": [ 991000041 ], "_iQuality": 3, "_kAttr": [ [ 1131, 5000 ] ] } }"""),
        ("DailyActivityConfig", """{ "3303": { "_iName": [ 991000050 ], "_iIsHide": 0 } }"""),
    ];
}

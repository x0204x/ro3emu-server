namespace Ro3Emu.Panel;

/// <summary>
/// The rest of the public game database: cards, crafting, classes and their skill trees, pets, mounts, shops, the
/// element table, a map's world (NPCs, links, levels), a monster's EXP and element weaknesses, the event calendar and the
/// field MVPs' timers. Everything comes from the server's game data and English texts, built once and kept in memory,
/// except the calendar and the MVP timers, which the server's own clocks answer.
/// </summary>
public partial interface IPanelBackend
{
    /// <summary>Every card in the card library, with the filter choices.</summary>
    Task<PanelCardList> CardsAsync();

    /// <summary>One card: every star's effects, the level-up bonuses and costs, who drops it; null when unknown.</summary>
    Task<PanelCard?> CardAsync(long id);

    /// <summary>Every crafting recipe: the life jobs' formulas and equipment crafting.</summary>
    Task<PanelRecipeBook> RecipesAsync();

    /// <summary>The recipes an item comes out of and the ones it goes into.</summary>
    Task<PanelItemCrafting> ItemCraftingAsync(long id);

    /// <summary>Every class with a skill tree, in its line's order.</summary>
    Task<IReadOnlyList<PanelClassSummary>> ClassesAsync();

    /// <summary>One class: its line and the skill trees it plays with (its own and its ancestors'); null when unknown.</summary>
    Task<PanelClassDetail?> ClassAsync(long id);

    /// <summary>One skill (a SkillConfig skill id): every level's description and numbers; null when unknown.</summary>
    Task<PanelSkillDetail?> SkillAsync(long id);

    /// <summary>Every pet.</summary>
    Task<IReadOnlyList<PanelPetSummary>> PetsAsync();

    /// <summary>One pet: growth, stars and skills; null when unknown.</summary>
    Task<PanelPet?> PetAsync(long id);

    /// <summary>Every mount with its speed, stats and the items that unlock it.</summary>
    Task<IReadOnlyList<PanelMount>> MountsAsync();

    /// <summary>Every shop with how many goods it sells.</summary>
    Task<IReadOnlyList<PanelShopSummary>> ShopsAsync();

    /// <summary>One shop (ShopConfig _iClassID) with its goods; null when unknown.</summary>
    Task<PanelShop?> ShopAsync(long id);

    /// <summary>The element table and the EXP rules every monster page refers to.</summary>
    Task<PanelCombatRules> CombatRulesAsync();

    /// <summary>A monster's EXP per kill and the damage it takes from each element; null when unknown.</summary>
    Task<PanelMonsterCombat?> MonsterCombatAsync(long id);

    /// <summary>A map's NPCs, links to other maps, type and recommended levels; null when unknown.</summary>
    Task<PanelMapWorld?> MapWorldAsync(long id);

    /// <summary>The scheduled activities from now to a week ahead, as the server opens them.</summary>
    Task<PanelCalendar> CalendarAsync();

    /// <summary>Every field MVP with its last death and next respawn, from the server's live state.</summary>
    Task<IReadOnlyList<PanelMvpTimer>> MvpTimersAsync();
}

/// <summary>A part of a readable text; <paramref name="Mark"/> is a value the game colours (a number of the level).</summary>
public sealed record PanelRun(string Text, bool Mark = false);

/// <summary>An item and a count: a material, a cost, a product.</summary>
public sealed record PanelAmount(long ItemId, string Item, int Quality, string Icon, long Count);

// ---------------------------------------------------------------- cards

/// <param name="Part">ItemConfig _iCardPart: the equipment slot the card goes into.</param>
/// <param name="Picture">The card's art (ItemConfig _kPic).</param>
/// <param name="Summary">The top star's effects in one line.</param>
/// <param name="Library">In the card library (CardConfig _iShowLibrary); cards outside it still exist in the game.</param>
public sealed record PanelCardSummary(long Id, string Name, int Quality, string QualityName, int Part, string PartName, string Icon,
    string Picture, string Summary, bool Library);

public sealed record PanelCardList(IReadOnlyList<PanelCardSummary> Cards, IReadOnlyList<PanelOption> Parts, IReadOnlyList<PanelOption> Qualities);

/// <param name="Tier">The star, 0 to 3 (CardConfig _iTier).</param>
/// <param name="LevelCap">The highest card level at this star; 0 = the table's highest.</param>
/// <param name="Effects">Each effect as one line of text.</param>
/// <param name="FlashEffects">What a Flash copy adds at this star.</param>
/// <param name="Copies">Copies of the card the next star costs; 0 at the top star.</param>
public sealed record PanelCardTier(int Tier, int LevelCap, int Power, IReadOnlyList<IReadOnlyList<PanelRun>> Effects,
    IReadOnlyList<IReadOnlyList<PanelRun>> FlashEffects, long Copies);

/// <param name="Bonus">What the level adds to every star's effects (CardUpgradeConfig _iAttr).</param>
/// <param name="Cost">What the next level costs; empty at the top.</param>
public sealed record PanelCardLevel(int Level, IReadOnlyList<PanelStat> Bonus, IReadOnlyList<PanelAmount> Cost);

/// <param name="Rules">The socket rules: one per equipment, an element card.</param>
/// <param name="ReturnCost">Minted Coins to revert the card's levels and stars (CardReturnCostConfig).</param>
public sealed record PanelCard(PanelCardSummary Card, IReadOnlyList<PanelCardTier> Tiers, IReadOnlyList<PanelCardLevel> Levels,
    IReadOnlyList<PanelItemDrop> DroppedBy, IReadOnlyList<PanelAmount> Decompose, IReadOnlyList<string> Rules, PanelAmount? ReturnCost);

// ---------------------------------------------------------------- crafting

/// <param name="Chance">The chance per craft, 0 to 1.</param>
public sealed record PanelRecipeOutput(long ItemId, string Item, int Quality, string Icon, int Min, int Max, double Chance);

/// <param name="Kind">"cooking" (a life job's formula) or "equipment".</param>
/// <param name="Requirement">What the player needs: "Chef Lv 3", "Tier 5 blueprint".</param>
/// <param name="Facts">The recipe's other numbers: vigor, time, life job EXP, unlock cost, guarantees.</param>
public sealed record PanelRecipe(long Id, string Kind, string Name, string Icon, string Requirement, IReadOnlyList<PanelAmount> Materials,
    IReadOnlyList<PanelRecipeOutput> Products, IReadOnlyList<PanelStat> Facts, IReadOnlyList<PanelRun> Description);

public sealed record PanelRecipeBook(IReadOnlyList<PanelRecipe> Recipes, IReadOnlyList<PanelOption> Kinds);

/// <param name="From">The recipes that make the item (any variant of its name).</param>
/// <param name="Into">The recipes that use it as a material.</param>
public sealed record PanelItemCrafting(IReadOnlyList<PanelRecipe> From, IReadOnlyList<PanelRecipe> Into);

// ---------------------------------------------------------------- classes and skills

/// <param name="Rank">JobConfig _iJobRank: 1 Novice, 2 first job, 3 second, 4 transcendent, 5 third.</param>
/// <param name="Skills">Skills in its own tree.</param>
public sealed record PanelClassSummary(long Id, string Name, string Icon, int Rank, long ParentId, int Skills);

/// <param name="Id">The skill id (SkillConfig _iSkillID), the same for every level.</param>
/// <param name="Passive">SkillConfig _iBasicType 0.</param>
public sealed record PanelSkillSummary(long Id, string Name, string Icon, int MaxLevel, bool Passive);

/// <summary>A cell of a tree: row and column from 1, as the client's skill window places it (7 columns).</summary>
public sealed record PanelSkillCell(int Row, int Col, PanelSkillSummary Skill);

public sealed record PanelSkillTree(PanelClassSummary Class, int Rows, IReadOnlyList<PanelSkillCell> Cells);

/// <param name="Line">The class's line from the first job to it.</param>
/// <param name="Next">The classes it can advance to.</param>
/// <param name="Trees">The trees it plays with, its own first.</param>
public sealed record PanelClassDetail(PanelClassSummary Class, string BigIcon, IReadOnlyList<PanelRun> Description, IReadOnlyList<PanelClassSummary> Line,
    IReadOnlyList<PanelClassSummary> Next, IReadOnlyList<PanelSkillTree> Trees);

/// <param name="Level">The level the skill must have; -1 means the two skills exclude each other.</param>
public sealed record PanelSkillRequirement(long SkillId, string Name, string Icon, int Level);

/// <param name="Cost">"SP 12", "HP 5%", an item's count; empty when free.</param>
public sealed record PanelSkillLevel(int Level, IReadOnlyList<PanelRun> Description, double CooldownSeconds, double CastSeconds, string Cost,
    IReadOnlyList<PanelSkillRequirement> Requires);

/// <param name="Element">The attack element's name; empty for a skill without one.</param>
/// <param name="DamageType">"Physical", "Magical", "Recovery" or empty.</param>
/// <param name="RangeMeters">The cast range, 0 for self or melee.</param>
/// <param name="Unlocks">The skills that need this one.</param>
public sealed record PanelSkillDetail(PanelSkillSummary Skill, PanelClassSummary? Class, string Element, string DamageType, double RangeMeters,
    IReadOnlyList<PanelSkillLevel> Levels, IReadOnlyList<PanelSkillRequirement> Unlocks);

// ---------------------------------------------------------------- pets and mounts

/// <param name="Picture">The pet's illustration (PetBaseConfig _kEncyclopediaPic).</param>
/// <param name="Camp">The pet's camp, 1 to 4 (PetBaseConfig _iCamp).</param>
public sealed record PanelPetSummary(long Id, string Name, int Quality, string QualityName, string Icon, string Picture, int Camp);

/// <param name="Stats">Its own HP, attack and defence at that level and its first star, as the pet window shows them.</param>
public sealed record PanelPetGrowth(int Level, IReadOnlyList<PanelStat> Stats, long Exp, int FightPower, int AssistPower);

/// <param name="Role">"Active", "Passive", "Protect" or "Core".</param>
public sealed record PanelPetSkill(string Role, long SkillId, string Name, string Icon, IReadOnlyList<PanelRun> Description);

/// <param name="Stage">The step inside a star, 0 to 3; stars 1-5 have four, star 6 one.</param>
/// <param name="Multiplier">How much of its base stats the pet has (PetStarConfig _iBaseAttr / 10000).</param>
/// <param name="Fight">What the pet gives its owner while it fights (PetStarConfig _kFightSkill of the star).</param>
/// <param name="Collection">What the owner keeps once the pet is collected (PetStarConfig _kCollectAttr of the star).</param>
/// <param name="Cost">Fragments to reach this step.</param>
public sealed record PanelPetStar(int Star, int Stage, double Multiplier, int FightPower, int AssistPower, IReadOnlyList<PanelStat> Fight,
    IReadOnlyList<PanelStat> Collection, PanelAmount? Cost, IReadOnlyList<PanelPetSkill> Skills);

/// <param name="Wild">The monster of the same name in the field, when there is one.</param>
/// <param name="Fragment">What a duplicate turns into (PetBaseConfig _kDecompose).</param>
public sealed record PanelPet(PanelPetSummary Pet, IReadOnlyList<PanelPetGrowth> Growth, IReadOnlyList<PanelPetStar> Stars,
    PanelMonsterSummary? Wild, PanelAmount? Fragment);

/// <param name="DurationSeconds">How long the item lends the mount; 0 = for good.</param>
public sealed record PanelMountSource(long ItemId, string Item, int Quality, string Icon, long DurationSeconds);

/// <param name="SpeedPercent">The move speed it adds, in percent (attribute 1131, per 10000).</param>
/// <param name="Stats">Its other attributes.</param>
/// <param name="FashionScore">The appearance's fashion score (WardRobeConfig).</param>
public sealed record PanelMount(long Id, string Name, int Quality, string QualityName, string Icon, double SpeedPercent,
    IReadOnlyList<PanelStat> Stats, IReadOnlyList<PanelMountSource> Items, long FashionScore);

// ---------------------------------------------------------------- shops

/// <param name="Id">ShopConfig _iClassID.</param>
public sealed record PanelShopSummary(long Id, string Name, int Goods, string Currency);

/// <param name="Item">The goods and how many one purchase gives.</param>
public sealed record PanelShopGood(PanelAmount Item, long CurrencyId, string Currency, string CurrencyIcon, long Price);

public sealed record PanelShop(PanelShopSummary Shop, IReadOnlyList<PanelShopGood> Goods);

// ---------------------------------------------------------------- combat rules, monsters, maps

/// <param name="Percent">The damage change in percent: 50 = +50 %, -25 = -25 %.</param>
public sealed record PanelElementFactor(int Element, string Name, int Percent);

/// <param name="Defender">The element of the one hit.</param>
/// <param name="Attacks">The change for each attacking element.</param>
public sealed record PanelElementRow(int Defender, string Name, IReadOnlyList<PanelElementFactor> Attacks);

/// <param name="FromGap">Player level minus monster level, from (inclusive).</param>
/// <param name="Percent">The share of the EXP the kill pays.</param>
public sealed record PanelExpGap(int FromGap, int ToGap, int Percent);

/// <param name="Members">Party members on the map.</param>
public sealed record PanelPartyBonus(int Members, int Percent);

public sealed record PanelCombatRules(IReadOnlyList<PanelElementRow> Elements, IReadOnlyList<PanelExpGap> LevelGap, IReadOnlyList<PanelPartyBonus> Party);

/// <param name="Kind">"Kill", "Bonus Time" or "Low-level bonus".</param>
/// <param name="MinPlayerLevel">The player levels the row pays at.</param>
/// <param name="Maps">The maps the row is limited to; empty = everywhere.</param>
public sealed record PanelExpRow(string Kind, long Min, long Max, int MinPlayerLevel, int MaxPlayerLevel, IReadOnlyList<string> Maps);

/// <param name="Exp">Base EXP per kill (Job EXP is the same), before the level-gap and party corrections.</param>
/// <param name="Taken">The damage the monster takes from each attacking element.</param>
public sealed record PanelMonsterCombat(IReadOnlyList<PanelExpRow> Exp, IReadOnlyList<PanelElementFactor> Taken);

/// <param name="Functions">What the NPC offers: its title, warps, the shops and windows it opens.</param>
/// <param name="ShopId">The shop it runs (ShopConfig _iClassID); 0 = none.</param>
public sealed record PanelMapNpc(long NpcId, string Name, string Title, string Icon, IReadOnlyList<string> Functions, long ShopId, int Count);

/// <param name="Via">The portals that lead there.</param>
public sealed record PanelMapLink(long MapId, string Map, string Image, IReadOnlyList<string> Via);

/// <param name="Type">"Town", "Field", "Dungeon floor", "Territory war".</param>
/// <param name="MinLevel">The lowest recommended level (OnHookMonsterRecommendConfig); 0 when the client gives none.</param>
/// <param name="Kafra">A Kafra can send players here.</param>
/// <param name="Parent">The map a dungeon floor belongs to.</param>
/// <param name="Floors">The dungeon floors entered from here.</param>
public sealed record PanelMapWorld(long Id, string Type, int MinLevel, int MaxLevel, bool Kafra, IReadOnlyList<PanelMapNpc> Npcs,
    IReadOnlyList<PanelMapLink> Links, PanelMapLink? Parent, IReadOnlyList<PanelMapLink> Floors);

// ---------------------------------------------------------------- calendar and MVPs

public sealed record PanelEventTime(DateTimeOffset Start, DateTimeOffset End);

/// <param name="Level">The base level it needs (DailyActivityConfig _iiLv).</param>
/// <param name="Period">The client's own words for when it runs.</param>
/// <param name="Times">Its openings from now to a week ahead, the current one first.</param>
public sealed record PanelEvent(long Id, string Name, string Icon, int Level, string Period, IReadOnlyList<PanelEventTime> Times);

/// <param name="Season">Long windows (NewActivityConfig): card pools, passes, season events, open now or later.</param>
public sealed record PanelSeasonEvent(long Id, string Name, DateTimeOffset Start, DateTimeOffset End);

/// <param name="Zone">The server's time zone, which the schedule is written in.</param>
/// <param name="DayStartHour">The hour a game day starts (05:00).</param>
/// <param name="AlwaysOpen">The server's testing switch runs every activity all day.</param>
/// <param name="OpenContent">The server keeps the season's content open (Ro3Emu:OpenContent).</param>
public sealed record PanelCalendar(DateTimeOffset Now, string Zone, int DayStartHour, bool AlwaysOpen, bool OpenContent,
    IReadOnlyList<PanelEvent> Events, IReadOnlyList<PanelSeasonEvent> Season);

/// <param name="State">"alive", "respawning" or "waiting" (not spawned yet since the server started).</param>
/// <param name="LastDeath">The latest kill; null when none is known.</param>
/// <param name="NextSpawn">When it comes back; null while alive.</param>
public sealed record PanelMvpTimer(long Id, PanelMonsterSummary Monster, int Level, long MapId, string Map, string MapImage, string State,
    DateTimeOffset? LastDeath, DateTimeOffset? NextSpawn, int RespawnSeconds);

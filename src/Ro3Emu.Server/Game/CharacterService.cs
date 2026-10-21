using System.Text.Json;
using Google.Protobuf;
using Romsg;
using Ro3Emu.Server.Accounts;
using Ro3Emu.Server.Accounts.Persistence;
using Ro3Emu.Server.Data;
using Ro3Emu.Server.Economy;
using Ro3Emu.Server.Maps;

namespace Ro3Emu.Server.Game;

/// <summary>
/// Character progression, bag and equipment: the AUTO_SYNC datasets 5066 (level/exp), 5067
/// (stat points), 5009 (bag, currencies), 5007 (equipment), the property lists the character window shows, and the
/// requests that change them. Numbers come from the client's own tables (AttriBaseLvConfig, AttributeConversionConfig,
/// BaseLvConfig, JobLvConfig, BasePointConfig, EquipConfig).
/// </summary>
public sealed partial class CharacterService(RoleStore roles, ItemStore itemStore, GameData data, World world, ILogger<CharacterService> log)
{
    public const uint RoleBaseInfoSync = 5066;
    public const uint BaseAttrSync = 5067;
    public const uint ItemsSync = 5009;
    public const uint EquipSync = 5007;
    public const uint OnePropertyChange = 31008;
    public const uint AoiAttrChange = 31048;
    public const uint EntityBaseChange = 31049;
    public const uint GetRewardNotify = 31322;
    public const uint ItemUse = 31323;
    public const uint PackItems = 31301;
    public const uint EquipItemOpr = 31400;
    public const uint AllocateAttrPoints = 31500;
    public const uint ResetAttrPoints = 31501;

    /// <summary>Free stat points of a new character (RO: 48), before BaseLvConfig._iStatusPoint per level.</summary>
    public const int StartingStatPoints = 48;
    public const int MaxStat = 99;

    /// <summary>Stat-point and equipment plans are 0-based (the equipment window labels plan key k "Build k+1").</summary>
    private const int StatPlan = 0;

    private const int ItemTypeCurrency = 1;
    private const int ItemTypeEquip = 5;
    private const int ZenyId = 1;
    private const int BaseExpItem = 100;
    private const int JobExpItem = 101;

    /// <summary>ItemAttrTypeEnum keys: 3 = appraised (equipment can be worn only when 1), 10001 = set-up source.</summary>
    internal const int AttrAppraise = 3;
    private const int AttrSetUpSource = 10001;

    /// <summary>Assigned by EmblemService: per-instance generated itemInfos for ItemConfig type 8/subtype 800.</summary>
    public Func<int, Dictionary<int, long>?>? EmblemAttrFactory { get; set; }

    /// <summary>Totem instance attributes, generated once for each acquired GUID (MG_Pack itemInfos 220..270).</summary>
    public Func<int, Dictionary<int, long>?>? TotemAttrFactory { get; set; }

    /// <summary>Transactional collection consumables. Called with StateLock held after common item validation.</summary>
    public List<Func<GameSession, ItemUseReq, InvItem, JsonElement, int, Task<int?>>> SpecialItemUse { get; } = new();

    /// <summary>EntityBase.equipInfo holds the item ids of equipment grids 1..7 (MainHand, ViceHand, Armor, ...).</summary>
    private const int EquipInfoSlots = 7;

    /// <summary>Property ids (AttributeConfig): base stats 1..6, derived 101..116, current HP/SP and level.</summary>
    private const int MaxHp = 101, MaxSp = 102, Atk = 103, Matk = 104, Speed = 113, BaseAspd = 115, Hp = 2000, Sp = 2001, Lv = 2002;

    /// <summary>Always in the self property list, even when no table contributes to them.</summary>
    private static readonly int[] SelfProperties = [1, 2, 3, 4, 5, 6, 101, 102, 103, 104, 105, 106, 107, 108, 109, 110, 111, 112, 113, 114, 115, 116, Hp, Sp, Lv];
    private static readonly int[] AoiProperties = [MaxHp, Hp, MaxSp, Sp, Speed, Lv, 2003];

    /// <summary>What a new character finds in the bag: Beginner Potions, a Novice Dagger, a Cotton Shirt, some Zeny.</summary>
    private static readonly (int Entry, int Count)[] StarterKit = [(1000000, 20), (2010121, 1), (2550120, 1), (ZenyId, 1000)];

    // ---- helpers ----

    internal void Save(GameSession session) => roles.SaveProgress(session.Role!.Id, session.Progress!);

    /// <summary>An item entered the bag: entry and the count gained (card collection first sight, achievement counters).</summary>
    public List<Action<GameSession, int, int>> ItemGained { get; } = new();
    internal sealed record FirstCardSightPlan(Action<RoleItemPlanBuilder> Persist, Action Apply);
    /// <summary>CardService enlists first sight in owned award/exchange commits and, for a kill, the slain monster's Codex kill (NPC id, 0 none).</summary>
    internal Func<GameSession, IReadOnlyList<int>, int, FirstCardSightPlan?>? StageFirstCardSight { get; set; }

    /// <summary>After a successful 31323 consume and its effects; quest engine queues objective evaluation.</summary>
    public List<Action<GameSession, int, int>> ItemUsed { get; } = new();

    /// <summary>LifeJobService owns action 607/315 item transactions; null means ordinary item effects.</summary>
    internal Func<GameSession, InvItem, JsonElement, int, Task<int?>>? LifeItemUse { get; set; }

    private static readonly AsyncLocal<LifeItemExchangeScope?> LifeItemExchange = new();

    private sealed class LifeItemExchangeScope(GameSession session, Action<RoleItemPlanBuilder> persist, Action publish)
    {
        public GameSession Session { get; } = session;
        public Action<RoleItemPlanBuilder> Persist { get; } = persist;
        public Action Publish { get; } = publish;
        public bool Active { get; set; } = true;
        public bool Committed { get; set; }
        public Action<GameSession>[]? PendingStateChanged { get; set; }
    }

    /// <summary>After a committed inventory, equipment or stat change; listeners defer any grant work.</summary>
    public List<Action<GameSession>> StateChanged { get; } = new();

    /// <summary>For CardService: item, stat and look updates after socket changes (caller holds the lock).</summary>
    internal async Task ItemsChangedAsync(GameSession session, bool stats)
    {
        var lifeUse = LifeItemExchange.Value;
        if (lifeUse is { Active: true, Committed: true, PendingStateChanged: { } pending }
            && ReferenceEquals(lifeUse.Session, session))
        {
            lifeUse.PendingStateChanged = null;
            foreach (var handler in pending) handler(session);
        }
        await SyncItemsAsync(session);
        if (stats) await SendStatsAsync(session);
        foreach (var handler in StateChanged) handler(session);
    }

    internal ItemBaseInfo ItemInfoOf(InvItem item) => ItemInfo(item);

    /// <summary>5007 after an equipped instance changes; caller holds StateLock.</summary>
    internal Task EquipmentChangedAsync(GameSession session) => SyncAsync(session, EquipSync, BuildEquip(session));

    internal static long NextBagSort(GameSession session, int pack) => NextSort(session, pack);

    internal static Task<IDisposable> Lock(GameSession session) => LockAsync(session);
    internal static Task<IDisposable> Lock(GameSession session, IReadOnlyList<Ownership.ReservationKey> additionalKeys)
        => LockAsync(session, additionalKeys: additionalKeys);
    internal static Task<IDisposable> Lock(GameSession session, Ownership.OwnerContext context)
    { context.AssertRole(session.Role!.Id); return LockAsync(session); }

    private static Ownership.OwnerContext RequireOwnedRole(GameSession session)
    {
        var context = session.Owners?.Current ?? throw new InvalidOperationException("Character Core requires an active owner capability.");
        context.AssertRole(session.Role!.Id);
        return context;
    }

    /// <summary>Installed by SeasonTalentService without a CharacterService constructor cycle.</summary>
    internal Func<GameSession, IEnumerable<(long Id, double Value)>>? TalentAttributes { get; set; }

    /// <summary>Runs after level / job level / job changes (SkillService resends skill points and the normal attack).</summary>
    public List<Func<GameSession, Task>> ProgressHandlers { get; } = new();

    /// <summary>Runs after equipped weapons or the active battle mount change.</summary>
    public List<Func<GameSession, Task>> CombatLoadoutHandlers { get; } = new();

    internal async Task CombatLoadoutChangedAsync(GameSession session)
    {
        foreach (var handler in CombatLoadoutHandlers) await handler(session);
    }

}

/// <summary>Per equipment grid (1..7) levels, kept with the role: strengthen, refine and refine luck (1/10000).</summary>
public sealed class EquipLevels
{
    public Dictionary<int, int> Strength { get; set; } = new();
    public Dictionary<int, int> Refine { get; set; } = new();
    public Dictionary<int, int> Luck { get; set; } = new();
}

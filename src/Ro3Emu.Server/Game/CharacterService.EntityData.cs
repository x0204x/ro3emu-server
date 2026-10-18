using System.Text.Json;
using Google.Protobuf;
using Romsg;
using Ro3Emu.Server.Accounts;
using Ro3Emu.Server.Accounts.Persistence;
using Ro3Emu.Server.Data;
using Ro3Emu.Server.Economy;
using Ro3Emu.Server.Maps;

namespace Ro3Emu.Server.Game;

public sealed partial class CharacterService
{
    // ---- entity data ----

    /// <summary>
    /// Recomputes what ENTITYSELF and AOI bases show of this player (RoleSessionService.PlayerEntity reads it from the
    /// session): the property list (base stats, derived stats, HP/SP, level) and the equipment look.
    /// </summary>
    public void Refresh(GameSession session)
    {
        var stats = Stats(session);
        session.EntityProperties = PropertyIds(stats).Select(id => (Id: (long)id, Value: stats[id])).ToArray();
        session.EquipLook = EquipInfo(session);
        session.Speed = stats[Speed];
    }

    /// <summary>
    /// romsg.RoleDetailInfo (MG_PublicServer.proto): a role's public card, its worn equipment (5007's shape) and its full
    /// property list, as LC_DAL_RolePublic.ParseRolePublicBaseInfo / _parseEquipInfo / _parseAllProperty read them. Used for
    /// snapshots other players see later (the Realm of the Gods hall of fame).
    /// </summary>
    internal RoleDetailInfo DetailInfo(GameSession session)
    {
        var role = session.Role!;
        var p = session.Progress!;
        var stats = Stats(session);
        var info = new RoleDetailInfo
        {
            Rolebase = new RolePublicBaseInfo { Roleid = role.Id, Name = role.Name, Career = p.Career, BaseLevel = p.Level,
                JobLevel = p.JobLevel, Gender = role.Sex },
            EquipInfo = BuildEquip(session),
            Selfproperty = new AllProperty { EntityID = role.Id },
        };
        info.Rolebase.Appearances.Add(session.Wardrobe?.CurUseAppearances.ToDictionary(a => a.Key, a => a.Value) ?? RoleProto.Appearances(role));
        foreach (var id in PropertyIds(stats))
            info.Selfproperty.Propertys.Add(new Property { PropertyId = id, Data = stats[id], BaseData = stats.Base.GetValueOrDefault(id, stats[id]), Vd64 = stats[id] });
        return info;
    }

    /// <summary>EntityBase.equipInfo: ItemConfig ids of grids 1..7, 0 for empty.</summary>
    private static long[] EquipInfo(GameSession session)
    {
        var slots = new long[EquipInfoSlots];
        foreach (var item in Equipped(session))
            if (item.Grid is >= 1 and <= EquipInfoSlots) slots[item.Grid - 1] = item.Entry;
        return slots;
    }

    /// <summary>
    /// Every number of the character window, the client's way (StatCalculator): raw contributions
    /// from AttriBaseLvConfig[career*1000+level], JobConfig._kAttri, worn gear (EquipConfig._kBasicAttribute) and
    /// AttributeConversionConfig per point of STR..LUK (_kAttribute2 for ranged jobs), grouped and rounded up.
    /// Current HP/SP come from the session (full when unset).
    /// </summary>
    public StatSheet Stats(GameSession session)
    {
        var p = session.Progress!;
        var raw = new Dictionary<int, double>();
        void Add(long id, double v) => raw[(int)id] = raw.GetValueOrDefault((int)id) + v;

        var matchAttributes = MatchAttributes?.Invoke(session);
        if (matchAttributes != null)
        {
            // A Survivor Battle career replaces the character's own build for the match (TimedBattleService.Chicken):
            // EatChickenGameCareer._iJobAttr and the worn match equipment, plus the buffs of the match.
            foreach (var (id, value) in matchAttributes) Add(id, value);
            foreach (var buff in session.Buffs)
                foreach (var (id, v) in buff.Changes) Add(id, v);
        }
        else AddOwnAttributes(session, p, Add);
        if (!raw.ContainsKey(Speed)) raw[Speed] = RoleSessionService.DefaultSpeed;
        raw[Speed] += session.SpeedBonus;
        if (!raw.ContainsKey(BaseAspd)) raw[BaseAspd] = 100;

        var sheet = EquipmentStunts.ApplyConversions(session, raw, data);
        if (matchAttributes == null) sheet = EmblemService.ApplyResonanceConversions(session, raw, sheet, data);
        long maxHp = sheet[MaxHp], maxSp = sheet[MaxSp];
        sheet[Hp] = Math.Clamp(session.CurrentHp ?? maxHp, 0, maxHp);
        sheet[Sp] = Math.Clamp(session.CurrentSp ?? maxSp, 0, maxSp);
        foreach (var provider in DynamicProperties)
            foreach (var (id, value) in provider(session))
            {
                // Independent active shield owners (totems and timed buffs) contribute to property 2003.
                if (id == 2003) sheet[id] = (long)Math.Clamp((decimal)sheet[id] + value, 0, long.MaxValue);
                else sheet[id] = value;
            }
        sheet[Lv] = p.Level;
        return sheet;
    }

    /// <summary>The character's own raw contributions (gear, cards, talents, buffs, level and job rows, conversions).</summary>
    private void AddOwnAttributes(GameSession session, RoleProgress p, Action<long, double> Add)
    {
        var baseStats = new double[6];
        for (int i = 0; i < 6; i++) baseStats[i] = p.Stats[i];
        foreach (var item in Equipped(session))
        {
            if (EquipRow(item.Entry) is not { } equip) continue;
            foreach (var (id, v) in BaseAttributes(item, equip).Concat(EquipmentAffixes.Entries(item)))
            {
                if (id is >= 1 and <= 6) baseStats[id - 1] += v;
                else Add(id, v);
            }
            // enchant stats are per instance; base stats also feed their derived conversions.
            foreach (var enchant in item.Enchantments.Values)
                foreach (var (id, v) in enchant.Attr)
                {
                    if (id is >= 1 and <= 6) baseStats[id - 1] += v;
                    else Add(id, v);
                }
        }
        foreach (var (id, value) in EquipmentStunts.FlatAttributes(session, data))
        {
            if (id is >= 1 and <= 6) baseStats[id - 1] += value;
            else Add(id, value);
        }
        // Grid levels: StrengthenTable / RefiningTable _kAttr are totals at a level, counted for a
        // worn grid (inferred); resonance (§5) adds the reached Strengthen/RefiningEffectTable row's _kEffect.
        foreach (var item in Equipped(session).Where(i => i.Grid <= EquipInfoSlots))
        {
            foreach (var row in new[] { StrengthenRow(item.Grid, session.EquipLevels.Strength.GetValueOrDefault(item.Grid)), RefineRow(item.Grid, session.EquipLevels.Refine.GetValueOrDefault(item.Grid)) })
                if (row is { } r)
                    foreach (var (id, v) in GameData.Pairs(r, "_kAttr")) Add(id, v);
        }
        foreach (var table in new[] { "StrengthenEffectTable", "RefiningEffectTable" })
            if (ResonanceRow(session, table) is { } effect)
                foreach (var (id, v) in GameData.Pairs(effect, "_kEffect")) Add(id, v);
        foreach (var card in session.Items.Where(i => i.Grid < 0))
        {
            int tier = (int)(card.Attrs?.GetValueOrDefault(AttrCardStar) ?? 0);
            int level = (int)(card.Attrs?.GetValueOrDefault(AttrCardLevel) ?? 0);
            if (CardRow(card.Entry, tier) is not { } cardRow) continue;
            foreach (var (id, v) in GameData.Pairs(cardRow, "_kBasicAttribute"))
            {
                if (id is >= 1 and <= 6) baseStats[id - 1] += v;
                else Add(id, v);
            }
            if (data.Row("ItemConfig", card.Entry) is { } itemRow)
            {
                long part = GameData.Int(itemRow, "_iCardPart"), quality = GameData.Int(itemRow, "_iQuality");
                if (CardUpgradeRow(level, part, quality) is { } upgrade)
                    foreach (var (id, v) in GameData.Pairs(upgrade, "_iAttr"))
                    {
                        if (id is >= 1 and <= 6) baseStats[id - 1] += v;
                        else Add(id, v);
                    }
            }
        }
        // The active Soul Echo plan contributes raw attributes; applying the shared final-stat
        // pipeline is inferred because the original server aggregation order is not captured.
        foreach (var emblem in EmblemService.Worn(session))
            foreach (var (id, value) in EmblemService.Attributes(emblem, data))
            {
                if (id is >= 1 and <= 6) baseStats[id - 1] += value;
                else Add(id, value);
            }
        // LC_SeasonTalent's property overview reads the selected plan's target-level effect once.
        // Base stats 1..6 must feed conversion below; other attrs enter StatCalculator as raw contributions (inferred).
        foreach (var (id, value) in TalentAttributes?.Invoke(session) ?? [])
        {
            if (id is >= 1 and <= 6) baseStats[id - 1] += value;
            else Add(id, value);
        }
        foreach (var provider in ExtraAttributes)
            foreach (var (id, value) in provider(session))
            {
                if (id is >= 1 and <= 6) baseStats[id - 1] += value;
                else Add(id, value);
            }
        for (int i = 0; i < 6; i++) Add(i + 1, baseStats[i]);
        foreach (var buff in session.Buffs)
            foreach (var (id, v) in buff.Changes) Add(id, v);
        if (BaseRow(p.Career, p.Level) is { } flat)
            foreach (var (id, v) in GameData.Pairs(flat, "_kAttri")) Add(id, v);
        foreach (var (id, v) in data.JobAttributes(p.Career)) Add(id, v);
        bool ranged = data.Row("JobConfig", p.Career) is { } job && GameData.Int(job, "_iDistanceType") == 2;
        for (int stat = 1; stat <= 6; stat++)
        {
            if (data.Row("AttributeConversionConfig", stat) is not { } conversion) continue;
            foreach (var (id, perPoint) in GameData.Pairs(conversion, ranged ? "_kAttribute2" : "_kAttribute1")) Add(id, perPoint * baseStats[stat - 1]);
        }
    }

    /// <summary>The self property list: the fixed set plus every member attribute the sheet carries (1031 ...).</summary>
    private static IEnumerable<int> PropertyIds(StatSheet sheet) => SelfProperties.Union(sheet.Data.Keys).Order();

    private JsonElement? BaseRow(int career, int level)
    {
        // Every job has rows 1..100; fall back to the Novice table for unknown careers.
        return data.Row("AttriBaseLvConfig", career * 1000L + level) ?? data.Row("AttriBaseLvConfig", 1000L * 1000 + level);
    }

    /// <summary>After any change of stats: the window (31008, owner) and head-info/HP bars (31048, owner and viewers).</summary>
    /// <summary>Stats changed outside this service (buffs): the window, HP bars and the AOI.</summary>
    public Task RefreshStatsAsync(GameSession session) => SendStatsAsync(session);

    /// <summary>Configured contributions from other owned systems (pet lineup/collection).</summary>
    public List<Func<GameSession, IEnumerable<(long Id, double Value)>>> ExtraAttributes { get; } = new();

    /// <summary>A match owner's replacement build (null: the character's own), e.g. a Survivor Battle career.</summary>
    public Func<GameSession, IReadOnlyList<(long Id, double Value)>?>? MatchAttributes { get; set; }

    /// <summary>Runtime properties such as an absorb shield, projected on full stats and late AOI snapshots.</summary>
    public List<Func<GameSession, IEnumerable<(int Id, long Value)>>> DynamicProperties { get; } = new();

    /// <summary>Set by BuffService: puts a buff on the player (item use action 203).</summary>
    public Func<GameSession, int, int, Task>? GiveBuff { get; set; }

    private async Task SendStatsAsync(GameSession session)
    {
        var oldIds = session.EntityProperties.Select(p => (int)p.Id).ToHashSet();
        Refresh(session);
        var stats = Stats(session);
        var all = new AllProperty { EntityID = session.Role!.Id };
        foreach (var id in PropertyIds(stats).Union(oldIds).Union(session.SentPropertyIds).Order())
        {
            long value = stats[id];
            all.Propertys.Add(new Property { PropertyId = id, Data = value, BaseData = stats.Base.GetValueOrDefault(id, value), Vd64 = value });
            session.SentPropertyIds.Add(id);
        }
        await session.SendGameAsync(OnePropertyChange, new Romsg.OnePropertyChange { Property = all });

        var aoi = new AOIAttrChange { EntityUuid = (ulong)session.Role.Id };
        foreach (var id in AoiProperties) aoi.Property.Add(new AoIProperty { Idx = id, Data = stats[id], BaseData = stats[id] });
        await ToSelfAndViewersAsync(session, AoiAttrChange, aoi);
    }

    /// <summary>
    /// Sets current HP and/or SP (clamped to the maxima; null = unchanged) and tells the player (31008) and everyone
    /// around (31048)
    /// </summary>
    public async Task SetHpSpAsync(GameSession session, long? hp, long? sp)
    {
        var before = Stats(session);
        if (hp is { } h) session.CurrentHp = Math.Clamp(h, 0, before[MaxHp]);
        if (sp is { } s) session.CurrentSp = Math.Clamp(s, 0, before[MaxSp]);
        var stats = Stats(session);
        session.EntityProperties = PropertyIds(stats).Select(id => (Id: (long)id, Value: stats[id])).ToArray();
        var all = new AllProperty { EntityID = session.Role!.Id };
        var aoi = new AOIAttrChange { EntityUuid = (ulong)session.Role.Id };
        foreach (var id in new[] { Hp, Sp })
        {
            all.Propertys.Add(new Property { PropertyId = id, Data = stats[id], BaseData = 0, Vd64 = stats[id] });
            aoi.Property.Add(new AoIProperty { Idx = id, Data = stats[id] });
        }
        await session.SendGameAsync(OnePropertyChange, new Romsg.OnePropertyChange { Property = all });
        await ToSelfAndViewersAsync(session, AoiAttrChange, aoi);
    }

    private async Task ToSelfAndViewersAsync(GameSession session, uint msgId, IMessage body)
    {
        await session.SendGameAsync(msgId, body);
        if (!session.Owners!.AllowsPresentation(session)) return;
        var viewers = world.Viewers(session);
        if (viewers.Count == 0) return;
        var prepared = new PreparedMessage(msgId, body); // serialized once for every observer
        foreach (var viewer in viewers)
        {
            if (!session.Owners!.AllowsPresentation(session)) break;
            try { await viewer.SendPreparedAsync(prepared); }
            catch (Exception e) when (e is IOException or ObjectDisposedException or InvalidOperationException) { }
        }
    }
}

using System.Globalization;
using System.Runtime.CompilerServices;
using Ro3Emu.Server.Data;
using Ro3Emu.Server.Data.Config;

namespace Ro3Emu.Server.Game;

/// <summary>
/// What riding a mount changes (inferred: the client only reads the result).
/// <list type="bullet">
/// <item>A ridden mount's _kBindBuff rows (CartRemouldTable, MountsConfig) stand while it is ridden. The wolf's
/// 1331001 "Mount Buff" is Wolf Taming's "While mounted on the Demonic Wolf, Movement Speed increases by 10% and Normal
/// Attack becomes [Warg Strike]" (1131 +1000, mode 1 of 13099 and 13312); the gryphon's Gryphon State switches 11099 the
/// same way; the Peco Peco's and the dragon's are markers.</item>
/// <item>Riding and Dragon Training carry SpecialEffectConfig rows [17, mount _iSubType, attribute, value], a type past
/// the client's enum_SpecialEffectType (it stops at 15): "increase Movement Speed by ${1}% while mounted" (Riding on
/// subtypes 2, 6 and 7, Dragon Training on 6).</item>
/// <item>Dragon Training's "increases the damage of [Dragon Breath (Fire)] and [Dragon Breath (Water)] by ${2}%" is its
/// BuffConfig 11318 row of its level (11317, the same name, has no tuples), and Wolf's Fang's "While mounted on the Demonic
/// Wolf, Physical ATK increases by ${1}%" is the row its own 13311 row's _kLogicData[0] names (13312); each stands while
/// its mount is ridden.</item>
/// <item>Wolf Taming: "When the Demonic Wolf is not mounted and follows the Ranger, it howls every ${2} sec, increasing
/// the Ranger's Physical ATK by ${3}% for ${4} sec." The leased wolf follows while it is not ridden; its howl is BuffConfig
/// 1332501 "Warg Howl" (1032 +500, the ${3} 5%; linked by the name).</item>
/// </list>
/// </summary>
public sealed class RidingEffects : IGameTicker
{
    private const int RideAttribute = 17;
    private const long DragonSubType = 6, WolfSubType = 4;
    private const int DragonTraining = 11307, DragonTrainingBuff = 11318, WolfsFang = 13311, WolfTaming = 13310;
    private const int WolfMount = 1500400, HowlBuff = 1332501;
    // The passive groups whose rows carry type 17 special effects: Riding (Knight, Crusader) and Dragon Training.
    private static readonly int[] RideSpeedPassives = [11107, 11407, DragonTraining];
    // The buffs stand while riding; scene loads and job changes set them again, so a day is "permanent" (inferred).
    private const int PermanentMs = 86_400_000;

    private readonly GameData _data;
    private readonly CombatService _combat;
    private readonly BuffService _buffs;
    private readonly MountService _mounts;
    private readonly HashSet<int> _ridingBuffs;
    private readonly SessionRegistry _sessions;
    private readonly GameLoop _loop;
    private readonly ConditionalWeakTable<GameSession, StrongBox<long>> _nextHowl = [];
    private TimeSpan _lastTick;

    public RidingEffects(GameData data, CombatService combat, BuffService buffs, MountService mounts, CharacterService character,
        SceneService scenes, SkillService skills, SessionRegistry sessions, GameLoop loop)
    {
        _sessions = sessions;
        _loop = loop;
        _data = data;
        _combat = combat;
        _buffs = buffs;
        _mounts = mounts;
        _ridingBuffs = AllRidingBuffs(data);
        character.ExtraAttributes.Add(RideAttributes);
        mounts.RideChanged.Add(SyncAsync);
        // It touches only the loaded session's own buffs, inside the scene load's scope.
        scenes.AddOwnedAfterLoaded(SyncAsync);
        skills.Learned.Add(SyncAsync);
        character.JobChanged.Add(SyncAsync);
    }

    /// <summary>Every buff riding can grant: the mounts' bind buffs and the two passives' rows of every level.</summary>
    private static HashSet<int> AllRidingBuffs(GameData data)
    {
        var ids = new HashSet<int>();
        foreach (var row in data.Table("CartRemouldTable").Values) ids.UnionWith(new CartRemouldTable(row).BindBuff.Select(id => (int)id));
        foreach (var row in data.Table("MountsConfig").Values) ids.UnionWith(new MountsConfig(row).BindBuff.Select(id => (int)id));
        for (int level = 1; level <= 10; level++)
        {
            if (BuffConfig.Get(data, DragonTrainingBuff * 100L + level) is { } dragon) ids.Add((int)dragon.Id);
            if (BuffConfig.Get(data, WolfsFang * 100L + level) is { LogicData: [var fang, ..] }) ids.Add((int)fang);
        }
        return ids;
    }

    private IEnumerable<(long Id, double Value)> RideAttributes(GameSession s)
    {
        long subType = _mounts.Ridden(s).SubType;
        if (subType == 0) yield break;
        foreach (int group in RideSpeedPassives)
        {
            if (_combat.LearnedPassive(s, group) is not { } skill) continue;
            foreach (long id in skill.SpecialEffect)
                if (SpecialEffectConfig.Get(_data, id) is { } special)
                    foreach (var effect in special.SpecialEffect)
                        if (effect is [RideAttribute, var mount, var attribute, var value] && mount == subType) yield return (attribute, value);
        }
    }

    /// <summary>The buffs riding grants now: the mount's bind buffs and the passives that need its subtype.</summary>
    private HashSet<int> Wanted(GameSession s)
    {
        var (bind, subType) = _mounts.Ridden(s);
        var wanted = bind.Select(id => (int)id).ToHashSet();
        if (subType == DragonSubType && _combat.LearnedPassive(s, DragonTraining) is { } dragon
            && BuffConfig.Get(_data, DragonTrainingBuff * 100L + dragon.Level) is { } dragonBuff)
            wanted.Add((int)dragonBuff.Id);
        if (subType == WolfSubType && _combat.LearnedPassive(s, WolfsFang) is { } fang
            && BuffConfig.Get(_data, WolfsFang * 100L + fang.Level) is { LogicData: [var fangBuff, ..] })
            wanted.Add((int)fangBuff);
        return wanted;
    }

    private async Task SyncAsync(GameSession s)
    {
        if (s.Role is not { } role || s.Dead) return;
        var wanted = Wanted(s);
        long now = MonsterService.NowMs;
        foreach (var stale in s.Buffs.Where(b => _ridingBuffs.Contains(b.ConfigId) && !wanted.Contains(b.ConfigId)).ToList())
            await _buffs.RemoveAsync(s, stale);
        foreach (int id in wanted)
            if (!s.Buffs.Any(b => b.ConfigId == id && b.ExpireMs > now)) await _buffs.AddAsync(s, id, PermanentMs, role.Id);
    }

    /// <summary>Wolf Taming's howl: each second, a Ranger whose leased wolf follows unmounted howls when its ${2} is up.</summary>
    public void Tick(GameTime time)
    {
        if (time.Now - _lastTick < TimeSpan.FromSeconds(1)) return;
        _lastTick = time.Now;
        long now = MonsterService.NowMs;
        foreach (var s in _sessions.All())
        {
            if (s.Role is not { } role || s.Dead || s.SceneLoading || !CareerSpecialService.IsLeasedMount(s, WolfMount)
                || MountService.Rider(s) == WolfMount
                || _combat.LearnedPassive(s, WolfTaming) is not { DescData: [_, var every, _, var lasts, ..] }) continue;
            var next = _nextHowl.GetOrCreateValue(s);
            if (now < next.Value) continue;
            // Keep the cadence: a howl on time counts from when it was due, not from this one-second tick.
            next.Value = (next.Value > 0 && now - next.Value < 1000 ? next.Value : now) + Seconds(every);
            int duration = (int)Seconds(lasts);
            _loop.PostOwned(s, () => _buffs.AddAsync(s, HowlBuff, duration, role.Id), "wolf howl");
        }
    }

    private static long Seconds(string value) =>
        double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out double seconds) ? (long)(seconds * 1000) : 0;
}

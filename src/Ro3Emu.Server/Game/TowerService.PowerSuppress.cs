using System.Runtime.CompilerServices;
using System.Text.Json;
using Ro3Emu.Server.Data;
using Ro3Emu.Server.Maps;

namespace Ro3Emu.Server.Game;

/// <summary>
/// PowerSuppressConfig (one row; PetFloorConfig._iPowerSuppress = 1 on every floor). LC_ClimbTowerHelper.
/// _refreshDifficultyTag (lua :431-481) compares the role's power (PlayerChangePower.currentPower) as a percentage of
/// the floor's recommended power after the daily reduction (_iPowerRecommend x GetDifficultyRatio / 10000) with
/// _kApplicableLevel [0,80,100,110]: the last threshold reached picks the tag. The client never reads
/// _kApplicableBuff [5000001,5000101,0,5000201]; *(inferred)* the server puts the buff at the same index on the floor's
/// monsters (BuffConfig "Tower Boss Stat Adjustment", _iTime -1, cleared with the scene): below 80% 5000001 (damage
/// and damage reduction -10%), 80-100% 5000101 (-5%), 100-110% none, from 110% 5000201 (+10%). A weaker team meets a
/// weaker floor. Its 215 (per_dmg) scales the damage the monster deals and its 216 (per_res_dmg) the damage it takes,
/// as MvpService.FrenzyDealt / FrenzyTaken do, on every tower path: pet and player hits on it, its hits on pets and the
/// player (CombatService.MonsterDamageAdjusters / DamageAdjusters, no allocation per hit) *(inferred; the floor's HP stays the captured 1200 x decay, TowerDecayTests)*. Monster unit buffs do not
/// otherwise reach the damage formula; if they ever do generally, drop these adjusters. The power is the server's
/// 5066 / 31502 current power (normal + season).
/// </summary>
public sealed partial class TowerService
{
    internal readonly record struct Suppression(int Buff, long Dealt, long Taken);

    private readonly ConditionalWeakTable<MonsterUnit, StrongBox<Suppression>> _suppressed = new();

    private void RegisterSuppression()
    {
        _combat.DamageAdjusters.Add((_, monster, _, _, damage) => SuppressTaken(monster, damage));
        _combat.MonsterDamageAdjusters.Add((monster, damage, monsterAttacks) =>
            monsterAttacks ? SuppressDealt(monster, damage) : SuppressTaken(monster, damage));
    }

    /// <summary>A floor monster's hit with its suppression buff's 215 (per_dmg).</summary>
    internal long SuppressDealt(MonsterUnit monster, long damage) =>
        damage > 0 && _suppressed.TryGetValue(monster, out var box) && box.Value.Dealt != 0
            ? Math.Max(0, damage * (10000 + box.Value.Dealt) / 10000) : damage;

    /// <summary>A hit on a floor monster with its suppression buff's 216 (per_res_dmg, damage reduction).</summary>
    internal long SuppressTaken(MonsterUnit monster, long damage) =>
        damage > 0 && _suppressed.TryGetValue(monster, out var box) && box.Value.Taken != 0
            ? Math.Max(0, damage * (10000 - box.Value.Taken) / 10000) : damage;

    /// <summary>The PowerSuppressConfig buff and its 215/216 values for a role entering a floor.</summary>
    internal Suppression Suppress(GameSession s, JsonElement floor, int difficultyRatio)
    {
        var power = PowerService.Current(s); // the full character power (PowerService), not only the Soul Echo part
        return Suppress(_data, floor, power.NormalPower + power.SeasonPower, difficultyRatio);
    }

    internal static Suppression Suppress(GameData data, JsonElement floor, long power, int difficultyRatio)
    {
        if (data.Row("PowerSuppressConfig", GameData.Int(floor, "_iPowerSuppress")) is not { } row) return default;
        long recommend = GameData.Int(floor, "_iPowerRecommend") * Math.Max(1, difficultyRatio) / 10000;
        // The client: pct = power / (recommend * ratio / 10000) * 100; every threshold it reaches overwrites the tag.
        double percent = recommend > 0 ? power * 100.0 / recommend : double.MaxValue;
        var levels = Numbers(row, "_kApplicableLevel");
        var buffIds = Numbers(row, "_kApplicableBuff");
        int buff = 0;
        for (int i = 0; i < levels.Length && i < buffIds.Length; i++)
            if (percent >= levels[i]) buff = (int)buffIds[i];
        if (buff <= 0 || data.Row("BuffConfig", buff) is not { } config) return new(0, 0, 0);
        long dealt = 0, taken = 0;
        foreach (var (id, value) in GameData.Pairs(config, "_kChangeAttri"))
        {
            if (id == 215) dealt += (long)value;
            else if (id == 216) taken += (long)value;
        }
        return new(buff, dealt, taken);
    }

    private static long[] Numbers(JsonElement row, string field) =>
        row.TryGetProperty(field, out var list) && list.ValueKind == JsonValueKind.Array
            ? list.EnumerateArray().Select(v => (long)(GameData.Number(v) ?? 0)).ToArray() : [];
}

using Ro3Emu.Server.Data;
using Ro3Emu.Server.Maps;

namespace Ro3Emu.Server.Game;

public sealed partial class BuffService
{
    /// <summary>(inferred scope) Only a guardian's Freeze/Petrify/Sleep breaks on a hit: the same groups from ordinary
    /// skills keep their previous behaviour until the rule is established and tested for every source.</summary>
    private static bool BreaksOnDamage(ActiveBuff buff) =>
        buff.IsGuardianOwned && buff.ConfigId / 100 is FreezeGroup or PetrifyGroup or SleepGroup;

    /// <summary>BuffConfig Freeze/Petrify/Sleep descriptions remove control on a hit. Freeze/Petrify explicitly
    /// link their follow-up slow and duration in _kLogicData. (inferred) Only positive committed HP damage breaks it.</summary>
    internal async Task BreakHitControlsAsync(GameSession player)
    {
        foreach (var buff in player.Buffs.Where(BreaksOnDamage).ToArray())
        {
            await RemoveAsync(player, buff);
            if (_data.Row("BuffConfig", buff.ConfigId) is not { } row) continue;
            var next = GuardianPetRules.Numbers(row, "_kLogicData");
            if (next.Length == 2 && next[1] > 0) await AddGuardianAsync(player, (int)next[0], (int)next[1], buff.SourceId);
        }
    }

    internal async Task BreakHitControlsAsync(WorldUnit unit)
    {
        if (!_unitBuffs.TryGetValue(unit.Uid, out var active)) return;
        foreach (var buff in active.Where(BreaksOnDamage).ToArray())
        {
            await RemoveUnitBuffAsync(unit, buff);
            if (_data.Row("BuffConfig", buff.ConfigId) is not { } row) continue;
            var next = GuardianPetRules.Numbers(row, "_kLogicData");
            if (next.Length == 2 && next[1] > 0) await AddGuardianAsync(unit, (int)next[0], (int)next[1], buff.SourceId);
        }
    }
}

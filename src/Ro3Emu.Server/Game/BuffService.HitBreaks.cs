using Ro3Emu.Server.Data;
using Ro3Emu.Server.Maps;

namespace Ro3Emu.Server.Game;

public sealed partial class BuffService
{
    /// <summary>Lex Aeterna 1410201: "The [Damage Taken Increase] effect lasts ${2} seconds and disappears after the target takes damage."</summary>
    private const int LexAeternaGroup = 14102;

    /// <summary>Freeze, Petrify, Sleep and Lex Aeterna break on a committed hit, regardless of source.</summary>
    private static bool BreaksOnDamage(ActiveBuff buff) => buff.ConfigId / 100 is FreezeGroup or PetrifyGroup or SleepGroup or LexAeternaGroup
        || !buff.IsGuardianOwned && buff.StateOrdinal is 11 or 12 or 19; // the same states from another row (Petrify 4340301)

    /// <summary>BuffConfig Freeze/Petrify/Sleep descriptions remove control on a hit. Freeze/Petrify explicitly
    /// link their follow-up slow and duration in _kLogicData; the follow-up keeps the broken control's owner, a guardian's
    /// or an ordinary source's (inferred). (inferred) Only positive committed HP damage breaks it.</summary>
    internal async Task BreakHitControlsAsync(GameSession player)
    {
        foreach (var buff in player.Buffs.Where(BreaksOnDamage).ToArray())
        {
            await RemoveAsync(player, buff);
            if (_data.Row("BuffConfig", buff.ConfigId) is not { } row) continue;
            var next = GuardianPetRules.Numbers(row, "_kLogicData");
            if (next.Length == 2 && next[1] > 0)
                await AddCoreAsync(player, (int)next[0], (int)next[1], buff.SourceId, null, guardianOwned: buff.IsGuardianOwned);
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
            if (next.Length == 2 && next[1] > 0)
                await AddSupportAsync(unit, (int)next[0], (int)next[1], buff.SourceId, guardianOwned: buff.IsGuardianOwned);
        }
    }
}

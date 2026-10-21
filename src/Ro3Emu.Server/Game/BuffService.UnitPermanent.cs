using Romsg;
using Ro3Emu.Server.Data;
using Ro3Emu.Server.Maps;

namespace Ro3Emu.Server.Game;

public sealed partial class BuffService
{
    /// <summary>A permanent unit buff (BuffConfig _iTime -1, NPCConfig _kBornEventBuff / AIConfig _iUnFightBUFF): no
    /// expiry, sent like a passive (expire_time 0). Rows without effects are still sent so the client has them.</summary>
    public async Task AddPermanentAsync(WorldUnit unit, int configId, long sourceId)
    {
        if (_data.Row("BuffConfig", configId) is not { } row || GameData.Int(row, "_iBuffID") <= 0) return;
        if (!_unitBuffs.TryGetValue(unit.Uid, out var list)) _unitBuffs[unit.Uid] = list = new();
        if (list.Any(b => b.ConfigId == configId && b.IsPassive)) return;
        var buff = new ActiveBuff(Interlocked.Increment(ref _guids), configId, long.MaxValue, 0,
            GameData.Pairs(row, "_kChangeAttri").Where(c => c.Value != 0).ToArray(), sourceId, IsPassive: true);
        list.Add(buff);
        var info = Wire(buff);
        await _world.ToUnitViewersAsync(unit, BuffUpdate, new BuffUpdateNotify { UnitId = unit.Uid, Buffs = { info } });
        await _world.ToUnitViewersAsync(unit, AoiAttrChange, new AOIAttrChange
        {
            EntityUuid = (ulong)unit.Uid, BuffInfo = new AoIBuffInfo { AddBuffs = { info } },
        });
        if (buff.Changes.Count > 0)
            foreach (var handler in UnitChanged) await handler(unit);
    }

    /// <summary>Remove every live buff of one BuffConfig id from a unit.</summary>
    public async Task RemoveUnitConfigAsync(WorldUnit unit, int configId)
    {
        if (!_unitBuffs.TryGetValue(unit.Uid, out var list)) return;
        foreach (var buff in list.Where(b => b.ConfigId == configId).ToArray()) await RemoveUnitBuffAsync(unit, buff);
    }
}

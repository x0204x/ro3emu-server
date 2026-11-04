using Romsg;
using System.Text.Json;
using Ro3Emu.Server.Data;
using Ro3Emu.Server.Maps;

namespace Ro3Emu.Server.Game;

public sealed partial class CombatService
{
    private static readonly int[] PublishedMonsterAttributes = [
        AttributeId.Atk, AttributeId.Matk, AttributeId.Def, AttributeId.Mdef, AttributeId.Hit, AttributeId.Flee, AttributeId.Cri,
        AttributeId.CastSpeed,
    ];

    /// <summary>BuffConfig._kChangeAttri uses the same AttributeConfig groups for enemy PATK/MATK/DEF/MDEF.
    /// (inferred) Overlay live changes on the resolved monster template, using the existing player/pet calculator.
    /// Never mutate the shared template: expiration and two monsters of the same kind stay independent.</summary>
    internal StatSheet MonsterStats(MonsterUnit unit)
    {
        var t = unit.Template;
        var raw = new Dictionary<int, double>
        {
            [AttributeId.MaxHp] = t.MaxHp, [AttributeId.Atk] = t.Atk, [AttributeId.Matk] = t.Matk, [AttributeId.Def] = t.Def,
            [AttributeId.Mdef] = t.Mdef,
            [AttributeId.Hit] = t.Hit, [AttributeId.Flee] = t.Flee, [AttributeId.Cri] = t.Cri, [AttributeId.Speed] = t.SpeedCm,
            [AttributeId.CastSpeed] = t.CastSpeed,
        };
        foreach (var (id, value) in _buffs.UnitCombatAttributes(unit)) raw[(int)id] = raw.GetValueOrDefault((int)id) + value;
        return StatCalculator.Compute(raw, _data);
    }

    private Fighter MonsterFighter(MonsterUnit unit)
    {
        var t = unit.Template;
        // The template as it was, without the per-hit attribute pass when nothing overlays it.
        if (!_buffs.HasUnitCombatBuffs(unit))
            return new(t.Atk, t.Matk, t.Hit, t.Cri, t.Level, t.Element) { Def = t.Def, Mdef = t.Mdef, Flee = t.Flee, Race = t.Race, Size = t.Size, Kind = KindOf(t) };
        var stats = MonsterStats(unit);
        return new(stats[AttributeId.Atk], stats[AttributeId.Matk], stats[AttributeId.Hit], stats[AttributeId.Cri], unit.Template.Level,
            unit.Template.Element)
        { Def = stats[AttributeId.Def], Mdef = stats[AttributeId.Mdef], Flee = stats[AttributeId.Flee], ResCri = stats[AttributeId.ResCri], Race = t.Race, Size = t.Size, Kind = KindOf(t) };
    }

    private long MonsterBuffDamage(MonsterUnit monster, JsonElement? skill, long amount)
    {
        if (!_buffs.HasUnitCombatBuffs(monster)) return amount;
        int element = skill is { } row && GameData.Int(row, "_iEleType") > 1
            ? (int)GameData.Int(row, "_iEleType") : monster.Template.Element;
        double bonus = _buffs.UnitCombatAttributes(monster).Where(c => RaisesDamage(c.Id, element)).Sum(c => c.Value);
        return (long)Math.Clamp(Math.Floor(amount * Math.Max(0, 1 + bonus / 10000)), 0, long.MaxValue);
    }

    private async Task PublishMonsterAttributesAsync(WorldUnit unit)
    {
        if (unit is not MonsterUnit monster) return;
        // Control states come from any source's buff (BuffService.StateFor): always publish the state and forbid masks.
        var changed = new AOIAttrChange
        {
            EntityUuid = (ulong)unit.Uid,
            StateInfo = new AoiObjStateInfo { Data = new CommonBigMask { Values = { (ulong)monster.StatusBits | _buffs.UnitStatus(unit) } } },
            StaggerInfo = new AoiObjStaggerInfo { StaggerMask = _buffs.UnitStaggerMask(unit) },
            ForbidInfo = new AoiObjForbidInfo { Data = new CommonBigMask { Values = { _buffs.UnitForbiddenMask(unit) } } },
        };
        // Buffs that do not count (BuffService.CountsInUnitStats) leave the template's stats and speed as they were; publish
        // only changes that do and, once the last of them is gone, the return to the template.
        bool guardian = _buffs.HasUnitCombatBuffs(monster);
        if (guardian || monster.BuffSpeedCm != null)
        {
            var stats = MonsterStats(monster);
            await _monsters.SetBuffSpeedAsync(monster, guardian ? (int)Math.Clamp(stats[AttributeId.Speed], 0, int.MaxValue) : null);
            foreach (int id in PublishedMonsterAttributes)
                changed.Property.Add(new AoIProperty { Idx = id, Data = stats[id], BaseData = stats.Base.GetValueOrDefault(id) });
        }
        await _world.ToUnitViewersAsync(unit, AoiAttrChange, changed);
    }
}

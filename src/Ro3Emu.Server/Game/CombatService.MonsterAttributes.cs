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
        foreach (var (id, value) in _buffs.UnitAttributes(unit)) raw[(int)id] = raw.GetValueOrDefault((int)id) + value;
        return StatCalculator.Compute(raw, _data);
    }

    private Fighter MonsterFighter(MonsterUnit unit)
    {
        var t = unit.Template;
        // 沒有有效狀態時直接使用模板，避免每擊重算空的屬性覆寫。
        if (!_buffs.HasUnitBuffs(unit))
            return new(t.Atk, t.Matk, t.Hit, t.Cri, t.Level, t.Element) { Def = t.Def, Mdef = t.Mdef, Flee = t.Flee };
        var stats = MonsterStats(unit);
        return new(stats[AttributeId.Atk], stats[AttributeId.Matk], stats[AttributeId.Hit], stats[AttributeId.Cri], unit.Template.Level,
            unit.Template.Element)
        { Def = stats[AttributeId.Def], Mdef = stats[AttributeId.Mdef], Flee = stats[AttributeId.Flee], ResCri = stats[AttributeId.ResCri] };
    }

    private long MonsterBuffDamage(MonsterUnit monster, JsonElement? skill, long amount)
    {
        if (!_buffs.HasUnitBuffs(monster)) return amount;
        int element = skill is { } row && GameData.Int(row, "_iEleType") > 1
            ? (int)GameData.Int(row, "_iEleType") : monster.Template.Element;
        double bonus = _buffs.UnitAttributes(monster).Where(c => RaisesDamage(c.Id, element)).Sum(c => c.Value);
        return (long)Math.Clamp(Math.Floor(amount * Math.Max(0, 1 + bonus / 10000)), 0, long.MaxValue);
    }

    private async Task PublishMonsterAttributesAsync(WorldUnit unit)
    {
        if (unit is not MonsterUnit monster) return;
        bool hasBuffs = _buffs.HasUnitBuffs(monster);
        // 所有來源都套用有效屬性；最後一個狀態移除後，還原模板速度並清除控制遮罩。
        if (!hasBuffs && monster.BuffSpeedCm == null) return;
        var stats = MonsterStats(monster);
        await _monsters.SetBuffSpeedAsync(monster, hasBuffs ? (int)Math.Clamp(stats[AttributeId.Speed], 0, int.MaxValue) : null);
        var changed = new AOIAttrChange
        {
            EntityUuid = (ulong)unit.Uid,
            StateInfo = new AoiObjStateInfo { Data = new CommonBigMask { Values = { (ulong)monster.StatusBits | _buffs.UnitStatus(unit) } } },
            StaggerInfo = new AoiObjStaggerInfo { StaggerMask = _buffs.UnitStaggerMask(unit) },
            ForbidInfo = new AoiObjForbidInfo { Data = new CommonBigMask { Values = { _buffs.UnitForbiddenMask(unit) } } },
        };
        foreach (int id in PublishedMonsterAttributes)
            changed.Property.Add(new AoIProperty { Idx = id, Data = stats[id], BaseData = stats.Base.GetValueOrDefault(id) });
        await _world.ToUnitViewersAsync(unit, AoiAttrChange, changed);
    }
}

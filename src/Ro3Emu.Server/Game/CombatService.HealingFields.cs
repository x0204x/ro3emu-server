using Romsg;
using System.Text.Json;
using Ro3Emu.Server.Data;
using Ro3Emu.Server.Maps;

namespace Ro3Emu.Server.Game;

public sealed partial class CombatService
{
    // 10110300022：聖域只傷害不死「屬性」或惡魔「種族」，不是所有可見的敵人。
    // 配對 enum_SkillElementType.Undead=10；enum_ESelectRaceType.Demon=4 對應種族序號3。
    private static bool HealingFieldEnemyAllowed(JsonElement row, MonsterUnit target) =>
        Script(row) != "Sanctuary" || target.Template.Element == 10 || target.Template.Race == 3;
    private sealed record HealingField(GameSession Owner, ActiveSkillCast Cast, Vec3 Center)
    {
        public bool Cancelled { get; set; }
    }
    private readonly HashSet<HealingField> _healingFields = [];

    /// <summary>Sanctuary 的10級原表均為4秒、每0.6秒。模板14115的單一攻擊點只決定建立時間。
    /// (inferred) 首次恢復在建立後600ms，依描述形成6次週期；原服首次週期相位仍待封包驗收。
    /// 不虛構NPC，不重收SP／重開CD，也不重複施放完成或職業天賦事件。</summary>
    private bool StartHealingField(GameSession source, ActiveSkillCast action)
    {
        if (Script(action.Row) != "Sanctuary") return false;
        var field = new HealingField(source, action, (action.Request.TargetPos ?? action.Origin).Clone());
        _healingFields.Add(field);
        for (int pulse = 1; pulse <= 6; pulse++)
        {
            bool final = pulse == 6;
            ScheduleRuntime(action.WorldScene, TimeSpan.FromMilliseconds(pulse * 600), async () =>
            {
                try
                {
                    if (field.Cancelled || !CurrentCastMap(source, action)) return;
                    var request = action.Request.Clone();
                    request.IsExtraSkill = true;
                    var tick = new ActiveSkillCast
                    {
                        Id = action.Id, OwnerId = action.OwnerId, WorldScene = action.WorldScene,
                        SceneGeneration = action.SceneGeneration, Attachment = action.Attachment,
                        Request = request, Row = action.Row, Origin = action.Origin, StartMs = action.StartMs,
                        ChantMs = 0, ActionMs = 0,
                    };
                    var allies = _world.PlayersIn(action.WorldScene)
                        .Where(p => !p.Dead && !p.SceneLoading && Relation(action.Row, true, p == source)
                            && SkillAreaContains(action.Row, action.Origin, field.Center, request.Face, p.Position))
                        .OrderBy(p => Distance(p.Position, field.Center)).Take((int)Math.Max(1, GameData.Int(action.Row, "_iTargetMax"))).ToArray();
                    await ApplyEffectsAsync(source, tick, field.Center,
                        chosenTargets: SelectEnemies(source, action, field.Center), chosenAllies: allies);
                }
                finally { if (final) _healingFields.Remove(field); }
            });
        }
        return true;
    }

    private void CancelHealingFields(GameSession source)
    {
        foreach (var field in _healingFields.Where(f => f.Owner == source).ToArray())
        {
            field.Cancelled = true;
            _healingFields.Remove(field);
        }
    }
}

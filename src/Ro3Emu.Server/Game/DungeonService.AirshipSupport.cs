using Romsg;
using Ro3Emu.Server.Data.Config;

namespace Ro3Emu.Server.Game;

public sealed partial class DungeonService
{
    /// <summary>原服甲板首批敵人後三次出生。獨立於每波 SceneActor，以免換波時移除參戰友軍。</summary>
    private void StartAirshipSupport(Attempt attempt, AirshipStoryState state)
    {
        if (state.SupportStarted) return;
        state.SupportStarted = true;
        foreach (var (npc, delay) in new[] { (5300086, 6508), (5300088, 7736), (5300087, 8950) })
            AirshipLater(attempt, state, delay, () => SpawnAirshipSupportAsync(attempt, state, npc),
                () => attempt.Wave is >= 2 and <= 4);
    }

    private async Task SpawnAirshipSupportAsync(Attempt attempt, AirshipStoryState state, int npc)
    {
        if (state.Support.Any(unit => unit.NpcId == npc) || CompanionUnit.Build(_data, _monsters, npc) is not { } original
            || NPCConfig.Get(_data, npc) is not { } row) return;
        var kit = original with
        {
            StoryPresentationOnly = true,
            BirthAnimation = 25,
            // 同本場首領的上取整；配對配置可得原服出生 101/2000=10001，不另造攻擊或傷害倍率。
            Template = original.Template with
            {
                MaxHp = (long)Math.Ceiling(MonsterService.TemplateAttributes(_data, row.Row,
                    (int)row.Level).GetValueOrDefault(AttributeId.MaxHp)),
            },
        };
        var unit = new CompanionUnit(kit, _world, _combat)
        {
            Uid = CompanionUnit.UidBase | Interlocked.Increment(ref _companionSerial),
            SceneEntry = AirshipScene, InstanceId = attempt.Room.Id,
            X = 223.5916f, Y = 228.65002f, Z = 432.4861f, Point = 0,
            HomeX = 223.5916f, HomeZ = 432.4861f,
            Credit = () => CompanionCredit(attempt),
            Fell = fallen => RemoveAirshipSupportAsync(attempt, state, fallen),
        };
        if (await _instances.AddPropAsync(attempt.Room, unit)) state.Support.Add(unit);
    }

    private async Task RemoveAirshipSupportAsync(Attempt attempt, AirshipStoryState state, CompanionUnit unit)
    {
        state.Support.Remove(unit);
        if (!unit.Removed) await _instances.RemovePropAsync(attempt.Room, unit);
    }

    private async Task ClearAirshipSupportAsync(Attempt attempt, AirshipStoryState state)
    {
        foreach (var unit in state.Support.ToArray())
        {
            // 原服結束支援：動畫狀態 1、HP=0、離開 AOI；不是怪物擊殺，不發放獎勵。
            unit.PresentationAnimation = 1;
            unit.Hp = 0;
            await _world.ToUnitViewersAsync(unit, WorldEffectsService.Animation,
                new UpdateEntityAnimation { EntityID = unit.Uid, AnimationInfo = { [1] = 1 } });
            await _world.ToUnitViewersAsync(unit, CombatService.AoiAttrChange, unit.HpChange());
            await RemoveAirshipSupportAsync(attempt, state, unit);
        }
    }
}

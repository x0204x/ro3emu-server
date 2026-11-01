using Romsg;
using Ro3Emu.Server.Data.Config;

namespace Ro3Emu.Server.Game;

public sealed partial class DungeonService
{
    private const int AirshipDeckBlastNpc = 5300037, AirshipDeckBurnNpc = 5300057;

    /// <summary>
    /// G 50761／50829 的甲板砲擊：31037 出生後送 31106；5300057 的 UnitConfig 5300018
    /// 使用 fx_airship_boomloop_01.prefab。這些是演出單位，不列入三艦或九名登船敵人的擊殺數。
    /// 5365201 的危險區傷害尚未還原，這裡只恢復有實包與模型依據的場景演出。
    /// </summary>
    private async Task AirshipDeckBlastAsync(Attempt a, AirshipStoryState state, Vec3 point, bool leaveBurn)
    {
        if (a.Settled || a.Room.Closed || state.Stopped) return;
        if (leaveBurn) await AddAirshipDeckActorAsync(a, state, AirshipDeckBurnNpc, point);
        var actor = await AddAirshipDeckActorAsync(a, state, AirshipDeckBlastNpc, point);
        if (actor == null) return;
        int skillId = (int)(NPCConfig.Get(_data, AirshipDeckBlastNpc)?.RespawnSkill ?? 0);
        if (SkillConfig.Get(_data, skillId) is { } skill)
            await _world.ToUnitViewersAsync(actor, CombatService.UseInSkillHigh, new UseInSkillInfo
            {
                SrcID = actor.Uid, SkillID = skillId, TargetPos = point.Clone(),
                SkillMs = (int)skill.ActionTime, PlayAct = true, StartTime = MonsterService.NowMs,
            });
        // (inferred) 原服兩次出生後 387／441 ms 收到 leave_aoi；取近似 400 ms，不刪除持續燃燒單位。
        AirshipLater(a, state, 400, () => RemoveAirshipDeckActorAsync(a, state, actor));
    }

    private async Task<SceneActorUnit?> AddAirshipDeckActorAsync(Attempt a, AirshipStoryState state, int npc, Vec3 point)
    {
        if (NPCConfig.Get(_data, npc) is not { } config) return null;
        var actor = new SceneActorUnit
        {
            Uid = SceneActorUidBase | Interlocked.Increment(ref _sceneActorSerial), SceneEntry = a.Plan.SceneEntry,
            InstanceId = a.Room.Id, NpcId = npc, Point = 0, EntityType = (int)config.NPCType, Camp = config.Camp,
            Name = "", BornMs = MonsterService.NowMs, X = point.X, Y = point.Y, Z = point.Z,
            Template = npc == AirshipDeckBurnNpc ? _monsters.Template(npc) : null,
        };
        if (!await _instances.AddPropAsync(a.Room, actor)) return null;
        state.DeckActors.Add(actor);
        return actor;
    }

    private async Task RemoveAirshipDeckActorAsync(Attempt a, AirshipStoryState state, SceneActorUnit actor)
    {
        if (state.DeckActors.Remove(actor)) await _instances.RemovePropAsync(a.Room, actor);
    }

    /// <summary>G 54620／54639／54650：接受船長選項後五處爆炸，沿用這次房間的世代取消與重送保護。</summary>
    private void ScheduleAirshipCaptainBlasts(Attempt a, AirshipStoryState state)
    {
        foreach (var (delay, x, z) in new (int, float, float)[]
        {
            (3300, 228.66f, 476.721f), (3300, 239.0966f, 454.0193f),
            (3700, 239.1845f, 466.0333f), (4300, 227.12f, 448.181f), (4300, 229.9801f, 459.6027f),
        })
            AirshipLater(a, state, delay,
                () => AirshipDeckBlastAsync(a, state, new Vec3 { X = x, Y = 228.65002f, Z = z }, leaveBurn: true),
                () => a.Wave == 6);
    }

    private async Task ClearAirshipDeckAsync(Attempt a, AirshipStoryState state)
    {
        // 原服燃燒單位沒有獨立移除事件；生命週期跟隨本次副本，在離場／結算時清除。
        foreach (var actor in state.DeckActors.ToArray()) await RemoveAirshipDeckActorAsync(a, state, actor);
    }
}

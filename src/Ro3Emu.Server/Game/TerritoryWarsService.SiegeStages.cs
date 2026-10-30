using Romsg;

namespace Ro3Emu.Server.Game;

/// <summary>
/// The siege's stages (docs/systems/territory-wars.md "Siege stages"): DungeonLua_2230101.lua DungeonBase.DungeonStageModule
/// (l.236-257) "0" 准备阶段 Prepare, "1" 城门阶段 Gate, "2" 内城阶段 Inner city, "3" 华丽阶段 the crystal, the top bar's
/// Prepare 0, Gate 1, Stronghold 2, Crystal 3 (UI/Siege/Controllers/UI_Sys_Siege_TopBarWidgetCtrl.lua head). The client
/// follows stageModuleData.stage of 30401 and pushes the stage's chat itself when it changes
/// (LC_SceneCom_UIModule_Siege.lua _processDungeonEvent, UIModule_StageSwitchChatIdMap 4110141-4110146).
/// </summary>
public sealed partial class TerritoryWarsService
{
    private const int SiegePrepareStage = 0, SiegeGateStage = 1, SiegeStrongholdStage = 2, SiegeCrystalStage = 3;
    /// <summary>DungeonStageModule maxTime of stages 0-3 in seconds (300000, 1500000, 3000000, 3000000 ms).</summary>
    private static readonly long[] SiegeStageMaxSeconds = [300, 1500, 3000, 3000];
    /// <summary>LC_DunGeon_VarDefine.lua:330-440: 512 CSiegeTopBarTimestamp (the top bar's Prepare countdown, epoch ms) and
    /// 542 CSiegeStageCountdown [end epoch ms, ms added] (the other stages' countdown and its "+N S").</summary>
    private const int TopBarTimestampVar = 512, StageCountdownVar = 542;
    /// <summary>LC_DunGeon_VarDefine.lua 511 CSiegeTopBarState (a number). Its only reader is
    /// UI_Sys_Dungeon_Main_GoWidgetCtrl: a defender sees the "go" button while it is 0, which walks them
    /// (CrossMapAutoFindPath) to the points of DungeonLua_2230101 UIModule_DefFrontPointIdList "0" (304-307 on the city
    /// wall at the ladder lanes), a list keyed by the stage. (inferred) The state is the stage: 0 in Prepare.</summary>
    private const int TopBarStateVar = 511;
    /// <summary>LC_DunGeon_VarDefine.lua 513 CSiegeGateNpcUid, a number (DungeonVariableConfig swaps the names of 513 and
    /// 514; the client decodes by VarDefine).</summary>
    private const int GateNpcUidVar = 513;
    /// <summary>BuffConfig 4904401 "Battle Start Protection" (fx_gvg_buff_hudun), a City Gate born buff.</summary>
    private const int BattleStartProtection = 4904401;
    private static readonly int[] SiegeInnerTowers = [41, 42, 43];

    private static long SiegeStageStart(SiegeRunState r) => r.Stage == SiegePrepareStage ? r.Start : r.StageStart;

    /// <summary>Prepare's end: its maxTime after the siege's start, or earlier by the test aid (!siege prepare).</summary>
    private static long SiegePrepareEnd(SiegeRunState r) =>
        r.PrepareEnd > 0 ? Math.Min(r.PrepareEnd, r.Start + SiegeStageMaxSeconds[SiegePrepareStage])
            : r.Start + SiegeStageMaxSeconds[SiegePrepareStage];

    /// <summary>(inferred) A stage's countdown ends at its maxTime, plus what the kills added to it
    /// (TerritoryWarsService.SiegeUnits.cs), or at the siege's own end, whichever comes first; only the siege's end
    /// (AttackCityEndTime 60 min) settles it.</summary>
    private static long SiegeStageEnd(SiegeRunState r) => r.Stage == SiegePrepareStage ? SiegePrepareEnd(r)
        : Math.Min(SiegeStageStart(r) + SiegeStageMaxSeconds[Math.Clamp(r.Stage, 0, SiegeCrystalStage)] + r.StageAdded, r.End);

    /// <summary>
    /// The stage the castle allows next: Gate once Prepare's 300 s passed, Stronghold once the gate at 36 fell, Crystal
    /// once the three Knight Guardians of the inner city (41-43, InnerTower) fell (inferred: the stage names and the
    /// top bar's objects of each stage, "Destroy the Outer Gate", "Defeat the Protector", "Destroy the Ornate Metal").
    /// </summary>
    private int SiegeNextStage(SiegeRunState r) => r.Stage switch
    {
        SiegePrepareStage when _clock.Now >= SiegePrepareEnd(r) => SiegeGateStage,
        SiegeGateStage when r.Destroyed.Contains(SiegeGatePoint) => SiegeStrongholdStage,
        SiegeStrongholdStage when SiegeInnerTowers.All(r.Destroyed.Contains) => SiegeCrystalStage,
        _ => r.Stage,
    };

    /// <summary>Moves the run on as far as the castle allows; a change goes to every loaded member (30401) and to the
    /// dungeon task tracker.</summary>
    private async Task SiegeAdvanceStageAsync(SiegeRun battle)
    {
        var r = battle.State;
        if (r.Settled) return;
        int from = r.Stage;
        for (int next = SiegeNextStage(r); next != r.Stage; next = SiegeNextStage(r))
        {
            r.StageStart = r.Stage == SiegePrepareStage ? SiegePrepareEnd(r) : _clock.Now;
            r.Stage = next;
            r.StageAdded = r.LastAdded = 0;
        }
        if (r.Stage == from) return;
        await SaveAsync(state);
        await SiegeGateRulesAsync(battle);
        await SiegeBroadcastBaseAsync(battle);
        SiegeTrack(battle, r.Players.Keys);
    }

    /// <summary>30401 as a change (eChangeType_Change) to the loaded members: the client handles stageModuleData and vars
    /// alike for either type (LC_DunGeon_Base.DUNGEON_BASEINFO_NOTIFY).</summary>
    private Task SiegeBroadcastBaseAsync(SiegeRun battle)
    {
        var info = SiegeBaseInfo(battle);
        info.ChangeType = DungeonBaseInfoNotify.Types.eChangeType.Change;
        return RoleFanout.EachAsync(SiegeLoadedMembers(battle), s => Send(s, 30401, info));
    }

    /// <summary>
    /// The gates' rules once Prepare is over: the City Gate at 36 loses its Battle Start Protection (NPCConfig 4900005
    /// _kBornEventBuff 4904401 "Immune to damage and debuffs", a server rule: the buff has no attribute) and the
    /// attackers can break it; the gate of the attackers' camp at 214 leaves (inferred: it holds the attackers in their
    /// camp while they prepare, like the camp's air walls 71/264/265). Idempotent: run after every spawn and stage change.
    /// </summary>
    private async Task SiegeGateRulesAsync(SiegeRun battle)
    {
        if (battle.State.Stage < SiegeGateStage) return;
        if (battle.Objects.GetValueOrDefault(SiegeGatePoint) is { Dead: false } gate && SiegeProtected(gate))
            await _buffs.RemoveUnitConfigAsync(gate, BattleStartProtection);
        if (battle.Objects.Remove(SiegeCampGatePoint, out var campGate))
        {
            battle.SentHp.Remove(SiegeCampGatePoint);
            await _instances.RemoveMonsterAsync(battle.Room, campGate);
            var entry = SiegeGlobalEntry(SiegeCampGatePoint, campGate);
            entry.Hp = 0; // deletes it in the client (LC_SceneCom_GlobalObjMgr.lua:91)
            var msg = new GlobalObjectDataNotify { GlobalDatas = { [campGate.Uid] = entry } };
            await RoleFanout.EachAsync(SiegeLoadedMembers(battle), s => Send(s, GlobalObjectNotify, msg));
        }
    }

    /// <summary>The siege's dungeon vars in 30401: the standing air walls (133, TerritoryWarsService.SiegeWalls.cs), the
    /// top bar's state (511, the stage), the stage's countdown (512 in Prepare, 542 [end, the last kill's addition in ms]
    /// after it: the top bar plays "+N S" when the end moves with an addition) and 513 the City Gate's uid while it is in
    /// the room.</summary>
    private static IEnumerable<DungeonVar> SiegeVars(SiegeRun battle)
    {
        var r = battle.State;
        yield return SiegeWallVar(r);
        yield return new DungeonVar { VarId = TopBarStateVar, Vi64 = { r.Stage } };
        long end = SiegeStageEnd(r) * 1000;
        if (r.Stage == SiegePrepareStage) yield return new DungeonVar { VarId = TopBarTimestampVar, Vi64 = { end } };
        else yield return new DungeonVar { VarId = StageCountdownVar, Vi64 = { end, r.LastAdded } };
        if (battle.Objects.GetValueOrDefault(SiegeGatePoint) is { } gate)
            yield return new DungeonVar { VarId = GateNpcUidVar, Vi64 = { gate.Uid } };
    }
}

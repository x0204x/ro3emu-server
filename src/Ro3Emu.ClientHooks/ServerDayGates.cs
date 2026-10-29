using System;
using System.Collections.Generic;
using UnityEngine;

namespace Ro3Emu.ClientHooks
{
    /// <summary>
    /// Server-day gates the client checks on its own (open-content policy, docs/guide/open-content.md). With
    /// ServerOptions.OpenContent the server counts every "server open day N" (3031) and "playbook seconds" (3030)
    /// condition as met from the first day and reports those functions open in 5056 FunctionOpenInfo; the client still
    /// compares a few of them with ServerInfo.iOpenDayCount, computed from the real opening time the server keeps
    /// sending (faking it would shift every window derived from it). Two places refuse content because of that:
    /// <list type="bullet">
    /// <item>Season cards (UI_Sys_Season_CardWidgetCtrl._processActionStageLogic and
    /// UI_Sys_Season_ActiveNewFuncitonListWidgetCtrl._processActionState) mark an Action card NotOpen when
    /// LC_FunctionHelper.GetFunctionProgressByDefine(function, CheckServerDay) &gt; iOpenDayCount, and their click
    /// handlers pop UIWords 70028 instead of going to the function (function 61, ten-player dungeons, [3031,5]).
    /// GetFunctionProgressByDefine is wrapped: for a 3031/3030 gate of a function the server reports open
    /// (LC_FunctionHelper.CheckFunctionIsOpen, i.e. AllOpenFunctionInfo from 5056) the gate reads 0, so the card is
    /// open exactly when the server says the function is.</item>
    /// <item>Generic conditions: LC_ConditionDefine.m_kCheckFunctionGroup[3031] (LC_InteractCondition.CheckServerDay)
    /// and [3030] (CheckPlayBook) answer LC_ConditionManager.ProcessCondition/ProcessConditionDes. The guild challenge
    /// panel (UI_Sys_GVE_InstancesNewPanelCtrl._refreshStartBtnView) locks difficulties 2 and 3 on them
    /// (GuildChallengeConfig._kCondition 51000001/51000002: [3031,3], [3031,5]) while the server's
    /// GuildChallengeService.Unlocked already opens them. Both checkers are wrapped: when the original says no, the
    /// gate holds if the server opens server-day gates, i.e. it reports open some function whose own 3031 gate is still
    /// ahead of iOpenDayCount (Guild League 60 [3031,3], Ilmata 1324 [3031,4] and 1323 [3031,5] carry no level gate, so
    /// any character sees them on days 1-4; OpenContentTests pins them). That answer is cached by Probe from the
    /// per-frame pump, never computed inside a checker. From day 5 on no client-checked server-day gate is ahead.</item>
    /// <item>The season page (top bar "Season", function 69 SeasonMainPage, router 10007 -> UI_Sys_Season_ActiveChapterPanel)
    /// in the season's first week. Stage 101001 (days 1-7, MultiverseStage [3,0]..[3,604800]) has _iHide 1, so
    /// LC_SeasonHelper.GetStageList skips it and GetCurrentStageId (LC_SeasonHelper.lua:1454-1479) returns nil until
    /// 101002 starts on day 8; UI_Sys_Main_FunctionEntryWidgetBaseCtrl._checkFunctionCanEnter (lua:718-743) then pops
    /// UIWords 70039 "SeasonPageNotStart" and the click opens nothing. The chapter panel also locks every stage after
    /// the current one (UI_Sys_Season_ActiveChapterPanelModel.SetSelectStage, lua:242-258; the stage tab widget,
    /// UI_Sys_Season_ActiveChapterStageWidgetCtrl.lua:139-151). GetCurrentStageId is wrapped: while the server opens
    /// content it answers the last visible stage that lists activities (101004), so the page opens and every chapter
    /// counts as started; a later stage (the break, 101005) is kept. The stage clock (82707) stays as sent.</item>
    /// <item>Season Exploration chapters (activity 111): ExplorePhaseConfig opens phases 101/102/103 on days 8/10/12 and
    /// closes them on day 15 (type-4 tuples on stages 101002/101003). LC_SeasonExplorationHelper.IsChapterInActivityTime
    /// (LC_SeasonExplorationHelper.lua:85-113) is wrapped to answer true while the server opens content; the server
    /// opens the phases with OpenContent (NewActivityService.WorldPhaseOpen).</item>
    /// </list>
    /// "The server opens content": it reports open a function whose own server-day gate is still ahead (above), or one
    /// whose base-level gate no character reaches (999/9999: OpenContent's forced 7201, 70122, 41). A server with the
    /// original rules never reports either, so every wrapper keeps the original behaviour there. Dated windows
    /// (TimeLimitConfig, activity begin/end times) and the stage clock (82707) are not touched. Each part is installed
    /// only when the client module and method it needs exist; otherwise it logs once and does nothing.
    /// </summary>
    public static class ServerDayGates
    {
        private const long ServerDay = 3031, PlayBookSeconds = 3030, BaseLevel = 1002;
        /// <summary>A base-level gate no character reaches: the original never opened such a function.</summary>
        private const long NeverLevel = 999;
        private const float ProbeSeconds = 5f;

        /// <summary>LC_FunctionHelper.GetFunctionProgressByDefine(functionId, conditionType) (called with '.').</summary>
        public delegate object Progress(object functionId, object conditionType);
        /// <summary>A m_kCheckFunctionGroup checker: fn(DynamicParamEnv, input) -> bool.</summary>
        public delegate bool Check(object env, object input);
        /// <summary>LC_SeasonHelper.GetCurrentStageId() (called with '.', no arguments) -> stage id or nil.</summary>
        public delegate object Stage();
        /// <summary>LC_SeasonExplorationHelper.IsChapterInActivityTime(phaseCfgId) (called with '.') -> bool.</summary>
        public delegate bool Phase(object phase);

        // Kept alive here: Lua holds the pushed delegates only weakly through xLua's object pool.
        private static readonly Progress ProgressWrapper = FunctionProgress;
        private static readonly Check ServerDayWrapper = CheckServerDay, PlayBookWrapper = CheckPlayBook;
        private static readonly Stage StageWrapper = CurrentStage;
        private static readonly Phase PhaseWrapper = ChapterOpen;

        private static Obf_R.Obf_ff _global;
        private static Obf_R.Obf_Ef _progress, _isOpen, _serverDay, _playBook, _stage, _stageList, _chapter;
        // FunctionConfig is static per LuaEnv: each function's own 3031 and 1002 gates, computed once.
        private static readonly Dictionary<long, long> DayGate = new Dictionary<long, long>(), LevelGate = new Dictionary<long, long>();
        private static bool _gatesOpen, _contentOpen;
        private static float _nextProbe = float.MinValue;

        /// <summary>The cached "server opens content" answer of <see cref="Probe"/> (LevelCapTip reads it).</summary>
        internal static bool ContentOpen => _contentOpen;

        /// <summary>From LuaPatches.Tick, once per LuaEnv.</summary>
        internal static void Install(Obf_R.Obf_ff global, Obf_R.Obf_ff functionHelper, Obf_R.Obf_ff conditionDefine,
            Obf_R.Obf_ff seasonHelper, Obf_R.Obf_ff exploration)
        {
            _global = global;
            _progress = _isOpen = _serverDay = _playBook = _stage = _stageList = _chapter = null;
            DayGate.Clear();
            LevelGate.Clear();
            _gatesOpen = _contentOpen = false;
            _nextProbe = float.MinValue;

            var progress = functionHelper?.Obf_RuA<Obf_R.Obf_Ef>("GetFunctionProgressByDefine");
            var isOpen = functionHelper?.Obf_RuA<Obf_R.Obf_Ef>("CheckFunctionIsOpen");
            if (progress == null || isOpen == null)
            {
                Debug.LogWarning("[Ro3Emu.ServerDayGates] LC_FunctionHelper.GetFunctionProgressByDefine/CheckFunctionIsOpen not found;"
                    + " season cards and server-day conditions keep the client's own day count");
                return;
            }
            _progress = progress;
            _isOpen = isOpen;
            functionHelper.Obf_luA<string, Progress>("GetFunctionProgressByDefine", ProgressWrapper);

            string checkers;
            try
            {
                var group = conditionDefine?.Obf_RuA<Obf_R.Obf_ff>("m_kCheckFunctionGroup");
                _serverDay = group?.Obf_ruA<long, Obf_R.Obf_Ef>(ServerDay);
                _playBook = group?.Obf_ruA<long, Obf_R.Obf_Ef>(PlayBookSeconds);
                if (_serverDay != null) group.Obf_luA<long, Check>(ServerDay, ServerDayWrapper);
                if (_playBook != null) group.Obf_luA<long, Check>(PlayBookSeconds, PlayBookWrapper);
                checkers = group == null ? "not found (LC_ConditionDefine.m_kCheckFunctionGroup missing)"
                    : "3031 " + (_serverDay != null ? "wrapped" : "missing") + ", 3030 " + (_playBook != null ? "wrapped" : "missing");
            }
            catch (Exception e)
            {
                // A wrapper already in place keeps its captured original; one never set leaves the client's checker.
                checkers = "partly wrapped or not (" + e.GetType().Name + ": " + e.Message + ")";
            }
            Debug.Log("[Ro3Emu.ServerDayGates] season card day gates follow the server's function state; condition checkers " + checkers
                + "; season page stage " + InstallStage(seasonHelper) + "; exploration chapters " + InstallExploration(exploration));
        }

        private static string InstallStage(Obf_R.Obf_ff seasonHelper)
        {
            try
            {
                var stage = seasonHelper?.Obf_RuA<Obf_R.Obf_Ef>("GetCurrentStageId");
                var list = seasonHelper?.Obf_RuA<Obf_R.Obf_Ef>("GetStageList");
                if (stage == null || list == null) return "not wrapped (LC_SeasonHelper methods missing)";
                _stage = stage;
                _stageList = list;
                seasonHelper.Obf_luA<string, Stage>("GetCurrentStageId", StageWrapper);
                return "wrapped";
            }
            catch (Exception e) { return "not wrapped (" + e.GetType().Name + ": " + e.Message + ")"; }
        }

        /// <summary>
        /// The original's stage, or, when the server opens content, the last visible stage that lists activities
        /// (101004 for playbook 101) if the original is earlier or nil: every chapter of the season page counts as
        /// started. A later original (the break stage 101005) is kept. Only reads here: the stage list and the stage
        /// table, pure lookups.
        /// </summary>
        private static object CurrentStage()
        {
            object original = First(_stage.Obf_HuA());
            if (!(_contentOpen || _gatesOpen)) return original;
            try
            {
                var list = First(_stageList.Obf_HuA()) as Obf_R.Obf_ff;
                var stages = new List<long>();
                for (long i = 1; list != null; i++)
                {
                    long id = LuaPatches.ToLong(list.Obf_ruA<long, object>(i));
                    if (id <= 0) break;
                    stages.Add(id);
                }
                int last = -1;
                for (int i = stages.Count - 1; i >= 0 && last < 0; i--)
                    if (HasActivities(stages[i])) last = i;
                if (last < 0) return original;
                int current = original == null ? -1 : stages.IndexOf(LuaPatches.ToLong(original));
                if (current >= last) return original;
                Note("season page: every chapter counts as started (the server opens content), stage " + stages[last]
                    + " instead of " + (original ?? "none"));
                return stages[last];
            }
            catch (Exception e) { Once("stage: " + e.GetType().Name + ": " + e.Message); }
            return original;
        }

        /// <summary>Lua_DBManager:GetInstance().m_kCFG_MultiverseStage:GetStageActivityIDList(stage) is a non-empty list.</summary>
        private static bool HasActivities(long stage)
        {
            var manager = _global?.Obf_RuA<Obf_R.Obf_ff>("Lua_DBManager");
            var instance = First(manager?.Obf_RuA<Obf_R.Obf_Ef>("GetInstance")?.Obf_HuA(manager)) as Obf_R.Obf_ff;
            var config = instance?.Obf_RuA<Obf_R.Obf_ff>("m_kCFG_MultiverseStage");
            var ids = First(config?.Obf_RuA<Obf_R.Obf_Ef>("GetStageActivityIDList")?.Obf_HuA(config, stage)) as Obf_R.Obf_ff;
            return ids != null && LuaPatches.ToLong(ids.Obf_ruA<long, object>(1L)) > 0;
        }

        private static object First(object[] result) => result != null && result.Length > 0 ? result[0] : null;

        /// <summary>
        /// LC_SeasonExplorationHelper.IsChapterInActivityTime(phaseCfgId): true while the server opens content. Every
        /// chapter lock of Season Exploration asks it (the movement chapter widget, the play panel's filter, the big map's
        /// tabs through IsChapterInActivityTimeByIndex, condition 16001 CheckExplorePhaseOpen); the server opens the
        /// phases to match (NewActivityService.WorldPhaseOpen). Cached flag only: condition 16001 calls it from inside
        /// LC_ConditionManager.
        /// </summary>
        private static bool ChapterOpen(object phase)
        {
            if (_contentOpen || _gatesOpen)
            {
                Note("exploration chapter " + LuaPatches.ToLong(phase) + " counts as open (the server opens content)");
                return true;
            }
            var result = _chapter?.Obf_HuA(phase);
            return result != null && result.Length > 0 && result[0] is bool open && open;
        }

        private static string InstallExploration(Obf_R.Obf_ff exploration)
        {
            try
            {
                var chapter = exploration?.Obf_RuA<Obf_R.Obf_Ef>("IsChapterInActivityTime");
                if (chapter == null) return "not wrapped (LC_SeasonExplorationHelper.IsChapterInActivityTime missing)";
                _chapter = chapter;
                exploration.Obf_luA<string, Phase>("IsChapterInActivityTime", PhaseWrapper);
                return "wrapped";
            }
            catch (Exception e) { return "not wrapped (" + e.GetType().Name + ": " + e.Message + ")"; }
        }

        private static object FunctionProgress(object functionId, object conditionType)
        {
            try
            {
                long type = LuaPatches.ToLong(conditionType);
                if ((type == ServerDay || type == PlayBookSeconds) && ServerSaysOpen(functionId))
                {
                    Note("function " + LuaPatches.ToLong(functionId) + ": server-day gate " + type + " reads as reached (the server reports it open)");
                    return 0L;
                }
            }
            catch (Exception e) { Once("progress: " + e.GetType().Name + ": " + e.Message); }
            var result = _progress.Obf_HuA(functionId, conditionType);
            return result != null && result.Length > 0 && result[0] != null ? result[0] : 0L;
        }

        // Only the cached answer here: a checker runs inside LC_ConditionManager, whose result list (ProcessConditionDes),
        // input and DynamicParamEnv are shared module state, so asking it anything again from here would corrupt the
        // evaluation in progress. Probe refreshes the answer from the per-frame pump instead.
        private static bool CheckServerDay(object env, object input) => Checked(_serverDay, env, input) || _gatesOpen;

        private static bool CheckPlayBook(object env, object input) => Checked(_playBook, env, input) || _gatesOpen;

        private static bool Checked(Obf_R.Obf_Ef original, object env, object input)
        {
            var result = original?.Obf_HuA(env, input);
            return result != null && result.Length > 0 && result[0] is bool met && met;
        }

        /// <summary>LC_FunctionHelper.CheckFunctionIsOpen(id, false): AllOpenFunctionInfo[id].isOpen and not banned.</summary>
        private static bool ServerSaysOpen(object functionId)
        {
            var result = _isOpen?.Obf_HuA(functionId, false);
            return result != null && result.Length > 0 && result[0] is bool open && open;
        }

        /// <summary>
        /// From LuaPatches.Tick (outside any Lua call), at most every <see cref="ProbeSeconds"/> seconds: does the server
        /// open server-day gates? Yes when it reports open a function whose own 3031 gate is ahead of the client's day count.
        /// </summary>
        internal static void Probe()
        {
            if (_progress == null || (_serverDay == null && _playBook == null && _stage == null)) return;
            float now = Time.realtimeSinceStartup;
            if (now < _nextProbe) return;
            _nextProbe = now + ProbeSeconds;
            bool open = false, content = false;
            try
            {
                long day = OpenDayCount();
                var functions = Asset("AllOpenFunctionInfo");
                if (functions != null)
                {
                    // Keys first: no Lua call may run while the table is being walked.
                    foreach (long id in new List<long>(functions.Obf_quA<long>()))
                    {
                        bool dayAhead = !open && day != long.MinValue && Gate(id, DayGate, ServerDay) > day;
                        bool neverLevel = !content && Gate(id, LevelGate, BaseLevel) >= NeverLevel;
                        if (!(dayAhead || neverLevel) || !ServerSaysOpen(id)) continue;
                        if (dayAhead)
                        {
                            Note("server opens server-day gates: function " + id + " is open on day " + day + " (its gate: day " + Gate(id, DayGate, ServerDay) + ")");
                            open = true;
                        }
                        if (neverLevel)
                        {
                            Note("server opens content: function " + id + " is open (its level gate: " + Gate(id, LevelGate, BaseLevel) + ")");
                            content = true;
                        }
                        if (open && content) break;
                    }
                }
            }
            catch (Exception e) { Once("probe: " + e.GetType().Name + ": " + e.Message); }
            if (open != _gatesOpen) Debug.Log("[Ro3Emu.ServerDayGates] server-day conditions " + (open ? "count as met" : "follow the client's day count"));
            if (content != _contentOpen) Debug.Log("[Ro3Emu.ServerDayGates] server " + (content ? "opens content (season page from the first week)" : "keeps the original's rules"));
            _gatesOpen = open;
            _contentOpen = content;
        }

        /// <summary>A function's own gate of one condition type (its LC_ConditionList progress), cached per LuaEnv.</summary>
        private static long Gate(long functionId, Dictionary<long, long> cache, long conditionType)
        {
            if (cache.TryGetValue(functionId, out long gate)) return gate;
            var result = _progress.Obf_HuA(functionId, conditionType);
            gate = result != null && result.Length > 0 ? LuaPatches.ToLong(result[0]) : 0;
            cache[functionId] = gate;
            return gate;
        }

        private static long OpenDayCount()
        {
            var info = Asset("ServerInfo");
            return info == null ? long.MinValue : LuaPatches.ToLong(info.Obf_RuA<object>("iOpenDayCount"));
        }

        /// <summary>LC_AssetManagerIns:ExecCmd(enum_AssetManager_Cmd.SelectOnly, name).</summary>
        private static Obf_R.Obf_ff Asset(string name)
        {
            var global = _global;
            var assets = global?.Obf_RuA<Obf_R.Obf_ff>("LC_AssetManagerIns");
            var exec = assets?.Obf_RuA<Obf_R.Obf_Ef>("ExecCmd");
            object select = global?.Obf_LuA<object>("enum_AssetManager_Cmd.SelectOnly");
            if (exec == null || select == null) return null;
            var result = exec.Obf_HuA(assets, select, name);
            return result != null && result.Length > 0 ? result[0] as Obf_R.Obf_ff : null;
        }

        private static readonly HashSet<string> Reported = new HashSet<string>();
        private static void Once(string message) { if (Reported.Add(message)) Debug.LogWarning("[Ro3Emu.ServerDayGates] " + message); }
        private static void Note(string message) { if (Reported.Add(message)) Debug.Log("[Ro3Emu.ServerDayGates] " + message); }
    }
}

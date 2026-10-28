using System;
using System.Text;
using HappyEngine.Base;
using UnityEngine;

namespace Ro3Emu.ClientHooks
{
    /// <summary>
    /// The pass challenges in the client's own season task panel (UI_Sys_Season_TaskPanel; nothing in the client opens
    /// it and its data has no pass tasks). The server's challenges (NewActivityService.PassPlay) become TaskConfig type-13
    /// rows with two tabs of our own, "Daily" (son type 139) and "Weekly" (138), under an activity row of our own whose
    /// _kTaskRenew lists them. Typing "/pass" in chat opens the panel instead of sending the line: the hook first sends
    /// <see cref="MsgId"/>, after which the server adds the challenge rows to 5027, then opens the panel a moment later.
    /// A client without this hook never sends it, so the server never sends it task ids its TaskConfig lacks (an
    /// increment for one throws in LC_DAL_Task). Rows, texts and award previews are plain Lua tables set through the
    /// LuaTable API, before any 5027 can carry them. Three traps met on the way (in game):
    /// <list type="bullet">
    /// <item>the configs are lazy proxies, and the season switch at login reloads all of them (ReLoadAllCfg): the rows
    /// go in again after every reload, and a periodic check puts them back if anything else drops them;</item>
    /// <item>rows are written with rawset, ahead of m_kValues' lazy __index;</item>
    /// <item>this client's table.pack leaves a __paramslen field that pairs() meets as a number: it is removed.</item>
    /// </list>
    /// </summary>
    public static class PassChallenges
    {
        /// <summary>Must equal the server's NewActivityService.PassPanelMsg.</summary>
        public const uint MsgId = PrivateMessageIds.PassChallenges;

        private const long ActivityId = 99139;
        private const long DailySonType = 139, WeeklySonType = 138;
        private const long TaskType = 13;
        private const long TextBase = 99_130_000_000;
        // ProgressConfig 103022 (as season task 110001) only makes the progress entry valid for LC_ConditionManager; the number shown is ours.
        private const long ProgressCondition = 103022;
        private const int PassExpItem = 132;

        private sealed class Challenge
        {
            public readonly long TaskId, Target, Reward; public readonly string Text;
            public Challenge(long taskId, string text, long target, long reward) { TaskId = taskId; Text = text; Target = target; Reward = reward; }
        }

        // Must equal NewActivityService.DailyChallenges / WeeklyChallenges, in order (ids DailyTaskBase / WeeklyTaskBase + index).
        private static readonly Challenge[] Daily =
        {
            new Challenge(99_130_001, "Defeat 100 monsters", 100, 300),
            new Challenge(99_130_002, "Defeat 50 monsters in a party", 50, 300),
            new Challenge(99_130_003, "Reach 60 daily activity", 60, 300),
            new Challenge(99_130_004, "Clear a dungeon", 1, 300),
            new Challenge(99_130_005, "Complete 3 quests", 3, 300),
        };

        private static readonly Challenge[] Weekly =
        {
            new Challenge(99_130_101, "Defeat 1000 monsters", 1000, 1000),
            new Challenge(99_130_102, "Defeat 300 monsters in a party", 300, 1000),
            new Challenge(99_130_103, "Earn 300 daily activity", 300, 1000),
            new Challenge(99_130_104, "Clear 5 dungeons", 5, 1000),
            new Challenge(99_130_105, "Take part in 3 MVP fights", 3, 1000),
            new Challenge(99_130_106, "Complete 20 quests", 20, 1000),
            new Challenge(99_130_107, "Play on 5 days", 5, 1000),
        };

        public delegate object SendMsg(object channelIndex, object channelId, object windowType, object sendType, object text,
            object extra, object checkCd);

        // Kept alive here: Lua holds the pushed delegates only weakly through xLua's object pool.
        private static readonly SendMsg SendWrapper = Send;
        private static Obf_R.Obf_ff _global;
        private static Obf_R.Obf_Ef _originalSend, _originalReload;
        private static readonly LuaPatches.SelfMethod ReloadWrapper = Reload;
        private static int _openInTicks, _checkIn;

        /// <summary>From LuaPatches.Tick, once per LuaEnv.</summary>
        internal static void Install(Obf_R.Obf_ff global)
        {
            _global = global;
            _openInTicks = 0;
            _checkIn = 0;
            try
            {
                var chat = global.Obf_RuA<Obf_R.Obf_ff>("LC_ChatHelper");
                _originalSend = chat?.Obf_RuA<Obf_R.Obf_Ef>("SendMsg");
                if (_originalSend != null) chat.Obf_luA<string, SendMsg>("SendMsg", SendWrapper);
                // The season switch at login (GameLogic) reloads every config through ReLoadAllCfg, without our rows.
                var dbClass = global.Obf_RuA<Obf_R.Obf_ff>("Lua_DBManager");
                _originalReload = dbClass?.Obf_RuA<Obf_R.Obf_Ef>("ReLoadAllCfg");
                if (_originalReload != null) dbClass.Obf_luA<string, LuaPatches.SelfMethod>("ReLoadAllCfg", ReloadWrapper);
                bool added = Inject();
                Debug.Log("[Ro3Emu.Pass] challenge rows " + (added ? "added" : "NOT added") + "; /pass "
                    + (_originalSend != null ? "opens the panel" : "not hooked (LC_ChatHelper.SendMsg missing)"));
            }
            catch (Exception e)
            {
                Debug.LogWarning("[Ro3Emu.Pass] install failed: " + e);
            }
        }

        /// <summary>
        /// From UiTweaks.Tick (every 10th frame): opens the panel once the server has had time to add the rows, and puts
        /// the rows back when the client reloaded a config (its configs are lazy proxies that can be released and read
        /// again, without our rows; a 5027 for a missing row throws in LC_DAL_Task).
        /// </summary>
        internal static void Tick()
        {
            if (_global == null) return;
            if (++_checkIn >= 30)
            {
                _checkIn = 0;
                try { if (!Present()) Debug.Log("[Ro3Emu.Pass] config reloaded; challenge rows " + (Inject() ? "added again" : "NOT added")); }
                catch (Exception e) { Debug.LogWarning("[Ro3Emu.Pass] row check failed: " + e.Message); }
            }
            if (_openInTicks <= 0 || --_openInTicks > 0) return;
            Open();
        }

        /// <summary>Lua_DBManager:ReLoadAllCfg(): the original, then our rows into the fresh configs.</summary>
        private static void Reload(Obf_R.Obf_ff self)
        {
            _originalReload?.Obf_HuA(self);
            try { Debug.Log("[Ro3Emu.Pass] configs reloaded; challenge rows " + (Inject() ? "added again" : "NOT added")); }
            catch (Exception e) { Debug.LogWarning("[Ro3Emu.Pass] rows after reload failed: " + e.Message); }
        }

        /// <summary>Lua_DBManager:GetInstance(): the configs' owner, whose lazy proxies the client's own code reads.</summary>
        private static Obf_R.Obf_ff Configs()
        {
            var db = _global.Obf_RuA<Obf_R.Obf_ff>("Lua_DBManager");
            var manager = db?.Obf_RuA<Obf_R.Obf_Ef>("GetInstance")?.Obf_HuA(db);
            return manager != null && manager.Length > 0 ? manager[0] as Obf_R.Obf_ff : null;
        }

        /// <summary>The first challenge row, read the way LC_DAL_Task.DecodeTaskInfo reads it (TaskConfig:GetTaskConfig).</summary>
        private static bool Present()
        {
            var config = Configs()?.Obf_RuA<Obf_R.Obf_ff>("m_kCFG_TaskConfig");
            var get = config?.Obf_RuA<Obf_R.Obf_Ef>("GetTaskConfig");
            var row = get?.Obf_HuA(config, Daily[0].TaskId);
            return row != null && row.Length > 0 && row[0] != null;
        }

        private static bool Inject()
        {
            var dbm = Configs();
            if (dbm == null)
            {
                Debug.LogWarning("[Ro3Emu.Pass] Lua_DBManager not found; the pass challenges stay hidden");
                return false;
            }
            var tasks = Values(dbm, "m_kCFG_TaskConfig");
            var activities = Values(dbm, "m_kCFG_NewActivityConfig");
            var types = Values(dbm, "m_kCFG_TypeConfig");
            var groups = Values(dbm, "m_kCFG_MultiverseGroupConfig");
            var awards = Values(dbm, "m_kCFG_AwardConfig");
            var texts = _global.Obf_RuA<Obf_R.Obf_ff>("LanguageKV");
            if (tasks == null || activities == null || types == null || groups == null || awards == null || texts == null)
            {
                Debug.LogWarning("[Ro3Emu.Pass] a config table is missing (tasks " + (tasks != null) + ", activities " + (activities != null)
                    + ", types " + (types != null) + ", groups " + (groups != null) + ", awards " + (awards != null) + ", texts " + (texts != null) + ")");
                return false;
            }
            long text = TextBase;
            Text(texts, ++text, "Pass Challenges");
            long title = text;
            Text(texts, ++text, "Daily");
            long dailyLabel = text;
            Text(texts, ++text, "Weekly");
            long weeklyLabel = text;
            foreach (var reward in new long[] { 300, 1000 }) Award(groups, awards, reward);
            foreach (var c in Daily) { Text(texts, ++text, c.Text); Task(tasks, c, DailySonType, text); }
            foreach (var c in Weekly) { Text(texts, ++text, c.Text); Task(tasks, c, WeeklySonType, text); }
            var group13 = types.Obf_ruA<long, Obf_R.Obf_ff>(TaskType);
            if (group13 != null)
            {
                RawSet(group13, DailySonType, Row("_iGroup", TaskType, "_iType", DailySonType, "_iName", List(dailyLabel)));
                RawSet(group13, WeeklySonType, Row("_iGroup", TaskType, "_iType", WeeklySonType, "_iName", List(weeklyLabel)));
            }
            RawSet(activities, ActivityId, Row(
                "_iID", ActivityId, "_iName", List(title), "_iActivityType", 0L,
                "_kTaskRenew", List(List(0L, TaskType, DailySonType, 0L), List(0L, TaskType, WeeklySonType, 0L))));
            NovicePass.Inject(dbm, texts);
            return Present();
        }

        /// <summary>LC_ChatHelper.SendMsg(channelIndex, channelId, windowType, sendType, text, extra, checkCD).</summary>
        private static object Send(object channelIndex, object channelId, object windowType, object sendType, object text,
            object extra, object checkCd)
        {
            var line = (text as string ?? "").Trim();
            if (line.Equals("/pass", StringComparison.OrdinalIgnoreCase) || line.StartsWith("/pass ", StringComparison.OrdinalIgnoreCase))
            {
                Request();
                return null; // "not sent", as SendMsg returns for a refused line
            }
            var result = _originalSend?.Obf_HuA(channelIndex, channelId, windowType, sendType, text, extra, checkCd);
            return result != null && result.Length > 0 ? result[0] : null;
        }

        /// <summary>Asks the server for the challenge rows, then opens the panel a moment later (/pass and the pass view's button).</summary>
        internal static void Request()
        {
            try
            {
                if (!Present()) Inject();
                SingletonMonoBehaviour<NW_CBBNetworkManager>.Instance.Obf_jBd(MsgId, Encoding.UTF8.GetBytes("open"));
                _openInTicks = 6; // about a tenth of a second per tick: the server's 5027 arrives first
            }
            catch (Exception e)
            {
                Debug.LogError("[Ro3Emu.Pass] request failed: " + e);
            }
        }

        private static void Open()
        {
            try
            {
                var global = _global;
                var ui = global?.Obf_RuA<Obf_R.Obf_ff>("Lua_UIManagerNewIns");
                var open = ui?.Obf_RuA<Obf_R.Obf_Ef>("OpenPanelDefault");
                object panel = global?.Obf_LuA<object>("UINames.UI_Sys_Season_TaskPanel");
                if (open == null || panel == null)
                {
                    Debug.LogWarning("[Ro3Emu.Pass] UI manager or task panel not found (open " + (open != null) + ", panel " + panel + ")");
                    return;
                }
                // Lua_UIManagerNew:OpenPanelDefault(name, nil, nil, nil, initData): the panel's init data is the activity id.
                open.Obf_HuA(ui, panel, null, null, null, ActivityId);
            }
            catch (Exception e)
            {
                Debug.LogError("[Ro3Emu.Pass] open failed: " + e);
            }
        }

        /// <summary>A whole row in the shape of a real season task (TaskConfig 110001): every field the client reads is set.</summary>
        private static void Task(Obf_R.Obf_ff tasks, Challenge c, long sonType, long text)
        {
            RawSet(tasks, c.TaskId, Row(
                "_iTaskID", c.TaskId, "_iTaskType", TaskType, "_iTaskSonType", sonType,
                "_iTaskDescribe", List(text), "_iName", List(text),
                "_kProgressTarget", List(List(ProgressCondition, c.Target)),
                "_kTrack", List(List("")), "_kPCTrack", List(List("")),
                "_iAward", AwardId(c.Reward), "_iShowTaskProgress", 0L,
                "_iAcquire", List(), "_iBeforeDescribe", List(0L), "_iCondition", 0L, "_iDefeat", 0L,
                "_iDungeonDifficulty", 0L, "_iDungeonID", 0L, "_iIfGiveUp", 0L, "_iIfRepeat", 0L, "_iIfReset", 0L,
                "_iIsGlobalTask", 0L, "_iShowAccept", 0L, "_iShowItem", 0L, "_iShowItemDisc", List(0L), "_iShowOnMap", 0L,
                "_iShowTag", List(0L), "_iStageName", List(0L), "_iStrongerRank", 0L, "_iStrongerSource", List(),
                "_iTargetDescribe", List(0L), "_iTaskRank", 0L, "_kAcceptAction", List(List("")), "_kAcceptTask", List(0L),
                "_kDeliverTask", List(), "_kEXECAction", List(List("")), "_kEXP", List(), "_kIcon", "",
                "_kMultiverseArray", List(), "_kNPCAcceptMark", "", "_kNPCDeliverMark", "", "_kNPCProgressMark", "",
                "_kObjective", 0L, "_kPic", "", "_kPreTask", List(), "_kStartTime", List(), "_kTimeLimit", 0L));
        }

        /// <summary>A reward preview: MultiverseGroupConfig (type 0, default value) -> AwardConfig rows -> _kPreview.</summary>
        private static void Award(Obf_R.Obf_ff groups, Obf_R.Obf_ff awards, long reward)
        {
            long id = AwardId(reward);
            RawSet(groups, id, Row("_iID", id, "_itype", 0L, "_iValue_default", id));
            RawSet(awards, id, List(Row("_iID", id, "_kPreview", List(List((long)PassExpItem, reward)))));
        }

        private static long AwardId(long reward) => TextBase / 1000 + reward; // 99_130_300 and 99_131_000

        internal static void Text(Obf_R.Obf_ff texts, long id, string value) =>
            RawSet(texts, id, Row("_iID", id, "_kDes", value));

        /// <summary>
        /// rawset: the client fills config rows lazily through m_kValues' __index (rows decoded from its payload on first
        /// use), and that loader fails for an id it does not know. A raw entry is found before __index runs.
        /// </summary>
        internal static void RawSet(Obf_R.Obf_ff table, object key, object value) =>
            _global.Obf_LuA<Obf_R.Obf_Ef>("rawset").Obf_HuA(table, key, value);

        /// <summary>A config's raw rows: Lua_DBManager's lazy proxy loads it on first access.</summary>
        internal static Obf_R.Obf_ff Values(Obf_R.Obf_ff manager, string config) =>
            manager.Obf_RuA<Obf_R.Obf_ff>(config)?.Obf_RuA<Obf_R.Obf_ff>("m_kConfigData")?.Obf_RuA<Obf_R.Obf_ff>("m_kValues");

        /// <summary>A new Lua table: table.pack() without its count field.</summary>
        private static Obf_R.Obf_ff NewTable()
        {
            var made = _global.Obf_LuA<Obf_R.Obf_Ef>("table.pack").Obf_HuA();
            var table = (Obf_R.Obf_ff)made[0];
            // pairs() must meet no count field: Lua 5.x table.pack sets n, this client's own version __paramslen.
            var rawset = _global.Obf_LuA<Obf_R.Obf_Ef>("rawset");
            rawset.Obf_HuA(table, "n", null);
            rawset.Obf_HuA(table, "__paramslen", null);
            return table;
        }

        internal static Obf_R.Obf_ff List(params object[] items)
        {
            var table = NewTable();
            for (int i = 0; i < items.Length; i++) table.Obf_luA<long, object>(i + 1, items[i]);
            return table;
        }

        /// <summary>A new Lua table from key, value pairs.</summary>
        internal static Obf_R.Obf_ff Row(params object[] fields)
        {
            var table = NewTable();
            for (int i = 0; i + 1 < fields.Length; i += 2) table.Obf_luA<string, object>((string)fields[i], fields[i + 1]);
            return table;
        }
    }
}

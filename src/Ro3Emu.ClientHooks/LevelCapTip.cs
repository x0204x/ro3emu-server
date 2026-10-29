using System;
using UnityEngine;

namespace Ro3Emu.ClientHooks
{
    /// <summary>
    /// The server level cap the client shows. It computes the server day from ServerInfo.iOpenTime and reads the cap of
    /// that day from BaseLvLimitConfig (39 on day 1, rising to 99 from day 36):
    /// <list type="bullet">
    /// <item>LC_TaskHelper.GetBaseLevelLimitTip, the quest tracker line under a level target ("Current Server Level Cap:
    /// Lv. ${1}\n${2}: Server Level Cap increases to Lv. ${3}", UIWordsDefine.CheckBaseLevelDesc), from
    /// GetBaseLimit(day) and GetNextBaseInfo(day);</item>
    /// <item>UI_Sys_RoleAttributesBarWidgetCtrl and UI_Sys_RoleTipsPanelCtrl, the cap and its next rise in the character
    /// panel, from the same two plus GetMaxBaseLimit;</item>
    /// <item>UI_Sys_RoleInfoWidgetCtrl, the cap beside the EXP bar, from GetBaseLimit(iOpenDayCount).</item>
    /// </list>
    /// With OpenContent the server caps the level at the table's highest _iBaseLimit from the first day
    /// (CharacterService.LevelRules), so on day 1 the client said "Lv. 39, rises to 45 on ..." while 99 applied. Both
    /// lookups are wrapped on the config class (Lua_CFG_BaseLvLimitConfig; BaseClass's __newindex writes the shared
    /// vtbl, so the Lua_DBManager proxy reaches the wrappers): while the server opens content (ServerDayGates) they answer
    /// for the table's last day, the final stage, so every view shows what the original shows on that stage. The tracker
    /// line then disappears (GetNextBaseInfo of the last day is {0, cap}, which GetBaseLevelLimitTip answers with nil) and
    /// the character panel shows the cap without a next rise (UIWords 16004). GetWorldLevel, the server level that EXP
    /// bonuses and conditions read, is not touched. With the original rules the server never opens content and both
    /// lookups are the client's own.
    /// </summary>
    public static class LevelCapTip
    {
        /// <summary>A config method called as cfg:Method(day) -> number, table or nil.</summary>
        public delegate object DayLookup(object self, object day);

        // Kept alive here: Lua holds the pushed delegates only weakly through xLua's object pool.
        private static readonly DayLookup LimitWrapper = BaseLimit, NextWrapper = NextBaseInfo;

        private static Obf_R.Obf_Ef _limit, _next;
        private static long _lastDay;

        /// <summary>From LuaPatches.Tick, once per LuaEnv, with the config class (require "lua_cfg_baselvlimitconfig").</summary>
        internal static void Install(Obf_R.Obf_ff config)
        {
            _limit = _next = null;
            _lastDay = 0;
            try
            {
                var limit = config?.Obf_RuA<Obf_R.Obf_Ef>("GetBaseLimit");
                var next = config?.Obf_RuA<Obf_R.Obf_Ef>("GetNextBaseInfo");
                if (limit == null || next == null)
                {
                    Debug.LogWarning("[Ro3Emu.LevelCapTip] Lua_CFG_BaseLvLimitConfig.GetBaseLimit/GetNextBaseInfo not found; the cap shown follows the client's day count");
                    return;
                }
                _limit = limit;
                _next = next;
                config.Obf_luA<string, DayLookup>("GetBaseLimit", LimitWrapper);
                config.Obf_luA<string, DayLookup>("GetNextBaseInfo", NextWrapper);
                Debug.Log("[Ro3Emu.LevelCapTip] server level cap display follows the server's open-content state");
            }
            catch (Exception e)
            {
                Debug.LogWarning("[Ro3Emu.LevelCapTip] not wrapped (" + e.GetType().Name + ": " + e.Message + ")");
            }
        }

        private static object BaseLimit(object self, object day) => First(_limit?.Obf_HuA(self, Day(self, day)));

        private static object NextBaseInfo(object self, object day) => First(_next?.Obf_HuA(self, Day(self, day)));

        /// <summary>The day asked for, or the table's last day while the server opens content.</summary>
        private static object Day(object self, object day)
        {
            if (!ServerDayGates.ContentOpen) return day;
            try
            {
                long last = LastDay(self);
                if (last <= 0) return day;
                Note("level cap shown for day " + last + ", the final stage (the server opens content), instead of day " + LuaPatches.ToLong(day));
                return last;
            }
            catch (Exception e) { Once("day: " + e.GetType().Name + ": " + e.Message); }
            return day;
        }

        /// <summary>The highest day with a row (10000 + day), cached per LuaEnv: the rows are consecutive days.</summary>
        private static long LastDay(object self)
        {
            if (_lastDay > 0) return _lastDay;
            long day = 0;
            while (day < 9999 && First(_limit.Obf_HuA(self, day + 1)) != null) day++;
            _lastDay = day;
            return day;
        }

        private static object First(object[] result) => result != null && result.Length > 0 ? result[0] : null;

        private static readonly System.Collections.Generic.HashSet<string> Reported = new System.Collections.Generic.HashSet<string>();
        private static void Once(string message) { if (Reported.Add(message)) Debug.LogWarning("[Ro3Emu.LevelCapTip] " + message); }
        private static void Note(string message) { if (Reported.Add(message)) Debug.Log("[Ro3Emu.LevelCapTip] " + message); }
    }
}

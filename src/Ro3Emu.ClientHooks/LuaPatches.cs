using System;
using UnityEngine;

namespace Ro3Emu.ClientHooks
{
    /// <summary>
    /// Fixes for bugs in the client's own Lua that the original server would hit as well. No Lua source runs here:
    /// LA_LuaManager.Obf_KD (DoString) is disabled outside the Editor, and the native loader only accepts the client's
    /// own bytecode format (plain source fails with "access denied"). The fixes are table assignments between modules
    /// the client already has, done through its LuaTable objects (xLua API: Obf_LuA = GetInPath, Obf_RuA = Get, Obf_luA = Set;
    /// LA_LuaManager.Obf_MD = require).
    /// <list type="bullet">
    /// <item>Card rewards in the battle pass: UI_Sys_Season_PassListWidgetBase forces the generic item show-func
    /// (LC_ItemShowHelper.GetShowFuncExtra, __index = lc_itemshowfunc) on every reward, but a card reward
    /// (1440108, premium level 5) is drawn by UI_Com_CardWidget, which calls card-only show-func methods (GetCardType,
    /// GetCornerMarkId, GetIsCardStarShow...) unguarded -> "attempt to call a nil value". Only the card widgets call them,
    /// so lc_cardshowfunc's own functions are added to lc_itemshowfunc.</item>
    /// <item>Not a bug but an English need: publicparamconfig.StallsNameCharacterLimit 10 (sized for Chinese) caps the
    /// stall rename input; it becomes StallNameMaxLength, the server's ServerOptions.StallNameMaxLength.</item>
    /// <item>For our open-content policy: the Prelude of the Goddess countdowns (top bar and panel) are hidden while its
    /// window ends more than a year away (PatchCountdowns). Lua functions cannot be written here, so the replaced
    /// methods are C# delegates (SelfMethod) that xLua calls like Lua functions.</item>
    /// <item>The menu's Feedback entry gets a text panel that sends bug reports to the server (FeedbackButton).</item>
    /// <item>"/pass" in chat opens the season task panel with the pass challenges (PassChallenges).</item>
    /// <item>The character window gets a Title button that hosts the title rank widget (TitleButton).</item>
    /// <item>For our open-content policy: the client's own server-day checks (season cards, guild challenge difficulties)
    /// follow what the server reports instead of the day count from the real opening time, and the season page opens in
    /// the season's hidden first week (ServerDayGates).</item>
    /// <item>For our open-content policy: the server level cap shown (quest tracker, character panel) is the final
    /// stage's, the cap the server applies, instead of the day count's (LevelCapTip).</item>
    /// <item>Map config modules this build does not ship (Dracula's Lair, the territory maps) go into package.loaded
    /// before any scene needs them (MapModules).</item>
    /// <item>Profile cards without stickers parse (LC_DAL_Personal.ParsePersonalInfo walks a nil list; PersonalCards).</item>
    /// </list>
    /// </summary>
    public static class LuaPatches
    {
        // Every lc_cardshowfunc function that lc_itemshowfunc / lc_baseshowfunc lack and that only UI_Com_Card* widgets call
        // (IsActive, IsShowName and SetRedDotData have other callers or are guarded, so they stay out).
        private static readonly string[] CardShowFuncs =
        {
            "GetCardType", "GetCornerMarkId", "GetCardLabel", "GetCardPos", "GetCardStar", "GetIsCardStarShow",
            "GetCardGearLabel", "GetCardLevel", "GetIconMax", "IsBan", "IsEmptyLock", "ShowAnim",
        };

        /// <summary>Must equal ServerOptions.StallNameMaxLength.</summary>
        internal const int StallNameMaxLength = 24;

        /// <summary>A window that ends later than this is open for good (OpenContent: ActivityClock.Forever, 2099):
        /// its countdown would read tens of thousands of days, so it is hidden instead.</summary>
        private const long PerpetualAfterSeconds = 365L * 86400;

        private static object _doneEnv;
        private static int _frame;
        // The controller class tables already wrapped in this LuaEnv (their modules load on first use of the UI).
        private static Obf_R.Obf_ff _global, _topBarClass, _evePanelClass;
        private static Obf_R.Obf_Ef _topBarState, _topBarRefresh, _evePanelOriginal;
        // Kept alive here: Lua holds the pushed delegates only weakly through xLua's object pool.
        private static readonly SelfMethod TopBarStateWrapper = UpdateCountdownState, TopBarRefreshWrapper = RefreshCountdown,
            EvePanelWrapper = RefreshEveTimeText;

        /// <summary>A Lua method called as self:Method().</summary>
        public delegate void SelfMethod(Obf_R.Obf_ff self);

        /// <summary>From UiTweaks.Tick (every 10th frame); works every 30th: (re)applies when the LuaEnv is (re)created.</summary>
        public static void Tick()
        {
            if (++_frame % 3 != 0) return;
            object env = null;
            try
            {
                var manager = HappyEngine.Base.SingletonMonoBehaviour<LA_LuaManager>.Instance;
                if (manager == null) return;
                env = EnvOf(manager, out string why);
                if (env == null) { Report(why); return; }
                var global = ((Obf_R.Obf_Cf)env).Obf_A;
                MapModules.Install(env, global); // own once-per-LuaEnv guard; needs only package.loaded
                if (ReferenceEquals(env, _doneEnv)) { ServerDayGates.Probe(); return; }
                _topBarClass = _evePanelClass = null;
                // GlobalRequire sets LC_ItemShowHelper once the game's modules are up; before that, requiring is unsafe.
                if (global == null || global.Obf_RuA<object>("LC_ItemShowHelper") == null) { Report("game helpers not loaded"); return; }
                var item = Module(manager, global, "lc_itemshowfunc");
                var card = Module(manager, global, "lc_cardshowfunc");
                if (item == null || card == null) { Report("show-func modules not loaded"); return; }
                int added = 0;
                foreach (var name in CardShowFuncs)
                {
                    if (item.Obf_RuA<object>(name) != null) continue;
                    var function = card.Obf_RuA<object>(name);
                    if (function == null) continue;
                    item.Obf_luA<string, object>(name, function);
                    added++;
                }
                var param = Module(manager, global, "publicparamconfig");
                if (param != null) param.Obf_luA<string, object>("StallsNameCharacterLimit", (long)StallNameMaxLength);
                _doneEnv = env;
                PatchCountdowns(manager, global);
                var functionHelper = Module(manager, global, "lc_functionhelper");
                FeedbackButton.Install(global, functionHelper);
                PassChallenges.Install(global);
                TitleButton.Install(manager, global);
                ServerDayGates.Install(global, functionHelper, Module(manager, global, "lc_conditiondefine"),
                    global.Obf_RuA<Obf_R.Obf_ff>("LC_SeasonHelper") ?? Module(manager, global, "lc_seasonhelper"),
                    global.Obf_RuA<Obf_R.Obf_ff>("LC_SeasonExplorationHelper") ?? Module(manager, global, "lc_seasonexplorationhelper"));
                LevelCapTip.Install(Module(manager, global, "lua_cfg_baselvlimitconfig"));
                PersonalCards.Install((Obf_R.Obf_Cf)env, Module(manager, global, "lc_dal_personal"));
                Debug.Log("[Ro3Emu.LuaPatches] lc_itemshowfunc: " + added + " card show-func method(s) added; stall name limit "
                    + (param != null ? StallNameMaxLength.ToString() : "not set (publicparamconfig missing)"));
            }
            catch (Exception e)
            {
                _doneEnv = env; // one attempt per LuaEnv: a failure here is not retried every tick
                Debug.LogWarning("[Ro3Emu.LuaPatches] " + e.GetType().Name + ": " + e.Message);
            }
        }

        /// <summary>
        /// Hides the countdowns of windows that are open for good (OpenContent keeps the season windows open until
        /// 2099; the client still counts down to that end). Two places draw one, both for the Prelude of the Goddess:
        /// <list type="bullet">
        /// <item>the top bar's function button: UI_Sys_Main_FunctionItemWidget_PCCtrl._updateCountdownState shows
        /// m_kTTXT_TimeFunction and starts a 1 s timer for enum_FunctionType.SeasonEve, whose _refreshCountdown prints
        /// "Nd" from LC_ActivityHelper.GetTimeRange(enum_SeasonResidentActivities.SeasonEve);</item>
        /// <item>the Prelude panel: UI_Sys_Season_EvePlayPanelCtrl._refreshTimeText builds day/hour/minute widgets
        /// under m_kRTRANSFORM_EvePlayTime from the same range.</item>
        /// </list>
        /// Each method is replaced through the class table (BaseClass's __newindex writes the shared vtbl, so open
        /// widgets switch too) by a C# wrapper that calls the original and only acts when the window ends more than a
        /// year from now, so a real dated window keeps its countdown. Both modules are required here, before the world UI
        /// exists: a button's timer captures self._refreshCountdown when it starts, so wrapping later changes nothing.
        /// </summary>
        private static void PatchCountdowns(LA_LuaManager manager, Obf_R.Obf_ff global)
        {
            _global = global;
            if (_topBarClass == null && Module(manager, global, "ui_sys_main_functionitemwidget_pcctrl") is { } top)
            {
                _topBarClass = top;
                // Both: a button set up before this patch keeps its 1 s timer, which only calls _refreshCountdown.
                _topBarState = top.Obf_RuA<Obf_R.Obf_Ef>("_updateCountdownState");
                _topBarRefresh = top.Obf_RuA<Obf_R.Obf_Ef>("_refreshCountdown");
                if (_topBarState != null) top.Obf_luA<string, SelfMethod>("_updateCountdownState", TopBarStateWrapper);
                if (_topBarRefresh != null) top.Obf_luA<string, SelfMethod>("_refreshCountdown", TopBarRefreshWrapper);
                Debug.Log("[Ro3Emu.LuaPatches] top bar countdown " + (_topBarState != null && _topBarRefresh != null ? "wrapped" : "method not found"));
            }
            if (_evePanelClass == null && Module(manager, global, "ui_sys_season_eveplaypanelctrl") is { } panel)
            {
                _evePanelClass = panel;
                _evePanelOriginal = panel.Obf_RuA<Obf_R.Obf_Ef>("_refreshTimeText");
                if (_evePanelOriginal != null) panel.Obf_luA<string, SelfMethod>("_refreshTimeText", EvePanelWrapper);
                Debug.Log("[Ro3Emu.LuaPatches] Prelude panel countdown " + (_evePanelOriginal != null ? "wrapped" : "method not found"));
            }
        }

        private static void UpdateCountdownState(Obf_R.Obf_ff self)
        {
            _topBarState?.Obf_HuA(self);
            HideTopBarCountdown(self);
        }

        private static void RefreshCountdown(Obf_R.Obf_ff self)
        {
            if (!HideTopBarCountdown(self)) _topBarRefresh?.Obf_HuA(self);
        }

        /// <summary>For the Prelude button with an open-for-good window: stops its timer and hides the text.</summary>
        private static bool HideTopBarCountdown(Obf_R.Obf_ff self)
        {
            try
            {
                var global = _global;
                var info = self.Obf_RuA<Obf_R.Obf_ff>("m_kFunctionInfo");
                long eve = global == null ? long.MinValue : ToLong(global.Obf_LuA<object>("enum_FunctionType.SeasonEve"));
                if (info == null || eve == long.MinValue || ToLong(info.Obf_RuA<object>("functionId")) != eve) return false;
                bool perpetual = EvePerpetual(global);
                Note("top bar Prelude countdown: " + (perpetual ? "hidden (window open for good)" : "kept (dated window)"));
                if (!perpetual) return false;
                self.Obf_RuA<Obf_R.Obf_Ef>("_stopCountdownTimer")?.Obf_HuA(self);
                Call(self.Obf_LuA<object>("View.m_kTTXT_TimeFunction"), "SetActive", false);
                return true;
            }
            catch (Exception e) { Once("top bar countdown: " + e.GetType().Name + ": " + e.Message); return false; }
        }

        private static void RefreshEveTimeText(Obf_R.Obf_ff self)
        {
            try
            {
                var global = _global;
                bool perpetual = global != null && EvePerpetual(global);
                Note("Prelude panel countdown: " + (perpetual ? "hidden (window open for good)" : "kept (dated window)"));
                ShowEveTimeBlock(self, !perpetual);
                if (perpetual) return;
            }
            catch (Exception e) { Once("Prelude panel countdown: " + e.GetType().Name + ": " + e.Message); }
            _evePanelOriginal?.Obf_HuA(self);
        }

        /// <summary>
        /// The whole "Season 1 · ... begins in:" block of Pan_Title, as the prefab nests it (seen in game 11.10):
        /// Pan_Title { Txt_Time (the caption), TimePos_GraphicSwitchG { Pic_TimeBg (hourglass and bar),
        /// EvePlayTime_RTransform (the day/hour/minute widgets) } }. Shown again for a dated window: panels are pooled.
        /// </summary>
        private static void ShowEveTimeBlock(Obf_R.Obf_ff self, bool show)
        {
            var wrapper = self.Obf_LuA<Obf_R.Obf_ff>("View.m_kRTRANSFORM_EvePlayTime");
            var times = wrapper?.Obf_RuA<Obf_R.Obf_Ef>("GetRectTransform")?.Obf_HuA(wrapper);
            if (!(times != null && times.Length > 0 && times[0] is Transform time) || time.parent == null) return;
            var block = time.parent;
            if (block.gameObject.activeSelf != show) block.gameObject.SetActive(show);
            var caption = block.parent != null ? block.parent.Find("Txt_Time") : null;
            if (caption != null && caption.gameObject.activeSelf != show) caption.gameObject.SetActive(show);
        }

        /// <summary>The Prelude window's end, from the same helper the client counts down with, against server time.</summary>
        private static bool EvePerpetual(Obf_R.Obf_ff global)
        {
            var range = global.Obf_LuA<Obf_R.Obf_Ef>("LC_ActivityHelper.GetTimeRange")
                ?.Obf_HuA(global.Obf_LuA<object>("enum_SeasonResidentActivities.SeasonEve"));
            var timers = global.Obf_RuA<object>("TimerManagerIns");
            var now = timers is Obf_R.Obf_ff table ? table.Obf_RuA<Obf_R.Obf_Ef>("GetSystemTime")?.Obf_HuA(table) : null;
            if (range == null || range.Length < 2 || now == null || now.Length < 1) return false;
            return ToLong(range[1]) - ToLong(now[0]) > PerpetualAfterSeconds;
        }

        /// <summary>obj:Method(arg) on a C# component or on a Lua view wrapper.</summary>
        private static void Call(object target, string method, bool arg)
        {
            if (target is Obf_R.Obf_ff table) { table.Obf_RuA<Obf_R.Obf_Ef>(method)?.Obf_HuA(table, arg); return; }
            target?.GetType().GetMethod(method, new[] { typeof(bool) })?.Invoke(target, new object[] { arg });
        }

        internal static long ToLong(object value) => value switch
        {
            long l => l,
            int i => i,
            double d => (long)d,
            float f => (long)f,
            _ => long.MinValue,
        };

        private static readonly System.Collections.Generic.HashSet<string> Reported = new System.Collections.Generic.HashSet<string>();
        private static void Once(string message) { if (Reported.Add(message)) Debug.LogWarning("[Ro3Emu.LuaPatches] " + message); }
        private static void Note(string message) { if (Reported.Add(message)) Debug.Log("[Ro3Emu.LuaPatches] " + message); }

        /// <summary>package.loaded[name], or require(name) when not loaded yet.</summary>
        internal static Obf_R.Obf_ff Module(LA_LuaManager manager, Obf_R.Obf_ff global, string name)
        {
            if (global.Obf_LuA<Obf_R.Obf_ff>("package.loaded." + name) is { } loaded) return loaded;
            var result = manager.Obf_MD(name);
            return result != null && result.Length > 0 ? result[0] as Obf_R.Obf_ff : null;
        }

        /// <summary>LA_LuaManager's LuaEnv (field Obf_Dc), only when the manager reports it initialised (Obf_Fc).</summary>
        private static object EnvOf(LA_LuaManager manager, out string why)
        {
            const System.Reflection.BindingFlags all = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic;
            var type = typeof(LA_LuaManager);
            var flag = type.GetField("Obf_Fc", all);
            var envField = type.GetField("Obf_Dc", all);
            why = flag == null || envField == null ? "LA_LuaManager fields not found" : null;
            if (why != null) return null;
            if (!(flag.GetValue(manager) is bool inited) || !inited) { why = "LuaEnv not initialised"; return null; }
            var env = envField.GetValue(manager);
            if (env == null) why = "LuaEnv null";
            return env;
        }

        private static string _lastReport;

        /// <summary>Logs each distinct waiting state once (diagnostics without spam).</summary>
        private static void Report(string state)
        {
            if (state == _lastReport) return;
            _lastReport = state;
            Debug.Log("[Ro3Emu.LuaPatches] waiting: " + state);
        }
    }
}

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using HappyEngine.Base;
using UnityEngine;

namespace Ro3Emu.ClientHooks
{
    /// <summary>
    /// Opt-in skill presentation trace for player acceptance. It wraps the client's existing Lua methods without
    /// replacing their behaviour and writes the decoded 31100-family fields plus timeline construction to Player.log.
    /// Enable with GM chat command <c>!client skilltrace on</c>; disable with <c>!client skilltrace off</c>.
    /// </summary>
    internal static class SkillTrace
    {
        public delegate void RouteHandler(Obf_R.Obf_ff self, Obf_R.Obf_ff message);
        public delegate void BuildTimeline(Obf_R.Obf_ff self, object unit, Obf_R.Obf_ff message, object parentAgent);

        private static readonly string[] RouteSymbols =
        {
            "USEINSKILL", "USEINSKILL_HIGH_LEVEL", "EXTRA_USEINSKILL", "EXTRA_USEINSKILL_HIGH_LEVEL",
            "SKILLHIT", "SKILL_EFFECT_DISPLAY_NOTIFY", "SKILL_EFFECT_DISPLAY_NOTIFY_HIGH_LEVEL",
            "SKILL_FIXTIME_BULLET_NOTIFY", "SKILL_FIXTIME_BULLET_NOTIFY_HIGH_LEVEL",
        };
        private static readonly RouteHandler RouteWrapper = TraceRoute;
        private static readonly BuildTimeline BuildWrapper = TraceBuild;
        private static readonly Dictionary<string, Obf_R.Obf_Ef> OriginalRoutes = new Dictionary<string, Obf_R.Obf_Ef>();
        private static Obf_R.Obf_ff _global, _sceneInstance, _routeTable, _timelineClass;
        private static Obf_R.Obf_Ef _originalBuild;
        private static object _environment;
        private static bool _enabled;

        internal static void Command(string command)
        {
            string value = (command ?? "").Trim().ToLowerInvariant();
            if (value == "on" || value == "1" || value == "true") Enable();
            else if (value == "off" || value == "0" || value == "false") Disable();
            else Debug.Log("[Ro3Emu.SkillTrace] " + (_enabled ? "on" : "off")
                + "; use !client skilltrace on|off");
        }

        private static void Enable()
        {
            if (_enabled) { Debug.Log("[Ro3Emu.SkillTrace] already on"); return; }
            try
            {
                var manager = SingletonMonoBehaviour<LA_LuaManager>.Instance;
                if (manager == null) { Debug.LogWarning("[Ro3Emu.SkillTrace] Lua manager not ready"); return; }
                var environment = EnvironmentOf(manager);
                if (environment == null) { Debug.LogWarning("[Ro3Emu.SkillTrace] Lua environment not ready"); return; }
                var global = ((Obf_R.Obf_Cf)environment).Obf_A;
                if (global == null) { Debug.LogWarning("[Ro3Emu.SkillTrace] Lua global table not ready"); return; }

                _timelineClass = global.Obf_RuA<Obf_R.Obf_ff>("LC_TimeLine_Object")
                    ?? LuaPatches.Module(manager, global, "lc_timeline_object");
                _originalBuild = _timelineClass?.Obf_RuA<Obf_R.Obf_Ef>("Build");
                if (_originalBuild == null)
                {
                    _timelineClass = null;
                    Debug.LogWarning("[Ro3Emu.SkillTrace] timeline class not loaded");
                    return;
                }

                _timelineClass.Obf_luA<string, BuildTimeline>("Build", BuildWrapper);
                _global = global;
                _environment = environment;
                _enabled = true;
                BindScene();
                Debug.Log("[Ro3Emu.SkillTrace] on; tracing skill routes and timeline Build");
            }
            catch (Exception e)
            {
                DisableCore();
                Debug.LogError("[Ro3Emu.SkillTrace] enable failed: " + e);
            }
        }

        private static void Disable()
        {
            if (!_enabled) { Debug.Log("[Ro3Emu.SkillTrace] already off"); return; }
            try
            {
                RestoreScene();
                if (_timelineClass != null && _originalBuild != null)
                    _timelineClass.Obf_luA<string, Obf_R.Obf_Ef>("Build", _originalBuild);
                Debug.Log("[Ro3Emu.SkillTrace] off");
            }
            catch (Exception e) { Debug.LogWarning("[Ro3Emu.SkillTrace] restore failed: " + e.Message); }
            finally { DisableCore(); }
        }

        /// <summary>Rebind after a map switch: LC_Scene_Sync snapshots its handlers into each instance's KVMsgFunc.</summary>
        internal static void Tick()
        {
            if (!_enabled) return;
            if (!EnvironmentAlive())
            {
                Debug.LogWarning("[Ro3Emu.SkillTrace] Lua environment changed; trace disabled");
                DisableCore();
                return;
            }
            try { BindScene(); }
            catch (Exception e) { Debug.LogWarning("[Ro3Emu.SkillTrace] scene bind failed: " + e.Message); }
        }

        private static void TraceRoute(Obf_R.Obf_ff self, Obf_R.Obf_ff message)
        {
            string fields = Fields(message);
            Debug.Log("[Ro3Emu.SkillTrace] recv " + fields);
            try
            {
                string route = Value(message?.Obf_RuA<object>("pbc_msg_name"));
                if (!OriginalRoutes.TryGetValue(route, out var original))
                {
                    Debug.LogError("[Ro3Emu.SkillTrace] original route not found: " + route);
                    return;
                }
                original.Obf_HuA(self, message);
                Debug.Log("[Ro3Emu.SkillTrace] handler complete " + fields);
            }
            catch (Exception e)
            {
                Debug.LogError("[Ro3Emu.SkillTrace] handler failed " + fields + ": " + e);
                throw;
            }
        }

        private static void TraceBuild(Obf_R.Obf_ff self, object unit, Obf_R.Obf_ff message, object parentAgent)
        {
            string fields = Fields(message);
            try
            {
                _originalBuild?.Obf_HuA(self, unit, message, parentAgent);
                bool hasSkillData = self?.Obf_RuA<object>("m_kNowSkillData") != null;
                object correlation = self?.Obf_RuA<object>("m_iCorKey");
                Debug.Log("[Ro3Emu.SkillTrace] timeline built=" + hasSkillData
                    + " corKey=" + Value(correlation) + " " + fields);
            }
            catch (Exception e)
            {
                Debug.LogError("[Ro3Emu.SkillTrace] timeline failed " + fields + ": " + e);
                throw;
            }
        }

        private static string Fields(Obf_R.Obf_ff message)
        {
            if (message == null) return "message=null";
            return "route=" + Value(message.Obf_RuA<object>("pbc_msg_name"))
                + " src=" + First(message, "srcID", "sourceId", "entityID")
                + " skill=" + First(message, "skillID", "skillId", "skill_id", "skillConfigId")
                + " target=" + Value(message.Obf_RuA<object>("targetId"))
                + " session=" + Value(message.Obf_RuA<object>("sessionId"))
                + " chantMs=" + Value(message.Obf_RuA<object>("chantMs"))
                + " skillMs=" + Value(message.Obf_RuA<object>("skillMs"))
                + " playAct=" + Value(message.Obf_RuA<object>("playAct"))
                + " showPoint=" + Value(message.Obf_RuA<object>("showPoint"))
                + " effectId=" + Value(message.Obf_RuA<object>("effectId"))
                + " fixedTime=" + Value(message.Obf_RuA<object>("fixedTime"))
                + " startTime=" + Value(message.Obf_RuA<object>("startTime"))
                + " startTimestampMs=" + Value(message.Obf_RuA<object>("startTimestampMs"))
                + " clientStart=" + Value(message.Obf_RuA<object>("iStartClientTime"));
        }

        private static string First(Obf_R.Obf_ff message, params string[] names)
        {
            foreach (string name in names)
            {
                object value = message.Obf_RuA<object>(name);
                if (value != null) return Value(value);
            }
            return "null";
        }

        private static void BindScene()
        {
            var manager = _global?.Obf_RuA<Obf_R.Obf_ff>("LC_SceneManagerIns");
            var result = manager?.Obf_RuA<Obf_R.Obf_Ef>("GetCurScene")?.Obf_HuA(manager);
            var scene = result != null && result.Length > 0 ? result[0] as Obf_R.Obf_ff : null;
            if (scene == null || ReferenceEquals(scene, _sceneInstance)) return;

            RestoreScene();
            var routes = scene.Obf_RuA<Obf_R.Obf_ff>("KVMsgFunc");
            if (routes == null) return;
            foreach (string symbol in RouteSymbols)
            {
                string route = Value(_global.Obf_LuA<object>("NW_Helper.ProtobufNameMap." + symbol));
                if (route == "null" || OriginalRoutes.ContainsKey(route)) continue;
                var original = routes.Obf_RuA<Obf_R.Obf_Ef>(route);
                if (original == null) continue;
                OriginalRoutes.Add(route, original);
                routes.Obf_luA<string, RouteHandler>(route, RouteWrapper);
            }
            _sceneInstance = scene;
            _routeTable = routes;
            Debug.Log("[Ro3Emu.SkillTrace] scene routes wrapped=" + OriginalRoutes.Count);
        }

        private static void RestoreScene()
        {
            if (_routeTable != null)
                foreach (var item in OriginalRoutes) _routeTable.Obf_luA<string, Obf_R.Obf_Ef>(item.Key, item.Value);
            OriginalRoutes.Clear();
            _routeTable = null;
            _sceneInstance = null;
        }

        private static string Value(object value)
        {
            if (value == null) return "null";
            if (value is IFormattable formattable) return formattable.ToString(null, CultureInfo.InvariantCulture);
            return value.ToString();
        }

        private static object EnvironmentOf(LA_LuaManager manager)
        {
            const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
            var type = typeof(LA_LuaManager);
            var ready = type.GetField("Obf_Fc", flags);
            var environment = type.GetField("Obf_Dc", flags);
            if (ready == null || environment == null || !(ready.GetValue(manager) is bool initialized) || !initialized) return null;
            return environment.GetValue(manager);
        }

        private static bool EnvironmentAlive()
        {
            var manager = SingletonMonoBehaviour<LA_LuaManager>.Instance;
            return manager != null && ReferenceEquals(_environment, EnvironmentOf(manager));
        }

        private static void DisableCore()
        {
            _enabled = false;
            _environment = null;
            _global = _sceneInstance = _routeTable = _timelineClass = null;
            OriginalRoutes.Clear();
            _originalBuild = null;
        }
    }
}

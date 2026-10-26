using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
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

        private sealed class RouteSpec
        {
            public RouteSpec(string symbol, uint msgId) { Symbol = symbol; MsgId = msgId; }
            public string Symbol { get; }
            public uint MsgId { get; }
        }

        private static readonly RouteSpec[] Routes =
        {
            new RouteSpec("USEINSKILL", 31100), new RouteSpec("USEINSKILL_HIGH_LEVEL", 31106),
            new RouteSpec("EXTRA_USEINSKILL", 31121), new RouteSpec("EXTRA_USEINSKILL_HIGH_LEVEL", 31107),
            new RouteSpec("SKILLHIT", 31101), new RouteSpec("SKILL_EFFECT_DISPLAY_NOTIFY", 31119),
            new RouteSpec("SKILL_EFFECT_DISPLAY_NOTIFY_HIGH_LEVEL", 31109),
            new RouteSpec("SKILL_FIXTIME_BULLET_NOTIFY", 31120),
            new RouteSpec("SKILL_FIXTIME_BULLET_NOTIFY_HIGH_LEVEL", 31108),
        };
        private static readonly BuildTimeline BuildWrapper = TraceBuild;
        private static readonly Dictionary<string, Obf_R.Obf_Ef> OriginalRoutes = new Dictionary<string, Obf_R.Obf_Ef>();
        private static readonly Dictionary<string, RouteHandler> RouteWrappers = new Dictionary<string, RouteHandler>();
        private static readonly Dictionary<string, uint> RouteIds = new Dictionary<string, uint>();
        private static Obf_R.Obf_ff _global, _sceneInstance, _routeTable, _timelineClass;
        private static Obf_R.Obf_Ef _originalBuild;
        private static object _environment;
        private static bool _enabled;
        private static bool _markerControls;
        private static byte[] _telemetryToken;
        private static DateTime _telemetryExpiresUtc;
        private static uint _telemetrySequence;
        private static SkillTraceReportLimiter _telemetryLimiter;
        private static int _nextMarkerCheckFrame;

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
                RestoreHooks();
                DisableCore();
                Debug.LogError("[Ro3Emu.SkillTrace] enable failed: " + e);
            }
        }

        private static void Disable()
        {
            if (!_enabled) { Debug.Log("[Ro3Emu.SkillTrace] already off"); return; }
            try
            {
                RestoreHooks();
                Debug.Log("[Ro3Emu.SkillTrace] off");
            }
            finally { DisableCore(); }
        }

        private static void RestoreHooks()
        {
            try { RestoreScene(); }
            catch (Exception e) { Debug.LogWarning("[Ro3Emu.SkillTrace] route restore failed: " + e.Message); }
            try
            {
                if (_timelineClass != null && _originalBuild != null)
                    _timelineClass.Obf_luA<string, Obf_R.Obf_Ef>("Build", _originalBuild);
            }
            catch (Exception e) { Debug.LogWarning("[Ro3Emu.SkillTrace] timeline restore failed: " + e.Message); }
        }

        /// <summary>Rebind after a map switch: LC_Scene_Sync snapshots its handlers into each instance's KVMsgFunc.</summary>
        internal static void Tick()
        {
            CheckLocalMarker();
            CheckTelemetryGrant();
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

        /// <summary>
        /// A local marker enables tracing without sending a chat/GM command to the connected server. This is useful
        /// when comparing a client against a server that does not implement RO3Emu's diagnostic command.
        /// </summary>
        private static void CheckLocalMarker()
        {
            if (Time.frameCount < _nextMarkerCheckFrame) return;
            _nextMarkerCheckFrame = Time.frameCount + 60;
            try
            {
                string marker = Path.GetFullPath(Path.Combine(Application.dataPath, "..", "ro3emu", "skilltrace.on"));
                bool requested = File.Exists(marker);
                if (requested && !_markerControls)
                {
                    if (!_enabled) Enable();
                    _markerControls = _enabled;
                    if (_enabled) Debug.Log("[Ro3Emu.SkillTrace] enabled by local marker " + marker);
                }
                else if (!requested && _markerControls)
                {
                    _markerControls = false;
                    Disable();
                }
            }
            catch (Exception e)
            {
                Debug.LogWarning("[Ro3Emu.SkillTrace] local marker check failed: " + e.Message);
            }
        }

        /// <summary>The server grant arrives on the network thread and is handed here by ServerImAdapter's main-thread pump.</summary>
        internal static void Grant(byte[] body)
        {
            if (!SkillTraceProtocol.TryDecodeGrant(body, out var grant))
            {
                Debug.LogWarning("[Ro3Emu.SkillTrace] invalid telemetry grant");
                return;
            }
            _telemetryToken = grant.Token;
            _telemetryExpiresUtc = DateTime.UtcNow.AddSeconds(grant.TtlSeconds);
            _telemetrySequence = 0;
            _telemetryLimiter = new SkillTraceReportLimiter(grant.EventsPerSecond, grant.Burst, MonotonicMs());
            Debug.Log("[Ro3Emu.SkillTrace] telemetry grant received; local tracing remains opt-in");
        }

        internal static void Disconnect()
        {
            _telemetryToken = null;
            _telemetryExpiresUtc = default;
            _telemetrySequence = 0;
            _telemetryLimiter = null;
        }

        private static void CheckTelemetryGrant()
        {
            if (_telemetryToken == null || DateTime.UtcNow < _telemetryExpiresUtc) return;
            Disconnect();
            Debug.Log("[Ro3Emu.SkillTrace] telemetry grant expired");
        }

        private static void TraceRoute(uint msgId, string route, Obf_R.Obf_Ef original, Obf_R.Obf_ff self, Obf_R.Obf_ff message)
        {
            string fields = SafeFields(message, route);
            Debug.Log("[Ro3Emu.SkillTrace] recv " + fields);
            Report(message, msgId, SkillTraceProtocol.Stage.RouteReceived, true, null, 0);
            try
            {
                original.Obf_HuA(self, message);
                Debug.Log("[Ro3Emu.SkillTrace] handler complete " + fields);
                Report(message, msgId, SkillTraceProtocol.Stage.HandlerCompleted, true, null, 0);
            }
            catch (Exception e)
            {
                Debug.LogError("[Ro3Emu.SkillTrace] handler failed " + fields + ": " + e);
                Report(message, msgId, SkillTraceProtocol.Stage.HandlerFailed, false, e.GetType().Name, 0);
                throw;
            }
        }

        private static void TraceBuild(Obf_R.Obf_ff self, object unit, Obf_R.Obf_ff message, object parentAgent)
        {
            string route = Value(message?.Obf_RuA<object>("pbc_msg_name"));
            uint msgId = RouteIds.TryGetValue(route, out uint id) ? id : 0;
            string fields = SafeFields(message, route == "null" ? "timeline.Build" : route);
            try
            {
                _originalBuild?.Obf_HuA(self, unit, message, parentAgent);
            }
            catch (Exception e)
            {
                Debug.LogError("[Ro3Emu.SkillTrace] timeline failed " + fields + ": " + e);
                Report(message, msgId, SkillTraceProtocol.Stage.TimelineFailed, false, e.GetType().Name, 0);
                throw;
            }
            try
            {
                bool hasSkillData = self?.Obf_RuA<object>("m_kNowSkillData") != null;
                object correlation = self?.Obf_RuA<object>("m_iCorKey");
                Debug.Log("[Ro3Emu.SkillTrace] timeline built=" + hasSkillData
                    + " corKey=" + Value(correlation) + " " + fields);
                Report(message, msgId, SkillTraceProtocol.Stage.TimelineBuilt, hasSkillData,
                    hasSkillData ? null : "NoSkillData", Number(correlation));
            }
            catch (Exception e) { Debug.LogWarning("[Ro3Emu.SkillTrace] timeline trace failed " + fields + ": " + e.Message); }
        }

        private static void Report(Obf_R.Obf_ff message, uint msgId, SkillTraceProtocol.Stage stage, bool success,
            string reason, long correlation)
        {
            if (!_enabled || _telemetryToken == null || _telemetryLimiter == null
                || DateTime.UtcNow >= _telemetryExpiresUtc || !SkillTraceProtocol.IsSkillMessage(msgId)) return;
            try
            {
                long clientMs = MonotonicMs();
                if (!_telemetryLimiter.TryTake(clientMs)) return;
                var report = new SkillTraceProtocol.Report(_telemetryToken, ++_telemetrySequence,
                    clientMs, msgId,
                    (int)FirstNumber(message, "skillID", "skillId", "skill_id", "skillConfigId"),
                    FirstNumber(message, "srcID", "sourceId", "entityID"), Number(message?.Obf_RuA<object>("targetId")),
                    Number(message?.Obf_RuA<object>("sessionId")), correlation,
                    (int)Number(message?.Obf_RuA<object>("showPoint")),
                    (int)Number(message?.Obf_RuA<object>("effectId")),
                    FirstNumber(message, "startTimestampMs", "startTime", "iStartClientTime"), stage, success, reason);
                SingletonMonoBehaviour<NW_CBBNetworkManager>.Instance.Obf_jBd(
                    SkillTraceProtocol.ReportMsgId, SkillTraceProtocol.EncodeReport(report));
            }
            catch (Exception e)
            {
                Debug.LogWarning("[Ro3Emu.SkillTrace] telemetry send failed: " + e.GetType().Name + ": " + e.Message);
            }
        }

        private static long MonotonicMs() => (long)(Time.realtimeSinceStartup * 1000d);

        private static string SafeFields(Obf_R.Obf_ff message, string route)
        {
            try { return Fields(message, route); }
            catch (Exception e) { return "route=" + route + " traceError=" + e.GetType().Name + ":" + e.Message; }
        }

        private static string Fields(Obf_R.Obf_ff message, string route)
        {
            if (message == null) return "message=null";
            return "route=" + route
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

        private static long FirstNumber(Obf_R.Obf_ff message, params string[] names)
        {
            if (message == null) return 0;
            foreach (string name in names)
            {
                object value = message.Obf_RuA<object>(name);
                if (value != null) return Number(value);
            }
            return 0;
        }

        private static long Number(object value)
        {
            if (value == null) return 0;
            try { return Convert.ToInt64(value, CultureInfo.InvariantCulture); }
            catch (Exception) { return 0; }
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
            foreach (RouteSpec spec in Routes)
            {
                string route = Value(_global.Obf_LuA<object>("NW_Helper.ProtobufNameMap." + spec.Symbol));
                if (route == "null" || OriginalRoutes.ContainsKey(route)) continue;
                var original = routes.Obf_RuA<Obf_R.Obf_Ef>(route);
                if (original == null) continue;
                RouteHandler wrapper = (self, message) => TraceRoute(spec.MsgId, route, original, self, message);
                OriginalRoutes.Add(route, original);
                RouteWrappers.Add(route, wrapper);
                RouteIds.Add(route, spec.MsgId);
                routes.Obf_luA<string, RouteHandler>(route, wrapper);
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
            RouteWrappers.Clear();
            RouteIds.Clear();
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
            _markerControls = false;
            _environment = null;
            _global = _sceneInstance = _routeTable = _timelineClass = null;
            OriginalRoutes.Clear();
            RouteWrappers.Clear();
            RouteIds.Clear();
            _originalBuild = null;
        }
    }
}

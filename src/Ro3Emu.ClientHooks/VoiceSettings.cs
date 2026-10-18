using System;
using System.Runtime.InteropServices;
using HappyEngine.Base;
using Debug = UnityEngine.Debug;

namespace Ro3Emu.ClientHooks
{
    /// <summary>
    /// Voice playback follows the game's own sound settings, which only drive Wwise (voice goes through winmm, see
    /// VoiceAudio): the Master slider and its tick box (AD_WwiseManager GetVolume / GetMuteState of
    /// "RPTC_Volume_Master", as the settings panel reads them, LC_SettingProcessFunc) and "Mute When Minimized"
    /// (lc_settinghelper.GetValue(enum_SettingItemType.MinimizeMute) == 1 while the game window is minimized, the
    /// same rule as GameMain.OnWindowMinimizeChanged, whose Wwise mute event the master state does not show).
    /// Polled from the main-thread pump twice a second; any failure leaves the last value.
    /// </summary>
    internal static class VoiceSettings
    {
        private const long PollMs = 500;
        private const string MasterRtpc = "RPTC_Volume_Master";

        private static long _lastPoll = -PollMs;
        private static IntPtr _window;
        private static string _lastProblem;

        /// <summary>Main thread, every frame (cheap: returns unless a poll is due).</summary>
        public static void Poll(long nowMs)
        {
            if (nowMs - _lastPoll < PollMs) return;
            _lastPoll = nowMs;
            try
            {
                var global = SingletonMonoBehaviour<LA_LuaManager>.Instance?.Obf_jD()?.Obf_A;
                if (global == null) return;
                float scale = Master(global);
                if (MinimizeMuteOn(global) && Minimized()) scale = 0f;
                VoiceAudio.OutputScale = scale;
            }
            catch (Exception e)
            {
                string problem = e.GetType().Name + ": " + e.Message;
                if (problem != _lastProblem) Debug.LogWarning("[Ro3Emu.Voice] sound settings: " + problem);
                _lastProblem = problem;
            }
        }

        /// <summary>The Master slider as 0..1 (the panel shows it as a percentage), 0 while its tick box is off.</summary>
        private static float Master(Obf_R.Obf_ff global)
        {
            if (!(global.Obf_RuA<object>("AD_WwiseManager") is Obf_R.Obf_ff wwise)) return VoiceAudio.OutputScale;
            var muted = wwise.Obf_RuA<Obf_R.Obf_Ef>("GetMuteState")?.Obf_HuA(wwise, MasterRtpc);
            if (muted != null && muted.Length > 0 && muted[0] is bool m && m) return 0f;
            var volume = wwise.Obf_RuA<Obf_R.Obf_Ef>("GetVolume")?.Obf_HuA(wwise, MasterRtpc);
            if (volume == null || volume.Length == 0 || volume[0] == null) return VoiceAudio.OutputScale;
            double v = Convert.ToDouble(volume[0]);
            if (v > 1.0) v /= 100.0; // a 0..100 slider
            return (float)Math.Max(0.0, Math.Min(1.0, v));
        }

        private static bool MinimizeMuteOn(Obf_R.Obf_ff global)
        {
            var getValue = global.Obf_LuA<Obf_R.Obf_Ef>("package.loaded.lc_settinghelper.GetValue");
            var item = global.Obf_LuA<object>("enum_SettingItemType.MinimizeMute");
            var value = item != null ? getValue?.Obf_HuA(item) : null;
            return value != null && value.Length > 0 && value[0] != null && Convert.ToDouble(value[0]) == 1.0;
        }

        /// <summary>The game window, remembered while it is the active window of this (main) thread, is minimized.</summary>
        private static bool Minimized()
        {
            var active = GetActiveWindow();
            if (active != IntPtr.Zero) _window = active;
            return _window != IntPtr.Zero && IsIconic(_window);
        }

        [DllImport("user32.dll")] private static extern IntPtr GetActiveWindow();
        [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr window);
    }
}

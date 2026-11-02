using System;
using UnityEngine;

namespace Ro3Emu.ClientHooks
{
    /// <summary>
    /// Stops the frame hitches in Auto-Battle: Debug.LogFormat (Framework/Tools/Lua_Debug.lua) builds a debug.traceback and
    /// writes it to Player.log, and the client forces this for plain info lines on a hot path: [RecoveryAOI] on every AOI
    /// message (LC_Scene_Sync) and Lua_CFG_ItemConfig.GetIconPath for item 0 on data changes. Measured in 40 s of Auto-Battle,
    /// same kills: 54 fps, worst frame 59-77 ms, 40 frames over 33 ms with them; 60 fps, 23-30 ms, none without. Lower
    /// graphics settings do not help, since this is all CPU. Warnings and errors still go through.
    /// The method is replaced through the table, as in LuaPatches.PatchCountdowns.
    /// </summary>
    internal static class LuaLogs
    {
        public delegate void LogFormatMethod(object type, object force, object text, object a, object b, object c, object d, object e, object f, object g, object h, object i);

        private static Obf_R.Obf_Ef _original;
        private static long _infoType = long.MinValue;
        // Kept alive here: Lua holds the pushed delegate only weakly through xLua's object pool.
        private static readonly LogFormatMethod Wrapper = LogFormat;

        internal static void Install(Obf_R.Obf_ff global)
        {
            var debug = global.Obf_RuA<Obf_R.Obf_ff>("Debug");
            _original = debug?.Obf_RuA<Obf_R.Obf_Ef>("LogFormat");
            _infoType = LuaPatches.ToLong(global.Obf_LuA<object>("enum_LogType.Log"));
            bool wrap = _original != null && _infoType != long.MinValue;
            if (wrap) debug.Obf_luA<string, LogFormatMethod>("LogFormat", Wrapper);
            Debug.Log("[Ro3Emu.LuaPatches] Lua info logs " + (wrap ? "off" : "left on (method not found)"));
        }

        private static void LogFormat(object type, object force, object text, object a, object b, object c, object d, object e, object f, object g, object h, object i)
        {
            if (LuaPatches.ToLong(type) == _infoType) return;
            _original?.Obf_HuA(type, force, text, a, b, c, d, e, f, g, h, i);
        }
    }
}

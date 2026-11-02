using System;
using UnityEngine;

namespace Ro3Emu.ClientHooks
{
    /// <summary>
    /// The emote widget's "table index is nil" (UI_Sys_Main_ActionWidgetBaseCtrl): closing it clears m_iOpenIndex, and a 5006
    /// that arrives before the next open reaches _updateEmojiData, then _generateEmojiData, SortList and
    /// _GetEmojiCFGByType(nil), which writes m_kActionList[nil]. Opening it builds the list again, so with no tab open the
    /// call has nothing to do. The method is replaced through the class table, as in LuaPatches.PatchCountdowns.
    /// </summary>
    internal static class EmoteWidget
    {
        private static Obf_R.Obf_Ef _original;
        // Kept alive here: Lua holds the pushed delegate only weakly through xLua's object pool.
        private static readonly LuaPatches.SelfMethod Wrapper = GenerateEmojiData;

        internal static void Install(Obf_R.Obf_ff widget)
        {
            _original = widget?.Obf_RuA<Obf_R.Obf_Ef>("_generateEmojiData");
            if (_original != null) widget.Obf_luA<string, LuaPatches.SelfMethod>("_generateEmojiData", Wrapper);
            Debug.Log("[Ro3Emu.LuaPatches] emote widget " + (_original != null ? "wrapped" : "method not found"));
        }

        private static void GenerateEmojiData(Obf_R.Obf_ff self)
        {
            try
            {
                if (self.Obf_RuA<object>("m_iOpenIndex") == null) return;
            }
            catch (Exception e) { Debug.LogWarning("[Ro3Emu.LuaPatches] emote widget: " + e.GetType().Name + ": " + e.Message); }
            _original?.Obf_HuA(self);
        }
    }
}

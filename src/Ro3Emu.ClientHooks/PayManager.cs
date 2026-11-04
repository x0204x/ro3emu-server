using UnityEngine;

namespace Ro3Emu.ClientHooks
{
    /// <summary>
    /// The pay manager's first use overflows the Lua stack, so a buy button that goes through it sends no 2001. Lua_SDK_Factory
    /// keeps a manager only after its Init returns. Lua_SDK_PayMgr.Init asks MetaSDKUtil.ReqSkuInfo for the store's prices, and
    /// without the store's SDK ("sdk proxy is not init") that answers at once through GameLogic.OnSkuInfoResult, which calls
    /// GetPayMgr again and builds another manager. Init does nothing else and nothing on this server reads store prices, so it
    /// is emptied through the class table, as in LuaPatches.PatchCountdowns.
    /// </summary>
    internal static class PayManager
    {
        // Kept alive here: Lua holds the pushed delegate only weakly through xLua's object pool.
        private static readonly LuaPatches.SelfMethod EmptyInit = self => { };

        internal static void Install(Obf_R.Obf_ff manager)
        {
            bool found = manager?.Obf_RuA<Obf_R.Obf_Ef>("Init") != null;
            if (found) manager.Obf_luA<string, LuaPatches.SelfMethod>("Init", EmptyInit);
            Debug.Log("[Ro3Emu.LuaPatches] pay manager Init " + (found ? "emptied" : "not found"));
        }
    }
}

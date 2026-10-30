using System;
using UnityEngine;

namespace Ro3Emu.ClientHooks
{
    /// <summary>
    /// 保留原生賽季料理側欄。G 的 SeasonCuisineWidget._onClickClose 只呼叫父控制器
    /// ShowSeasonCuisine(false)，但原生 ShowSeasonCuisine 沒有清 m_bSeasonCuisineVisible，
    /// 造成關閉後再按入口會先空按一次。只同步成功的原生顯示狀態，不改 Function3614 的解鎖門檻。
    /// </summary>
    public static class SeasonCuisine
    {
        public delegate void ShowMethod(Obf_R.Obf_ff self, bool visible);

        // xLua 只弱引用 delegate，須保留至目前 LuaEnv 結束。
        private static readonly ShowMethod ShowWrapper = Show;
        private static Obf_R.Obf_Ef _show;

        internal static void Install(LA_LuaManager manager, Obf_R.Obf_ff global)
        {
            _show = null;
            try
            {
                var controller = LuaPatches.Module(manager, global, "ui_sys_roleattributeswidgetctrl");
                var original = controller?.Obf_RuA<Obf_R.Obf_Ef>("ShowSeasonCuisine");
                if (original == null)
                {
                    Debug.LogWarning("[Ro3Emu.SeasonCuisine] native ShowSeasonCuisine not found; unchanged");
                    return;
                }
                _show = original;
                controller.Obf_luA<string, ShowMethod>("ShowSeasonCuisine", ShowWrapper);
                Debug.Log("[Ro3Emu.SeasonCuisine] native panel visibility state synchronized");
            }
            catch (Exception error)
            {
                // 這是每個 LuaEnv 一次的可選修正，不阻止其餘原生功能安裝。
                Debug.LogWarning("[Ro3Emu.SeasonCuisine] install failed: " + error.GetType().Name + ": " + error.Message);
            }
        }

        private static void Show(Obf_R.Obf_ff self, bool visible)
        {
            var original = _show;
            if (original == null) throw new InvalidOperationException("Native SeasonCuisine binding is unavailable.");
            SeasonCuisineVisibility.Apply(visible,
                show => { _ = original.Obf_HuA(self, show); },
                show => self.Obf_luA<string, object>("m_bSeasonCuisineVisible", show));
        }
    }
}

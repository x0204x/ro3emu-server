using System;

namespace Ro3Emu.ClientHooks
{
    /// <summary>原生側欄成功顯示／隱藏後才更新控制器旗標；原生呼叫失敗不偽造成功狀態。</summary>
    internal static class SeasonCuisineVisibility
    {
        internal static void Apply(bool visible, Action<bool> nativeShow, Action<bool> setVisible)
        {
            nativeShow(visible);
            setVisible(visible);
        }
    }
}

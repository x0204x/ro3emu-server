using System;
using System.Text;
using HappyEngine.Base;
using UnityEngine;

namespace Ro3Emu.ClientHooks
{
    /// <summary>
    /// The function menu's "Feedback" entry (enum_FunctionType.Feedback = 41, FunctionConfig bar 2) does nothing in this
    /// client: UI_Sys_MainFunctionMenuWidgetBaseCtrl._onClickFunction calls LC_FunctionHelper.OpenFunctionPanelDefault(41),
    /// which has neither a FunctionSpecialFunc nor a FunctionOpenPanelInfo entry for 41 and only logs "missing function
    /// &amp; panel mapping". This hook gives it one: FunctionSpecialFunc[41] becomes a C# delegate that opens the client's
    /// own generic text panel (UI_Com_InputContentPanel, the one the stall uses for its share text) titled "Feedback"
    /// (UIWords 90505). Its confirm callback sends the text to the server as game message <see cref="MsgId"/> (UTF-8);
    /// the server stores the report (FeedbackService) and answers on the marquee. No Lua source runs: the panel data is a
    /// table made by the client's own table.pack, its fields set through the LuaTable API.
    /// </summary>
    public static class FeedbackButton
    {
        /// <summary>Must equal the server's FeedbackService.SubmitMsg.</summary>
        public const uint MsgId = PrivateMessageIds.Feedback;

        private const long FeedbackFunction = 41;
        private const long TitleWord = 90505; // UIWords "Feedback"
        private const long MaxLength = 500;
        private const string Placeholder = "What went wrong? What did you do, what happened, what did you expect?";

        public delegate void NoArgs();
        /// <summary>sureClickCallBtn(ctrl, text): ctrl is the data's ctrl field (unset here, so nil).</summary>
        public delegate void Confirm(object ctrl, object text);

        // Kept alive here: Lua holds the pushed delegates only weakly through xLua's object pool.
        private static readonly NoArgs OpenWrapper = Open;
        private static readonly Confirm ConfirmWrapper = Send;

        private static Obf_R.Obf_ff _global;

        /// <summary>From LuaPatches.Tick, once per LuaEnv.</summary>
        internal static void Install(Obf_R.Obf_ff global, Obf_R.Obf_ff functionHelper)
        {
            _global = global;
            var special = functionHelper?.Obf_RuA<Obf_R.Obf_ff>("FunctionSpecialFunc");
            if (special == null)
            {
                Debug.LogWarning("[Ro3Emu.Feedback] LC_FunctionHelper.FunctionSpecialFunc not found; the Feedback button stays inert");
                return;
            }
            long id = global.Obf_LuA<object>("enum_FunctionType.Feedback") is long l ? l : FeedbackFunction;
            special.Obf_luA<long, NoArgs>(id, OpenWrapper);
            Debug.Log("[Ro3Emu.Feedback] function " + id + " opens the feedback text panel");
        }

        private static void Open()
        {
            try
            {
                var global = _global;
                var made = global?.Obf_LuA<Obf_R.Obf_Ef>("table.pack")?.Obf_HuA();
                if (!(made != null && made.Length > 0 && made[0] is Obf_R.Obf_ff data))
                {
                    Debug.LogWarning("[Ro3Emu.Feedback] could not make the panel data table");
                    return;
                }
                data.Obf_luA<string, object>("titleID", TitleWord);
                data.Obf_luA<string, object>("maxLen", MaxLength);
                data.Obf_luA<string, object>("defaultContentID", Placeholder);
                data.Obf_luA<string, Confirm>("sureClickCallBtn", ConfirmWrapper);
                var ui = global.Obf_RuA<Obf_R.Obf_ff>("Lua_UIManagerNewIns");
                var open = ui?.Obf_RuA<Obf_R.Obf_Ef>("OpenPanel");
                object layer = global.Obf_LuA<object>("UILayers.POPUP"), panel = global.Obf_LuA<object>("UINames.UI_Com_InputContentPanel");
                if (open == null || layer == null || panel == null)
                {
                    Debug.LogWarning("[Ro3Emu.Feedback] UI manager or panel not found (open " + (open != null) + ", layer " + layer + ", panel " + panel + ")");
                    return;
                }
                // Lua_UIManagerNew:OpenPanel(layer, name, nil, nil, nil, initData), as UI_Sys_Stall_SellerWidgetCtrl._onShareSell.
                open.Obf_HuA(ui, layer, panel, null, null, null, data);
            }
            catch (Exception e)
            {
                Debug.LogError("[Ro3Emu.Feedback] open failed: " + e);
            }
        }

        private static void Send(object ctrl, object text)
        {
            try
            {
                var report = (text as string ?? text?.ToString() ?? "").Trim();
                if (report.Length == 0) return;
                SingletonMonoBehaviour<NW_CBBNetworkManager>.Instance.Obf_jBd(MsgId, Encoding.UTF8.GetBytes(report));
                Debug.Log("[Ro3Emu.Feedback] report sent (" + report.Length + " characters)");
            }
            catch (Exception e)
            {
                Debug.LogError("[Ro3Emu.Feedback] send failed: " + e);
            }
        }
    }
}

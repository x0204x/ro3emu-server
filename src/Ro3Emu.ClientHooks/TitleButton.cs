using System;
using System.Linq;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace Ro3Emu.ClientHooks
{
    /// <summary>
    /// A "Title" button in the character window: the client has the title rank widget (UI_Sys_Role_TitleWidget, the
    /// upgrade button sends 43102) but no code ever hosts it (function 14 opened only at base level 9999, and the PC
    /// character window has no tabs). The Astral Level button (PeakLevel_RTransform) is cloned one step to its right; a
    /// click builds the widget into the window's content the way UI_Sys_RolePanelCtrl.SelectTab builds its tabs
    /// (Lua_UIManagerNewIns:CreateWidget(UINames.UI_Sys_Role_TitleWidget, window, View.m_kRTRANSFORM_Content:GetTransform())),
    /// hiding the attributes widget, and a second click switches back. The widget's own request lock
    /// (lc_dal_title.pbLocked, cleared only by its upgrade animation) is reset on each click, so a refused request does not
    /// lock it for the session. UI_Sys_RoleWindowCtrl.OnClose is wrapped to return the widget with the window.
    /// </summary>
    public static class TitleButton
    {
        private const string Window = "UI_Sys_RoleWindow(Clone)";
        private const string CloneName = "Ro3Emu_Title_Btn";
        private const string WidgetName = "UI_Sys_Role_TitleWidget(Clone)", AttributesName = "UI_Sys_RoleAttributesWidget(Clone)";
        private const string BackdropName = "Ro3Emu_Title_Backdrop";
        private static Obf_R.Obf_ff _global, _dal, _window, _title;
        private static Obf_R.Obf_Ef _close;
        private static bool _titleShown;
        private static readonly LuaPatches.SelfMethod CloseWrapper = OnClose;

        internal static void Install(LA_LuaManager manager, Obf_R.Obf_ff global)
        {
            _global = global;
            _window = _title = null;
            _dal = LuaPatches.Module(manager, global, "lc_dal_title");
            var window = LuaPatches.Module(manager, global, "ui_sys_rolewindowctrl");
            _close = window?.Obf_RuA<Obf_R.Obf_Ef>("OnClose");
            if (_close != null) window.Obf_luA<string, LuaPatches.SelfMethod>("OnClose", CloseWrapper);
            Debug.Log("[Ro3Emu.TitleButton] " + (_close != null ? "character window close wrapped" : "UI_Sys_RoleWindowCtrl not found")
                + (_dal != null ? "" : "; lc_dal_title not found"));
        }

        public static void Tick()
        {
            var window = GameObject.Find(Window);
            if (window == null) return;
            var all = window.GetComponentsInChildren<Transform>(true);
            Backdrop(all);
            if (all.Any(t => t.name == CloneName)) return;
            var peak = all.FirstOrDefault(t => t.name == "PeakLevel_RTransform" && t.GetComponent<RectTransform>() != null);
            if (peak == null) return;
            var button = UnityEngine.Object.Instantiate(peak.gameObject, peak.parent, false);
            button.name = CloneName;
            var size = ((RectTransform)peak).rect.width * peak.lossyScale.x;
            button.transform.position = peak.position + new Vector3(size * 1.1f, 0, 0);
            button.SetActive(true);
            // Drop every handler the clone carries (Lua binds the original by its view slot, not this copy).
            foreach (var b in button.GetComponentsInChildren<Button>(true)) b.onClick.RemoveAllListeners();
            foreach (var behaviour in button.GetComponentsInChildren<MonoBehaviour>(true))
            {
                if (behaviour is IPointerClickHandler && !(behaviour is Button) && !(behaviour is Graphic) || behaviour.GetType().Name == "LangTransUtils_TMP")
                    behaviour.enabled = false;
            }
            // The clone's root is a layout holder: the click lands on its PeakLevel_Btn, whose Button (listeners removed)
            // would take the event, so the handler sits on each button object.
            var targets = button.GetComponentsInChildren<Button>(true).Select(b => b.gameObject).Distinct().ToList();
            if (targets.Count == 0) targets.Add(button);
            foreach (var target in targets) target.AddComponent<Click>();
            foreach (var label in button.GetComponentsInChildren<TMP_Text>(true)) label.text = "Title";
            Debug.Log("[Ro3Emu.TitleButton] added");
        }

        private static void OnClose(Obf_R.Obf_ff self)
        {
            try
            {
                if (_title != null && ReferenceEquals(self, _window))
                {
                    var ui = _global?.Obf_RuA<Obf_R.Obf_ff>("Lua_UIManagerNewIns");
                    ui?.Obf_RuA<Obf_R.Obf_Ef>("ReturnWidget")?.Obf_HuA(ui, _title);
                }
            }
            catch (Exception e) { Debug.LogWarning("[Ro3Emu.TitleButton] return failed: " + e.Message); }
            _title = null;
            _window = null;
            _titleShown = false;
            _close?.Obf_HuA(self);
        }

        /// <summary>The open character window's controller: the window in Lua_UIManagerNewIns:GetAllWindow() with a
        /// kRoleAttrWidget.</summary>
        private static Obf_R.Obf_ff FindWindow()
        {
            var ui = _global?.Obf_RuA<Obf_R.Obf_ff>("Lua_UIManagerNewIns");
            var windows = ui?.Obf_RuA<Obf_R.Obf_Ef>("GetAllWindow")?.Obf_HuA(ui)?.FirstOrDefault() as Obf_R.Obf_ff;
            var next = _global?.Obf_RuA<Obf_R.Obf_Ef>("next");
            if (windows == null || next == null) return null;
            object key = null;
            for (int guard = 0; guard < 256; guard++)
            {
                var pair = next.Obf_HuA(windows, key);
                if (pair == null || pair.Length == 0 || pair[0] == null) return null;
                key = pair[0];
                if (pair.Length > 1 && pair[1] is Obf_R.Obf_ff ctrl && ctrl.Obf_RuA<object>("kRoleAttrWidget") != null) return ctrl;
            }
            return null;
        }

        private static void Toggle()
        {
            try
            {
                _dal?.Obf_luA<string, object>("pbLocked", false);
                var window = FindWindow();
                if (window == null) { Debug.LogWarning("[Ro3Emu.TitleButton] character window controller not found"); return; }
                if (!ReferenceEquals(window, _window)) { _window = window; _title = null; _titleShown = false; }
                var attributes = window.Obf_RuA<object>("kRoleAttrWidget");
                if (_title == null)
                {
                    var ui = _global.Obf_RuA<Obf_R.Obf_ff>("Lua_UIManagerNewIns");
                    var create = ui?.Obf_RuA<Obf_R.Obf_Ef>("CreateWidget");
                    object name = _global.Obf_LuA<object>("UINames.UI_Sys_Role_TitleWidget");
                    var content = window.Obf_LuA<object>("View.m_kRTRANSFORM_Content");
                    object parent = content is Obf_R.Obf_ff wrapper
                        ? wrapper.Obf_RuA<Obf_R.Obf_Ef>("GetTransform")?.Obf_HuA(wrapper)?.FirstOrDefault() : content;
                    if (create == null || name == null || parent == null)
                    {
                        Debug.LogWarning("[Ro3Emu.TitleButton] cannot build the widget (create " + (create != null) + ", name " + name + ", parent " + parent + ")");
                        return;
                    }
                    _title = create.Obf_HuA(ui, name, window, parent)?.FirstOrDefault() as Obf_R.Obf_ff;
                    _titleShown = _title != null;
                    if (_titleShown) SetActive(attributes, false);
                    Debug.Log("[Ro3Emu.TitleButton] title widget " + (_title != null ? "built" : "not built"));
                    return;
                }
                _titleShown = !_titleShown;
                SetActive(_title, _titleShown);
                SetActive(attributes, !_titleShown);
            }
            catch (Exception e) { Debug.LogError("[Ro3Emu.TitleButton] " + e); }
        }

        /// <summary>
        /// The widget was drawn for the mobile role panel's own background: here it would lie see-through over the
        /// attributes, so it gets a parchment backdrop (first child, the widget's whole rect) and the attributes are hidden.
        /// </summary>
        private static void Backdrop(Transform[] all)
        {
            var widget = all.FirstOrDefault(t => t.name == WidgetName);
            var attributes = all.FirstOrDefault(t => t.name == AttributesName);
            bool shown = widget != null && widget.gameObject.activeInHierarchy;
            if (attributes != null && attributes.gameObject.activeSelf == shown) attributes.gameObject.SetActive(!shown);
            if (widget == null || widget.Find(BackdropName) != null) return;
            var backdrop = new GameObject(BackdropName, typeof(RectTransform), typeof(Image));
            var rect = (RectTransform)backdrop.transform;
            rect.SetParent(widget, false);
            rect.SetAsFirstSibling();
            rect.anchorMin = Vector2.zero;
            rect.anchorMax = Vector2.one;
            rect.offsetMin = new Vector2(-12, -12);
            rect.offsetMax = new Vector2(12, 12);
            var image = backdrop.GetComponent<Image>();
            image.color = new Color(0.93f, 0.89f, 0.80f, 0.98f);
            image.raycastTarget = false;
        }

        private static void SetActive(object widget, bool active)
        {
            if (widget is Obf_R.Obf_ff table) table.Obf_RuA<Obf_R.Obf_Ef>("SetWidgetActive")?.Obf_HuA(table, active);
        }

        private sealed class Click : MonoBehaviour, IPointerClickHandler
        {
            public void OnPointerClick(PointerEventData eventData) => Toggle();
        }
    }
}

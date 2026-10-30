using System;
using System.Linq;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace Ro3Emu.ClientHooks
{
    /// <summary>
    /// 客製 PC 頭銜入口。保留 G 的 TitleWidget／43102／升級動畫，但以獨立置中視窗承載，
    /// 不把行動版 RolePanel 的大尺寸 widget 塞進 PC RoleWindow.Content。關閉仍交原生 ReturnWidget 回收。
    /// </summary>
    public static class TitleButton
    {
        private const string Window = "UI_Sys_RoleWindow(Clone)";
        private const string CloneName = "Ro3Emu_Title_Btn";
        private const string WidgetName = "UI_Sys_Role_TitleWidget(Clone)", AttributesName = "UI_Sys_RoleAttributesWidget(Clone)";
        private const string ModalName = "Ro3Emu_Title_Modal";
        private static Obf_R.Obf_ff _global, _window, _title;
        private static Obf_R.Obf_Ef _close, _dalInit;
        private static ClientLabelText _labels;
        private static TMP_Text[] _buttonLabels;
        private static TMP_Text _closeLabel;
        private static GameObject _modal, _attributes, _ownerWindow;
        private static RectTransform _canvas, _panel, _holder, _native;
        private static CanvasGroup _nativeVisibility;
        private static Vector2 _nativeSize, _nativeCenter, _originalAnchorMin, _originalAnchorMax, _originalPivot, _originalSize;
        private static Vector3 _originalPosition, _originalScale;
        private static Quaternion _originalRotation;
        private static bool _attributesWereActive, _nativeLayoutSaved;
        private static TMP_FontAsset _font;
        private static readonly LuaPatches.SelfMethod CloseWrapper = OnClose;
        private static readonly LuaPatches.SelfMethod DalInitWrapper = OnDalInit;

        internal static void Install(LA_LuaManager manager, Obf_R.Obf_ff global)
        {
            CloseModal();
            _global = global;
            _labels = ClientLabelText.Installed();
            var dal = LuaPatches.Module(manager, global, "lc_dal_title");
            _dalInit = dal?.Obf_RuA<Obf_R.Obf_Ef>("_onInitMsgHandler");
            if (_dalInit != null) dal.Obf_luA<string, LuaPatches.SelfMethod>("_onInitMsgHandler", DalInitWrapper);
            var window = LuaPatches.Module(manager, global, "ui_sys_rolewindowctrl");
            _close = window?.Obf_RuA<Obf_R.Obf_Ef>("OnClose");
            if (_close != null) window.Obf_luA<string, LuaPatches.SelfMethod>("OnClose", CloseWrapper);
            Debug.Log("[Ro3Emu.TitleButton] " + (_close != null ? "character window close wrapped" : "UI_Sys_RoleWindowCtrl not found"));
        }

        public static void Tick()
        {
            var window = GameObject.Find(Window);
            if (window == null) { CloseModal(); return; }
            if (_modal != null && window != _ownerWindow) CloseModal();
            if (_modal != null) UpdateModal();
            RefreshLabels();
            var all = window.GetComponentsInChildren<Transform>(true);
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
            _buttonLabels = button.GetComponentsInChildren<TMP_Text>(true);
            foreach (var label in _buttonLabels) _font = label.font;
            RefreshLabels();
            Debug.Log("[Ro3Emu.TitleButton] added");
        }

        private static void OnClose(Obf_R.Obf_ff self)
        {
            if (ReferenceEquals(self, _window)) CloseModal();
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
                if (_modal != null) { CloseModal(); return; }
                var window = FindWindow();
                if (window == null) { Debug.LogWarning("[Ro3Emu.TitleButton] character window controller not found"); return; }
                OpenModal(window);
            }
            catch (Exception e) { CloseModal(); Debug.LogError("[Ro3Emu.TitleButton] " + e); }
        }

        private static void OpenModal(Obf_R.Obf_ff window)
        {
            if (!EnsureCurrentDal()) { Debug.LogWarning("[Ro3Emu.TitleButton] TitleLevel reply handler unavailable"); return; }
            var gameWindow = GameObject.Find(Window);
            var canvas = gameWindow?.GetComponentInParent<Canvas>()?.rootCanvas;
            var ui = _global?.Obf_RuA<Obf_R.Obf_ff>("Lua_UIManagerNewIns");
            var create = ui?.Obf_RuA<Obf_R.Obf_Ef>("CreateWidget");
            object name = _global?.Obf_LuA<object>("UINames.UI_Sys_Role_TitleWidget");
            if (canvas == null || create == null || name == null) return;
            _window = window;
            _ownerWindow = gameWindow;
            _canvas = canvas.transform as RectTransform;
            if (_canvas == null) { CloseModal(); return; }
            _modal = new GameObject(ModalName, typeof(RectTransform), typeof(Image));
            var overlay = (RectTransform)_modal.transform;
            overlay.SetParent(_canvas, false);
            overlay.anchorMin = Vector2.zero; overlay.anchorMax = Vector2.one;
            overlay.offsetMin = overlay.offsetMax = Vector2.zero;
            _modal.GetComponent<Image>().color = new Color(0, 0, 0, 0.7f);
            _panel = NewRect("Panel", overlay);
            _panel.gameObject.AddComponent<Image>().color = new Color(0.93f, 0.89f, 0.80f, 1);
            _holder = NewRect("NativeWidget", _panel);
            _holder.sizeDelta = _canvas.rect.size;
            _nativeVisibility = _holder.gameObject.AddComponent<CanvasGroup>();
            _nativeVisibility.alpha = 0;
            AddCloseButton();
            _title = create.Obf_HuA(ui, name, window, _holder)?.FirstOrDefault() as Obf_R.Obf_ff;
            if (_title == null) { CloseModal(); return; }
            _attributes = gameWindow.GetComponentsInChildren<Transform>(true).FirstOrDefault(t => t.name == AttributesName)?.gameObject;
            if (_attributes != null) { _attributesWereActive = _attributes.activeSelf; _attributes.SetActive(false); }
            UpdateModal();
        }

        private static RectTransform NewRect(string name, Transform parent)
        {
            var rect = (RectTransform)new GameObject(name, typeof(RectTransform)).transform;
            rect.SetParent(parent, false);
            rect.anchorMin = rect.anchorMax = rect.pivot = new Vector2(0.5f, 0.5f);
            return rect;
        }

        private static void AddCloseButton()
        {
            var rect = NewRect("Close", _panel);
            rect.anchorMin = rect.anchorMax = new Vector2(1, 1);
            rect.pivot = new Vector2(1, 1);
            rect.anchoredPosition = new Vector2(-TitleModalGeometry.Padding, -4);
            rect.sizeDelta = new Vector2(88, TitleModalGeometry.Header - 8);
            rect.gameObject.AddComponent<Image>().color = new Color(0.28f, 0.23f, 0.17f, 1);
            rect.gameObject.AddComponent<Button>().onClick.AddListener(CloseModal);
            var textRect = NewRect("Label", rect);
            textRect.anchorMin = Vector2.zero; textRect.anchorMax = Vector2.one;
            textRect.offsetMin = textRect.offsetMax = Vector2.zero;
            var text = textRect.gameObject.AddComponent<TextMeshProUGUI>();
            _closeLabel = text;
            text.font = _font; text.text = Label("Close"); text.fontSize = 20;
            text.alignment = TextAlignmentOptions.Center; text.raycastTarget = false;
        }

        private static void UpdateModal()
        {
            // CreateWidget 可延後載入；只對實際生成的原生 Content 量測，不把空父節點當完成。
            if (_native == null)
            {
                _native = _holder.GetComponentsInChildren<RectTransform>(true).FirstOrDefault(t => t.name == WidgetName);
                if (_native == null) return;
                var content = (_native.Find("Root/Content") ?? _native.Find("_Root/Content") ?? _native.Find("Content")) as RectTransform;
                if (content == null) { Debug.LogWarning("[Ro3Emu.TitleButton] native Content not found"); CloseModal(); return; }
                SaveNativeLayout();
                var rootSize = _native.rect.size;
                _native.anchorMin = _native.anchorMax = _native.pivot = new Vector2(0.5f, 0.5f);
                _native.sizeDelta = rootSize;
                var corners = new Vector3[4]; content.GetWorldCorners(corners);
                var min = _native.InverseTransformPoint(corners[0]);
                var max = _native.InverseTransformPoint(corners[2]);
                _nativeCenter = (min + max) * 0.5f;
                _nativeSize = new Vector2(max.x - min.x, max.y - min.y);
            }
            if (!TitleModalGeometry.TryFit(_canvas.rect.width, _canvas.rect.height, _nativeSize.x, _nativeSize.y, out var fit))
            { CloseModal(); return; }
            _panel.sizeDelta = new Vector2(fit.Width, fit.Height);
            _native.localScale = new Vector3(fit.Scale, fit.Scale, fit.Scale);
            _native.anchoredPosition = -_nativeCenter * fit.Scale - new Vector2(0, TitleModalGeometry.Header * 0.5f);
            _nativeVisibility.alpha = 1;
        }

        private static void SaveNativeLayout()
        {
            _originalAnchorMin = _native.anchorMin; _originalAnchorMax = _native.anchorMax;
            _originalPivot = _native.pivot; _originalSize = _native.sizeDelta;
            _originalPosition = _native.anchoredPosition3D;
            _originalScale = _native.localScale; _originalRotation = _native.localRotation;
            _nativeLayoutSaved = true;
        }

        private static void RestoreNativeLayout()
        {
            if (!_nativeLayoutSaved || _native == null) return;
            _native.anchorMin = _originalAnchorMin; _native.anchorMax = _originalAnchorMax;
            _native.pivot = _originalPivot; _native.sizeDelta = _originalSize;
            _native.anchoredPosition3D = _originalPosition;
            _native.localScale = _originalScale; _native.localRotation = _originalRotation;
        }

        private static void CloseModal()
        {
            // 先取走 ownership；ReturnWidget／原生 OnClose 重入也只回收一次。
            var title = _title; _title = null;
            var modal = _modal; _modal = null;
            _window = null;
            try
            {
                // 不讓自有 modal 的 Destroy 連帶銷毀原生物件池可能保留的 widget。
                RestoreNativeLayout();
                if (_native != null && _canvas != null) _native.SetParent(_canvas, false);
                if (title != null)
                {
                    var ui = _global?.Obf_RuA<Obf_R.Obf_ff>("Lua_UIManagerNewIns");
                    ui?.Obf_RuA<Obf_R.Obf_Ef>("ReturnWidget")?.Obf_HuA(ui, title);
                }
            }
            catch (Exception e) { Debug.LogWarning("[Ro3Emu.TitleButton] return failed: " + e.Message); }
            finally
            {
                if (_attributes != null) _attributes.SetActive(_attributesWereActive);
                _attributes = _ownerWindow = null; _canvas = _panel = _holder = _native = null; _nativeVisibility = null;
                _nativeLayoutSaved = false;
                _closeLabel = null;
                if (modal != null) { modal.SetActive(false); UnityEngine.Object.Destroy(modal); }
            }
        }

        private static string Label(string source) => _labels?.Map(source) ?? source;

        private static void RefreshLabels()
        {
            string title = Label("Title");
            if (_buttonLabels != null)
                foreach (var text in _buttonLabels) if (text != null && text.text != title) text.text = title;
            string close = Label("Close");
            if (_closeLabel != null && _closeLabel.text != close) _closeLabel.text = close;
        }

        private static void OnDalInit(Obf_R.Obf_ff self)
        {
            _dalInit?.Obf_HuA(self);
            BindTitleReply(self);
        }

        private static bool EnsureCurrentDal()
        {
            // G LC_AssetManager.GetDAL 使用正式 instance；也涵蓋安裝 hook 前已初始化的 DAL。
            var location = _global?.Obf_RuA<Obf_R.Obf_ff>("ServerLocation");
            var assets = location?.Obf_RuA<Obf_R.Obf_Ef>("GetServer")?.Obf_HuA(location, "LC_AssetManager")?.FirstOrDefault() as Obf_R.Obf_ff;
            var dal = assets?.Obf_RuA<Obf_R.Obf_Ef>("GetDAL")?.Obf_HuA(assets, "lc_dal_title")?.FirstOrDefault() as Obf_R.Obf_ff;
            return BindTitleReply(dal);
        }

        private static bool BindTitleReply(Obf_R.Obf_ff dal)
        {
            if (dal == null) return false;
            var register = dal.Obf_RuA<Obf_R.Obf_Ef>("RegisterMsgHandler");
            if (register == null) return false;
            var handlers = dal.Obf_RuA<Obf_R.Obf_ff>("msgHandlerList");
            var current = handlers?.Obf_RuA<Obf_R.Obf_ff>(TitleReplyBinding.Dataset);
            // G 的 UpgradeUIError 是唯一清鎖動作；只有原生 DecodeError 分派失敗回覆時才執行。
            return TitleReplyBinding.Ensure(current?.Obf_RuA<object>("successHandler"), current?.Obf_RuA<object>("errorHandler"),
                dal.Obf_RuA<object>("UpgradeUIError"), !(current?.Obf_RuA<object>("isShowError") is bool show) || show,
                (dataset, success, error, showErrors) => register.Obf_HuA(dal, dataset, success, error, showErrors));
        }

        private sealed class Click : MonoBehaviour, IPointerClickHandler
        {
            public void OnPointerClick(PointerEventData eventData) => Toggle();
        }
    }
}

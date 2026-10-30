using System;
using System.Reflection;
using TMPro;
using UnityEngine;

namespace Ro3Emu.ClientHooks
{
    /// <summary>相容性恢復既有登入版本欄；不建立 overlay，也不開啟原本隱藏的 VersionNum 偵錯欄。</summary>
    public static class NativeVersionLabel
    {
        private static readonly string[] Roots = { "UI_Sys_LoginWidget_PC(Clone)", "UI_Sys_LoginWidget(Clone)" };
        private static TMP_Text _label;
        private static GameObject _owner;
        private static Func<string> _resourceVersion;
        private static int _ticks;
        private static string _lastFailure;

        /// <summary>由既有 UI pump 呼叫；只讀取目前原生版本與選定登入元件。</summary>
        public static void Tick()
        {
            if (++_ticks % 3 != 0) return;
            try
            {
                if (_owner == null || !_owner.activeInHierarchy) { _owner = null; _label = null; }
                if (_label == null) FindLabel();
                if (_label == null || _owner == null || !_owner.activeInHierarchy) return;
                if (_resourceVersion == null) FindResourceGetter();
                string resource = _resourceVersion == null ? null : _resourceVersion();
                string text = NativeVersionText.Compose(Application.version, resource, _label.text);
                if (string.IsNullOrEmpty(resource)) return; // 缺原生來源時保留原樣，不能造版本。
                if (_label.text != text) _label.text = text;
                if (!_label.enabled) _label.enabled = true;
                if (!_label.gameObject.activeSelf) _label.gameObject.SetActive(true);
                Transform parent = _label.transform.parent;
                if (parent != null && parent.name == "Pan_SerialNumber" && !parent.gameObject.activeSelf) parent.gameObject.SetActive(true);
            }
            catch (Exception e)
            {
                string failure = e.GetType().Name;
                if (failure == _lastFailure) return;
                _lastFailure = failure;
                Debug.LogWarning("[Ro3Emu.NativeVersionLabel] " + failure);
            }
        }

        private static void FindLabel()
        {
            foreach (string name in Roots)
            {
                GameObject root = GameObject.Find(name);
                if (root == null) continue;
                foreach (TMP_Text label in root.GetComponentsInChildren<TMP_Text>(true))
                    if (label.name == "SerialNumber_TTxt") { _owner = root; _label = label; return; }
            }
        }

        private static void FindResourceGetter()
        {
            foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                if (assembly.GetName().Name != "HappyEngine.Resource") continue;
                MethodInfo method = assembly.GetType("HappyEngine.Runtime.NewRuntimeSystem.GameVersionProxy")
                    ?.GetMethod("LocalVersion", BindingFlags.Public | BindingFlags.Static, null, Type.EmptyTypes, null);
                if (method != null && method.ReturnType == typeof(string))
                    _resourceVersion = (Func<string>)Delegate.CreateDelegate(typeof(Func<string>), method);
                return;
            }
        }

    }
}

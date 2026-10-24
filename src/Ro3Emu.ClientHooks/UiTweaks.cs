using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

namespace Ro3Emu.ClientHooks
{
    /// <summary>
    /// Small layout changes to the client's UI that the signed Lua and bundles cannot carry, applied from the per-frame
    /// pump while a view is open.
    /// <list type="bullet">
    /// <item>Mode selection ("Select Mode", first world entry and Settings → PC Mode): the Mobile column (in development
    /// in this build) is removed and Classic PC / Modern PC are centred, keeping the original spacing.</item>
    /// </list>
    /// </summary>
    public static class UiTweaks
    {
        private static readonly string[] ModeWidgets = { "UI_Sys_SetModeWidget_PC(Clone)", "UI_Sys_SetModeWidget(Clone)" };
        private const string ModeList = "Root/Content_RTransform/Pop_Center/Root/Pan_Content/Pan_BgMask/Content_RTransform";
        private const string MobileColumn = "PCMobile_RTransform";

        private static readonly HashSet<int> Done = new HashSet<int>();
        private static int _frame;

        /// <summary>Called every frame; looks for the views every 10th frame (GameObject.Find only sees active objects).</summary>
        public static void Tick()
        {
            Diagnostics.AutoDump();
            TextFit.Tick();
            if (++_frame % 10 != 0) return;
            try
            {
                foreach (var name in ModeWidgets)
                {
                    var widget = GameObject.Find(name);
                    if (widget != null && Done.Add(widget.GetInstanceID())) HideMobileMode(widget.transform);
                }
                foreach (var name in StallWindows)
                {
                    var window = GameObject.Find(name);
                    if (window != null && Done.Add(window.GetInstanceID())) WidenStallName(window.transform);
                }
                if (Done.Count > 64) Done.Clear();
                QuitButton.Tick();
                LuaPatches.Tick();
                PassChallenges.Tick();
                PassButton.Tick();
                TitleButton.Tick();
            }
            catch (Exception e)
            {
                Debug.LogError("[Ro3Emu.UiTweaks] " + e);
            }
        }

        private static readonly string[] StallWindows = { "UI_Sys_StallWindow(Clone)", "UI_Sys_StallWindow_PC(Clone)" };

        /// <summary>
        /// The stall title input (UI_Sys_StallWindow m_kTINPUTFIELD_TInput) has characterLimit 10 in the prefab, sized for
        /// Chinese; it gets LuaPatches.StallNameMaxLength, the same limit the server applies.
        /// </summary>
        private static void WidenStallName(Transform window)
        {
            foreach (var input in window.GetComponentsInChildren<TMPro.TMP_InputField>(true))
                if (input.characterLimit > 0 && input.characterLimit < LuaPatches.StallNameMaxLength)
                {
                    Debug.Log("[Ro3Emu.UiTweaks] stall input " + input.name + ": limit " + input.characterLimit + " -> " + LuaPatches.StallNameMaxLength);
                    input.characterLimit = LuaPatches.StallNameMaxLength;
                }
        }

        private static void HideMobileMode(Transform widget)
        {
            var content = widget.Find(ModeList);
            if (content == null) return;
            Transform mobile = null;
            foreach (var t in content.GetComponentsInChildren<Transform>(true))
                if (t.name == MobileColumn) { mobile = t; break; }
            if (mobile == null) return;
            var row = mobile.parent;
            var columns = new List<RectTransform>();
            for (int i = 0; i < row.childCount; i++)
            {
                var c = row.GetChild(i) as RectTransform;
                if (c != null && c != mobile && c.gameObject.activeSelf) columns.Add(c);
            }
            float oldLeft = float.MaxValue, oldRight = float.MinValue;
            foreach (var c in columns) Edges(c, ref oldLeft, ref oldRight);
            Edges(mobile as RectTransform, ref oldLeft, ref oldRight);
            mobile.gameObject.SetActive(false);

            var group = row.GetComponent<HorizontalOrVerticalLayoutGroup>();
            if (group != null)
            {
                // A layout group re-flows the remaining columns; centre them in the row.
                group.childAlignment = TextAnchor.MiddleCenter;
                var rowRect = row as RectTransform;
                if (rowRect != null) LayoutRebuilder.ForceRebuildLayoutImmediate(rowRect);
            }
            else if (columns.Count > 0 && row is RectTransform rect)
            {
                // Hand-placed columns: move them together so their middle is the row's middle (spacing unchanged).
                float min = float.MaxValue, max = float.MinValue;
                foreach (var c in columns)
                {
                    float x = c.localPosition.x;
                    min = Math.Min(min, x + c.rect.xMin);
                    max = Math.Max(max, x + c.rect.xMax);
                }
                float shift = rect.rect.center.x - (min + max) / 2f;
                foreach (var c in columns) c.localPosition += new Vector3(shift, 0f, 0f);
            }
            // The close (right) and back (left) buttons sit at the edges of the column set: follow the new edges.
            float newLeft = float.MaxValue, newRight = float.MinValue;
            foreach (var c in columns) Edges(c, ref newLeft, ref newRight);
            var panel = widget.Find("Root/Content_RTransform/Pop_Center/Root/Pan_Content");
            if (panel != null && columns.Count > 0)
            {
                var close = panel.Find("Close_Btn");
                if (close != null) close.position += new Vector3(newRight - oldRight, 0f, 0f);
                var back = panel.Find("Return_Btn");
                if (back != null) back.position += new Vector3(newLeft - oldLeft, 0f, 0f);
            }
            Debug.Log("[Ro3Emu.UiTweaks] mode selection: Mobile removed, " + columns.Count + " column(s) centred" + (group != null ? " (layout group)" : ""));
        }

        private static readonly Vector3[] Corners = new Vector3[4];

        /// <summary>Widens [left, right] (world x) by the rect's corners.</summary>
        private static void Edges(RectTransform rect, ref float left, ref float right)
        {
            if (rect == null) return;
            rect.GetWorldCorners(Corners);
            left = Math.Min(left, Corners[0].x);
            right = Math.Max(right, Corners[2].x);
        }
    }
}

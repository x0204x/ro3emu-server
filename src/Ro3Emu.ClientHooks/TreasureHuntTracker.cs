using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityEngine.UI;

namespace Ro3Emu.ClientHooks
{
    /// <summary>
    /// 使用者要求的自訂右側寶圖追蹤列。重用 G 的原生 ListWidget 外觀，但不進一般任務陣列、
    /// 不給 m_kTaskInfo、不呼叫 OnRefresh；點擊只重讀 SeasonTreasureHunt 並恢復同圖 StartHunt。
    /// 這是相容介面，不宣稱 G 原生已有此追蹤列，也不接受任務、領獎或使用道具。
    /// </summary>
    public static class TreasureHuntTracker
    {
        private const string OwnerKey = "_ro3TreasureTrackerOwner";
        // 相容 provider 可明確公開此欄位；不以文字、一般任務 ID 或猜測的原生欄位判斷重複。
        internal const string CompatiblePointKey = "_ro3SeasonTreasurePointId";
        private static readonly string[] HiddenFields =
        {
            "m_kRTRANSFORM_Target", "m_kRTRANSFORM_Complete", "m_kRTRANSFORM_Fail", "m_kRTRANSFORM_Season",
            "m_kWT_Item", "m_kTTXT_ItemName", "m_kRTRANSFORM_Fx_InProgress", "m_kRTRANSFORM_Fx_Shuaxin",
        };
        private static readonly List<Entry> Entries = new List<Entry>();
        private static readonly HashSet<string> Reported = new HashSet<string>();
        private static readonly LuaPatches.SelfMethod UpdateWrapper = Updated, CloseWrapper = Closed, FrameWrapper = FrameClicked;
        private static object _env;
        private static Obf_R.Obf_ff _global;
        private static Obf_R.Obf_Ef _update, _close, _frame;
        private static ClientLabelText _labels;

        public delegate void WidgetReady(Obf_R.Obf_ff widget);

        private sealed class Entry
        {
            public object Env;
            public Obf_R.Obf_ff Parent, Widget;
            public RectTransform Content;
            public readonly TreasureHuntTrackerLease Lease = new TreasureHuntTrackerLease();
            public WidgetReady Created;
            public bool Closed, Disabled;
        }

        internal static void Install(object env, LA_LuaManager manager, Obf_R.Obf_ff global)
        {
            if (ReferenceEquals(env, _env)) return;
            foreach (var entry in Entries.ToArray())
                try { Remove(entry); }
                catch (Exception error) { Warn("reset: " + error.Message); }
            Entries.Clear();
            Reported.Clear();
            _env = env;
            _global = global;
            _labels = ClientLabelText.Installed();
            _update = _close = _frame = null;
            try
            {
                var owner = LuaPatches.Module(manager, global, "ui_sys_main_teamup_taskwidgetbasectrl");
                var row = LuaPatches.Module(manager, global, "ui_sys_main_teamup_task_listwidgetctrl");
                var update = owner?.Obf_RuA<Obf_R.Obf_Ef>("_onUpdateTaskInfo");
                var close = owner?.Obf_RuA<Obf_R.Obf_Ef>("OnClose");
                var frame = row?.Obf_RuA<Obf_R.Obf_Ef>("_onClickFrame");
                if (update == null || close == null || frame == null)
                { Warn("native tracker methods unavailable; unchanged"); return; }
                _update = update; _close = close; _frame = frame;
                row.Obf_luA<string, LuaPatches.SelfMethod>("_onClickFrame", FrameWrapper);
                owner.Obf_luA<string, LuaPatches.SelfMethod>("OnClose", CloseWrapper);
                owner.Obf_luA<string, LuaPatches.SelfMethod>("_onUpdateTaskInfo", UpdateWrapper);
            }
            catch (Exception error) { Warn("install: " + error.GetType().Name + ": " + error.Message); }
        }

        private static void Updated(Obf_R.Obf_ff self)
        {
            _update?.Obf_HuA(self);
            try
            {
                var entry = self.Obf_RuA<object>(OwnerKey) as Entry;
                if (entry == null || entry.Closed || !ReferenceEquals(entry.Env, _env))
                {
                    if (Entries.Count >= 16) return;
                    entry = new Entry { Env = _env, Parent = self };
                    Entries.Add(entry);
                    RawSet(self, OwnerKey, entry);
                }
                Refresh(entry);
            }
            catch (Exception error) { Warn("update: " + error.GetType().Name + ": " + error.Message); }
        }

        private static void Closed(Obf_R.Obf_ff self)
        {
            try { if (self.Obf_RuA<object>(OwnerKey) is Entry entry) Remove(entry); }
            catch (Exception error) { Warn("close: " + error.Message); }
            _close?.Obf_HuA(self);
        }

        /// <summary>沿 UiTweaks 既有每十 frame 節奏；UI 刷新本身不啟動尋路。</summary>
        internal static void Tick()
        {
            foreach (var entry in Entries.ToArray())
            {
                if (entry.Closed) continue;
                if (entry.Disabled) { ReleaseWidget(entry); continue; }
                try { Refresh(entry); }
                catch (Exception error)
                {
                    entry.Disabled = true;
                    ReleaseWidget(entry);
                    Warn("render: " + error.GetType().Name + ": " + error.Message);
                }
            }
        }

        private static void Refresh(Entry entry)
        {
            if (entry.Closed || entry.Disabled || !ReferenceEquals(entry.Env, _env)) return;
            var plan = ReadPlan(entry.Parent);
            if (!plan.Show) { ReleaseWidget(entry); EmptyHint(entry.Parent, false); return; }
            if (entry.Widget == null && !entry.Lease.Pending) Create(entry);
            EmptyHint(entry.Parent, entry.Widget != null);
            if (entry.Widget != null && entry.Lease.Ready) Paint(entry, plan);
        }

        private static void Create(Entry entry)
        {
            var ui = _global?.Obf_RuA<Obf_R.Obf_ff>("Lua_UIManagerNewIns");
            var create = ui?.Obf_RuA<Obf_R.Obf_Ef>("CreateWidget");
            var content = entry.Parent.Obf_LuA<Obf_R.Obf_ff>("View.m_kRTRANSFORM_Content");
            var transform = content?.Obf_RuA<Obf_R.Obf_Ef>("GetTransform")?.Obf_HuA(content)?.FirstOrDefault();
            var name = _global?.Obf_LuA<object>("UINames.UI_Sys_Main_TeamUp_Task_ListWidget");
            if (create == null || ui.Obf_RuA<Obf_R.Obf_Ef>("ReturnWidget") == null || transform == null || name == null)
                throw new InvalidOperationException("native widget factory unavailable");
            // 本輪尚無 prefab 排版的靜態證據；只有實際 Content 的既有 layout 可安全替獨立 child 留空間。
            var rect = transform as RectTransform;
            var layout = rect != null ? rect.GetComponent<LayoutGroup>() : null;
            if (layout == null || !layout.isActiveAndEnabled)
            { Warn("waiting for active native Content LayoutGroup; custom row withheld"); return; }
            entry.Content = rect;
            int generation = entry.Lease.Begin();
            // 靜態 Entries 保活 callback；原生 ReturnWidget 會取消尚未執行的載入 callback。
            entry.Created = widget =>
            {
                if (entry.Closed || !ReferenceEquals(entry.Env, _env) || !entry.Lease.Complete(generation)) return;
                entry.Widget = widget;
                try
                {
                    RawSet(widget, OwnerKey, entry);
                    RawSet(widget, "m_kTaskInfo", null);
                    var plan = ReadPlan(entry.Parent);
                    if (plan.Show) Paint(entry, plan);
                }
                // 原生 callback 之後才 OnOpen；回收必須延至下一個 Tick，避免先入池又被 OnOpen 啟用。
                catch (Exception error) { entry.Disabled = true; Warn("created: " + error.Message); }
            };
            var returned = create.Obf_HuA(ui, name, entry.Parent, transform, entry.Created)?.FirstOrDefault() as Obf_R.Obf_ff;
            if (entry.Widget == null) entry.Widget = returned;
            if (entry.Widget == null) { entry.Lease.Cancel(); throw new InvalidOperationException("native widget was not created"); }
        }

        private static void Paint(Entry entry, TreasureHuntTrackerPlan plan)
        {
            if (!plan.Show) { ReleaseWidget(entry); return; }
            var view = entry.Widget.Obf_RuA<Obf_R.Obf_ff>("View");
            if (view == null) throw new InvalidOperationException("native widget view unavailable");
            RawSet(entry.Widget, "m_kTaskInfo", null);
            RawSet(entry.Widget, CompatiblePointKey, plan.PointId);
            foreach (var field in HiddenFields) Invoke(view.Obf_RuA<object>(field), "SetActive", false);
            Text(view.Obf_RuA<object>("m_kTTXT_Title"), NativeTitle());
            Text(view.Obf_RuA<object>("m_kTTXT_Des"), plan.Description(MapName(plan.TargetMapId), _labels.Map));
            Invoke(view.Obf_RuA<object>("m_kTTXT_Des"), "SetActive", true);
            Invoke(view.Obf_RuA<object>("m_kRTRANSFORM_Des"), "SetActive", true);
            var click = view.Obf_RuA<object>("m_kCLICKEVENT_Frame");
            if (click == null) throw new InvalidOperationException("native frame unavailable");
            // CreateWidget callback 在 OnOpen 前；OnOpen 會 Bind 到已包裝的 _onClickFrame，
            // 不把 C# delegate 以反射硬塞進未知的 C# ClickEvent ABI。
            if (entry.Content != null) LayoutRebuilder.MarkLayoutForRebuild(entry.Content);
        }

        private static void FrameClicked(Obf_R.Obf_ff self)
        {
            if (self.Obf_RuA<object>(OwnerKey) is Entry entry) { Resume(entry); return; }
            _frame?.Obf_HuA(self);
        }

        private static void Resume(Entry entry)
        {
            if (entry.Closed || entry.Disabled || !ReferenceEquals(entry.Env, _env)) return;
            try
            {
                var plan = ReadPlan(entry.Parent); // 點擊時重讀，絕不沿用建立按鈕時的舊 point。
                if (!plan.Show) { ReleaseWidget(entry); return; }
                Paint(entry, plan);
                if (!plan.CanResume) return; // 異圖只顯示目標圖，不傳送、重抽或強制換圖。
                var manager = Instance("LC_SystemManager");
                var system = manager?.Obf_RuA<Obf_R.Obf_Ef>("GetSystem")?.Obf_HuA(manager,
                    _global.Obf_LuA<object>("enum_SystemType.SeasonTreasure"))?.FirstOrDefault() as Obf_R.Obf_ff;
                system?.Obf_RuA<Obf_R.Obf_Ef>("StartHunt")?.Obf_HuA(system, plan.PointId);
            }
            catch (Exception error) { Warn("resume: " + error.GetType().Name + ": " + error.Message); }
        }

        private static TreasureHuntTrackerPlan ReadPlan(Obf_R.Obf_ff parent)
        {
            var assets = _global?.Obf_RuA<Obf_R.Obf_ff>("LC_AssetManagerIns");
            var hunt = assets?.Obf_RuA<Obf_R.Obf_Ef>("GetAssetData")?.Obf_HuA(assets, "SeasonTreasureHunt")?.FirstOrDefault() as Obf_R.Obf_ff;
            long point = Number(hunt?.Obf_RuA<object>("iPointId"));
            var cfg = Configs()?.Obf_RuA<Obf_R.Obf_ff>("m_kCFG_TreasureHuntMapPointConfig");
            long map = point > 0 ? Number(cfg?.Obf_RuA<Obf_R.Obf_Ef>("GetMapId")?.Obf_HuA(cfg, point)?.FirstOrDefault()) : 0;
            var area = Instance("LC_TriggerAreaManager");
            long current = Number(area?.Obf_RuA<Obf_R.Obf_Ef>("GetCurMapId")?.Obf_HuA(area)?.FirstOrDefault());
            long nativePoint = 0;
            var list = parent.Obf_RuA<Obf_R.Obf_ff>("m_kTaskListWidgets");
            if (list != null)
                for (long i = 1; i <= 64; i++)
                {
                    var row = list.Obf_ruA<long, Obf_R.Obf_ff>(i);
                    if (row == null) break;
                    if (Number(row.Obf_RuA<object>(CompatiblePointKey)) == point) { nativePoint = point; break; }
                }
            return TreasureHuntTrackerPlan.Read(point, map, current, nativePoint);
        }

        private static string NativeTitle()
        {
            var id = _global?.Obf_LuA<object>("UIWordsDefine.SeasonTreasureFinding");
            return Word(id) ?? _labels.Map("Treasure hunt");
        }

        private static string MapName(long map)
        {
            var cfg = Configs()?.Obf_RuA<Obf_R.Obf_ff>("m_kCFG_MapConfig");
            var row = cfg?.Obf_RuA<Obf_R.Obf_Ef>("GetEntryPtr")?.Obf_HuA(cfg, map)?.FirstOrDefault() as Obf_R.Obf_ff;
            var names = row?.Obf_RuA<Obf_R.Obf_ff>("_iName");
            return Word(names?.Obf_ruA<long, object>(1)) ?? (_labels.Map("Map") + " " + map);
        }

        private static string Word(object id) => id == null ? null :
            _global?.Obf_RuA<Obf_R.Obf_Ef>("GetWord")?.Obf_HuA(id)?.FirstOrDefault() as string;

        private static Obf_R.Obf_ff Configs() => Instance("Lua_DBManager");
        private static Obf_R.Obf_ff Instance(string name)
        {
            var type = _global?.Obf_RuA<Obf_R.Obf_ff>(name);
            return type?.Obf_RuA<Obf_R.Obf_Ef>("GetInstance")?.Obf_HuA(type)?.FirstOrDefault() as Obf_R.Obf_ff;
        }

        private static void Remove(Entry entry)
        {
            entry.Closed = true;
            ReleaseWidget(entry);
            RawSet(entry.Parent, OwnerKey, null);
            Entries.Remove(entry);
        }

        private static void ReleaseWidget(Entry entry)
        {
            entry.Lease.Cancel();
            var widget = entry.Widget; entry.Widget = null;
            if (widget == null) return;
            try
            {
                RawSet(widget, OwnerKey, null);
                RawSet(widget, CompatiblePointKey, null);
                var ui = _global?.Obf_RuA<Obf_R.Obf_ff>("Lua_UIManagerNewIns");
                ui?.Obf_RuA<Obf_R.Obf_Ef>("ReturnWidget")?.Obf_HuA(ui, widget);
            }
            catch (Exception error) { Warn("return: " + error.Message); }
            if (entry.Content != null) LayoutRebuilder.MarkLayoutForRebuild(entry.Content);
            EmptyHint(entry.Parent, false);
        }

        private static void EmptyHint(Obf_R.Obf_ff parent, bool hasTracker)
        {
            var rows = parent.Obf_RuA<Obf_R.Obf_ff>("m_kTaskListWidgets");
            bool hasOrdinary = rows?.Obf_ruA<long, object>(1) != null;
            Invoke(parent.Obf_LuA<object>("View.m_kIMG_Null"), "SetActive", !hasTracker && !hasOrdinary);
        }

        private static void RawSet(Obf_R.Obf_ff table, string name, object value)
        {
            var set = _global?.Obf_RuA<Obf_R.Obf_Ef>("rawset");
            if (set == null) throw new InvalidOperationException("native rawset unavailable");
            set.Obf_HuA(table, name, value);
        }

        private static void Text(object target, string text)
        {
            if (target == null) throw new InvalidOperationException("native text field unavailable");
            if (target is TMPro.TMP_Text tmp) { tmp.text = text; return; }
            if (target is UnityEngine.UI.Text legacy) { legacy.text = text; return; }
            Invoke(target, "SetText", text);
        }

        private static void Invoke(object target, string method, object value)
        {
            if (target is Obf_R.Obf_ff lua)
            {
                var function = lua.Obf_RuA<Obf_R.Obf_Ef>(method);
                if (function == null) throw new InvalidOperationException("native method unavailable: " + method);
                function.Obf_HuA(lua, value);
                return;
            }
            if (target == null) return;
            var call = target.GetType().GetMethods().FirstOrDefault(m => m.Name == method && m.GetParameters().Length >= 1
                && (value == null || m.GetParameters()[0].ParameterType.IsInstanceOfType(value))
                && m.GetParameters().Skip(1).All(p => p.IsOptional));
            if (call == null) throw new InvalidOperationException("native method unavailable: " + method);
            var parameters = call.GetParameters();
            var arguments = new object[parameters.Length];
            arguments[0] = value;
            for (int i = 1; i < arguments.Length; i++) arguments[i] = parameters[i].DefaultValue;
            call.Invoke(target, arguments);
        }

        private static long Number(object value) { long number = LuaPatches.ToLong(value); return number == long.MinValue ? 0 : number; }
        private static void Warn(string message) { if (Reported.Add(message)) Debug.LogWarning("[Ro3Emu.TreasureTracker] " + message); }
    }
}

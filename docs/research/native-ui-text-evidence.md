# 原生版本欄與按鍵名稱

來源為唯讀 `G:\Genesis Launcher\ROO_PC`；本項採原件 Lua、Managed CIL 與 prefab 靜態證據，不啟動遊戲或擷取封包。

`LoginWidgetBaseCtrl.OnOpen` 呼叫 `SetVersionTxt`，以 `Application.version` 與 `HappyEngine.Runtime.NewRuntimeSystem.GameVersionProxy.LocalVersion` 組成 `app_resource`，寫入既有的 `m_kTTXT_SerialNumber`。PC 控制器沒有覆寫此函式；原生 PC prefab 的 `Pan_SerialNumber`／`SerialNumber_TTxt` 為啟用，另有未啟用的 `VersionNum_TTxt` 偵錯欄位，不能將兩者混為一談。

`NativeVersionLabel` 只尋找原登入元件的 SerialNumber 欄位，恢復原生文字與可見狀態；來源尚未就緒時保留現值，精確吻合目前 app／resource 的既有 Build 後綴也會保留。原生版本缺失的實際執行原因與 `Build 140083` 追加來源仍是 `UNKNOWN`，這是相容性恢復候選，不能宣稱已重現其全部原服執行流程。更新發布序號、玩家更新包版本、登入器版本均不冒充原生 Build。

按鍵名稱走另一條路：`InputSystemManager` 經 Unity `InputActionRebindingExtensions.GetBindingDisplayString` 產生顯示字串；`InputBinder` 讀取對應值，`Happyengine.UI.Framework.dll` 的 `InputBinderBase.OnKeyCodeChange` 將它寫入 `m_tip` 的 TMP 文字出口。此鏈不使用物品操作的 `LanguageMain.Get(sid)`。

原繁中一般文字對照將英文 `Insert` 選為「插卡」，誤套到鍵盤提示。`zh_tw_overrides.tsv` 以精確 `text` 範圍保留 `Insert`；插卡、鑲嵌操作仍依各自的原生 SID 顯示，沒有全域替換中文「插入」。自造同字異義測試使用隨附覆寫，驗證按鍵名稱與兩種物品操作可並存；完整來源表另做離線 profile 驗證。截圖中圖示對應的 prefab actionName 與實際玩家畫面尚未執行驗收。

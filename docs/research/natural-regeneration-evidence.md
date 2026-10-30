# 自然 HP／SP 回復

權威原件為 `G:\Genesis Launcher\ROO_PC`。`LC_DAL_PlayerInfo.TryGetAttrValue` 的 FinalReHp／FinalReSp 分支讀取 `ReHP／SP + MaxHP／SP × PreReHP／SP`。整數屬性對應 107／108，百分比屬性 1071／1081 在伺服器屬性表以萬分比保存；不是把目前剩餘 SP 當百分比基底。

新鮮原件來源：`Logic/Asset/LC_DAL_PlayerInfo.lua.bytes`、`Logic/Role/LC_PlayerInfoHelper.lua.bytes`、`Config/DataConfig/AttributeConfig.lua.bytes` 與 `Config/CFG/Lua_CFG_AttributeConfig.lua.bytes`。AttributeConfig 原件 SHA-256 為 `4fddedee25d1ff1ce27d46795065a8fb6c058d560c374b2a5b155553720b0a82`，CFG 原件為 `449c54b18dd5037a09710020941d2902a0e560eab442b74368df5ca7b63e7e49`。靜態分析不等於原服伺服器的執行紀錄。

117／118 的 `_iDataType=0`、`_iTextValueDisplayType=0`、`_kParamCountType=[1]`，原生 RoleStatItemWidgetCtrl 經 CFG GetAttrRealDesc 使用 `Mathf.Ceil` 後以 `%.0f` 顯示。這證明顯示值向上取整，不能推出權威伺服器每次實際入帳也向上取整。本地自然回復結算採向上取整以對齊畫面顯示（inferred consistency policy）；原服權威入帳取整仍為 UNKNOWN，不以 UI rounding 冒稱已證實。

舊 `CombatService.RegenAsync` 在 FightBit 有效時直接返回，連 SP 也完全不回復，造成持續戰鬥／施法時的已回報問題。這次依使用者需求採「戰鬥中仍回復 SP」的本地政策（inferred）；原服的戰鬥停止條件仍未由客戶端或執行紀錄閉合。HP 保留舊有的脫戰恢復限制。

既有每 5000ms、依角色相位分散的 GameLoop 排程不變；沒有新增計時器或改寫 owner。回呼在角色鎖前後都驗證目前角色／owner／attachment；死亡不恢復。等待角色鎖時發生的耗魔先完成，回復取得鎖後重新讀目前 SP，不以等待前的舊值覆蓋。全滿、未設定的全滿狀態或回復量為零均不發送額外屬性封包。

`Regeneration` 用 Int128 計算完整分子 `flat×10000 + max×rate`，正值以 `(分子+9999)/10000` 向上取整，避免 long 中間乘積溢位與逐項取整誤差；非正回復量歸零，不再強行至少補1。加入目前值時先算距上限差額，採飽和加法並限制在最大值，不會因加法溢位反而清空 HP／SP。這些是有界整數處理，不創造額外屬性或持久化餘額。

`NaturalRegenerationTests` 使用自造屬性與 SyntheticHost，包含真 GameLoop 自然排程及 Bot31008：戰鬥中 SP20→27、HP50不變；SP99封頂100；死亡／過期／零值／全滿不發包；以實際角色鎖等待重現耗魔70→50後再恢復57，過期回呼則保留50。`NaturalRegenerationMathTests` 另驗零值、負值、向上取整與 long 極值溢位，包括自造組合 flat300／max1210／rate400 得349；不把此組合當成實際玩家配裝。這些測試與原生玩家長時間施法驗收分開，未取原服封包或記憶體。

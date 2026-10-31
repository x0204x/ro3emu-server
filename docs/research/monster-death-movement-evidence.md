# 怪物死亡、移動終止與屍體離場

## 客戶端證據範圍

本次唯讀核對 `G:\Genesis Launcher\ROO_PC\ro3_Data\StreamingAssets\Recovery\LuaPayload` 的 Lua 位元組，
在獨立 LAB 正規化並反編譯，未執行遊戲或 Lua。以下證據繫結指定本機客戶端的 Recovery 載入資料，
不能直接推定原服實際封包時序，也不能以靜態函式取代玩家畫面驗收。

| 相對 LuaPayload 路徑 | SHA-256 |
| --- | --- |
| `Unit/LC_UnitBase.lua.bytes` | `3d63e6a3f4a493bc20fc48a3a5424e371ae0bcd1fea5cec476a81d627fada49b` |
| `Unit/Component/LC_Movement_Base.lua.bytes` | `167058f01723d8eb34cc71427fe843963559e1ef493d144b80036ea9d5929f61` |
| `Logic/Unit/Component/LC_Movement_MoveSyncWalk.lua.bytes` | `f78b96ff65cadea510c9a183f326b3786480bf50fbea75a34341d5f96aab164c` |
| `Logic/Unit/Component/LC_Movement_MoveSyncNone.lua.bytes` | `a9ff92e81133c3a709b3a21924468e692d596b588ee3c3705a3a516add0af4be` |
| `Logic/StateMachine/LC_State_Unit_Dead.lua.bytes` | `05975e17072098ea0d2a9f494fd7f21d87f95a056e0ba91076cddb06bb1b685a` |
| `Logic/Scene/LC_Scene_Sync.lua.bytes` | `437e47fcc98257de1261996aa784e096c1fccec106da25ce80cb362b6d73e01d` |

`LC_Scene_Sync._onMovementChange` 將正常移動通知交給單位的 `OnMovement`。
`LC_Movement_MoveSyncWalk._doStart` 依方向、速度與持續時間求出終點，呼叫 `StartWalkMode` 啟動平滑移動；
`LC_Movement_MoveSyncNone._doStart` 則透過 `SetSmoothPos` 校正位置並結束該段移動。

`LC_UnitBase._unitDead` 對非玩家單位呼叫移動元件 `OnDisable`，再進入 Dead 狀態。
可見的 `LC_Movement_Base.OnDisable` 呼叫 `Stop`，後者透過 `SetMoveState(false)` 清除移動旗標與預設 Walk 狀態。
這段 Lua 本身不足以證明所有已啟動的原生平滑路徑都已取消，因此伺服器不能只修改自己的 `Moving` 旗標。

`LC_State_Unit_Dead._startDead` 依 `_iDeadShow`、`_iBodyRemainTime` 及死亡表演時間安排屍體表現。
本修正保留這些資料及既有伺服器屍體保留、重生計時，不依影片調整死亡動畫秒數。

## 伺服器排序與修正

`World.MoveUnitAsync` 與進場、離場共用單位的 `PresentationGate`。移動可先建立，等候進場完成時才取得鎖；
在這段等待期間，戰鬥可能已將怪物 HP 歸零。原本取得鎖後只檢查 `Removed`，仍會送出舊的 Walk。
現在於取得鎖後重新檢查 `Dead` 與 `Hp <= 0`，拒絕該怪物的 Walk；明確的靜止校正仍可通過。

`MonsterService.KillAsync` 原本只清除伺服器移動旗標，再發布 HP 與死亡狀態。
現在先經相同 AOI 鎖送出最後權威座標的 `31081 / MoveTypeNone`，再發布 `31048` 與 `31049 / Dead`。
此順序為 **(inferred) 模擬器的同步保護**，不是宣稱已擷取到原服相同順序。
若原先的 Walk 已在傳送中，靜止通知會排在它之後；若尚未取得鎖，死亡檢查會將它拒絕。
死亡時不再向舊終點推進座標，避免把交易等待時間算成死亡後的追擊移動。

既有 `World.RemoveUnitAsync` 仍負責設定 `Removed`、移出空間索引、清除觀看者及送出 `31037 / leave_aoi`。
重複呼叫擊殺或移除不會重送屍體離場；重生使用新單位與新 UID，不沿用死亡單位。

## 合成回歸驗證

所有測試使用自造資料，不複製客戶端設定列。

| 測試 | 驗證內容 |
| --- | --- |
| `WorldTests.MonsterWalkWaitingForAoiPublicationCannotResumeAfterLethalDamage(false)` | AOI 鎖等候期間 HP 歸零、尚未標記 Dead，也不得送出舊 Walk；靜止校正可送出且只離場一次。 |
| 同測試 `(true)` | 已標記 Dead 時具有相同保護。 |
| `MonsterDeathMovementTests.DeathStopsThePublishedWalkBeforeDeadStateAndRemovesTheCorpseOnce` | 真合成主機與 Bot 接收封包，確認 Walk → 靜止 → Dead，停止座標不變，重複擊殺仍只有一次離場。 |

三個案例在修正前都失敗：前兩個收到不應出現的 Walk，第三個只有 Walk 而缺少靜止通知。
修正後三個皆通過；連同相關 World、Pet AOI 測試共 27 個通過，無失敗。

## 尚待驗收

玩家端屍體漂移改善為 `NOT_RUN`，仍須以相同連續擊殺情境確認。
影片中多具已消失屍體同時短暫重現的原因為 `UNKNOWN`；目前沒有該時段的 UID、封包與畫面對時證據，
不可將此次停止移動修正宣稱為所有殘影問題都已解決。
客戶端的 `SetAllShowOrHideUnitGhost` 呼叫位於本地玩家控制單位的 Ghost 離場分支，
目前不能用這個名稱推定一般怪物屍體閃回的原因。

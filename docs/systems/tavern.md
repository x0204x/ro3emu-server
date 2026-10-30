# 酒館房間與每日任務

酒館由 `TavernService` 管理房間、角色租約及有效停留時間；任務接受、完成、經驗與物品獎勵仍由 `DailyActivityService`、`TaskService` 與既有經濟交易處理。客戶端契約與推定政策見 [來源證據](../research/tavern-evidence.md)。

## 支援範圍

| OPCODE | 請求／同步 | 目前行為 |
| --- | --- | --- |
| 82481 | `CreateBarRoomReq` | 驗證房型、語言、房名與功能資格，建立房間並加入 |
| 82482 | `EnterBarRoomReq` | 驗證房間、區域、容量及目前租約，加入房間 |
| 82483 | `ExitBarRoomReq` | 結算有效時間後退出；不能退出別人的房間 |
| 82508 | `BarRoomGetReq` | 回傳目前租約所屬房間的 `BarRoomTotalDetail` |
| 5043 | `DBBarRoomInfo` | 同步房間、已結算時間與當日交付狀態 |
| 82506 | `BarRoomInfoUpdateNotify` | 通知仍在房間內的成員重新取得明細 |

`PartyService` 的 `partyType=4` 提供酒館目錄。`getPartysSummary` 回傳 `SummaryList[].GameSummaryData`，內容為 `BarRoomSummary` protobuf bytes；其他 party type 保留原有組隊分派。`getSubPartysCountByType` 依區域統計房型數量。

`GroupCount` 的內部形狀採既有 PartyService 的 `{SubType, Count}` 陣列，屬 **推定（inferred）**。原件酒館 helper 只證實接收並保存 `GroupCount`，尚無原服封包證明該陣列形狀。合成測試只能確認目前服務輸出契約，不能將它升格為原服相符證據。

以下 18 個請求回傳對應 protobuf response，`Result=FeatureUnavailable`：

| OPCODE | 請求名稱 |
| --- | --- |
| 82484 | `InviteEnterBarRoomReq` |
| 82486 | `ResponseInviteEnterBarRoomReq` |
| 82487 | `ApplyOpenMicReq` |
| 82488 | `DownMicReq` |
| 82489 | `TransferOwnerReq` |
| 82490 | `ModifyControlMicTypeReq` |
| 82491 | `PresentGiftReq` |
| 82492 | `OwnerHandleOpenMicReq` |
| 82493 | `SetMicOrderReq` |
| 82494 | `SetBarRoomInfoReq` |
| 82498 | `ForbiddenUserReq` |
| 82499 | `CanecelForbiddenUserReq`（原件拼字） |
| 82500 | `KickUserReq` |
| 82501 | `CancelKickUserReq` |
| 82504 | `ShutUpAllReq` |
| 82505 | `CancelShutUpAllReq` |
| 82513 | `SetGmBarRoomInfoReq` |
| 82514 | `MuteBarRoomPlayerReq` |

房間建立成功不代表語音 SDK 加入成功；伺服器不偽造語音成功、送禮、人氣或麥克風狀態。82485、82507 是原件通知契約，目前沒有提供相應邀請或語音功能。

## 設定與資格

`BarroomTypeConfig._iType=0` 是房型，`_iType=1` 是語言，建立時不能互換。房名上限讀 `PublicParamConfig.BarRoomNameLen`，容量讀 `BarroomPersonNum`。推薦搜尋的 `BarroomFindBarRoom` 人數範圍不等於房間容量。

酒館功能為 2410，原件條件 2000174 要求 Base 等級 36。每日活動 2000 的任務接受動作為 `31,1,2000`；`BarroomTaskID` 指定任務 2000，目標為 `[106010,1200]`，達標後自動交付。獎勵及 Base／Job 經驗沿現行任務與獎勵設定解析，不由酒館另行發放。106010 也被其他任務使用，酒館秒數只允許更新設定指定的酒館任務。

## 時間、斷線與跨日

有效時間必須有已提交房間成員資格、同一角色附著租約及仍有效的房間。僅一般在線時間不算酒館時間。毫秒保留至下一次結算，任務只增加跨過的整秒，當日時間以任務門檻為上限。

GameGate 在首次進入 socket 斷線處理時記下 UTC 毫秒，再由遊戲迴圈通知酒館；成功重連時記錄恢復時點。酒館只記錄暫存的失聯區段，下一次角色 owner 交易才扣除離線時間。重複失聯通知保留第一個起點，多次重連保留各段缺口；提交成功後才清除已結算區段。這保留斷線前尚未提交的連線秒數，同時排除 reconnect grace 與交接等待時間。重連票證、有效期與原有寬限政策不變。

每日切換先結算舊日有效時間並交付欠獎，再將每日資料、任務及酒館計時狀態一起重設。退出與斷線清理也先走此流程，避免新日時間灌入舊日。已達標但獎勵交易失敗時，登入重設必須保留舊日資料，待自然任務結算成功後才換日。

原服對靜默停留、語音參與及每日時區的精確規則仍為 `UNKNOWN`。目前採有效房間停留與既有伺服器每日日界，屬 **推定（inferred）**；日光節約時間造成的長短日仍需另外核對。

## 持久化與一次性獎勵

`role.tavern` 保存每日期間、有效毫秒、已結算游標及房間租約；realm `tavern.state` 保存房間與成員控制資料。兩者都沒有貨幣、道具或待領獎勵，經濟讀取器以嚴格 JSON 與欄位清單分類。

房間異動同時取得角色與酒館 realm owner。計時以同一交易保存 meter、任務進度與必要後續來源，提交 callback 才更新記憶體。獎勵只走既有任務交付帳本；提交前故障可重試，提交後套用故障進入角色復原流程，再登入依持久化狀態續做。

重送建立／加入不重複建立成員資格。房間身分先由共用配置器提交，因此房間提交失敗可能留下號碼間隙；重試不發布重複房間。房主離開時以加入時間、角色 ID 轉移給剩餘成員，空房關閉，屬 **推定（inferred）**。冷啟動不恢復活躍連線租約，重新登入清除舊租約，不補算離線或停機時間。

## 驗證範圍

以下為已通過的合成／靜態資料驗證範圍，不能替代真實客戶端驗收：

| 測試 | 驗證類型 |
| --- | --- |
| `TavernTests.MeterUsesOnlyEligiblePositiveIntervalsAndCapsOnce` | 自造時間、上限與原物件不變 |
| `TavernTests.FractionalIntervalsRetainTheirRemainderWithoutDuplicateCredit` | 毫秒餘數與重送 |
| `TavernTests.SavedOldDayAwardRemainsPendingUntilActualDelivery` | 舊日期間保留條件 |
| `TavernOwnerTests.RoomLifecycleRequiresLeaseAndDirectoryCarriesRealSummaryBytes` | Bot 原始 OPCODE、protobuf 回應、Micro 分派與權限 |
| `TavernOwnerTests.RoomCommitFailureLeavesNoPublishedRoomAndRetryCreatesOne` | 提交前故障、重送與單一房間 |
| `TavernOwnerTests.TimeCommitFailureRetriesWithoutChangingMemoryOrPayingTwice` | 計時故障、實物獎勵入帳與重啟 |
| `TavernOwnerTests.ExitAcrossDailyBoundarySplitsTimeAndRestartCannotResumeOldLease` | 跨日退出、冷啟動及離線時間 |
| `TavernOwnerTests.HeldOldDayAwardSettlesOnNextDayLoginExactlyOnce` | 欠獎、跨日登入與一次性入帳 |
| `TavernOwnerTests.TimeCommitAppliedFailureRequiresRecoveryAndRestartSettlesOnlyOnce` | 提交後套用故障與復原 |
| `TavernOwnerTests.BeginWithoutRoleDoesNotKeepAStaleTavernCutoffAfterLogin` | 空角色交接與 session 重用 |
| `TavernClientDataTests.ActualCategoriesAndTaskThresholdRemainDistinct` | 客戶端設定分類與門檻 |

`ReconnectGraceExcludesEveryGapButKeepsUncommittedConnectedSeconds` 已通過完整 gate-auth、多次真實 socket 掉線／重連與寬限期間計時的合成驗證。玩家端房間介面、目錄人數、語音及原服封包比對為 `NOT_RUN`。

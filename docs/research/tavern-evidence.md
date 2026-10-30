# 酒館客戶端契約證據

本文件記錄 Genesis **2026.10.29.1** 原件的可定位契約。SHA-256 綁定原始 bytes，不以檔名、反編譯快取或模擬器輸出當作原件相同證據。原始客戶端與大量反編譯內容不納入 Git。

## 原始 bytes 綁定

Lua 路徑相對於客戶端 `Recovery/LuaPayload`；protobuf 路徑相對於 `StreamingAssets`。

| 檔案 | SHA-256 |
| --- | --- |
| `Network/NW_Helper_PBDefine.lua.bytes` | `cb609af8e5215f3b50e8317cfc701529fbd05f0951cb057529b48fb7c2f24d22` |
| `Network/NW_AutoSyncClient_Define.lua.bytes` | `7bf376f82b352871e4a8190df596bdb98dc73f8efc6dd258cf15f3df22abdf48` |
| `Logic/ChatBarroom/BarRoomNet/LC_BarRoomServer.lua.bytes` | `d57ce1198b1fff363dda2d106948c544dab32585f11b6387e4ffde3076a897f2` |
| `Logic/ChatBarroom/BarRoomLogic/LC_BarRoomHelper.lua.bytes` | `6ede718c3e23266831dfd897103c971d0959759765e66bb43f781b3e86394604` |
| `Logic/Asset/LC_DAL_BarRoom.lua.bytes` | `5808418c8b18b29492ab69f95940df38d63ecd732cea1524aa99be39c8e16f26` |
| `Logic/ChatBarroom/BarRoomSDK/LC_BarRoomTalk.lua.bytes` | `7182af9ee06b624780eacaa65519083d3642533213cdb585eacc99a3b2fdbf9f` |
| `Config/Define/Lua_GameDefine.lua.bytes` | `fb019d825d76c14f0e44956b743190f5044800da7e6994f8c67da897d00b4b7a` |
| `Config/DataConfig/TaskConfig.lua.bytes` | `6e15ff75c90d95626bb9041a83ae8198efa80bb0275cd22222f00f034be4609d` |
| `Config/DataConfig/BarroomTypeConfig.lua.bytes` | `336a7f71f99b8f3fd87230f155a472f84c1003681009ab3272ba7e48641bc91c` |
| `Config/ComplexConfig/PublicParamConfig.lua.bytes` | `0a227e020dfc494d77cacb927d811f3f6299c9bfd0b455566bc6f03d2e0d1270` |
| `LocalProto/proto.pb.bytes` | `f80474d87743f65daf9f4502f47db174267dc45c91fd6ac3bb9f73ae33695664` |

Lua 以純靜態正規化及反編譯定位 symbol，protobuf 以 descriptor 定位完整型別與 field number；這些是靜態契約，不代表原服已實際產生該封包。

## 已由原件證實的契約

`NW_Helper_PBDefine` 與 `LC_BarRoomServer` 綁定 82481 建立、82482 進入、82483 退出、82508 目前房間明細。完整支援／拒絕清單見 [系統說明](../systems/tavern.md#支援範圍)。82485、82506、82507 為通知，不應當作一般客戶端操作成功回應。

`MG_Barroom.proto` 的主要欄位如下：

| 型別 | 欄位號與型別 |
| --- | --- |
| `CreateBarRoomReq` | 1 `barRoomType:int32`、2 `languageType:int32`、3 `barRoomName:string`、4 `areaId:int64` |
| `EnterBarRoomReq` | 1 `barRoomGuid:int64`、2 `areaId:int64` |
| `ExitBarRoomReq` | 1 `barRoomGuid:int64` |
| `CreateBarRoomResp`／`EnterBarRoomResp`／`BarRoomGetResp` | 1 `result:int32`、2 `info:BarRoomTotalDetail` |
| `BarRoomSummary` | 1 `barRoomGuid:int64`、2 `barRoomType:int32`、3 `languageType:int32`、4 `barRoomName:string`、7 `regionId:int32`、8 `memberList:repeated MemberInfo` |
| `MemberInfo` | 1 `memberId:int64`、2 `areaId:int64`、3 `joinTime:int64` |
| `BarRoomTotalDetail` | 1 `barRoomSummary:BarRoomSummary`、6 `curSpeaks:repeated CurSpeakUsers`、7 `historyHot:HistoryHotInfo`、9 `isCloseMic:bool`、10 `ownerGuid:int64`、11 `createTime:int64` |

`NW_AutoSyncClient_Define` 的 5043 對應 `DBBarRoomInfo`：3 `beginBarRoomTime:int64`、4 `inBarRoomTime:int32`、5 `isToDayActComplete:bool`、7 `barRoomId:int64`。人氣、活動天數及週人氣另有欄位，不能因結構存在就宣稱已實作其原服生產者。

`LC_BarRoomServer.PartyQuerySummaryRequest` 使用 `PartyService.getPartysSummary`，位置參數依序為 partyType=4、字串條件、整數條件、排序條件、起點、筆數、DirectQuery。可定位的整數條件名稱為原件拼字 `BaroomCreateTime`、`BaroomHeadCount`。`LC_BarRoomHelper.OnPartyQueryGroupCount` 僅將回應的 `GroupCount` 存入酒館資料；不能據此推導其元素欄位。房間明細實際由 82508 取得，僅宣告 `getParty` 名稱不構成必須另造該回應的證據。

`Lua_GameDefine` 將 `ChannelBarRoom` 綁定功能 2410，並定義 Micro 位置參數索引。`BarroomTypeConfig` 的 13 筆資料分為 `_iType=0` 房型與 `_iType=1` 語言。`PublicParamConfig` 的具名參數包括 `BarRoomNameLen=18`、`BarroomPersonNum=1000`、`BarroomTaskID=2000`；推薦搜尋人數 2..30 不是容量限制。

`TaskConfig[2000]` 為 type 9，`_kProgressTarget=[[106010,1200]]`、`_kDeliverTask=[1]`、`_iAward=8000`、`_kEXP=[1,100]`。這些證實任務門檻與獎勵解析入口，不能獨自證明 Job 經驗公式或實際收到的數量；入帳仍須核對獎勵設定與任務交易。

`LC_BarRoomTalk.JoinChannel` 呼叫語音加入流程並處理成功／失敗回呼；房間加入成功不能替代該回呼，也不能從靜態方法存在推論語音服務可用。

## 推定與未取得證據

- **推定（inferred）**：有效房間租約的靜默停留可累積任務秒數；原服是否另要求語音、互動或其他活躍資格為 `UNKNOWN`。
- **推定（inferred）**：沿用伺服器既有每日日界、房主離開時的確定性轉移、冷啟動關閉舊租約；原服 lease、日界時區與日光節約時間政策為 `UNKNOWN`。
- **推定（inferred）**：`GroupCount` 採共用 PartyService 的 `{SubType,Count}` 陣列。原服回應形狀仍待封包或完整消費端證據。
- 設定中的資訊修改／轉移冷卻、禮物、人氣、麥克風與管理欄位僅屬靜態契約；目前拒絕未支援請求，不能稱為完整酒館功能。
- 原服封包擷取、玩家端房間／目錄／語音驗收為 `NOT_RUN`。合成資料、Bot 封包、提交故障及重啟測試只驗證模擬器自身的流程與持久化。

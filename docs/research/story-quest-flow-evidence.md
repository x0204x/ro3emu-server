# G 版客戶端三條主線任務流程證據

日期：2026-10-29
狀態：`PARTIAL`（靜態 G 客戶端證據已完成；尚未進行伺服器/玩家端同一回合封包驗收）

## 1. 範圍與結論

本報告只處理 `G:\RO3 Asia Launcher\Client` 的下列三條任務，不把 D 版或 OfflineLab 的檔案身分當成 G 版結論：

1. 魔物來襲：任務 `10102100`，副本 `1021301`。
2. 虎王：任務 `10102200`，副本 `1022001`；後續任務 `10102210`、互動目標 `40022001`。
3. 凶惡的鳥媽媽：任務 `10103120`，副本 `1051001`。

靜態結論如下：

- 三條任務的完成條件都不是「客戶端看到副本結算畫面就自行完成」。`30403 CELL_DUNGEON_RES_NOTIFY` 只走副本結算事件/UI；真正任務進度與狀態由伺服器透過 `5027 AUTO_SYNC_CLIENT_ROMSG_DBTASKINFO` 的 `DBTaskInfoIncrement` 同步。
- 魔物來襲 `10102100` 的客戶端條件是「副本 `1021301` 通關 1 次」。三波怪物的波次生成、波次清場與副本何時判定成功，不在該 TaskConfig 任務列內，屬伺服器/副本腳本權威邏輯。
- 虎王 `10102200` 的客戶端條件是「副本 `1022001` 通關 1 次」。其後續 `10102210` 才是「客戶端互動 `40022001` 1 次」；互動完成時客戶端送 `31214 TASK_TRIGGER_EVENT`，payload 為 `{taskId:40022001, goalCount:1}`，伺服器仍須再以 `5027` 推進 `10102210`。
- 凶惡的鳥媽媽 `10103120` 的客戶端條件是「副本 `1051001` 通關 1 次」，不是 TaskConfig 內的擊殺隻數條件。因此 UI 沒寫要殺幾隻，不能由客戶端資料推定一個數量；副本成功條件仍需由伺服器/副本腳本實作。

## 2. G 客戶端精確身分

| 檔案 | 位元組 | SHA-256 / MVID |
|---|---:|---|
| `G:\RO3 Asia Launcher\Client\ro3_Data\Managed\Assembly-CSharp.dll` | 4,188,160 | SHA-256 `4365BCCFB4BADBF9D73C39CD8491F8BCFAF7604FC09C91DBC71123B100602E11`; MVID `e110a0b2-21ac-4fc3-a019-467cdaaaa9a1` |
| `G:\RO3 Asia Launcher\Client\ro3_Data\Plugins\x86_64\xlua.dll` | 872,960 | SHA-256 `C189E0FDFB23782D381DFD761A4D746C1E195508ED8EECBE25F23A39BFC7309A` |
| `G:\RO3 Asia Launcher\Client\ro3_Data\StreamingAssets\LocalProto\proto.pb.bytes` | 1,149,991 | SHA-256 `B5CF437794324A834854115CF5F69FF1A738E79E16228C0D42BD9D95C02DEA00` |
| `Recovery/LuaPayload/Config/DataConfig/TaskConfig.lua.bytes` | 283,482 | SHA-256 `DBC9F157A40957D38BD9F97480C8A7B9EB3786B08A46EFCBE7CC4BD7316528A1` |

Recovery manifest 顯示 `BuildVariant=ReconstructedPayload`、`LuaSourceKind=ReconstructedPayload`、共 13,488 個模組，且 `approved_player_xlua_sha256` 等於上述 G 版 `xlua.dll` 雜湊。

本流程用到的其他 G 版 Lua payload：

| Payload 相對路徑 | SHA-256 |
|---|---|
| `Logic/ConditionSystem/LC_ConditionDefine.lua.bytes` | `f13bdb662d7b558dbd837d3793c6c0c7b57056d3249459b59396d4627157b304` |
| `Logic/Level/LC_DunGeon_Base.lua.bytes` | `1c5366b69c20e418b27a88229aa38370757a5bc22ddbde19128635522d264c91` |
| `Network/NW_GameServerHandler.lua.bytes` | `df4fa8bc18976bf23231306f93d5dba3bf050ce16c1690f9c7f55aa804dea0da` |
| `Logic/Task/LC_TaskHelper.lua.bytes` | `b10847fd14a3f063c3e52059f24b13e5fdeadd19be1719548b154646c21ee16e` |
| `Network/NW_Helper_PBDefine.lua.bytes` | `cb609af8e5215f3b50e8317cfc701529fbd05f0951cb057529b48fb7c2f24d22` |
| `Network/NW_AutoSyncClient_Define.lua.bytes` | `7bf376f82b352871e4a8190df596bdb98dc73f8efc6dd258cf15f3df22abdf48` |
| `Logic/Asset/LC_AssetAutoSyncDataDefine.lua.bytes` | `0f5f4997eb6b246a20fa07a3f42b5d04067f9bf26c2a16eeb8f7639b3cc86c91` |
| `Logic/Asset/LC_AssetManager.lua.bytes` | `89f7a37aed83d2c294ec78dc66451813f95e59141dc81493485ea47a3be8243c` |
| `Logic/Asset/LC_DAL_Task.lua.bytes` | `9ee7e0ab27b8b16d041cf456762e1840441ebd86ab937259b228a995741bae3e` |
| `Logic/Asset/Systems/LC_System_Task.lua.bytes` | `73bc027d03e01013ca4eef95c2ed25210d1cd35cdf2f815ff9c770eff1fa479f` |

OfflineLab 僅用於讀取「與上表 G payload 或 Proto 完全相同 SHA-256」所產生的靜態解析結果；不同雜湊的舊 D/歷史輸出沒有被提升為 G 版結論。

## 3. REA / IDA 證據索引

### Managed（精確 G `Assembly-CSharp.dll`）

| 證據 | Evidence ID | 觀察 |
|---|---|---|
| Managed 摘要 view | `ev_f5d426c9743ff95231974ffbb7b7c7a29d7bb72cb06094e495bbc7a7e4923979` | Unity Mono、CIL-only、metadata complete；TypeDef 2044、MethodDef 22758、Field 9681。 |
| Managed 原始父證據 | `ev_56c27f5819ce203aad8ef7b2796954226f874f34a23d28d094af1b1178524005` | 綁定 SHA-256 與 MVID。 |
| `LanguageMain` members view | `ev_e6f7b7983fbd87544630f861896e48af5672ab9a095574d8e90bea16c93295d3` | 11 fields、19 methods、65 call edges、51 user strings。 |
| `LanguageMain` members 父證據 | `ev_361c72d878320bd367b88e72817f2c2f7a14869789a10427aad10292300fe1a4` | 成員 token 僅在此 build 內有效。 |
| Managed members page | `ev_f051f3bdf606353c5d29f25b158f8c803e9c07ab7de3c4cddaa8e3513dd21105` | 名稱已混淆，不能把 token 當跨版本 API。 |
| Managed/native 邊界 | `ev_ca9786dd01cffc9239804b979570085d4bca41884f87951afcb8d9a80f140199` | 11 ModuleRefs（含 `xlua`）、369 P/Invoke、870 native/runtime implementations；無 mixed/native header、無 ReadyToRun。 |

### Native IDA（精確 G `xlua.dll`）

| 證據 | Evidence ID | 觀察 |
|---|---|---|
| 目標函式搜尋父證據 | `ev_ac7122aa2bbbadcd5d5b615ac2fba3984abdbb51855f6918da7f4138ac95e32d` | `luaL_loadbufferx`、`lua_pcall`、`xluaL_loadbuffer`、`xluaL_loadbuffer_0`。 |
| 搜尋摘要 | `ev_fced1f409455321c9170783b84e6257d5aa7a75e601ed266d8030755e1a40109` | 此次 focused search 的範圍摘要。 |
| 搜尋列 view | `ev_c5fff042c38a82fa0a36717ec7372a6494b1fa888e49e69154dc7e82c1cb7130` | 位址：`0x180001c3a`、`0x180001db1`、`0x180001e24`、`0x180043c80`。 |
| `xluaL_loadbuffer_0` pseudocode | `ev_b172a25988efff4859ee9d9d4aa8696a7ef7cb240fcaa0de9d3cf9974a37931e` | 轉送到 `luaL_loadbufferx(..., mode=0)`。 |
| `xluaL_loadbuffer` thunk | `ev_714f95f1e2748f46e9faae62890c99fabc8f4e1971bc0b17c1929af79da7c057` | 轉送 `xluaL_loadbuffer_0`。 |
| `luaL_loadbufferx` thunk | `ev_34681907ab4b71f2742af78dfa1f92b864d9b4fe969e8802b2833e1ae54702fd` | 轉送 `luaL_loadbufferx_0`。 |
| `lua_pcall` thunk | `ev_41977cf281450c790f5fe05c2eee1ebe5e4c5a3e0275e08ce09dc6b894fdc789` | 轉送 `lua_pcall_0`。 |
| `luaL_loadbufferx_0` pseudocode | `ev_54ff27f69dba63538c1a14b28546f486e276d1fde1c74fc0437b3839b7b15189` | 保存 buffer/length，呼叫 `lua_load(state, reader, ctx, chunkName, mode)`。 |
| `lua_pcall_0` pseudocode | `ev_dbf28e72640637a706145c026e9226d0fe42df522053fda952ee60684dca8238` | 轉送 `lua_pcallk(..., ctx=0, k=0)`。 |
| `xluaL_loadbuffer_0` xrefs | `ev_20c8dbb17f6db040ad5edfc30774eca9407492b9f225ba4a8347e608b8d6b7e2` | incoming code `0x180001e24`、data `0x1800cf2f0`。 |
| `luaL_loadbufferx_0` xrefs | `ev_d48a46d024d04172db1df669de79c70e6163ce8db7c9b2985ee7592c1829cb2c` | code `0x180001c3a`、data `0x1800cb6cc`。 |
| `lua_pcall_0` xrefs | `ev_516fca6c936bc1f783538b9224fba9d07e0c788368491e0eab66b759f1dedee9` | code `0x180001db1`、data `0x1800cf26c`。 |

IDA 結論僅是：`xlua.dll` 在此路徑提供一般 Lua 載入/呼叫橋接。任務 ID、opcode、payload schema 與 handler 位於 LuaPayload/Proto；不能從這幾個 native wrapper 推定某一條任務已在執行期正確完成。

## 4. 共用 opcode 與 payload

| Opcode | 名稱 | 方向/用途 | Payload |
|---:|---|---|---|
| `30403` | `CELL_DUNGEON_RES_NOTIFY` | 伺服器通知副本結算 | req `romsg.NullArg`; resp `romsg.DungeonResNotify` |
| `5027` | `AUTO_SYNC_CLIENT_ROMSG_DBTASKINFO` | 伺服器同步任務權威狀態 | req `romsg.NullArg`; resp `romsg.DBTaskInfoIncrement` |
| `31202` | `TASK_ACCEPT` | 客戶端接任務 | req `romsg.TaskAcceptReq`; resp `romsg.TaskAcceptResp` |
| `31205` | `TASK_DELIVERY` | 客戶端交付任務/領獎 | req `romsg.TaskDeliveryReq`; resp `romsg.TaskDeliveryResp` |
| `31214` | `TASK_TRIGGER_EVENT` | 客戶端回報互動型任務事件 | req `romsg.TriggerTaskEventReq`; resp `romsg.TriggerTaskEventResp` |

關鍵結構：

- `DungeonResNotify`: `dungeonEntry:int64(1)`、`res:DungeonRes(2)`、`data:DungeonResData(3)`、`rewards:map<int32,int64>(4)`、`auctionRewards:repeated int32(5)`；`DungeonResSuccess=1`。
- `TaskInfo`: `taskId:int64(1)`、`status:TaskState(2)`、`completeNum:repeated int64(3)`、`startTime(4)`、`follow(5)`、`limitStartTime(6)`、`isChapterLocked(7)`。
- `TaskState`: `None=0`、`CanAccept=1`、`Running=2`、`Finish=3`、`Delivery=4`、`Fail=5`。
- `DBTaskInfoIncrement`: `version(1)`、`syncAllMark(2)`、`infos:map<int64, infosFieldIncrement>(10)`；map value 的 oneof 是 `all:TaskInfo(1)`、`inc:TaskInfoIncrement(2)`、`del:bool(3)`。
- `TaskInfoIncrement.completeNum(30)` 的 oneof 為 `all` 或 `inc`，兩者皆攜帶 repeated int64 `value`。
- `TaskDeliveryReq`: `req:repeated TaskDeliverySingleReq(1)`；single req 是 `id:int64(1)`、`npcId:int64(2)`。
- `TaskAcceptReq`: `id:int64(1)`、`npcId:int64(2)`。
- `TriggerTaskEventReq`: `taskId:int32(1)`、`goalCount:int32(2)`。

## 5. 共用客戶端處理鏈

### 5.1 副本結算不是任務完成

1. `NW_GameServerHandler` 把 `CELL_DUNGEON_RES_NOTIFY` 註冊至 `_Dungeon_Message`。
2. `_Dungeon_Message` 將 `pbc_msg_name` 放入訊息後，派送 Scene message。
3. `LC_DunGeon_Base` 的同名 handler 派送 `LC_NotifyId.Scene.DungeonSettlement`，供結算 UI/場景消費。
4. 此鏈沒有修改 `TaskInfo.completeNum` 或 `TaskInfo.status`。

因此，只送 `30403` 可以讓玩家看到勝利/結算，但不足以讓任務真正完成。

### 5.2 任務權威狀態同步

1. `5027` 映射至 `DBTaskInfoIncrement`，資料域 `TaskInfo`、DAL `lc_dal_task`。
2. `LC_AssetManager` 依 full/increment 路徑呼叫 `AutoSyncAll` 或 `AutoSyncUpdate`。
3. `LC_DAL_Task` 更新 `AllTaskInfo.TaskInfos[id]`：
   - `Running` 轉換會派送 `TaskReceiveEvent`。
   - `Delivery` 會派送 `TaskSuccessfull`，並寫入 Guide 的 TaskFinish 值。
   - `Finish` 會派送 `TaskFinish`。
   - update-end 派送 `UpdateTaskInfoEvent`。
4. `LC_System_Task` 收到 `UpdateTaskInfoEvent` 後刷新地圖 NPC/任務追蹤 UI；收到 `TaskSuccessfull` 後呼叫 `OnSuccessTask`，再由 `LC_TaskHelper.DealAcception` 處理任務成功後的接受/執行動作。

### 5.3 客戶端請求形狀

- 接任務 `31202`: `{id:<taskId>, npcId:<NPC UUID 或空/0>}`。
- 交付 `31205`: `{req:[{id:<taskId>, npcId:<NPC UUID 或 0>}...]}`。
- 互動事件 `31214`: `{taskId:<interactionId>, goalCount:<requiredCount>}`。

`LC_TaskHelper.AddClientInteractionNumByTaskId` 先更新本機互動計數；所有目標達標時呼叫事件 builder 送 `31214`，之後刷新任務進度畫面。這個本機刷新不取代伺服器 `5027` 的權威狀態。

## 6. 三條任務的精確條件與 sequence

TaskConfig 的靜態 literal parser 狀態為 `PARTIAL_LITERAL_PREFIX_DYNAMIC_UNSUPPORTED`：共 57,513 instructions，於 PC `57482` 遇到未評估的 `CALL` 才停止；本節四個任務列位於 PC 約 `44629–46618`，均在停止點之前。解析沒有執行 Lua 或 native code，故是靜態值證據，不是 runtime 等價證明。

### 6.1 魔物來襲 `10102100`

TaskConfig：

- 前置：`10102090`
- `_kProgressTarget = [[101018, 1021301, 1]]`
- `101018 = CheckDungeonPass`
- 獎勵表：`31210`
- EXP：`[1, 50000]`
- 追蹤：`["142", "10213", "1010209001", "-1"]`

條件 sequence：

1. 伺服器副本腳本管理三波怪物並決定副本 `1021301` 是否成功。波數不在此任務列。
2. 成功結算可送 `30403`：`{dungeonEntry:1021301, res:DungeonResSuccess(1), ...}`，客戶端只開啟/刷新副本結算事件。
3. 伺服器必須另外送 `5027`，在 `infos[10102100]` 的 `all` 或 `inc` 中把第一個 `completeNum` 推進至至少 `1`，並把 `status` 推進至此任務適用的 `Delivery` 或 `Finish`。
4. 客戶端 DAL 更新資料、派送成功/完成事件並刷新任務 UI。
5. 若該任務需客戶端交付/領獎，客戶端送 `31205`：`{req:[{id:10102100,npcId:<UUID/0>}]}`，伺服器回覆並再同步最終任務狀態。

故障定位：三波打完後只出現結算或完全無後續時，優先查伺服器是否真的結束 `1021301`，以及是否對 `10102100` 送出正確 `5027`；不要只補 `30403`。

### 6.2 虎王 `10102200` 與後續 `10102210`

TaskConfig `10102200`：

- 前置：`10102190`
- `_kAcceptAction = [["3000", "1022001", "1", "10102200"]]`
- `_kProgressTarget = [[101018, 1022001, 1]]`
- `101018 = CheckDungeonPass`
- 獎勵表：`31220`
- 追蹤：`["142", "10220", "1010220001", "-1"]`

TaskConfig `10102210`：

- 前置：`10102200`
- `_kProgressTarget = [[103046, 40022001, 1]]`
- `103046 = CheckClientInteractionNum`
- `_kEXECAction = [["345", "1253018"]]`
- 獎勵表：`31221`
- 追蹤：`["142", "10220", "1010221001", "-1"]`

條件 sequence：

1. 接受 `10102200` 後，其 action 指向副本 `1022001`。
2. 成功結算可送 `30403`：`{dungeonEntry:1022001, res:1, ...}`；這仍只驅動結算鏈。
3. 伺服器另送 `5027`，把 `10102200` 的第一個 `completeNum` 推至至少 `1` 並推進 status；客戶端刷新/交付。
4. `10102200` 完成後，伺服器使後續 `10102210` 可接受/Running，並以 `5027` 同步。
5. 玩家完成互動 `40022001` 時，客戶端把本機計數推至 `1`，送 `31214`：`{taskId:40022001, goalCount:1}`。
6. 伺服器驗證互動事件後，對真正的任務 ID `10102210` 送 `5027`，使 progress/status 到達 Delivery/Finish；其後才執行 `345/1253018` 所代表的成功後動作。

注意：`31214.taskId` 在此流程是互動目標 ID `40022001`，不是任務列 ID `10102210`。伺服器若只用 `10102210` 查 `31214.taskId`，會漏掉事件。

### 6.3 凶惡的鳥媽媽 `10103120`

TaskConfig：

- 前置：`10103110`
- `_kProgressTarget = [[101018, 1051001, 1]]`
- `101018 = CheckDungeonPass`
- 獎勵表：`31312`
- EXP：`[1, 160000]`
- 追蹤：`["142", "10510", "1010311001", "-1"]`

條件 sequence：

1. 伺服器/副本腳本判定副本 `1051001` 的內部目標是否完成。
2. 成功可送 `30403`：`{dungeonEntry:1051001, res:1, ...}`，驅動副本結算 UI。
3. 伺服器另送 `5027`，把 `10103120` 第一個 `completeNum` 推至至少 `1` 並推進 status。
4. 客戶端 DAL 刷新任務 UI；需要交付時再送 `31205`。

此任務列沒有「兇殘的大嘴鳥 ID + 擊殺 N 隻」的 TaskConfig 條件；它只有 `1051001` 通關一次。若遊戲設計要求殺特定數量，數量只能位於副本 `1051001` 的伺服器/關卡腳本或其他資源，現有證據不能臆造。

## 7. 伺服器修正驗收條件

目前伺服器端已加入客戶端資料測試，覆蓋 `1021301/10102100 -> 10102110`、
`1022001/10102200 -> 10102210`、`1051001/10103120 -> 10103130`，並另測虎王後續在公共地圖
`10220` 送出 `31214 {taskId:40022001,goalCount:1}` 後交付 `10102210`、自動接取 `10102220`。本輪定向
結果為 4 passed、0 failed、0 skipped。這證明目前伺服器的 callback、5027 任務狀態與 31214 後續鏈可由
測試重現；它仍不等同玩家實際 G 客戶端同一回合的畫面與網路驗收。

每條任務至少需要同一回合的下列證據，才能把本報告由 `PARTIAL` 升為完整驗收：

1. 玩家進入指定副本前的 TaskInfo 快照（task ID、status、completeNum）。
2. 副本成功後實際送出的 `30403` payload。
3. 同回合實際送出的 `5027` payload，明確包含正確 task ID、`completeNum` 與 status。
4. 客戶端收到後的 TaskInfo/UI 狀態。
5. 如需交付：`31205` req/resp 與最終 `5027`。
6. 虎王後續另需 `31214 {taskId:40022001,goalCount:1}` 與其後 `5027` 更新 `10102210`。

建議伺服器端加上可重現、可關閉的任務 trace，至少記錄：`account/role`、`questId`、`dungeonEntry`、`opcode`、`before/after status`、`before/after completeNum`、`result`，避免只由畫面判定。

## 8. UNKNOWN 與邊界

- `UNKNOWN`：尚未捕捉 G 客戶端連目前伺服器的同一回合封包，故 `30403` 與 `5027` 的實際到達順序、延遲、重送與 payload 值仍未驗收。
- `UNKNOWN`：魔物來襲三波的生成/清場條件、虎王實體死亡條件、鳥媽媽副本內部擊殺條件不在三個 TaskConfig 任務列；需查伺服器副本邏輯或做受控 runtime trace。
- `UNKNOWN`：三條任務在目前伺服器最終應直接進 `Finish`，或先進 `Delivery` 再由 `31205` 完成，取決於伺服器狀態機與交付類型；靜態客戶端鏈只證明兩種狀態都會觸發不同事件。
- `UNKNOWN`：`_kEXECAction` 的 action code `345` 在執行期對物件 `1253018` 的完整副作用，尚未以同一回合 runtime 驗收。
- `UNKNOWN`：IDA provider 無法保證所有間接呼叫與函式邊界完整；`xlua.dll` 證據只能證明一般 Lua bridge，不是三任務 runtime 行為。
- Managed 成員名稱已混淆；REA 的 token 是 SHA-256 + MVID 綁定的 build-local 座標，不能當作跨版本原始碼名稱。
- TaskConfig parser 是全檔 `PARTIAL`，但本報告四個任務列位於 dynamic stop 之前且已靜態回收；這不代表整個 TaskConfig 已完整反編譯。
- 本次沒有啟動/附加遊戲、沒有修改 G 客戶端、沒有修改或重啟正式伺服器。

## 9. 最小修正原則

伺服器端針對三條任務應採用「副本成功 + 任務權威同步」雙鏈：

```text
dungeon success
  -> 30403 DungeonResNotify (settlement/UI)
  -> mutate authoritative quest progress/status
  -> 5027 DBTaskInfoIncrement (task/UI state)
  -> optional 31205 delivery round-trip
```

虎王後續另加：

```text
client interaction 40022001
  -> 31214 {taskId:40022001, goalCount:1}
  -> server resolves it to quest 10102210
  -> 5027 updates quest 10102210
```

不要以只補 `30403`、只在客戶端本機加 progress、或用錯 `31214.taskId=10102210` 取代上述權威流程。

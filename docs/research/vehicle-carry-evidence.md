# 載具乘客掛載契約

來源為 Genesis 2026.10.30.1 的 Lua、protobuf 與已停止的原服任務錄製。原件與擷取資料不納入版本庫。

## Deniro 螞蟻任務

`NPCInteractConfig` 的 5300005 進入 Vehicle 狀態；`VehicleSeatConfig` 的座位偏移以公分儲存。位移通知與互動狀態並不足以建立騎乘，還需要獨立的乘客掛載資訊。

原服可觀察到的必要順序：

1. `32702 InteractChangeNtf`：載具進入 Vehicle。
2. `31081 MovementChangeNtf`：玩家 `MoveTransBeCarriedMove`，顯示位置加上座位偏移。
3. `31044 AoIInfoSingle`：`carry_info.be_carried.id` 指向載具，`carry_offset` 指定座位偏移。
4. `32702 InteractChangeNtf`：玩家進入 Vehicle。
5. `32700 NpcInteractionRsp`：互動成功。
6. `31036 AoIInfoTotal`：載具的 `add_carries` 加入玩家。

原服在第 3、4 步間另有一筆沒有冷卻列的 `32705 NpcSkillsCdNotify`。目前修正的是乘客掛載與生命週期；不宣稱已重現所有狀態位元或未觀察到的分支。

下車順序為載具 `32702 Idle`、玩家 `31044` 的 `be_carried.id=0` 與空 `carry_offset`，再送玩家 `32702 Idle`。空偏移物件必須保留，不能以完全缺少 `be_carried` 取代解除操作。

## 客戶端消費路徑

- `Logic/Scene/LC_Scene_Sync._syncSelfAOIInfo` 為 31044 補入本地玩家 UID，再將 `carry_info` 交給載具處理。
- `Logic/Vehicle/LC_VehicleHelper.ProcessVehicle` 只有在 `be_carried.id > 0` 時才建立 `VehiclePassenger` 並呼叫 `EnterVehicle`；ID 為零則解除。
- `Logic/GamePlay/LC_GamePlayStatusMgr.EnterVehicle` 需要乘客元件存在，才會進入互動狀態。這是掛載資訊必須先於玩家 Vehicle 通知的原因。
- `Logic/Unit/Component/Vehicle/LC_PassengerComponent` 依座位設定掛到骨骼、播放上下車與乘坐動畫，並在進入動畫結束後切換 3C。若載具尚未載入，`EnterVehicle` 設定 `m_bCache`，由 `EnterVehicle_Cache` 等待載具與玩家完成載入。

`all_carries` 的新觀察者快照與 `remove_carries` 清理，沿用客戶端既有契約及伺服器載具同步模式，屬於推導的一致性處理；這次原服樣本沒有晚進 AOI 或旁觀者下車紀錄。測試驗證協定順序與伺服器清理，不能代替原生動畫、碰撞、天花板行走或玩家端完成任務驗收。

31053 同時觀察到 `eObjectStateVehicleDriver` 的增加與移除；其餘狀態包含角色既有旗標，尚未完整追溯所有權，不應把整組位元硬寫回所有角色。

## v1.0.25 移動檢查相容

新版預設 `WalkCheck=enforce`。私人真實匯出測試確認 Deniro 的劇情上車點（69, 251, 292）附近沒有地面導航格；乘客再加座位高度 2.8。此場景由 `NoviceDungeonReverseDungeonID` 啟用 `moveOnSurface`，客戶端 `LC_UnitBase`／`LC_Movement_Base` 沿模型表面移動，不能以地面格一概拒絕（既有 `SceneService.MovesOnSurface` 已列原件依據）。

例外同時要求表面場景與仍有效的 `AntStoryMovementSpeed` 授權，僅略過地面導航格；移動距離預算與空氣牆判定仍執行。離開座位後授權消失，同位置短步重新由一般導航判定拒絕。此處沒有宣稱完整重建客戶端表面碰撞。

LAB 驗證涵蓋真實劇情滑行與騎乘的短步、1000 公尺偽造步拒絕、退出座位恢復導航限制，以及既有 12／30 m/s 共用預算測試。玩家端畫面與完整表面碰撞驗收仍為 `NOT_RUN`。

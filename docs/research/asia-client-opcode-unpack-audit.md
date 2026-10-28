# 亞服客戶端 Opcode 與保護層稽核

本文件記錄 2026-10-28 對 Asia client artifact `2000000052` 的唯讀靜態稽核，以及可安全併入伺服器的技能呈現封包驗證。這不是官方伺服器封包擷取，也不把靜態反編譯結果當成遊戲內驗收。

## 原檔與隔離範圍

分析只對雜湊相同的 LAB 複本進行。開始與結束時的來源 SHA-256 相同：

| 檔案 | SHA-256 |
|---|---|
| `ro3.exe` | `F5F73D8616A2500E0FB0223D83774E2D7F6A74C1BBDAD772F4FEFC9BF5812036` |
| `UnityPlayer.dll` | `DC005461655B67912029EEA6BCC303FE244C3A336E2046107F934B901AB9CE2B` |
| `Assembly-CSharp.dll` | `4365BCCFB4BADBF9D73C39CD8491F8BCFAF7604FC09C91DBC71123B100602E11` |
| `Happyengine.Network.dll` | `DB0FDFE66CD0217592A7B21D79213947E264862852BD89132B968A08CBE03308` |
| `xlua.dll` | `C189E0FDFB23782D381DFD761A4D746C1E195508ED8EECBE25F23A39BFC7309A` |
| `proto.pb.bytes` | `B5CF437794324A834854115CF5F69FF1A738E79E16228C0D42BD9D95C02DEA00` |

## REA／IDA 結論

- `ro3.exe` 的 `WinMain` 直接進入 `UnityMain2`，沒有找到 Opcode 分派或外層解密器。
- `Assembly-CSharp.dll` 可直接讀取完整 Mono CIL metadata（2,044 個 TypeDef、22,758 個 MethodDef），不需要先脫殼。
- `xlua.dll` 的載入鏈為一般的 `xluaL_loadbuffer -> luaL_loadbufferx -> lua_load`；沒有找到可證實的 XXTEA、AES 或自訂 Lua payload 解密路徑。
- 三個 native 檔案的 PE sections、imports、TLS 與 entropy 都沒有形成可確認的 VMP／packer 證據。LAB 的 bounded VMP 嘗試沒有產生可用輸出，因此 `unpacked_artifact = NONE`，不得解讀成「已成功脫殼」。
- 可讀的 Opcode 真實來源是 Managed 網路橋接、Lua `NW_GameServerHandler`／`LC_Scene_Sync` 與 protobuf descriptor。現有靜態 catalog 共有 1,045 列，其中 1,012 筆有 protobuf map：316 筆 S2C notify、43 筆 C2S only、651 筆 request/response、2 筆 null/null；每列仍須依方向及實際 producer 分別驗證。

因此本版應採「直接還原可讀協定層」，而不是繼續對 Unity bootstrap 強制脫殼。`NO_CONFIRMED_VMP` 只表示本輪沒有確認外殼，不是對所有未命名或 inline transform 的絕對否定。

## 技能呈現 Opcode 對照

| Opcode | protobuf | client consumer | server 狀態 |
|---|---|---|---|
| 31100 | `UseInSkillInfo` | `_useInSkill` | 一般施放 producer 已存在並有 cast-to-hit wire test |
| 31106 | `UseInSkillInfo` | `_useInSkill` | 高階施放 producer 已存在並有 boss wire test |
| 31121 | `UseInSkillInfo` | `_extraUseInSkill` | 額外施放 producer 已用於守護寵物並有 wire test |
| 31107 | `UseInSkillInfo` | `_extraUseInSkill` | 高階額外路由已存在；本輪新增獨立 opcode 選路與反向排除測試 |
| 31101 | `SkillHitInfo` | `_skillHit` | 命中 producer 已存在並有傷害 wire test |
| 31120 | `SkillFixTimeBulletNotify` | `_skillFixTimeBullet` | 一般 server-timed bullet producer 已用於火箭術／火球術並有欄位測試 |
| 31108 | `SkillFixTimeBulletNotify` | `_skillFixTimeBullet` | 高階 bullet 路由已存在；本輪新增獨立 opcode 選路與完整欄位測試 |
| 31119 | `SkillEffectDisplayNotify` | `_skillEffectDisplay` | typed send API 可用；本輪新增九欄位 wire test，但沒有已證實的遊戲 producer |
| 31109 | `SkillEffectDisplayNotify` | `_skillEffectDisplay` | 高階 typed send API 可用；本輪新增九欄位 wire test，但沒有已證實的遊戲 producer |

31106／31100、31107／31121、31108／31120、31109／31119 分別是高階與一般 Opcode；每一對在 client 都共用同一 consumer。高階 Opcode 不是要求伺服器再送一份相同封包。

`SkillEffectDisplayNotify` 的 client contract 包含 `sourceId`、`targetId`、`effectId`、`targetPos`、`effectType`、`angle`、`masterId`、`is3DPosition` 與 `skill_id`。`effectType` 會選擇 Gfx、音效、鏡頭震動、警示區域、鎖定目標、Fresnel、掛件或寵物顯示等不同處理器。enum 雖然另有 `AnimGroup = 1`，但此 build 的 `EffectHandler` 沒有註冊該處理器，因此 31119／31109 也不能當成通用角色攻擊動畫封包。只知道 Opcode 與 schema，不能推導某技能何時必須產生哪個效果。

一般技能的角色動作、Gfx 與音效多半已由 `m_kShows` 時間軸播放。若將 31119／31109 無條件加到每次施放，client 會有重複動作或特效的風險。因此目前只驗證 bounded send API 與 wire shape，沒有建立臆測性的全技能 producer。

## 本輪新增驗證

`SkillCombatTests.ProtocolRoutes.cs` 新增兩項整合測試：

1. 高階額外施放只送 31107，高階 server-timed bullet 只送 31108，且不誤送 31100、31106、31121 或 31120。
2. 31119／31109 均能保留 `SkillEffectDisplayNotify` 的九個 client 欄位，並各自只送一次。

這些測試補上既有路由的 wire coverage；它們不宣稱已還原 31119／31109 的官方觸發規則。

## 尚待 runtime 證據

要把 31119／31109 或額外 proc 的 31121／31107 接到實際技能，仍需同一版本官方亞服的合法觀測序列，至少包含：

- 技能 ID、施放者／目標與同一施放 session；
- `31100/31106`、可選 `31120/31108`、`31101` 的順序與時間；
- 若真的存在，`31119/31109` 的九個欄位，或 `31121/31107` 的獨立時間軸；
- 對應技能的 `m_kShows` 與 EffectData，確認補送後不會重播。

在取得該證據前，31119／31109 的遊戲 producer 維持 `UNKNOWN`；靜態 client consumer、合成 wire test 與 server API 都不能替代玩家端同版本驗收。

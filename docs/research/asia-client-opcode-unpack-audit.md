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
- 可讀的 Opcode 真實來源是 Managed 網路橋接、Lua `NW_GameServerHandler`／`LC_Scene_Sync` 與 protobuf descriptor。`tools/opcode-matrix.js` 的全量交叉矩陣顯示：descriptor 的 `romsg.MSG_Type` 有 1,031 個值（含 `TT_None`），PB Lua map 有 1,012 筆（316 筆 S2C notify、43 筆 C2S only、651 筆 request/response、2 筆 null/null），另有 14 筆 EZ 與 64 筆 AutoSync overlay，完整 Lua schema map 為 1,090 個唯一 ID，numeric union 為 1,109 列。1,696 個 request／response root 遞迴展開為 3,789 個 message 與 177 個 enum，descriptor closure 的未解析型別引用為 0。先前 1,045 是 `1,031 descriptor 值 + 14 EZ`，不是完整 runtime schema map；每列仍須依方向及實際 producer 分別驗證。生成矩陣含客戶端衍生資料，只保留在被忽略的本機 `docs/artifacts/`，不進 Git。

因此本版應採「直接還原可讀協定層」，而不是繼續對 Unity bootstrap 強制脫殼。`NO_CONFIRMED_VMP` 只表示本輪沒有確認外殼，不是對所有未命名或 inline transform 的絕對否定。

矩陣另交叉核對 113 個 `NW_GameServerHandler` callback、59 個 Scene consumer 與 AutoSync DAL。19 個
descriptor-only 值可分為 13 個範圍 sentinel、4 個 Managed Chat、`84072` 的 paired response enum，以及
缺少 Lua route 的舊／外部 `1008`。37 個 Lua rows 共有 49 個 request／response 缺失端：14 個是 registry
直接寫入的 placeholder（`romsg.` 2、`romsg.null` 11、`ezmsg.NullArg` 1），另有 35 個具名端／34 個唯一
型別真的沒有 schema。34 個名稱在 Asia 6,001-message descriptor、Genesis 5,990-message descriptor、D 槽
28 份 descriptor 複本、259 個歷史 ZIP、Managed assemblies 與三份本機 repo history 的恢復數都是 0；
2026-10-13 舊稽核的 49 筆也與本輪逐筆相同。這些缺口保留原始名稱與來源行，不以猜測欄位補成可用
封包；唯一同時有明確 callback 與 Scene consumer 的具名缺口是 `31022 SyncMoveStateNotify`，維持高優先
`UNKNOWN`。

## Managed Chat 與模擬器私有 Opcode

亞服 descriptor `B5CF4377...DEA00` 新增官方 `77010–77013`：`MANAGED_CHAT_BOOTSTRAP`、
`MANAGED_CHAT_CREDENTIAL_REFRESH`、`MANAGED_CHAT_SEND`、`MANAGED_CHAT_CHANNEL_STATE_NTF`，以及 11 個
`ManagedChat*` message 與四組 enum。Genesis descriptor `F80474D8...95664` 尚未包含它們。舊版 RO3Emu
卻把 `77010–77013` 當成聊天 tunnel、診斷與玩家回報，會與亞服官方命名空間碰撞。

本輪把九個模擬器專用訊息集中到 `PrivateMessageIds` 的 `0x7F52_0001–0x7F52_0009`。ClientHooks 的
`NW_CBBNetworkManager.Obf_jBd(uint, byte[])`、server `MessageRoutes` 與 `GameSession.SendGameRawAsync(uint, ...)`
都保留完整 uint32 id；這個 block 低於 `Int32.MaxValue`，也不在 Asia／Genesis descriptor、Lua protobuf、
EZ protobuf 或 AutoSync catalog 的 1,109 個 numeric union 內。測試另鎖定 id 唯一、與 checked-in client
catalog 無交集，且不落在官方 `77000–77099` 範圍。

以 `tools/ProtoDump` 從同一亞服 descriptor 重建的 144 份 proto，與 server proto 比對達到 144/144
schema parity；唯一文字差異是 `MG_Chat.proto` 未保留未使用的 `import FieldOption.proto`，不影響 descriptor
語意。這證實可以安全加入 Managed Chat 的序列化型別；但沒有官方 runtime trace 能證明 provider、驗證、
重試或 channel state 的伺服器觸發規則，因此 `77010–77013` 目前只保留官方 schema，沒有猜測 handler 或
producer，也不能把 RO3Emu 自訂 chat tunnel 當成官方 Managed Chat 實作。

## 傳輸層與 Opcode 封裝

Managed CIL 顯示 TCP 與 KCP 最後都把相同 byte stream 交給 `RpcChannel`。每個 Phonest frame 是
`int32_le(length = 2 + protobuf bytes) | uint16_le method | protobuf`，最大 body 16,777,216 bytes；出站轉換
順序為 stateful zlib `Z_SYNC_FLUSH` 後 RC4，入站則先 RC4 再 stateful zlib。握手以 method 1 要 token、
method 10 回 token；client 將 16 個 random bytes 做 SHA-1 得到 20-byte session key，以 RSA-OAEP-SHA1
送 method 2，method 11 是啟用轉換後的第一個 ACK。此 active path 沒有找到額外 app CRC 或 HMAC。

Game client→server 的 `EntityMessage` 使用外層 method 4，server→client 使用 method 2；沒有 named RPC 時，
`parameters` 前四 bytes 是 little-endian uint32 game opcode，其後為原始 payload。一般 game gate C→S method
為 1 token、2 key、3 connect、4 entity、5 channel；S→C method 1–20 的型別均已由 client stub 解出。
Micro gate 另使用 33 request、34 response、35 push。這些 method index 是 Phonest RPC 層，不能與 1,109
列 game opcode 矩陣混成同一層。

主要 REA evidence IDs：RPC frame `ev_1edaf9959631101c267b45675c3001e317c9f2a58032bc25642754bb079af298`；
game envelope `ev_121f08d8efc91d1fffc97921881fb3ee6f980b87fee9db77072d0fcae5f05b77`；batch
`ev_044540dddb583cb2b09982c0654efd86b4286a089dcb48718fe1b4e1c023c1c9`；Lua bridge
`ev_3ad1dbcb274bc8f9f4d99eb89d932481500aa4956d41d915967b8dc1124a4559`。Server 的核心 TCP、
RSA/zlib/RC4、單包 EntityMessage 與 micro 33/34/35 路徑相符；KCP server、部分不使用的 gate method
producer，以及 game gate method 19/20 的 runtime 可達性仍是 `PARTIAL/UNKNOWN`。Client 具有 KCP 建連
失敗後切回 TCP 的明確路徑。本輪未啟動 client，因此這些靜態結果不等於 live handshake 或 gameplay
驗收。

## 外層 Opcode 2 批次封裝

可讀的 `NW_CBBNetworkManager.OnMsgReceived` 路徑顯示，S→C 的外層 `msgId == 2` 不是
`COMMON_ERROR_NTF` 本體，而是批次容器。容器內每筆為 `int32BE payloadLength` +
`int32BE innerOpcode` + `payload`；`payloadLength` 只計 payload，不含 8-byte header，零長度
payload 合法。`COMMON_ERROR_NTF` 的官方路徑是 outer `2` 內含 inner `2`。C→S 沒有這個
批次層，不得將所有送出訊息一律批次化。

伺服器原本直接送出 `[uint32LE 2][CommonErrorNtf]`，客戶端會把 protobuf 開頭誤讀為
批次長度。本輪將 `GameSession` 與 `PreparedMessage` 統一改用單筆批次編碼，並讓
`BotClient` 按相同 contract 解包。位元組測試釘住空／非空 CommonError payload，原有非 2
封包保持不變。此結論來自靜態反編譯；玩家端 runtime 驗收仍是 `NOT_RUN`。

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

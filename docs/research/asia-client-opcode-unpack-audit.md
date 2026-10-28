# 亞服客戶端 Opcode 與保護層稽核

本文件記錄 2026-10-28 使用 REA 6.8.0／IDA 對指定的 G 槽 Asia client artifact
`2000000052` 的唯讀靜態稽核，以及可安全併入伺服器的技能呈現封包驗證。這不是官方伺服器封包擷取，
也不把靜態反編譯結果當成遊戲內驗收。

## 原檔與隔離範圍

分析只對雜湊相同的 LAB 複本進行。開始與結束時的來源 SHA-256 相同：

| 檔案 | SHA-256 |
|---|---|
| `ro3.exe` | `F5F73D8616A2500E0FB0223D83774E2D7F6A74C1BBDAD772F4FEFC9BF5812036` |
| `UnityPlayer.dll` | `DC005461655B67912029EEA6BCC303FE244C3A336E2046107F934B901AB9CE2B` |
| `Assembly-CSharp.dll` | `4365BCCFB4BADBF9D73C39CD8491F8BCFAF7604FC09C91DBC71123B100602E11` |
| `Happyengine.Network.dll` | `DB0FDFE66CD0217592A7B21D79213947E264862852BD89132B968A08CBE03308` |
| `Phonest.Proto.dll` | `BEDF8FF6299D5641386EB80C80C7586CD5C898BC4F18A083B5C0C0D6C1644033` |
| `Phonest.Client.dll` | `ACCBC1315E405F2E0DD333016ADE6FAABB05B7C792AB3D8B6DABDFCAC93315B3` |
| `Phonest.Common.dll` | `936AFD0B4D1C196E75E1C85A8FD44647B8FE00D21D577D1F0FAA8FE15947B212` |
| `xlua.dll` | `C189E0FDFB23782D381DFD761A4D746C1E195508ED8EECBE25F23A39BFC7309A` |
| `ikcp.dll` | `8C3570628E9E3DD79199D8FCC0E2DB88C9DB04CA323B2D317242308928F288BA` |
| `proto.pb.bytes` | `B5CF437794324A834854115CF5F69FF1A738E79E16228C0D42BD9D95C02DEA00` |

同名 D 槽 LAB 的 `Phonest.Common.dll` 為 `33B55F69...D16DAA`，並非這次指定的 G 版原檔。因尚未逐方法
證明兩版等價，新傳輸清冊會拒絕 D 版雜湊，不把 G 版語意靜默套用到另一個 build。

## REA／IDA 結論

- `ro3.exe` 的 `WinMain` 直接進入 `UnityMain2`，沒有找到 Opcode 分派或外層解密器；imports 與 WinMain 的
  IDA evidence 分別為 `ev_917b8ae4be45399f9c969625b6f5f47a259dab85bbc5036fa9cbb84a9b1232c8`、
  `ev_79d935eff4855ccf667699a09ec7564078c126eb4d8aab1222719ae6f7d51239`。
- `Assembly-CSharp.dll` 可直接讀取完整 Mono CIL metadata（2,044 個 TypeDef、22,758 個 MethodDef），不需要先脫殼。
- `xlua.dll` 的載入鏈為一般的 `xluaL_loadbuffer -> luaL_loadbufferx -> lua_load`；沒有找到可證實的 XXTEA、AES 或自訂 Lua payload 解密路徑。IDA 另確認 `luaopen_pb_0` 註冊 `load/encode/decode/types/fields`
  等 39 個 API，`pb.load` 從外部 byte slice 載入 runtime descriptor，而非把 34 個缺失型別編進 DLL。
  相關 evidence：註冊表 `ev_9616c3c036ed13f5e903bd26f2e57b9cc111e90805e16917636f3de1e9beefab`、
  `pb.load` `ev_85087c50d4a5ec8abf0b028b52387f4f514ada6e6790637825159534fd60156f`、descriptor parser
  `ev_6aa3d256b93b523977308f7d972e3235162203aa645d74489a2268fd541224c0`、34-name negative search
  `ev_932909c14825d5440a25fa9b99ab47531e21dc2202cd631823525878f1ff85c0`。
- 四個 native 檔案的 PE sections、imports、TLS 與 entropy 都沒有形成可確認的 VMP／packer 證據。LAB 的 bounded VMP 嘗試沒有產生可用輸出，因此 `unpacked_artifact = NONE`，不得解讀成「已成功脫殼」。
- 可讀的 Opcode 真實來源是 Managed 網路橋接、Lua `NW_GameServerHandler`／`LC_Scene_Sync` 與 protobuf descriptor。`tools/opcode-matrix.js` 的全量交叉矩陣顯示：descriptor 的 `romsg.MSG_Type` 有 1,031 個值（含 `TT_None`），PB Lua map 有 1,012 筆，另有 14 筆 EZ 與 64 筆 AutoSync overlay，完整 Lua schema map 為 1,090 個唯一 ID，numeric union 為 1,109 列。將 `romsg.null` 與 `ezmsg.NullArg` 兩個歷史空封包別名納入方向判定後，全矩陣為 653 筆 request/response、43 筆 C2S only、392 筆 S2C notify、2 筆 null/null、19 筆 descriptor-only／未知方向。1,696 個 request／response root 遞迴展開為 3,789 個 message 與 177 個 enum，descriptor closure 的未解析型別引用為 0。先前 1,045 是 `1,031 descriptor 值 + 14 EZ`，不是完整 runtime schema map；每列仍須依方向及實際 producer 分別驗證。生成矩陣含客戶端衍生資料，只保留在被忽略的本機 LAB，不能進 Git。

因此本版應採「直接還原可讀協定層」，而不是繼續對 Unity bootstrap 強制脫殼。`NO_CONFIRMED_VMP` 只表示本輪沒有確認外殼，不是對所有未命名或 inline transform 的絕對否定。

### 2026-10-29 延續驗證與空封包別名修正

以相同 G 版雜湊重新執行 opcode、wire closure 與傳輸清冊，輸入與 2026-10-28 鎖定值一致；重新生成的
`msgtable.tsv`、修正前 opcode matrix、transport matrix SHA-256 分別為
`79E7937FA97F099FE3092C1F8B29422CAFD063B5C08282014AEB3E868914EE72`、
`C5CCB90284EBABEC12E4B7272A6C84C160E76A5A84AC1BDDF4C694F3AFA9EAF3`、
`37EE0E6903C3407E2DDE353F91C6C1F0FD195341076C57008BD29AD817C19E18`。這證明本輪是在同一輸入 epoch
延續，不是拿 D 版 `Assembly-CSharp.dll` 的不同 build 混用。

registry 中有 11 筆 `romsg.null` 與一筆 `ezmsg.NullArg`。它們沒有 descriptor，因此舊方向函式把 12 筆
S2C notify 誤列為 request/response。名稱、對端 payload 與 notify consumer 一致證明它們是
`romsg.NullArg` 的空 request 別名；方向修正後 S2C 由 380 增為 392，request/response 由 665 降為 653，
其他類別不變。這 12 筆仍保留 `noncanonical_null_alias` anomaly，因為 registry 與 descriptor 的版本偏差
本身仍需可見，不能假裝 schema 已補齊。

server `MsgCatalog` 現在保留原始 request／response type name，並將三種空參數標記一致處理。空 request 的
S2C notify、空 response 的 C2S one-way 以及 null/null 都不再由 unrouted fallback 建立假回覆；只有真正的
Game Gate request/response 且兩端 descriptor 都存在時才建立空 response；14 筆 `ezmsg` service row 也
不會再被 Game Gate fallback 當成一般遊戲請求。這修正的是 wire domain、方向與 fallback 行為，沒有
發明任何缺失欄位；request 或 response 任一端缺 descriptor 的 incomplete row 也會 fail closed，不建立
猜測回覆。Node 方向測試與合成 msgtable 的 server 單元測試均已釘住三種空參數拼法。

另以 49 個 schema 缺失端名稱對 G 版完整 `StreamingAssets` 做 bounded plaintext scan，結果為 0 命中。
這只排除已掃描檔案中的明文名稱，不能排除壓縮 bundle、計算生成字串、外部正式伺服器 schema 或 runtime
封包；34 個具名缺失 schema 與 `31022` 欄位 tag 因此仍維持 `UNKNOWN`。

`UnityPlayer.dll` 的本輪 IDA 冷開啟超過 300,000 ms registration deadline，沒有產生完整 IDA 字串 Evidence；
因此不能宣稱整個 Unity engine 已完整反編譯。兩次精確 close 都回 `cleanup_incomplete`，尚無法證明該次
IDA 私有暫存工作區已釋放。依 REA
recovery ladder 已停止重跑。四個原生檔的 ASCII／UTF-16LE bounded raw scan 對 34 個缺失型別均為 0 命中；
這能排除已檢查映像中的明文名稱，不能排除外部資源、計算生成字串或正式伺服器 schema。

`ikcp.dll` 的 IDA 結果另恢復出客製 KCP 段 header，但它屬傳輸層而非 game opcode：固定 18 bytes，依序為
`uint32 conv`、`uint8 cmd`、`uint8 wnd`、`uint32 ts`、`uint16 sn`、`uint16 una`、`uint16 payload/ack-list
length`、`uint16 RDC/extra length`，其後才是 payload。接受命令為 `0x51..0x57`：reliable PUSH、ACK、
probe ask、window tell、aggregated ACK list、名稱仍 `UNKNOWN` 的 RDC/extended ACK control，以及 unreliable
PUSH。主要 evidence：exports `ev_bfba9d96d60c48d5a79b818427c8a4b0c0d8989e7bd45ff8c2be07b591f8bd49`、
`ikcp_input` `ev_6a94ae399b3c1274d7d37e7eac25e57293ab6a0d61e8957e02e1c0cd0e0bf00b`、`ikcp_flush`
`ev_1e208afa819fb1aa9e290879460321546b604772d0e3cce7f2e5a8c19ab46de3`。此層沒有額外發現應用層
game opcode。

矩陣另交叉核對 113 個 `NW_GameServerHandler` callback、59 個 Scene consumer 與 AutoSync DAL。19 個
descriptor-only 值可分為 13 個範圍 sentinel、4 個 Managed Chat、`84072` 的 paired response enum，以及
缺少 Lua route 的舊／外部 `1008`。37 個 Lua rows 共有 49 個 request／response 缺失端：14 個是 registry
直接寫入的 placeholder（`romsg.` 2、`romsg.null` 11、`ezmsg.NullArg` 1），另有 35 個具名端／34 個唯一
型別真的沒有 schema。34 個名稱在 Asia 6,001-message descriptor、Genesis 5,990-message descriptor、D 槽
28 份 descriptor 複本、259 個歷史 ZIP、Managed assemblies 與三份本機 repo history 的恢復數都是 0；
2026-10-13 舊稽核的 49 筆也與本輪逐筆相同。這些缺口保留原始名稱與來源行，不以猜測欄位補成可用
封包；唯一同時有明確 callback 與 Scene consumer 的具名缺口是 `31022 SyncMoveStateNotify`，維持高優先
`UNKNOWN`。

REA 另對 34 個缺失具名 schema 做完整名稱查核：在 `Assembly-CSharp.dll`、
`Happyengine.Network.dll`、`Phonest.Proto.dll` 都是 34/34 unmatched，完整 `Managed\*.dll` 位元組掃描亦為
0 命中。完整 13,486 個反編譯 Lua 中，34/34 完整型別名稱只出現在 `NW_Helper_PBDefine` registry，非 registry
完整名稱命中為 0/34；12 份歷史 PBDefine 也全是相同 SHA-256 `C00658C...39092C`。

`31022 SyncMoveStateNotify` 的 Siege wrapper 可讀到部分欄位輪廓：`move_states[]`、`entity_id`、
`move_state_info.move_type`、`current_position`、`face`，之後呼叫 `_syncMoveState`。但整套 Lua 沒有該函式
實作，亦沒有 field tag、wire type、cardinality 或 nested message 型別，所以只能列為 partial field-name
shape，仍不能從名稱反推出可序列化 protobuf。主要 REA evidence 為 Managed 名稱查核
`ev_2c69d5e5054f076e8fa3652c2412ba45906228951de1858b494f6b8cb72d6cbc`、
`ev_167c141d3f55423eb455ab2e6edd03ab58577123def6a7a0d5c0ebff2f61cb12`、
`ev_857b3934bb2cfcc30011c2112a34e1cc65971bc3dbd494b6084b015d1ebd30b3`，Managed bridge
`ev_35133587c93d70b62abb844b91587d2fecdfd4e021294987a043d6c08c440ccc`。

`LC_AssetDatasetDefine.lua` 另提供 762 個 symbolic mapping，現有 1,109 列矩陣中有 685 列可連到 DAL。
其中 8 個 schema-incomplete opcode（`32747`、`33402`、`82506`、`82507`、`82974`、`83078`、`83244`、
`84073`）仍有 generic consumer mapping；原始文字共 9 個命中，因 `82974` mapping 重複。這只補強
consumer metadata，中央 protobuf decode 仍缺 schema，不能據此填入欄位編號。生成器現會輸出
`asset-dataset-consumer-registry.json` 並在每列附上 DAL mapping。

因此 37 個 schema-incomplete rows 中，共有 9 列可證明具體 downstream consumer：上述 8 個 AssetDataset
mapping，加上 `31022` 的 Scene `_syncMoveState` consumer。這個 8+1 計數不包含只有 callback、沒有下游
consumer 的 `31110`／`31111`。

四個 AutoSync (`5038`、`5042`、`5054`、`5063`) 雖會通過通用 parse/cache，但
`LC_AssetAutoSyncDataDefine` 沒有 DAL mapping，`LC_AssetManager.AutoSync` 因而直接返回；本 build 沒有下游
DAL dispatch，正式伺服器是否仍會傳送仍是 `UNKNOWN`。

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

本輪新增 `tools/transport-opcode-matrix.js`，以 G 版 `Phonest.Proto.dll`、`Phonest.Client.dll`、
`Phonest.Common.dll` 三個精確雜湊 fail-closed 產生 37 列傳輸方法清冊：Game Gate C→S 5、S→C 20，
Micro Gate C→S 5、S→C 7。複合鍵為 `(layer, direction, method_id)`，所以相同數字跨層或反方向不會合併。
每列保留 payload、定義位置、具體 handler 狀態、REA evidence，並明確標示 `runtime_observed=false`。

Game Gate S→C method 19／20 雖在泛型 `IGateClientService.DispatchProtobufRpc` 可分別解析 `MsResponse`／
`MsPush`，實際 `GateClientHandler` 沒有覆寫 `MicroServiceResponse`／`MicroServicePush`，會繼承到丟出
`NotImplementedException` 的基底實作；這兩個 method 因而是「schema 可解，但本 concrete handler 不支援」，
不是可由伺服器安全發送的路徑。method 9 是 concrete override 自己丟例外；12、13、15、16、18、19、20
則是 inherited base throw。相對地，Micro Gate 34／35 有 `MicroGateHandler` concrete implementation；Micro
Gate 5／12 只接受後直接忽略且不 parse body，所以 payload 型別維持 `UNKNOWN`，不猜成 `Void`。

主要 REA evidence IDs：RPC frame `ev_1edaf9959631101c267b45675c3001e317c9f2a58032bc25642754bb079af298`；
game envelope `ev_121f08d8efc91d1fffc97921881fb3ee6f980b87fee9db77072d0fcae5f05b77`；batch
`ev_044540dddb583cb2b09982c0654efd86b4286a089dcb48718fe1b4e1c023c1c9`；Lua bridge
`ev_3ad1dbcb274bc8f9f4d99eb89d932481500aa4956d41d915967b8dc1124a4559`；Game Gate dispatch
`ev_343b43ffab8ca77850fab66232d7051263425ab62cb3f8407aec0d4003e0b248`；concrete `GateClientHandler`
`ev_9904a30aa4177a540a9cf6365fd891c4fdd87fe1d6160cfa4b529f7b4aca4e2e`；G 版 Micro Gate stub、dispatch 與
handler `ev_75e8bccdea1a846f4d50d77fd95650c2a66eae0166a10b9c0d22918422c48492`。Server 的核心 TCP、
RSA/zlib/RC4、單包 EntityMessage 與 micro 33/34/35 路徑相符；KCP server、部分不使用的 gate method
producer 仍是 `PARTIAL/UNKNOWN`。game gate method 19／20 已從模糊的 runtime `UNKNOWN` 收斂為 concrete
handler 靜態不支援，但未做 live fault injection。Client 具有 KCP 建連失敗後切回 TCP 的明確路徑。本輪
未啟動 client，因此這些靜態結果不等於 live handshake 或 gameplay 驗收。

傳輸清冊重現方式：

```powershell
$managed = Join-Path $env:RO3_CLIENT_DIR 'ro3_Data\Managed'
node tools/transport-opcode-matrix.js `
  --phonest-proto (Join-Path $managed 'Phonest.Proto.dll') `
  --phonest-client (Join-Path $managed 'Phonest.Client.dll') `
  --phonest-common (Join-Path $managed 'Phonest.Common.dll') `
  --out 'docs\artifacts\transport-opcode-registry-20261028'

node --test tools/tests/opcode-matrix.test.js tools/tests/transport-opcode-matrix.test.js
```

兩次獨立輸出雜湊完全相同；產物只留在被忽略的 `docs/artifacts/`，不把客戶端衍生清冊提交到 Git。

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

`31122 SkillWhiteListChangeNotify` 的 consumer 可確認讀取 `dirtyInfo`、`objectId`、每筆 `operation`、
`skillBlackWhiteInfo.type` 與 `skillTagId`，但沒有找到 client request 或 server producer；觸發條件維持
`UNKNOWN`。`31110`／`31111` 只有通用 callback，沒有 Scene consumer；`31114` 連 callback 都沒有。本 build
的特殊狀態改由 AOI `state_info`／`common_info.stateInfo` 進 `_syncSpecialStateInfo`，再用 `data.values[1]`
更新 StatusBit，因此三個舊 opcode 是 stale-route candidate，不是可用來補動畫的替代封包。

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

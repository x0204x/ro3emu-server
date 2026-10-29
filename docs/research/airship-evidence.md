# G 飛空艇 1000501 任務證據

## 證據邊界

- **G 靜態資料**：Lua bytecode 位於 client-relative `Client/ro3_Data/StreamingAssets/Recovery/LuaPayload`，影片位於 `Client/ro3_Data/StreamingAssets/VFS/Videos`；以下 Lua 行號對應同一原始位元組的可讀解碼副本。資料可證明客戶端設定與路徑，不能單獨證明原服的實際封包時序或玩家畫面。
- **伺服器實作**：`Reconstructed/scripted-scenes.json`、`DungeonService.Scripted.cs`。`DungeonTests.Scripted.cs` 驗證炮台掛載/攻擊/死亡；`DungeonTests.ScriptedClient.cs` 走完整場景並檢查客戶端可提供互動與成功 QTE 結局。`DungeonTests.AirshipQte.cs` 驗證成功和失敗分支的影片設定及實際 84011 結算順序。
- **玩家驗收**：未取得玩家實機錄影或封包，因此畫面呈現、音效同步與原服結算仍是 `NOT_RUN`。

## G 原始素材指紋（SHA-256）

| 路徑，相對於 LuaPayload 或 Videos | SHA-256 |
|---|---|
| `Config/DataConfig/DungeonQuestConfig.lua.bytes` | `BAB57E218695090A52E1BF785267984EFB57317F8E0CF3F78C6BE211E7C4B137` |
| `Config/DataConfig/PlotInterpretation.lua.bytes` | `2FB3A575840A1A47C3671EA1B2D7A1AD9E9792A55011AA188EB1AE2ED18EDEB6` |
| `Config/DataConfig/QTEEventConfig.lua.bytes` | `8E8B36C3E38FF73113D2E1D9B290692A6DA3639784B660C607DEDDD31CF82CE9` |
| `Config/DataConfig/VideoConfig.lua.bytes` | `AAA597E8559DF419531C3519CDF95C8A1A6E619E774D20DCEF5CA99DEE1C97A6` |
| `Config/DataConfig/SkillConfig.lua.bytes` | `07A97ACA31C74CCEB850853660CE3E9C5FF2406C6E74DBE4FB06FBC879558339` |
| `Config/DataConfig/GameplayStatusButtonConfig.lua.bytes` | `A8562265CC36DD4D4DA549040A57CE347E01B30860375C84DFBCD1B3B067C80F` |
| `Config/InjectConfig/DungeonVariableFunctionConfig.lua.bytes` | `A0CBD39304311C84E6DEB95D4FFF911B8D7CBE27C598536F64A324AE257BBB31` |
| `Config/SceneEditor/sc_plot_airship_001_pl01_sceneeditor.lua.bytes` | `4475BDF9DE048C7A4CBE558AA9F7058A017A141CF046FFE8802462C64D0768B4` |
| `Xinshou_Fktcursh_part1.mp4` | `4C15E38263D55427D24D6FC8B184CD321F65F6618EAF78058B2FC3370D9FC933` |
| `Xinshou_Fktcursh_Part2success.mp4` | `DA9E05EB9191CD311ACC1084C6051B370908870D6D3A70C960305199077A626A` |
| `Xinshou_Fktcursh_Part2fail.mp4` | `A3DFBB511D58D8A8FDBF07D1AE33509B67744051D1FD8B547580687B2E55130B` |

## 六個任務階段與伺服器路徑

`DungeonQuestConfig.lua:2978-3134` 把 5300021–5300026 全部指定到副本 1000501，任務序號 1–6。最後一階的指引 `_kAction=[142,1000501,55,9,530003802]` 定位到船長對話。LAB 的八個步驟為階段 1、2、3（三批）、4、5、6：

| 階段 | G 客戶端資料 | LAB 對照 |
|---|---|---|
| 1 | Bobo、船長、Wule、Boya 的 5300031–34 自動範圍對話；buff 群 53645 開啟。 | 四個互動均需完成；團長 5300035 同場顯示。四個全需完成屬流程重建推論。 |
| 2 | 三座炮台 NPC 5300022，互動 5300006。GameplayStatus 12 是 UpperCannon；ExtraConfig 按鈕 27 施放技能 5353001、28 離開。 | 32700 掛炮台，32702 `InteractChangeNtf` 狀態推送，31102 施放 5353001，兩艘敵艦 5300015/5300053 的真實死亡才推進。固定射擊次數無 G 證據。 |
| 3 | 敵人登船、消滅敵人。場景編輯器有 4 名衝鋒怪與 5 名弓箭怪點位。 | 依 3+3+3 重建；分批時序是推論。 |
| 4 | 擊敗邪惡 Boss 5300018。 | 真實死亡後推進。 |
| 5 | 戰後檢視；5300061–65 的五個互動 5300048–52。 | 五個泡泡全需完成屬流程重建推論。 |
| 6 | 受傷船長 5300038，互動 5300035 開 530003802 對話，選項觸發 5300039／副本訊息 5300039。 | 訊息後推故事 130161；等待客戶端 QTE 的結局 130162 或 130163，才結算通關。 |

## 動畫與小遊戲：G 靜態呼叫鏈

1. `DungeonVariableFunctionConfig.lua:78-84` 把副本變數 `100020016` 綁到 `PlayCutSceneStory`；`LC_DunGeon_Actions.lua:3473-3491` 將變數陣列的 story ID 交給 `LC_CutSceneManager.PlayStory`。LAB 在 30404 `DungeonEventNotify` 的變數變更推送 `[130161]`。
2. `PlotInterpretation.lua:905-917`：130161 指向影片 30161，QTE 6，片尾動作 601 上報 130161。`QTEEventConfig.lua:119-143`：QTE 6 有 6000 ms、type 3、進度 `[1,3]`，成功事件 `[2,130162]`，失敗事件 `[2,130163]`。QTE 6 本列沒有 `_kShortcutKey`，但檔頭預設是 `F`（13、25 行），145–164 行的 metatable `__index` 套到各列；匯出的有效值也是 `F`。
3. `UI_Sys_NpcDialog_CGInteractionWidgetBaseCtrl.lua:557-566,680-769` 在接收故事資源後依 `_iQTEID` 建立並排程 QTE；`UI_Sys_NpcDialog_CGInteraction_SkillWidgetCtrl.lua:733-779,781-837` 從成功/失敗事件呼叫 `LC_CutSceneManager.PlayStory`，所以選結局是客戶端本地流程。
4. `PlotInterpretation.lua:919-970`：130162 指影片 30162、130163 指影片 30163；兩者的片尾動作序列均為 `150|10`、`337|5000|1`、`601|結局ID`。LAB 只把最後的 601 對應 84011 作為通關門檻，不從第一段影片 130161 的 601 直接結算。
5. `VideoConfig` 的 30161/30162/30163 依序指向上表三個實際存在的 G 影片。三片均為 H.264/AAC、2650×1080、25 fps，片長依序 9.64／19.12／14.36 秒；內容分別為敵艦接近、成功分支與失敗分支。來源影片可解碼不代表玩家播放器已正確呈現，後者仍為 `NOT_RUN`。
6. 炮擊呈現由 G `SkillConfig.lua:581648-581666` 的技能 5353001 指到 `_iScriptShow=61098`、`_kScriptLogic="beginnercannon"`、500 ms 動作/冷卻；`GameplayStatusButtonConfig.lua:194` 的按鈕 27 指這個技能。這只建立技能視效設定鏈，未取得玩家畫面的動畫播放證據。

## 炮台遠距目標與階段身分

- `DungeonQuestConfig[5300022..5300024]` 的 quest 2、3、4 都使用標題鍵 `11080700041`（遭遇邪惡首領／Encounter the Evil Boss）。標題本身不能判定階段；quest 2 的 detail `11080900041` 才明確要求用船上火炮攻擊敵艦，quest 3 是敵人登船，quest 4 是擊敗首領。
- `sc_plot_airship_001_pl01_sceneeditor` 的炮台點 40／42／43 位於約 `(241.5,229.039,458..470)`；敵艦點 24 為 `(346.8,224.64,451.9)`，點 61 為 `(141.63,211.245,471)`。從炮台到兩艦的水平距離約 100–107 m，超過伺服器一般 60 m AOI。
- G `LC_TPSComponent.lua:692–735` 的瞄準射線先取得客戶端已存在的 monster unit，才把其 UUID 寫入 `targetsId`。僅在伺服器存在遠距怪物不足以提供可瞄準目標；客戶端必須先收到其 31037 base。
- 伺服器因此對 `1000501` 的 Cannon 步驟兩艘 configured fight targets 使用 `(inferred)` 同房間完整可見政策。該設定只跟隨這兩個執行個體，場景／房間仍隔離；一般單位保留 60／68 m 進出視野，移動、後加入與轉場仍透過一般 AOI 生命週期，敵艦移除仍送正常 leave。這是相容性重建，不是已取得原服 AOI 半徑的證明。
- 炮口／爆炸呈現仍有獨立待查項目：`Utility/Lua_ServerLocation_Define.lua:403–442` 的 SkillInfo provider 明確以 `_iScriptShow` 選取演出，沒有依技能群組回退。5353001 選到的 `Config/Skill/skill_61098` 是空 shows。G 另有 `skill_53530` 炮口、`skill_53535` 炮口與子彈、`skill_53531` 爆炸、`skill_53532` 震屏與甲板爆炸音效；全表沒有技能選用 show 53530，不能把殘存素材當成正在使用的演出。5353501、5353101、5353201 才分別選到後三者。
- `NPCConfig[5300037]._kRespawnSkill=5353101` 與首領 `NPCConfig[5300018]._kRespawnSkill=5351901` 是設定候選；後者對應 4.5 秒 `Born` 動畫。G `LC_State_Unit_Born.lua:107–184` 的客戶端 BornSkill 路徑讀的是 `_kRespawnEffect`，不是 `_kRespawnSkill`，而首領的 `_kRespawnEffect` 為空。因此目前無法判定這兩個技能應由伺服器在出生當下發出。已搜尋可讀遊戲 Lua 的 `beginnercannon`、5353501、5353201 及 `_kRespawnSkill` 消費端，仍未取得原服服務端橋接腳本或封包；實際技能、來源實體、觸發時序均保留 `UNKNOWN`。

## 前置、正式入口與演出驗收矩陣

影片 `quest-animation-flow.md` 是原服畫面的歷史參照；舊表所指的炮台單次互動與缺 QTE，是修正前的程式快照，不能當成本版仍缺的證據。

| 流程 | G 設定與客戶端事件 | 目前支持與剩餘驗收 |
|---|---|---|
| 貨物擺放三關 | 任務10103150目標 `[103053,3]`；客戶端完成一關送83651，成功回覆後顯示成功／下一關面板。 | 已限制執行中任務、場景10101、最多三關；測試覆蓋錯地圖、重送上限。請求沒有格子／旋轉資料，無法據此補造伺服器解題驗證。玩家拖放與動畫仍 `NOT_RUN`。 |
| 碼頭預示 | 10103170等待130031的84011；交付後指南針面板先預載並暫停130032，按退出才ResumeVideo。 | 任務與劇情完成路由存在；指南針UI、正常播放／跳過與介面恢復仍需實機驗收。 |
| 送別與啟航 | 10103200的NPC100098／對話1010320001；10103201接受時動作41播放130041（影片30041），84011後才接10103210。 | 有既有任務及劇情路由。StoryWalk以GM旅行及模擬事件，不能證明正式碼頭入口、影片、讀取及甲板落點。 |
| 登炮、持續射擊、退出 | 32700／32702／31102技能5353001／32703；TPS需要31037敵艦。 | 已實作持續炮台狀態、射擊冷卻與真實死亡推進；本次補同房間遠距敵艦可見。炮口與爆炸的原服事件仍待抓包；玩家端 `NOT_RUN`。 |
| 三波、首領、五處調查 | 5300023–25任務資料與場景點位；首領5300018，五處互動5300048–52。 | 流程及協定測試存在；3+3+3波次、五處全需完成是重建推論。不得由速殺測試宣稱原服數值、時序與動畫一致。 |
| 船舵QTE及營地交接 | 船長後130161，客戶端成功130162／失敗130163，結局84011後通關；10103210交付動作141送既有自訂場景跳轉到10514／1040401。 | 兩個結局已各有LAB順序測試；錄影只展示失敗分支。正常／跳過／斷線後影片、HUD與控制恢復仍需實機；耐久獎勵／任務恢復另見 `airship-recovery-evidence.md`。 |

以上映射可由 `TaskConfig.lua:43737–44240`、`PlotInterpretation.lua:616–663,905–970`、`LC_TaskHelper.lua:537–560,1429–1494,1566–1614`、`UI_Sys_NoviceChallenge_CompassPanelCtrl.lua:106–157`、`LC_InteractActions.lua:1383–1401,2413–2431` 核對。這些行號是可讀解碼副本，不是原工程行號。

## 伺服器與測試覆蓋

- 炮台互動 5300006 掛載狀態，按鈕 27 施放 5353001、按鈕 28 離炮；敵艦真實死亡推進階段。沒有固定三次登炮或直接施放 5365001 的 G 證據。
- 伺服器的末段故事變數、QTE 兩結局等待及通關順序與 G 靜態路徑一致。
- `DungeonTests.AirshipQte` 分別走到船長對話後，核對 130161 不先結算、130162/130163 的 84011 各能結算；`TheAirshipCannonsSinkTheEnemyShips` 覆蓋炮台；`EveryInteractionAStepWaitsOnIsOfferedByTheClientsOwnRules` 的 1000501 案例覆蓋全階段互動。這些測試使用本地 loopback 服務及 HP=1 速殺，屬 LAB 遊戲協定路徑，並非玩家畫面驗收。
- 炮台案例從客戶端收到的 31037 bases／leaves 重建敵艦清單，確認登炮位置在一般 AOI 範圍外仍收到兩艦，再使用那些 UUID 發出攻擊並驗證死亡與後續 despawn。`WorldTests.SceneWideUnitsReachDistantRoomMembersWithoutBroadeningOrdinaryAoi` 覆蓋已在房內、後加入、玩家／單位移動、換房、正常移除及一般 AOI 範圍保留。

## 尚未驗收

- 玩家是否可在炮台上完成射擊、QTE 成功/失敗影片是否正確播出，以及實際獎勵交易是否完整，仍需實機紀錄，不能由靜態資料或 LAB 測試代替。

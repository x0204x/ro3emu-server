# G 組隊打怪 Base／Job 經驗值證據

狀態：`partial`。G 原件與同雜湊離線匯出可證實客戶端配置、提示及入帳顯示；
原服權威結算公式與實際玩家入帳仍 `UNKNOWN`。以下 Lua 路徑相對於
`Client/ro3_Data/StreamingAssets/Recovery/LuaPayload`，行號對應相同原始位元組的可讀反編譯。

## 來源

- `Logic/Team/LC_TeamHelper.lua.bytes` SHA-256：`025F733A50EB687572A64EDC89D0DBAAD818680FF8D188781F44D3377706C716`。`GetMemberOnlineState` 位於可讀 Lua `:738–839`，五席分組位於 `:178–221`。
- `Config/ComplexConfig/PublicParamConfig.lua.bytes` SHA-256：`870E6C41EFD48C03771604305CA0CEBBF8EC06C08443FB84EFB3F08497962AFD`。`TeamExpBuffNum` 位於 `:1398–1410`、`TeamBuffInfo` 位於 `:1413–1437`、`RoleFOVMax` 位於 `:9369–9371`、`TeamAloofLimit` 位於 `:9982–9983`。
- `UI/Team/Controllers/UI_Sys_TeamEXPTipsWidgetCtrl.lua.bytes` SHA-256：`E91747F2688EBF734B54E064C75619FA4B6B521B3E9BBF988A987AA1F8A60F51`。`:90–139` 依自己所屬五席計數非零 GUID 並顯示人數加成。

## 客戶端與伺服器對照

| 項目 | G 可證實內容 | 我們目前伺服器 |
| --- | --- | --- |
| 人數加成 | `PublicParamConfig.TeamExpBuffNum=[10000,12500,15000,17500,20000]`，目前 `TeamBuffInfo` 提示 1–5 人 Base/Job +0/25/50/75/100% | `TeamService.ExpBuff` 同值，但為硬編碼，日後 G 配置變動不會自動同步。 |
| 40 人團 | UI 只按自己所屬固定五席（1–5、6–10 等）計算提示 | `ExpGroupRange` 依同樣五席選擇，不以 40 人全部計。 |
| 場景與位置 | 新提示寫同一野外地圖；UI 人數計算本身沒有檢查地圖、分線、距離。隊員 `Faraway` 顯示可由不同地圖／分線或超過 `RoleFOVMax=55 m` 觸發；這是 UI 狀態。`TeamAloofLimit=50` 未找到發獎呼叫。 | 擊殺者及同五席、在線、角色已載入且附著、同一 public field `WorldScene`、非副本；沒有距離、死亡、貢獻判定。因此同場景隊友超過 55 m 仍符合目前分享資格，甚至 UI 可能顯示 `Faraway`。這是自訂的資格規則，未證實符合 G 原服。 |
| EXP 結算 | G 客戶端接收權威 `5066 RoleBaseInfoIncrement`，更新 Base/Job 欄位；無可證實的客戶端分攤公式。 | 怪物掉落中虛擬 Base EXP 項目 100 先依擊殺者等級差修正，再乘隊伍加成；Job EXP 取加成後、追趕加成前的 Base 值。Base 另經擊殺者追趕加成；之後兩種 EXP 各經伺服器倍率縮放、平均分給資格名單，餘數依席位順序分配。此公式為 **inferred**。 |
| 發放 | 5066 可顯示每位玩家自己的 Base/Job 變動；原服交易邊界未知。 | 擊殺者份額隨擊殺交易提交；其他隊員先持久化個別義務，後續冪等發放，斷線可於登入後恢復。 |
| 掉落 | 提示另寫 1–5 人掉率 +0/30/60/90/120%，舊文案 24043 寫 +20/30/40/50% 並與現行提示衝突。 | 目前沒有依 `TeamDropBuffWeights` 組隊加成掉落；不應把掉落提示當作已完成的實測規則。 |

G 原始 `LvExpCorrectionConfig` 有等級差係數，但未找到其在原服組隊發獎流程的
呼叫。原服是否等分／每人全額、先乘後分、取整、死亡／距離／貢獻與個別等級差，
均為 `UNKNOWN`。舊文案 24043 的 20/30/40/50% 不用於目前 UI 提示。

**遠離隊友結論：**現行伺服器只排除不同 `WorldScene` 或不在線等情況，
不以 50 或 55 m 截斷組隊經驗值；G 客戶端的 `Faraway` 與 `RoleFOVMax`
屬顯示／視野狀態，沒有證據可當作原服經驗值半徑。

現有測試 `TeamExpTests` 覆蓋五席分組；`DungeonTests.PartyExperience` 以合成
擊殺回饋驗證同場隊員 Base/Job 變動及 5066。它不驗證原服公式、真實怪物掉落、
離線重試或玩家畫面。原服與玩家端雙角色擊殺樣本仍 `NOT_RUN`。

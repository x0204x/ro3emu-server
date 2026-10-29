# 全技能動畫伺服器 producer 缺口稽核（G 客戶端靜態證據）

狀態：`PARTIAL / STATIC ONLY`。本報告證明現行伺服器會把若干多段動畫壓成第一段；不把靜態資料推定成官方封包時序，也不推定分段傷害。

## 證據邊界

- G 匯出來源：`G:\RO3 Asia Launcher\Client`，匯出時間 `2026-10-28T22:34:53.7868684Z`，`export.json` fingerprint `85a9f63af0f4ed058e84ba5fbd067bf2a363955c5f15a7fccf92c788f8212cf0`。
- `SkillConfig.json` SHA-256：`B2155EB93A3C25B1EF2F3FD94B6A8C740F7033498436E2CD69FB4A6833729D6E`。
- `Lua_Config_EffectData.json` SHA-256：`25A9C539DBD35BE5A1F04AC167ECE417B7E79E53FEDC9636A85F279462E07963`。
- `skill_111101.json` SHA-256：`107D939DA1394011BDA368C85BD61B7B6C1C5A6C23435960669EC953129A41FF`。
- `skill_155000.json` SHA-256：`E2BC4F6A957B6F316FAF44B50702D3F47F61482526A29EBE9BDB23C5EFB963B3`。
- G 反編譯 Lua 僅證明 client consumer：31100/31106 進 `_useInSkill`、31121/31107 進 `_extraUseInSkill`、31120/31108 進 `_skillFixTimeBullet`、31101 進 `_skillHit`/`CacheDamageHit`。31119/31109 是獨立 effect route，沒有證據可普遍套用。
- `LC_Skill_RunTimeContext.PlayClientEffect` 對每筆 `m_kClientPoints` 依序取 `[1]=time`、`[2]=effectId`、`[3]=split`，再呼叫 `SkillCreateFixTimeBullet`。這是明示配對；相對地，G decompiled `Logic` 全文沒有 `m_kAtkPoints` consumer，也沒有把 server bullet 的 `effectId` 回綁 attack-point index。
- `LC_DamageHelper.CalcDamageHitPoint` 直接以 31101 的 `showPoint` 取 EffectData 與 `m_kActionDamge`；client 端同樣沒有把 `showPoint` 經由 attack-point index 轉換。

## 現行 producer 的確定行為

`StartCastAsync` 只取 `m_kAtkPoints.FirstOrDefault`，只挑一個 `BulletEffect`，排程一次 31120/31108，再只 resolve 一次。`HitEffect` 與 `HitSplit` 也只選第一個 ActionDamage／第一個 `_kShowDmg`。因此多段 timeline 目前會被壓成單段。

另一次 producer 全域稽核確認：玩家與守護寵物路徑會送 `playAct=true`，但怪物普通攻擊、Boss 技能、同伴、塔寵及舊 MVP producer 原先留下 protobuf 的 false 預設。G `LC_TimeLine_Object` 只有在 `isPlayAnim` 或 `playAct` 為 true 時執行 Animation event；因此這是「技能效果或傷害存在、角色攻擊動作缺失」的共通確定缺口。共用 player/monster sender 現已強制 `PlayAct=true`，直接 producer 亦明示該欄位，並由 high-level Boss、Boss 排程、同伴、塔寵及 MVP 測試覆蓋。

| 路由 | normal / high | 現行 producer | 靜態判定 |
|---|---|---|---|
| cast | 31100 / 31106 | 一次 | consumer 與 route 已確定 |
| extra cast | 31121 / 31107 | `actor != null` 或 `IsExtraSkill` 時一次；`CastExtraAsync` 直接 ApplyEffects 而沒有 timeline | 是否每個 proc 都應有 timeline 為 `UNKNOWN` |
| bullet | 31120 / 31108 | 最多一次 | 無 `m_kClientPoints` 的 named Bullet 必須由 server producer 提供；多段配對仍未知 |
| hit | 31101 | resolve 一次，使用第一個 ActionDamage | 多段 hit 與傷害分配仍未知 |

## 全資料集盤點

盤點條件為 G `SkillConfig` 的 `_iRelease=1`、`_iBasicType=1`、`_iScriptShow>0`，EffectData 只計 `DamageType=2` 且有 named bullet，或 `DamageType=3` 且有 `m_kActionDamge`。

- 2749 列、953 個 show；其中存在匯出 skill template 的為 2734 列、938 個 show，另 15 列／15 個 show 無 template。
- 多於一個 `m_kAtkPoints`：466 列／136 個 show。
- 有 server bullet 且沒有 `m_kClientPoints`：357 列／108 個 show。
- 有多個 server bullet 且沒有 `m_kClientPoints`：54 列／10 個 show。
- 多 attack point、沒有 client points，且 bullet 或 ActionDamage 多於一個：153 列／44 個 show。這是現行 first-only resolver 明確會壓縮的範圍。
- attack point、server bullet、ActionDamage 三者數量完全相等：6 列／2 個 show。

10 個多 server-bullet show 為：`11418`、`12345`、`12353`、`15402`、`23342`、`49063`、`61202`、`111101`、`111102`、`155000`。

44 個 first-only 受影響 show 為：`11112`、`11418`、`12012`、`12342`、`12345`、`12353`、`12432`、`13211`、`15000`、`15402`、`23141`、`23251`、`23342`、`41108`、`41114`、`43125`、`43128`、`43402`、`43414`、`43419`、`43453`、`43806`、`43808`、`49063`、`52035`、`53523`、`53527`、`53528`、`53529`、`60010`、`61001`、`61011`、`61024`、`61202`、`62006`、`62007`、`62076`、`111101`、`147212`、`151000`、`152000`、`155000`、`247205`、`247209`。

## 兩個等基數候選

### 111101（SkillConfig 1111011..1111015）

- `m_kAtkPoints=[0.06666667,0.2333333]`，沒有 `m_kClientPoints`。
- ordered bullet inventory：`11110102`（life 200 ms）、`11110103`（life 180 ms），兩者皆為 `fx_bullet_spear.prefab`。
- ordered ActionDamage inventory：`11110105`、`11110106`。
- 1111011 的 `_kShowDmg=[3,1]`、`_kDamageParam1=[4000,5000]`、`_kDamageParam2=[60,220]` 不能單靠欄位名稱證明其索引就是 attack-point 索引。

### 155000（SkillConfig 15500000）

- `m_kAtkPoints=[0.1666667,0.3333333,0.3333333]`，沒有 `m_kClientPoints`。
- ordered bullet inventory：`15500008`（speed 100）、`15500010`（speed 70）、`15500012`（speed 35）。
- ordered ActionDamage inventory：`15500009`、`15500011`、`15500013`。
- 此列為 high route，因此若日後由 runtime 證實分段 producer，bullet route 必須是 31108；這不等於現在已證明三次 31108 的 effect/時間配對。

## 可以安全下的結論與仍未知項目

可以安全確認：上述 timeline 沒有 client bullet points，而 authored named bullet 多於一個；現行只送第一個 bullet，無法完整呈現 authored inventory。可先用資料契約測試鎖住 attack-point、bullet、ActionDamage 的 ordered inventory，防止後續資料或 resolver 再退化。

不能由靜態資料確認：

1. `attackPoints[i]` 是否一定配 `bulletEffects[i]` 及 `actionEffects[i]`。skill JSON 沒有 EffectData ID 配對欄位，EffectData 也沒有回指 attack point；client 只對 `m_kClientPoints` 提供明示的 time/effect/split 配對，不能類推到 server-only bullet。
2. 同值 attack point（155000 的後兩點皆 `0.3333333`）是同時觸發、相對時間或不同 state 下的時間。
3. `_kShowDmg`、`_kDamageParam1/2` 是分段索引、條件分支、等級參數或其他語意。不可因此拆分或重複 authoritative damage。
4. extra proc 是否應額外發 31121/31107；目前只能標為 `UNKNOWN`。

## 驗收 gate

- 靜態 gate：`SkillAnimationStageContractTests.EqualCardinalityCandidatesKeepTheirExportedOrderedInventories` 鎖住兩組 ordered inventory；它不宣稱 ordinal pairing。
- producer gate：所有已稽核 cast producer 都送出 `PlayAct=true`；普通與 high-level route、同伴、塔寵及 MVP 路徑有回歸測試。
- runtime gate：需同一 skill/session 的官方或可控 runtime trace，至少記錄 cast、每個 bullet、每個 hit 的 opcode、effectId、時間戳與 sessionId。
- 玩家 gate：修正後需在 G 相容客戶端逐技能觀察角色動作、每段 projectile、每段 hit effect，並核對怪物只承受一次正確 authoritative damage 或已證實的分段傷害。未通過前維持 `PARTIAL`。

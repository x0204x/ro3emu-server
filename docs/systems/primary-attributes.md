# 六項素質與衍生屬性

力量、敏捷、體質、智力、靈巧與幸運分別是屬性 ID `1..6`。`CharacterService` 依角色配點、裝備、卡片、職業／等級、天賦、其他屬性來源與有效 buff 建立原始貢獻，使用 `AttributeConfig` 定義的群組算出有效值，再套用 `AttributeConversionConfig`。

六項群組與其他屬性使用同一公式：`Base × (1 + BasePerAdd + OtherPerAdd) × (1 + BuffPerAdd) + BuffValueAdd`。百分比的資料尺度由 `AttributeConfig._iDataType` 決定。衍生轉換使用這六項的有效值，避免面板顯示 buff 已提高力量／智力，攻擊或最大 SP 卻仍使用 buff 前的值。這項來源聚合順序是明示推定（inferred），以原生客戶端的群組與加點預覽契約保持一致；尚未取得原服伺服器內部順序的執行證據。

| 素質 | 轉換方向 |
| --- | --- |
| 力量 | 近戰物理攻擊及其百分比、物理防禦 |
| 敏捷 | 迴避、攻擊速度 |
| 體質 | 最大 HP 與其百分比、HP 回復及其百分比 |
| 智力 | 最大 SP、魔法攻擊、魔法防禦及其相關百分比、SP 回復及其百分比 |
| 靈巧 | 命中、詠唱速度；遠程職業另有物理攻擊及其百分比 |
| 幸運 | 命中、迴避、暴擊 |

實際係數保留於配置表。`JobConfig._iDistanceType == 2` 使用 `_kAttribute2`，其他職業使用 `_kAttribute1`。六項轉換的目標不包含 ID `1..6`，因此不會遞迴放大六項素質。

配點請求是 `31500 AllocateAttrPointReq/Rsp`，持久化配點方案透過 `5067` 同步；角色屬性值透過 `31008` 更新。從初始 1 點提高至指定值的成本是 `BasePointConfig[2..指定值]._iStatusPoint` 總和，上限 99。不能以重送請求退點或超出可用點數。

原生屬性面板的 HP／SP 回復顯示公式分別是 `ReHP + MaxHP × PreReHP` 與 `ReSP + MaxSP × PreReSP`。`117/118` 的顯示型別為整數，`Lua_CFG_AttributeConfig.GetAttrRealDesc` 使用 `Mathf.Ceil` 後顯示。這只證實面板計算與取整方式；戰鬥期間是否回復、回復時機與實際入帳另屬戰鬥服務的規則，不能從面板公式推定原服執行結果。

`SkillCombatTests.PrimaryConversion` 的兩項測試比較六項固定值 buff 與同值配點的衍生結果，並驗證基礎百分比、buff 百分比與 buff 固定值混合時仍使用同一有效素質。這些是 LAB 聚合一致性測試，不代表玩家已施放對應技能或完成原服比對。

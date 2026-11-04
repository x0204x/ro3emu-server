# Ordinary pet guardian skills

Ordinary guardians have separate Primary, Reactive and King Core paths. The known scripts cover all 56 pet
families and 1,176 star/stage mappings in the examined client export. This is implementation coverage, not proof
of original-server parity. Numerical configuration and wire formats come from the client; execution policies
that it does not establish remain explicitly inferred. See [client evidence](../research/pet-skills-evidence.md).

## Ownership and protocol

`PetService.Guardians` resolves owned pets in unlocked fight slots of the current plan. Slot 1 supplies the only
Primary skill, projected into both slot-11 wheels on 5025. Slots 2-4 supply Reactive effects. The first King in
utility-slot order supplies Core; moving that King invalidates captured pending Core work. Assist and tower
lineups remain separate. A directly requested internal child, unknown wrapper, locked slot, unsupported cost,
invalid target or cooldown is rejected.

A primary request uses 31102; EXTRA_USEINSKILL (31121) carries the type-41 child and `petConfigId` so the client shows the temporary
pet actor. The wrapper reserves cooldown before any awaited publication. Guardian work does not replace the
owner's ordinary cast. Every delayed pulse revalidates owner, attachment, scene generation, plan, slot and
star/stage. Pulses serialize within one cast and aggregate committed primary hit amounts before a final Core hit.

## Implemented effects

- Single and multiple hits, adaptive attack, element components, circles/fans/rectangles and target-centered
  areas; Owl random lightning, Sarah bursts, Eve's final rectangle, owner heal per hit and kill-triggered curses.
- Direct and periodic recovery, finite shields, Chimera control charges, immunity, dispel, knock-up, root,
  petrify and timed stat changes. Monster stats use live overlays without altering shared templates.
- Category-specific Reactive effects: duration, coefficient, target count, cast speed, healing, defence,
  damage, elemental resistance, conditional shields, area debuffs, additional hits and temporary owner-hit windows.
- Stateful effects: Cat consumes the next owner-damage charges and the next received-hit vulnerability;
  Scout stacks defence after damage; Mummy limits kill-triggered curses; Piamette heals once below its threshold;
  Shinobi moves/cleanses/hides and ends stealth on owner damage or its incoming-hit limit; Scout detects stealth.
- King Core effects: Zherlthsh's share of accumulated primary damage; Hope's ally defence and bounded HP floor;
  Wander Man's amplified defence debuff and conditional control/attack debuff; Fairy's shield-bound debuff immunity.

PvP reuses hostility, damage settlement and target limits. Healing excludes hostile players. Guardian buff
attributes also affect real damage, casting and healing. Blind publishes the client's dark-state/radius properties rather
than inventing an accuracy penalty. The same inferred radius restricts monster target acquisition and normal hits.
Sohee's normal-attack reduction runs at the shared player HP transition for both PvE and PvP.
Freeze, petrify and sleep break on damage; the first two use their configured
follow-up debuffs. Ordinary timed buffs can outlive unequipping; future windows and delayed attacks cannot.

## Scope against ordinary combat

寵物來源仍以 `IsGuardianOwned` 保留來源識別與專用被動；下列共用規則已不再限制寵物來源：

- 標準擊飛、冰凍、石化、定身、霸體、控制與減益免疫；隱匿狀態沿用共用判定。
- 怪物攻防、命中、暴擊、施法速度、移動速度與傷害使用所有有效增益，移除後還原模板。
- 失明限制玩家與怪物的可見距離；冰凍、石化、睡眠在正值傷害後解除，後續狀態保留原施放者與來源類別。
- 持有者免疫分類拒絕所有來源的對應減益，並保留元素控制的抵抗追擊通知。
- 治療輸出／承受、SP 消耗與變動詠唱納入所有有效增益，排除到期但尚未清除的資料；玩家詠唱仍由固定時間加上可變時間組成。

怪物除了狀態與禁止行動遮罩，也同步 `ObjStagger`／`StaggerInfo`。配對客戶端
`LC_UnitSpecialStatus.GetStaggerState` 使用 `1 << _iStaggerType`；
`LC_State_Unit_Stagger.StateProcess` 會維持硬直，且 `LC_UnitBase` 將硬直排在技能之前。
伺服器依此拒絕硬直中的施法（權威執行策略標為 inferred），不新增原 `_kForbidden` 沒有的禁止位元。

失明半徑、治療修正與 SP 消耗的共用規則仍須依各來源的有效增益驗證；不能因此宣稱普通技能與裝備的所有效果都已覆蓋。
合成測試與真實資料測試分別驗證來源範圍、控制、怪物速度、詠唱及免疫；原生客戶端畫面仍需玩家驗收。

## Explicit inference boundary

The client does not contain the original authoritative pet scripts. Damage formula, adaptive-stat choice,
rounding, target tie-breaking, proc order, stacking, exact pulse-to-animation mapping and some auxiliary links
are emulator policies. Shared combat cooldowns are session state, not newly persisted timers.

Blind's original radius was not recovered: 5 metres is an explicit approximation. Angeling's periodic interval
is inferred. Where descriptions and auxiliary rows conflict, the code says which source it uses where it reads
it: Owl's reactive coefficient and Aliot's area heal follow the description, Angeling's heal uses the
description's radius rather than the halo's, and Incubus's aura uses the linked auxiliary's range. Butterfly's
exact subdivision and Sarah's auxiliary burst timing remain inferred. Repeat counts read from descriptions are
capped (`GuardianPetRules.MaxRepeats` and its neighbours); the caps are safety bounds, not client rules.

## Validation

Synthetic tests cover admission, shared cooldown reservation, wheel updates, utility-slot separation, lifecycle
cancellation, independent modifiers and directed AOI visibility. Client-data tests cover every star/stage mapping
and real combat cases for heals, shields, control, stat changes, multi-hit attacks, conditional windows, periodic
pulses and all four King Core mechanisms. Passing these tests establishes the declared server behavior; only
comparative captures from the official server could establish numeric and timing parity.

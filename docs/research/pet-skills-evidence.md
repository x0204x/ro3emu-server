# Ordinary guardian skills: client evidence

The client separates ordinary guardians from the tower pet unit's active skills. This document describes the
evidence boundary for the ordinary guardian implementation; it is not a claim of original-server parity.
Client exports and decompiled files stay outside Git.

## Configuration and projection

| Source | Established fact | Remaining inference |
|---|---|---|
| `Config/CFG/Lua_CFG_PetStarConfig`, `Logic/Pet/LC_PetHelper.GetPetAllSkill` | Star/stage select `_iProtectSkill` and `_iPassiveMain`; the latter is presented as Reactive. `_kFightSkill` contains separate static attributes. | Runtime registration and trigger order of reactive effects. |
| `MG_SkillGrowth.proto`, `Logic/Asset/Systems/LC_System_Skill._onJoystickSkillListChange` | `SkillWheelPet` is slot 11. The reader copies `skillWheel.whellSkills`; the separate `petSkill` field is not copied into that map. | No unsupported claim of original-server parity. |
| `Logic/Asset/LC_DAL_SkillDevelop._refreshEquipSkillList` | Both skill wheels flow from the synchronized skill data to the joystick list. 5025 uses the increment wrapper on the wire. | Publication timing relative to other updates. |
| `UI/Pet/Controllers/UI_Sys_Pet_LineUp_List_CellWidgetCtrl._initSkillWidget` | Lineup index 1 selects Protect; other ordinary lineup indexes select Reactive. | Effect execution order. |
| `UI/Pet/Controllers/UI_Sys_Pet_SkillTipsPanelCtrl_BottomTips._buildBottomTipsPresent`, localization 40042/40285/40286 | Primary skills can be used from the primary battle slot. Reactive is active in utility positions and inactive in the main position. | Passive stacking and internal trigger timing. |
| `Logic/Actor/AutoAttack/LC_AutoAttackSkillHelper` | Pet slot participates in the default automatic skill list, subject to slot and skill eligibility. | No evidence for a separate 250-ms server polling caster. |
| `BehaviorTree/trees/normalpet`, `LC_AutoAttack_BtCtx`, `LC_AutoAttackHelper` | The tree selects targets in the player's auto-battle context. Follow target comes from the team data router. | This is not evidence of autonomous pet-model combat AI. |
| `LC_AutoAttack_AttackSubState._performCastSkillCall`, `LC_Skill_ActionPreCheck.CheckSkillSendServer` | The selected skill goes through the ordinary client use-skill request path. | Server-only activation policies. |
| `Logic/Scene/LC_Scene_Sync._useExtraSkill` | A nonzero `petConfigId` and GuardianSkill type 41 select the temporary guardian-model presentation, with the owner in `srcID`. | Authoritative timing, damage, and receiver selection. |

## Named family execution

The type-8 wrapper resolves an auto-learned type-41 child through `_kLogicData`. A whitelist admits the named
wrapper families. `GuiNv_Enter` is the documented exception: its sole `GuiNv_HuZhu` child and shield description
corroborate the link. All 56 exported families resolve at all 1,176 star/stage mappings. Classifying a family is
not evidence that every runtime detail is exact.

Primary/Reactive descriptions, `_kDamageParam1`, `_kBuff1/2`, `_kSkillCategory`, `_kSkillAttributeList`,
`_kChangeAttri` and auxiliary controller links provide the available rules. The implementation names the child
script for each family instead of applying a generic damage formula to support placeholders. Reactive candidate
buffs are validated against their complete expected attribute payload; matching skill/buff IDs alone is unsafe.
Forest Fairy is a concrete counterexample. Buff duration tags distinguish buffs, debuffs, shields, control and
immunity. Duplicate utility category matches must not apply the same modifier twice.

`UI_Sys_Pet_LineUpWidgetCtrl._updatePetSetUpList` establishes the first utility King selection. The four known
Core families use their dedicated `_iCorePassiveSkill` rows. Core is an enhancement of its utility effect, not a second
primary caster or a passive on every deployed King.

## Stateful behavior and wire evidence

- Cat descriptions limit the owner's subsequent damage instances and the enemy's next received hit. Its reactive
  vulnerability is the named 23019 buff group; the same-ID passive buff is not that payload.
- Scout's controller links to the stacking defence buff. Detection links to a radius-bearing buff. Shinobi's
  wrapper supplies movement distance and stealth lifetime/hit limit. SpecialStatusConfig 2 is stealth.
- Mummy's owner kill window links to a distinct auxiliary curse; its utility curse is once per primary cast.
- `LC_UnitBase.UpdateDarkState/UpdateDarkRadius/DarkSelectRadius`, `LC_Unit_Player.UpdateDarkState`, and
  `LC_UnitChecker.CheckUnitInDarkArea` consume properties 2704/2705/2710 (radius in centimetres). Blind is a
  viewing/selection restriction. No original radius was recovered; the server explicitly approximates it as 5 m.
- Buff groups named Knock-Up, Freeze, Petrify, Sleep, Endure and Immunity map to the corresponding control
  states/categories. Freeze and Petrify controller data give their on-hit follow-up buff and duration.
- Raw AttributeConfig healing inputs 207/208 and 1207/1208 are distinct. SP cost inputs are 1231/1232.
  Their grouping, rounding and application order in server calculations remain inferred.

## Conflicts and unrecovered server policy

| Case | Evidence boundary / selected policy |
|---|---|
| Owl Reactive | Description and auxiliary coefficient disagree. Use the reactive description, auxiliary element/type. |
| Aliot heal | Primary description and the heal-looking auxiliary coefficient disagree. Use the description. |
| Angeling | Heal description and linked halo radius differ. The heal uses the description's radius; one-second heal ticks are inferred. |
| Incubus | Aura description and auxiliary range differ. Keep the auxiliary's range. |
| Butterfly / Sarah | Hit subdivision or exact extra-hit timing is not established by a recovered server script. Named policy is inferred. |
| Bobo / Fairy / other Reactive buffs | Family-specific candidate links, checked against full attribute payload; never a global same-ID join. |
| Blind | Client wire/rendering behavior is recovered; the radius is an explicit approximation. |

Timing points come from the skill shows, but cosmetic attack points are not universally executable hits.
Named scripts map coefficients to ordered points; callbacks revalidate their captured source. The shared emulator
damage formula, adaptive-stat selection, rounding, target priority, internal trigger ordering and state stacking
remain inferred. No official-server combat trace has established parity. See [runtime and validation](../systems/pet-guardian-skills.md).

# Astral Level (Paragon): 5015 and 83101-83105

The client calls this system PatronLevel; its panel title is "Astral Level". Function 71 opens it at base level 99
(condition 2000064 `[1002, 99]`), and the Role panel shows its button only then
(`UI_Sys_RoleAttributesWidgetCtrl.lua:163-171`). `ParagonService` owns the stars and Star Cores. The level and its EXP
are the progress columns `paragon_level` / `paragon_exp` (`RoleProgress.ParagonLevel` / `ParagonExp`), so every EXP
path stages them in the same transaction as the Base EXP that feeds them.

## EXP and levels

- **Source, inferred.** The client says only "Complete quests and Kill monsters to earn EXP" (16010/16118). Base EXP
  left over at the level cap (99) becomes Astral EXP instead of being dropped (`CharacterService.Paragon`).
- **Progress.** It is shown per level: `exp / PatronLevelConfig[lv]._iPatronExp`. Level 100 has no EXP row and shows
  "exp/MAX" (`UI_Sys_Personal_LevelWidgetCtrl.lua:461-566`). `lv` is at least 1 once the function is open.
- **Catch-up bonus.** `PatronExpAddConfig` compares the Astral Level with the server's (`PatronWordLvConfig` of the
  open day, the highest without a row). The first listed gap at or above the role's gives `_iPursueExpAdditionPercent`
  per million: 10 or more levels under the server's 100 gives +20%. 5015 `extraAdd` carries the raw value, and the
  role tips show it as `extraAdd / 10000` % (`UI_Sys_RoleTipsPanelCtrl.lua:167-198`).
- **Points (inferred).** The client never reads `_iStatusPoint`. `leftPoints` is the `_iStatusPoint` of every reached
  level, one each, minus the allocated points.

## Stars

- **runegroup keys.** `runegroup` is keyed by `PatronTalentConfig._iID` and carries every configured node, at 0 points
  too. The panel reads `runegroup[1].points` with no nil check. The ids missing from the table (28, 33, 35, 37) travel
  as empty entries as well, because the reset-all refund walks the map in order and stops at the first gap.
- **Allocation rules.** A star takes points (always 1 per request) when every node naming it in `_iPostTalentID` is at
  its `_iMaxLevel`, it is below its own cap, the budget allows and its constellation is active. The client checks only
  the budget and the cap (`UI_Sys_Personal_TalentistRunesWidgetCtrl.lua:787-831, 975-1013`).
- **activeGroup.** It is a set of group ids and always a prefix: group 1, then each group whose previous one has every
  node at its cap (`LC_PatronLevelHelper.IsTalentGroupCompleted`). Attributes count only in active groups: a node's
  `_kTalentAttrIds[points]` row of `PatronTalentAttrConfig`, plus its core's `PatronRuneConfig._kAttrs`.
- **lvPower (inferred).** It is the allocated levels' `_iSeasonPower` plus the installed cores' `_iSeasonPower`, in
  active groups. No power package unit names it, so like the pet lineup it adds to the total as season power.
- **Resets.** Reset single (83105) empties the star and every star after it along `_iPostTalentID`, and returns their
  cores (16103). Reset all (83104) empties everything. Both are free: no cost is shown anywhere.

## Star Cores

- **Matching.** A core (`ItemConfig._iSubType` 920-923, a `PatronRuneConfig` row) goes into a star whose `_iType`
  equals that subtype, once the star has its point. The Cross cores (924) fit no star in this export.
- **Leaving the bag.** An installed core leaves the bag, because the core list shows only cores not installed (16113).
  A replacement, removal (83103) or reset brings it back with its binding.
- **Receipt.** Each move is one `cd13b/paragon-*` receipt: the paid instance is a `ParagonCost` sink and a returned
  core a `ParagonGoods` source. The `paragon` blob and the bag commit together through `ExchangeLockedAsync`.
- **Empty slots.** A slot always sends its `rune`, empty as `itemEntry` 0. `OnDecodeParagonLevelRune` drops
  `runeData` only for that, and an increment cannot unset a message field.
- **Drops (inferred, `ServerOptions.StarCoreDrops`).** Cores have no source in the client data (`_iAcquire` and
  `_kItemSource` are empty).
  - A role with the Astral Level open finds one at 1 in 2000 per monster and 1 in 5 per boss (`_iNPCSubType` 3, 4 or 6).
  - The slot type is random. Quality is weighted 50/25/13/7/4/1 from Gray to Red.

## Not applied

- `runeState` is never read by the client and stays 0.
- Season resets (16084) do not happen: no season ends under the all-open policy.
- Refusal codes are inferred: function closed 2057; unknown star, inactive group, locked star, no points or at cap 82;
  core missing 154; bag full 159. The client ignores the 83101 result and reads the new state from 5015.

## Verified

- **Real client:**
  - Opened the panel from the Role panel at level 99.
  - Allocated stars along the chain and saw the next star unlock.
  - Inserted a Prism core: only matching cores were listed, the core left the bag and power rose by 5.
  - Removed the core: the slot showed empty again.
  - Reset all: refund of 13 points shown and applied.
- **Tests:** `ParagonTests` cover the EXP overflow, the chain and group rules, and core moves with a balanced economy
  ledger.

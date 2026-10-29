# Skill pass-block check (G Asia client)

## Source and scope

The audited Asia client contains 13,489 Lua payload and VFS files. Lua source references below
are relative to `Client/ro3_Data/StreamingAssets/Recovery/LuaPayload`; line numbers refer to
the readable decompilation of those exact bytes. `Config/DataConfig/SkillConfig.lua.bytes`
has raw SHA-256 `07A97ACA31C74CCEB850853660CE3E9C5FF2406C6E74DBE4FB06FBC879558339`.
All 7,978 G skill rows and their `_iPassBlock`, range and level fields match the Genesis export.
The exact-row `_iPassBlock` distribution is `-1:6`, `0:2143`, `1:527`, `2:5297`,
`4:5`. The six `-1` rows are release type 2, which has no positional ray here.
Skill groups `14312`, `15107`, and `50021` vary by row, so the selected `_iID`
must supply the pass level; a group-level default would lose client behavior.
The audited client `RoSceneSpace.dll` SHA-256 is
`48E4CDE2F6753A53388B61B8F886ED29A1F1D169F4267548CFA934861C9565B3`.
These are client-side static facts, not an original-server or player-run trace.

## What G checks

- Fire Bolt (火箭術, group `12004`) and Fire Ball (火球術, group `12012`) each have ten
  levels, `_iRelease=1`, `_iDistanceMax=1200` (12 m) and `_iPassBlock=2` in G
  `SkillConfig`. `_iPassBlock` is a **blocker level**, not the learned skill level.
  G's `ErrorCodeConfig[3026]` maps to the text `技能穿牆等級不符`; an unlearned skill
  has a different error, 3002.
- `LC_Skill_Function.CanInputSkillByPosition` passes the row's `_iPassBlock` to
  `Lua_SceneSpace.CheckIndirectPass` (`LC_Skill_Function.lua:3690-3708`). Normal scenes
  initialize RoSpace (`LC_SceneManagerNew.lua:525-530`), so the Lua method calls
  `RoSpace.CheckIndirectPass` (`Lua_SceneSpace.lua:910-934`). The managed call reaches
  `SceneSpaceDynamic.CheckInDirectPassFromTo` (`RoSpace-G-exact.cs:491-497`).
- For equal heights, `CheckInDirectPassFromTo` runs a 2D segment through
  `_checkPassPlane`; each visited cell calls `IsPlanePassable`, which tests
  `!_isBlocked` (`SceneSpaceDynamic.cs:851-877,1991-2005`). Different heights use
  two possible plane-plus-vertical paths. This is separate from G's direct-walk
  check.
- `_isBlocked` checks dynamic collision, `SceneSpaceStatic.IsBlocked`, then active
  dynamic grid block ranges (`SceneSpaceDynamic.cs:1774-1784`). Static `IsBlocked`
  tests grid level `> passLevel` only for a nonwalkable cell with no height layers
  (`SceneSpaceStatic.cs:246-257`). The G map loader calls `AddHeight` but does not
  call `SetGridLevel` (`RoSpace-G-exact.cs:126-188,192-267`); the ordinary loaded
  static grid therefore retains level 0. `AddAirWall` passes its configured
  `gridLevel` to `AddDynamic`; Lua defaults that level to 1
  (`RoSpace-G-exact.il:160593-160600`, `Lua_SceneSpace.lua:1084-1092`). The dynamic
  terrain-mask block ranges recovered from G carry level 0
  (`SpaceWalkableRectMaskData.cs:88-96`). A block must exceed the skill's level 2
  to stop these spells.
- The Lua RoSpace branch raises both endpoint `y` values by 0.5 m before the
  managed check (`Lua_SceneSpace.lua:10,910-934`). G quantizes height in 5 cm
  steps and its segment walker visits cell midpoints, endpoint and diagonal
  side cells (`SpaceCommon.cs:17-26,208-300`). These details matter around
  multi-level floors and wall edges.

## Local divergence and Ant Hell example

The pre-repair emulator returned 3026 when `_iPassBlock==2` and
`NavGrid.CanWalk` fails (`CombatService.Casting.cs:304-309`). `CanWalk` follows
walkable ground every 0.5 m and requires clearance in both adjacent cells at
a diagonal crossing (`NavGrid.cs:79-101`). G's skill predicate does not use
those movement rules.

G's map 10540 (螞蟻密穴) uses `sc_anthell02_001_dynamicmapdata.bytes`, SHA-256
`E212D5D126BC8C3A23750552C0D5EC60DA72A657DA3C800596455BBF2755829A`.
Its G scene-editor `m_kAirWalls` is empty, including the pl01 child scene.
With grounded monster spawn 282 at `(126.2496, 6.19, 170.8085)`, the
emulator's movement check rejects two in-range, same-height examples:

| Caster position | Distance | Why `CanWalk` fails |
| --- | ---: | --- |
| `(117.5, 6.19, 163.5)` | 11.4 m | sampled cell `(120,166)` is not walkable |
| `(117.5, 6.16, 164.5)` | 10.8 m | diagonal crossing requires the nonwalkable adjacent cell `(117,165)` |

These positions are offline map reproductions, not a captured player cast.
Both caster and target cells are walkable. The absence of authored AirWalls
and the G blocker-level rule make the emulator's movement-ray rejection a
source-backed false-positive candidate for this map. Runtime dynamic collision
and original-server acceptance remain `UNKNOWN`.

There are two possible 3026 producers to distinguish in a live reproduction.
The G manual skill selector can set 3026 locally when its position ray fails
(`LC_Skill_SelectorEucli_PC.lua:2259-2274`,
`LC_SkillInput_Handler_PC.lua:855-864`). The emulator can return 3026 in
`CastSkillRsp` after receiving message 31102. A client-only 3026 might also
mean RoSpace was not initialized when the check ran (`RoSpace-G-exact.cs:491-497`).
A live reproduction must pair the player Ant Hell request and result before
assigning an occurrence to either producer.

G's automatic attack state checks distance and height but does not run this
position ray before sending the cast (`LC_AutoAttack_AttackSubState.lua:899-935`).
Its `OnCastSkillReq` ignores the returned result (`:145-177`), so a server
rejection can lead to another try on the same target. This is a static control
flow inference; the retry cadence has not been measured in a player session.

## Server implementation and test coverage

`CombatService.ValidateTarget` now uses the selected row's nonnegative
`_iPassBlock` for the release types that call G's position check. This shared
server route covers manual and automatic casts. `MapWorld.CanSkillPass` applies
G's level comparison to authored AirWall cells and uses the 0.5 m height
offset; an unrelated nonwalkable floor cell no longer causes 3026. The typed
unit-spawned AirWall on map 2230101 is included. The siege-specific collision
guard remains separate. `NavGrid.CanWalk` remains the movement predicate.

`SkillPassClientDataTests` covers pass levels 0, 1, 2, and 4, authored level 3
walls, unrelated nonwalkable cells, missing scene metadata, and the unit wall.
`SkillCombatTests.SkillPass` and `SkillCombatTests.SkillPassDamage` exercise
31102/SkillHit/death paths for normal attack, Fire Bolt, and Fire Ball. The spell
cases also check a committed reward source and Base/Job progress.
`SiegeTests.QualifiedAttackCrystalSettlesOnceAndHistorySurvivesRestart` protects
the separate siege collision and settlement path. These are LAB protocol and
geometry tests, not player or original-server acceptance.

## Remaining evidence

G client static data cannot prove dynamic wall activation, original-server
decisions, or real player casting. Those remain `UNKNOWN` until a controlled
player trace; the current all-skill port is **partial** RoSpace parity.

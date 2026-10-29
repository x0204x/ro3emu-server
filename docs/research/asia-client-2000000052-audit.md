# Asia client release 2000000052 audit

This audit records read-only REA/IDA and recovered-Lua evidence from
`G:\RO3 Asia Launcher\Client`. Static evidence, server tests and live player acceptance are reported separately.

## Artifact identity

- Installed release sequence: `2000000052`
- Build id: `ro3-asia-client-asia-wedding-stage2-mapmodule-20261022`
- `ro3.exe`: `f5f73d8616a2500e0fb0223d83774e2d7f6a74c1bbdad772f4fefc9bf5812036`
- `Assembly-CSharp.dll`: `4365bccfb4badbf9d73c39cd8491f8bcfaf7604fc09c91dbc71123b100602e11`
- `xlua.dll`: `c189e0fdfb23782d381dfd761a4d746c1e195508ed8eecbe25f23a39bfc7309a`

The package declares itself a reconstructed-payload build. Its managed and xLua hashes are identical to the previously
audited Asia client; its release sequence therefore does not by itself establish a newer executable implementation.

REA reports complete managed metadata with 2,044 types and 22,758 methods
(`ev_f5d426c9743ff95231974ffbb7b7c7a29d7bb72cb06094e495bbc7a7e4923979`, parent
`ev_56c27f5819ce203aad8ef7b2796954226f874f34a23d28d094af1b1178524005`). The IDA session confirmed
that the xLua exports are generic Lua bridge functions and was closed after inspection. See
`auto-battle-evidence.md` for the native receipts.

## Skill animation

All 2,222 files under `Config/Skill` and all 37 recovered logic files whose names contain Skill, Combat or Bullet are
SHA-256 identical between the G-drive and D-drive Asia payloads. `Lua_Config_EffectData`, `SkillConfig`, Fire Bolt
(`skill_12004`) and Fire Ball (`skill_12012`) are also identical.

The server's static cast chain already contains authored caster action, projectile and hit-effect data:

1. 31100/31106 cast timeline with `PlayAct`, session, start time, target and facing;
2. 31120/31108 projectile point when the authored effect uses a bullet;
3. 31101 hit result with the authored action-damage show point.

The focused REA skill-type receipt is `ev_80e7db6a046fd4501dd8917c551d7fbf953ba68c614d0f1a05368b81e3fdfd36`
(parent `ev_f49042bc9151e1f4dbe0099044fbb42d21856e1a7700a12e64726a003e8a5c76`). The G package contains no
new animation data to copy into the server. Actual packet order and Unity rendering remain a live-trace acceptance gate.

One bounded route-specific defect was established. Guardian-pet child skills use the extra `UseInSkill` timeline, and
the recovered `LC_TimeLine_Object` executes its Animation events only when `isPlayAnim` or protobuf `playAct` is true.
The previous server message omitted `PlayAct`, although GFX events could still render. The server now emits
`PlayAct=true`, and the guardian-pet wire test asserts it. This correction does not alter the ordinary player
`31100 -> 31120/31108 -> 31101` route and does not claim that Fire Bolt or Fire Ball has passed a player-visible test.

## Party experience

`PublicParamConfig.TeamExpBuffNum` is `[10000, 12500, 15000, 17500, 20000]`, representing the same-map one-to-five
member multiplier. A forty-person team is rendered as fixed five-slot groups derived from `teamIndex` (1-5, 6-10,
and so on), not as one forty-person EXP group. The server now uses the killer's five-slot subgroup when selecting the
multiplier.

Message `5066` is `RoleBaseInfoIncrement`, whose own-role fields include `level`, `jobLevel`, `exp` and `jobExp`.
`LC_DAL_PlayerBasicInfo` registers the full and incremental role streams and writes those values into the local player
cache; the main and role-info widgets consume that same cache. `31322 GetRewardNotify` only builds the visible reward
notification and does not update EXP. `TeamMemberDetailInfo` contains member level, career, map and team index, but no
member EXP fields. The client therefore proves a per-recipient EXP update contract, not a client-side party fanout.

The server now selects eligible online roles in the killer's five-slot subgroup and same public field scene, applies
the configured team EXP bonus and divides the resulting Base/Job pools evenly. Other members receive durable EXP
obligations; the split and eligibility are **inferred server policy**, not an original-server formula. The client does
not contain the authoritative formula for splitting the original kill EXP pool, rounding a remainder, applying level
eligibility or selecting dead/offline/distant/non-participating members. Those rules remain `UNKNOWN`. A two-player
original-server runtime sample of one controlled kill is the remaining gate for distinguishing independent full grants
from a divided pool. See [party-experience-evidence.md](party-experience-evidence.md).

## Task Job EXP

The current export contains 1,072 `TaskConfig` rows: 714 have an empty `_kEXP`, 358 have `[1, N]`, none have
`[2, N]`, and none are malformed. All 632 tasks with `_iAward` were expanded through the default and playbook 101-107
multiverse selections: 5,056 routes in total. None reaches an `AwardConfig._kEXP` row and none grants item 100 (Base
EXP) or 101 (Job EXP) through `_kAward`. The four playbook selections that differ from their default route also contain
no EXP.

Only eight rows in the entire current `AwardConfig` have a non-empty `_kEXP`; type 2 occurs once at
`1001007=[2,100]`, with no reachable task or confirmed reward reference. Recovered `Lua_CFG_AwardConfig` does not read
`_kEXP`, and the bounded whole-Lua search found no consumer. Item 100/101 names prove the virtual-item meanings but do
not prove that `_kEXP` tuple tags share those numbers. The type-2 meaning therefore remains `UNKNOWN`.

Recovered `LC_DAL_Task` sends only task and NPC identity for task delivery. On success it updates task state and emits
the reward event; it neither calculates nor supplies an EXP formula. Consequently `[1, N]` remains typed Base EXP and
is not silently duplicated into Job EXP. Any hidden Base-to-Job conversion is `UNKNOWN` pending an official
server/runtime receipt. The server parser and `AwardService` now fail closed on unknown tuple semantics instead of
silently inventing a grant.

## Automatic battle

Target selection, reachability, chasing and optional Fly Wing use are client-owned. The recovered G-drive Lua filters
targets through a shared reachable-area grid, then checks `CanArriveToPos` before the actual A* chase, clears unreachable
targets and only uses a Fly Wing when its opt-in setting, scene, inventory and cooldown gates pass. When every configured
spawn is unreachable, the move-seek fallback can clear its visited set and test the same unreachable candidates again;
the automatic seek dispatch also provides no failure callback for a zero-length path. The server cannot choose the next
target or force a wing before an item-use request arrives. See `auto-battle-evidence.md` for the complete decision
boundary and the remaining live-trace gate.

## Acceptance state

| Area | Static/data state | Server correction | Live player state |
| --- | --- | --- | --- |
| General player skill animation | `VERIFIED`, G and D identical | No generic packet added | `UNKNOWN` |
| Guardian-pet cast action | Extra-timeline `PlayAct` gate verified | `PlayAct=true` implemented and unit-tested | `NOT_RUN` |
| Forty-person EXP subgroup | `VERIFIED` | Implemented and unit-tested | `NOT_RUN` |
| Party EXP distribution | Per-recipient update verified; original formula absent | Inferred same-field subgroup split and durable delivery implemented; original formula `UNKNOWN` | `NOT_RUN` |
| Task Job EXP route | No route in current 1,072 tasks | Existing typed parser retained | `NOT_RUN` |
| Auto-battle obstacle case | Reachable-grid/A*/retry chain verified | Unsafe server bypass rejected | `NOT_RUN` |

## Verification

- Guardian-pet, skill-animation-data and skill-protocol Release tests: 38 passed, 0 failed, 0 skipped.
- Focused Release tests for party EXP, task EXP parsing, all-player skill animation data and Fire Bolt/Fire Ball
  projectile/hit effects: 9 passed, 0 failed, 0 skipped.
- `Ro3Emu.Server` Release build: 0 warnings, 0 errors.
- Whitespace verification and `git diff --check`: passed.
- The full non-Slow integration suite produced no completion summary within the ten-minute bound and was stopped. Its
  result is `UNKNOWN`, not passed or failed.

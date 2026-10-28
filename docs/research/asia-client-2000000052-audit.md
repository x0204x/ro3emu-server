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

## Party experience

`PublicParamConfig.TeamExpBuffNum` is `[10000, 12500, 15000, 17500, 20000]`, representing the same-map one-to-five
member multiplier. A forty-person team is rendered as fixed five-slot groups derived from `teamIndex` (1-5, 6-10,
and so on), not as one forty-person EXP group. The server now uses the killer's five-slot subgroup when selecting the
multiplier.

The client does not contain the authoritative server formula for splitting the original kill EXP pool, rounding a
remainder, level eligibility or sending rewards to other members. Those rules remain `UNKNOWN`; this audit does not
invent them from UI-only data.

## Task Job EXP

The current export contains 1,072 `TaskConfig` rows: 714 have an empty `_kEXP`, 358 have `[1, N]`, none have
`[2, N]`, and none are malformed. Of 632 parseable default `_iAward` routes, none reaches an `AwardConfig._kEXP` row
and none grants item 100 (Base EXP) or 101 (Job EXP) through `_kAward`.

Recovered `LC_DAL_Task` sends only task and NPC identity for task delivery. On success it updates task state and emits
the reward event; it neither calculates nor supplies an EXP formula. ItemConfig proves item 100 is Base EXP and item
101 is Job EXP, but no current task route grants either item. Consequently `[1, N]` remains typed Base EXP and is not
silently duplicated into Job EXP. Any hidden Base-to-Job conversion is `UNKNOWN` pending an official server/runtime
receipt.

## Automatic battle

Target selection, reachability, chasing and optional Fly Wing use are client-owned. The recovered G-drive Lua filters
targets with `CheckAutoAtkReachable`, checks `CanArriveToPos` before chasing, clears unreachable targets and only uses a
Fly Wing when its opt-in setting, scene, inventory and cooldown gates pass. The server cannot choose the next target or
force a wing before an item-use request arrives. See `auto-battle-evidence.md` for the complete decision boundary.

## Acceptance state

| Area | Static/data state | Server correction | Live player state |
| --- | --- | --- | --- |
| Skill animation | `VERIFIED`, G and D identical | No new safe static change | `UNKNOWN` |
| Forty-person EXP subgroup | `VERIFIED` | Implemented and unit-tested | `NOT_RUN` |
| Party EXP distribution | Client does not own formula | `UNKNOWN`, not invented | `NOT_RUN` |
| Task Job EXP route | No route in current 1,072 tasks | Existing typed parser retained | `NOT_RUN` |
| Auto-battle obstacle case | Client ownership and gates verified | Server boundary documented | `NOT_RUN` |

## Verification

- Focused Release tests for party EXP, task EXP parsing, all-player skill animation data and Fire Bolt/Fire Ball
  projectile/hit effects: 9 passed, 0 failed, 0 skipped.
- `Ro3Emu.Server` Release build: 0 warnings, 0 errors.
- Whitespace verification and `git diff --check`: passed.
- The full non-Slow integration suite produced no completion summary within the ten-minute bound and was stopped. Its
  result is `UNKNOWN`, not passed or failed.

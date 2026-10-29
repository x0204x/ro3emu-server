# Remaining skill messages: implementation and evidence

This note covers the skill-family messages not described by the other skill documents, and summarizes where each of them stands. `data/` and `re/` are evidence inputs only.

The set contains **34** messages: **15 done, 17 partial, 2 missing, 0 stub**. All five skill-growth messages are done. These labels describe the documented scope, not original-server parity:

| Status | Message IDs |
| --- | --- |
| done | 31027, 31126, 31127, 31128, 31129, 31133, 31134, 31135, 31152, 31156, 31157, 31159, 31160, 31331, 31332 |
| partial | 31106, 31107, 31108, 31109, 31112, 31115, 31116, 31117, 31118, 31119, 31120, 31121, 31122, 31125, 31132, 31333, 31334 |
| missing | 31130, 31131: placeholder-only widget mapping; see the completion-boundary table below and [skill-buffs.md](skill-buffs.md). |

## Implemented behavior

* **MG_SkillGrowth:** 31152/31156/31157/31159/31160, with 31151 allocation and 31153 load integrated into persistent AUTO_SYNC **5025** plans. Plan mutations and their legacy active projections share a SQLite transaction. See [skill-growth.md](skill-growth.md).
* **Cast execution:** 31102 validates the actual configured level/mode, learned ownership, owned operator, living state, target relation, range/height/navigation, resources and cooldown before acceptance. Item/currency exchange uses the existing character transaction under its state lock; casts also have a per-session gate. HP/SP, buff piles and energy use their established owners. Interrupt 31124 and scene/role/disconnect/death teardown invalidate delayed work by cast identity, role and scene. Rejecting a different request does not interrupt an accepted cast. The refusal order keeps cooldown (3012) ahead of the busy state (3005). 31100/31101/31103/31113 retain real source/target/session identities and nonempty payloads where the client requires them.
* **Targets and movement:** friend/self/dead-friend and race masks come from `m_kSelectInfo`; selectors distinguish locked, ground, circular, rectangular and directional attacks. Heal uses the friend coefficient and its separate enemy branch. 31112 updates actual actor position and AOI for Back Slide and Arrow Repel. Resurrection targets a real dead ally; World Tree consumes configured item 1002004 through skill 2200101 and emits **31331** only after revival. Item map restrictions and status forbids are checked before charging. Battle resurrection delegates to the world-core invitation/claim/publication APIs and honors the dungeon's external-revive quota (see [world-core-evidence.md](../research/world-core-evidence.md) and [dungeon.md](dungeon.md)). No dungeon settlement is reopened.
* **Owned ground units:** Fire Wall, Quagmire, Fire Pillar, Meteor Storm, Lord of Vermilion and Storm Gust use NPC ids and interval/lifetime tuples from `_kLogicData`. Safety Wall/Pneuma create limited-hit/melee or ranged protection at the requested point. Hunter trap families create their matching NPC entities, trigger on a nearby enemy, emit **31128**, and despawn. Owner, world instance, source identity, lifetime and teardown are retained. Cast buffs are applied once, rather than once per ground tick. This is evidence for these families, not universal summon-script support.
* **Buff/resource integration:** **31332/31333/31334, 31134/31135**, actual 31048 status masks, item/movement/cast restrictions, death prevention, energy costs/recovery, late-viewer buff snapshots and controlled expiry. See [skill-buffs.md](skill-buffs.md).
* **Presentation and dynamic parameters:** **31027/31132**, actual damage **31129**, pause/resume **31133**, boss snapshot **31127**, scene clear **31126**, and bounded server-timed bullets **31120/31108** when an authored timeline lacks client bullet points. High/extra timeline and boss-CD dispatch share the actual cast/source owners. See [skill-protocol.md](skill-protocol.md) for qualified routes and exact boundaries.

Skill obstruction uses the selected `SkillConfig` row's `_iPassBlock` for targeted position checks. A block must exceed
that level; ordinary nonwalkable ground cells are not movement-ray skill blockers. Authored AirWall cells are checked
separately, while missing wall metadata and the siege unit wall retain a conservative movement fallback. Wall runtime
activation is not fully tracked. See [skill-pass-block-evidence.md](../research/skill-pass-block-evidence.md) for the
client predicate, tests, and remaining dynamic-collision limits.

## Inferred mechanics

The documents linked above enumerate plan entitlement/reset/serialization, buff stacking/status/death, resource and dynamic-parameter policies. Additional combat inferences are:

1. Cast impacts use the first authored attack point with the client timeline's piecewise chant/action scaling. Attack-point count is not treated as gameplay hit count. Script-specific multi-hit schedules still require their own implementation; the generic hit path is not original-server damage parity.
2. Existing reconstructed hit/defense/element/critical damage formulas are retained. `HitEffect` selects an authored type-3 Action damage entry because the client's 31101 path builds hit presentation from `m_kActionDamge`; type-2 Bullet entries use the separate bullet manager. `1000002` remains the Action fallback. `_iSkillEffectLevel > 0` chooses the high-priority timeline carrier, whose client callback is shared; the original routing threshold is unverified.
3. Cooldowns start at acceptance. `_iPubCDGroup` and `_iPubCD` are applied as parallel arrays, normalizing a configured row id to its skill group. `_iCdModeGroup < 0` shares the group cooldown; other values retain the explicit mode key. Original mode-sharing policy is unverified (the exact search is recorded in [skill-protocol.md](skill-protocol.md)).
4. Range slack is 0.5 m; `_iPassBlock=2` uses the existing navigation walkability segment. Shapes use configured centimetres and facing. Rows without a template selector fall back to recovery type; this fallback is not evidence of original faction/PvP policy.
5. Ground scripts with `[NPC, intervalMs, durationMs, ...]` use those first three fields. Hunter trap NPC ids follow their localized identities, with 1.5 m activation and 3 m effect radius; lifetime comes from the final description parameter. Safety/Pneuma tuple fields select radius/duration/charges. Generic named summon tuples have a bounded 30-second fallback, but no ordinary companion rental/AI lifecycle is qualified by that fallback.
6. Arrow Repel uses the described 5 m displacement and clips it to navigation. Detoxify/Healing Skills remove the described poison or silence/blind groups. Cost-type-4 two-element markers are skipped, following the live client checker; type 6 custom scenario resources are refused rather than charged from an invented meter. Live named cost types are 1/2/3/6/7/8 (`Lua_GameDefine`, `LC_Skill_Function.CheckSkillCost`).

## Completion boundaries and evidence

The skill implementation is **not complete 1:1 skill parity**. The generic implementation does not establish all 7,978 rows' script logic, PvP/party targeting, companion rental/mount AI, every dynamic modifier, or every buff opcode. The remaining message-level boundaries are concrete:

| IDs | Boundary and evidence |
| --- | --- |
| 31108/31120 | The player-cast producer is qualified only when the timeline has no `m_kClientPoints` and EffectData supplies a visible Bullet entry. It uses authored lifetime or distance/speed travel and does not emit for client-owned bullet points. Fire Bolt and Fire Ball have focused wire coverage; other script-owned projectile semantics remain partial. |
| 31109/31119 | The dispatch API is typed, but an independent effect producer is not qualified. Client `m_kShows` already plays ordinary effects, so an extra onset event could duplicate that presentation. Patterns, counts and positive references are in [skill-protocol.md](skill-protocol.md). |
| 31122 | Delta dispatch retains a visible target's real tag, but a configured target-eligibility transition is not qualified. Exact packet/selector searches return the consumer and carriers, not a recovered gameplay trigger; see [skill-protocol.md](skill-protocol.md). |
| 31130/31131 | WidgetConfig exports one placeholder row (key 0, id 1, widget 1000, empty effects/icons). Both Lua exports mirror it. Searches return display consumers and carriers but no usable grant mapping; see the widget searches in [skill-buffs.md](skill-buffs.md). Ground NPCs use their real NPC identities instead. |
| 31115/31116/31117 and 31118 | Empty legacy forbidden packets and the legacy parameter carrier are kept silent. Live restrictions are sent through 31048; dynamic parameters use 31132. Legacy-name searches and client receiver evidence are in [skill-buffs.md](skill-buffs.md) and [skill-protocol.md](skill-protocol.md). |
| 31106/31107/31121, 31125 | Actual cast dispatch exists, but the original high/extra event policy and a boss special-skill scheduler are not qualified by the normal-attack integration. Searches identify the packet consumers and the `_iImportantSkill` UI gate; they do not recover a complete AI schedule. |

Positive ground evidence includes SkillConfig 1201001/1201101/1210401/1210501/1210601/1210701/1210801/1401701 and their referenced NPCConfig rows. Companion evidence includes `LC_Pendant_Falcon.lua` (a client pendant) and Falconry Mastery's rental description; neither establishes an interactive unit spawn simply from learning the passive.

## Verification

Wire tests in `SkillGrowthTests`, `SkillBuffTests`, `SkillProtocolTests` and `SkillCombatTests` exercise the real game gate and protobuf interfaces on free test ports with temporary data directories. They include the manual-slot selected-level regression and the Endure cooldown refusal order. Run them with:

```powershell
dotnet test tests/Ro3Emu.Tests --filter "FullyQualifiedName~Skill"
```

These tests do not cover native client visual behavior.

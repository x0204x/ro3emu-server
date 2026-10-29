# Quest experience evidence

This page records the TaskConfig and award-chain evidence for quest Base and Job experience. It separates configured rewards from the independent monster-kill experience path.

## Current client data

The audited Asia and Genesis exports each contain 1,072 TaskConfig rows. In both exports, the 358 non-empty `_kEXP` values are type-1 tuples (`[1, amount]`); none is type 2. The two exports have no per-task difference in `_kEXP` or `_iAward`. Expanding all 632 tasks that carry `_iAward` through the default and playbook 101-107 selections covers 5,056 routes. Four playbook selections choose a different award from their default.

The full-route audit followed `TaskConfig._iAward -> MultiverseGroupConfig -> AwardConfig -> DropPoolConfig -> DropGroupConfig`, including level-banded pool rows. The audited Asia export resolves all 5,056 routes. The audited Genesis export resolves 5,048: task 9201101 names group 920160, which Genesis lacks, across all eight selections. Asia has the identity group and both exports have AwardConfig 920160 (item 920132 x 2,000), but the missing Genesis group is not permission to invent a fallback. This task has empty `_kEXP`; its actual acceptance and delivery remain unobserved.

No route reaches non-empty `AwardConfig._kEXP` or a fixed `_kAward` virtual item 100/101. That statement does **not** cover pools: 27 type-24 tasks (3001-3009, 3011-3019, 3021-3029) have Base EXP item 100 through pool 84001-84009, in 216 task/playbook routes. Each pool's EXP slot has chance 1,000,000/1,000,000 and one roll/pick; its group has one eligible row per Base level 1-99. The Genesis rows explicitly name item 100; the Asia rows rely on the server's documented group-3000 default. For a claimable task and an eligible level, the current server's pool resolver therefore selects one Base EXP grant. No audited task route has a fixed or pool Job EXP item 101. Tasks 3004, 3008, 3014 and 3018 also have `_kEXP=[1,60000]`, separate from their pool grant. Counting that overlap, 381 tasks have a configured Base EXP path and 691 have none; all 1,072 have no configured Job EXP path. These are configuration and source-path conclusions, not an original-server runtime or player grant capture.

The exact G client bytes were normalized only in an isolated LAB copy to recover its Task, MultiverseGroup and Award CFG readers. They show that `GetTaskAward` follows `_iAward` to a season-specific award **preview**. Of the 5,056 main-task routes, 1,776 routes across 222 tasks have `AwardConfig._kPreview` configured with both virtual item 100 and 101. A special-preview condition may select different display data; the actual UI was not run. These preview rows do not add a grant terminal: actual fixed/pool task routes above still contain no Job item 101. `GuildTransportTaskConfig` adds four rows and 20 award groups outside the 1,072-row TaskConfig inventory; four first-stage `_kSpecialPreview` rows configure 100/101, while their fixed/pool terminals have neither. Original-server Job award behavior therefore stays `UNKNOWN`; do not use preview quantity 1 as a grant amount.

Across the whole current `AwardConfig`, only eight rows have a non-empty `_kEXP`: seven type-1 rows and one type-2 row (`1001007=[2,100]`). The type-2 row has no reachable task or other confirmed reward reference in the audited client. `Lua_CFG_AwardConfig` consumes conditions, previews and fixed awards but never `_kEXP`; a bounded search of the recovered Lua finds `_kEXP` only where the data tables assign it, not a runtime consumer. ItemConfig and the protobuf enum prove that virtual items 100 and 101 mean Base and Job EXP respectively, but they do not establish that the unrelated `_kEXP` tuple tags use the same numbering.

Task 10101040 is a concrete example: `_kEXP=[1,7]`, and its award group contains no pool or extra Job EXP. The corresponding wire test expects exactly seven Base EXP, once, and verifies the result after relog.

For an export with a missing task award group, the [candidate preparation tool](../guide/task-award-repair.md)
can copy the proven mapping from a second local export after checking the target award groups. It does not add a
runtime fallback or embed client data in the repository.

## Server interpretation

`TaskService` currently accepts type 1 as task Base EXP, scales it with `RewardRates.BaseExperience`, and passes it to the character award transaction. `AwardService` separately resolves configured fixed and pool grants; virtual item 100 is Base EXP and item 101 is Job EXP, each with its own configured rate. `CharacterService` combines the task Base amount and resolved item-100 amount in one award transaction. Monster-kill Job EXP is a different inferred rule in `RewardService` and must not be copied into quest delivery.

The current data and implementation therefore agree that task `_kEXP` is Base EXP-only, and the 27 configured pool paths add possible Base EXP separately. Copying either amount to Job EXP, or inventing a Base-to-Job ratio, would contradict the available client data. Whether the original server grants Job EXP for a particular task remains `UNKNOWN` until a same-task before/after capture establishes it.

## Known forward-compatibility limit

The task parser now accepts only the observed empty shape and `[1, amount]`, and fails closed on malformed, negative or unknown tags. `AwardService` likewise refuses `AwardConfig._kEXP` because its application rule is unresolved. A future client or official trace that confirms a type-2 quest or award tuple would require a typed Base/Job representation, an explicit Job-rate path and an atomic wire/relog/replay test. No current reachable shipped row exercises that path, so adding a speculative Job grant now would not fix an observed data contract.

## Acceptance state

| Gate | State |
| --- | --- |
| Asia/Genesis task reward-field comparison | `VERIFIED` |
| Default and playbook task award routes | `VERIFIED` static inventory: 5,056 routes per export; Genesis has one missing group across eight routes |
| Level-banded task award pools | `VERIFIED` config/source: 27 tasks, 216 routes with Base item 100; no Job item 101 |
| Current type-1 Base EXP delivery and replay protection | `VERIFIED` (source/tests) |
| Current task-configured Job EXP | `ABSENT` in both audited exports, including fixed and pool award terminals |
| Task 9201101 award group | Asia defines identity group 920160; the audited Genesis export needs that client-backed group; actual player delivery `NOT_RUN` |
| Meaning of unreachable AwardConfig type 2 | `UNKNOWN` |
| Original-server task Job EXP behavior | `NOT_RUN` |
| Job EXP parity for a claimed affected task | `UNKNOWN` |

# Quest experience evidence

This page records the TaskConfig and award-chain evidence for quest Base and Job experience. It separates configured rewards from the independent monster-kill experience path.

## Current client data

The audited Asia and Genesis exports each contain 1,072 TaskConfig rows. In both exports, the 358 non-empty `_kEXP` values are type-1 tuples (`[1, amount]`); none is type 2. Expanding all 632 tasks that carry `_iAward` through the default and playbook 101-107 `MultiverseGroupConfig` selections covers 5,056 routes. Every route resolves as award type 0, and none reaches a non-empty `AwardConfig._kEXP`, virtual item 100 (Base EXP) or virtual item 101 (Job EXP). Four playbook selections differ from their default award, but those routes also contain no EXP. The two exports have no per-task difference in `_kEXP` or `_iAward`.

Across the whole current `AwardConfig`, only eight rows have a non-empty `_kEXP`: seven type-1 rows and one type-2 row (`1001007=[2,100]`). The type-2 row has no reachable task or other confirmed reward reference in the audited client. `Lua_CFG_AwardConfig` consumes conditions, previews and fixed awards but never `_kEXP`; a bounded search of the recovered Lua finds `_kEXP` only where the data tables assign it, not a runtime consumer. ItemConfig and the protobuf enum prove that virtual items 100 and 101 mean Base and Job EXP respectively, but they do not establish that the unrelated `_kEXP` tuple tags use the same numbering.

Task 10101040 is a concrete example: `_kEXP=[1,7]`, and its award group contains no extra Job EXP. The corresponding wire test therefore expects exactly seven Base EXP, once, and verifies the result after relog.

## Server interpretation

`TaskService` currently accepts type 1 as task Base EXP, scales it with `RewardRates.BaseExperience`, and passes it to the character award transaction. AwardConfig virtual item 100 and 101 remain separate Base and Job EXP grants, each with its own rate. Monster-kill Job EXP is a different inferred rule in `RewardService` and must not be copied into quest delivery.

The current data and implementation therefore agree: ordinary quest `_kEXP` is Base EXP-only. Copying the same amount to Job EXP, or inventing a Base-to-Job ratio, would contradict the available client data. A report that a quest Job reward feels wrong remains `UNKNOWN` until an original-server before/after capture shows a Job EXP change for the same task.

## Known forward-compatibility limit

The task parser now accepts only the observed empty shape and `[1, amount]`, and fails closed on malformed, negative or unknown tags. `AwardService` likewise refuses `AwardConfig._kEXP` because its application rule is unresolved. A future client or official trace that confirms a type-2 quest or award tuple would require a typed Base/Job representation, an explicit Job-rate path and an atomic wire/relog/replay test. No current reachable shipped row exercises that path, so adding a speculative Job grant now would not fix an observed data contract.

## Acceptance state

| Gate | State |
| --- | --- |
| Asia/Genesis task reward-field comparison | `VERIFIED` |
| Default and playbook task award routes | `VERIFIED`, 5,056 routes; no Base/Job EXP terminal |
| Current type-1 Base EXP delivery and replay protection | `VERIFIED` (source/tests) |
| Current task-configured Job EXP | `ABSENT` in both audited exports |
| Meaning of unreachable AwardConfig type 2 | `UNKNOWN` |
| Original-server task Job EXP behavior | `NOT_RUN` |
| Job EXP parity for a claimed affected task | `UNKNOWN` |

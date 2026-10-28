# Quest experience evidence

This page records the TaskConfig and award-chain evidence for quest Base and Job experience. It separates configured rewards from the independent monster-kill experience path.

## Current client data

The audited Asia and Genesis exports each contain 1,072 TaskConfig rows. In both exports, the 358 non-empty `_kEXP` values are type-1 tuples (`[1, amount]`); none is type 2. Expanding every task `_iAward` through its multiverse and award rows finds no task path that grants virtual item 101 (Job EXP), and the two exports have no per-task difference in `_kEXP` or `_iAward`.

Task 10101040 is a concrete example: `_kEXP=[1,7]`, and its award group contains no extra Job EXP. The corresponding wire test therefore expects exactly seven Base EXP, once, and verifies the result after relog.

## Server interpretation

`TaskService` currently accepts type 1 as task Base EXP, scales it with `RewardRates.BaseExperience`, and passes it to the character award transaction. AwardConfig virtual item 100 and 101 remain separate Base and Job EXP grants, each with its own rate. Monster-kill Job EXP is a different inferred rule in `RewardService` and must not be copied into quest delivery.

The current data and implementation therefore agree: ordinary quest `_kEXP` is Base EXP-only. Copying the same amount to Job EXP, or inventing a Base-to-Job ratio, would contradict the available client data. A report that a quest Job reward feels wrong remains `UNKNOWN` until an original-server before/after capture shows a Job EXP change for the same task.

## Known forward-compatibility limit

The parser currently turns an unknown `_kEXP` type into zero. A future client that introduces a confirmed type-2 quest tuple would therefore need a typed Base/Job representation, an explicit Job rate path and an atomic wire/relog/replay test. No current shipped row exercises that path, so adding a speculative Job grant now would not fix an observed data contract.

## Acceptance state

| Gate | State |
| --- | --- |
| Asia/Genesis task reward-field comparison | `VERIFIED` |
| Current type-1 Base EXP delivery and replay protection | `VERIFIED` (source/tests) |
| Current task-configured Job EXP | `ABSENT` in both audited exports |
| Original-server task Job EXP behavior | `NOT_RUN` |
| Job EXP parity for a claimed affected task | `UNKNOWN` |

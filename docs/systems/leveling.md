# Levelling from Lv 20 to 40

Where a character's Base EXP comes from between the first and the second job change, as the client's tables give it.

## The story to main task 10105090

The main chain (`10101010` onwards) pays 1,398,053 Base EXP up to `10104090` and 2,148,153 up to `10105090`
(`TaskConfig._kEXP` plus the item 100 of each `_iAward`). `BaseLvConfig._iBaseExp` puts Lv 30 at 1,377,405 and Lv 34 at
2,144,805 cumulative, so the story alone carries a character to about Lv 34 before any kill. Two gates sit on the way, both
Growth Manual chapters the panel shows (not the quest list):

- `10104100` waits for chapter 1 (`701990`). Its talent child `701020` asks for 40 talent levels: tree 10101 holds exactly
  40 (8 nodes to level 3, 4 to level 4, `SeasonTalentEffectConfig` groups 110101-110104 at 10 Talent Remnants and 100 Minted
  Coins a level), so 400 Remnants. The content before it pays exactly 400: 250 from `10103160`, `10104010`, `10104050`,
  `10104080` and 150 from the chapter's own `701030`, `701040`, `701050`. No slack, nothing missing
  (`LevelingTests.GrowthChapterOneTalentsArePaidByTheStoryBeforeIt` plays it).
- `10105030` waits for chapter 2 (`702990`): 60 talent levels (20 more in tree 10102, 200 Remnants; the story pays 450
  more before it) and 180 stat points spent (Lv 32).

Before `10105090` no daily content opens: To-Do (function 25) and Dodo's ring (5902) both open on condition 4004
`10105090` (`FunctionConfig._kOpenCondition`), and Bonus Time (function 18) opens at Lv 37. Field kills
(`NPCDropConfig` environment 0) are the only other EXP.

## Daily EXP after 10105090

`DailyActivityConfig._iTaskReceiveAction` `[31, n, task...]` is ActionConfig 31 `GiveoutAllTask`, a server-host action:
the client's Receive button does nothing for it, so the server hands the tasks out (`DailyActivityService.TrackingTasks`)
as soon as the row's function is open. In Lv 20-50 every reachable row does: the type 5 tasks (liveness and `1400011`),
the type 16 guild activity tasks and the type 9 Tavern quest `2000` (whose target 106010 has no server source yet). The type 23 rows `3200`, `3210`, `3211`, `3213` stay closed by their own
`_iCondition` (`[1002, 999]`, Lv 999), and `114`, `126`, `3003`, `3013` only in their calendar window. A row's `_iiLv` is
only the requirement its tip shows (`UI_Sys_ActivityTipPanelCtrl`); the server gates on the function, as the client does.

The EXP comes from two places, per day:

| Source | Rule | Lv 34 | Lv 37 |
|---|---|---|---|
| Dodo's ring (3302, one chain a day) | 9 nodes paying `DropGroupConfig` 3511-3519 (item 100, level row) + `_kEXP` 60,000 on two nodes (one on chain 3021) | 574,713 + 120,000 = 694,713 | 582,120 + 120,000 = 702,120 |
| To-Do boxes 7001-7004 | `GenericStageRewardsConfig` type 7 at 20/40/60/80 liveness, each `DropGroupConfig` 3501-3504 at the level | 2 boxes: 255,432 (3 with a guild caravan: 383,148) | 4 boxes: 517,448 |
| Total | | 950,145 (1,077,861) | 1,219,568 |

Liveness (item 5) comes from the tasks' own awards: `107` Consume 100 Vigor (20), the ring's terminal node (20), `101`
guild caravan (20, needs a guild); from Lv 37 `105` five MVP takedowns (4 each) and `106` 60 minutes of Bonus Time (40).
Box 7005 (100) pays Minted Coins, not EXP. From the end of the story (2,148,153) to Lv 40 (3,853,905) is 1,705,752 EXP:
about two days of ring and boxes, plus Bonus Time kills from Lv 37.

### Compensation mail

`PublicParamConfig.DailyTaskCompensationMail` names MailConfig 1058, "You didn't Claim the rewards from your Daily Quests
Yesterday, so we've reissued them by mail". (inferred) At the daily reset the boxes the ending day reached and did not claim
are rolled as their claim would have paid them, kept in `DailyState.Compensation` by that day's epoch, and mailed once
(delivery key `daily-compensation/{role}/{epoch}`), at the running reset or the next login
(`DailyActivityService.Compensation.cs`). A letter that cannot be delivered stays owed and is retried.

## Faster field levelling

Not original: an operator who wants quicker levelling sets `Ro3Emu:FieldExpRates` (or `!fieldexp` in game), which scales
the Base EXP of open-field kills by the killer's level ([monster AI](monster-ai.md)). Empty by default.

## Tests

```powershell
dotnet test tests/Ro3Emu.Tests --filter "FullyQualifiedName~LevelingTests|FullyQualifiedName~DailyTests"
```

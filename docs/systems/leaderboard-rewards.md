# Leaderboard rank rewards

A `LeaderBoardsConfig` row with `_iRankRewards` lists `LeaderBoardsGiftConfig` rank bands. The rank reward widgets
(`UI_Sys_RankRewards*`, `UI_Sys_Rank_TipAwardWidgetCtrl`) preview each band's `_kReward` (a MultiverseGroupConfig
award), and `Lua_CFG_LeaderBoardsGiftConfig.GetGiftIdByGiftList` picks the first band whose `_kParaNum` [first, last]
holds a place. `_kPrizesTimeNew` is the settlement time the rank panel prints (`UI_Sys_RankPowerWidgetCtrl`):
`[1, hour]` daily ("Daily Ranking Reward Settlement at ${1}"), `[2, weekday, hour]` weekly, `[3, activity]` after an
activity ends.

In this client two boards pay rewards, both daily at 05:00 realm time:

| Board | Bands | Letter |
|---|---|---|
| 1011 Elf Tower race | 8001: places 1-10 | 1104 "Elf Tower: Race to the Top Ranking Rewards" |
| 1301 Fashion Rating | 130101: places 1-10 | 1057 "Fashion Rating Ranking" |

`RankRewardService` checks every few seconds which settlement time of each rewarded board has passed last, and mails
the places of the board as it stands then (`RankService.RoleTopAsync`) with the band's award as attachments and the
place as the letter's `${1}`. The delivery key `rank-reward:<board>:<settlement>:<role>` pays a role once per board and
settlement, also after a restart or a retried failure; `server_state` `rank_rewards` keeps each board's last settlement.

**(inferred)**: the board at the settlement moment is the result (`_iRefresh` 4, these boards never reset); ties keep
the board's own order; a settlement missed while the server was down is paid once, at the next start, with the board
as it is then; a board never settled before (a new realm, or the first start with this feature) starts with its next
settlement instead of paying the board at once; weekday 1 is Monday. No rewarded board uses the activity type.

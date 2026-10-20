# Everything open (`Ro3Emu:OpenContent`)

With this option, every piece of content the client can show is playable. Seasons, the original server's
switch-offs and functions it never opened gate nothing; only the client's own limits remain.

The option is off by default, so the test suite keeps pinning the original's rules. A hosted server that wants all
content open turns it on:

- `appsettings.json`: `"Ro3Emu": { "OpenContent": { "Enabled": true } }`
- systemd unit or drop-in: `Environment=Ro3Emu__OpenContent__Enabled=true`
- local development: `appsettings.Development.json` (already on)
- tests: `o.OpenContent.Enabled = true` (`tests/Ro3Emu.Tests/OpenContentTests.cs`)

## What it changes

| Area | Original rule | With OpenContent | Where |
|---|---|---|---|
| Playbook | 5021 playbook from the options/DB, `!season` switches it | stays 101, the only playbook with its own content; `!season` and `!seasonnext` refuse | `GmCommands` |
| Functions the original never opened (base level 999/9999) | closed | `Functions` are open: 7201 Realm War map, 70122 Totem Shop, 41 Feedback (panel from `ClientHooks.FeedbackButton`, backend `FeedbackService`, [feedback.md](feedback.md)), 69 Season page (level 50 until the 20 Oct 2026 client closed it at level 9999). Shells without a working panel stay closed (no dead menu buttons); a function joins the list together with its backend (Adventure Group, Valhalla) | `FunctionService.IsOpen` |
| Server-day conditions (3031 open days, 3030 playbook seconds) | met on day N | met from the first day (Guild League, auctions, Ilmata, guild challenge difficulties, card flash outputs, system stall items) | `FunctionService.ConditionMet`, `GuildChallengeService.Unlocked`, `CardService.Flash`, `StallsService.System` |
| Activity open conditions | `_kOpenCondition` | `Activities` are forced: 200 Pass I (its own condition is base level 99, see [activities-battlepass-monthcard.md](../systems/activities-battlepass-monthcard.md)). Novice Challenge 112 is left out, its event texts are filler; `SeasonStageActivities` forces the season page's stage activities (all `[1002,99]`) | `NewActivityService.Gate` |
| Season Exploration chapters | `ExplorePhaseConfig` days 8/10/12 to 14 | open from the opening, never close | `NewActivityService.WorldPhaseOpen` + `ClientHooks.ServerDayGates` |
| TimeLimit windows bounded by the server opening or a season stage | e.g. days 9-30, stage 0 | open from the opening, never close (card pools, Pass I/II, Battle for Prontera, Ode to Echoes, tower races, Season Preview...); `KeepTimeLimits` 1021 Midterm Settlement, 1023 Top 10 Guilds, 1036 Feedback keep their bounds | `ActivityClock.Bound` |
| Absolute-date activities that ended | closed for good | repeat with their own length (Otherworldly Invasion 102: 10-day cycles, donation in the first 4 days); `PerpetualAbsolute` 1060 (Territory War activity) is open for good | `ActivityClock.ResolveActivity`, `ActivityClock.Bound` |
| Test card pools 1011-1014 | open by their windows | closed (`ClosedActivities`) | `NewActivityService.Applies` |
| Territory Wars | the exported 2026-05-19..27 block, once | the block repeats every `TerritoryWarCycleDays` (14); each repeat is its own territory season: `TerritoryWarsClock.Epoch` = `SeasonStart:PlayBook:cycle`, so honor/valor archive and award/settlement keys are per cycle (cycle 0 keeps the original key) | `TerritoryWarsClock`, `TerritoryWarsService`, `.Results` |

## Not covered yet

- Territory escort (`TerritoryEscortService`) still archives per season (`SeasonStart:PlayBook`), not per war cycle.
- Reward scaling by realm age (`RewardService.RollAuctionAwardBound`, guild caravan pools) still follows the real
  open day: it is pacing, not access.
- The client computes some gates itself from `ServerOpenTime` (`ServerInfo.iOpenDayCount`). The server still sends
  the real opening time, because faking it would shift every window the client derives from it. The two that refuse
  content are patched by `Ro3Emu.ClientHooks/ServerDayGates.cs` so they follow what the server reports:
  - season cards (`UI_Sys_Season_CardWidgetCtrl`, `UI_Sys_Season_ActiveNewFuncitonListWidgetCtrl`): the "opens on
    day N" lock of an Action card (Realm of the Gods 61, `[3031,5]`; 72 and 28 only have level gates) reads as reached
    when 5056 reports the function open (`LC_FunctionHelper.GetFunctionProgressByDefine` wrapped);
  - client-evaluated 3031/3030 conditions (`LC_ConditionDefine.m_kCheckFunctionGroup`), e.g. the guild challenge
    difficulties Hard/Nightmare (`GuildChallengeConfig` `[3031,3]`/`[3031,5]`): met while the server reports open a
    function whose own day gate is still ahead (60, 1324, 1323 on days 1-4, pinned by `OpenContentTests`);
  - the season page (top bar "Season", function 69) in the season's first week: stage 101001 (days 1-7) is hidden
    (`MultiverseStage._iHide`), so `LC_SeasonHelper.GetCurrentStageId` is nil until day 8 and the button only pops
    UIWords 70039, and the chapter panel locks every stage after the current one. While the server opens content (it
    reports open a function no level reaches: 7201, 70122, 41) the wrapped `GetCurrentStageId` answers the last
    visible stage with activities (101004), so the page opens and every chapter counts as started (the break stage
    101005 is kept when reached). The stage clock (82707) is not moved;
  - Season Exploration chapters (activity 111, `ExplorePhaseConfig` phases on days 8/10/12 to 14): the wrapped
    `LC_SeasonExplorationHelper.IsChapterInActivityTime` answers true while the server opens content (movement
    chapters, play panel, big-map tabs, condition 16001); the server's `WorldPhaseOpen` opens them with
    `PerpetualWindows`. Pinned by `FreshRealmTests`.

  The season page's stage activities (`MultiverseStage._kStageActivity`: 105, 106, 107, 109, 110, 111, 115) all carry
  `[1002,99]`, the final level cap (day 198 of `BaseLvLimitConfig`). With `SeasonStageActivities` (on) the server
  opens them at any level, so the chapter cards read the server's window (Ongoing) instead of the client's config
  fallback ("Opens in N days"). Their windows still apply: 107 Middle Season keeps its settlement bounds.

  A fresh realm (new `DataDir`, `GameDataDir` on the export) also needs the message table: `MsgCatalog` reads
  `msgtable.tsv` from `DataDir`, else from `GameDataDir`; without it no unrouted request gets its empty reply. The
  Fashion tab's 82338 `COMMODITY_GET_INFO` is answered by `ShopService.CommodityInfoAsync` either way.

  Still on the client's day count, display only: the level-cap line and tip by open day (`BaseLvLimitConfig`, the
  server applies no cap), world level (3033), the guild member list's "last week / this week" activity column and the
  daily schedule's week number. Not covered: the AFK adventure stages (`OnHookDungeonConfig._kUnlock`, world level
  3033; no server backend, a mobile mechanic) and the Guild Strategy Library unlocks (`[10002,101,days]` since the
  playbook start; no server backend).
- Launch Fest (function 50, ActivityConfig 10000001) stays closed: its task rewards have no message id in the client.

# Title ranks: 5031 and 43102

`TitleConfig` holds 21 ranks, from "Beginner" through "Wind's Departure", "Cloud's Splendor" and "Star Traveler" to
"Soaring Sky V". Each rank has:

- a base level (`_iLev`);
- a best-power requirement (`_iPower`);
- a Crest cost (`_kSpend`, items 1250801-1250808);
- a power bonus (`_iPowerAdd`).

The original never opened the system: function 14 needs base level 9999, and no client code hosts its widget
(`UI_Sys_Role_TitleWidget`).

## Server (`TitleService`)

- The rank is role blob `title` (`DBTitleInfo`). 5031 goes out in full at login. Later ranks go out as increments,
  because `LC_DAL_Title.UpgradeUI` plays the upgrade animation whose end event clears the widget's request lock
  (`pbLocked`). A full sync would leave the widget saying "Too many requests".
- 43102 (an empty request from the widget's Advance button) claims the next rank by `_kIndex`. Refusals:
  - base level below `_iLev` (`LevelTooLow`, the widget's text 31016);
  - best power ever reached (`PowerService.History`, the widget's `maxHistoryPower`) below `_iPower` (82);
  - short of the `_kSpend` Crests (154).
- The Crests are paid in one `cd13a/character-title` receipt with the blob. The reply carries the new rank; the client
  ignores it.
- The rank adds its `_iPowerAdd` to the titleScore power (`PowerService.TitleId`) and fills
  `RolePublicBaseInfo.title`. `_kAttr` is `[[101,0],[103,0],[104,0]]` in every row, so stats do not change.
- OpenContent forces function 14 open (`OpenContentOptions.Functions`).

## Client (`Ro3Emu.ClientHooks.TitleButton`)

- The character window gets a Title button, a clone of the Astral Level button.
- A click builds the widget into the window's content, as the mobile role panel's tabs are built:
  `Lua_UIManagerNewIns:CreateWidget(UINames.UI_Sys_Role_TitleWidget, window, View.m_kRTRANSFORM_Content:GetTransform())`.
- The widget was drawn over the mobile panel's own background. Here it gets a parchment backdrop, and the attributes
  widget under it is hidden.
- The window's close returns the widget.
- Each click resets `lc_dal_title.pbLocked`, so a refused request cannot lock the widget for the session.

## Inferred

- **Crest source.** The Crests have no source in the client data. A boss kill (`_iNPCSubType` 3, 4 or 6) drops one at
  `ServerOptions.TitleCrestPercent` (25%), of its level's tier:

  | Boss level | Crest |
  |---|---|
  | below 50 | Journey |
  | 50-89 | one tier per 10 levels |
  | 90-94 | Sun |
  | 95-99 | Heroes |
  | 100 and up | Hero Token |

- **PvP bonus.** The bonus against lower titles (`TitleBuffConfig`, `Formula_ATitleDamageAdd`, the widget's "Title
  level differences will trigger Extra effects") is not applied yet.

## Verified

- **Real client.**
  - The Title button appeared beside Astral Level.
  - The widget opened with the current and next rank, the requirements, the rank list and Advance.
  - Two Advances claimed "Beginner" and "Wind's Departure I". Each played its animation and unlocked the widget for the
    next request.
  - The rank survived a restart.
- **Tests.** `TitleTests` cover the full login sync and the rank order. They also check the level, power and Crest
  refusals, the increment, the Crest payment and the public title.

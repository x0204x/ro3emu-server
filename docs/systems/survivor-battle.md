# Survivor Battle in the match

The lobby, queue, ready check, stages, careers, landing, items, the poison zone, death and settlement are described in
[timed activities](activities-timed-events.md). This page covers what the client shows around them: how players look,
landing together, spectating, the shops, the encounter cards, the emote wheel and the weekly tasks. Code lives in
`TimedBattleService.Chicken*.cs` and `TaskService.ChickenTasks.cs`. Anything the client data does not say is marked
*(inferred)* in the code.

## Unit custom data

Much of the match is not a message but custom data on the player's own entity (`Lua_CustomDataDefine.lua`, handlers in
`LC_CustomDataHandler_Chicken.lua`). The server keeps it in the entity, so a player who comes into view later sees it,
and pushes 31018 to the room when it changes. All of it is cleared before the player leaves the room.

| Id | Name | Value | Effect |
|---|---|---|---|
| 5000007 | Chicken_PlayerCareer | career | The model becomes the career's `_iUnitB` / `_iUnitG` (`EatChickenGameCareer`) |
| 5000006 | Chicken_HUDClothQuality | quality of the worn clothes | Quality icon behind the name |
| 5000008-5000010 | ChickenOpenShop / Adventure / Ambulance | 1 open, 0 closed | Head sign while the player is in that panel (82672) |
| 4000003 | EmojiInfos | [emote, time in s] | Head bubble for `BubbleTime` seconds (83304) |
| 1002 | Unit_CameraBindUnitCustomData | watched role, 0 = own | Spectating camera |

1002 is different: `ProcessCamera` binds the camera of whichever client receives it, so it goes to the watcher alone
(`DirtyDataConfig` 1002 has `_iSyncScope` 0, the scope of the self-only attributes) and never rides on the entity.

## Landing together

On the ChoosePos map (`UI_Sys_Chicken_MapPanelModel`) the client sends only 82628, with three meanings: follow a
teammate (also accepting an invite), stop following, or invite one teammate or all (role 0). The server answers each
change with the team's whole relation in 82673 (follower to followed, plus invite cooldowns) and sends the invitation
itself as 82674. The checks follow the panel: no following inside one's own follow group or someone who follows
another; no invite while following, before a landing pick, or inside the 5 s cooldown (`InviteCdChicken`). A follower
lands at its leader's pick.

The map draws teammates one by one and enemies as crowds per map cell, so a pick goes to the team as an 82676 delta and
to everyone else as the whole lobby in 82630.

## Spectating

A player eliminated while a teammate still fights gets 82675 with a time 10 s ahead (`EatChickenObserve`, *(inferred)*
use). The client only counts down; when the time comes the server binds the camera to the first fighting teammate and
shows the watcher as `Watch` in 5036. 82640 switches to another living teammate (the client allows it every 2 s), and a
watcher whose teammate falls is moved on. A revive gives the camera back.

While a player watches, the server keeps its position, and with it its area of interest, on the watched teammate,
and nobody else sees it *(inferred: the eliminated body is gone for the others)*. Its own client gets no movement for
that; a revive teleports it to the chosen point.

## Shops

The Kafra Shop 15000054 (shop 801: items, equipment, skills) and the Equipment Merchant 15000060 (shop 802) stand in
the scene editor's refresh regions 877 and 866 from the battle start *(inferred: how many and where)*. Their panel is
client-hosted; it asks 82334 with the NPC only and lists exactly what the answer holds. Each `ShopConfig` row draws its
`_kGroup` commodities by `_iWeight`; each NPC sells `_iGlobalPurchase` of one, a player buys up to `_iPurchaseNum` in
the match *(inferred stock model)*. The purchase is the ordinary 82332 with the NPC as `npcId`, paid in Kaka Coin 10006
from the match pack; the changed stock follows in 82334. These two messages and the NPCs' 32700 run under the match's
reservation, not the role's.

## Encounters

The Magic Die 15000301 (region 899) runs `AdventureEventConfig` 10001: three cards of pool 1003 to choose one from,
re-rolls for 1000 / 2000 / 3000 / 4000 Kaka Coin, at most 10, 10 s to choose (`AdventureDeckConfig`). The offer reaches
the client only through 5039 `DBAdventureInfo`: a new `needSelectMap` entry opens the panel. 83421 picks and 83422
re-rolls, each answered before the 5039 that follows. When the countdown ends the server takes the first cards and
answers 83421 itself so the client's queue clears. Card effects: buffs (203), Kaka Coin (326), every worn item one tier up
(338). One die serves one player and goes away *(inferred)*.

## Emote wheel

43304 saves the six-slot wheel of dungeon 0 (Survivor) in the role's `emoji_info`; 5041 carries it. A role that never
edited it gets the Survivor emotes marked `_iAssemble 1`. 83304 plays a wheel emote in the match.

## Weekly tasks

Tasks 2501-2507 (`_iTaskType` 10) are offered while Survivor Battle is open and start over each game week (Monday
05:00) *(inferred)*; condition 6508 compares the Survivor rank. Settlement adds what the match brought: placing in the
top n, items of a type used, supply crates opened, and for the whole team equipment of a quality obtained, Camouflage
casts (skill families 151001-151004) and players defeated. Monster kills have no source, as no monster is spawned. A
finished task pops the client's completion panel through 31211; it is claimed with 31205.

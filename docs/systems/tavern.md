# Tavern: 82481-82514, 5043 and the PartyService room list

The Tavern (FunctionConfig 2410, level 36, opened from the chat side button) is a lobby of voice and text rooms. A
player browses rooms by topic and language, creates one, joins, chats in it, asks for one of the mic seats, and gives
drinks to other members. The owner runs the room. A daily quest pays for 20 minutes spent in any room, and two
achievement lines count those days and the Star Power received.

The client keeps no room state of its own (`LC_BarRoomHelper`): every reply that carries a room (`BarRoomTotalDetail`)
replaces what it shows, and every 82506 push makes an open Tavern window ask 82508 for the whole room again. The client's
offline mock (`Lua_BarRoom_Mock.lua`) is switched off in this build and its shapes are older than the proto, so the
handlers in `LC_DAL_BarRoom` / `LC_BarRoomHelper` are the reference.

## Server (`TavernService`)

### Rooms

- A room lives in memory: guid, owner, topic, language, name, members (join time, area), the speaking mode and its
  list, mic requests, the mute and Blocks lists, heat and tags. It closes when its last member leaves.
- Guids start at `VoiceRoomKey.TavernBase` (2^40). The client joins the room's voice as the guid in decimal, the same
  shape as a party's team id, so the two ranges must never meet.
- 82481 create: name (trimmed, 1-18 characters, `BarRoomNameLen`), topic (`BarroomTypeConfig._iType` 0) and language
  (`_iType` 1). Refusals 2576 / 2577 / 2579 / 2580, and 28 while the function is closed. The creator owns the room.
- 82482 enter: 2552 room gone, 2571 already in it, 2553 on its Blocks list, 2572 at `BarroomPersonNum` (1000).
  The others get 82507 UserEnter (their client asks for the room and shows the welcome line).
- 82483 exit, 82508 get (2578 when the caller is in no room: `OnPartyGetClient` cannot take an empty room).
- An owner who leaves hands the room to the first seated speaker who is online, else the longest-present online member.
- 5043 `DBBarRoomInfo.barRoomId` is the room the role is in: the client joins its chat and voice from it after a login.

### The room list

`LC_BarRoomServer.PartyQuerySummaryRequest` calls the micro-service `PartyService.getPartysSummary` with party type 4
(a msgpack array: type, string conditions, int conditions, sort conditions, start, size, direct query). The reply's
`SummaryList[].GameSummaryData` is `BarRoomSummary` bytes. Discover adds two int conditions, `BaroomCreateTime`
(created in the last `BarroomFindBarRoom[0]` = 1800 s) and `BaroomHeadCount` (2-30 members). The client filters by
topic, language and name and sorts the list itself. `getSubPartysCountByType` for type 4 answers rooms per topic.

### Seats and the owner's tools

| Id | What |
|---|---|
| 82490 | Speaking mode and its list: Free (anyone), Manual (the seat list), Order (the speech order). At most `BarroomSpeakNum` (10, 2559); a muted member cannot be listed (2558). The reply echoes the list as kept. |
| 82493 | The list alone (no client code sends it). |
| 82487 | A member asks for a seat with a reason. 2567 in Free mode or for the owner, 2560 already listed, 2558 muted, 2568 already asking. |
| 82492 | The owner agrees or declines (2569 no request, 2559 seats full). The applicant gets the same reply ("The host has Agreed / Declined"). |
| 82488 | A member leaves its seat (2561 the owner keeps the mic, 2562 no seat). |
| 82498 / 82499 | Mute a member for `BarroomForbiddentime` (24 h) / lift it (2558 already muted, 2570 not muted). The muted member gets the reply too ("The host has muted you."). |
| 82500 / 82501 | Remove a member onto the room's Blocks list / take it off (2566). The removed client gets 5043 with no room and an 82506 push. |
| 82504 / 82505 | Close / reopen every other mic (`isCloseMic`). |
| 82489 | Hand the room over (2557 blocked, 2558 muted, 2578 not in the room), once per `BarRoomTransferRoomCd` (30 s, 2575). |
| 82494 | Rename, change topic or language, once per `BarRoomSetInfoCd` (5 s, 2573). |
| 82514 | The owner's client asks to mute or unmute a member's own mic: the member gets 77003 `MutePlayerInChannel`. |
| 82484 / 82485 / 82486 | Invite: the server fills the inviter's name, job and level in 82485. Accepting enters the room; the invitee's reply carries its own id as `inviterGuid` (the client then opens the window), the inviter gets a copy with the invitee's id ("${1} has accepted / declined your invitation"). |

Every change answers its caller and pushes 82506 to the other online members. The client posts the gift, transfer
and close-mic lines into the room chat itself.

### Chat and voice

- The room's chat is public channel 10, IM group `10_<guid>` (`LC_ChatHelper`). `ChatService` lets only the room's
  members join it or write there, and drops a closed room's channel and kept lines.
- The voice relay serves the room as its guid (`VoiceRoomKey.IsTavern`). Only members may join (`TavernService.IsMember`);
  `TavernService.MaySpeak` decides whose mic the room hears: the owner always, nobody else while all mics are closed
  or while muted, anyone in Free mode, only listed members in Manual and Order mode. `Voice:MaxTavernRoomSize` (200)
  bounds the listeners of one room. The client's voice hook holds one room at a time, so entering a Tavern room's
  voice leaves the party or guild room.

### Gifts, heat and tags

- 82491 gives `giftNum` of a drink (`ItemConfig` type 2 / subtype 213) to a member. Use effect 110 `GainBarroomHot`
  times the count goes to the room's heat (the reply's `hotAdd`), 111 `GainStar` to the receiver's Star Power
  (`userBarRoomHot`). Refusals: 2574 yourself, 2555 the receiver left, 2563 offline, 2564 other server, 154 short.
- The drinks are spent under an `event/tavern-gift` receipt; the Star Power is credited in the receiver's own commit.

| Drink | Heat | Star Power |
|---|---|---|
| Hot Cocoa 1200801 | 1 | 1 |
| Gingerbread 1200802 | 10 | 5 |
| Craft Brew 1200803 | 60 | 35 |
| Grape Wine 1200804 | 400 | 300 |

### A role's counters (role blob `barroom`)

`TavernService.RoleState`, sent as 5043: Star Power in total and this week (`popularity`, `weekPopularity`), active
days (`actDays`), today's seconds in a room (`inBarRoomTime`, `isToDayActComplete`). Time counts every second a member
is online in a room and is written every minute, and at once when the day's 1200 s are reached.

### Quest and achievements

- Quest 2000 "Daily Tavern Quests" (`_iTaskType` 9, `BarroomTaskID`): `[106010, 1200]`, today's seconds in a room
  (`TaskService.TavernTasks`). Its award 8000 (MultiverseGroupConfig) is AwardConfig 1008000, three Hot Cocoa.
- 102100500-502 "A Nose for Wine": `[106007, 2000, n]`, deliveries of quest 2000 (1 / 7 / 30).
- 102100600-602 "A Drink for a Story": `[106008, n]`, Star Power received (1000 / 5000 / 10000).

### GM

The GM panel's Tavern ops (GMConfig 1024-1029): 134 print, 136 room heat / heat gain / extra head count, 137 a tag on a
room, 138 Star Power, 139 quest completions (106007), 140 active days, 141 today's seconds. `!tavern` lists the rooms.

## Inferred

- **Rooms are online state.** The list only ever shows live rooms. A room closes when its last member leaves, and
  nothing survives a restart.
- **Offline members.** A member whose session is gone keeps its place for 5 minutes (a relog brings it back through
  5043), then leaves. Error 2563 "recipient offline" shows the original kept offline members in rooms.
- **Next owner.** The first seated speaker who is online, else the longest-present online member.
- **Heat and tags.** Heat only grows while the room exists. Every `BarroomHeatTagsTime` (5 min) `historyHot` keeps the
  heat and its gain over the window, Spark (the heat icon) goes to the `BarroomHeatTagsNum` (2) rooms that gained at
  least `BarroomHeatTagsMiniIncrease` (50), and Boom (cheers) to the `BarroomHotTagsNum` (4) hottest rooms, both until
  the next window. RedBag and Activity come only from GM op 137 and last a day. Past the page size, the list orders
  rooms by `BarroomSortParam` [6000, 4000] on heat and head count.
- **Mute and Blocks values.** `forbiddenSpeaker` holds the mute's end, `blackList` the removal time, unix seconds; a
  removal lasts as long as the room.
- **Active day.** A day whose stay reaches the quest's 1200 s counts in `actDays`; the Tavern day starts at 05:00.
- **The quest** delivers itself when complete (no claim button, no `_kDeliverTask`) and is offered again every day.
- **Gift drinks.** The tables give only Hot Cocoa a source (the quest). Every active day also brings one higher drink:
  Grape Wine every 7th active day, Craft Brew every 3rd, Gingerbread otherwise (`Ro3Emu:TavernDayDrinks`, on by
  default; off leaves only the quest's Hot Cocoa).
- **Not checked:** a pending invitation (anybody may enter a room anyway) and room names against a word list.

## 本地既存資料相容

v1.0.25 採用上游完整酒館實作、`barroom` 角色狀態、房間管理／語音／送禮及任務流程。
原有 `tavern` 角色欄位與世界狀態仍保留，避免舊存檔、每日結算收據與經濟快照失去可讀性。
`LegacyTavernService` 僅負責既有秒數與跨日待領相容，未註冊玩家封包或定時器；玩家不會同時操作兩套酒館。
首次新版登入只將當日已提交秒數轉入 `barroom`，不恢復舊房間租約，不補算離線時間，也不補發新版額外飲料。
一旦 `barroom` 寫入，後續登入以新版資料為準；提交失敗時保留可重試狀態。

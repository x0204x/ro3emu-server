# Starshatter Dragon ritual: 82968-82974

The client calls it FlyingDragon (function 3220). One ritual runs per server: `DBFlyingDragonProduceInfo` is
`DBActivityParam` oneof 5, beside Siege and Territory Wars, and it has a single `OwnerRoleId`. `FlyingDragonService`
keeps it in `server_state` (key `flying_dragon`) and follows `PublicParamConfig` `DragonTask*` / `DragonCeremony*`.

## Flow

1. **Start (82968).** NPC 107 "Starshatter Dragon Ritual Administrator" in Prontera (spawn 570, dialogue 10701,
   "I Want to Summon the Dragon").
   - **Cost.** The owner pays the dragon's `WardRobeConfig 60060010._kMaterial`, Lord's Mane x1. For `extraRewardIndex`
     1-3, a red packet of `DragonTaskResortRank[i - 1] x DragonTaskHelpTimes` Zeny is added, as the investment panel
     shows.
   - **Refusals:**
     - a ritual is already running (1517);
     - away from the NPC (1518);
     - short of the cost (1520);
     - within `DragonTaskWaitTime` (24 h) of the role's last start (1526);
     - an owner of the dragon (1524).
   - **On success.** Marquee ChatConfig 8001 goes out with the owner's name, and every online player gets 82974: the main
     menu's top button reads that dataset.
2. **Progress.** Progress runs from 0 to 10000. It grows by `DragonTaskPerSec` (50) each second, exactly as
   `UI_Sys_Shop_UnblockingPanelCtrl` predicts it, plus `DragonTaskHelpProg` per assist. It ends at
   `ProcessEndTimeStamp`, which is the start plus `DragonTaskLimited`.
3. **Assist (82969).**
   - One per role, never the owner (1521/1519), at most `DragonTaskHelpTimes`.
   - It pays award `DragonTaskHelpReward`: AwardConfig 1001, one Minted Coin.
   - With a red packet it also pays its share in `DragonTaskReturnItem` (Minted Coin, text 200049).
4. **Self accelerate (82971).** The owner pays `(10000 - progress) x DragonTaskSpUPEffi` of `DragonTaskSpUPItem`. The
   client shows the button after `DragonTaskSpUPLimited` seconds.
5. **Complete (82972).** The owner, at 10000, uses the NPC's "Begin the ritual" option (action 343).
   - The Ceremony starts and marquee 8002 goes out ("in ${2} seconds", `DragonCeremonyLimited`).
   - The red packet's unclaimed shares go back by mail `DragonTaskPacketMail` 1116.
6. **Thumbs-up (82973).** Allowed during the ceremony within `DragonCeremonyAround` of the virtual NPC 108, once per
   role, never the owner. Refusals: 1522 after the window, 1523 a second time, 1518 too far away.
7. **End.** When the window closes, the owner receives item 1501004 "Starshatter Dragon" (use effect 201, permanent
   mount) by mail and the ritual returns to Idle.

A ritual that reaches `ProcessEndTimeStamp` before its ceremony returns the Lord's Mane by mail `DragonTaskMail` 1114
("did not complete … within the time limit") and the unpaid packet by mail 1116.

## Inferred

The client does not determine these:

- **Progress model.** Progress follows the client's own prediction, so a ritual fills in 200 s. The time limit only
  matters for an owner who never begins the ceremony.
- **The dragon.** It comes by mail as the item. No mail template names it, so the letter uses the item's name.
- **Currencies.** The red packet is paid in Zeny and shared out in Minted Coins, as the panel's two icons show.
- **Thumbs-up.** It pays nothing: `DragonLikeReward` and `DragonLikeTrigger` have no reader.
- **Start distance.** It is 15 m.
- **Lord's Mane** ("A mane dropped by the boss") has no source in the client data. An MVP kill drops one at
  `ServerOptions.LordsManePercent` (10%) to a participant picked as the rare winner is (`MvpService`).

## Verified

- **Real client.** The ritual was started (GM `!dragon start 1`, the NPC being hidden by the windmill roof for the
  camera).
  - The top button appeared and opened "Starshard Dragon Unseal" with the owner and a 60-minute timer.
  - Progress grew 8.5% → 42% → 100% in step with the server, and "Ritual Acceleration" showed after 180 s.
  - `!dragon complete` started the Ceremony: marquee 8002 "Panelka … 30" went out and the ceremony ended by itself
    30 s later.
  - The mailbox held "Starshatter Dragon" with the dragon and "Dragon Red Packet Return Mail"; the dragon was claimed.
- **Tests.** `FlyingDragonTests` cover the start refusals, one ritual per server, assists, the owner-only ceremony,
  thumbs-up rules, the mailed dragon, the cooldown and the timeout return.

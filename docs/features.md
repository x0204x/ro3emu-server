# What works

The server answers the client the way the original did: same messages, same order, same rules where the client shows
them. Rules the client cannot establish are reconstructed and marked *(inferred)* in the code and the
[system notes](systems). With `Ro3Emu:OpenContent` (on by default for hosted servers) everything the client can
show is playable from day one, without season or server-day gates ([details](guide/open-content.md)).

<table>
<tr><td valign="top" width="50%">

**World**
- login, server list, both gates, character create / select / delete
- maps, portals, NPCs, Kafra travel, wings, map channels
- monsters (they surround the player they attack, [monster AI](systems/monster-ai.md)), MVPs with their summons
  and fields, death and revive
- silent reconnect, emotes and sitting

**Character and combat**
- skills, buffs and debuffs, damage and healing over time, auras
- crowd-control diminishing returns, energy meters, talents, job resonance
- level cap 99 with catch-up EXP, character power score
- PvP and event fights

**Items and progression**
- equipment, appraisal, affixes, enchanting, strengthening, refining
- crafting with its guarantee counters, award and choice boxes
- cards (collection stars, insert rules, Flash cards, card effects)
- Soul Echo, pets and the hatchery, mounts, wardrobe, totems, job items
- achievements, codex, Growth Classroom, Seven-Day Goal, profile card
- titles, Paragon (Astral Level), the Starshatter ritual

</td><td valign="top" width="50%">

**Economy and social**
- trade center, stalls, auctions (guild, world, event), mail
- NPC, point and guild shops, battle pass
- friends and gifts, parties and raids, chat channels and horns
- party and guild voice chat through the server (off by default, see below)
- the Tavern: voice and text rooms with mic seats, drink gifts and a daily quest
  ([details](systems/tavern.md))
- adventure groups

**Guilds**
- management, journal, recruitment, founding, guild home
- bonfire party, caravan, black market, Ilmata challenge
- group mail, call to arms, impeachment, merging two guilds
- Guild League with elite rosters and capture rings
- Territory Wars and the castle siege: commander tools, Battle Merit, the war warehouse,
  cannons and vehicles, gates and stages, rams, ladders, archers, summon platforms
- the territory world's daily Faction Quest

**Instances and activities**
- story dungeons and scripted story scenes (reconstructed)
- Spirit Tower, Phantom Realm, Valhalla Trials, Realm of the Gods
  (blessings, Final Trial honors, hall of fame), Hidden Realm
- bosses cast their special skills, with phases and summons
- Monster Hunt, Rift Crisis, Survivor Battle, Wedding Crashers,
  Dragon Coffin Island, Mock Battle (40 vs 40), Hunting Challenge,
  timed events, daily activities, leaderboards
- Survivor Battle careers, landing together, spectating, shops, encounters,
  the emote wheel and weekly tasks

</td></tr>
</table>

**Not yet**
- **Boss script mechanics:** the special skills are in, but mechanics that lived only in the original server's scripts
  (bubbles, bells, rhythm stages) are plain area attacks.
- **Smaller missing pieces:** some stages of the story dungeons
  that are still simplified.

**Web panel.** Built into the server ([guide](guide/panel.md)): a player portal with rankings, who's online, news,
sign-up and a game wiki (items, monsters, maps, with the pictures exported from your own client), and admin tools
(accounts and bans, character sheets, mail with rewards, gift codes, GM console, broadcasts, economy log).

**Voice.** Party, guild and Tavern voice go through a relay in the server instead of the publisher's Tencent GME, and
the patched client records and plays it through Windows' own winmm. It is off by default: `Voice:Port` turns it
on. The voice stream is encrypted (AES-CTR with an HMAC-SHA256 tag per frame) with a fresh key per session that
travels inside the one-time ticket over the encrypted game gate, so the relay serves off loopback too.

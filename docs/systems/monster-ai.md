# Monster AI: surrounding a player

A field or dungeon monster's AI is its `NPCConfig._iAI` row in `AIConfig`. `MonsterService` reads the view range,
roam distance and time, chase distance and leash (`_iUnFightParam`) from it when it builds the monster's template. This
page covers the two fields that decide how the attackers of one player stand around it.

## Client data

- `AIConfig._iIsSurround` (0 or 1) and `AIConfig._iSurroundDistance` (cm), read through the `AIConfig` view.
- 23 rows have `_iIsSurround` 1. Their distances are 100 cm (9 rows), 200 cm (2), 220 cm (6) and 0 (6). No row with
  `_iIsSurround` 0 has a distance.
- 1210 of the 2289 monster rows (`_iNPCType` 1) use a row with surround on and a distance above 0. Among them are
  24000002 and 24000001 (220 cm; 467 and 343 monsters) and 20000001 (100 cm; 270 monsters).

The client does not run monster AI, so it does not show what the server did with these fields.

## Server (`MonsterService.Surround`)

Everything in this section is *(inferred)*.

- **Who.** A monster whose row has `_iIsSurround` 1 and `_iSurroundDistance` above 0, fighting a player, and able to
  walk. Every other monster keeps the straight approach. So does every monster whose target is a tower pet or a
  companion.
- **The ring.** A circle around the player at 0.8 of the monster's attack range. The straight chase walks to the same
  distance, so the ring is inside the attack reach. It has floor(2π × radius / `_iSurroundDistance`) slots, evenly
  spread, at least one. Slots are at least `_iSurroundDistance` apart along the circle.
- **Which slot.** A monster takes the free slot whose angle is nearest its own bearing from the player. On a tie it
  takes the lower slot. Monsters attacking the same player share one ring when they have the same radius and slot
  count. When every slot is taken, the monster shares the nearest slot, the same stacking as the straight approach. It
  keeps its slot until its target changes, it leaves the map, or it falls back to the straight approach.
- **Walking.** The slot moves with the player. Out of reach, the monster walks to its slot instead of straight at the
  player, in segments of at most 12 m, re-pathed every 400 ms.
- **Swinging comes first.** In reach, the monster stops and swings whenever its swing is due, and stands for the
  swing's action time (the skill's `_iActionTime`), exactly like a monster without surround. Between swings it walks to
  its slot, and resumes that walk after a swing interrupts it. Once there, it stays put while the player moves a
  little: while its slot is no farther than half the spacing and no farther than the gap between the ring and the
  attack range. Beyond that, it walks to its slot again between swings. A monster whose action time fills its whole
  swing interval never walks in reach, so it swings from where it entered reach.
- **Fallback.** Some slots cannot be used: the nav grid has no straight walkable line from the player to the slot, or
  the walk to the slot cannot start (nav grid, or a control that forbids walking). Then the monster chases and swings
  straight, as before.
- Its attack rate is the same as without surround, also against a player who moves within reach. Swing interval,
  damage, leash and return home are unchanged.

## Tests

`tests/Ro3Emu.Tests/MonsterSurroundTests.cs` runs on invented fixture rows:

- three attackers end up on distinct slots of the ring, at least the spacing apart and within reach. Each one hits,
  and they follow the player when it moves;
- slots are taken in order, the nearest free one first, and a full ring is shared;
- a slot blocked by the nav grid (its own cell, or a wall between it and the player) falls back to the straight
  approach;
- a row with `_iIsSurround` 0 keeps the straight approach;
- against a player strafing within reach, a surrounding monster swings as often as one without surround (within one
  swing over 8 s).

## Fewer monsters on a map (`Ro3Emu:SpawnLimits`)

A field map's spawn pool group creates as many of an NPC as the client's scene data says: Payon Forest (10220) has one group of
1120 monsters (`genNumberRandomRange` 1120, `refreshThreshold` 1120, the sum of its limits) with 400 Greatest Generals
(5000070); Deep Forest (10219) rolls 425 to 435. The server follows that. A server operator who finds a map too crowded can cap
one NPC on one map; the list is empty by default, so nothing changes unless it is set (**inferred**: the original had no such knob):

```json
{
  "Ro3Emu": {
    "SpawnLimits": [
      { "Scene": 10220, "Npc": 5000070, "Max": 300 },
      { "Scene": 10219, "Npc": 5000070, "Max": 400 }
    ]
  }
}
```

A rule only lowers the count a pool group rolls for that NPC at the first spawn of a map line (a group that rolls fewer keeps its
count); respawns replace each defeated monster one for one. It is read at start-up.
## RO1 HP reference (`Ro3Emu:Ro1MonsterHp`)

The client makes some RO1 cocoons and eggs die in one hit: the Ant Egg of the fields (5000085) has 92 HP and the Peco Peco Egg
(5000033) 45, against 420 in RO1 (ids 1097 and 1047), and the Pupa:

The client's Pupa (NPCConfig 5000035, level 4) has 4 HP: AttriMonsterConfig 110004 gives 40 and the NPC's own `_kAttriScale`
`[[101, 1000]]` takes it to 10%, so it dies in one hit. In Ragnarok Online 1 the Pupa is a level 2 cocoon with 427 HP (rAthena
pre-renewal `mob_db.yml`, id 1008). The client's beginner monsters already sit on the RO1 scale once `_kAttriScale` is applied
(Fabre 63 HP against RO1's 63, Lunatic 58 against 60, Drops 40 against 55, Poring 32 to 65 against 50), so the RO1 number is used
as it is. `Reconstructed/ro1-monster-hp.json` lists the monsters; with the switch on their templates take that HP. It is off
by default: the client cuts their HP on purpose (`_kAttriScale`), so a 1:1 server keeps it, and an operator turns it on for the
RO1 feel (`Ro3Emu__Ro1MonsterHp=true`). Every client monster whose name has Egg or Pupa was checked: the Thief Bug Egg (83 against 48), the Eggyra, the story dungeon's Ant Egg (5300036, 830) and the level 50 Peco Peco Egg are not below RO1 and are not listed. An RO1 reference, not client evidence
(**inferred**).

## Field EXP rates (`Ro3Emu:FieldExpRates`)

A kill pays what the client's drop tables say: the monster's `_iDrop` and `_iNormalDrop` chains give item 100 (Base EXP), scaled
by `LvExpCorrectionConfig` for the level gap between killer and monster (`RewardService.ResolveKill`). The client has no EXP value
per monster, and the original's own rate is not in its data. An operator who finds levelling too slow can scale it by the killer's
base level; the list is empty by default, so nothing changes unless it is set (**inferred**: the original had no such knob):

```json
{
  "Ro3Emu": {
    "FieldExpRates": [
      { "MinLevel": 20, "MaxLevel": 40, "Percent": 300 }
    ]
  }
}
```

The first rule whose range holds the killer's base level scales that kill's Base EXP (and the Job EXP, which equals it) after the
level-gap correction and the team bonus; 100 leaves it as it is. It applies only to open-field monsters (`NPCConfig._iWildMonsters`
1), not to monsters without that flag (the story dungeons' ones, for example), and to both ordinary and Bonus Time kills. A rule with an empty range or a percent outside 1 to
10000 is ignored, and a kill that pays EXP never pays less than 1. The `BaseExpAddConfig` catch-up is applied after it, as before.
`Ro3Emu__FieldExpRates__0__MinLevel=20` and its siblings set a rule through the environment.

A GM changes the rules in force without a restart, in any chat channel or through the loopback `/ro3emu/gm` that the control panel
uses: `!fieldexp` shows them, `!fieldexp set <minLevel> <maxLevel> <percent> [<minLevel> <maxLevel> <percent> ...]` replaces
them (one group of three numbers a rule; a group that is not usable refuses the whole line and answers with the usage),
`!fieldexp clear` leaves none (the client's rates) and `!fieldexp reset` brings back the configured ones. The change lives in
memory: the configuration is not written, so the next start uses `Ro3Emu:FieldExpRates` again. Like every GM command it follows
`GmAccounts` (docs/guide/gm-access.md); the loopback endpoint answers to anyone on the host when that list is empty.

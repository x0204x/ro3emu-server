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

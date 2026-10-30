# The client's GM panel

The client has its own GM panel (the GM button on the HUD, shown only to accounts that have GM, see
[gm-access.md](gm-access.md)). Its buttons come from the client's `GMConfig` and send one op (`GmOpEnum`) with the
parameters typed in; the preset buttons run a list of ops from `GMCombinationConfig`. The reply comes back as the
panel's message.

The client gives each op its parameter names and a short description, but not what the original server did with it.
Where the emulator had to choose, the code marks the choice `(inferred)`. A target id of 0 or empty means the GM's own
character. Ops the emulator does not run answer `GM op <n> <name> is not implemented by the emulator.`, and a preset
ends with one line that lists the steps it skipped.

GM changes made through the panel are meant for testing. Combat switches and attribute changes last until relog.

## Character and gear

| Op | What it does |
|---|---|
| 4 GmOpStrengthen, 5 GmOpRefine | Sets the strengthen or refine level of a slot: 1 weapon (main and off hand), 2 armor, 3 cloak, 4 shoes, 5 and 6 accessories. Default level 1. No materials, no chance. |
| 8 GmOpAddAttrPnt, 9 GmOpResetAttrPnt | Adds stat points; resets the active stat plan. |
| 24 GmSetLevel, 25 GmAddExp, 101 GmSetJob | Base and job level, experience, job. |
| 110 GmCurAllSkillLevUpMax | `1` learns every skill of the job at its max level on the active plan, `0` clears them. No skill points are charged. |
| 1, 2, 66 | Add item, remove item, clear the bag. |
| 26 GmDropPool | Rolls a drop pool into the bag. |
| 78 GmFullHpMp | Full HP and SP. |

## Quests, dungeons, guild, mail

| Op | What it does |
|---|---|
| 99 GmTask | `<state>,<taskId>`: 0 accept, 1 abandon, 2 reset, 3 submit, 4 complete, 5 delete. |
| 108 GmEnterDungeon, 93 GmDungeonCmd | Enter a dungeon by id; leave the current one. |
| 94 GmOpGuild | Creates a guild with that name for the GM. |
| 70 GmSendMailPerson | Mail to the GM, with an item, a drop pool or an award attached. |
| 71, 72 | System mail to every role online now (the emulator keeps no server-wide mail record). |
| 51 GmGVGReset | `open`, `close`, `state` of the War of Emperium siege (same as `!siege`). |

## Position

| Op | What it does |
|---|---|
| 31 GmJumpMap | Warp to a map, optionally to x, y. |
| 79 GmShowLocation | Scene, map, line, position and facing. |
| 83 GmFlyToTarget | Jump to a player, monster or NPC. |

## Combat and entities

| Op | What it does |
|---|---|
| 23 GmChangeHp | Heal or damage a target's current HP; 0 HP kills. |
| 27 GmAddNpc | Spawns that monster in front of the GM. |
| 30 GmKillEntity | Kills the target. A monster killed this way gives no rewards. |
| 32 GmDumpEntityAttr, 77 GmShowAttr | Lists a target's attributes, or one of them. |
| 33 GmSetEntityAttr, 34 GmChangeEntityAttr | Sets or changes one attribute of a target. |
| 35 GmAddBuff, 36 GmRemoveBuff, 76 GmShowBuff | Adds, removes and lists buffs. |
| 52 GmNoCD | No skill cooldowns for the GM. |
| 53 GmDamageFix | No damage variance on hits the GM deals or takes. |
| 80 GmUnlimit | `<on>,<kind>`: kind 2 (the default) invincible with no skill costs, kind 1 no cooldowns. |
| 81 GmCastSkill, 82 GmTargetCastSkill | Casts a skill, learned or not, through the normal cast path. |
| 95 GmSetEntityCamp | Changes a monster's camp. |

## Class presets

The preset buttons (Gear, class builds) build a test character for a class. They use ops that only presets send:

| Op | What it does |
|---|---|
| 999 GmPlayerAsset `openallfunc` | Every function counts as open for this session. |
| 170 GMJobProfessSkill, 171 GMJobProfessAttr | The build's recommended skills and stats for the role's level. |
| 172 GMSetSkillWheel, 38 GMSkillQueue | The skill bar and the skill queue. |
| 173 GMEquipWeapon | Gives and equips the weapon. |
| 175 GMEquipMount, 176 GMEquipPendant | Leases the mount (and rides it with a trailing `1`) or the pendant. |
| 174 GMCreateLvEmblem | Creates a Soul Echo at that level and deploys it. |
| 261 GMSeasonTalent `upgradetree` | Every node of that talent tree at its max level. |
| 251 GMWarRobe `lightupallappearance` | Unlocks every appearance the role may have. |

## Not run

These answer with the reason:

- 62 GmSetServerTime, 63 GmSetServerOpenDay: the server runs on the host clock and the season start.
- 54 GmCreateMuiltRole: roles are created only through an account's own login.
- 73 GmRevokeSystemMail: there is no server-wide mail record.
- 186 GmSetMoveVerify: the movement check is one host setting (`WalkCheck`), not a per-role switch.
- 301 GmSetRoleAttr: the client's table for it has no columns.
- 58 GmReloadSkillBlueprint, 92 GmOpenLog, 20000 GmBattle: skills load at start, combat logs go to the server log, and there is no damage-range snapshot.
- 134, 136 to 141: the tavern ("Barroom") system does not exist in the emulator.

# Web panel

The server has a website built in: a player portal and an admin area, in the launcher's look. It runs inside the
server process on a port of its own, and every change it makes goes through the running server: a ban disconnects
the player at once, and the panel never writes the database behind the server's back.

| Who | Pages |
|---|---|
| everyone | Home (status, version, players online, uptime, accounts and characters; latest news, who's online, top 5 by level) · Who's online · Rankings · News · Items · Monsters · Maps · Guilds and the territory map · Market · every character's public profile |
| a player, logged in with the game account | My account (created, last played, characters with class, level, map and guild) · the sheet of each own character · password change |
| a Moderator | Dashboard (the server's own metrics) · Accounts (search, detail, login history, deleted characters, ban and unban) · every character's sheet and mailbox · Reports · Audit log |
| a GM | the Moderator's pages and Mail & rewards · Gift codes · GM console · News editor · Broadcasts · Economy log · Character actions (kick, teleport, rename, items and currencies, restore) · Item tracking |
| an Admin | the GM's pages and Staff roles · Server control (restart, shutdown, maintenance, backups, updates) · 2FA resets |

Players log in with the same account name and password as in the game. An account the game created logs in here,
and an account made on the panel's sign-up page logs in to the game: both paths write the same record
(`AccountStore.RegisterAsync`, the code of the game's first login).

## Turning it on

It is on by default, on port **5381**, next to the server's HTTP port 5380:

| Option | Default | What |
|---|---|---|
| `Ro3Emu:Panel:Enabled` | `true` | `false` keeps the port closed |
| `Ro3Emu:Panel:Port` | `5381` | the panel's port; it must differ from the server's other ports |
| `Ro3Emu:Panel:BindAddress` | the server's | the interface it listens on; empty uses the one the other ports use (loopback for local play, every interface on a public host) |
| `Ro3Emu:Panel:TrustedProxies` | none | proxy addresses whose `X-Forwarded-For` and `X-Forwarded-Proto` are believed (see below) |
| `Ro3Emu:Panel:SessionHours` | `12` | hours a login stays valid without use |
| `Ro3Emu:Panel:Registration` | `true` | `false` hides the sign-up page; accounts are then created only by a first login in the game |
| `Ro3Emu:Panel:SignUpsPerAddressPerDay` | `0` (no limit) | accounts the sign-up creates for one client address (an IPv6 one by its /64) in any 24 hours; behind a reverse proxy list it in `TrustedProxies` |
| `Ro3Emu:Panel:SignUpsPerHour` | `0` (no limit) | accounts the sign-up creates in any hour, from every address together |
| `Ro3Emu:Panel:ImageDir` | `icons/` of the game data | the pictures the pages show (see [Pictures](#pictures)) |
| `Ro3Emu:Panel:Branding:*` | RO3Emu's | your server's name, tagline, logo, footer and colour (see [Branding](#branding)) |
| `Ro3Emu:Panel:Roles:<account>` | none | a staff role, `Moderator`, `GM` or `Admin` (see [Staff roles](#staff-roles)) |
| `Ro3Emu:Panel:RequireAdmin2FA` | `true` when other machines reach the server (a public bind or `PublicHost`, or `TrustedProxies` or `PublicUrl` set), `false` for local play | GM and Admin accounts must turn on two-factor authentication before the admin area opens to them |
| `Ro3Emu:Panel:LoginAddresses` | `false` | keep and show the address of each login in the login history |

Set them in `appsettings.json` under `Ro3Emu`, as environment variables (`Ro3Emu__Panel__Port=8081`) or as
arguments (`--Ro3Emu:Panel:Port=8081`). The startup log says `Web panel on http://...`.

`RO3Emu.exe start`, `host` and `install` take `--panel-port <n>` and `--no-panel` and pass them on; so do
`server.sh` on Linux and `scripts/server.sh`. RO3Emu.exe checks the panel's port with the others before it starts the
server, so a second server on the same PC needs its own (`--panel-port 5391`) or `--no-panel`.

When you play on your own PC the panel listens on loopback only: open `http://127.0.0.1:5381`, or press **Web panel**
on the launcher's title bar, or run `RO3Emu.exe panel` ([launcher](launcher.md)). On a public host it
listens on every interface like the game ports; open TCP 5381 in the firewall, or better, put it behind a proxy with
https as below and keep 5381 closed to the outside.

## Staff roles

The admin area is for staff. Each role has everything of the one before it:

| Role | May |
|---|---|
| Moderator | open the dashboard, accounts (with their login history and deleted characters), every character's sheet and mailbox, the reports and the audit log; ban and unban; triage reports |
| GM | also send mail, make gift codes, run the GM console, edit news, broadcast, read the economy log, and act on characters |
| Admin | also give and take staff roles, control the server (restart, shutdown, maintenance, backups, updates) and reset another account's 2FA |

An account gets its role from, in this order:

1. GM in game ([GM access](gm-access.md)): every account listed in `Ro3Emu:GmAccounts` is an Admin. While that list
   is empty, every account is an Admin only on a local panel with GM open (local play: `PublicHost`, the server and
   the panel on loopback, no `PublicUrl`, no `TrustedProxies`); a panel that other machines reach has no Admin from the GM rule.
2. `Ro3Emu:Panel:Roles`, account to role, spelled as stored: `Ro3Emu__Panel__Roles__anna=Moderator`, or in
   `appsettings.json` `"Panel": { "Roles": { "anna": "Moderator", "tomek": "GM" } }`.
3. The **Staff roles** page, where an Admin gives an existing account a role or takes it away. These are stored by
   the server (`panel_role`) and audited; the roles of 1 and 2 are shown there but changed only in the configuration.
   Nobody changes their own role.

A request that a proxy forwarded (`X-Forwarded-For`, `Forwarded` or `X-Real-IP`) never gets the Admin of an open local
server. List your admins before you put a proxy or a tunnel in front of the panel: one that adds no such header
cannot be told from your own browser ([GM access](gm-access.md)).

**The panel follows the game's GM rule**: on your own PC with an empty `GmAccounts` every account is GM in game and an
Admin in the panel. On a server that other machines reach (a LAN or public address, or a panel behind a proxy) or one
started as Production, an empty list gives nobody either, and the startup log warns `!!! Web panel: no Admin in the
configuration`. Even `GmOpenWhenEmpty=true` there opens only the game, not the admin area. A public server lists its
admins in `GmAccounts`; the log says `Web panel Admins: ...` at start. The roles of 2 and 3 are the panel's only: GM
commands in the game still follow `GmAccounts`.

Every route of the API names the least role it needs and refuses the others (403); the sidebar shows each role only
the pages it may use.

Every admin action is written to the audit log (`admin_audit`: who, what, when, on which account, the reason). A change
the panel's own plans make (a ban, a role, news, gift codes, report triage, a rename or restore, maintenance, a 2FA
reset) is recorded in the same database transaction as the change itself, so a recorded action always happened and a
performed one is always recorded. An action that runs through the game's own code (mail, a GM command, a broadcast, a
kick, a teleport, an item grant, a planned restart, a backup) is recorded right after it ran.

## Bans

A ban from the Accounts page (reason, and 1 day to 30 days or no end):

- writes the `account_ban` row ([account bans](../systems/account-bans.md)) and its audit row in one transaction;
- closes the account's game session at once: a connected client is kicked (`BE_KICKED`) and its character logged
  out, a character waiting for a reconnect is logged out, and no reconnect brings it back;
- ends the account's panel sessions;
- refuses every later login of the account, in the game and on the panel, until it ends or is lifted.

Lifting a ban deletes the row and is audited the same way.

Staff act only below their own role: a Moderator cannot ban or unban a GM or an Admin, and two Admins cannot ban each
other (take the role away first). A ban also stands against anyone below the one who placed it, so a Moderator cannot
lift an Admin's ban. On your own PC with an empty `GmAccounts` the role by the lists counts, so every account can ban the
others there. Signing an account out and resetting its 2FA stay open between Admins: they need Admin already, they are
audited, and the reset is how an Admin who lost a phone gets back in.

## Admin tools

Each tool is a page of the admin area's sidebar; each change is written to the audit log, with the admin's name.

- **Mail & rewards.** In-game mail from the server (sender name, title, text) with up to 10 items and Zeny (item 1)
  attached, to one character, all characters of an account, or everyone (optionally only those in the game, or from a
  base level up). It goes through `MailService.SendRawAsync`, as `!mail` does: stored first, then announced, so a
  player in the game sees it at once and the others when they log in. Item ids are checked against the item table.
  A mail to more than one character is sent only after the admin confirms the recipient count the page showed.
- **Gift codes.** The list of codes with their rewards, uses (counted from `gift_code_redemption`), limits, expiry and
  state, and the switch to disable or enable one. New codes are typed (up to 500, letters, digits, `-` and `_`) or
  generated (up to 500 random codes with an optional prefix, without the look-alike characters 0, O, 1, I, L), with
  rewards, max uses, uses per account and optional start and expiry. All the codes of one request are created or none.
  The server writes `gift_code` through its writer ([gift codes](../systems/gift-codes.md)).
- **GM console.** Any `!` chat command ([GM access](gm-access.md)) run as the admin's own character, or for any
  character in the game, with the replies the command sends shown on the page; arrow up and down recall earlier
  lines. It runs on the game loop like `POST /ro3emu/gm`. The admin is the authority: the chosen character's account
  needs no GM rights. The page lists the admin's past commands (from the audit log).
- **News editor.** Create, edit, delete and pin news (category news, event, maintenance or patch, and a publish time;
  a time in the future keeps an item hidden until then). The public News and Home pages show pinned items first.
- **Broadcasts.** A one-line notice on the screen of everyone in the game: the GM marquee (`BROADCAST_NOTIFY` 83270,
  the message GM replies use). It can repeat every few minutes, up to 100 more times; repeats are kept in the
  server's memory (a restart ends them) and can be cancelled.

## Account security

*My account → Security* is every player's own:

- **Two-factor authentication** with any authenticator app (TOTP, RFC 6238: HMAC-SHA1, 30-second steps, 6 digits).
  The page shows the key and an `otpauth://` link; the first code from the app turns it on and shows ten recovery
  codes once (`ABCDE-FGHJK`, each good once, stored as SHA-256 hashes). From then on a login asks for a code after the
  password: the current one, the one before or after it (a phone's clock may be 30 seconds off), or a recovery code.
  A code is good once: the server keeps the last time step used and refuses it and every earlier one. Turning 2FA off
  or making new recovery codes asks for a code too. Turning it on signs out the account's other browsers.
- **Signed-in browsers**: the panel sessions of the account, with "Sign out" for one and "Log out everywhere else".
  A password change already signs out the others, a ban all of them.
- **Latest logins**: the account's last 30 logins (the server keeps 100), each with its time and whether it was the
  panel or the game (the SDK login). The address only with `Ro3Emu:Panel:LoginAddresses`; without it no address is
  stored at all.

**Required for GM and Admin.** With `Ro3Emu:Panel:RequireAdmin2FA`, a GM or Admin without 2FA has no staff role in the
panel until it is on: the admin area answers 403 and *My account* asks to set it up. Unset, it is on whenever other
machines reach the server: it listens on an address that is not loopback, `PublicHost` is not loopback (a tunnel), or a
proxy serves the panel (`TrustedProxies` or `PublicUrl` is set, because a proxy on the same machine reaches a loopback
panel). It is off only for local play. Set `false` to turn it off anyway, `true` to require it locally. The startup log says which.
Moderators are not required to use it.

**Limits.** A login that needs a code waits 5 minutes for it and takes 5 tries; an account takes 10 wrong codes in 15
minutes from all its logins together, then refuses codes until the window passes. Every code also counts against the
address's login budget (`Ro3Emu:LoginAttemptsPerMinute`). Codes and recovery codes are compared in constant time and
never logged; a wrong code logs only the account. An Admin can reset another account's 2FA (a lost phone without
recovery codes) from its account page, which also signs it out; the reset is audited.

A Moderator sees an account's login history, 2FA state and panel sessions on its page; an Admin can also sign it out of
the panel there.

## Report inbox

**Reports** lists the players' in-game reports ([bug reports](feedback.md): `/bug` in chat and the Feedback button),
newest first, with tabs for their status: *new*, *in progress*, *done* and *won't fix*. A report shows what the player
wrote, the character, account, level, map, position and line, how it was sent, and the server's log lines about the
player from the minutes before. Staff change its status and assignee (any account) and add notes; each change is
audited. "Take it" assigns it to yourself. The reports stay in `reports/feedback.jsonl` as the game wrote them; the
triage is the database's (`feedback_triage`, `feedback_note`). A Discord webhook, when set, still gets each report.

## Character actions

From an account's page a GM opens **Character actions** for each of its characters:

- **Kick** (in the game): closes the game session at once, as a ban's kick does, without a ban; the player can log in
  again.
- **Teleport**: in the game, the server's own `!warp` moves the character now (to the given x and z, or the map's
  arrival point). Out of the game, the stored position changes and the character logs in there; the login puts it on
  the ground or, where the point is not walkable, at the map's arrival point. A map the game client has no scene for
  (Glast Heim and five eastern towns, listed in the export's `client-scenes.json`) is refused: the client would never
  finish loading it.
- **Rename**: the game's name rule (2 to 16 characters once trimmed, unique in any letter case). Only while the
  character is out of the game; the old name is free the moment it commits.
- **Items and currencies**: while the character is in the game, through the GM grant (`!item`, `!delitem`): the bag
  changes on the player's screen at once and the economy log records it. Zeny is item 1, other currencies are items
  too. For a character out of the game use *Mail & rewards*: the mail waits for the next login.
- **Restore**: the server keeps a deleted character (its row, renamed `name~id`); an account's page lists them, and a
  GM brings one back under its old name, unless another character took that name since. It gets a new lifecycle, so
  nothing of the deleted one's server state applies to it.

An action on a character out of the game takes the character's lifecycle reservation and is refused while it is in the
game, loading or waiting for a reconnect, as the HTTP character deletion is. Every action is audited.

## Server control

**Server control** (Admins):

- **Restart or shut down** now or in up to a day. The players in the game are told on the GM marquee at once and again
  15, 10, 5, 3, 2 and 1 minutes and 30 and 10 seconds before; then the server stops the way Ctrl+C stops it (the loop
  drains, the database closes). A plan can be cancelled (the players are told) and lives in memory: a restart forgets
  it. The process exits with code **75** after a restart and **0** after a shutdown, so a process manager decides what
  comes back: the systemd unit `server.sh` installs has `Restart=always` with `RestartPreventExitStatus=0` (a restart
  comes back, a shutdown stays down). Without a process manager (`RO3Emu.exe start`, a terminal) a restart is a stop:
  start the server again.
- **Maintenance**: while it is on, a game login of an account without a staff role is refused at the game gate with
  `ConnectServerResp` *ClusterShutdown*, which the client shows as its own maintenance notice
  (`LC_LoginManager`: `ClusterShutdown` maps to UIWords 28108 *ServerMaintenancing*; that the gate's answer reaches
  that state is inferred). Players already in the game stay; plan a restart to send them out. It is kept in the
  database (`panel_setting`), so it survives a restart.
- **Backups**: the files of `data/backups`, newest first. *Make a backup now* runs the server's own backup (a checked
  copy, as at every start); *Download* sends the file; *Restore* plans a restart in 5 minutes whose next start puts
  that backup back **before the realm opens**, exactly as `Ro3Emu.Server --restore-backup <file>` does: the running
  database is never swapped. The request is `data/restore-request.txt`, removed as the restore starts, so a refused
  restore is not tried again at every start; what it did is in `data/restore-result.txt` and on this page. The replaced
  database is kept in `data/restore-saved/`.

## Updates

**Server control > Updates** (Admins) keeps the server on the newest release. Nothing installs by itself: the page
checks, downloads and checks again, and an Admin plans the install like a restart.

- **Version.** The running version and commit against the releases of `aethai/ro3emu-server` on GitHub: the latest
  one, every newer one with its date, its package size and its release notes (shown as plain text; the newest five
  open, the rest behind *Show older releases*). The server asks GitHub's API without a token (60 requests an hour per
  address) when *Check now* is pressed, at most once a minute, and every 6 hours in the background while the panel is
  on, the first time 2 minutes after a start. It sends the last answer's ETag, so an unchanged list does not count
  against the limit; a spent limit is waited out, and a failed check keeps the last good list and says why. The answer
  is kept in `data/updates/releases.json`.
- **Download** takes this server's package (`RO3Emu-server-linux-x64.tar.gz` on Linux x64, `RO3Emu-win-x64.zip` on
  Windows x64; other platforms have none) into `data/updates/downloads`, and refuses it unless its size and SHA-256 are
  the ones GitHub lists for it (`digest`); a release without a digest is not downloaded. The package is unpacked into
  `data/updates/staging/<tag>`: only plain files under its one folder (`RO3Emu-server/`, `RO3Emu/`), no link, no path
  out of it, bounded sizes. Then the server, the RO3Emu tool and `appsettings.json` must be there and the server must
  say it is the tag's version (read from its single-file bundle, without running it). The page follows the download and
  can cancel it; a failure says why.
- **Install** (only where the server can come back by itself, below) plans a restart with the countdown notices of
  *Restart or shut down* (*The server restarts for an update in ...*). When it ends, the server makes a database backup
  with its own backup (and keeps a copy in `data/updates/backups`, the newest three, out of reach of the rotation),
  copies the files it is about to replace to `data/updates/previous`, then swaps the release's files in one by one: the
  current file is renamed to `*.old` in its folder (Linux and Windows both let a running executable be renamed) and the
  new one moved in; a failure puts every file back and the restart is cancelled (the players are told). An
  `appsettings*.json` you edited stays; the release's own lands beside it as `.new`. The process exits with code 75
  and the process manager starts the new version, whose first start writes the outcome to the history and the audit
  log and deletes the `*.old` files. Another plan waiting refuses an install; a planned install is cancelled like any
  restart.
- **Roll back** (same modes) plans a restart that puts the previous version's files back the same way (the version it
  replaces becomes the previous one). It can also restore the database backup made just before the update: the next
  start restores it before the realm opens, as *Restore* does, and keeps the replaced database in
  `data/restore-saved/`. **Whether an older server can open the newer one's database:** the database has no schema
  version. Every start runs the same idempotent steps (`DatabaseSchema`): create the tables that are missing, add the
  columns that are missing, rebuild a table only to relax a constraint. Nothing removes a table or a column, so an
  older server usually opens a database a newer one used and ignores what it does not know. Nothing checks that,
  though, and rows the newer version wrote (new states, new kinds of records) may not mean the same to the older one.
  Restoring the pre-update backup is the sure way back and loses every change since the update.
- **Install history**: every install, rollback, failure and the start that followed, in `data/updates/history.jsonl`
  (a database restore does not rewind it). Every action (download, cancel, delete, install, rollback and their
  outcome) is also in the audit log as `update` on the server.

What the page may do depends on how the server runs. It finds out at start, or `Ro3Emu:Updates:Hosting` says it:

| Hosting | Found by | Install and rollback |
|---|---|---|
| systemd (`server.sh --service`) | `INVOCATION_ID`, set by systemd | yes: the unit's `Restart=always` starts the new files (`RestartPreventExitStatus=0` keeps a shutdown down) |
| Windows service (NSSM, WinSW, ...) | no interactive session | yes, when the wrapper starts the server again after it exits with code 75; the running exe is renamed aside, not overwritten |
| `server.sh` in a terminal, the exe in a console | none of the others | no: nothing would start it again. The page downloads and checks, then lists the steps (stop, `cp -a '<staging>/.' '<install>/'`, start) |
| RO3Emu.exe or the launcher | `RO3EMU_STARTER`, set by them | no: they stop with the server. Quit RO3Emu and start Ro3EmuLauncher.exe, which updates itself from the same release, or copy the staged files over the folder |
| Docker | `/.dockerenv`, `/run/.containerenv`, `DOTNET_RUNNING_IN_CONTAINER` | no: build or pull an image of the new release and recreate the container with the same data volume. The page still downloads a checked copy of the package |
| a build of the source tree | not in a release folder | no download: `git pull` and build |

The release folder is the one above the server's own `server/` folder, with the RO3Emu tool beside it (the layout of
both packages). The server must be able to write there; an install checks every folder first and changes nothing when
it cannot.

If a new version does not start, put the previous one back with the server stopped:

```
Ro3Emu.Server --rollback-update [your config arguments]
```

It moves the files of `data/updates/previous` back (they are plain copies, so copying them by hand works too) and
prints the `--restore-backup` command for the pre-update database backup, which it never restores by itself.

| Option | Default | What |
|---|---|---|
| `Ro3Emu:Updates:Check` | `true` | ask GitHub for releases; `false`: no request leaves the server and the page shows the installed version only |
| `Ro3Emu:Updates:Repository` | `aethai/ro3emu-server` | the repository whose releases are offered |
| `Ro3Emu:Updates:CheckHours` | `6` | hours between background checks (at least 1) |
| `Ro3Emu:Updates:Hosting` | `auto` | `systemd`, `service`, `docker`, `app` or `manual` instead of what the server finds |
| `Ro3Emu:Updates:InstallDir` | found | the release folder, when the server runs from somewhere else |
| `Ro3Emu:Updates:ApiUrl` | `https://api.github.com` | another releases feed with the same API, for testing; plain http only on loopback, and its downloads must be https or on the feed's own address |

## Branding

The panel can carry your server's name and look instead of RO3Emu's. Every field is optional:

| Option | What |
|---|---|
| `Ro3Emu:Panel:Branding:ServerName` | the name in the header, the browser's title and the home page (at most 64 characters); empty: `Ro3Emu:ClusterName` |
| `Ro3Emu:Panel:Branding:Tagline` | a line under the name on the home page |
| `Ro3Emu:Panel:Branding:LogoPath` | a PNG, JPG or WebP file in the data folder, as a path relative to it (`brand/logo.png`); shown instead of the star, about 28 pixels high |
| `Ro3Emu:Panel:Branding:FooterText` | the footer's text |
| `Ro3Emu:Panel:Branding:AccentColor` | the colour of links, focus rings and highlights, as `#rrggbb` |

```json
{ "Ro3Emu": { "Panel": { "Branding": {
  "ServerName": "Midgard Classic", "Tagline": "A friendly realm, rates 5x",
  "LogoPath": "brand/logo.png", "FooterText": "Midgard Classic, a fan server.", "AccentColor": "#c2410c" } } } }
```

The values reach the pages as text. A colour that is not `#rrggbb` is ignored, and so is a logo path that leaves the
data folder. The logo is served at `/brand/logo` and cached by the browser until the file changes.

## Characters and mailboxes

A character's **sheet** shows what the server keeps for it, read only: class, base, job and Astral level with the
EXP into the level, sex, map and position, created and last login, the six base stats and the points left to spend,
every currency, the equipment by slot with the slot's strengthen and refine levels and the item's enchantments, the
bags tab by tab (the cart included), and the learned skills. A character in the game is read from its session on
the game loop, so the sheet shows what the player has now; any other from the database. A player opens the sheets of
the characters of their own account from *My account*; an admin opens every sheet from an account's page (the
characters link to it). Another account's character is "not found" to a player.

An admin also sees a character's **mailbox** (`/characters/<id>/mail`), read only and newest first, 10 letters a page:
title and text (a template letter's from MailConfig), sender, sent and expiry times, read and claimed, and the
attachments with their icons. It is the role's mailbox as `MailService` stores it.

## Items, monsters, maps and the economy log

**Items**, **Monsters** and **Maps** are public databases built from the server's own game data and English texts the
first time someone opens them, then kept in memory. An item shows its quality, type, level, trade rule, stats (equipment
and cards), the monsters that drop it and the shops that sell it. A monster shows the HP and stats the server gives it
(AttriMonsterConfig scaled by the NPC's `_kAttriScale`, as `MonsterService` does), its drops with the chance per kill
that at least one drops (from the pools `RewardService` rolls, outside bonus time and without a player's level and
class limits), and the maps whose scene editor spawns it, with how many stay alive and the respawn. A map shows its
picture, the monsters that spawn there with their levels, and the characters on it now.

The client's tables hold many rows of one thing: an item copied under another id with only its sort order changed, a
monster copied for another map, quest or scene. Rows a player cannot tell apart are one *variant*: items with the
same name, type, quality, level, description, stats, trade rule, stack and classes; monsters with the same name,
level, HP, stats, speed, element, race, size and rank. The lists then show **one row per name**: the row stands for
the variants of that name the filters match, with the picture and numbers of the one the game repeats most (an item)
or spawns most in the field (a monster), the level (and HP) range of all of them, and a "4 variants" chip. A page
lists the variants of its name in a table (id, level, HP, size and where each spawns for a monster; id, quality, type
and level for an item), shows the ids of its own variant in a small line, and merges what the variants drop, sell and
spawn; a merged drop says "Only from ..." when not every variant drops it. Rows still named `TBD`, rows with a `@{n}`
placeholder the row gives no value for, and level-1 monsters with 100 000 HP or more that spawn on no map (story and
scene actors) are left out of the lists; searching for their id still finds them.

## The rest of the game database

The **Database** page links every public database. Besides items, monsters and maps:

- **Cards**: every card of the card library with each star's effects as the client's tips write them (attributes by
  _iNameEquip and _iTextValueDisplayType, special effects by LC_SpecialEffectHelper's rules), the copies a star costs,
  what each level adds and costs (CardUpgradeConfig of the card's slot and quality), the revert price and who drops it.
- **Crafting**: the Chef's formulas (LifeFormulaConfig: materials, vigor, time, life job EXP, the products and their
  chances through the award's drop pool) and equipment crafting (EquipCraftConfig, each quality by
  EquipCraftQualityConfig's weights, the guarantees). An item page shows the recipes it comes from and goes into.
- **Classes & skills**: each class's line and skill trees laid out as the skill window does (SkillTreeConfig rows, 7
  columns, the class's _kSkillReplace, Novice's row centred), and a page per skill with every level's description
  (Language.Trans of _iDescription with _kDescData), cooldown, cast time, cost and prerequisites.
- **Pets**: stats per level (AttriMonsterConfig times _iBaseAttr), fight and assist power, every star's step and cost,
  what the pet gives while it fights and once collected, and its skills per star. **Mounts**: the speed each adds
  (attribute 1131), its other stats and the items that lend it. **Shops**: every shop's goods and prices.
- **Elements & EXP**: the element table (EleConfig) and how a kill's EXP is scaled (LvExpCorrectionConfig, the party
  bonus). A monster page shows its Base EXP per kill (item 100 of its drop pools, also in Bonus Time and the low-level
  bonus) and the damage it takes from each element; a map page its type, recommended levels
  (OnHookMonsterRecommendConfig), NPCs with what they offer, portals, neighbours and dungeon floors.
- **Event calendar**: the timed To-Do activities from now to a week ahead, as TimedTodoSchedule opens them, and the
  season's long windows (NewActivityConfig), shown in the viewer's own time with live countdowns; it says when the
  testing switch runs everything all day. Asked on the game loop at most once a minute.
- **MVP timers**: every field MVP's state, last kill and next respawn from MvpService, at most every 5 seconds.

The data export also writes the pictures these pages use: icons/pets (PetBaseConfig heads and illustrations),
icons/npcs (world NPC heads), icons/mounts (the mount appearances) and icons/events (the To-Do icons).

## Pictures

The item, monster, class and map pictures come from **your own game client**, the same way the game data does:
RO3Emu ships none. The data export (`RO3Emu.exe` on first start, `RO3Emu.exe export <game> <data>`, `server.sh`)
decodes them from the client's Unity bundles into `icons/` of the data folder:

| Folder | From | What |
|---|---|---|
| `icons/items/` | ItemConfig `_kIcon`, `_kPic` | bag icons (128 px) and card pictures (halved to at most 400 px) |
| `icons/monsters/` | NPCConfig `_kHeadIcon` of the monster rows | portraits (128 px) |
| `icons/classes/` | JobConfig `_kIcon`, `_kBigIcon` | class emblems |
| `icons/maps/` | MapConfig `_kBgImage[0]` | the map window's outline (halved to 512 px) |
| `icons/skills/` | SkillConfig `_kIcon` | skill icons for the character sheet |
| `icons/guilds/` | GuildIconConfig `_kBigIcon` | guild badge frames and designs |
| `icons/titles/` | TitleConfig `_kTitleIcon02` | title rank emblems for the profiles |
| `icons/achievements/` | AchievementType, AchievementLevel `_kIcon` | achievement level and category icons |

About 2 500 PNG files, 120 MB, in about 20 seconds; a later export decodes only what a client update changed
(`icons/index.json` records the client file each picture came from) and removes what the client dropped.
`--no-icons` skips them. A server host that downloads only what it needs (`RO3Emu.exe update --server-files`) also
gets the bundles the pictures come from, about 550 MB.

The panel serves them under `/img/<folder>/<file>`, only the export's own folders and file names. The pages ask for
`/img/...?v=<hash of the client file>`, so a picture can be cached for a year and a client update still shows the
new one. Without the folder (an export with `--no-icons`, or one made before RO3Emu had pictures) every page still
works and draws the first letter of the name instead; the next `RO3Emu.exe start` without `--no-icons` adds them.

The admin's **Economy log** reads the server's ledger (`economy_entry` with its `operation_receipt`) on the read
pool, newest first: filter by account, character, item (an id, or part of a name) and a range of UTC days. The totals
add up everything the filter matches; they only mean something for one item or currency.

## Profiles, guilds, the territory map and the market

These public pages show the realm's own state. They are read from the database on the read pool, from the cached
online list and, for the territory map, from TerritoryWarsService on the game loop at most once every 5 seconds. Heavy
traffic on them never reaches the game loop.

**Profiles** (`/profiles/<name>`; the names on *Who's online*, *Rankings* and the guild pages link to them) show what
another player can see in the game's details window. That is the class, the base and job level, the guild and the
position in it, the title rank (TitleConfig), the equipment with each slot's strengthen and refine levels, the
achievement level with each category's level and the latest achievements reached (`DBRoleAchievementInfo`), and the
character's place on every board where it is in the top 100.

A profile never shows the bags, purse, map, position, mail or account. It shows what the server last saved, so a
character in the game may be a few minutes ahead of its profile.

The game has one privacy choice: the setting *show detailed info* (`SettingShowDetailInfo`, `NoShow` or `ShowAll`).
When a player turns it off in the game, the profile shows only the name, class, levels and guild, and says the
player keeps the rest private. *(Inferred:)* a character that never saved the setting is shown in full, because the
client offers the setting as a way to hide details.

**Guilds** (`/guilds`, `/guilds/<id>`) list every guild by prestige. Each row has its badge (the frame
`State.Background` with the design `State.Icon` over it, both GuildIconConfig `_kBigIcon`), leader, level, members,
prestige, territories held and guild league tier. A guild's page also shows:

- the declaration (`Announce`; the internal notice stays private);
- the members with class, level, position (GuildPositionConfig) and Battle Merit;
- the territory zones it holds and the siege cities it defends;
- its guild league standing (tier, group, place, strength; GuildLeagueService's state);
- its place on the guild boards.

*(Inferred:)* the game has no guild Battle Merit, so the page adds up the members' scores on the Battle Merit board.

**Territory map** (`/territory`) shows the war's zones (GVGWarZoneConfig: the capital, its regions, their
strongholds and fields) with the guild that holds each. It also shows the stage in force, the next stage and the war's
coming battles (TerritoryWarStageConfig through TerritoryWarsClock), the battles running now with their guild scores
and the number fighting, and the siege cities (SiegeConfig 1001 and 1101-1103, the cities with an owner source) with
their defender, attacker and, while a siege runs, its stage. *(Inferred:)* the stage names in words.

The client draws its territory map from a UI prefab whose art no table names, so there is no picture to export. The
page draws a schematic of the zones' own tree instead: the capital in the middle, the regions around it, and each
region's strongholds (medium) and fields (small) fanned out around the region. A zone is filled with its holder's
colour and ringed while a battle runs on it. Each zone links to its map page.

**Market** (`/market`, `/market/<id>`) shows the following, read at most once every 30 seconds *(inferred interval)*:

- what is on offer: the exchange's listings (TradeService `trade_listing`) not yet expired, and the stock of open
  stalls (StallsService `stall`, player and system stalls);
- the lowest price, the last sale and the most traded items of the last 30 days (`trade_history`, the exchange's
  sales; the game itself shows its last 20 as an item's recent price);
- an item's daily price history over 90 days as a chart (the day's average with its low-high range and the quantity
  sold), its latest sales and its auction lots (AuctionStore);
- the guild materials exchange's hourly price (GuildStore `MarketState.PriceHistory`, the game's own last 168 hours).

Sellers on the exchange are not named, as in the game. A stall shows its owner, whose name stands over it in the
street.

## Item tracking

**Item tracking** (`/admin/items`, admins) finds an item by name or id and shows where it is held now and how it moved.
The places it counts:

| Place | From |
|---|---|
| Bag, Cart, Worn | the item table (cart = pack 100, worn = a grid slot) |
| Mail | the unclaimed letters of every mailbox: configured grants and attached instances |
| Exchange | listings in escrow |
| Stall | stall stock |
| Auction | open auction lots |

Each holding has a count, the instance guids, and links to the character and its account. Only characters that are not
deleted count. The economy's other holding places (caravan, escort and territory pendings, recovery) are not
counted, so a total is a lower bound. A currency has no holders here, only its ledger. Currencies are kept in the
characters' purses.

The ledger part adds up the item's legs in `economy_entry`: what its source legs let in, what its sink legs took out,
the first and the last entry. It also shows the latest 25 movements, which link to the full economy log.

The **duplication check** runs over one item (on its page) or over every item held anywhere (*Run the check*, the 200
worst). Its rules are *inferred*. They rest on the server's own economy invariant (`EconomyExactnessChecker`): what is
held changes by exactly the ledger's sources minus its sinks.

| Flag | When |
|---|---|
| Instance seen twice | one instance guid is in two places. A guid is unique in the item table, and a clone in escrow or mail keeps its guid only while the table row is gone. One character's stack and the part of it they listed (an exchange listing, a stall offer, a stall's partial take-back mail) share the guid by design and are not flagged |
| Exceeds ledger | more is held than the ledger's sources minus its sinks |
| Unrecorded | the item is held but the ledger never recorded it |

*Exceeds ledger* and *Unrecorded* assume that the ledger has run since the realm began. Items from before the ledger,
or from a database restored without it, show up as *Unrecorded*. The report gives the ledger's first entry, so you
can tell. Nothing here changes anything: the check is a lead for an admin to follow in the economy log.

## Behind nginx with https

The panel speaks plain HTTP. On a public server put a TLS-terminating proxy in front of it, bind the panel to
loopback and tell it to trust the proxy:

```bash
Ro3Emu__Panel__BindAddress=127.0.0.1
Ro3Emu__Panel__TrustedProxies__0=127.0.0.1
```

`Ro3Emu:Panel:BindAddress` moves only the panel; the game ports keep the server's address. With a trusted proxy
the panel takes the client's address from the last entry of `X-Forwarded-For` (for the login limit) and marks its
cookies `Secure` when `X-Forwarded-Proto` is `https`. From any other address both headers are ignored.

```nginx
server {
    listen 443 ssl http2;
    server_name panel.example.com;
    ssl_certificate     /etc/letsencrypt/live/panel.example.com/fullchain.pem;
    ssl_certificate_key /etc/letsencrypt/live/panel.example.com/privkey.pem;
    add_header Strict-Transport-Security "max-age=31536000" always;

    location / {
        proxy_pass http://127.0.0.1:5381;
        proxy_set_header Host $host;
        proxy_set_header X-Forwarded-For $proxy_add_x_forwarded_for;
        proxy_set_header X-Forwarded-Proto $scheme;
    }
}
```

Set `PublicUrl` or `TrustedProxies`: without either, the API of a panel on loopback answers 404 to a `Host` that is not
this machine (a page that resolves its own name to 127.0.0.1 could otherwise use it), and the log warns once in ten
minutes. Keep `Host` as the browser sent it: the panel refuses a POST whose `Origin` names another host. Forward only the
panel's port. The server's own HTTP port (5380) has operator endpoints that answer only a local tool
([security model](../systems/security.md)). If you proxy 5380 as well (the game's login and the launcher's calls), shut
just those off in the proxy, because `/ro3emu/status`, `/ro3emu/gate-key.pem`, `/ro3emu/panel` and
`/ro3emu/account/delete-role` are public and the launcher needs them:

```nginx
    location ~ ^/ro3emu/(gm|shutdown|online|reports|metrics)(/|$) {
        deny all;
    }
```

Logins share the server's credential budget (`Ro3Emu:LoginAttemptsPerMinute` per client address, an IPv6 one by its /64 network, the same counter as
the game's login, on the panel and on port 5380 alike). Behind a trusted proxy the address is the player's own, from
`X-Forwarded-For`. A proxy that is not listed in `TrustedProxies` is not believed, so every player behind it counts
against one budget: the proxy's. Loopback is exempt from the budget only for local tools: a request with
`X-Forwarded-For`, `Forwarded` or `X-Real-IP`, or any request on a server others reach (a public `PublicHost` or
bind, `TrustedProxies` or `PublicUrl` set), is counted like any other.

## Security notes

- **Sessions.** A random 256-bit id in an `HttpOnly`, `SameSite=Strict` cookie, `Secure` over https. Sessions live in
  the server's memory: a restart logs everyone out. A login always issues a new id. A password change signs out the
  account's other browsers, a ban all of them. The sessions page names a session by an HMAC of its id, never the id.
- **Two-factor authentication.** See [Account security](#account-security): TOTP secrets stay in the database, recovery
  codes only as hashes; codes are good once, compared in constant time, limited per login, per account and per
  address, and never logged.
- **Roles.** Every API route checks the caller's staff role on each request; a role taken away or a ban applies to the
  next request.
- **CSRF.** Every POST carries a token bound to the session cookie (before a login, to a visitor cookie) and is
  refused without it, or when its `Origin` is another site.
- **Passwords** are checked and stored by the server's own code (PBKDF2-SHA256, 100 000 rounds). A wrong password, an
  unknown account and a banned account get the same answer. The panel never logs a password.
- **Sign-up.** The game accepts any account name that is not empty once trimmed. A sign-up on the panel also refuses
  names over 64 characters or with control characters and passwords under 6 or over 128 characters (the panel's own
  rule, not the game's). A name that is taken in any letter case, or banned, is refused.
- **Pages.** No inline script or style, so the `Content-Security-Policy` allows only the panel's own files;
  `X-Frame-Options: DENY`, `nosniff`, `Referrer-Policy: same-origin`. Every value from the server reaches the page as
  text, never as HTML. Request bodies are capped at 16 KB; API answers are not cached.
- **Data.** Who's online shows character names, classes, levels and maps to everyone; the account behind a
  character only to admins. A public profile shows only what the game's details window shows another player, and less
  when the player turned *show detailed info* off.
- **Load.** The online list is read on the game loop at most once every 5 seconds, the level board from the database
  at most every 30; the other boards are RankService's own cache. Heavy traffic does not reach the loop.

## Suspicious activity

**Suspicious activity** (`/admin/suspicious`, moderators and up) lists what the server's anti-cheat saw over a window
(1 hour by default; the page switches between 1 h, 6 h, 24 h and 7 days). It is read only. What the server checks is in
[anti-cheat](../systems/anticheat.md).

| Alert | Listed when |
|---|---|
| Refused requests | an account had at least `Alerts:Refusals` (20) requests refused as forged or impossible in the window. The row names the character with the most and the counts by kind (`walk`, `flight`, `flood`, `quest-area`, ...) |
| Zeny gain | a character gained Zeny from the game (the ledger's source legs) past `Alerts:GainFactor` (10) times the median of the characters that gained any in the window, and at least `Alerts:ZenyFloor` |
| Item gain | the same for items, all items counted together; floor `Alerts:ItemFloor` |
| EXP gain | the same for base EXP, from `anticheat_exp` (the ledger has no EXP); floor `Alerts:ExpFloor` |
| Duplication | the duplication check's *Exceeds ledger* and *Instance seen twice* flags, when an Admin adds the check (it reads every holding) |

Each row links to the account and the character, or to the item for a duplication. The thresholds are server options
(`Ro3Emu:AntiCheat:Alerts`); the page shows the ones in force and the medians. The same refusals are in the server log as
`Anticheat <kind> account <name> role <id> <character>: <detail>`: the first five of each account and kind, then every
50th.

## What it stores

| Table | What |
|---|---|
| `admin_audit` | one row per admin action |
| `panel_news` | the news the public pages show, written with the news editor; `pinned` keeps an item on top |
| `panel_role` | the staff roles an Admin gave on the panel |
| `account_login` | the latest 100 logins of each account (panel or game; the address only with `LoginAddresses`) |
| `account_totp` | each account's two-factor secret, last used step and recovery code hashes |
| `feedback_triage`, `feedback_note` | the report inbox's status, assignee and notes |
| `panel_setting` | maintenance mode |
| `anticheat_refusal`, `anticheat_exp` | the anti-cheat's refusal counts and the EXP per character and hour, kept `AntiCheat:KeepDays` (30) days |

They are created by the server at startup like its other tables and are part of every backup. The update manager keeps
its files outside the database, in `data/updates/` ([Updates](#updates)).

## For developers

The pictures are decoded in `src/Ro3Emu.DataExport` (`IconExport`, `UnityImages`, `Png`): the VFS index maps a
sprite's file name to its bundle; the image is a texture of that name, a sprite over a texture of its bundle, or a
sprite packed into a sprite atlas (`<tag>.spriteatlasv2`, whose render data names the atlas texture and the
rectangle). Bundles are read with AssetsTools.NET and BC1/BC3/BC7 textures decoded with AssetRipper.TextureDecoder
(both MIT); the server itself only serves the files.

`src/Ro3Emu.Panel` holds the website: the static files (`www/`, embedded in the assembly; the fonts are the
launcher's; the colours, spacing scale, type scale and components are the tokens at the top of `app.css`), the JSON API under `/api/` (`PanelSite`), sessions and CSRF. It knows nothing of the game: everything
goes through `IPanelBackend`, `IPanelAdminTools` and `IPanelOperations` (roles, security, reports, character actions,
server control), which the server implements in `Http/PanelBackend*.cs`, `Http/PanelAdminTools*.cs` and
`Http/PanelOperations*.cs` over its stores, writer and loop, and `IPanelUpdates`, which `Updates/UpdateManager` implements
(`ReleaseFeed`, `UpdatePackage`, `BundleVersion`, `UpdateInstaller`, `UpdateHosting`). A page in its own file registers with
`registerAdminPage({ ..., role })`; `adminAccountCards` takes cards for an account's admin page.
`ServerHost` routes every request that arrives on the panel's port to `PanelSite` before the server's own
middleware, so neither port serves the other's paths. Tests: `PanelTests`; the test hosts keep the panel off unless a
test turns it on with a free port, and their update checks off (`TestHostDefaults`). `PanelAdminTests` cover roles,
security, reports, character actions and server control; `UpdateTests` and `PanelUpdateTests` the update manager, with a
fake HTTP stack or a loopback releases feed and invented packages.

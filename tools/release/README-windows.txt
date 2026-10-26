RO3Emu - play Ragnarok Online 3 (PC) on your own server
=======================================================

Nothing to install. Extract the whole zip, then double-click RO3Emu.exe.
If Windows says "Windows protected your PC" (RO3Emu is not signed), click
"More info", then "Run anyway".

The first time it:
  1. finds your Ragnarok 3 game folder (the one with ro3.exe), or lets you pick it,
     or downloads the game from the official Genesis servers (about 24 GB; every
     file is checked against the publisher's signature);
  2. exports the game data it needs from your game folder (about a minute);
  3. starts the server on this PC;
  4. patches the game to talk to it and starts the game.

Log in with any account name and password: the account is created on first login.
Keep the RO3Emu window open while you play; press Q there (or close it) to stop the server.
Next time, just double-click RO3Emu.exe again.

Other things it can do (open a command prompt in this folder):
  RO3Emu.exe join 203.0.113.10     play on a friend's server
  RO3Emu.exe host                  let friends on your network join your server
  RO3Emu.exe install               download the game from the official servers
  RO3Emu.exe update                update the game from the official servers
  RO3Emu.exe restore               undo the patch (the game talks to the official servers again)
  RO3Emu.exe diag                  write RO3Emu-diagnostics.zip for a bug report
  RO3Emu.exe help                  everything else

Skill animation diagnostics (only when the server owner asks)
  - Join normally first so RO3Emu can apply the complete client patch. Copying only
    client\Ro3Emu.ClientHooks.dll into the game folder is not enough.
  - To opt in on this PC, create an empty file named ro3emu\skilltrace.on under the
    game folder (beside ro3.exe), then restart the game.
  - Delete that marker and restart the game to opt out. A server grant alone never
    turns tracing on, and the diagnostic sends no account name, IP address, chat,
    credentials, raw packet or stack trace.

What it changes on your PC
  - In the game folder: a few DLLs in ro3_Data\Managed (the originals are kept in
    ro3emu-backup; "RO3Emu.exe restore" puts them back) and ro3emu\loc_en.tsv
    (English texts made from your own game files).
  - In this folder: data\ (game data and your characters), logs\, downloads\ and
    RO3Emu.settings.json. Delete the folder to remove RO3Emu.
  - Do not use the publisher's launcher to repair the game while you play on
    RO3Emu: it undoes the patch (RO3Emu.exe patches again on the next start).

If something goes wrong
  RO3Emu writes RO3Emu-diagnostics.zip and tells you where it is. Open an issue at
  https://github.com/aethai/ro3emu-server/issues and attach that file.

RO3Emu does not include or host any game file. It is not affiliated with Gravity or
any publisher of Ragnarok Online.

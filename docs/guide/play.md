# Play on your own PC

The full guide behind the README's three steps.

1. Download **`RO3Emu-win-x64.zip`** from the [latest release](https://github.com/aethai/ro3emu-server/releases/latest)
   and extract it anywhere.
2. Double-click **`RO3Emu.exe`**. Nothing to install: no .NET, Java or Node.js. If Windows says "Windows protected
   your PC" (RO3Emu is not signed), click *More info*, then *Run anyway*.
3. The first time, it finds your Ragnarok 3: Genesis game folder (or asks for the one with `ro3.exe`), prepares the
   game data it needs (about a minute), starts the server and the game. Next time it only starts the server and the
   game.
4. Log in with any account name and password: the account is created on first login. Keep the RO3Emu window open
   while you play; press **Q** there (or close it) to stop the server.

**Don't have the game?** RO3Emu.exe downloads it from Genesis' update servers (about 24 GB) and checks every
file against Genesis' signature: it offers this when it finds no game folder, or run `RO3Emu.exe install`.
If it cannot reach the servers, your internet provider may block Genesis' mirror: connect through Cloudflare WARP
(or another VPN) and try again, or install the game with the Genesis launcher and choose "I have the game" (issue #104).
RO3Emu never hosts game files. Tested with the Genesis client releases of 7, 16, 21, 23, 25, 29 and 30 October 2026 (2026.10.30.1,
the current one); the 20 October release passes `RO3Emu.exe check` and the data export.

| Command | What it does |
|---|---|
| `RO3Emu.exe` | play on your own server (same as `RO3Emu.exe start`) |
| `RO3Emu.exe join <address>` | play on someone else's server (`host`, `host:port` or `http://host:port`) |
| `RO3Emu.exe host` | 在本機啟動伺服器供朋友連線；每個 GM 帳號各指定一次，例如 `--gm you --gm friend`。登入畫面可另設標籤：`--cluster-name <name> --area-name <name>`。 |
| `RO3Emu.exe install [folder]` / `update [folder]` | 從 Genesis 更新伺服器下載遊戲，並驗證每個檔案。 |
| `RO3Emu.exe restore` | 還原修補，讓遊戲重新連線至 Genesis 伺服器。 |
| `RO3Emu.exe check` | does this game version fit this RO3Emu? (changes nothing) |
| `RO3Emu.exe diag` | makes `RO3Emu-diagnostics.zip` for a bug report |

Your server also serves a [web panel](panel.md) on `http://127.0.0.1:5381` (your account, characters, rankings, and
an admin area for GM accounts). `--panel-port <n>` moves it, `--no-panel` turns it off; two servers on one PC need
different ports for it too (`--http-port`, `--micro-port`, `--game-port`, `--panel-port`).

**What it changes.** In the game folder: a few DLLs in `ro3_Data\Managed` (the originals are kept in `ro3emu-backup`)
and `ro3emu\loc_en.tsv` (English texts, built from your own game files). Next to RO3Emu.exe: `data\` (game data and
your characters), `logs\` and `RO3Emu.settings.json`.

> [!WARNING]
> Don't let the Genesis launcher repair a patched game folder: it puts the original DLLs back. Run RO3Emu.exe
> again and it patches the game again.

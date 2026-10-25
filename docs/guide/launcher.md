# Launcher and client distribution

Players can get one file, `Ro3EmuLauncher.exe` (`src/Ro3Emu.Launcher`, .NET 10 WPF + WebView2, self-contained). It
installs the RO3 client patched for your server, keeps it updated, updates itself and starts `ro3.exe` with the launch
proof. The official RO3 launchers cannot be reused for this: they pin the publisher's manifest signing key and update
hosts in the exe, update themselves, and their Repair restores the original DLLs.

This page explains how to host your own patch repository and publish to it.

## How it works

- `src/Ro3Emu.Patcher` is the patch engine: a content-addressed repository, FastCDC chunks, zstd, a staging folder plus
  a journal commit, and self-update by renaming the running exe. The download cache keeps compressed chunks and
  deletes each one after its last use, so a fresh install needs about the client's size in free space, not twice it.
- The repository is plain static files, served by any web server (nginx, Apache, a CDN bucket):
  - `channels/<channel>.json`: the only mutable file; it names the current client manifest and launcher for a channel
    (for example `test` or `live`);
  - `manifests/`: one immutable manifest per published version;
  - `chunks/`: the content-addressed, compressed data chunks;
  - `launcher/<version>/Ro3EmuLauncher.exe`: published launcher builds for self-update;
  - optional `news.json` for the launcher page:
    `{ "items": [ { "tag", "color": "blue|green|orange|red", "title", "text" } ] }`.
- The launcher pings `<server>/ro3emu/status` (player count only) for the online indicator.

## Configure the launcher

`src/Ro3Emu.Launcher/launcher.json` is built into the exe (the csproj property `LauncherConfigFile` can point to
another file). A `launcher.json` placed beside the exe overrides the built-in one, which is handy for testing against a
local repository.

```json
{
  // Patch repository (static HTTP) and the channel this launcher follows.
  "repository": "https://patch.example.com/ro3/",
  "channel": "test",
  // The game server's HTTP base; the launcher pings it for the online indicator.
  "server": "http://127.0.0.1:5380",
  "serverName": "RO3Emu"
}
```

Set `repository` to the public URL of your repository folder and `server` to your game server's public HTTP address.

## Releasing

All commands below use placeholders: `<client dir>` is a clean official RO3 PC install (the folder with `ro3.exe`),
`<repo dir>` is your local copy of the repository folder, `<server url>` is your game server (for example
`http://127.0.0.1:5380` for a local test) and `<channel>` is the channel name.

### Client

Publish a new client version after a change to `Ro3Emu.ClientHooks` or the localization, or when the server address
or gate key changes:

```
Ro3Emu.ClientTools release <client dir> <server url> <gate_rsa_public.pem> <repo dir> <version> <channel> 9 --loc-map <loc_en.tsv>
```

The published file set is exactly the official install (`.ro3-update/verified-baseline.txt`). The patched DLLs are
rebuilt from the originals in `ro3emu-backup` inside a staging copy, so the source client itself may already be
patched for another server. The optional `--loc-map` file is published as `ro3emu/loc_en.tsv`; without that option,
the tool uses `<client dir>/ro3emu/loc_en.tsv` when it exists. This lets a clean official client stay untouched while
a reviewed localization overlay is supplied from elsewhere. Only changed chunks are new, so a hooks or localization
change is a few MB.

### Launcher

Bump `<Version>` in `src/Ro3Emu.Launcher/Ro3Emu.Launcher.csproj` first, then:

```
dotnet publish src/Ro3Emu.Launcher -c Release -o <out>
Ro3Emu.ClientTools release-launcher <repo dir> <channel> <out>\Ro3EmuLauncher.exe <version>
```

### Upload

Copy `chunks/`, `manifests/` and `launcher/` to the web server first and `channels/` last, so a player never sees a
channel that points at files that are not there yet. Any method works (rsync, scp, tar over ssh, an object-storage
sync). Also put a copy of `Ro3EmuLauncher.exe` at the repository root (for example
`https://patch.example.com/ro3/Ro3EmuLauncher.exe`) as the download link for new players.

## Development

- `RO3EMU_LAUNCHER_UI=<path to src/Ro3Emu.Launcher/ui>` serves the launcher page from disk and enables the dev tools.
- `ui/index.html?mock=install|update|ready|downloading|repair|offline` previews the page in a normal browser.
- A `launcher.json` beside the exe overrides the built-in one (for example a local repository such as
  `http://localhost:8790/`).

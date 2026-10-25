# Launcher and client distribution

Players can get one file, `RO3Launcher.exe` (`src/Ro3Emu.Launcher`, pure .NET 10 WPF, self-contained). It
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
  - `channels/<channel>.json.sig`: optional detached RSA-PSS/SHA-256 publisher signature;
  - `manifests/`: one immutable manifest per published version;
  - `chunks/`: the content-addressed, compressed data chunks;
  - `launcher/<version>/RO3Launcher.exe`: published launcher builds for self-update;
  - `content/news/<sha256>.json`: optional immutable launcher notices and banners, referenced by the signed Channel:
    `{ "items": [ { "tag", "color": "blue|green|orange|red", "title", "text" } ],
       "banners": [ { "image": "https://...", "url": "https://..." } ] }`.
- The launcher pings `<server>/ro3emu/status` (player count only) for the online indicator.
- The game process is resolved only as `<installed client directory>/ro3.exe`, where the installed client directory
  is the directory managed by this launcher. The signed manifest must name `ro3.exe`, include that file, and use a
  relative non-escaping path; the launcher never falls back to a Genesis/Asia installation elsewhere on the PC.

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
  "serverName": "Ragnarok 3",
  // Optional. If omitted, Register explains that accounts are created on first login.
  "registerUrl": "https://account.example.com/register",
  "websiteUrl": "https://www.example.com/",
  "discordUrl": "https://discord.gg/example",
  "allowedExternalHosts": [ "www.example.com", "discord.gg" ],
  "requireHttpsRepository": true,
  "requireSignedChannel": true,
  // Optional LAB/test isolation. Player releases normally omit this and use %APPDATA%/RO3Emu.
  "settingsFile": "D:/isolated-lab/launcher-settings.json",
  "trustedPublisherKeys": {
    "my-server-2026": "-----BEGIN PUBLIC KEY-----\n...\n-----END PUBLIC KEY-----"
  }
}
```

Set `repository` to the public URL of your repository folder and `server` to your game server's public HTTP address.
For an isolated Tailscale/LAN lab, both trust flags may be `false`. A public player release should require HTTPS and
a signed channel.

## Publisher identity

Create an offline RSA-3072 publisher identity in an empty folder:

```
dotnet run --project src/Ro3Emu.ReleaseTools -- keygen <key folder> <key-id>
```

Keep `repository-private.pem` offline. Put only the generated public key from `launcher-trust.json` into
`launcher.json`. Sign an existing channel after publishing a client:

```
dotnet run --project src/Ro3Emu.ReleaseTools -- sign-channel <repo dir> <channel> <private.pem> <key-id>
```

The launcher verifies the detached signature before reading the channel. The signed channel authenticates the
manifest, launcher self-update and news hashes, has an expiry, and carries a positive monotonically increasing
sequence. Each launcher remembers the highest accepted sequence and rejects a rollback. A public release also rejects
an HTTPS-to-HTTP redirect. Banner links open only when their HTTPS host is explicitly listed in
`allowedExternalHosts`; the launcher-owned footer and registration URLs are allowed by exact configured URL.

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
dotnet run --project src/Ro3Emu.ReleaseTools -- publish-launcher <repo dir> <channel> <out>\RO3Launcher.exe <version> <private.pem> <key-id>
```

Publish notices and banner metadata through the same signed Channel instead of copying an unsigned `news.json` to
the repository root:

```
dotnet run --project src/Ro3Emu.ReleaseTools -- publish-news <repo dir> <channel> <news.json> <private.pem> <key-id>
```

The publisher validates every existing manifest, launcher and news reference before it signs a new Channel. It also
refuses to overwrite an existing launcher version with different bytes; publish changed bytes under a new version.

### Upload

Copy `chunks/`, `manifests/`, `launcher/` and `content/` to the web server first and `channels/` last, so a player never sees a
channel that points at files that are not there yet. Any method works (rsync, scp, tar over ssh, an object-storage
sync). Also put a copy of `RO3Launcher.exe` at the repository root (for example
`https://patch.example.com/ro3/RO3Launcher.exe`) as the download link for new players.

## Development

- The launcher UI is pure WPF; it does not require WebView2.
- Private visual assets can be imported from a locally owned, inspected launcher without placing them in Git:
  `tools/Import-OfficialLauncherAssets.ps1 -ReferenceDirectory <REA/ILSpy reference directory>`.
  The destination `src/Ro3Emu.Launcher/PrivateAssets/` is gitignored.
- A `launcher.json` beside the exe overrides the built-in one (for example a local repository such as
  `http://localhost:8790/`).
- `uiFixture` is a local visual-test switch (`home`, `progress`, `queue`, `maintenance`, `failure-help-open` or
  `settings`). Never put it in a player release configuration: fixture mode intentionally does not contact a patch
  repository or start the game.
- `settingsFile` is optional and exists so an isolated LAB can keep accepted channel sequences and the selected
  client directory out of the player's normal AppData. Relative paths resolve beside the launcher. Omit it from a
  normal player configuration.

Run the deterministic visual matrix after changing WPF layout or state projection:

```
dotnet run --project tools/LauncherVisualMatrix -- <output directory>
```

It produces 36 PNGs plus layout JSON for six states, two logical window sizes, and 96/120/144 DPI. A successful run
still reports `PARTIAL`, because an off-screen `RenderTargetBitmap` is not a real monitor-DPI transition.

`tools/LauncherProcessE2E` is a deliberately explicit LAB harness for signed loopback process acceptance. It can
prepare and serve a synthetic client repository, prove the 15-second bounded old/new process handoff, and verify
install/update/repair hashes without launching a real game. Its private test key and generated client must remain in
`TestResults` or another disposable LAB directory; never reuse them for a player channel.

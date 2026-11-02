# Launcher and client distribution

玩家取得單一 `RO3Launcher.exe`（`src/Ro3Emu.Launcher`，純 .NET 10 WPF、自含執行環境），
即可安裝已設定為連線至此伺服器的 RO3 客戶端、更新遊戲與啟動器，並以啟動證明開啟 `ro3.exe`。
Genesis 啟動器的執行檔固定了 Genesis 的 manifest 簽章金鑰與更新來源，且會自行更新；
執行其修復功能時會還原原始 DLL，因此本專案使用獨立啟動器發送自有簽章更新。

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
- 啟用預設的 `requireServerClientRelease` 時，正式機契約或更新站無法核對就停止啟動；不能退回離線模式。
- Only when strict server-client pairing is explicitly disabled, the launcher permits an offline start when its local
  state belongs to the configured channel, names a packaged launch executable and every managed file still matches
  the last committed size and timestamp. A loose or modified `ro3.exe` is blocked until **Reconnect** can verify and
  repair it; this prevents an official or otherwise unpatched client from silently connecting to the wrong server.

## Configure the launcher

`src/Ro3Emu.Launcher/launcher.json` is built into the exe (the csproj property `LauncherConfigFile` can point to
another file). A `launcher.json` placed beside the exe overrides the built-in one, which is handy for testing against a
local repository.

```json
{
  // 更新來源必須使用 HTTPS；HTTP 僅允許 localhost 本機測試。
  "repository": "https://patch.example.com/ro3/",
  "channel": "test",
  // Optional client folder. Relative paths resolve from RO3Launcher.exe; user-selected Settings still wins.
  "clientDirectory": ".",
  // The game server's HTTP base; the launcher pings it for the online indicator.
  "server": "http://127.0.0.1:5380",
  "serverName": "Ragnarok 3",
  // Optional. If omitted, Register explains that accounts are created on first login.
  "registerUrl": "https://account.example.com/register",
  "websiteUrl": "https://www.example.com/",
  "discordUrl": "https://discord.gg/example",
  // 選用：伺服器對外公布的網頁面板；未設定就不顯示入口。
  "panelUrl": "https://panel.example.com/",
  "allowedExternalHosts": [ "www.example.com", "discord.gg" ],
  "requireHttpsRepository": true,
  "requireSignedChannel": true,
  // Optional LAB/test isolation. Player releases normally omit this and use %APPDATA%/RO3Emu.
  "settingsFile": "D:/isolated-lab/launcher-settings.json",
  // 選用：只保留本機設定檔身份，不是下載網址；settingsFile 有設定時優先。
  "settingsIdentity": "my-server-stable",
  "trustedPublisherKeys": {
    "my-server-2026": "-----BEGIN PUBLIC KEY-----\n...\n-----END PUBLIC KEY-----"
  }
}
```

Set `repository` to the public URL of your repository folder and `server` to your game server's public HTTP address.
遠端更新來源一律要求 HTTPS，包含 Tailscale 與區域網路；關閉信任旗標也不會放行遠端 HTTP。
HTTP 僅允許 loopback 本機測試。正式玩家更新仍應同時啟用 HTTPS 與發行者簽章，兩者不能互相替代。 `clientDirectory` defaults to `.` so a packaged launcher installs and starts `ro3.exe` beside
`RO3Launcher.exe`. Players can still choose another folder in Settings; their saved choice wins. A split layout can
opt in explicitly: `G:\RO3 test\RO3Emu\RO3Launcher.exe` plus `G:\RO3 test\Client\ro3.exe` uses `../Client`.

`panelUrl` 對應上游 v1.0.25 的網頁面板入口，接受完整的 HTTP 或 HTTPS 網址。
設定有效網址後，WPF 上方導覽列顯示「網頁面板」，點擊才以預設瀏覽器開啟。
未設定或使用其他協定時不顯示；登入器不會探測玩家電腦、啟動本機伺服器或自行啟用面板。
反向代理網址可配合伺服器的 `Ro3Emu:Panel:PublicUrl` 使用，兩邊應指向同一公開入口。
面板網址只允許設定中的完整網址；其他公告連結仍套用原有 HTTPS 與主機允許清單。
管理端也可明確執行 `RO3Emu.exe panel [address]`，查詢已啟動伺服器的面板並開啟瀏覽器。

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

All commands below use placeholders: `<client dir>` is a clean Genesis RO3 PC install (the folder with `ro3.exe`),
`<repo dir>` is your local copy of the repository folder, `<server url>` is your game server (for example
`http://127.0.0.1:5380` for a local test) and `<channel>` is the channel name.

### Client

Publish a new client version after a change to `Ro3Emu.ClientHooks` or the localization, or when the server address
or gate key changes:

```
Ro3Emu.ClientTools release <client dir> <server url> <gate_rsa_public.pem> <repo dir> <version> <channel> 9 --loc-map <loc_en.tsv> [--skill-trace]
```

The published file set is exactly the Genesis install (`.ro3-update/verified-baseline.txt`). The patched DLLs are
rebuilt from the originals in `ro3emu-backup` inside a staging copy, so the source client itself may already be
patched for another server. The optional `--loc-map` file is published as `ro3emu/loc_en.tsv`; without that option,
the tool uses `<client dir>/ro3emu/loc_en.tsv` when it exists. This lets a clean official client stay untouched while
a reviewed localization overlay is supplied from elsewhere. Only changed chunks are new, so a hooks or localization
change is a few MB.

During a bounded animation investigation, `--skill-trace` adds the zero-byte `ro3emu/skilltrace.on` marker to the
signed release. Updated players then enable the client-side skill trace automatically without a chat command or a
manual file. This may create additional player logs and telemetry, so remove the option from the next release after
the investigation; the launcher will delete the marker because it is no longer owned by that manifest.

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

For a small Tailscale-only Windows host, the repository includes a static server that binds one explicit address,
does not list directories, and supports the byte ranges used to resume chunk downloads. The host needs the .NET 8
runtime (or publish the tool self-contained):

```
dotnet publish tools/Ro3Emu.RepositoryServer -c Release -o <server tool dir>
dotnet <server tool dir>/Ro3Emu.RepositoryServer.dll <repo dir> --bind 127.0.0.1 --port 8080 --path /ro3/
```

`<repo dir>` is the directory whose root contains `channels/`, `manifests/`, `chunks/` and `RO3Launcher.exe`.
此命令只提供本機 HTTP 後端；遠端玩家應透過具有有效憑證的 HTTPS 反向代理連線。
例如 Tailscale Serve 可將 tailnet 的 HTTPS FQDN 轉送到 `http://127.0.0.1:8080`，並保留 `/ro3/` 路徑。
先核對實際 DNS、憑證與完整通道／簽章／清單／區塊路徑，再發布使用該 HTTPS 位址的新登入器。
不要改成接受不受信任的憑證，也不要為 Tailscale IP 放寬 HTTP 限制。
既有執行檔旁的 `launcher.json` 優先於內嵌設定，遷移時也必須同步檢查該檔。
原有各來源設定檔的安裝目錄與最高通道序號需要一併承接；換主機名稱不會自動載入舊來源設定。
發行者私鑰留在發布管理端，不能放入對外提供的目錄。

參考 [Tailscale Serve](https://tailscale.com/docs/features/tailscale-serve)；啟用 HTTPS、調整代理與正式發布
是獨立部署操作，不會因為編譯登入器而自動完成。

## Player diagnostics

正式機契約核對、清單解壓、檔案盤點與 SHA-256 掃描都在背景執行，不占用 WPF 視窗執行緒。
核對畫面顯示目前步驟的百分比、已處理／總檔案數、資料量與目前檔案；大型檔案讀取中也持續更新。
尚未取得總量時顯示「處理中…」與不定進度列，不用固定 0% 冒充進度。
百分比代表目前步驟已處理的比例；缺檔或長度錯誤也會計入已處理範圍，但仍列為待修復。
完成一次掃描不代表可啟動：完整 SHA-256、簽署清單及掃描後再次核對正式機契約全部通過後才會就緒。
核對期間仍可操作視窗或按「暫停作業」。進度回報節流，階段切換與完成回報不會被略過；
雜湊讀取共用緩衝區，避免大量小檔案反覆配置大型物件。

The download percentage covers compressed repository chunks. After it completes, the launcher changes to
**Decompress, assemble and verify files** while it builds and hashes the full installed client; this second phase can
write substantially more data than the download size. A phase that has not actually finished is displayed at no more
than 99%, and the current relative file name is shown while assembling.

`launcher.log` is kept beside `RO3Launcher.exe` (rotated to `launcher.log.old` at 2 MB). It records phase changes,
bounded progress samples, the selected client directory, exceptions and the exact game PID/exit code. Ask a player
for this file when an update or launch appears stuck; it contains no account password.

遊戲成功通過啟動證明與早退檢查後，登入器會自動關閉，避免背景 UI 持續占用資源；遊戲會繼續執行。
啟動失敗時保留登入器並顯示原因。再次開啟登入器，會先依選定客戶端的執行檔完整路徑辨識既有遊戲；
最多同時四開，第四個啟動後仍會關閉登入器，第五個會被阻擋。遊戲執行期間不會更新、修復或切換安裝目錄。
若無法讀取同名程序的執行檔位置，登入器會停止這些操作並提示使用相同權限重開，避免誤判。
尚未啟動遊戲時，裝飾動畫只在前景首頁播放；失焦、最小化或切換分頁會停止，恢復時沿用原動態效果設定。
裝飾動畫限制為每秒 20 格，輪播圖片快取最多保留三張，不影響更新驗證或玩家在遊戲內選擇的語系。

## Development

- The launcher UI is pure WPF; it does not require WebView2.
- Private visual assets can be imported from a locally owned, inspected launcher without placing them in Git:
  `tools/Import-OfficialLauncherAssets.ps1 -ReferenceDirectory <REA/ILSpy reference directory>`.
  The destination `src/Ro3Emu.Launcher/PrivateAssets/` is gitignored.
- A `launcher.json` beside the exe overrides the built-in one (for example a local repository such as
  `http://localhost:8790/`).
- `uiFixture` is a local visual-test switch (`home`, `progress`, `queue`, `maintenance`, `failure-help-open`,
  `offline-unmanaged` or `settings`). Never put it in a player release configuration: fixture mode intentionally does not contact a patch
  repository or start the game.
- `settingsFile` is optional and exists so an isolated LAB can keep accepted channel sequences and the selected
  client directory out of the player's normal AppData. Relative paths resolve beside the launcher. Omit it from a
  normal player configuration.

未指定 `settingsFile` 時，各更新來源分別使用 `%APPDATA%\RO3Emu\launcher-settings-<repository>.json`，避免
不同伺服器的啟動器共用遊戲目錄與已接受的簽章更新序號。新來源設定檔尚不存在時，會承接舊共用
`launcher-settings.json` 的既有選擇；已有來源設定不會被舊檔覆蓋。明確指定 `settingsFile` 時不讀取舊共用檔，
維持 LAB 隔離與本地的更新序號倒退拒絕規則。

更新來源由 IP 改為 HTTPS 主機名稱時，可明確設定 `settingsIdentity`，沿用舊的
`launcher-settings-<identity>.json`，保留玩家選定的客戶端目錄與已接受的最高更新序號。
身份限 1 至 128 個小寫英數字，可含 `.`、`-`、`_`，首尾須為英數，不能是網址或檔案路徑。
例如原來源 `http://100.126.246.106:8080/ro3/` 的既有身份為 `100.126.246.106_8080_ro3`。
它只決定本機設定檔名稱；真正下載仍使用 `repository`，HTTPS、簽章及正式機契約照常驗證。
明確身份不存在時不回落其他共用設定，也不掃描玩家電腦猜測舊目錄。
未設定身份則沿用原本按更新來源分檔的行為；`settingsFile` 仍有最高優先權。

私人更新站可設定 `repositoryConnectionAddress` 為 `100.126.246.106`，僅限
`https://hiroshi-pc3.taila2669f.ts.net:443/` 這個核准來源。未使用 Tailscale DNS 的玩家也能連線，
但仍須連上可達該位址的私人網路。這只指定 TCP 目的地，原始 HTTPS URI、SNI、Host、憑證鏈與主機名稱
驗證均保留；不更動系統 DNS 或憑證信任庫。此模式停用更新站連線的代理與自動重導向，其他來源不能借用
這個位址。未設定時使用原本的 DNS／代理行為。旁置 `launcher.json` 仍優先於內嵌設定，因此發布前應核對
實際載入的設定，而非只確認內嵌檔案已更新。

Run the deterministic visual matrix after changing WPF layout or state projection:

```
dotnet run --project tools/LauncherVisualMatrix -- <output directory>
```

It produces 42 PNGs plus layout JSON for seven states, two logical window sizes, and 96/120/144 DPI. A successful run
still reports `PARTIAL`, because an off-screen `RenderTargetBitmap` is not a real monitor-DPI transition.

`tools/LauncherProcessE2E` is a deliberately explicit LAB harness for signed loopback process acceptance. It can
prepare and serve a synthetic client repository, prove the 15-second bounded old/new process handoff, and verify
install/update/repair hashes without launching a real game. Its private test key and generated client must remain in
`TestResults` or another disposable LAB directory; never reuse them for a player channel.

# RO3Launcher 2.2.23 clean-room reconstruction ledger

This document records the behavioral evidence used to rebuild the RO3Emu player launcher. The official executable
and extracted artifacts are analysis inputs only. The repository contains newly written source and never contains the
official publisher private keys, official endpoints, or locally imported proprietary artwork.

## Artifact identity

| Artifact | SHA-256 | Static identity |
|---|---|---|
| `G:\Genesis Launcher\RO3Launcher.exe` | `646c93e3702f4f95d79516f7031d98ede6371087240b30a700a6761f64195ae2` | .NET 10 single-file host |
| Embedded `RO3Launcher.dll` | `213911abb10b01027bd05621e9d35e6edf49303413a3dacc756b8e355006d925` | 2.2.23.0, 59 types, 427 methods |
| Embedded `Ro3.Launcher.Platform.dll` | `1a4db28092b6c0304a0fd7368169053a8799f0caf560bf17a161e513fdb581dd` | 68 types, 382 methods |
| Embedded `Ro3.Recovery.Core.dll` | `61efb8e70a4de2601ab2313ed53456df8a9ec18bfcfd49ccb6e3fd4c0a24a3a1` | 226 types, 1,718 methods |

REA evidence:

- Outer single-file host and embedded assembly marker: `ev_0dc1577d624064ad4daa9f8dc2aecbd6d4c675d88c99967c83a019083bc9b7b7`,
  `ev_4b1621ce61d306f1f43eff31fdb0285d92792a842313326d0a156a1aafd8549b`.
- Managed entry assembly identity and complete metadata summary:
  `ev_5d5e0e738442aae8445c52ef11adb9371311ee1cf2114392f88212068bf23bb6`.
- Launcher view-model member selection: `ev_af2a207afa8afb397d6be14ffd4d59372b91d831b6f0a1be7ef9eeebe1b03862`.
- Platform layer summary: `ev_2fbaca9325125b5de9fee9c43841c86eff718f7155b168d0d857cc4751e623c3`.
- Recovery engine summary: `ev_73fe16642c52db961bc611cd502cfc83d4a93a4e4a07ebb621d02a02c43f34ce`.

## Observed behavior mapped to new source

| Observed behavior | Clean-room implementation | Current verification |
|---|---|---|
| 1200×800 WPF shell over a 1536×1024 design surface | `Ro3Emu.Launcher/MainWindow.xaml` | Built, runtime-captured and compared with the official window in the same session |
| Home, news and settings navigation | pure-WPF tabs and code-behind | Runtime-captured |
| Registration link, 5-second banner carousel, clickable bullets, notice cards and 22 px/s hover-paused loop | pure-WPF controls and animations | Static behavior mapping plus deterministic local fixtures; current full DPI pixel matrix is pending |
| Fixed-seed ambient particles and poring drift | 14 sprites, seed 7351, matching observed timing/easing constants | Static behavior mapping plus local fixture runtime; video-cycle comparison is pending |
| Rounded progress, queue/maintenance/retry states, recovery actions and error-help panel | pure-WPF state projection | Progress geometry compared with the live official checking state; deterministic synthetic states are available |
| Install, verify and ready steps | state projection from `LauncherController` | Synthetic/static state paths |
| Install, update, repair, pause/resume and launch | existing `Ro3Emu.Patcher` plus `GameStarter` | Patcher tests; live player test pending |
| SHA-256 file/chunk validation and journaled commit | `Updater` | Automated tests |
| Publisher-authenticated channel | `RepositoryTrust` and detached RSA-PSS/SHA-256 signature | Automated tests |
| Signed news and self-update metadata | content-addressed hashes and URLs authenticated by signed Channel | Automated tamper and self-update swap tests; live process restart pending |
| Channel expiry and rollback resistance | positive `ChannelInfo.Sequence`, required expiry and per-user accepted sequence | Automated trust tests; live rollback pending |
| Publisher continuity | validates existing references, refuses same-version launcher overwrite and signs Channel last | Automated negative tests |
| Traditional Chinese UI | WPF labels with the observed YaHei geometry and JhengHei fallback | Runtime-captured |
| Private artwork without public redistribution | local import script and gitignored `PrivateAssets` | Build verified |

## Evidence boundaries

- **Observed:** artifact hashes, managed metadata, type/method inventories and WPF resource/layout facts.
- **Inferred:** decompiler-produced C# and reconstructed intent. Decompiler output is not canonical source.
- **Runtime verified:** the rebuilt launcher and official 2.2.23 launcher were opened and captured in the same Windows
  session. Window/design-surface geometry, header, home/news/settings panels, banner slot, step/progress area, recovery
  actions and footer positions match. The rebuilt copy reached its offline/reconnect state without starting the game.
- **Content boundary:** the frame is intentionally reproduced, while account URLs, news, banners, localization and
  release metadata belong to RO3Emu and must not impersonate the official service.
- **Automated verification:** the isolated patcher suite currently has 32 passing, 0 failing and 0 skipped tests,
  including signed install/update/repair, signed-news tamper rejection, self-update swap/tamper handling and publisher
  refusal cases. This is synthetic/local evidence, not a public CDN or player-login acceptance.
- **Not yet accepted:** an automated pixel-difference threshold across every dynamic state, a signed production
  repository, self-update over the production host, and end-to-end player install/update/repair/login.
- **Out of scope:** official Asia account enrollment, publisher anti-cheat identity and official service credentials.

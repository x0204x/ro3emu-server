# In-game bug reports

Players report problems from inside the game; the server stores each report with who, where and when. Code:
`src/Ro3Emu.Server/Game/FeedbackService.cs`, `src/Ro3Emu.ClientHooks/FeedbackButton.cs`.

## How a player reports

- **Menu → Feedback** (the side column of the function menu, next to Ranking). A text panel opens; type what went
  wrong and confirm. Up to 500 characters in the panel.
- **Chat:** `/bug <text>` (or `/feedback <text>`) in any channel. The line is not shown to other players. The chat
  input holds 60 characters, so longer reports go through the button, or as several `/bug` lines.

Either way the marquee answers `Thank you! Report #N was saved.` Empty reports get a usage hint; a second report
within 5 seconds is refused (`Ro3Emu:Feedback:CooldownSeconds`).

## What the client's Feedback button did before

Nothing, in this client build:

- `UI/Main/Controllers/UI_Sys_MainFunctionMenuWidgetBaseCtrl.lua:305-313` maps `enum_FunctionType.Feedback` (41,
  `Config/Define/Lua_GameDefine.lua:6799`) to the menu's "Feedback" view; a click runs `_onClickFunction`
  (`:1070-1088`), which closes the menu and calls `LC_FunctionHelper.OpenFunctionPanelDefault(41)` (`:1083`).
- `Logic/Function/LC_FunctionHelper.lua:1245-1354` (`OpenFunctionPanelDefault`) looks in `FunctionSpecialFunc`
  (`:338-359`: Stall, Questionnaire, Bag, Personal, BlackMarket only) and `FunctionOpenPanelInfo` (`:360-1023`, no
  Feedback entry); with neither it only logs `缺少功能&界面映射: 41` ("missing function & panel mapping", `:1282-1297`).
- The SDK customer-service web page belongs to **Questionnaire** (function 77, "Support"):
  `LC_FunctionHelper.lua:120-131` → `Lua_SDK_WebViewMgr.OpenCustomService` (`SDK/Lua_SDK_WebViewMgr.lua:33-43`) →
  `MetaSDKUtil.OpenCustomService`, a publisher page that cannot reach an emulated server.
- `Logic/Report/Lua_ReportCustomDefine.lua` / `LC_ReportCustomHelper.lua` are SDK analytics events (TLOG/GPM:
  `button_click`, `window_toggle`, `chat_flow`...), not a player form. `Quality/Stability/QS_Stability_Feedback.lua`
  is an empty stub. The chat report panel (`UI/Chat/Controllers/UI_Sys_Chat_ReportWidgetCtrl.lua`) has a text box but
  `ClickSubmit` (`:430-491`) sends `reportDescription` (`:483`), which only `GetReportDescription` (`:295-307`) sets,
  and then to the text's length: the text itself never leaves the client.
- `enum_NormalActivities.Feedback` (999, `Lua_GameDefine.lua:12104`) is the questionnaire activity of the Three Days
  panel, unrelated.
- FunctionConfig 41 has open condition 2000037 (never met), so the original never showed the button.

## What the emulator adds

- **Client hook** (`FeedbackButton`, installed from `LuaPatches.Tick`): `FunctionSpecialFunc[41]` becomes a C#
  delegate that opens the client's generic text panel `UI_Com_InputContentPanel` (the stall's share-text panel,
  `UI_Sys_Stall_SellerWidgetCtrl.lua:966-990`, generic part `UI/Common/Controllers/UI_Com_InputContentWidgetCtrl.lua`)
  titled UIWords 90505 "Feedback". Its confirm callback sends the text as
  RO3Emu-private game message `PrivateMessageIds.Feedback` (UTF-8).
- **Server:** function 41 is in `OpenContent.Functions`, so the button shows wherever OpenContent is on (a
  hosted server, see [open-content.md](open-content.md)). The private message and chat command go to `FeedbackService.SubmitAsync`.
- An old client without the new hook still shows the button (the server opens 41) but the click does nothing; `/bug`
  works on every client. Players get the hook with the next patched client (`tools/patch-client.ps1`, or the launcher: [launcher.md](launcher.md)).

## What is stored

One JSON line per report in `<DataDir>/reports/feedback.jsonl` (append-only, survives restarts; not part of the
database backups):

| field | |
|---|---|
| `id` | 1, 2, 3... |
| `time` | server UTC time |
| `source` | `button` or `chat` |
| `account`, `roleId`, `roleName`, `level`, `career` | who |
| `scene`, `map`, `instance`, `line`, `x`, `y`, `z` | where (scene entry = MapConfig id; instance 0 = shared map) |
| `text`, `truncated` | the player's words (control characters removed, at most `Feedback:MaxLength` 1000) |
| `context` | up to 40 server log lines from the last 15 minutes that name the account or the role |

The context comes from an in-memory ring of the last 4096 log lines (`Diagnostics/RecentLogBuffer.cs`).
`Ro3Emu:Feedback:ContextLines=0` turns it off.

## Reading reports (operator)

Loopback only, on the server machine:

```bash
curl -s 'http://127.0.0.1:5380/ro3emu/reports?format=text'          # all, readable
curl -s 'http://127.0.0.1:5380/ro3emu/reports?since=12'             # JSON, reports after #12
curl -s 'http://127.0.0.1:5380/ro3emu/reports?since=2026-10-12T00:00:00Z&limit=50'
```

`since` is a report id (reports after it) or an ISO-8601 time (reports at or after it); `limit` 1-1000 (default 200),
oldest first. From another machine: `ssh <your server> curl -s ...`. The file itself is plain JSON lines too.

Each report also writes one log line: `Feedback #N from <account> (<role>) via <source> at scene ...`.

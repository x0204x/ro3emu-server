# Quest trace (controlled LAB)

This is a narrow, reviewable trace for three story quests only:

- 10102100 / story dungeon 1021301
- 10102200 / story dungeon 1022001, followed by field interaction 40022001 over message 31214
- 10103120 / story dungeon 1051001

The game gate switches to deflate + RC4 after its RSA handshake. A normal PCAP therefore proves TCP timing and byte
counts, but cannot by itself recover the post-handshake message ids or protobuf bodies. RO3Emu already observes the
plaintext message boundary after decryption and before dispatch in `TrafficLog`; use that boundary instead of saving
keys or credentials.

Start a private test server with traffic logging restricted to the one tester account. With the direct server executable:

```powershell
$env:Ro3Emu__TrafficLog = 'the-test-account'
# Start the server through the usual, already reviewed command.
```

The log is written to `<data>\logs\traffic-yyyyMMdd.jsonl`. It does not include the game-gate login token or RSA
handshake, but it can include other game messages belonging to that account. Keep the raw file private. Produce a
shareable, account-redacted quest-only trace with:

```powershell
& .\tools\QuestTrace\Export-QuestTrace.ps1 `
  -Path 'C:\RO3Emu\data\logs\traffic-20261029.jsonl' `
  -OutputPath '.\artifacts\quest-trace-20261029.jsonl'
```

The export keeps only opcodes 5027, 30005, 30108, 30400-30404 and 31214, then narrows their payloads to the selected
scenes, tasks and field interaction. Account names are replaced with `<redacted>`. It intentionally excludes chat,
login, inventory, economy and unrelated combat messages.

Expected acceptance chain:

1. 30400 enters the selected story dungeon; 30005/30108 completes its scene transition.
2. 30404 and 30401 publish each stage and the dungeon quest variables (10001 quest id, 10003 done/total).
3. 30403 reports a successful result. On the server, `DungeonService.Completed` runs before this notification and
   invokes `TaskService.OnDungeonClearedAsync`.
4. 5027 contains the cleared task as delivered and its automatic follow-up as running.
5. Tiger King only: after leaving the dungeon and returning to field 10220, 31214 carries taskId 40022001 and
   goalCount 1; the response result is 0, followed by 5027 delivering 10102210 and accepting 10102220.

External PCAP is optional corroboration only. Without the server-side session key and exact compression/RC4 state it
must be reported as `framing only / encrypted payload UNKNOWN`, never as a decoded quest trace.

# Skill animation trace

This diagnostic correlates each skill-presentation message sent by the server with the stages reached by a patched
player client. It identifies a missing route, handler or client timeline while reproducing skill animations; it does
not claim that an animation or VFX rendered successfully.

## Privacy and admission

The current investigation build enables the server side for every signed-in account by default. Player reporting is
still locally opt-in through the marker below. A report contains only a connection token, sequence and monotonic
time, skill message and numeric skill/source/target/session/correlation/show/effect identifiers, stage, success flag
and a bounded ASCII reason. It does not send account credentials, chat, IP addresses, character names, stack traces
or arbitrary packets. The server writes neither the account nor the connection token to the trace log.

Each connection receives a random, expiring token and a client-side rate budget. The server rejects unknown or
expired tokens, repeated sequence numbers, non-skill message identifiers, oversized payloads and reports above the
rate limit. The expected-event cache and writer queue are bounded so diagnostics cannot block the game loop.

## Current all-account default

The shipped server configuration currently contains:

```json
"SkillTraceTelemetry": {
  "Enabled": true,
  "AllowAllAccounts": true,
  "Accounts": [],
  "GrantSeconds": 3600,
  "EventsPerSecond": 20,
  "Burst": 40,
  "MaxPayloadBytes": 256
}
```

`AllowAllAccounts` is explicit: an empty `Accounts` list by itself never means everybody. Set it to `false` and list
specific account names if a later test should be limited.

After the animation investigation is complete, set `Enabled` to `false` and restart the server.

The patched hook is safe to install for every player, but reporting is still locally opt-in. Create an empty file at:

```text
<game folder>\ro3emu\skilltrace.on
```

Restart the game after adding or removing that marker. A server grant alone does not turn local tracing on.

## Read the evidence

The server appends bounded JSON Lines records to:

```text
<server data folder>\logs\skilltrace-yyyyMMdd.jsonl
```

`yyyyMMdd` uses UTC, matching the timestamp stored in each record.

Server records show the nine skill-presentation messages emitted to a traced connection. Client records show these
checkpoints:

- `RouteReceived`: the client hook received the message.
- `HandlerCompleted` / `HandlerFailed`: the original route handler returned or threw.
- `TimelineBuilt` / `TimelineFailed`: the client built or failed to build the presentation timeline.

The service assigns an `eventId` and records a correlation `status`:

- `matched`: this client checkpoint matches an expected server event, but more stages are still required.
- `complete`: every required route/handler/timeline stage for the event was observed.
- `unmatched`: the report used a valid connection grant but did not match a recent server event.
- `gap:*`: the expectation passed the 15-second correlation window, was reset, was evicted by the bounded cache, or
  remained during shutdown. Read `missingStages` to see the first absent client checkpoint.

This phase does not yet observe Unity animation state entry, projectile instantiation or hit-VFX rendering; add those
narrow hooks only after a real trace proves that the message handler and timeline both completed.

Start with one or two players even when all accounts are admitted, check log growth and server load, then expand.

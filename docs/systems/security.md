# Security model

What a RO3Emu server exposes, what it trusts, and what it does not protect against. Report a vulnerability as
[SECURITY.md](../../SECURITY.md) describes.

## Listeners

All of them bind to `Ro3Emu:BindAddress`. When it is empty they bind to loopback if `PublicHost` is a loopback
address (local play), and to every interface otherwise (`ServerOptions.ListenAddress`).

| Port (default) | Speaks | Who uses it |
|---|---|---|
| HTTP (5380) | plain HTTP (Kestrel) | the client's SDK login and server list, the launcher, the operator's tools |
| Micro gate (20000) | Phonest frames | the client's login services (role list, friends, mail, ranks...) |
| Game gate (33100) | Phonest frames | the game itself |
| Voice (off; `Ro3Emu:Voice:Port`) | the voice relay's own frames | team voice |
| Web panel (5381; `Ro3Emu:Panel`) | plain HTTP (Kestrel), its own pages and `/api/` only | players and admins in a browser; see [web panel](../guide/panel.md) for its sessions, CSRF, headers and the https proxy setup |

### HTTP endpoints

| Endpoint | Access | Notes |
|---|---|---|
| `POST /sdk/login` | public, rate limited | checks the password; the first login of an unknown name registers it |
| `GET` server list, `GET /ro3emu/gate-key.pem` | public | the gate's RSA public key |
| `GET /ro3emu/status` | public | the player count only, from the loop's census |
| `POST /ro3emu/account/delete-role` | public, rate limited | needs the account's password; refuses a role that is in the world |
| `POST /gift-card/use/v2` | public, rate limited | the client's gift-code redemption; needs the account's engine sign (the micro-gate login token); see [gift codes](gift-codes.md) |
| `/ro3emu/gm`, `/ro3emu/metrics*`, `/ro3emu/online`, `/ro3emu/reports`, `/ro3emu/shutdown` | operator only | loopback, operator token; 404 otherwise |

The operator tools (`OpsEndpoints`) answer a request only when all of this holds: the peer is loopback, the Host names
this machine (`127.0.0.1`, `localhost` or `[::1]`, so a page whose own name resolves to 127.0.0.1 is refused), no
`X-Forwarded-For`, `Forwarded` or `X-Real-IP` header (a proxy on the same machine is loopback too), and the
`X-Ro3Emu-Operator` header equals the data dir's `operator.token` (compared in constant time). Loopback alone was not
enough: a web page open in a browser on the host can send a simple POST to 127.0.0.1, but it cannot set that header
without a preflight the server never answers, and it cannot read the file. Anything else gets the 404 of an unknown
path. RO3Emu.exe, BotCli and the load test read the token from the data dir; `Ro3Emu__OperatorToken` sets it.

A reverse proxy on the same machine makes every request arrive from loopback. Do not forward the operator paths. The
web panel on loopback also answers its API only under this machine's name or its `PublicUrl`'s host.

## Accounts and credentials

- Passwords are stored as PBKDF2-SHA256 hashes, 100 000 rounds, with a per-account salt (`AccountPlans`).
- A credential check costs one such hash, so `CredentialLimiter` allows `LoginAttemptsPerMinute` (20) per client
  address (an IPv6 one by its /64 network) and answers 429 past that without hashing. The client address is the peer, or behind a proxy listed in
  `Ro3Emu:Panel:TrustedProxies` the last `X-Forwarded-For` entry, on the panel and on port 5380 alike. Loopback is
  exempt only for local tools: not for a request with `X-Forwarded-For`, `Forwarded` or `X-Real-IP`, not on a
  server others reach (a public `PublicHost` or bind, `TrustedProxies` or `Ro3Emu:Panel:PublicUrl` set), and not with
  `LoginLimitIncludesLoopback`. Behind a proxy
  that is not listed every player shares the proxy's address, so list it.
- `/sdk/login` returns a token. The client presents it to `LoginService.verifySdk` on the micro gate, which binds
  the account to that connection.
- A banned account cannot sign in. The web panel bans through the server, which closes the account's game session at
  once; the server reads the `account_ban` table on every password, token and gate-bind check, and a ban is refused
  like a wrong credential. See [account bans](account-bans.md).
- The client sends the password over the HTTP port. That is plain HTTP unless the client was patched with an
  `https://` server address and a TLS-terminating proxy stands in front of the server. Tell players not to reuse a
  password they use elsewhere.

## The gates

Both gates use the client's own channel protocol:

1. RSA-OAEP key exchange against `gate_rsa_private.pem`.
2. zlib compression.
3. An RC4 stream.

RC4 is what the client speaks and cannot be changed. Treat the channel as protection against casual sniffing, not
as strong confidentiality.

Every client is untrusted:

- **Frames:** a frame larger than 1 MiB closes the connection (`PhonestChannel.MaxFrame`).
- **msgpack payloads** (micro arguments, game-gate RPC arguments, login tokens): read with
  `MicroServiceRouter.Untrusted`, which uses collision-resistant hashing and a maximum depth of 32.
- **Before sign-in, the micro gate** accepts only `LoginService.verifySdk` and `PhonestGate.bind`, the two calls
  the client makes then. Anything else gets `"not verified"` before its arguments are read (`MicroServiceRouter`).
- **The game gate goes by session stage** (`SessionStage`):
  - *connected:* only the heartbeat;
  - *role screen:* adds role creation, role selection and the SDK info report;
  - *world:* everything else.

  `GameMessageHandler` drops a message sent before its stage without an answer. It logs the first one per
  connection. `DispatcherAdmissionTests` pins the full table.
- **Every request** is validated against the client's configuration and refused with an `ErrorCodes` value.
  Game state changes only on the game loop.

The web panel's admin area has its own staff roles (Moderator, GM, Admin: `Ro3Emu:Panel:Roles`, `GmAccounts` as
Admin, or given on the panel by an Admin); every API route checks its role. GM and Admin accounts must use two-factor
authentication when the panel listens on a public address (`Ro3Emu:Panel:RequireAdmin2FA`). See
[web panel](../guide/panel.md#staff-roles).

GM commands (`!` chat lines, the GM panel, 1031/1034) are limited to `Ro3Emu:GmAccounts`. An empty list means
everyone only on a local server (loopback only, no proxy, not Production), and nobody, in game or as a panel Admin, on
a server other machines reach. See [GM access](../guide/gm-access.md), and set the list on any public server.

## Data on disk

`DataDir` holds:

- `ro3emu.db`: accounts, password hashes and every role, and the panel's two-factor secrets;
- `gate_rsa_private.pem`: the gate key;
- the logs. With `Ro3Emu:TrafficLog` on, these include the game traffic of the listed accounts.

Anyone who can read the folder can impersonate the server to patched clients and attack the password hashes offline.
Keep it readable by the server's account only, and do not share it or attach it to bug reports.

## Out of scope

- **Denial of service by a determined attacker.** The limits above stop accidents and cheap abuse. Volumetric
  attacks need protection in front of the server.
- **A modified client** sending requests that pass validation. The server enforces the original's rules and the checks
  in [anti-cheat](anticheat.md) (walk and flight budgets, message rates, quest reports, chat limits); a well-made bot on
  the unmodified client sends what a player sends.
- **The client's own security.** The patcher changes only what `ClientPatcher` lists.

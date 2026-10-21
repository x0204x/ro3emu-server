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

### HTTP endpoints

| Endpoint | Access | Notes |
|---|---|---|
| `POST /sdk/login` | public, rate limited | checks the password; the first login of an unknown name registers it |
| `GET` server list, `GET /ro3emu/gate-key.pem` | public | the gate's RSA public key |
| `GET /ro3emu/status` | public | the player count only, from the loop's census |
| `POST /ro3emu/account/delete-role` | public, rate limited | needs the account's password; refuses a role that is in the world |
| `/ro3emu/gm`, `/ro3emu/metrics*`, `/ro3emu/online`, `/ro3emu/reports`, `/ro3emu/shutdown` | loopback only | answer 404 to any other address |

A reverse proxy on the same machine makes every request arrive from loopback. Do not forward the loopback-only paths.

## Accounts and credentials

- Passwords are stored as PBKDF2-SHA256 hashes, 100 000 rounds, with a per-account salt (`AccountPlans`).
- A credential check costs one such hash, so `CredentialLimiter` allows `LoginAttemptsPerMinute` (20) per remote
  address and answers 429 past that without hashing. Loopback is exempt unless `LoginLimitIncludesLoopback` is
  set. Behind a reverse proxy every player shares the proxy's address, so raise the limit there.
- `/sdk/login` returns a token. The client presents it to `LoginService.verifySdk` on the micro gate, which binds
  the account to that connection.
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

GM commands (`!` chat lines, the GM panel, 1031/1034) are limited to `Ro3Emu:GmAccounts`. An empty list means
everyone outside Production. See [GM access](../guide/gm-access.md), and set the list on any public server.

## Data on disk

`DataDir` holds:

- `ro3emu.db`: accounts, password hashes and every role;
- `gate_rsa_private.pem`: the gate key;
- the logs. With `Ro3Emu:TrafficLog` on, these include the game traffic of the listed accounts.

Anyone who can read the folder can impersonate the server to patched clients and attack the password hashes offline.
Keep it readable by the server's account only, and do not share it or attach it to bug reports.

## Out of scope

- **Denial of service by a determined attacker.** The limits above stop accidents and cheap abuse. Volumetric
  attacks need protection in front of the server.
- **A modified client** sending requests that pass validation. The server enforces the original's rules, not
  anti-cheat.
- **The client's own security.** The patcher changes only what `ClientPatcher` lists.

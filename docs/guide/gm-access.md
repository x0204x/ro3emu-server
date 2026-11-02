# Who has GM (`Ro3Emu:GmAccounts`)

GM commands are the `!` lines in chat (`!help`), the client's GM panel (30300) and the GM transmit/inspect messages
(1031/1034). One check decides all of them: `GmCommands.Allowed`.

| `GmAccounts` | `GmOpenWhenEmpty` | server | in game | panel Admin |
|---|---|---|---|---|
| listed accounts | any | any | only those accounts, spelled as stored (an account name is one account in any letter case; the list matches its stored spelling) | the same accounts |
| empty | unset | local only, not started as Production | everyone (tests, `tools/run-server.ps1`, local play) | everyone |
| empty | unset | reachable from other machines | **nobody** | **nobody** |
| empty | unset | started as **Production** | **nobody** | **nobody** |
| empty | `true` | local only | everyone | everyone |
| empty | `true` | reachable from other machines | everyone (an explicit choice, logged loudly) | **nobody** |
| empty | `false` | any | nobody | nobody |

"Local only" means only this machine reaches the server: the client is told a loopback address (`PublicHost` is
`127.0.0.1`), the game ports listen on loopback (`BindAddress` is empty or loopback), the [web panel](panel.md) listens
on loopback too, and no proxy serves it (`Ro3Emu:Panel:PublicUrl` and `Ro3Emu:Panel:TrustedProxies` are unset). An address
that is not an IP address counts as reachable, so a typo never opens GM. Everything else is "reachable from other
machines": a LAN or public address, `0.0.0.0`, a tunnel (a public `PublicHost` with a loopback bind), or a panel behind a
reverse proxy.

**List your GM accounts before you open a tunnel or a proxy.** The server can only judge what it is told. A tunnel
(frp, `ssh -R`, nginx `stream`) or a proxy that adds no forwarding header delivers strangers to loopback, and with
`PublicHost=127.0.0.1` and no panel options that cannot be told from local play, so an empty list stays open. A panel
request that carries `X-Forwarded-For`, `Forwarded` or `X-Real-IP` never gets that open Admin, whatever else is set.

"Started as Production" means the environment was set explicitly: `ASPNETCORE_ENVIRONMENT=Production`,
`DOTNET_ENVIRONMENT=Production` or `--environment Production`. ASP.NET's own default when nothing is set is also
called Production, but it does not count, so tests and local runs keep "empty = everyone".

`RO3Emu.exe` follows the same table without settings of its own: local play starts a local, Development server
(everyone is GM), and hosting for friends starts a Production server on the LAN or public address with the GM accounts
you give it.

At startup the server logs who has GM. With an empty list it warns loudly in both cases that matter:

- Nobody has GM: `!!! Ro3Emu:GmAccounts is empty, so NO account has GM commands in game (environment ..., reachable from other machines on ...)`.
- GM open to everyone on a server other machines reach (only with `GmOpenWhenEmpty=true`): `!!! Ro3Emu:GmAccounts is empty and GM is open: EVERY account can use GM commands in game ...`

The [web panel](panel.md)'s admin area gives Admin to the listed accounts. With an empty list it gives Admin to every
account only on a local panel with GM open, never on a panel that other machines reach: there anyone who signs up
would own the server. The startup log says who the configured Admins are (`Web panel Admins: ...`), and warns when
there is none. Its GM console runs a command for any character in the game: the admin's rights count there, not those
of the character's account.

## Configuration

Like every `Ro3Emu:` option, the list can be set in `appsettings.json`:

```json
{
  "Ro3Emu": {
    "GmAccounts": [ "your-admin-login" ]
  }
}
```

or through environment variables, one per account: `Ro3Emu__GmAccounts__0=your-admin-login`,
`Ro3Emu__GmAccounts__1=another-login`, and so on. The value is the game login (account name), not a character name.

## The loopback tool

`POST http://127.0.0.1:5380/ro3emu/gm?account=<account>` (body `!warp 10111 150 190`) answers only a local tool and
keeps its own rule: with an empty list it runs for any online account, with a list only for listed accounts.
Production does not close it; it is the operator's tool on the server machine.

The request needs the operator token from the data dir, in the `X-Ro3Emu-Operator` header; without it, from another
address, under another Host than `127.0.0.1`, `localhost` or `[::1]`, or through a proxy, the answer is 404. That keeps
a web page open in a browser on the server machine from running GM commands, since a page cannot read the file:

```bash
curl -s -H "X-Ro3Emu-Operator: $(cat data/operator.token)" -d "!warp 10111 150 190" \
  "http://127.0.0.1:5380/ro3emu/gm?account=<account>"
```

The server writes `operator.token` (64 hex characters, readable only by its user) into its data dir at the first start
and keeps it. Set `Ro3Emu__OperatorToken` (at least 32 characters) to choose it yourself; it is written to the same file.

## A public server

On a server that other people can reach, start the host as Production and list the GM accounts. With systemd, a
drop-in such as `/etc/systemd/system/ro3emu.service.d/gm.conf` (adjust the unit name to yours) needs both lines:

```ini
[Service]
Environment=ASPNETCORE_ENVIRONMENT=Production
Environment=Ro3Emu__GmAccounts__0=your-admin-login
```

More GMs: `Ro3Emu__GmAccounts__1=...`. Passwords never go in the unit, the repository or these docs. After
`systemctl daemon-reload` and a restart, the log shows `GM accounts: your-admin-login`.

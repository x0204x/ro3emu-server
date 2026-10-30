# Account bans

A banned account cannot sign in. Bans live in one table of the server's database. The server's own
[web panel](../guide/panel.md) bans and unbans through the server, which also disconnects the player at once; an
external tool may still write the table from its own process. The whole feature is **(inferred)**: the client has no
ban message that we know of, so the server refuses a banned account the same way it refuses a wrong credential.

## The table

The server creates it at startup (`AccountBanSchemaPlan` in `DatabaseSchema`):

```sql
CREATE TABLE IF NOT EXISTS account_ban (
    account TEXT NOT NULL PRIMARY KEY,
    reason TEXT NOT NULL DEFAULT '',
    banned_utc INTEGER NOT NULL,
    until_utc INTEGER,
    banned_by TEXT);
```

| Column | Meaning |
|---|---|
| `account` | the account's stored name, `account.name`. The match is exact, with no collation, like `account.name` itself |
| `reason` | free text for the operator; the panel shows it to admins, the game never |
| `banned_utc` | when the ban was made, in unix seconds |
| `until_utc` | when the ban ends, in unix seconds; `NULL` means it does not end |
| `banned_by` | the admin who made the ban |

A ban is active while its row exists and `until_utc` is `NULL` or later than now. An expired row blocks nothing and
can stay. Unban means delete the row.

## Writers

**The web panel** (Accounts page, admins only) goes through `AccountStore`'s writer: `AccountBanWritePlan`
(insert-or-replace, only for an existing account) and `AccountUnbanPlan` (deletes an active ban). Each one writes its
`admin_audit` row in the same transaction. After the ban commits, the server closes every game session of the account
on the game loop (`ReconnectService.KickNow`): a connected client gets `BE_KICKED` and its role is logged out without
a reconnect grace, a role already waiting in its grace is logged out, and the reconnect ticket is revoked. The
account's panel sessions end too.

**An external tool** may write the table with its own SQLite connection to `ro3emu.db` (insert-or-replace to ban,
delete to unban), as the older ro3-cpanel did. The server reads the table again on every check, so such a ban takes
effect on the next sign-in without a restart, but nobody is disconnected and nothing is audited. Such a tool must not
create the table: when it is missing, the server is too old to enforce bans.

A ban outlives its account: deleting an account (for example with an external tool) keeps its `account_ban` row, so the
player cannot escape a ban by recreating the account. `/sdk/login` registers unknown names, but a name with an active
ban is refused before it is registered, so no new account is created for it. That check ignores case, as the account
lookup and the registration's uniqueness do: otherwise `bob` could be registered while `Bob` is banned, and `Bob` would
then sign in to it. For an existing account the ban is matched exactly against its stored name.

## Where the server checks

Every path that turns a credential into a signed-in account goes through `AccountStore`
(`AccountStore.Bans.cs`):

| Path | Check | Answer to a banned account |
|---|---|---|
| `POST /sdk/login` | `SignInAsync`, after the password matched | `{code:401}`, the wrong-password answer |
| micro `LoginService.verifySdk` | the SDK ticket (`ResolveSdkTokenAsync`), also one issued before the ban | `"invalid ticket"` |
| game gate `connectServer` | the engine sign (`ResolveEngineSign`), also one issued before the ban | `ConnectRefused` |
| micro `PhonestGate.bind` | the gate secret's account (`UnlessBannedAsync`) | `"bind refused"` |
| `POST /ro3emu/account/delete-role` | `VerifiedAccountAsync` | `{code:401}`, the wrong-password answer |
| web panel login, and every panel request of a signed-in account | `VerifiedAccountAsync`, `IsBannedAsync` | the wrong-password answer; an open panel session ends |
| web panel sign-up | `RegisterAsync`, the same check as the first login | "This account name is taken." |

`/sdk/login` checks the password first. A wrong password gets the same answer whether or not the account is
banned, so the answer does not reveal a ban. Any later endpoint that checks a password or a token through these
methods is covered too.

The server log line of each refusal says `Account <name> is banned: <path> refused`.

A ban made through the web panel kicks a player who is in the world. A row written by an external tool does not: that
player stays until they log out, and a game-gate reconnection of that session (`ReconnectService`) is not checked.

## Tests

`AccountBanTests` run a server on the invented fixture and insert ban rows through a separate `SqliteConnection`,
as an external tool does. `PanelTests.ABanKicksTheOnlinePlayerBlocksEveryLoginAndIsAudited` bans through the web
panel: the player in the world is disconnected, every login is refused, and both actions are in the audit log. They cover the refused login and pre-ban ticket, expiry, unban, an unaffected account, a row seen
without a restart, the wrong-password answer, and the engine sign, password check, delete-role and gate bind paths.

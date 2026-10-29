# Account bans

A banned account cannot sign in. The admin panel (ro3-cpanel) bans and unbans by writing one table in the server's
database; the server only reads it. The whole feature is **(inferred)**: the client has no ban message that we know
of, so the server refuses a banned account the same way it refuses a wrong credential.

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
| `reason` | free text for the operator; the server does not read it |
| `banned_utc` | when the ban was made, in unix seconds |
| `until_utc` | when the ban ends, in unix seconds; `NULL` means it does not end |
| `banned_by` | the admin who made the ban; the server does not read it |

A ban is active while its row exists and `until_utc` is `NULL` or later than now. An expired row blocks nothing and
can stay. Unban means delete the row.

The panel is the only writer. It writes with its own SQLite connection to `ro3emu.db` (insert-or-replace to ban,
delete to unban). The server never writes the table, and it reads the table again on every check, so a ban or unban
takes effect on the next sign-in without a restart. The panel must not create the table: when it is missing, the
server is too old to enforce bans.

A ban outlives its account: deleting an account (for example from the panel) keeps its `account_ban` row, so the
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

`/sdk/login` checks the password first. A wrong password gets the same answer whether or not the account is
banned, so the answer does not reveal a ban. Any later endpoint that checks a password or a token through these
methods is covered too.

The server log line of each refusal says `Account <name> is banned: <path> refused`.

A player who is already in the world when the ban is made is not kicked. A game-gate reconnection of that session
(`ReconnectService`) is not checked either.

## Tests

`AccountBanTests` run a server on the invented fixture and insert ban rows through a separate `SqliteConnection`,
as the panel does. They cover the refused login and pre-ban ticket, expiry, unban, an unaffected account, a row seen
without a restart, the wrong-password answer, and the engine sign, password check, delete-role and gate bind paths.

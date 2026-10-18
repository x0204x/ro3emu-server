# Durable activity settlement (payout obligations)

An activity that ends with rewards for several roles (a match, a boss kill, a raid) must never lose a payout and
never pay one twice, whatever fails in between: a refused commit, a disconnect, a restart. The server does this with
**payout obligations**: one row per (settlement, role), saved before anyone is told the result and paid on its own
afterwards. `Game/RoleObligations.cs` owns the rules; `Accounts/Persistence/RoleObligationPlans.cs` holds the shared
plans.

## The rules

1. **Persist, then announce.** The settlement's rows are saved in one transaction (`RoleObligations.FreezeAsync`, or
   `RoleObligations<T>.CreatePlan` added to a larger settlement commit) before the settlement counts as done and before
   any result notice. A refused save leaves nothing durable: keep the settlement in memory and try again later.
   `FreezeAsync` adopts a save whose acknowledgement was lost (the stored rows of the scope are exactly these).
2. **Pay each row once.** A row is paid by the activity's pay callback in **one** commit holding the goods, the role
   state it changes, the activity's receipt (its idempotent source is the row's `source_key`) and
   `RoleObligations<T>.DeliverPlan(row)`. The deliver plan refuses unless the row is still undelivered, so a second
   payment can never commit. Pay under `CharacterService.Lock(session, [RoleObligations<T>.Key(source)])`.
3. **Retry.** With `ObligationRetry.TickWithBackoff` a failed payment stays pending and `Tick()` (call it from the
   service's tick) retries it after 5, 10, 20 ... 300 s while the role is online. `NextRecovery` (MVP) leaves it to
   the activity's next delivery pass and the next login.
4. **Deliver on login.** Add `LoginKeysAsync(role)` to `RoleSystems.LoginKeysAsync` (the role select reserves the
   pending rows) and call `DeliverPendingAsync(session, role, current)` from the activity's login step.
5. **Account for it.** The table is classified by the economy snapshot reader.

## Adding an activity

1. A table with `source_key TEXT PRIMARY KEY`, a role column, the frozen columns (keep the payout as data: grants,
   a JSON result, pool ids) and `delivered INTEGER NOT NULL DEFAULT 0 CHECK(delivered IN (0,1))`, an index on
   `(role, delivered)` and the `owner_revision` seed, in a schema plan listed in `DatabaseSchema`
   (`CasualBattleSchemaPlan` is the smallest example).
2. A row record implementing `IRoleObligationRow<TRow>`: its `RoleObligationTable` (table, revision kind, role column,
   columns, pending order, labels, optional scope column such as `match_id`), `Read`, `Values`, `Detach`, `SameFrozen`.
3. A `RoleObligations<TRow>` in the service, with the pay callback (rule 2), wired to its tick and login (rules 3-4).
4. The economy reader: the table's columns in `EconomySnapshotReader.KnownColumns`, a reader for its unpaid rows
   (frozen goods as `Pending`, an unrolled reward as a `RecoveryDebt`, see `ReadCasualBattle`) and, when a JSON
   payload type is new, its entry in the format manifest (`S3EconomyTests.Persisted_type_surface` prints the new hash).
   `RoleObligationsTests.EveryObligationTableMatchesItsSchemaAndIsKnownToTheEconomyReader` fails until the table and
   its columns are known.

## Activities on it

| Activity | Table | Retry | Notes |
|---|---|---|---|
| Mock Battle | `casual_battle_obligation` | tick with backoff | rows saved alone (`FreezeAsync`); pools rolled at payment |
| MVP | `mvp_award_obligation` (revision kind `mvp_obligation`) | next kill / login | rows saved with the kill's settlement (`mvp_settlement`, counters, realm receipt); goods rolled at the kill |
| Survivor, Dragon Island, Rift | `timed_battle_obligation` | login | shared plans and login reservation; delivery stays in `TimedBattleService` (role + row scope right after the settlement commit, again at login) |
| Dungeons | `dungeon_reward_obligation` | own | not on the shared component: two-phase rows (prepared, then activated by the terminal kill) with their own ledger earn rows (`DungeonObligationLedger`) |

using System.Globalization;
using System.Text.Json;
using Google.Protobuf;
using Romsg;
using Ro3Emu.Server.Accounts;
using Ro3Emu.Server.Accounts.Persistence;
using Ro3Emu.Server.Economy;
using Ro3Emu.Server.Data;
using Ro3Emu.Server.Game.Ownership;

namespace Ro3Emu.Server.Game;

public sealed partial class TimedBattleService
{
    /// <summary>The frozen grants of a settlement (timed_battle_obligation) on the shared obligation plans. Delivery
    /// stays this service's own: each row is paid under its role and row reservation right after the settlement
    /// commit, and at login (the role select reserves the pending rows, <see cref="LoginKeysAsync"/>).</summary>
    private static class Obligations
    {
        internal static ReservationKey Key(string source) => RoleObligations<TimedBattleObligationRow>.Key(source);
        internal static DbPlan CreatePlan(IEnumerable<TimedBattleObligationRow> rows) =>
            RoleObligations<TimedBattleObligationRow>.CreatePlan(rows);
        internal static DbPlan DeliverPlan(TimedBattleObligationRow row) => RoleObligations<TimedBattleObligationRow>.DeliverPlan(row);
        internal static Task<List<TimedBattleObligationRow>> PendingAsync(AccountStore accounts, long role) =>
            RoleObligations<TimedBattleObligationRow>.PendingAsync(accounts, role);
    }

    /// <summary>C/D 15: the role's undelivered settlement grants, read before the role select admits so it can reserve
    /// them for <see cref="DeliverPendingAsync"/>.</summary>
    internal Task<ReservationKey[]> LoginKeysAsync(long roleId) =>
        RoleObligations<TimedBattleObligationRow>.LoginKeysAsync(_accounts, roleId);

    private sealed record SettlementCost(int Entry, int Count);
    private sealed record FrozenRecipient(GameSession Player, State Next, RoleProgress? PaidProgress,
        TimedBattleObligationRow Obligation, CharacterService.FirstCardSightPlan? TaskPlan);

    /// <summary>Freeze every admitted result and its payment before any participant sees terminal success.</summary>
    private static ReservationKey[] SettlementKeys(Match match, long matchId) =>
        new[] { ReservationKey.Entity("timed_match", matchId.ToString(CultureInfo.InvariantCulture)) }
            .Concat(match.Participants.Values.Select(participant => ReservationKey.Role(participant.Id)))
            .Concat(match.Participants.Values.Select(participant => Obligations.Key(
                $"timed_battle/{matchId}/{participant.Id}"))).Distinct().ToArray();

    private async Task SettleMatchCoreAsync(Match match)
    {
        if (match.Settled || match.Room == null) return;
        FrozenRecipient[] frozen;
        var matchId = match.Room.Id;
        var keys = SettlementKeys(match, matchId);
        using (var scope = _loop.Owners is { } owners
            ? await owners.EnterAsync("timed battle settlement", keys) : null)
        {
            if (match.Settled || match.Room == null) return;
            var captured = match.Players.Select(player => (Player: player, Original: match.Participants[player])).ToArray();
            var eligible = new HashSet<GameSession>(ReferenceEqualityComparer.Instance);
            foreach (var target in captured)
            {
                var player = target.Player;
                var owner = target.Original.Owner;
                bool current = !match.Absent.Contains(player) && player.ActivitiesReady && player.TimedBattle != null
                    && ReferenceEquals(player.Role, target.Original.Role)
                    && ReferenceEquals(player.Owner, owner) && ReferenceEquals(player.Attachment, target.Original.Lease);
                if (current && player.Owners != null)
                    current = owner != null && target.Original.Lease != null && owner.RoleId == target.Original.Id
                        && owner.LifecycleEpoch == target.Original.Epoch && ReferenceEquals(owner.Attachment, target.Original.Lease)
                        && owner.State is RoleOwnerState.Attached or RoleOwnerState.Quiescing
                        && scope!.Context.CanPin(owner) && !target.Original.Lease.Cancelled;
                if (!current)
                {
                    match.Absent.Add(player);
                    match.Fallen.Add(player);
                    player.EventFaction = 0;
                    _active.Remove(player);
                    continue;
                }
                if (owner != null) scope?.Context.Pin(owner);
                eligible.Add(player);
            }
            if (AfterSettlementAdmissionForTest is { } admitted) await admitted();
            var survivors = RemainingFactions(match);
            int winnerFaction = survivors.Count == 1 ? survivors.Single() : 0;
            if (match.Chicken != null)
            {
                // Survivor Battle: the last team standing, or at the end of the battle stage the best ranked standing
                // team (ChickenRanks, (inferred) tie-break).
                if (winnerFaction == 0 && survivors.Count > 1 && _time.GetUtcNow().ToUnixTimeSeconds() >= match.Deadline)
                    winnerFaction = ChickenRanks(match).Where(r => survivors.Contains(r.Key)).MinBy(r => r.Value).Key;
            }
            else if (match.Dragon != null)
            {
                // Dragon Coffin Island: the camp with the higher score when the countdown ends (UITips 290002); a camp
                // that left entirely forfeits before it (inferred).
                if (winnerFaction == 0 && _time.GetUtcNow().ToUnixTimeSeconds() >= match.Deadline) winnerFaction = DragonLeader(match);
            }
            else if (winnerFaction == 0 && _time.GetUtcNow().ToUnixTimeSeconds() >= match.Deadline)
            {
                // (inferred) Unique confirmed-kill lead wins a timed-out round.
                var ranked = match.Players.GroupBy(p => match.Faction[p]).Where(team => survivors.Contains(team.Key))
                    .Select(team => (Faction: team.Key, Kills: team.Sum(p => match.Kills.GetValueOrDefault(p))))
                    .OrderByDescending(team => team.Kills).ToArray();
                if (ranked.Length > 0 && ranked[0].Kills > 0 && (ranked.Length == 1 || ranked[0].Kills > ranked[1].Kills))
                    winnerFaction = ranked[0].Faction;
            }
            var winner = winnerFaction == 0 ? null : match.Players
                .Where(p => eligible.Contains(p) && match.Faction[p] == winnerFaction)
                .OrderByDescending(p => match.Dragon?.Score.GetValueOrDefault(p) ?? 0)
                .ThenByDescending(p => match.Kills.GetValueOrDefault(p)).ThenBy(p => match.Participants[p].Id)
                .FirstOrDefault();
            bool actualVictory = winner != null;
            var staged = new List<FrozenRecipient>();
            foreach (var player in match.Players.Where(eligible.Contains))
            {
                var original = match.Participants[player];
                bool won = actualVictory && match.Faction[player] == winnerFaction;
                var state = player.TimedBattle!;
                if (match.Entry == ChickenEntry && state.ChickenSettled.Contains(match.Open)
                    || match.Entry == DragonEntry && state.DragonSettled.Contains(match.Open)) continue;
                var next = JsonSerializer.Deserialize<State>(JsonSerializer.SerializeToUtf8Bytes(state))!;
                var grants = new List<AwardGrant>();
                var costs = new List<SettlementCost>();
                RoleProgress? paid = null;
                byte[] result;
                if (match.Entry == ChickenEntry)
                {
                    next.ChickenSettled.Add(match.Open);
                    next.ChickenMatches++;
                    var level = _data.Row("EatChickenLevelConfig", state.ChickenRank);
                    long limit = level is { } rank ? GameData.Int(rank, "_iPointLimit") : 100;
                    long compensate = level is { } loss ? GameData.Int(loss, "_iCompensate") : 10;
                    long floor = ChickenRankFloor(state.ChickenRank);
                    long oldTotal = state.ChickenScore < floor ? floor + state.ChickenScore : state.ChickenScore;
                    next.ChickenScore = Math.Max(floor, oldTotal + (won ? limit : actualVictory ? -compensate : 0));
                    if (limit > 0 && next.ChickenScore - floor >= limit
                        && _data.Row("EatChickenLevelConfig", next.ChickenRank + 1) != null) next.ChickenRank++;
                    long walletDelta = next.ChickenScore - _character.CountOf(player, 7);
                    if (walletDelta > 0) grants.Add(new AwardGrant(7, checked((int)walletDelta)));
                    if (walletDelta < 0) costs.Add(new SettlementCost(7, checked((int)-walletDelta)));
                    result = ChickenSnapshot(match, player, actualVictory, new ChickenScoreChange(next.ChickenScore - oldTotal,
                        oldTotal, state.ChickenRank, next.ChickenRank)).ToByteArray();
                }
                else
                {
                    int oldRank = state.DragonRank;
                    long oldScore = state.DragonScore;
                    var level = _data.Row("DragonIslandLevelConfig", oldRank);
                    long limit = level is { } rank ? GameData.Int(rank, "_iPointLimit") : 0;
                    long compensate = level is { } loss ? GameData.Int(loss, "_iCompensate") : 0;
                    long delta = won ? limit : actualVictory ? -compensate : 0;
                    next.DragonSettled.Add(match.Open);
                    next.DragonScore = Math.Max(0, next.DragonScore + delta);
                    next.DragonRankScore = Math.Max(0, next.DragonRankScore + delta);
                    if (limit > 0 && next.DragonRankScore >= limit
                        && _data.Row("DragonIslandLevelConfig", next.DragonRank + 1) != null)
                    { next.DragonRankScore -= limit; next.DragonRank++; }
                    delta = next.DragonScore - oldScore;
                    long today = TreasureDay();
                    if (next.DragonCurrencyDay != today) { next.DragonCurrencyDay = today; next.DragonCurrencyEarned = 0; }
                    int cap = (int)Math.Max(0, Math.Min(PublicValue("DragonIslandCurrencyLimit"),
                        level is { } capRow ? GameData.Int(capRow, "_iSettleCurrencyLimit") : int.MaxValue));
                    int currency = actualVictory ? (int)PublicValue(won
                        ? "DragonIslandCurrencyBaseWin" : "DragonIslandCurrencyBaseLose") : 0;
                    currency = Math.Clamp(currency, 0, Math.Max(0, cap - next.DragonCurrencyEarned));
                    next.DragonCurrencyEarned += currency;
                    if (currency > 0) grants.Add(new AwardGrant(6, currency));
                    if (delta > 0) grants.Add(new AwardGrant(17, checked((int)delta)));
                    if (delta < 0) costs.Add(new SettlementCost(17, checked((int)-delta)));
                    // Preserve the established wallet-mismatch refusal without publishing an unpaid rank result.
                    if (costs.Any(cost => _character.CountOf(player, cost.Entry) < cost.Count))
                    {
                        _log.LogWarning("Dragon settlement wallet mismatch for role {Role}", original.Id);
                        next = JsonSerializer.Deserialize<State>(JsonSerializer.SerializeToUtf8Bytes(state))!;
                        next.DragonSettled.Add(match.Open);
                        grants.Clear(); costs.Clear(); currency = 0; delta = 0;
                    }
                    result = new DragonIslandSettleNtf
                    {
                        ResultType = won ? 1 : 2, OldRankId = oldRank, NewRankId = next.DragonRank,
                        OldNetScore = oldScore, NewNetScore = next.DragonScore, NetScoreDelta = delta,
                        CurrencyEarned = currency, Snapshot = DragonSnapshot(match, winner, actualVictory,
                            _time.GetUtcNow().ToUnixTimeMilliseconds()),
                    }.ToByteArray();
                }
                if (costs.Count > 0)
                {
                    paid = CopyProgress(player.Progress!);
                    foreach (var cost in costs)
                    {
                        if (paid.Currency.GetValueOrDefault(cost.Entry) < cost.Count)
                            throw new InvalidOperationException("Timed battle cost changed before settlement staging.");
                        paid.Currency[cost.Entry] -= cost.Count;
                    }
                }
                string source = $"timed_battle/{match.Room.Id}/{original.Id}";
                staged.Add(new FrozenRecipient(player, next, paid,
                    new TimedBattleObligationRow(source, match.Room.Id, match.Entry, original.Id,
                        JsonSerializer.SerializeToUtf8Bytes(grants), JsonSerializer.SerializeToUtf8Bytes(costs),
                        JsonSerializer.SerializeToUtf8Bytes(next), result),
                    _tasks.StageTimedBattleSettlement(player, match.Entry, source)));
            }
            frozen = staged.ToArray();
            var tx = _roles.NewTransaction();
            foreach (var item in frozen)
            {
                tx.RoleBlob(item.Obligation.RoleId, "timed_battle", item.Obligation.NextState);
                if (item.PaidProgress != null) tx.Progress(item.Obligation.RoleId, item.PaidProgress);
                item.TaskPlan?.Persist(tx);
            }
            tx.Add(Obligations.CreatePlan(frozen.Select(item => item.Obligation)));
            var catalog = EventEconomy.Catalog(_data);
            EconomyAsset Asset(int entry) => catalog.TryAsset(entry, out var asset) ? asset
                : throw new InvalidDataException($"Timed battle item {entry} is not catalogued.");
            var goods = frozen.SelectMany(item => JsonSerializer.Deserialize<AwardGrant[]>(item.Obligation.Grants)!
                .Where(grant => grant.Entry is not (100 or 101))
                .Select(grant => new EconomyQuantity(Asset(grant.Entry), grant.Count))).ToArray();
            var debits = frozen.SelectMany(item => JsonSerializer.Deserialize<SettlementCost[]>(item.Obligation.Costs)!
                .Select(cost => new EconomyQuantity(Asset(cost.Entry), cost.Count))).ToArray();
            if (goods.Length > 0 || debits.Length > 0)
            {
                var deltas = frozen.SelectMany(item =>
                {
                    var owner = new EconomyOwner(EconomyOwnerKind.Role,
                        item.Obligation.RoleId.ToString(CultureInfo.InvariantCulture));
                    return JsonSerializer.Deserialize<AwardGrant[]>(item.Obligation.Grants)!
                        .Where(grant => grant.Entry is not (100 or 101))
                        .Select(grant => new EventEconomyOwnerDelta(owner, Asset(grant.Entry), grant.Count))
                        .Concat(JsonSerializer.Deserialize<SettlementCost[]>(item.Obligation.Costs)!
                            .Select(cost => new EventEconomyOwnerDelta(owner, Asset(cost.Entry), -cost.Count)));
                }).ToArray();
                tx.WithEventEconomy(new EventEconomyMutation(catalog, Guid.NewGuid().ToString("N"),
                    "realm", 0, "event/timed-battle-settle", $"timed-battle/match/{match.Room.Id}",
                    DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), debits, goods, deltas,
                    JsonSerializer.SerializeToUtf8Bytes(new { Match = match.Room.Id, Recipients = frozen.Select(x => x.Obligation.RoleId).ToArray() })));
            }
            await _roles.CommitAsync(tx, "timed-battle/settlement", _ =>
            {
                foreach (var item in frozen)
                {
                    item.Player.TimedBattle = item.Next;
                    if (item.PaidProgress != null) item.Player.Progress = item.PaidProgress;
                    item.TaskPlan?.Apply();
                    _pendingDeliveryRoles.Add(item.Obligation.RoleId);
                }
                match.Settled = true;
            });
            // An earlier admitted participant may begin handover while this frozen commit is held.
            // Its new role delivery will be deferred, but finish the already admitted terminal wire result.
            foreach (var item in frozen)
            {
                var participant = match.Participants[item.Player];
                if (participant.Owner?.State != RoleOwnerState.Quiescing
                    || participant.Lease?.Cancelled != true) continue;
                if (item.Obligation.Entry == ChickenEntry)
                    await item.Player.SendGameAsync(ChickenResult,
                        ChickenResultNotify.Parser.ParseFrom(item.Obligation.Result));
                else await item.Player.SendGameAsync(DragonSettle,
                    DragonIslandSettleNtf.Parser.ParseFrom(item.Obligation.Result));
            }
        }
        var recipients = frozen.ToDictionary(item => item.Player);
        await RoleFanout.EachAsync(recipients.Keys, async player =>
        {
            var item = recipients[player];
            try
            {
                var keys = new[] { ReservationKey.Role(item.Obligation.RoleId), Obligations.Key(item.Obligation.SourceKey) };
                if (_loop.Owners is { } owners)
                    await owners.RunSessionAsync("timed battle recipient", keys, item.Player,
                        () => DeliverOneAsync(item.Player, item.Obligation));
                else await DeliverOneAsync(item.Player, item.Obligation);
            }
            catch (OperationCanceledException) { throw; /* Login delivers the frozen obligation. */ }
            catch (Exception error) { _log.LogError(error, "Timed battle delivery deferred for role {Role}", item.Obligation.RoleId); }
        });
        await RoleFanout.EachAsync(match.Players, async player =>
        {
            try
            {
                if (match.Room != null && player.InstanceId == match.Room.Id)
                    await _instances.LeaveAsync(player, recoverDeadOnReturn: match.Chicken != null);
            }
            catch (Exception error) when (error is not OperationCanceledException)
            { _log.LogWarning(error, "Timed battle room leave failed for role {Role}", match.Participants[player].Id); }
            finally
            {
                player.EventFaction = 0;
                _active.Remove(player);
                // A player in a handover may refuse the stat refresh; that must not keep the match open.
                if (match.Chicken != null)
                    try { await ChickenDetachAsync(match, player); }
                    catch (Exception error) when (error is not OperationCanceledException)
                    { _log.LogWarning(error, "Survivor Battle detach failed for role {Role}", match.Participants[player].Id); }
            }
        });
        if (match.Room != null) _matches.Remove(match.Room.Id);
        SettlementCompletedForTest?.Invoke();
    }

    private async Task DeliverPendingAsync(GameSession session)
    {
        if (session.Role == null) return;
        long roleId = session.Role.Id;
        var pending = await Obligations.PendingAsync(_accounts, roleId);
        if (pending.Count > 0) _pendingDeliveryRoles.Add(roleId);
        foreach (var obligation in pending)
        {
            if (session.Role?.Id != roleId) throw new OperationCanceledException("Timed battle recipient changed role.");
            var keys = new[] { ReservationKey.Role(obligation.RoleId), Obligations.Key(obligation.SourceKey) };
            if (_loop.Owners is { } owners)
                await owners.RunSessionAsync("timed battle delivery", keys, session,
                    () => DeliverOneAsync(session, obligation));
            else await DeliverOneAsync(session, obligation);
        }
        _pendingDeliveryRoles.Remove(roleId);
    }

    private async Task DeliverOneAsync(GameSession session, TimedBattleObligationRow obligation)
    {
        var grants = JsonSerializer.Deserialize<AwardGrant[]>(obligation.Grants)
            ?? throw new InvalidDataException("Invalid frozen timed battle grants.");
        bool paid;
        if (grants.Length > 0)
            paid = await _character.ApplyAwardAsync(session, new AwardPlan(0, 0, grants), persist: tx =>
            {
                tx.Add(Obligations.DeliverPlan(obligation));
                if (grants.Any(grant => grant.Entry is not (100 or 101)))
                    tx.WithEventEconomy(EventEconomy.ForRole(_data, session, "timed-battle-deliver",
                        obligation.SourceKey + "/deliver", [], [], new { obligation.SourceKey }));
            });
        else
        {
            var tx = _roles.NewTransaction();
            tx.Add(Obligations.DeliverPlan(obligation));
            await _roles.CommitAsync(tx, "timed-battle/deliver-empty");
            paid = true;
        }
        if (!paid) throw new InvalidOperationException("Frozen timed battle award was not delivered.");
        if ((await Obligations.PendingAsync(_accounts, obligation.RoleId)).Count == 0)
            _pendingDeliveryRoles.Remove(obligation.RoleId);
        await SyncBattleStateAsync(session);
        if (obligation.Entry == ChickenEntry)
            await session.SendGameAsync(ChickenResult, ChickenResultNotify.Parser.ParseFrom(obligation.Result));
        else if (obligation.Entry == DragonEntry)
            await session.SendGameAsync(DragonSettle, DragonIslandSettleNtf.Parser.ParseFrom(obligation.Result));
        else await session.SendGameAsync(DungeonService.Result, DungeonResNotify.Parser.ParseFrom(obligation.Result));
        await _tasks.RefreshAsync(session);
    }

    private static RoleProgress CopyProgress(RoleProgress source)
    {
        var copy = new RoleProgress
        {
            SourceRevision = source.SourceRevision, Career = source.Career,
            Level = source.Level, JobLevel = source.JobLevel, Exp = source.Exp, JobExp = source.JobExp,
            BonusPoints = source.BonusPoints, StarterKit = source.StarterKit
        };
        source.Stats.CopyTo(copy.Stats, 0);
        foreach (var (entry, amount) in source.Currency) copy.Currency[entry] = amount;
        foreach (var (id, level) in source.Skills) copy.Skills[id] = level;
        foreach (var (slot, id) in source.SkillBar) copy.SkillBar[slot] = id;
        return copy;
    }
}

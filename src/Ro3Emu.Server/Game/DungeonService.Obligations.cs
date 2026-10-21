using System.Text.Json;
using Romsg;
using Ro3Emu.Server.Accounts;
using Ro3Emu.Server.Data;
using Ro3Emu.Server.Economy;
using Ro3Emu.Server.Maps;

namespace Ro3Emu.Server.Game;

public sealed partial class DungeonService
{
    internal Func<Attempt, Task>? AfterTerminalKillsPrecountedForTest { get; set; }

    /// <summary>The final actor's report and all eligible recipients become durable before lethal publication.</summary>
    private async Task FreezeTerminalKillAsync(GameSession killer, MonsterUnit monster)
    {
        if (Active(killer) is not { } a || a.GuildChallenge != null
            || monster.InstanceId != a.Room.Id || !a.Room.Monsters.Contains(monster)
            || a.Spawning || a.Advancing || a.Wave + 1 != a.Plan.Waves.Count) return;
        long roleId = killer.Role?.Id ?? throw new OperationCanceledException("Dungeon killer detached before terminal freeze.");
        var attachment = killer.Attachment
            ?? throw new OperationCanceledException("Dungeon killer has no terminal attachment.");
        if (a.ObligationsFrozen)
        {
            if (a.Precounted.Contains(monster.Uid))
            {
                if (a.TerminalKillerRole != roleId || !ReferenceEquals(a.TerminalKillerAttachment, attachment)
                    || attachment.Cancelled) throw new OperationCanceledException("Dungeon terminal killer changed.");
                StageActivation(a, monster);
            }
            return;
        }
        if (a.Killed.Contains(monster.Uid)) return;
        var unobserved = a.Room.Monsters.Where(m => !a.Killed.Contains(m.Uid)
            && (ReferenceEquals(m, monster) || m.DeathDurable)).ToArray();
        if (a.Killed.Count + unobserved.Length != a.Spawned) return;
        await a.FreezeGate.WaitAsync();
        try
        {
            if (killer.Role?.Id != roleId || !ReferenceEquals(killer.Attachment, attachment)
                || attachment.Cancelled || !ReferenceEquals(Active(killer), a))
                throw new OperationCanceledException("Dungeon killer detached before terminal freeze admission.");
            if (a.ObligationsFrozen) { StageActivation(a, monster); return; }
            unobserved = a.Room.Monsters.Where(m => !a.Killed.Contains(m.Uid)
                && (ReferenceEquals(m, monster) || m.DeathDurable)).ToArray();
            if (a.Killed.Count + unobserved.Length != a.Spawned) return;
            var counted = new List<(MonsterUnit Monster, RoleStatisticsInfo Stats)>();
            try
            {
                foreach (var killed in unobserved)
                {
                    long recipient = ReferenceEquals(killed, monster) ? roleId : killed.RewardRecipient;
                    if (recipient == 0 || !a.Stats.TryGetValue(recipient, out var stats))
                        throw new InvalidOperationException("Durable dungeon kill has no participant recipient.");
                    if (!a.Killed.Add(killed.Uid) || !a.Precounted.Add(killed.Uid))
                        throw new InvalidOperationException("Dungeon terminal kill count changed during freeze.");
                    counted.Add((killed, stats));
                    if (!stats.KillNpcData.TryGetValue(killed.Template.NpcId, out var count))
                        stats.KillNpcData[killed.Template.NpcId] = count = new ExtraValueData();
                    count.LongValue++;
                }
                a.TerminalMonster = monster;
                a.TerminalKillerRole = roleId;
                a.TerminalKillerAttachment = attachment;
                monster.RewardSource ??= new RewardSourceId(_character.RewardRealmId,
                    RewardSourceId.ProcessRun, monster.SpawnGeneration);
                a.TerminalKillSource = monster.RewardSource.Key;
                if (AfterTerminalKillsPrecountedForTest is { } afterPrecounted)
                    await afterPrecounted(a);
                await FreezeLockedAsync(a, _loop.Now - a.StartedAt < TimeSpan.FromMilliseconds(a.Plan.DurationMs),
                    a.TerminalKillSource);
            }
            catch
            {
                if (!a.ObligationsFrozen)
                {
                    foreach (var (killed, stats) in counted)
                    {
                        var count = stats.KillNpcData[killed.Template.NpcId];
                        count.LongValue--;
                        if (count.LongValue == 0) stats.KillNpcData.Remove(killed.Template.NpcId);
                        a.Precounted.Remove(killed.Uid);
                        a.Killed.Remove(killed.Uid);
                    }
                    a.TerminalMonster = null;
                    a.TerminalKillerRole = 0;
                    a.TerminalKillerAttachment = null;
                    a.TerminalKillSource = null;
                }
                throw;
            }
            StageActivation(a, monster);
        }
        finally { a.FreezeGate.Release(); }
    }

    private async Task FreezeAsync(Attempt a, bool won)
    {
        await a.FreezeGate.WaitAsync();
        try { if (!a.ObligationsFrozen) await FreezeLockedAsync(a, won); }
        finally { a.FreezeGate.Release(); }
    }

    private async Task FreezeLockedAsync(Attempt a, bool won, string? killSourceKey = null)
    {
        bool success = won && a.PlayBook == _seasons.PlayBook && a.SeasonId == _seasons.SeasonId;
        var observed = Present(a).Where(a.Loaded.Contains)
            .Select(s => (Session: s, Role: s.Role!.Id, Attachment: s.Attachment)).ToArray();
        using var ownerScope = await _owners.EnterAsync("dungeon/freeze",
            observed.Select(p => Ownership.ReservationKey.Role(p.Role)).ToArray());
        if (a.Settled || a.Room.Closed) throw new OperationCanceledException("Dungeon room ended before reward freeze.");
        var participants = observed.Where(p => p.Session.Role?.Id == p.Role
            && ReferenceEquals(p.Session.Attachment, p.Attachment)
            && Present(a).Contains(p.Session)).OrderBy(p => p.Role).Select(p => p.Session).ToArray();
        foreach (var participant in participants)
            if (participant.Owner is { } owner) ownerScope.Context.Pin(owner);
        long duration = Math.Max(1, (long)(_loop.Now - a.StartedAt).TotalMilliseconds);
        var rows = new List<DungeonRewardObligation>();
        long lethalDamage = killSourceKey != null ? a.TerminalMonster?.StagedLethalDamage ?? 0 : 0;
        if (lethalDamage > 0) a.Stats[a.TerminalKillerRole].TotalDamage += lethalDamage;
        try
        {
            foreach (var s in participants)
            {
                long roleId = s.Role!.Id;
                var original = JsonSerializer.SerializeToUtf8Bytes(s.Dungeons!);
                var next = CurrentState(s.Dungeons!);
                var goods = new List<DungeonRewardGood>();
                bool newRecord = false;
                bool auctionEligible = false;
                if (success && a.Plan.RewardMulti is { } multi && a.Plan.RewardBoss is { } boss)
                {
                    string key = RecordKey(a.Plan);
                    var old = next.Records.GetValueOrDefault(key);
                    var allowance = SpendableAllowance(next, s, multi, a.Plan.RewardMark);
                    bool first = old?.Passed != true;
                    if (first || allowance.Remaining > 0)
                    {
                        goods = (Award(GameData.Int(boss, "_iPersonAward"), s)
                            ?? throw new InvalidOperationException("Missing dungeon award config"))
                            .Select(g => new DungeonRewardGood(g.Entry, g.Count, g.Bind,
                                GameData.Int(_data.Row("ItemConfig", g.Entry)!.Value, "_iType") == 1
                                    ? EconomyAssetKind.Currency : EconomyAssetKind.Item)).ToList();
                        auctionEligible = true;
                        if (!first) allowance.Remaining--;
                    }
                    newRecord = old == null || old.FastestMs == 0 || duration < old.FastestMs;
                    next.Records[key] = new Record { Passed = true, FastestMs = newRecord ? duration : old!.FastestMs };
                    EarnHonors(next, a, boss, duration);
                }
                // Valhalla Trials: a layer pays its _iPassReward once, and the clear is recorded with that payment.
                if (success && a.Plan.Layer is { } layer)
                {
                    string key = ValhallaPrefix + a.Plan.LayerId.ToString(System.Globalization.CultureInfo.InvariantCulture);
                    if (next.Records.GetValueOrDefault(key)?.Passed != true)
                    {
                        goods = (Award(GameData.Int(layer, "_iPassReward"), s)
                            ?? throw new InvalidOperationException("Missing Valhalla layer award config"))
                            .Select(g => new DungeonRewardGood(g.Entry, g.Count, g.Bind,
                                GameData.Int(_data.Row("ItemConfig", g.Entry)!.Value, "_iType") == 1
                                    ? EconomyAssetKind.Currency : EconomyAssetKind.Item)).ToList();
                        next.Records[key] = new Record { Passed = true, FastestMs = duration };
                    }
                }
                StoreHistory(next, a);
                if (success && GameData.Int(a.Plan.Config, "_iGamePlayType") == 24
                    && CaptureJourneyVictory(s, a.Plan.SceneEntry,
                        $"journey/dungeon/{_character.RewardRealmId}/{a.Room.Id}/{roleId}") is { } victory)
                    next.PendingJourney[victory.Source] = victory;
                var tiers = new Dictionary<int, long>();
                if (a.Plan.RewardBoss is { } auctionBoss && auctionBoss.TryGetProperty("_iGuildAward", out var tierRows)
                    && tierRows.ValueKind == JsonValueKind.Array)
                    foreach (var tier in tierRows.EnumerateArray())
                        if (tier.ValueKind == JsonValueKind.Array && tier.GetArrayLength() >= 2)
                            tiers[(int)(GameData.Number(tier[0]) ?? 0)] = (long)(GameData.Number(tier[1]) ?? 0);
                rows.Add(new DungeonRewardObligation
                {
                    Key = $"dungeon/{_character.RewardRealmId}/{a.Room.Id}/{roleId}", RoleId = roleId,
                    RoomId = a.Room.Id, SceneEntry = a.Plan.SceneEntry, Success = success,
                    DurationMs = duration, NewRecord = newRecord, BaseState = original,
                    NextState = JsonSerializer.SerializeToUtf8Bytes(next), Goods = goods,
                    GuildId = _guilds.ForRole(roleId)?.Id ?? 0,
                    AuctionSourceType = auctionEligible && a.Plan.RewardMulti is { } selected
                        ? _auctions.DungeonSrcType((int)GameData.Int(selected, "_iDifficulty")) : 0,
                    PlayBook = a.PlayBook, AuctionTiers = auctionEligible ? tiers : new(),
                    RequiresKill = killSourceKey != null, KillSourceKey = killSourceKey ?? "",
                });
            }
        }
        finally { if (lethalDamage > 0) a.Stats[a.TerminalKillerRole].TotalDamage -= lethalDamage; }
        // One writer transaction establishes every recipient, including disconnected roles present at the barrier.
        try { await _obligations.FreezeAsync(rows); }
        catch
        {
            // A lost writer acknowledgement may follow a committed freeze. Adopt its exact stored
            // outcome; never roll a replacement award or let the death resume without its recipients.
            var stored = _obligations.FrozenRoom(a.Room.Id);
            if (stored.Count != rows.Count || rows.Any(row => !stored.Any(old => old.Key == row.Key
                && JsonSerializer.SerializeToUtf8Bytes(old).AsSpan()
                    .SequenceEqual(JsonSerializer.SerializeToUtf8Bytes(row)))))
                throw;
        }
        a.ObligationsFrozen = true;
        a.ObligationsActivated = killSourceKey == null;
        a.FrozenWin = success;
    }

    private void StageActivation(Attempt a, MonsterUnit monster)
    {
        if (a.TerminalKillSource is not { } source || a.TerminalMonster != monster
            || source != monster.RewardSource?.Key)
            throw new InvalidOperationException("Frozen dungeon terminal source changed before kill.");
        var settlement = monster.RewardSettlement ??= new RewardSettlement(
            source, "monster-kill", autoDecompose: true)
        { DeferPresentation = true };
        if (settlement.SourceKey != source || settlement.CommandKind != "monster-kill")
            throw new InvalidOperationException("Dungeon activation has a different kill source.");
        if (ReferenceEquals(a.ActivationSettlement, settlement)) return;
        var persist = settlement.Enlist;
        settlement.Enlist = tx => { persist?.Invoke(tx); _obligations.EnlistActivate(a.Room.Id, source, tx); };
        var applied = settlement.Applied;
        settlement.Applied = () => { applied?.Invoke(); a.ObligationsActivated = true; };
        a.ActivationSettlement = settlement;
    }

    /// <summary>Only the ordinary kill receipt can earn a prepared terminal outcome.</summary>
    private async Task<bool> ResolvePreparedAsync(Attempt a)
    {
        // Wait for freeze setup, then release its gate before awaiting the kill writer or owner lock.
        await a.FreezeGate.WaitAsync();
        a.FreezeGate.Release();
        if (a.TerminalMonster?.RewardCommit is { } commit)
            try { await commit; } catch { /* An absent receipt below decides the failed hit. */ }
        if (_obligations.IsActivated(a.Room.Id))
        {
            a.ObligationsActivated = true;
            return true;
        }
        int canceled = await _obligations.CancelPreparedAsync(a.Room.Id);
        if (canceled == 0)
        {
            if (_obligations.IsActivated(a.Room.Id))
            {
                a.ObligationsActivated = true;
                return true;
            }
            throw new InvalidOperationException("Prepared dungeon recipients disappeared before settlement.");
        }
        foreach (var id in a.Precounted.ToArray())
        {
            a.Precounted.Remove(id);
            var monster = a.Room.Monsters.Single(m => m.Uid == id);
            if (monster.DeathDurable || monster.RewardSettlement?.Committed == true) continue;
            a.Killed.Remove(id);
            var stats = a.Stats[ReferenceEquals(monster, a.TerminalMonster)
                ? a.TerminalKillerRole : monster.RewardRecipient];
            if (stats.KillNpcData.TryGetValue(monster.Template.NpcId, out var count))
            {
                count.LongValue--;
                if (count.LongValue == 0) stats.KillNpcData.Remove(monster.Template.NpcId);
            }
        }
        a.TerminalMonster = null;
        a.TerminalKillerRole = 0;
        a.TerminalKillerAttachment = null;
        a.TerminalKillSource = null;
        a.ActivationSettlement = null;
        a.ObligationsFrozen = false;
        a.FrozenWin = false;
        return false;
    }

    private static State ApplyFrozenState(State current, DungeonRewardObligation row)
    {
        var before = JsonSerializer.Deserialize<State>(row.BaseState)!;
        var earned = JsonSerializer.Deserialize<State>(row.NextState)!;
        var next = current.SeasonStart < earned.SeasonStart ? new State
        {
            PlayBook = earned.PlayBook, SeasonStart = earned.SeasonStart,
            JobPosition = current.JobPosition, FirstSetted = current.FirstSetted,
            PendingJourney = new(current.PendingJourney)
        } : Copy(current);
        if (next.PlayBook == earned.PlayBook && next.SeasonStart == earned.SeasonStart)
        {
            foreach (var (key, record) in earned.Records)
                if (!before.Records.TryGetValue(key, out var prior) || prior.Passed != record.Passed || prior.FastestMs != record.FastestMs)
                {
                    var live = next.Records.GetValueOrDefault(key);
                    next.Records[key] = new Record
                    {
                        Passed = record.Passed || live?.Passed == true,
                        FastestMs = live?.FastestMs > 0 && record.FastestMs > 0
                            ? Math.Min(live.FastestMs, record.FastestMs) : Math.Max(live?.FastestMs ?? 0, record.FastestMs)
                    };
                }
            // Final Trial honors only move forward (NotFinish -> Finish -> Received); a claim made meanwhile stays.
            foreach (var (key, honors) in earned.Honors)
                foreach (var (id, state) in honors)
                    if (before.Honors.GetValueOrDefault(key)?.GetValueOrDefault(id) != state)
                    {
                        if (!next.Honors.TryGetValue(key, out var live)) next.Honors[key] = live = new();
                        live[id] = Math.Max(live.GetValueOrDefault(id), state);
                    }
            foreach (var (key, allowance) in earned.Allowances)
            {
                var old = before.Allowances.GetValueOrDefault(key);
                int spent = old == null ? 0 : Math.Max(0, old.Remaining - allowance.Remaining);
                if (next.Allowances.TryGetValue(key, out var live) && live.Epoch == allowance.Epoch)
                    live.Remaining = Math.Max(0, live.Remaining - spent);
                else if (!next.Allowances.ContainsKey(key)) next.Allowances[key] = new Allowance
                { Epoch = allowance.Epoch, Remaining = allowance.Remaining };
            }
        }
        foreach (var (key, bytes) in earned.Statistics)
            if (!before.Statistics.ContainsKey(key)) next.Statistics.TryAdd(key, bytes);
        foreach (var (key, bytes) in earned.Results)
            if (!before.Results.ContainsKey(key)) next.Results.TryAdd(key, bytes);
        foreach (var (key, victory) in earned.PendingJourney)
            if (!before.PendingJourney.ContainsKey(key)) next.PendingJourney.TryAdd(key, victory);
        return next;
    }

    private async Task<(bool Committed, List<ItemBaseInfo> Awarded)> DeliverAsync(GameSession s, DungeonRewardObligation row)
    {
        if (s.Role?.Id != row.RoleId || s.Dungeons == null) return (false, []);
        var settlement = new RewardSettlement(row.Key, "dungeon-reward") { DeferPresentation = true };
        var shown = new List<ItemBaseInfo>();
        using (await CharacterService.Lock(s, [RunKey(row.RoomId)]))
        {
            if (s.Role?.Id != row.RoleId || s.Dungeons == null) return (false, []);
            var next = ApplyFrozenState(s.Dungeons, row);
            var oldStacks = s.Items.ToDictionary(item => item.Guid, item => item.Stack);
            var granted = await _character.ExchangeLockedAsync(s, [], [], popup: false, sync: false,
                boundGoods: row.Goods.Where(g => g.Entry is not (100 or 101))
                    .Select(g => (g.Entry, g.Count, g.Bind)).ToArray(),
                baseExp: row.Goods.Where(g => g.Entry == 100).Sum(g => (long)g.Count),
                jobExp: row.Goods.Where(g => g.Entry == 101).Sum(g => (long)g.Count),
                persist: tx =>
                {
                    tx.RoleBlob(row.RoleId, "dungeons", JsonSerializer.SerializeToUtf8Bytes(next));
                    if (row.Success && row.AuctionSourceType != 0 && row.AuctionTiers.Count > 0)
                        _auctions.RecordDungeonClear(tx, row.RoomId, row.RoleId, row.GuildId,
                            row.AuctionSourceType, row.PlayBook, row.AuctionTiers);
                    _obligations.EnlistComplete(row.Key, tx);
                }, rewardSettlement: settlement, asyncCommit: true, committed: () => s.Dungeons = next);
            if (granted == null || !settlement.Committed)
                throw new InvalidOperationException("Validated dungeon obligation was refused.");
            foreach (var item in granted.DistinctBy(item => item.Guid))
            {
                var info = _character.ItemInfoOf(item);
                info.Stack = item.Stack - oldStacks.GetValueOrDefault(item.Guid);
                if (info.Stack > 0) shown.Add(info);
            }
            foreach (var good in row.Goods.Where(g => _data.Row("ItemConfig", g.Entry) is { } item
                && GameData.Int(item, "_iType") == 1))
                shown.Add(_character.ItemInfoOf(new InvItem
                { Entry = good.Entry, Stack = good.Count, BindType = good.Bind }));
        }
        try { await settlement.PresentAsync(); }
        catch (Exception e) { _log.LogWarning(e, "Dungeon reward presentation failed after commit for {Source}", row.Key); }
        await DeliverJourneyPendingAsync(s);
        if (row.Success && row.AuctionSourceType != 0 && _obligations.PendingRoom(row.RoomId).Count == 0)
        {
            // The publication reserves every participant of the run. Inside a narrower durable scope (the role select
            // delivering the last pending obligation, C/D 15) it runs as its own operation right after.
            if (_loop.Owners is { } owners && !owners.CurrentCovers(Ownership.ReservationKey.Realm))
                _loop.TrackRuntimeDetached("dungeon auction publication", () => PublishAuctionAsync(row.RoomId));
            else await PublishAuctionAsync(row.RoomId);
        }
        return (true, shown);
    }

    private async Task PublishAuctionAsync(long room)
    {
        try
        {
            await _auctions.MarkDungeonRunReadyAsync(room);
            await _auctions.FinalizeDungeonRunAsync(room);
        }
        catch (Exception e) { _log.LogError(e, "Auction publication deferred for dungeon {Room}", room); }
    }

    private static Ownership.ReservationKey RunKey(long room) =>
        Ownership.ReservationKey.Entity("auction_run", room.ToString(System.Globalization.CultureInfo.InvariantCulture));

    /// <summary>C/D 15: the auction runs of the role's pending obligations, read before the role select admits so it
    /// can reserve them up front.</summary>
    internal async Task<Ownership.ReservationKey[]> LoginKeysAsync(long roleId) =>
        [.. (await _obligations.PendingAsync(roleId)).Select(row => RunKey(row.RoomId)).Distinct()];

    /// <summary>Login and new entry both drain already earned grants before a later dungeon can spend allowances.</summary>
    public async Task DeliverPendingAsync(GameSession s)
    {
        if (s.Role == null || s.Dungeons == null) return;
        foreach (var row in _obligations.Pending(s.Role.Id))
        {
            // Inside the role select only the obligations it reserved are delivered; one frozen after its admission
            // stays durable and pending for the next entry or login.
            if (_loop.Owners is { } owners && !owners.CurrentCovers(RunKey(row.RoomId))) continue;
            try
            {
                var delivered = await DeliverAsync(s, row);
                if (delivered.Committed) await _character.ItemsChangedAsync(s, stats: false);
            }
            catch (Exception e)
            {
                _log.LogError(e, "Dungeon obligation {Source} remains pending for {Role}", row.Key, s.Role.Id);
                if (s.Owner?.State == Ownership.RoleOwnerState.RecoveryRequired)
                {
                    s.CancelPendingCharacterIntents();
                    s.AbortForRecovery();
                }
                throw;
            }
        }
    }
}

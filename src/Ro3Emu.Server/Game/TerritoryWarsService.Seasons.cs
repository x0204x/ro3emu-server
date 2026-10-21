using System.Text.Json;
using Romsg;
using Ro3Emu.Server.Accounts;
using Ro3Emu.Server.Accounts.Persistence;

namespace Ro3Emu.Server.Game;

public sealed partial class TerritoryWarsService
{
    private WorldState? _seasonCleanupState;
    private readonly HashSet<long> _seasonRoleProjectionPending = new();

    private async Task ProjectSeasonRolesAsync()
    {
        var candidates = _sessions.All().Where(s => s.Role != null && !RoleRequiresRecovery(s.Role.Id)
            && _seasonRoleProjectionPending.Contains(s.Role.Id)).ToArray();
        await RoleFanout.EachAsync(candidates, async s =>
        {
            long roleId = s.Role!.Id;
            var attachment = s.Attachment;
            try
            {
                var stagedRole = LoadRole(roleId);
                await SaveRoleAsync(roleId, stagedRole);
                if (s.Role?.Id != roleId || !ReferenceEquals(s.Attachment, attachment)) return;
                s.TerritoryWars = stagedRole;
                await SyncSiegeAsync(s);
                _seasonRoleProjectionPending.Remove(roleId);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception error) { _log.LogWarning(error, "Territory season role projection pending for {Role}", roleId); }
        });
    }

    private async Task CleanupSeasonRuntimeAsync()
    {
        await RoleFanout.EachAsync(_queue.Keys.ToArray(),
            s => CancelQueueAsync(s, TerritoryWarQueueExitReason.SceneDestroyed));
        foreach (var b in _battles.Values.ToArray())
        {
            await RoleFanout.EachAsync(b.Room.ActiveMembers.Select(m => m.Session), async s =>
            {
                var role = s.Role;
                var attachment = s.Attachment;
                try
                {
                    if (ReferenceEquals(s.TerritoryBattle, b) && !await _instances.LeaveAsync(s))
                        await _instances.AbandonAsync(b.Room, s);
                    else if (!ReferenceEquals(s.TerritoryBattle, b))
                        await _instances.AbandonAsync(b.Room, s);
                }
                finally
                {
                    if (ReferenceEquals(s.Role, role) && ReferenceEquals(s.Attachment, attachment)
                        && !b.Room.ActiveMembers.Any(m => ReferenceEquals(m.Session, s))
                        && ReferenceEquals(s.TerritoryBattle, b)) s.TerritoryBattle = null;
                }
            });
            if (!b.Room.ActiveMembers.Any()) _battles.Remove(b.Key);
        }
        foreach (var b in _siegeBattles.Values.ToArray())
        {
            await RoleFanout.EachAsync(b.Room.ActiveMembers.Select(m => m.Session), async s =>
            {
                var role = s.Role;
                var attachment = s.Attachment;
                try
                {
                    if (ReferenceEquals(s.SiegeBattle, b) && !await _instances.LeaveAsync(s))
                        await _instances.AbandonAsync(b.Room, s);
                    else if (!ReferenceEquals(s.SiegeBattle, b))
                        await _instances.AbandonAsync(b.Room, s);
                }
                finally
                {
                    if (ReferenceEquals(s.Role, role) && ReferenceEquals(s.Attachment, attachment)
                        && !b.Room.ActiveMembers.Any(m => ReferenceEquals(m.Session, s))
                        && ReferenceEquals(s.SiegeBattle, b)) s.SiegeBattle = null;
                }
            });
            if (!b.Room.ActiveMembers.Any()) _siegeBattles.Remove(b.Key);
        }
        if (_queue.Count != 0 || _battles.Count != 0 || _siegeBattles.Count != 0) return;
        _siegeTestOpen = null;
        await ResetWorldAsync();
        _seasonCleanupState = null;
    }

    /// <summary>
    /// SeasonService.Start/PlayBook identifies the activity epoch. Inferred administrative rollover:
    /// refund only unfinished auctions, abort unfinished rooms, archive ownership/history and retain
    /// paid reward keys. Returning to an epoch restores its ledger, never creates a second payout.
    /// </summary>
    private async Task ReconcileSeasonAsync()
    {
        if (_seasonCleanupState is { } oldRuntime)
        {
            var current = state;
            state = oldRuntime;
            try { await CleanupSeasonRuntimeAsync(); }
            finally { state = current; }
            if (_seasonCleanupState != null) return;
        }
        if (state.Epoch == Epoch) { await ProjectSeasonRolesAsync(); return; }
        if (state.Epoch.Length == 0) { state.Epoch = Epoch; await SaveAsync(state); return; }
        var old = Copy(state);
        var original = Copy(old);
        var treasury = new Dictionary<long, GuildStore.State>();
        var priorTreasury = new Dictionary<long, GuildStore.State>();
        foreach (var round in old.Rounds.Values.Where(r => !r.Settled))
        {
            foreach (var entry in round.Zones.Values.SelectMany(z => z.Entries).Where(e => !e.Occupier && e.Amount > 0))
            {
                if (!treasury.TryGetValue(entry.GuildId, out var guild))
                {
                    guild = _guildStore.Get(entry.GuildId);
                    if (guild != null)
                    {
                        treasury[guild.Id] = guild;
                        priorTreasury[guild.Id] = _guildStore.Get(guild.Id)!;
                    }
                }
                if (guild != null)
                { if (entry.Legacy) guild.Materials = checked(guild.Materials + entry.Amount); else guild.AuctionMaterials = checked(guild.AuctionMaterials + entry.Amount); }
            }
            round.Settled = true;
            foreach (var zone in round.Zones.Values) zone.Entries.Clear();
            foreach (var auto in round.Auto.Values) auto.State = 0;
        }
        foreach (var battle in old.Battles.Values.Where(b => !b.Settled)) battle.Settled = true;
        foreach (var battle in old.SiegeRuns.Values.Where(b => !b.Settled)) battle.Settled = true;
        var targetBytes = await _accounts.ReadAsync<byte[]?>(new TerritoryWarsSeasonArchiveReadPlan(Epoch));
        var restored = targetBytes is { Length: > 0 }
            ? JsonSerializer.Deserialize<WorldState>(targetBytes) ?? throw new JsonException("Null territory archive.")
            : new WorldState { Epoch = Epoch };
        if (restored.Epoch != Epoch) throw new JsonException("Territory archive epoch mismatch.");
        var transaction = _accounts.NewTransaction();
        int receipt = transaction.Add(new TerritoryWarsSeasonTransitionPlan(old.Epoch,
            JsonSerializer.SerializeToUtf8Bytes(old), Epoch,
            JsonSerializer.SerializeToUtf8Bytes(new WorldState { Epoch = Epoch })));
        foreach (var guild in treasury.Values)
            _guildStore.EnlistSave(guild, transaction, "Guild changed during territory season rollover");
        var beforeArchives = targetBytes is { Length: > 0 }
            ? new[] { (State: restored, Prefix: "archive/" + Epoch + "/") } : [];
        if (WarsEconomy(original, restored, priorTreasury.Values.ToArray(), treasury.Values.ToArray(),
            "season-rollover", $"territory-season-rollover:{old.Epoch}:{Epoch}",
            beforeArchives: beforeArchives,
            afterArchives: [(old, "archive/" + old.Epoch + "/")]) is { } economy)
            transaction.WithEventEconomy(economy);
        var committed = await _roles.CommitAsync(transaction, "territory/season-rollover");
        var next = JsonSerializer.Deserialize<WorldState>((byte[])committed[receipt]!)!;
        foreach (var peer in _sessions.All().Where(s => s.Role != null && s.TerritoryWars != null))
            _seasonRoleProjectionPending.Add(peer.Role!.Id);
        _seasonCleanupState = state;
        try
        {
            // Battle.State reads the current epoch during scene leave. Retire the old runtime only
            // after the transition is durable, and before exposing the new epoch to those callbacks.
            await CleanupSeasonRuntimeAsync();
        }
        finally { PublishCommitted(next); }
        await ProjectSeasonRolesAsync();
        foreach (long guild in treasury.Keys) await _guilds.RefreshResourceAsync(guild);
        await PublishAsync();
    }
}

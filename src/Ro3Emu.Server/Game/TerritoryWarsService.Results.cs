using Google.Protobuf;
using Romsg;
using Ro3Emu.Server.Accounts;
using Ro3Emu.Server.Accounts.Persistence;
using Ro3Emu.Server.Data;
using Ro3Emu.Server.Economy;
using Ro3Emu.Server.Maps;
using System.Text.Json;

namespace Ro3Emu.Server.Game;

public sealed partial class TerritoryWarsService
{
    private SceneStatisticsInfo Statistics(BattleState r)
    {
        var scene = new SceneStatisticsInfo { SceneId = r.StatisticsId, DungeonId = (int)r.Scene };
        foreach (var p in r.Players.Values)
            scene.RoleStatistics[p.Id] = new RoleStatisticsInfo
            {
                BaseInfo = new StatisticsRoleBaseInfo
                {
                    Id = p.Id, Area = 1, Name = p.Name, Job = p.Job, GuildId = p.Guild,
                    RewardData = new StatisticsAwardCheckData { PlayerLevel = p.Level, Job = p.Job }
                },
                TotalDamage = p.Damage, TotalBeDamage = p.Taken, TotalCure = p.Healed, Totalscore = p.Score
            };
        foreach (long guild in r.Guilds)
            if (guildStore.Get(guild) is { } g)
                scene.GuildStatistics[guild] = new GuildStatisticsInfo
                {
                    GuildId = guild, GuildName = g.Name,
                    GuildIcon = new GuildHeadIcon { IconType = g.Icon, BackgroundType = g.Background }
                };
        return scene;
    }
    private DungeonStatisticsInfoWithDungeon StatisticsReport(BattleState r) => new()
    {
        StatisticsId = r.StatisticsId, DungeonId = (int)r.Scene, StatisticsTime = (r.Settled ? r.End : clock.Now) * 1000,
        SceneStatistics = { [r.StatisticsId] = Statistics(r) }
    };

    /// <summary>
    /// The last crystal owner wins the capital/siege (GVGConfig help). Point-war score ranks capture/hold work;
    /// equal scores retain the incumbent, then guild id gives a stable order (inferred tie policy).
    /// Ownership, report and per-role reward outboxes commit together before any client is notified.
    /// </summary>
    private async Task SettleAsync(int key)
    {
        if (state.Battles[key].Settled) return;
        var next = Copy(state); var r = next.Battles[key];
        long incumbent = r.Points[0].Owner;
        long winner = r.Round >= 2 ? incumbent : r.Scores.Where(x => x.Value > 0)
            .OrderByDescending(x => x.Value).ThenByDescending(x => x.Key == incumbent).ThenBy(x => x.Key).Select(x => x.Key).FirstOrDefault();
        if (winner == 0 && r.Guilds.Contains(incumbent)) winner = incumbent;
        r.Winner = winner; r.Settled = true;
        AssignTerritoryOwner(next, r.Zone, winner);
        // MG_Dungeon cause enums: this settlement is triggered by the scheduled stage timeout;
        // LC_DAL_TerritoryWars determines each recipient's victory from winEntityId, not the round number.
        var result = new DungeonResultInfo
        {
            FirstResultType = 1, SecondResultType = 1, Result = winner == 0 ? 0 : 1,
            WinEntityId = winner, StatisticsId = r.StatisticsId, DungeonId = (int)r.Scene, StatisticsTime = r.End * 1000,
            DungeonStartTimestamp = r.Start * 1000, SceneStatistics = { [r.StatisticsId] = Statistics(r) }
        };
        r.Result = result.ToByteArray();
        if (!next.Results.TryGetValue(r.Zone, out var list)) next.Results[r.Zone] = list = new();
        list.Add(r.Result);
        if (list.Count > 20) list.RemoveRange(0, list.Count - 20); // bounded history retention (inferred).
        var roleChanges = new Dictionary<long, RoleState>();
        int rank = 0;
        foreach (var p in r.Players.Values.Where(p => p.Loaded).OrderByDescending(p => p.Score).ThenByDescending(p => p.Damage).ThenBy(p => p.Id))
        {
            rank++;
            var role = LoadRole(p.Id);
            string awardKey = $"{clock.Epoch}:{r.Round}:{r.Zone}";
            if (role.Paid.Contains(awardKey) || role.Pending.ContainsKey(awardKey)) continue;
            role.Honor += p.Score; role.Valor += p.Kills;
            var recipient = sessions.All().FirstOrDefault(s => s.Role?.Id == p.Id)
                ?? new GameSession(null!, null!) { Role = roles.Get(p.Id), Progress = roles.LoadProgress(p.Id) };
            var goods = new List<Grant>();
            if (recipient.Role != null)
            {
                AddAward(goods, Param(p.Guild == winner ? "TerritoryStrongHoldOccupyAuctionAward" : "TerritoryStrongHoldJoinAuctionAward", 1001006), recipient);
                // Settlement tables are rank/qualification awards. Roll once here and persist the exact goods.
                AddRankAward(goods, "SiegeSettlementPrimaryAward", rank, recipient);
                if (p.Score > 0) AddRankAward(goods, "SiegeSettlementHonorAward", rank, recipient);
                if (p.Guild == winner && r.Round >= 2) AddRankAward(goods, "SiegeSettlementSeniorAward", rank, recipient);
            }
            role.Pending[awardKey] = goods.GroupBy(x => (x.Entry, x.Bind)).Select(g => new Grant(g.Key.Entry, checked(g.Sum(x => x.Count)), g.Key.Bind)).ToList();
            roleChanges[p.Id] = role;
        }
        var transaction = accounts.NewTransaction();
        Save(next, transaction);
        foreach (var (id, role) in roleChanges) SaveRole(id, role, transaction);
        string settlementKey = $"{clock.Epoch}:{r.Round}:{r.Zone}";
        var awards = roleChanges.ToDictionary(pair => pair.Key, pair => pair.Value.Pending[settlementKey]);
        if (PendingSource(awards, "battle-settle", $"territory-battle-settle:{next.Epoch}:{r.Round}:{r.Zone}") is { } reward)
            transaction.WithEventEconomy(reward);
        await roles.CommitAsync(transaction, "territory/settle");
        PublishCommitted(next);
        await RoleFanout.EachAsync(sessions.All().Where(s => s.Role != null && roleChanges.ContainsKey(s.Role.Id)), s =>
        {
            if (s.Role is { } current && roleChanges.TryGetValue(current.Id, out var role)) s.TerritoryWars = role;
            return Task.CompletedTask;
        });
        await RoleFanout.EachAsync(sessions.All().Where(s => s.Role != null && r.Players.ContainsKey(s.Role.Id)), async s =>
        {
            try
            {
                await DeliverAsync(s); await SyncSiegeAsync(s);
                await Send(s, ResultNotify, new TerritoryWarsDungeonResultNotify { CommonResultInfo = result });
                if (s.TerritoryBattle is { } b && b.Key == key) await Send(s, 30401, BaseInfo(b));
            }
            catch (Exception e) when (e is IOException or ObjectDisposedException or System.Net.Sockets.SocketException)
            { log.LogDebug(e, "Territory settlement delivery deferred for disconnected role {Role}", s.Role?.Id); }
        });
        await PublishAsync();
    }
    private void AddRankAward(List<Grant> grants, string table, int rank, GameSession s)
    {
        foreach (var row in data.Table(table).Values)
        {
            if (row.TryGetProperty("_kRank", out var range) && range.ValueKind == JsonValueKind.Array && range.GetArrayLength() >= 2
                && (rank < GameData.Number(range[0]) || rank > GameData.Number(range[1]))) continue;
            if (row.TryGetProperty("_kMultiverseArray", out var mv) && mv.ValueKind == JsonValueKind.Array
                && mv.GetArrayLength() > 0 && !mv.EnumerateArray().Any(v => GameData.Number(v) == clock.PlayBook)) continue;
            AddAward(grants, GameData.Int(row, "_iAwardGroupID"), s); break;
        }
    }
    private void AddAward(List<Grant> grants, long group, GameSession s)
    {
        long id = group;
        if (data.Row("MultiverseGroupConfig", group) is { } mg)
        { id = GameData.Int(mg, $"_iValue_{clock.PlayBook}"); if (id == 0) id = GameData.Int(mg, "_iValue_default"); }
        if (data.Row("AwardConfig", id) is not { } raw) return;
        var row = raw.ValueKind == JsonValueKind.Array ? raw[0] : raw;
        if (row.TryGetProperty("_kAward", out var fixedItems) && fixedItems.ValueKind == JsonValueKind.Array)
            foreach (var t in fixedItems.EnumerateArray())
                if (t.ValueKind == JsonValueKind.Array && t.GetArrayLength() >= 2 && GameData.Number(t[1]) is > 0)
                    grants.Add(new Grant((int)(GameData.Number(t[0]) ?? 0), (int)(GameData.Number(t[1]) ?? 0), t.GetArrayLength() > 2 ? (int)(GameData.Number(t[2]) ?? 0) : 0));
        grants.AddRange(rewards.RollAwardBound(id, s).Select(g => new Grant(g.Entry, g.Count, g.Bind)));
    }

    /// <summary>Shared dungeon report IDs return the same real counters as 83015; a role may query its own reports.</summary>
    public async Task<bool> SharedAsync(GameSession s, uint id, ReadOnlyMemory<byte> body)
    {
        if (s.Role == null) return false;
        if (id == 30114)
        {
            var req = DungeonStatisticsInfoWithDungeonReq.Parser.ParseFrom(body.Span);
            var records = state.Battles.Values.Where(r => req.StaisticsIds.Contains(r.StatisticsId)).ToArray();
            if (records.Length == 0) return false;
            var msg = new DungeonStatisticsInfoWithDungeonResp();
            if (req.StaisticsIds.Count > 20 || records.Length != req.StaisticsIds.Distinct().Count() || records.Any(r => !r.Players.ContainsKey(s.Role.Id))) msg.Result = ErrorCodes.NoPermission;
            else msg.DungeonStatistics.AddRange(records.Select(StatisticsReport));
            await Send(s, id, msg); return true;
        }
        if (s.TerritoryBattle is not { } b) return false;
        if (id == 30106) return true; // ClientDungeonEvent cannot author a capture, score or final result.
        if (id == 30117) await Send(s, id, new GetDungeonStatisticsInDungeonResp { DungeonStatistics = StatisticsReport(b.State) });
        else if (id == 30116)
        {
            var report = StatisticsReport(b.State);
            var msg = new DungeonStatisticsModuleRsp { StatisticsId = report.StatisticsId, DungeonId = report.DungeonId, StatisticsTime = report.StatisticsTime };
            msg.SceneStatistics.Add(report.SceneStatistics); await Send(s, id, msg);
        }
        else if (id == 30110)
        {
            var msg = new DungeonStatisticsRes(); var map = new DungeonStatisticsInfo { DungeonMapId = b.State.Scene };
            foreach (var p in b.State.Players.Values) map.DungeonRolesInfo.Add(new DungeonRoleInfo
            {
                RoleBase = new ObjScoreBaseInfo { Id = p.Id, Name = p.Name, Job = p.Job, Area = 1 }, TotalDamage = p.Damage,
                TotalBeDamage = p.Taken, TotalCure = p.Healed, Totalscore = p.Score
            });
            msg.DungeonRolesInfo[b.State.StatisticsId] = map; await Send(s, id, msg);
        }
        else return false;
        return true;
    }

    /// <summary>TerritoryWorldDropItemCarryLimit/MaterialNPC and supply dialogue select currency 133 and the donor NPC.</summary>
    private async Task SubmitDropAsync(GameSession s)
    {
        var guild = Own(s);
        var npc = maps.Map(s.SceneEntry)?.Npcs.FirstOrDefault(n => n.NpcId == Param("TerritoryWorldMaterialNPC", 200052)
            && MonsterService.Distance(n.X, n.Z, s.Position.X, s.Position.Z) <= 5);
        if (guild == null) { await Error(s, SubmitDrop, ErrorCodes.TerritoryGuild); return; }
        if (s.Dead || s.SceneLoading || npc == null) { await Error(s, SubmitDrop, ErrorCodes.TerritoryInvalidPoint); return; }
        using var held = await CharacterService.Lock(s);
        guild = Own(s);
        if (guild == null) { await Error(s, SubmitDrop, ErrorCodes.TerritoryGuild); return; }
        long count = s.Progress!.Currency.GetValueOrDefault(133);
        if (count <= 0) { await Error(s, SubmitDrop, ErrorCodes.Parameter); return; }
        var role = LoadRole(s.Role!.Id); var next = Copy(state);
        role.Donated = checked(role.Donated + count); role.Donations[clock.Now] = checked(role.Donations.GetValueOrDefault(clock.Now) + count);
        next.Donations[s.Role.Id] = role.Donated;
        guild.Materials = checked(guild.Materials + count); // carried supply -> guild material 126, one-to-one (inferred).
        if (await character.ExchangeLockedAsync(s, [(133, count)], [], sync: false, popup: false, persist: tx =>
        {
            guildStore.EnlistSave(guild, tx); SaveRole(s.Role.Id, role, tx); Save(next, tx);
            tx.WithEventEconomy(new EventEconomyMutation(EventEconomy.Catalog(data), Guid.NewGuid().ToString("N"),
                s.Account!, s.Role.Id, "event/territory-submit-drop",
                $"territory-submit-drop:{next.Epoch}:{s.Role.Id}:{role.Donated}",
                DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                [new(new(EconomyAssetKind.Currency, 133), count)],
                [new(new(EconomyAssetKind.GuildResource, 3), count)],
                [new(new(EconomyOwnerKind.Role, s.Role.Id.ToString()), new(EconomyAssetKind.Currency, 133), -count),
                 new(new(EconomyOwnerKind.Guild, guild.Id.ToString()), new(EconomyAssetKind.GuildResource, 3), count)],
                JsonSerializer.SerializeToUtf8Bytes(new { Kind = "submit-drop", Role = s.Role.Id, Guild = guild.Id, Count = count })));
        }, asyncCommit: true) == null) { await Error(s, SubmitDrop, ErrorCodes.Parameter); return; }
        PublishCommitted(next); s.TerritoryWars = role;
        await character.ItemsChangedAsync(s, false);
        await guilds.RefreshResourceAsync(guild.Id);
        await Send(s, SubmitDrop, new NullArg());
    }
    private Task WarHallAsync(GameSession s)
    {
        var guild = Own(s);
        if (guild == null) return Send(s, WarHall, new TerritoryWarsWarHallGetInfoResp { Result = ErrorCodes.TerritoryGuild });
        var utc = DateTimeOffset.FromUnixTimeSeconds(clock.Now).UtcDateTime;
        long week = new DateTimeOffset(utc.Date.AddDays(-((int)utc.DayOfWeek + 6) % 7), TimeSpan.Zero).ToUnixTimeSeconds();
        long month = new DateTimeOffset(new DateTime(utc.Year, utc.Month, 1), TimeSpan.Zero).ToUnixTimeSeconds();
        TerritoryWarsWarHallDonateRankGroup Group(long start)
        {
            var g = new TerritoryWarsWarHallDonateRankGroup();
            foreach (var member in guild.Members.Values)
            {
                var r = LoadRole(member.RoleId);
                long count = r.Donations.Where(x => x.Key >= start).Sum(x => x.Value);
                if (count > 0) g.Entries.Add(new TerritoryWarsWarHallDonateRankEntry { PlayerId = member.RoleId, GuildPosition = member.Official, DonateCount = count });
            }
            var ordered = g.Entries.OrderByDescending(x => x.DonateCount).ThenBy(x => x.PlayerId).ToArray(); g.Entries.Clear(); g.Entries.AddRange(ordered); return g;
        }
        return Send(s, WarHall, new TerritoryWarsWarHallGetInfoResp { WeekRank = Group(week), MonthRank = Group(month), SeasonRank = Group(clock.SeasonStart) });
    }
}

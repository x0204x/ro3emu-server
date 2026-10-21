using Romsg;
using Ro3Emu.Server.Accounts;
using Ro3Emu.Server.Data;

namespace Ro3Emu.Server.Game;

public sealed partial class TerritoryWarsService
{
    public sealed class AuctionRound
    {
        public int Round { get; set; }
        public long FinalEnd { get; set; }
        public bool Settled { get; set; }
        public Dictionary<int, AuctionZone> Zones { get; set; } = new();
        public Dictionary<long, AutoBidPlan> Auto { get; set; } = new();
    }
    public sealed class AuctionZone
    {
        public int Id { get; set; }
        public int Seats { get; set; }
        public List<BidEntry> Entries { get; set; } = new();
        public HashSet<long> Bidded { get; set; } = new();
    }
    public sealed class BidEntry
    {
        public long GuildId { get; set; }
        public long Amount { get; set; }
        public long Time { get; set; }
        public bool Occupier { get; set; }
        public bool Legacy { get; set; }
        public long EscortScore { get; set; }
    }
    public sealed class AutoBidPlan
    {
        public long Creator { get; set; }
        public int Zone { get; set; }
        public long Initial { get; set; }
        public long Maximum { get; set; }
        public int Style { get; set; }
        public int State { get; set; }
        public long Next { get; set; }
    }

    private AuctionRound? Round() => state.Rounds.GetValueOrDefault(_clock.Current.Round);
    private static bool Bidding(int type) => type is 4 or 8 or 12 or 16 or 17 or 18;
    private static bool Final(int type) => type is 16 or 17 or 18;
    private IEnumerable<int> AuctionZones(int round) => Zones.Where(z => GameData.Int(z, "_iType") == (round == 3 ? 1 : 2)).Select(z => (int)Id(z));
    private AuctionRound NewRound(int round)
    {
        var final = _clock.RoundStage(round, 16, 17, 18);
        var range = ParamArray("TerritoryAuctionFinalTimeRandomRange");
        // PublicParam's secret final interval is drawn once and persisted (inferred placement within final stage).
        long seconds = range.Length == 2 ? Random.Shared.NextInt64(range[0], range[1] + 1) : 0;
        var r = new AuctionRound { Round = round, FinalEnd = final == null ? 0 : Math.Min(final.EndTime, final.StartTime + seconds) };
        foreach (int id in AuctionZones(round))
        {
            var z = new AuctionZone
            {
                Id = id, Seats = (int)Param(round == 1 ? "TerritoryAuctionPointSeatCount"
                : round == 2 ? "TerritoryAuctionStateSeatCount" : "TerritoryAuctionSiegeSeatCount", round == 1 ? 9 : 2)
            };
            // Qualified owners occupy reserved seats, as isOccupier in LC_DAL_TerritoryWarsAuction.lua:194-223.
            long owner = OwnerOf(id);
            if (owner == 0 && round > 1)
            {
                var previous = WarStage(round - 1);
                var finalists = previous == null ? [] : state.RuleResults.GetValueOrDefault(previous.ResultRule) ?? [];
                // (inferred) The best preceding owner in this branch receives the incumbent seat.
                owner = state.Battles.Values.Where(b => b.Round == round - 1 && b.Settled && finalists.Contains(b.Winner)
                    && (Parent(b.Zone) == id || RootOf(b.Zone) == id)).OrderByDescending(b => b.Scores.GetValueOrDefault(b.Winner))
                    .ThenBy(b => b.Winner).Select(b => b.Winner).FirstOrDefault();
            }
            if (owner > 0)
                z.Entries.Add(new BidEntry { GuildId = owner, Occupier = true });
            r.Zones[id] = z;
        }
        return r;
    }

    /// <summary>GuildBlackMarket permission 25 is selected by LC_TerritoryWarsHelper.IsHasTerritoryWarsAuctionRight.</summary>
    private int BidEligibility(GameSession s, GuildStore.State? g, AuctionRound? round, int zone, bool legacy, bool setting = false)
    {
        if (g == null) return ErrorCodes.TerritoryGuild;
        if (!Right(g, s.Role!.Id, 25)) return ErrorCodes.NoPermission;
        if (!_functions.IsOpen(s, 72)) return ErrorCodes.FeatureUnavailable;
        if (round == null || !Bidding(_clock.Current.Type) && !(setting && _clock.Current.Type is 3 or 7 or 11)) return ErrorCodes.TerritoryAuctionClosed;
        if (round.Settled || _clock.Now >= round.FinalEnd) return ErrorCodes.TerritoryAuctionSettled;
        var zones = round.Zones;
        if (!zones.TryGetValue(zone, out var target)) return ErrorCodes.TerritoryInvalidPoint;
        if (g.Members.Count < Param("TerritoryAuctionMinGuildMemberCount", 20)) return ErrorCodes.TerritoryGuildMembers;
        if (target.Entries.Any(e => e.GuildId == g.Id && e.Occupier) || OwnerOf(zone) == g.Id) return ErrorCodes.TerritoryOccupier;
        if (zones.Any(z => z.Key != zone && z.Value.Entries.Any(e => e.GuildId == g.Id))) return ErrorCodes.TerritoryOtherRegion;
        // Both UI protocols compete for the same seats. Preserve each bid's source treasury on refund.
        if (round.Zones.Values.Any(z => z.Entries.Any(e => e.GuildId == g.Id && e.Legacy != legacy && !e.Occupier))) return ErrorCodes.TerritoryAlreadyQualified;
        return 0;
    }

    // The legacy UI uses 126/min100 while the current UI uses 129/min1000. Both target one seat pool.
    // (inferred compatibility adapter) compare opening-bid multiples, not nominal amounts of unlike assets.
    // Escrow and refunds remain exact in their original treasury; this is not an item exchange operation.
    private decimal BidUnit(bool legacy) => legacy ? Param("TerritoryAuctionMinBidCost", 1000)
        / (decimal)Math.Max(1, Param("TerritoryStrongHoldAuctionDefaultPrice", 100)) : 1m;
    private decimal BidValue(BidEntry entry) => entry.Amount * BidUnit(entry.Legacy);
    private List<BidEntry> Ranked(AuctionZone zone) => zone.Entries.OrderByDescending(e => e.Occupier)
        .ThenByDescending(BidValue).ThenByDescending(e => e.EscortScore).ThenBy(e => e.Time).ThenBy(e => e.GuildId).ToList();
    private long Minimum(AuctionZone z, long guildId, bool legacy)
    {
        long start = Param(legacy ? "TerritoryStrongHoldAuctionDefaultPrice" : "TerritoryAuctionMinBidCost", legacy ? 100 : 1000);
        long inc = legacy ? Param("TerritoryStrongHoldBidNum", 100)
            : (long)Math.Ceiling(start * (decimal)Param("TerritoryAuctionBidIncrementRatio", 1000) / 10000);
        long prior = z.Entries.FirstOrDefault(e => e.GuildId == guildId)?.Amount ?? 0;
        decimal lowest = z.Entries.Where(e => !e.Occupier).Select(e => BidValue(e) / BidUnit(legacy)).DefaultIfEmpty(long.MaxValue).Min();
        long threshold = z.Entries.Count < z.Seats ? start : (long)Math.Min(long.MaxValue, Math.Ceiling(lowest) + inc);
        return Math.Max(threshold, prior > 0 ? (long)Math.Min(long.MaxValue, (decimal)prior + inc) : start);
    }

    /// <summary>
    /// Change the incremental treasury hold and evict/refund below-seat bids in the same SQLite transaction.
    /// The DAL's Returned row proves refunds; oldest bid then guild id for ties is (inferred).
    /// </summary>
    private async Task<(int Error, long[] Changed)> PlaceBidAsync(long guildId, int zoneId, long amount, long observed,
        bool legacy, GameSession? actor = null)
    {
        if (Round() is not { Settled: false } live || !Bidding(_clock.Current.Type) || _clock.Now >= live.FinalEnd)
            return (ErrorCodes.TerritoryAuctionSettled, []);
        if (!live.Zones.ContainsKey(zoneId)) return (ErrorCodes.TerritoryInvalidPoint, []);
        var next = Copy(state);
        var round = next.Rounds[_clock.Current.Round];
        var z = round.Zones[zoneId];
        var old = z.Entries.FirstOrDefault(e => e.GuildId == guildId);
        long current = old?.Amount ?? 0;
        if (observed != current) return (ErrorCodes.TerritoryBidChanged, []);
        if (amount <= 0 || amount * BidUnit(legacy) > long.MaxValue || amount < Minimum(z, guildId, legacy)) return (legacy ? ErrorCodes.TerritoryLegacyBidLow : ErrorCodes.TerritoryBidLow, []);
        var guild = _guildStore.Get(guildId);
        if (guild == null) return (ErrorCodes.TerritoryGuild, []);
        if (guild.Members.Count < Param("TerritoryAuctionMinGuildMemberCount", 20)) return (ErrorCodes.TerritoryGuildMembers, []);
        if (z.Entries.Any(e => e.GuildId == guildId && e.Occupier)) return (ErrorCodes.TerritoryOccupier, []);
        if (round.Zones.Any(p => p.Key != zoneId && p.Value.Entries.Any(e => e.GuildId == guildId))) return (ErrorCodes.TerritoryOtherRegion, []);
        if (old != null && old.Legacy != legacy) return (ErrorCodes.TerritoryAlreadyQualified, []);
        long delta = amount - current;
        if ((legacy ? guild.Materials : guild.AuctionMaterials) < delta) return (ErrorCodes.TerritoryAuctionFunds, []);
        if (legacy) guild.Materials -= delta; else guild.AuctionMaterials -= delta;
        z.Entries.RemoveAll(e => e.GuildId == guildId);
        z.Entries.Add(new BidEntry
        {
            GuildId = guildId, Amount = amount, Time = _clock.Now, Legacy = legacy,
            EscortScore = GuildEscortScore(guildId, round.Round)
        });
        z.Bidded.Add(guildId);
        var treasury = new Dictionary<long, GuildStore.State> { [guildId] = guild };
        var ranking = Ranked(z);
        foreach (var evicted in ranking.Skip(z.Seats))
        {
            if (!treasury.TryGetValue(evicted.GuildId, out var other))
            { other = _guildStore.Get(evicted.GuildId); if (other != null) treasury[other.Id] = other; }
            if (other != null) { if (evicted.Legacy) other.Materials = checked(other.Materials + evicted.Amount); else other.AuctionMaterials = checked(other.AuctionMaterials + evicted.Amount); }
            if (round.Auto.TryGetValue(evicted.GuildId, out var auto)) auto.Next = _clock.Now + AutoDelay(auto.Style);
        }
        z.Entries = ranking.Take(z.Seats).ToList();
        await CommitAsync(next, treasury.Values, "bid", actor,
            $"territory-bid:{Epoch}:{round.Round}:{zoneId}:{guildId}:{observed}:{amount}:{_clock.Now}");
        return (0, treasury.Keys.ToArray());
    }

    private async Task BidAsync(GameSession s, TerritoryAuctionBidReq req)
    {
        var g = Own(s);
        int error = BidEligibility(s, g, Round(), req.ZoneId, false);
        long[] changed = [];
        if (error == 0) (error, changed) = await PlaceBidAsync(g!.Id, req.ZoneId, req.TargetBid, req.CurrentBid, false, s);
        if (error != 0) { await Error(s, Bid, error); return; }
        await Send(s, Bid, new TerritoryAuctionBidRes { ActualBid = req.TargetBid });
        await AuctionChangedAsync(changed);
    }

    private async Task AuctionChangedAsync(IEnumerable<long> changed)
    {
        foreach (long guild in changed) await _guilds.RefreshResourceAsync(guild);
        await RoleFanout.EachAsync(_sessions.All().Where(s => s.Role != null && s.TerritoryWars != null), SendRanksAsync);
        await PublishAsync();
    }

    private Task SendRanksAsync(GameSession s)
    {
        var round = Round() ?? NewRound(Math.Clamp(_clock.Current.Round, 1, 3));
        var own = Own(s);
        var msg = new TerritoryAuctionRankDataNtf { IsSettled = round.Settled };
        foreach (var z in round.Zones.Values.OrderBy(z => z.Id))
        {
            var info = new TerritoryZoneRankData
            {
                ZoneId = z.Id, SeatCount = z.Seats,
                SelfGuildBidded = own != null && z.Bidded.Contains(own.Id),
                SelfGuildCanBid = BidEligibility(s, own, Round(), z.Id, false) == 0
            };
            int rank = 0;
            foreach (var e in Ranked(z)) info.Entries.Add(new TerritoryBidRankEntry { Rank = ++rank, GuildId = e.GuildId, BidAmount = (long)BidValue(e), IsOccupier = e.Occupier });
            msg.Zones.Add(info);
        }
        return Send(s, RankNotify, msg);
    }

    private int LegacyZone(int id) => ZoneType(id) == 2 ? id : _data.Row("WarzoneConfig", id) is { } old && GameData.Int(old, "_iType") == 2
        ? Zones.Where(z => GameData.Int(z, "_iType") == 2).OrderBy(Id).Skip(id - 2).Select(z => (int)Id(z)).FirstOrDefault() : id;
    private async Task LegacyBidAsync(GameSession s, TerritoryWarsAuctionReq req)
    {
        int zone = LegacyZone(req.StateId);
        var g = Own(s);
        int error = BidEligibility(s, g, Round(), zone, true);
        long[] changed = [];
        if (error == 0)
        {
            long current = Round()!.Zones[zone].Entries.FirstOrDefault(e => e.GuildId == g!.Id)?.Amount ?? 0;
            (error, changed) = await PlaceBidAsync(g!.Id, zone, req.AuctionPrice, current, true, s);
        }
        var res = new TerritoryWarsAuctionResp { Result = error };
        if (error == 0) res.Info = LegacyInfo(req.StateId, Round()!.Zones[zone]);
        await Send(s, LegacyBid, res);
        if (error == 0)
        {
            await AuctionChangedAsync(changed);
            var notice = new TerritoryWarsAuctionFinishTimeNotify { GuildId = g!.Id, StateId = req.StateId, FinishTime = Round()!.FinalEnd * 1000 };
            await RoleFanout.EachAsync(_sessions.All().Where(x => x.Role != null && g.Members.ContainsKey(x.Role.Id)),
                member => Send(member, AuctionTimeNotify, notice));
        }
    }
    private TerritoryWarsAuctionStateInfo LegacyInfo(int id, AuctionZone z)
    {
        var info = new TerritoryWarsAuctionStateInfo { StateId = id };
        foreach (var e in Ranked(z))
        {
            var g = _guildStore.Get(e.GuildId);
            if (g == null) continue;
            var entry = new TerritoryWarsAuctionGuildInfo { GuildId = g.Id, GuildInfo = _guilds.Snapshot(g), AuctionPrice = (long)Math.Ceiling(BidValue(e) / BidUnit(true)), AuctionTime = e.Time * 1000 };
            foreach (var point in state.Owners.Where(p => p.Value == g.Id && ZoneType(p.Key) == 3)) entry.OccupyPointId[point.Key] = 1;
            info.GuildInfos[g.Id] = entry;
        }
        return info;
    }
    private Task LegacyQueryAsync(GameSession s, GetTerritoryWarsAuctionInfoReq req)
    {
        var response = new GetTerritoryWarsAuctionInfoResp { Info = new() };
        var r = Round() ?? NewRound(Math.Clamp(_clock.Current.Round, 1, 3));
        var ids = req.StateId.Count > 0 ? req.StateId.ToArray() : r.Zones.Keys.Where(z => req.ZoneId == 0 || RootOf(z) == req.ZoneId).ToArray();
        foreach (int id in ids) if (r.Zones.TryGetValue(LegacyZone(id), out var z)) response.Info.StateInfos[id] = LegacyInfo(id, z);
        return Send(s, LegacyQuery, response);
    }

    private long AutoDelay(int style)
    {
        var range = ParamArray(style == 1 ? "TerritoryAuctionAutoBidDelayAggressive" : style == 2 ? "TerritoryAuctionAutoBidDelaySteady" : "TerritoryAuctionAutoBidDelayStealth");
        return range.Length == 2 ? Random.Shared.NextInt64(range[0], range[1] + 1) : 5;
    }
    private async Task AutoSetAsync(GameSession s, TerritoryAutoBidSetReq req)
    {
        var g = Own(s);
        int error = BidEligibility(s, g, Round(), req.ZoneId, false, setting: true);
        if (error == 0 && req.InitialBid < Param("TerritoryAuctionMinBidCost", 1000)) error = ErrorCodes.TerritoryInitialBidLow;
        if (error == 0 && (req.MaxBid <= 0 || (decimal)req.MaxBid * 10000 < (decimal)req.InitialBid * Param("TerritoryAuctionMaxBidMinMultiplier", 20000))) error = ErrorCodes.TerritoryMaxBidLow;
        if (error == 0 && (int)req.Style is < 1 or > 3) error = ErrorCodes.Parameter;
        if (error != 0) { await Error(s, AutoSet, error); return; }
        var next = Copy(state);
        next.Rounds[_clock.Current.Round].Auto[g!.Id] = new AutoBidPlan
        {
            Creator = s.Role!.Id, Zone = req.ZoneId, Initial = req.InitialBid,
            Maximum = req.MaxBid, Style = (int)req.Style, State = 1, Next = _clock.Now + AutoDelay((int)req.Style)
        };
        await CommitAsync(next);
        await Send(s, AutoSet, new TerritoryAutoBidSetRes());
        await NotifyAutoGuildAsync(g.Id);
    }
    private async Task AutoCancelAsync(GameSession s, TerritoryAutoBidCancelReq req)
    {
        var g = Own(s);
        if (g == null || !Right(g, s.Role!.Id, 25)) { await Error(s, AutoCancel, ErrorCodes.NoPermission); return; }
        if (Round()?.Auto.TryGetValue(g.Id, out var plan) == true && plan.Zone == req.ZoneId)
        {
            var next = Copy(state); next.Rounds[_clock.Current.Round].Auto[g.Id].State = 0; await CommitAsync(next);
        }
        await Send(s, AutoCancel, new TerritoryAutoBidCancelRes());
        await NotifyAutoGuildAsync(g.Id);
    }
    private Task NotifyAutoGuildAsync(long id) => RoleFanout.EachAsync(
        _sessions.All().Where(s => s.Role != null && Own(s)?.Id == id), SendAutoAsync);
    private Task SendAutoAsync(GameSession s)
    {
        var auto = Round()?.Auto.GetValueOrDefault(Own(s)?.Id ?? 0);
        return Send(s, AutoNotify, auto == null ? new TerritoryAutoBidSettingChangeNtf() : new TerritoryAutoBidSettingChangeNtf
        {
            ZoneId = auto.Zone, IsActive = (TerritoryAutoBidState)auto.State, InitialBid = auto.Initial, MaxBid = auto.Maximum, Style = (TerritoryAutoBidStyle)auto.Style
        });
    }

    private async Task AuctionClockAsync()
    {
        var stage = _clock.Current;
        if (stage.Type is 3 or 4 or 7 or 8 or 11 or 12 or 16 or 17 or 18 && !state.Rounds.ContainsKey(stage.Round))
        { var next = Copy(state); next.Rounds[stage.Round] = NewRound(stage.Round); await CommitAsync(next); }
        foreach (var r in state.Rounds.Values.Where(r => !r.Settled && _clock.Now >= r.FinalEnd).ToArray())
        {
            var next = Copy(state); next.Rounds[r.Round].Settled = true;
            var refunds = ApplyAuctionResultRule(next, next.Rounds[r.Round]);
            foreach (var auto in next.Rounds[r.Round].Auto.Values) auto.State = 0;
            await CommitAsync(next, refunds, "auction-settlement", sourceKey: $"territory-auction-settle:{Epoch}:{r.Round}");
            await AuctionChangedAsync(refunds.Select(g => g.Id));
            foreach (long guild in r.Auto.Keys) await NotifyAutoGuildAsync(guild);
        }
        var active = Round();
        if (active == null || active.Settled || !Bidding(stage.Type)) return;
        foreach (var (guildId, original) in active.Auto.Where(a => a.Value.State == 1 && a.Value.Next <= _clock.Now).ToArray())
        {
            var live = Round()!;
            var z = live.Zones[original.Zone];
            var existing = z.Entries.FirstOrDefault(e => e.GuildId == guildId);
            long min = Minimum(z, guildId, false);
            long amount = Math.Max(original.Initial, min);
            // AutoBidSteps are configured ceilings, then maxBid is the final ceiling (inferred step selection).
            long step = ParamArray("TerritoryAuctionAutoBidSteps").FirstOrDefault(v => v >= amount && v <= original.Maximum);
            if (step > 0) amount = step;
            var guild = _guildStore.Get(guildId);
            int error = guild == null ? ErrorCodes.TerritoryGuild : !Right(guild, original.Creator, 25) ? ErrorCodes.NoPermission
                : guild.Members.Count < Param("TerritoryAuctionMinGuildMemberCount", 20) ? ErrorCodes.TerritoryGuildMembers : 0;
            long[] changed = [];
            if (error == 0 && existing == null)
                if (amount > original.Maximum) error = ErrorCodes.TerritoryBidLow;
                else (error, changed) = await PlaceBidAsync(guildId, z.Id, amount, 0, false);
            var next = Copy(state);
            var plan = next.Rounds[stage.Round].Auto[guildId];
            plan.Next = _clock.Now + AutoDelay(plan.Style);
            if (error != 0) plan.State = 2;
            await CommitAsync(next);
            if (changed.Length > 0) await AuctionChangedAsync(changed);
            await NotifyAutoGuildAsync(guildId);
        }
    }

    /// <summary>Point phase seats are distributed across its three child flags; later phases contest the selected city.</summary>
    private long[] QualifiedFor(int territory, int? roundId = null)
    {
        int round = roundId ?? Math.Clamp(_clock.Current.Round, 1, 3);
        if (!state.Rounds.TryGetValue(round, out var r) || !r.Settled) return [];
        int target = round == 1 && ZoneType(territory) != 2 ? Parent(territory) : territory;
        if (!r.Zones.TryGetValue(target, out var z)) return [];
        var entries = Ranked(z).Take(z.Seats).Select(x => x.GuildId).Distinct().ToArray();
        var rule = _clock.RoundStage(round, 16, 17, 18)?.ResultRule ?? 0;
        if (state.RuleResults.TryGetValue(rule, out var qualified)) entries = entries.Where(qualified.Contains).ToArray();
        if (round != 1) return entries;
        var points = Zones.Where(p => Parent((int)Id(p)) == target && GameData.Int(p, "_iType") == 3).Select(p => (int)Id(p)).Order().ToArray();
        int index = Array.IndexOf(points, territory);
        // (inferred) Snake-free round-robin grouping of the ranked qualifiers, preserving configured three flags/state.
        return index < 0 ? entries : entries.Where((_, i) => i % points.Length == index).ToArray();
    }
}

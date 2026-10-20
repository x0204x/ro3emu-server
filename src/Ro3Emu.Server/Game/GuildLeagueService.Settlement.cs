using System.Globalization;
using System.Text.Json;
using Google.Protobuf;
using Romsg;
using Ro3Emu.Server.Data;
using Ro3Emu.Server.Economy;

namespace Ro3Emu.Server.Game;

/// <summary>
/// Outcomes, rewards and the tournament settlement. Every step runs under Realm from <see cref="ReconcileAsync"/> and is
/// idempotent on the committed league state: an outcome is written once per pair and round, its rewards once (Rewarded) and
/// its auctions once (AuctionDone).
/// </summary>
public sealed partial class GuildLeagueService
{
    /// <summary>Commit a decided match: its outcome and report, then 30414 to everyone still on either battlefield.</summary>
    private async Task CommitMatchAsync(Match m)
    {
        var next = Copy(state);
        var group = next.Cup is { } cup && cup.Index == m.Index
            ? cup.Groups.FirstOrDefault(g => g.Tier == m.Tier && g.Order == m.Order) : null;
        if (group == null || group.Outcomes.Any(o => o.Round == m.Round && o.Pair == m.Pair)) { m.Committed = true; return; }
        var outcome = new Outcome
        {
            Round = m.Round, Pair = m.Pair, Blue = m.Blue, Red = m.Red, Winner = m.Winner,
            BluePoints = m.Points[m.Blue], RedPoints = m.Points[m.Red], Time = m.EndedAt, Report = m.Key
        };
        foreach (var f in m.Main.Fighters.Values.Concat(m.Sub.Fighters.Values))
        {
            var row = outcome.Players.TryGetValue(f.Role, out var known) ? known : outcome.Players[f.Role] = [f.Guild, 0];
            row[1] += f.Score;
        }
        group.Outcomes.Add(outcome);
        var history = History(m, group, outcome);
        next.Reports.Add(new Report { Id = m.Key, Time = m.EndedAt, Guilds = [m.Blue, m.Red], History = history.ToByteArray() });
        Prune(next);
        await CommitAsync(next, "match");
        m.Committed = true;
        var scores = outcome.Players.ToDictionary(p => p.Key, p => p.Value[1]);
        foreach (var handler in MatchCommitted)
            try { handler(m.Round, scores); }
            catch (Exception e) { log.LogError(e, "Guild League match hook failed for {Match}", m.Key); }
        await SendAllAsync(Present(m), ResultNotify, new GuildLeagueDungeonResultNtf { History = history });
    }

    /// <summary>
    /// GuildLeagueHistory: LeagueResultInfo (rankType = tier, result 0 blue / 1 red win, curStage = round) and both
    /// battlefields' boards keyed by scene (LC_GuildLeagueHelper.lua:1155-1241).
    /// </summary>
    private GuildLeagueHistory History(Match m, Group group, Outcome outcome)
    {
        var history = new GuildLeagueHistory
        {
            ResultId = m.Key, OwnerId = "",
            ResultInfo = new LeagueResultInfo
            {
                RankType = m.Tier, BlueGuild = Dungeon(m.Blue, group), RedGuild = Dungeon(m.Red, group),
                Result = outcome.Winner == m.Blue ? 0 : 1, Time = outcome.Time, CurStage = m.Round
            }
        };
        foreach (var (key, info) in Boards(m)) history.DungeonRolesInfo[key] = info;
        return history;
    }

    /// <summary>Keep each report while it is among the latest GuildLeagueHistoryReportNum of one of its guilds.</summary>
    private void Prune(LeagueState next)
    {
        int keep = (int)Setting("GuildLeagueHistoryReportNum", 10);
        var kept = new HashSet<Report>();
        foreach (long guild in next.Reports.SelectMany(r => r.Guilds).Distinct())
            foreach (var r in next.Reports.Where(r => r.Guilds.Contains(guild)).OrderByDescending(r => r.Time).Take(keep)) kept.Add(r);
        next.Reports.RemoveAll(r => !kept.Contains(r));
    }

    /// <summary>
    /// Close every round whose window has ended: live matches end, then each pair without an outcome is decided. A guild
    /// facing an empty slot wins by bye; two guilds that never met give the win to blue (inferred: nothing decided the
    /// pair). Neither carries rewards.
    /// </summary>
    private async Task CloseRoundsAsync(long now)
    {
        while (state.Cup is { Settled: false } cup && cup.SettledRound < 3 && now >= cup.Round(cup.SettledRound + 1).End)
        {
            int round = cup.SettledRound + 1;
            foreach (var m in matches.Values.Where(m => m.Index == cup.Index && m.Round == round && !m.Ended).ToArray()) End(m, now);
            foreach (var m in matches.Values.Where(m => m.Index == cup.Index && m.Round == round && !m.Committed).ToArray())
                await CommitMatchAsync(m);
            var next = Copy(state);
            var nextCup = next.Cup!;
            foreach (var group in nextCup.Groups)
            {
                var pairs = GuildLeagueBracket.Pairs(Preliminary(group), Scores(group, round - 1), round);
                for (int i = 0; i < pairs.Length; i++)
                {
                    if (group.Outcomes.Any(o => o.Round == round && o.Pair == i)) continue;
                    var (blue, red) = (pairs[i].Blue, pairs[i].Red);
                    group.Outcomes.Add(new Outcome
                    {
                        Round = round, Pair = i, Blue = blue, Red = red,
                        Winner = blue == 0 ? red : blue, Time = nextCup.Round(round).End, Rewarded = true, AuctionDone = true
                    });
                }
            }
            nextCup.SettledRound = round;
            await CommitAsync(next, "round");
        }
    }

    /// <summary>
    /// Per match: mail 1038 with the tier's _iPersonAwardID (win) or _iPersonLoseAwardID (loss) to every participant with at
    /// least GuildSettingConfig GuildLeaguePersonAwardNeedScore personal points (GuildLeagueRankConfig columns proven; the
    /// mail and threshold use are inferred from their names). Then each guild's auction (AuctionService.PublishGuildLeagueAsync)
    /// when it scored GuildLeagueAuctionAwardNeedScore guild points with GuildLeagueAuctionNeedNum participants of at least
    /// GuildLeagueAuctionNeedScore points (10140100123 names both requirements; which setting is which is inferred), else mail
    /// 1124 to its participants.
    /// </summary>
    private async Task RewardPendingAsync()
    {
        if (state.Cup is not { } cup) return;
        foreach (var group in cup.Groups)
            foreach (var outcome in group.Outcomes.ToArray())
            {
                if (!outcome.Rewarded) await RewardAsync(cup.Index, group, outcome);
                if (!outcome.AuctionDone) await AuctionAsync(cup.Index, group, outcome);
            }
    }

    private static string OutcomeKey(int index, Group group, Outcome o) => MatchKey(index, o.Round, group.Tier, group.Order, o.Pair);

    private Outcome Find(LeagueState next, Group group, Outcome outcome) => next.Cup!.Groups
        .Single(g => g.Tier == group.Tier && g.Order == group.Order).Outcomes.Single(o => o.Round == outcome.Round && o.Pair == outcome.Pair);

    private async Task RewardAsync(int index, Group group, Outcome outcome)
    {
        string key = OutcomeKey(index, group, outcome);
        long need = Setting("GuildLeaguePersonAwardNeedScore", 20);
        var awards = new Dictionary<long, List<MailService.Attachment>>();
        if (Rank(group.Tier) is { } rank)
            foreach (var (role, row) in outcome.Players)
            {
                if (row[1] < need || roles.Get(role) == null) continue;
                long award = GameData.Int(rank, row[0] == outcome.Winner ? "_iPersonAwardID" : "_iPersonLoseAwardID");
                var goods = Award(award);
                if (goods.Count > 0) awards[role] = goods;
            }
        var next = Copy(state);
        Find(next, group, outcome).Rewarded = true;
        int template = (int)Setting("GuildLeagueJoinAwardMailID", 1038);
        using (await mail.LockRecipientsAsync(awards.Keys))
            await CommitAsync(next, "rewards", tx =>
            {
                foreach (var (role, goods) in awards)
                    mail.QueueTemplateLocked(role, $"guild-league:{key}:{role}", template, goods, tx);
                if (AwardSource(awards, "guild-league-award:" + key) is { } economy) tx.WithEventEconomy(economy);
            });
        foreach (long role in awards.Keys)
            try { await mail.NotifyRecipientAsync(role); }
            catch (Exception e) { log.LogWarning(e, "Guild League reward mail notice deferred for {Role}", role); }
    }

    private async Task AuctionAsync(int index, Group group, Outcome outcome)
    {
        string key = OutcomeKey(index, group, outcome);
        long guildNeed = Setting("GuildLeagueAuctionAwardNeedScore", 1000), personalNeed = Setting("GuildLeagueAuctionNeedScore", 20);
        long countNeed = Setting("GuildLeagueAuctionNeedNum", 20);
        var refused = new List<long>();
        foreach (long guild in new[] { outcome.Blue, outcome.Red }.Where(g => g > 0))
        {
            long points = guild == outcome.Blue ? outcome.BluePoints : outcome.RedPoints;
            var players = outcome.Players.Where(p => p.Value[0] == guild && p.Value[1] >= personalNeed).Select(p => p.Key).ToArray();
            if (points >= guildNeed && players.Length >= countNeed)
            {
                int result = await auctions.PublishGuildLeagueAsync(key, guild, group.Tier, guild == outcome.Winner, players);
                if (result != 0) log.LogWarning("Guild League auction {Key} for guild {Guild} refused: {Result}", key, guild, result);
            }
            else refused.AddRange(outcome.Players.Where(p => p.Value[0] == guild).Select(p => p.Key).Where(r => roles.Get(r) != null));
        }
        var next = Copy(state);
        Find(next, group, outcome).AuctionDone = true;
        int template = (int)Setting("GuildLeagueJoinAwardFailedMailID", 1124);
        using (await mail.LockRecipientsAsync(refused))
            await CommitAsync(next, "auctions", tx =>
            {
                foreach (long role in refused)
                    mail.QueueTemplateLocked(role, $"guild-league:{key}:no-auction:{role}", template, [], tx, guildNeed, countNeed);
            });
        foreach (long role in refused)
            try { await mail.NotifyRecipientAsync(role); }
            catch (Exception e) { log.LogWarning(e, "Guild League auction mail notice deferred for {Role}", role); }
    }

    /// <summary>A MultiverseGroupConfig reward group's fixed AwardConfig items, as the Siege settlement reads them.</summary>
    private List<MailService.Attachment> Award(long groupId)
    {
        if (data.Row("MultiverseGroupConfig", groupId) is not { } group || GameData.Int(group, "_itype") != 0) return [];
        long id = GameData.Int(group, $"_iValue_{seasons.PlayBook}");
        if (id == 0) id = GameData.Int(group, "_iValue_default");
        if (data.Row("AwardConfig", id) is not { } raw) return [];
        var row = raw.ValueKind == JsonValueKind.Array ? raw[0] : raw;
        if (!row.TryGetProperty("_kAward", out var items) || items.ValueKind != JsonValueKind.Array) return [];
        return items.EnumerateArray().Where(a => a.ValueKind == JsonValueKind.Array && a.GetArrayLength() >= 2)
            .Select(a => new MailService.Attachment((int)(GameData.Number(a[0]) ?? 0), (int)(GameData.Number(a[1]) ?? 0),
                a.GetArrayLength() > 2 ? (int)(GameData.Number(a[2]) ?? 0) : 0))
            .Where(a => a.Count > 0 && data.Row("ItemConfig", a.Entry) != null).ToList();
    }

    private EventEconomyMutation? AwardSource(IReadOnlyDictionary<long, List<MailService.Attachment>> awards, string sourceKey)
    {
        var catalog = EventEconomy.Catalog(data);
        var grants = awards.SelectMany(pair => pair.Value.Where(g => g.Count > 0)
            .Select(grant => (Role: pair.Key, Grant: grant,
                Asset: catalog.TryAsset(grant.Entry, out var asset) ? asset
                    : throw new InvalidOperationException("Uncatalogued Guild League award " + grant.Entry)))).ToArray();
        if (grants.Length == 0) return null;
        var goods = grants.GroupBy(x => x.Asset).Select(g => new EconomyQuantity(g.Key,
            g.Aggregate(0L, (n, x) => checked(n + x.Grant.Count)))).ToArray();
        var deltas = grants.GroupBy(x => (x.Role, x.Asset)).Select(g => new EventEconomyOwnerDelta(
            new(EconomyOwnerKind.Role, g.Key.Role.ToString(CultureInfo.InvariantCulture)), g.Key.Asset,
            g.Aggregate(0L, (n, x) => checked(n + x.Grant.Count)))).ToArray();
        return new EventEconomyMutation(catalog, Guid.NewGuid().ToString("N"), "realm", 0,
            "event/guild-league-award", sourceKey, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            [], goods, deltas, JsonSerializer.SerializeToUtf8Bytes(new
            {
                Source = sourceKey,
                Awards = grants.Select(x => new { x.Role, x.Grant.Entry, x.Grant.Count, x.Grant.Bind })
            }));
    }

    /// <summary>
    /// After the finals: places 8 - score; per group the places 1.._iGroupUpNum move up a tier and the last _iGroupDownNum
    /// of the eight places move down (GuildLeagueBracket.Lift), shown as rankLift. Group
    /// winners of the tiers with _iGuildIconID get mail 1039 (10140100044: the Honor Emblem is unlocked; adding the
    /// emblem to the guild's icon choices is not implemented).
    /// </summary>
    private async Task SettleCupAsync()
    {
        var next = Copy(state);
        var cup = next.Cup!;
        var tiers = Tiers;
        var letters = new List<(long Leader, string Tier, int Place)>();
        foreach (var group in cup.Groups)
        {
            var scores = Scores(group);
            var real = group.Pairs.SelectMany((p, i) => new[] { (Id: p[0], Seed: i + 1), (Id: p[1], Seed: i + 5) })
                .Where(x => x.Id > 0).OrderBy(x => GuildLeagueBracket.Place(scores.GetValueOrDefault(x.Id))).ThenBy(x => x.Seed)
                .Select(x => x.Id).ToList();
            var rank = Rank(group.Tier);
            int up = rank is { } r1 ? (int)GameData.Int(r1, "_iGroupUpNum") : 0, down = rank is { } r2 ? (int)GameData.Int(r2, "_iGroupDownNum") : 0;
            int at = Array.IndexOf(tiers, group.Tier);
            foreach (long id in real)
            {
                if (!next.Guilds.TryGetValue(id, out var standing)) continue;
                standing.Group = group.Order;
                standing.Place = GuildLeagueBracket.Place(scores.GetValueOrDefault(id));
                standing.Lift = (int)GuildLeagueBracket.Lift(standing.Place, up, down, at > 0, at >= 0 && at + 1 < tiers.Length);
                if (standing.Lift == (int)GuildLeagueRankLift.Up) standing.Tier = tiers[at - 1];
                else if (standing.Lift == (int)GuildLeagueRankLift.Down) standing.Tier = tiers[at + 1];
            }
            if (real.Count > 0 && rank is { } row && row.TryGetProperty("_iGuildIconID", out var icons)
                && icons.ValueKind == JsonValueKind.Array && icons.GetArrayLength() > 0
                && guilds.Get(real[0]) is { } winner && winner.Members.Values.FirstOrDefault(m => m.Official == 1) is { } leader)
                letters.Add((leader.RoleId, data.TextOf(row, "_iName"), 1));
        }
        cup.Settled = true;
        int template = (int)Setting("GuildLeagueGuildIconNoticeMailID", 1039);
        using (await mail.LockRecipientsAsync(letters.Select(l => l.Leader)))
            await CommitAsync(next, "settle", tx =>
            {
                foreach (var (leader, tier, place) in letters)
                    mail.QueueTemplateLocked(leader, $"guild-league:{cup.Index}:honor:{leader}", template, [], tx, tier, place);
            });
        foreach (var (leader, _, _) in letters)
            try { await mail.NotifyRecipientAsync(leader); }
            catch (Exception e) { log.LogWarning(e, "Guild League honor mail notice deferred for {Role}", leader); }
        log.LogInformation("Guild League tournament {Index} settled", cup.Index);
    }
}

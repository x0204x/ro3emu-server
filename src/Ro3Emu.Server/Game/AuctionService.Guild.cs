using System.Text.Json;
using Romsg;
using Ro3Emu.Server.Accounts;
using Ro3Emu.Server.Accounts.Persistence;
using Ro3Emu.Server.Data;
using Ro3Emu.Server.Economy;

namespace Ro3Emu.Server.Game;

public sealed partial class AuctionService
{
    /// <summary>
    /// Trusted completion boundary for the GuildChallenge event owner, never a player upload route.
    /// GuildChallengeConfig / GuildChallengeRewardConfig choose the season and stage reward; AuctionTabConfig
    /// maps activity 125 to source 1003. Caller supplies a durable completion identity and proven participants.
    /// Stage 0 is the full-clear shortcut; the base _iRewardAuction award contains only _kPreview entries.
    /// committedParticipants is reserved for a durable clear record whose event owner already verified guild membership.
    /// </summary>
    internal Task<int> PublishGuildChallengeAsync(string completionKey, long guildId, int challengeId, int stage,
        IReadOnlyList<long> participants, bool committedParticipants = false, int? rewardPlayBook = null, int? rewardOpenDay = null)
    {
        int playBook = committedParticipants ? rewardPlayBook ?? seasons.PlayBook : seasons.PlayBook;
        var challenge = data.Row("GuildChallengeConfig", challengeId);
        if (challenge == null || stage < 0 || string.IsNullOrWhiteSpace(completionKey))
            return Task.FromResult(ErrorCodes.Parameter);
        if (challenge.Value.TryGetProperty("_kMultiverseArray", out var books) && books.ValueKind == JsonValueKind.Array
            && books.GetArrayLength() > 0 && !books.EnumerateArray().Any(book => GameData.Number(book) == playBook))
            return Task.FromResult(ErrorCodes.NoPermission);
        // (inferred) A full clear settles the final configured stage. The client's base auction award is
        // preview-only, while GuildChallengeRewardConfig gives the actual stage auction drop pool.
        // Canonicalizing before building the source key makes stage 0 and the explicit final stage idempotent.
        if (stage == 0)
        {
            if (!challenge.Value.TryGetProperty("_kStageId", out var stages) || stages.ValueKind != JsonValueKind.Array)
                return Task.FromResult(ErrorCodes.RewardMissing);
            stage = stages.EnumerateArray().Select(v => (int)(GameData.Number(v) ?? 0)).DefaultIfEmpty().Max();
        }
        var stageRow = data.Table("GuildChallengeRewardConfig").Values.FirstOrDefault(r =>
            GameData.Int(r, "_iGuildChallengeId") == challengeId && GameData.Int(r, "_iStage") == stage);
        if (stageRow.ValueKind != JsonValueKind.Object) return Task.FromResult(ErrorCodes.RewardMissing);
        long group = GameData.Int(stageRow, "_kRewardStageAuction");
        return PublishGuildAwardAsync($"guild:challenge:{guildId}:{challengeId}:{stage}:{completionKey}",
            guildId, GuildSourceForActivity(GameData.Int(challenge.Value, "_iActivityId")), group, participants,
            committedParticipants, playBook, committedParticipants ? rewardOpenDay : null, economicChallenge: true);
    }

    /// <summary>
    /// Trusted league completion boundary: rank, win/loss and distinct participant-count tier come from
    /// GuildLeagueRankConfig. AuctionTabConfig places league activities 109-111 under source 1002.
    /// </summary>
    internal Task<int> PublishGuildLeagueAsync(string completionKey, long guildId, int rank, bool won,
        IReadOnlyList<long> participants)
    {
        if (string.IsNullOrWhiteSpace(completionKey)) return Task.FromResult(ErrorCodes.Parameter);
        var config = data.Table("GuildLeagueRankConfig").Values.FirstOrDefault(r => GameData.Int(r, "_iRank") == rank);
        if (config.ValueKind != JsonValueKind.Object) return Task.FromResult(ErrorCodes.RewardMissing);
        long[] distinct = participants.Distinct().Order().ToArray();
        string field = won ? "_kAuctionAwardList" : "_kAuctionLoseAwardList";
        long group = 0, threshold = -1;
        if (config.TryGetProperty(field, out var tiers) && tiers.ValueKind == JsonValueKind.Array)
            foreach (var tuple in tiers.EnumerateArray())
                if (tuple.ValueKind == JsonValueKind.Array && tuple.GetArrayLength() >= 2
                    && GameData.Number(tuple[0]) is { } minimum && minimum <= distinct.Length && minimum > threshold)
                {
                    threshold = (long)minimum;
                    group = (long)(GameData.Number(tuple[1]) ?? 0);
                }
        return PublishGuildAwardAsync($"guild:league:{guildId}:{completionKey}",
            guildId, GuildSourceForActivity(109), group, distinct);
    }

    private int GuildSourceForActivity(long activity) => (int)data.Table("AuctionTabConfig").Values
        .Where(row => GameData.Int(row, "_iAuctionType") == (int)AuctionType.Guild
            && row.TryGetProperty("_kDailyActivity", out var activities) && activities.ValueKind == JsonValueKind.Array
            && activities.EnumerateArray().Any(v => GameData.Number(v) == activity))
        .Select(row => GameData.Int(row, "_iID")).SingleOrDefault();

    private async Task<int> PublishGuildAwardAsync(string source, long guildId, int srcType, long group,
        IReadOnlyList<long> participants, bool committedParticipants = false, int? rewardPlayBook = null, int? rewardOpenDay = null,
        bool economicChallenge = false)
    {
        if (!loop.IsOnLoop) return await loop.InvokeAsync(() => PublishGuildAwardAsync(source, guildId, srcType,
            group, participants, committedParticipants, rewardPlayBook, rewardOpenDay, economicChallenge));
        if (source.Length > 240 || participants.Count == 0 || srcType <= 0 || group < 0)
            return ErrorCodes.Parameter;
        long[] members = participants.Distinct().Order().ToArray();
        bool committed = false;
        try
        {
            for (int restage = 0; restage < 4; restage++)
            {
                var guild = guilds.Get(guildId);
                // (inferred) A committed clear keeps the eligibility snapshot even if a participant leaves before
                // outbox delivery. Direct/GM calls and league awards still require current guild membership.
                if (guild == null || members.Any(role => roles.Get(role) == null
                    || !committedParticipants && !guild.Members.ContainsKey(role)))
                    return ErrorCodes.NoPermission;
                long award = 0;
                if (group > 0)
                {
                    if (data.Row("MultiverseGroupConfig", group) is not { } mg) return ErrorCodes.RewardMissing;
                    award = GameData.Int(mg, $"_iValue_{rewardPlayBook ?? seasons.PlayBook}");
                    if (award == 0) award = GameData.Int(mg, "_iValue_default");
                    if (award == 0 || data.Row("AwardConfig", award) == null) return ErrorCodes.RewardMissing;
                }
                // Durable event owners capture the reward variant at clear time, so restart/date rollover cannot change it.
                int openDay = rewardOpenDay ?? Math.Max(1, (clock.GetLocalNow().Date - seasons.Start.Date).Days + 1);
                var rewardProgress = award == 0 ? null : roles.LoadProgress(members[0]);
                long? rewardProgressRevision = rewardProgress?.SourceRevision!.Revision(RevisionStore.Progress);
                var goods = award == 0 ? [] : rewards.RollAuctionAwardBound(award, rewardProgress!, openDay);
                // Every rolled item needs an actual pricing row. Reject atomically rather than turn a shared reward
                // into an arbitrary personal mail. A preview-only or explicitly empty award is a valid empty result.
                foreach (var (entry, amount, _) in goods)
                    if (entry is 100 or 101 || amount <= 0 || data.Row("ItemConfig", entry) == null
                        || data.Row("AuctionConfig", entry) is not { } cfg || GameData.Int(cfg, "_iAuction") != 1
                        || GameData.Int(cfg, "_iAuctionPrice") <= 0
                        || GameData.Int(cfg, "_iSellPrice") < GameData.Int(cfg, "_iAuctionPrice")
                        || GameData.Int(cfg, "_iBaseRaisePrice") <= 0
                        || data.Row("ItemConfig", GameData.Int(cfg, "_iCurrency")) == null)
                        return ErrorCodes.RewardMissing;
                bool created = false;
                // (inferred) Guild exhibit/time values are minutes; dungeon _iTimeLast is separately in seconds.
                long start = checked(Now + Math.Max(0, Param("GuildAuctionExhibit", 5)) * 60);
                long end = checked(start + Math.Max(1, Param("GuildAuctionTime", 30)) * 60);
                int itemCount = goods.Sum(g => g.Count);
                var keys = RoleKeys(members).Concat([
                    Ownership.ReservationKey.Entity("guild", guildId.ToString()),
                Ownership.ReservationKey.Entity("auction_source", source)])
                    .Concat(Enumerable.Range(0, itemCount).Select(index =>
                        Ownership.ReservationKey.Entity("auction_item", $"{source}:{index}"))).ToArray();
                long[] noticeRoles;
                using (var scope = await EnterAuctionAsync("auction/guild-publish", () => keys))
                {
                    guild = guilds.Get(guildId);
                    if (guild == null || members.Any(role => roles.Get(role) == null
                        || !committedParticipants && !guild.Members.ContainsKey(role))) return ErrorCodes.NoPermission;
                    if (rewardProgressRevision is { } captured && roles.ReadRevisionVector(members[0])
                            .Revision(RevisionStore.Progress) != captured
                        || openDay != (rewardOpenDay ?? Math.Max(1, (clock.GetLocalNow().Date - seasons.Start.Date).Days + 1)))
                        continue; // Release every key before rerolling and staging the new source-row set.
                    var transaction = roles.NewTransaction();
                    // E05d's challenge source shares the existing auction transaction; generated lot identities
                    // are bound to their staged source keys and writer receipts by the economy envelope.
                    if (economicChallenge && transaction.CaptureEntity(RevisionStore.Entity("auction_source", source)).Exists)
                        return 0;
                    store.EnlistSource(source, transaction);
                    var escrow = economicChallenge ? null : AuctionIntent("auction/guild-publish", sourceKey: "auction/source/" + source)
                        .Evidence(new
                        {
                            Source = source, GuildId = guildId, SrcType = srcType, Group = group,
                            Members = members, PlayBook = rewardPlayBook ?? seasons.PlayBook, OpenDay = openDay,
                            Goods = goods
                        });
                    var economicLots = new List<EconomyEffect>();
                    int index = 0;
                    foreach (var (entry, amount, bind) in goods)
                    {
                        escrow?.Source(AuctionAsset(entry), amount, EconomyReason.ActivityReward,
                            EconomyCompartment.AuctionLot);
                        var cfg = data.Row("AuctionConfig", entry)!.Value;
                        for (int n = 0; n < amount; n++)
                        {
                            string lotSource = $"{source}:{index++}";
                            store.EnlistSave(new AuctionStore.Item
                            {
                                SourceKey = lotSource, Type = AuctionType.Guild, GuildId = guildId,
                                SrcType = srcType, Entry = entry, Bind = bind, Eligible = members.ToList(),
                                Start = start, End = end, State = AuctionState.Public,
                                Price = (int)GameData.Int(cfg, "_iAuctionPrice"),
                                Raise = (int)GameData.Int(cfg, "_iBaseRaisePrice"),
                                FixedPrice = (int)GameData.Int(cfg, "_iSellPrice"),
                                MaxPrice = (int)GameData.Int(cfg, "_iSellPrice"),
                                Currency = (int)GameData.Int(cfg, "_iCurrency"),
                                TypeAttr = (int)GameData.Int(cfg, "_iTypeAttr"),
                            }, transaction);
                            if (economicChallenge) economicLots.Add(new(new(EconomyAssetKind.Item, entry),
                                new(EconomyOwnerKind.Escrow, "auction-source:" + lotSource), EconomyCompartment.AuctionLot,
                                1, EconomyLegKind.Source, EconomyReason.ActivityReward));
                        }
                    }
                    if (economicChallenge)
                    {
                        var actor = roles.Get(members[0])!;
                        transaction.WithEconomy(new EconomyEnvelope(Guid.NewGuid().ToString("N"), actor.Account, actor.Id,
                            "guild-challenge-auction-source", source, source, clock.GetUtcNow().ToUnixTimeMilliseconds(), economicLots, []));
                    }
                    if (escrow != null) transaction.WithEscrowEconomy(escrow);
                    RequireScope(scope, transaction);
                    try { await roles.CommitAsync(transaction, "auction/guild-publish"); }
                    catch (Ro3Emu.Server.Accounts.Persistence.AuctionSourceDuplicateException) { return 0; }
                    catch (EconomyOperationConflictException) when (store.HasSource(source)) { return 0; }
                    created = goods.Count > 0;
                    committed = true;
                    noticeRoles = guild.Members.Keys.ToArray();
                }
                if (created) await NotifyNewRowsAsync(AuctionType.Guild, noticeRoles);
                return 0;
            }
            return ErrorCodes.Unknown;
        }
        catch (Exception e)
        {
            log.LogError(e, "Guild auction source {Source} failed (committed before the failure: {Committed})", source, committed);
            return committed ? 0 : ErrorCodes.Unknown;
        }
    }
}

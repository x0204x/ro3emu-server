using System.Text.Json;
using Google.Protobuf;
using Romsg;
using Ro3Emu.Server.Accounts;
using Ro3Emu.Server.Accounts.Persistence;
using Ro3Emu.Server.Data;

namespace Ro3Emu.Server.Game;

/// <summary>
/// Season page 5022 / 82901-82903. The six function IDs come from
/// MultiverseConfig[101]._iDanSystem; LC_SeasonHelper.CalFunctionRank consumes successive _iExpLimit values.
/// Claims are limited per dan level across functions, not per function (GetSeasonDanRewardState:562-633).
/// </summary>
public sealed class SeasonPageService(GameData data, CharacterService character, FunctionService functions,
    SeasonService seasons, RoleStore roles, RewardService rewards, MailService mail, TimeProvider clock)
{
    public const uint PageSync = 5022, GetData = 82901, ReceiveRankReward = 82902, PutOnHonorBadge = 82903;

    /// <summary>
    /// ItemConfig/Localization identify 11..14 as MVP/WoE/league/life season rank points. Function 65 is
    /// Survivor Battle, whose net victory currency is 7. Direct use as page scores is (inferred).
    /// Function 23 accepts explicit authoritative dungeon-score events; no configured ordinary-clear event
    /// supplies an amount. No level, combat-power or unrelated currency substitutes for dungeon achievement.
    /// </summary>
    private static readonly IReadOnlyDictionary<int, int> ScoreCurrency = new Dictionary<int, int>
    { [17] = 11, [67] = 12, [60] = 13, [36] = 14, [65] = 7 };

    /// <summary>Server-owned score baseline and event dedupe; DBSeasonPage carries only client-visible history.</summary>
    public sealed class PageMeta
    {
        public int SeasonId { get; set; }
        public Dictionary<int, long> Baseline { get; set; } = new();
        public long DungeonScore { get; set; }
        public HashSet<string> DungeonEvents { get; set; } = new();
        public bool BreakSnapshotTaken { get; set; }
    }

    private PageMeta LoadMeta(long role, int current, bool hadPage)
    {
        var saved = roles.LoadSeasonPageMeta(role);
        return saved == null ? new PageMeta { SeasonId = hadPage ? 1 : current }
            : JsonSerializer.Deserialize<PageMeta>(saved) ?? new PageMeta { SeasonId = current };
    }

    /// <summary>
    /// (inferred) ItemConfig's five life_icon_badge_* currency records represent honor-wall possession.
    /// The client displays honorBadgeData independently of 5009; seed the wall from actual server-owned inventory.
    /// Acquisition remains permanent if a badge currency is later spent.
    /// </summary>
    private async Task<bool> AcquireOwnedBadgesAsync(GameSession session)
    {
        if (session.SeasonPage == null || session.SeasonState?.SeasonId != seasons.SeasonId) return false;
        bool changed = false;
        var next = session.SeasonPage!.Clone();
        foreach (var (id, count) in session.Progress!.Currency)
        {
            if (count <= 0 || next.HonorBadgeData.ContainsKey(id)
                || data.Row("ItemConfig", id) is not { } row || GameData.Int(row, "_iSubType") != 124
                || !row.TryGetProperty("_kIcon", out var icon)
                || !icon.GetString()!.StartsWith("life_icon_badge_", StringComparison.Ordinal)) continue;
            next.HonorBadgeData[id] = new SeasonPageHonorBadgeData
            {
                Id = id, GetTime = clock.GetUtcNow().ToUnixTimeSeconds(),
                GetSeasonId = session.SeasonState!.SeasonId, GetPlayBookId = session.SeasonState.PlayBookId,
            };
            changed = true;
        }
        if (changed)
        {
            var transaction = roles.NewTransaction();
            transaction.RoleBlob(session.Role!.Id, "season_page", next.ToByteArray());
            await roles.CommitAsync(transaction, "season-page/badges", _ => session.SeasonPage = next);
        }
        return changed;
    }

    public async Task LoginAsync(GameSession session)
    {
        var saved = roles.LoadSeasonPage(session.Role!.Id);
        session.SeasonPage = saved == null ? new DBSeasonPage() : DBSeasonPage.Parser.ParseFrom(saved);
        using (await CharacterService.Lock(session))
        {
            bool hadMeta = roles.LoadSeasonPageMeta(session.Role.Id) != null;
            var meta = LoadMeta(session.Role.Id, seasons.SeasonId, saved != null);
            if (meta.SeasonId < seasons.SeasonId) await ArchiveAsync(session, meta);
            else if (meta.SeasonId == seasons.SeasonId) await CaptureBreakSnapshotAsync(session, meta);
            else throw new InvalidDataException("Season page is ahead of the server cycle");
            await AcquireOwnedBadgesAsync(session);
            if (!hadMeta && meta.SeasonId == seasons.SeasonId)
            {
                var transaction = roles.NewTransaction();
                transaction.RoleBlob(session.Role.Id, "season_page_meta", JsonSerializer.SerializeToUtf8Bytes(meta));
                await roles.CommitAsync(transaction, "season-page/seed-meta");
            }
        }
        await SyncAsync(session);
    }

    /// <summary>
    /// Record only an actual authoritative dungeon-score event. Completion alone supplies no score value in this
    /// export. The source must provide a stable event ID; duplicate delivery is harmless (inferred dedupe policy).
    /// </summary>
    public async Task<bool> RecordDungeonScoreAsync(GameSession session, long score, string eventId)
    {
        if (session.Role == null || session.Progress == null || session.SeasonState?.SeasonId != seasons.SeasonId
            || session.SeasonState.PlayBookId != 101 || score <= 0 || string.IsNullOrWhiteSpace(eventId)) return false;
        using var guard = await CharacterService.Lock(session);
        if (session.SeasonState?.SeasonId != seasons.SeasonId || session.SeasonState.PlayBookId != 101) return false;
        var meta = LoadMeta(session.Role.Id, seasons.SeasonId, true);
        if (meta.SeasonId != seasons.SeasonId || !meta.DungeonEvents.Add(eventId)) return false;
        meta.DungeonScore = checked(meta.DungeonScore + score);
        var transaction = roles.NewTransaction();
        transaction.RoleBlob(session.Role.Id, "season_page_meta", JsonSerializer.SerializeToUtf8Bytes(meta));
        await roles.CommitAsync(transaction, "season-page/dungeon-score");
        return true;
    }

    private Dictionary<int, long> Scores(GameSession session, PageMeta meta) => FunctionIds(session).ToDictionary(id => id,
        id => id == 23 ? meta.DungeonScore : ScoreCurrency.TryGetValue(id, out int currency)
            ? Math.Max(0, session.Progress!.Currency.GetValueOrDefault(currency) - meta.Baseline.GetValueOrDefault(currency)) : 0L);

    /// <summary>Day-77 boundary snapshot; off-season activity may still raise the final scores before rollover.</summary>
    private async Task CaptureBreakSnapshotAsync(GameSession session, PageMeta meta)
    {
        if (meta.BreakSnapshotTaken || session.SeasonState?.PlayBookId != 101
            || clock.GetUtcNow().ToUnixTimeSeconds() < seasons.Times().SeasonBreakStartTime) return;
        var nextMeta = JsonSerializer.Deserialize<PageMeta>(JsonSerializer.SerializeToUtf8Bytes(meta))!;
        nextMeta.BreakSnapshotTaken = true;
        var snapshot = new SeasonPageHistoryFunctionScoreData();
        snapshot.FunctionScore.Add(Scores(session, meta).ToDictionary(x => (long)x.Key, x => x.Value));
        var nextPage = session.SeasonPage!.Clone();
        nextPage.HistorySnapshot = snapshot;
        var transaction = roles.NewTransaction();
        transaction.RoleBlob(session.Role!.Id, "season_page", nextPage.ToByteArray());
        transaction.RoleBlob(session.Role.Id, "season_page_meta", JsonSerializer.SerializeToUtf8Bytes(nextMeta));
        await roles.CommitAsync(transaction, "season-page/break-snapshot", _ =>
        {
            session.SeasonPage = nextPage;
            meta.BreakSnapshotTaken = true; // a first-login metadata save preserves the committed snapshot flag
        });
    }

    private async Task ArchiveAsync(GameSession session, PageMeta meta)
    {
        if (meta.SeasonId <= 0 || !seasons.History.TryGetValue(meta.SeasonId, out var old)
            || old.PlayBook != 101 || session.SeasonPage == null)
            throw new InvalidDataException($"Cannot archive unknown season {meta.SeasonId}");
        var scores = new Dictionary<int, long>();
        foreach (int id in data.Row("MultiverseConfig", old.PlayBook)!.Value.GetProperty("_iDanSystem").EnumerateArray()
                     .Select(v => v.GetInt32()))
            scores[id] = id == 23 ? meta.DungeonScore : ScoreCurrency.TryGetValue(id, out int currency)
                ? Math.Max(0, session.Progress!.Currency.GetValueOrDefault(currency) - meta.Baseline.GetValueOrDefault(currency)) : 0;
        var next = session.SeasonPage.Clone();
        var snapshot = new SeasonPageHistoryFunctionScoreData();
        snapshot.FunctionScore.Add(scores.ToDictionary(x => (long)x.Key, x => x.Value));
        next.HistoryFunctionScoreData[meta.SeasonId] = snapshot.Clone();
        next.HistorySnapshot = snapshot;
        var unclaimed = new List<MailService.Attachment>();
        foreach (var (function, score) in scores)
        {
            int level = DanLevel(function, score);
            foreach (var row in Dans(function).Where(r => GameData.Int(r, "_iDanLevel") <= level))
            {
                int dan = (int)GameData.Int(row, "_iDanLevel");
                long group = GameData.Int(row, "_iNormalRewardGroup");
                if (group == 0 || next.RankRewardReceiveInfo.GetValueOrDefault(dan)?.Functions.Contains(function) == true)
                    continue;
                var claimed = next.RankRewardReceiveInfo.GetValueOrDefault(dan);
                if ((claimed?.Functions.Count ?? 0) >= Constant("MultiverseDanRewardLimit")) continue;
                long awardId = ResolveAward(group, session);
                var grants = Award(awardId, session);
                if (grants.Any(g => g.Bind != 0 || g.Count <= 0 || data.Row("ItemConfig", g.Entry) == null))
                    throw new AwardResolutionException($"Unsupported season reissue reward {awardId}");
                unclaimed.AddRange(grants.Select(g => new MailService.Attachment(g.Entry, g.Count)));
                if (claimed == null) next.RankRewardReceiveInfo[dan] = claimed = new FunctionList();
                claimed.Functions.Add(function); // reserve one of the three configured per-dan claims
            }
        }
        next.RankRewardReceiveInfo.Clear();
        next.MailRewardReceiveInfo.Clear();
        var nextMeta = new PageMeta
        {
            SeasonId = seasons.SeasonId,
            Baseline = ScoreCurrency.Values.Distinct().ToDictionary(id => id, id => session.Progress!.Currency.GetValueOrDefault(id))
        };
        void Persist(Ro3Emu.Server.Accounts.Persistence.RoleItemPlanBuilder tx)
        {
            tx.RoleBlob(session.Role!.Id, "season_page", next.ToByteArray());
            tx.RoleBlob(session.Role.Id, "season_page_meta", JsonSerializer.SerializeToUtf8Bytes(nextMeta));
        }
        if (unclaimed.Count == 0)
        {
            var transaction = roles.NewTransaction();
            Persist(transaction);
            await roles.CommitAsync(transaction, "season-page/reissue", _ => session.SeasonPage = next);
        }
        else await mail.SendSeasonReissueAsync(session, 1081, unclaimed,
            $"season-rank-reissue:{session.Role!.Id}:{meta.SeasonId}", Persist,
            () => session.SeasonPage = next);
    }

    private int[] FunctionIds(GameSession session) => data.Row("MultiverseConfig", session.SeasonState?.PlayBookId ?? 0) is { } row
        && row.TryGetProperty("_iDanSystem", out var ids) && ids.ValueKind == JsonValueKind.Array
        ? ids.EnumerateArray().Select(v => v.GetInt32()).ToArray() : [];

    public Dictionary<int, long> Scores(GameSession session) => Scores(session,
        LoadMeta(session.Role!.Id, (int)session.SeasonState!.SeasonId, true));

    private IEnumerable<JsonElement> Dans(int function) => data.Table("MultiverseDan").Values
        .Where(r => GameData.Int(r, "_iFunctionID") == function).OrderBy(r => GameData.Int(r, "_iID"));

    /// <summary>Exactly LC_SeasonHelper.lua:1072-1119: subtract each tier's cost; equality advances a tier.</summary>
    public int DanLevel(int function, long score)
    {
        var rows = Dans(function).ToList();
        for (int i = 0; i < rows.Count; i++)
        {
            if (score < GameData.Int(rows[i], "_iExpLimit") || i == rows.Count - 1)
                return (int)GameData.Int(rows[i], "_iDanLevel");
            score -= GameData.Int(rows[i], "_iExpLimit");
        }
        return 0;
    }

    private int AccessError(GameSession session) => session.Role == null || session.Progress == null || session.SeasonPage == null
        || session.SeasonState?.PlayBookId != 101 || session.SeasonState.SeasonId != seasons.SeasonId
        || FunctionIds(session).Length == 0 ? ErrorCodes.FeatureUnavailable
        : !functions.IsOpen(session, 69) ? ErrorCodes.FunctionLocked : 0; // (inferred) server enforcement of the UI gate.

    public async Task GetDataAsync(GameSession session)
    {
        if (session.Role == null || session.Progress == null)
        {
            await session.SendGameAsync(GetData, new SeasonPageGetDataResp { Result = ErrorCodes.FeatureUnavailable });
            return;
        }
        await seasons.ReconcileAsync(session);
        using var guard = await CharacterService.Lock(session);
        if (await AcquireOwnedBadgesAsync(session)) await SyncAsync(session);
        if (session.SeasonState?.SeasonId == seasons.SeasonId && session.SeasonPage != null)
            await CaptureBreakSnapshotAsync(session, LoadMeta(session.Role.Id, seasons.SeasonId, true));
        var response = new SeasonPageGetDataResp { Result = AccessError(session), StageID = session.SeasonState?.StageId ?? 0 };
        if (response.Result == 0) response.RankScore.Add(Scores(session));
        await session.SendGameAsync(GetData, response);
    }

    /// <summary>
    /// 82902: validate the complete request, then persist claims and grants in one inventory transaction.
    /// Atomic batch refusal and receiveResult = accepted dan-to-function pairs are (inferred); the Lua response
    /// handler is empty, while 5022 is the visible claim authority. Award bindings are retained.
    /// </summary>
    public async Task ClaimAsync(GameSession session, SeasonPageReceiveRankRewardReq request)
    {
        if (session.Role == null || session.Progress == null)
        {
            await session.SendGameAsync(ReceiveRankReward, new SeasonPageReceiveRankRewardResp { Result = ErrorCodes.FeatureUnavailable });
            return;
        }
        using var guard = await CharacterService.Lock(session);
        if (await AcquireOwnedBadgesAsync(session)) await SyncAsync(session);
        int error = AccessError(session);
        var next = session.SeasonPage?.Clone();
        var grants = new List<CharacterService.AttributedGood>();
        if (error == 0 && request.RankReward.Count == 0) error = ErrorCodes.Parameter;
        if (error == 0)
        {
            var scores = Scores(session);
            foreach (var (level, function) in request.RankReward.OrderBy(x => x.Key))
            {
                var row = Dans(function).FirstOrDefault(r => GameData.Int(r, "_iDanLevel") == level);
                if (!scores.TryGetValue(function, out long score) || row.ValueKind != JsonValueKind.Object || level > DanLevel(function, score))
                { error = ErrorCodes.SeasonRankReward; break; }
                var received = next!.RankRewardReceiveInfo.GetValueOrDefault(level);
                if (received?.Functions.Contains(function) == true) { error = ErrorCodes.RewardClaimed; break; }
                if ((received?.Functions.Count ?? 0) >= Constant("MultiverseDanRewardLimit")) { error = ErrorCodes.SeasonRankReward; break; }
                long groupId = GameData.Int(row, "_iNormalRewardGroup");
                long award = ResolveAward(groupId, session);
                var goods = Award(award, session);
                if (goods.Count == 0 || goods.Any(g => g.Count <= 0 || data.Row("ItemConfig", g.Entry) == null))
                { error = ErrorCodes.RewardMissing; break; }
                grants.AddRange(goods.Select(g => new CharacterService.AttributedGood(g.Entry, g.Count, g.Bind, 0, 0)));
                if (received == null) next.RankRewardReceiveInfo[level] = received = new FunctionList();
                received.Functions.Add(function);
            }
        }
        if (error == 0)
        {
            string source = $"season-rank:{session.Role!.Id}:{seasons.SeasonId}:" +
                string.Join(",", request.RankReward.OrderBy(x => x.Key).Select(x => $"{x.Key}-{x.Value}"));
            var economy = character.ProgressionEconomy(session, "cd13c/season-page-claim", [],
                grants.Select(g => (g.Entry, g.Count)).ToArray(),
                new { Season = seasons.SeasonId, Claims = request.RankReward.OrderBy(x => x.Key).ToArray() }, source);
            if (await character.ExchangeLockedAsync(session, [], [], popup: true, sync: false, attributedGoods: grants,
                    persist: tx => { tx.RoleBlob(session.Role!.Id, "season_page", next!.ToByteArray()); tx.WithEconomy(economy); },
                    committed: () => session.SeasonPage = next, asyncCommit: true) == null)
                error = ErrorCodes.BagFull;
            else
            {
                await character.ItemsChangedAsync(session, stats: false);
                await SyncAsync(session);
            }
        }
        var response = new SeasonPageReceiveRankRewardResp { Result = error };
        if (error == 0) response.ReceiveResult.Add(request.RankReward);
        await session.SendGameAsync(ReceiveRankReward, response);
    }

    private long ResolveAward(long id, GameSession session)
    {
        if (data.Row("MultiverseGroupConfig", id) is not { } row || GameData.Int(row, "_itype") != 0) return 0;
        long variant = GameData.Int(row, $"_iValue_{session.SeasonState!.PlayBookId}");
        return variant != 0 ? variant : GameData.Int(row, "_iValue_default");
    }

    /// <summary>Same AwardConfig row-0 fixed triples used by TaskService, plus any configured drop pool.</summary>
    private List<(int Entry, int Count, int Bind)> Award(long id, GameSession session)
    {
        var result = new List<(int Entry, int Count, int Bind)>();
        if (id == 0 || data.Row("AwardConfig", id) is not { ValueKind: JsonValueKind.Array } rows || rows.GetArrayLength() == 0)
            return result;
        if (rows[0].TryGetProperty("_kAward", out var fixedAward) && fixedAward.ValueKind == JsonValueKind.Array)
            foreach (var item in fixedAward.EnumerateArray())
                if (item.ValueKind == JsonValueKind.Array && item.GetArrayLength() >= 2)
                    result.Add((item[0].GetInt32(), item[1].GetInt32(), item.GetArrayLength() > 2 ? item[2].GetInt32() : 0));
        result.AddRange(rewards.RollAwardBound(id, session));
        return result;
    }

    private int Constant(string name) => data.Table("MultiverseConstant").TryGetValue(name, out var value)
        ? (int)(GameData.Number(value) ?? 0) : 0;

    /// <summary>
    /// 82903 changes only an owned honor record; the ID refers to ItemConfig in UI_Com_Tips_GloryPanelCtrl.
    /// UI lines 246-266 enforce MultiverseMedalMaxWear. No badge is granted merely by sending this request.
    /// Original badge award/settlement rules are unproven; persisted awards can be worn/removed independently.
    /// </summary>
    public async Task WearAsync(GameSession session, SeasonPagePutOnHonorBadgeReq request)
    {
        if (session.Role == null || session.Progress == null)
        {
            await session.SendGameAsync(PutOnHonorBadge, new SeasonPagePutOnHonorBadgeResp { Result = ErrorCodes.FeatureUnavailable });
            return;
        }
        using var guard = await CharacterService.Lock(session);
        if (await AcquireOwnedBadgesAsync(session)) await SyncAsync(session);
        int error = AccessError(session);
        SeasonPageHonorBadgeData? badge = null;
        if (error == 0 && (!session.SeasonPage!.HonorBadgeData.TryGetValue(request.Id, out badge)
            || badge.Id != request.Id || data.Row("ItemConfig", request.Id) == null)) error = ErrorCodes.ItemMissing;
        if (error == 0 && request.IsPutOn && !badge!.IsPutOn
            && session.SeasonPage!.HonorBadgeData.Values.Count(b => b.IsPutOn) >= Constant("MultiverseMedalMaxWear"))
            error = ErrorCodes.Parameter; // (inferred) no dedicated honor-wall refusal code was recovered.
        if (error == 0 && badge!.IsPutOn != request.IsPutOn)
        {
            var next = session.SeasonPage!.Clone();
            next.HonorBadgeData[request.Id].IsPutOn = request.IsPutOn;
            var transaction = roles.NewTransaction();
            transaction.RoleBlob(session.Role!.Id, "season_page", next.ToByteArray());
            await roles.CommitAsync(transaction, "season-page/wear", _ => session.SeasonPage = next);
            await SyncAsync(session);
        }
        await session.SendGameAsync(PutOnHonorBadge, new SeasonPagePutOnHonorBadgeResp { Result = error });
    }

    private static Task SyncAsync(GameSession session) => CharacterService.SyncAsync(session, PageSync,
        session.SeasonPage!.Clone(), PrepareEmptyMaps);

    /// <summary>
    /// LC_DAL_Season.lua:262-434 iterates empty rank/history maps without nil guards. LC_PbHelper.lua:1538-1556,
    /// 1926-1958 creates a map before deleting a key. A wire-only tombstone gives an empty Lua table without
    /// inventing a claim, badge or history record (inferred compatibility encoding; no game-client run).
    /// All MultiverseDan._iMailRewardGroup values are zero. MultiverseSettlementTask[101] points to repeatable
    /// type-12 level quests, not a dan-to-mail mapping. No mail is marked delivered while MailService is a stub.
    /// </summary>
    internal static void PrepareEmptyMaps(IMessage message)
    {
        if (message is not DBSeasonPageIncrement { SyncAllMark: true } full) return;
        if (full.RankRewardReceiveInfo.Count == 0) full.RankRewardReceiveInfo[0] = new() { Del = true };
        if (full.MailRewardReceiveInfo.Count == 0) full.MailRewardReceiveInfo[0] = new() { Del = true };
        if (full.HonorBadgeData.Count == 0) full.HonorBadgeData[0] = new() { Del = true };
        if (full.HistoryFunctionScoreData.Count == 0) full.HistoryFunctionScoreData[0] = new() { Del = true };
    }
}

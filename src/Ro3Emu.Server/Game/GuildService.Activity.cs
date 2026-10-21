using System.Globalization;
using System.Text.Json;
using Google.Protobuf;
using Romsg;
using Ro3Emu.Server.Accounts;
using Ro3Emu.Server.Data;
using Ro3Emu.Server.Economy;

namespace Ro3Emu.Server.Game;

/// <summary>
/// Guild Activity, the weekly activity chests, activity dividends and the Fund Rankings.
/// <list type="bullet">
/// <item>Guild quests and other awards grant the guild currencies 104 Funds, 105 Prestige, 106 Activity and 107
/// Contribution to the player (ItemConfig currency subtypes 114/128/129/130). They belong to the guild (UITips 17019:
/// guild quests give Guild Activity and Guild Contribution): after an award the member's balance moves into the guild in
/// one transaction (role currency sink, guild treasury source; activity is a weekly counter, not a balance). (inferred)
/// A guildless role keeps them until it joins.</item>
/// <item>Activity is shared by the guild and resets every Monday 05:00 (UITips 17019); the member's own share drives the
/// dividend grade. The roll is lazy: the next guild write of the new week moves this week to last week.</item>
/// <item>32311: GenericStageRewardsConfig type 9 chests by the guild's activity this week, once a week each, recorded in
/// 5047 activeRewardReceived (LC_DAL_Guild.lua:3760-3790; ActivityCalender :700-765).</item>
/// <item>32300: last week's dividend (GuildSalaryConfig types 1-3, UITips 17015), claimed by hand; the claim is marked
/// in activeRewardReceived under key 0 (no reward row has id 0; inferred storage).</item>
/// <item>32291: every member with the Guild Funds they brought in (Royal Caravan, UITips 17023; deposited 104).</item>
/// </list>
/// </summary>
public sealed partial class GuildService
{
    public const uint TakeActivityBonus = 32300, ReceiveWeekActiveReward = 32311, MoneyRank = 32291;
    private const int FundsItem = 104, PrestigeItem = 105, ActivityItem = 106, ContributionItem = 107, Zeny = 1;
    private static readonly int[] GuildCurrencies = [FundsItem, PrestigeItem, ActivityItem, ContributionItem];
    // ErrorCodeConfig (English): 547 Not enough Guild Activity, 548 Guild Activity Reward claimed, 1511 Invalid Activity
    // Reward ID, 10035 Guild activity dividend reward claimed.
    public const int ErrActivityShort = 547, ErrActivityClaimed = 548, ErrActivityRewardId = 1511, ErrDividendClaimed = 10035;
    private const int ChestType = 9, DividendMarker = 0, DividendFunction = 1333;
    private const long Week = 7 * 86400;

    private readonly HashSet<GameSession> _depositQueued = new();
    private bool _depositHooked;

    /// <summary>Monday 05:00 local of this week, unix seconds (the guild activity reset, UITips 17019).</summary>
    private long WeekStart
    {
        get
        {
            var local = clock.GetLocalNow();
            var day = local.AddHours(-5).Date;
            int sinceMonday = ((int)day.DayOfWeek + 6) % 7;
            return new DateTimeOffset(day.AddDays(-sinceMonday).AddHours(5), local.Offset).ToUnixTimeSeconds();
        }
    }

    // ------------------------------------------------------------------ weekly views and roll

    private long CurActivity(GuildStore.State g) => g.ActivityWeek == WeekStart ? g.CurActivity : 0;
    /// <summary>This week's Guild Activity of a committed guild (the 1031 leaderboard).</summary>
    internal long WeekActivityOf(GuildStore.State g) => CurActivity(g);
    private long LastWeekActivity(GuildStore.State g) => g.ActivityWeek == WeekStart ? g.LastWeekActivity
        : g.ActivityWeek == WeekStart - Week ? g.CurActivity : 0;
    /// <summary>The guild's Guild Activity of the last finished week (Guild League registration, GuildLeagueActive).</summary>
    internal long LastWeekActivityOf(GuildStore.State g) => LastWeekActivity(g);
    private long LastWeekActivity(GuildStore.State g, GuildStore.Member m) => g.ActivityWeek == WeekStart ? m.LastWeekActivity
        : g.ActivityWeek == WeekStart - Week ? m.WeekActivity : 0;

    /// <summary>Moves this week to last week once the week has turned. True when it changed the guild.</summary>
    private bool RollActivity(GuildStore.State g)
    {
        long week = WeekStart;
        if (g.ActivityWeek == week) return false;
        bool previous = g.ActivityWeek == week - Week;
        g.LastWeekActivity = previous ? g.CurActivity : 0;
        g.CurActivity = 0;
        foreach (var m in g.Members.Values)
        {
            m.LastWeekActivity = previous ? m.WeekActivity : 0;
            m.WeekActivity = 0;
        }
        g.ActivityWeek = week;
        return true;
    }

    private GuildActivityInfo ActivityInfo(GuildStore.State g) => new() { CurActivity = CurActivity(g), LastWeekActivity = LastWeekActivity(g) };

    // ------------------------------------------------------------------ deposit of guild currencies

    /// <summary>After any committed award: a member holding guild currencies hands them to the guild.</summary>
    private void HookDeposits()
    {
        if (_depositHooked) return;
        _depositHooked = true;
        character.StateChanged.Add(s =>
        {
            if (s.Role == null || s.Progress is not { } p || !GuildCurrencies.Any(c => p.Currency.GetValueOrDefault(c) > 0)
                || store.ForRole(s.Role.Id) == null || !_depositQueued.Add(s)) return;
            // Detached runtime work: the guild write reserves the guild and its members, which a role-owned post
            // could not widen to (a nested scope may not expand).
            loop.TrackRuntimeDetached("guild/deposit", async () =>
            {
                _depositQueued.Remove(s);
                if (s.Role != null && s.Attachment?.Cancelled != true) await DepositAsync(s);
            });
        });
    }

    public Task DepositAsync(GameSession s) => RunGuildAsync(s, [], [], () => DepositCoreAsync(s), characterGate: true);

    private async Task DepositCoreAsync(GameSession s)
    {
        if (s.Role is not { } role || s.Progress is not { } p || Own(s) is not { } g || !g.Members.TryGetValue(role.Id, out var member)) return;
        var amounts = GuildCurrencies.Select(c => (Entry: c, Count: p.Currency.GetValueOrDefault(c))).Where(x => x.Count > 0).ToArray();
        if (amounts.Length == 0) return;
        long Of(int entry) => amounts.Where(a => a.Entry == entry).Sum(a => a.Count);
        long funds = Of(FundsItem), prestige = Of(PrestigeItem), activity = Of(ActivityItem), contribution = Of(ContributionItem);
        var before = JsonSerializer.SerializeToUtf8Bytes(g);
        RollActivity(g);
        g.Money = checked(g.Money + funds);
        g.Reputation = checked(g.Reputation + prestige);
        g.Contribute = checked(g.Contribute + contribution);
        g.CurActivity = checked(g.CurActivity + activity);
        member.WeekActivity = checked(member.WeekActivity + activity);
        member.ContributeMoney = checked(member.ContributeMoney + funds);

        var roleOwner = new EconomyOwner(EconomyOwnerKind.Role, role.Id.ToString(CultureInfo.InvariantCulture));
        var guildOwner = new EconomyOwner(EconomyOwnerKind.Guild, g.Id.ToString(CultureInfo.InvariantCulture));
        var effects = amounts.Select(a => new EconomyEffect(new EconomyAsset(EconomyAssetKind.Currency, a.Entry), roleOwner,
            EconomyCompartment.Currency, -a.Count, EconomyLegKind.Sink, EconomyReason.GuildActivityCost)).ToList();
        foreach (var (denomination, count) in new[] { (EconomyDenominations.GuildMoney, funds),
            (EconomyDenominations.GuildContribution, contribution), (EconomyDenominations.GuildReputation, prestige) })
            if (count > 0) effects.Add(new(new(EconomyAssetKind.GuildResource, denomination), guildOwner, EconomyCompartment.GuildTreasury,
                count, EconomyLegKind.Source, EconomyReason.GuildResourceGrant));
        string operation = Guid.NewGuid().ToString("N");
        var economy = new EconomyEnvelope(operation, s.Account!, role.Id, "guild-deposit",
            string.Join(",", amounts.Select(a => $"{a.Entry}:{a.Count}")), $"guild-deposit:{g.Id}:{role.Id}:{operation}", NowMs(), effects, []);
        bool ok = false;
        try
        {
            ok = await character.ApplyAwardAsync(s, new AwardPlan(0, 0, []),
                costs: amounts.Select(a => (a.Entry, checked((int)a.Count))).ToList(),
                persist: tx => { store.EnlistSave(g, tx); tx.WithEconomy(economy); }, stateLockHeld: true, suppressRewardNotice: true);
        }
        finally
        {
            if (!ok) Restore(g, before);
        }
        if (!ok) return;
        if (funds > 0 || prestige > 0 || contribution > 0)
            await PushBase(g, new GuildBaseInfoChangeNtf { Tp = GuildBaseInfoChangeNtf.Types.ChangeType.ChangeResource, Resource = Resource(g) });
        if (activity > 0)
            await PushBase(g, new GuildBaseInfoChangeNtf { Tp = GuildBaseInfoChangeNtf.Types.ChangeType.ChangeGuildActivity, Activity = ActivityInfo(g) });
    }

    /// <summary>A refused or failed write leaves the live guild as it was.</summary>
    private static void Restore(GuildStore.State g, byte[] before)
    {
        var old = JsonSerializer.Deserialize<GuildStore.State>(before)!;
        g.Money = old.Money; g.Reputation = old.Reputation; g.Contribute = old.Contribute;
        g.CurActivity = old.CurActivity; g.LastWeekActivity = old.LastWeekActivity; g.ActivityWeek = old.ActivityWeek;
        foreach (var (id, m) in g.Members)
            if (old.Members.TryGetValue(id, out var o))
            { m.WeekActivity = o.WeekActivity; m.LastWeekActivity = o.LastWeekActivity; m.ContributeMoney = o.ContributeMoney; }
    }

    // ------------------------------------------------------------------ 32311 weekly activity chests

    public Task ReceiveWeekRewardAsync(GameSession s, ReceiveGuildWeekActiveRewardReq req) =>
        RunGuildAsync(s, [], [], () => ReceiveWeekRewardCoreAsync(s, req), characterGate: true);

    /// <summary>This week's received chests (older entries expired with the week, UITips 17019).</summary>
    private Dictionary<long, long> Received(DBRoleGuildInfo? personal) => personal?.ActiveRewardReceived
        .Where(r => r.Value >= WeekStart).ToDictionary(r => r.Key, r => r.Value) ?? new();

    private async Task ReceiveWeekRewardCoreAsync(GameSession s, ReceiveGuildWeekActiveRewardReq req)
    {
        int code = 0;
        var g = Own(s);
        var row = data.Table("GenericStageRewardsConfig").Values
            .FirstOrDefault(r => GameData.Int(r, "_iID") == req.RewardId && GameData.Int(r, "_iType") == ChestType);
        if (g == null || s.Guild == null) code = ErrorCodes.NotInGuild;
        else if (row.ValueKind != JsonValueKind.Object) code = ErrActivityRewardId;
        else if (CurActivity(g) < GameData.Int(row, "_iScore")) code = ErrActivityShort;
        else if (Received(s.Guild).ContainsKey(req.RewardId)) code = ErrActivityClaimed;
        if (code == 0)
            code = await ClaimPersonalAsync(s, [GameData.Int(row, "_iAward")], req.RewardId, $"guild-chest/{s.Role!.Id}/{WeekStart}/{req.RewardId}");
        await s.SendGameAsync(ReceiveWeekActiveReward, new ReceiveGuildWeekActiveRewardRes { Result = code, RewardId = req.RewardId });
        if (code == 0) await SyncRole(s);
    }

    /// <summary>One guild-award receipt: the awards and the 5047 marker (key, now) together.</summary>
    private async Task<int> ClaimPersonalAsync(GameSession s, IReadOnlyList<long> awardGroups, long markerKey, string source)
    {
        AwardPlan[] plans;
        try { plans = awardGroups.Select(group => awards.Resolve(s, group)).ToArray(); }
        catch (AwardResolutionException) { return ErrorCodes.RewardMissing; }
        var next = s.Guild!.Clone();
        next.ActiveRewardReceived.Clear();
        foreach (var (key, value) in Received(s.Guild)) next.ActiveRewardReceived[key] = value;
        next.ActiveRewardReceived[markerKey] = markerKey == DividendMarker ? WeekStart : NowSec();
        byte[] bytes = next.ToByteArray();
        long role = s.Role!.Id;
        bool ok = await character.ApplyAwardAsync(s, new AwardPlan(0, 0, plans.SelectMany(p => p.Grants)),
            persist: tx => tx.RoleBlob(role, "guild_info", bytes), stateLockHeld: true, committed: () => s.Guild = next,
            ownedAward: new CharacterService.OwnedAwardSource("cd13e/guild-award", source,
                new { Version = 1, Marker = markerKey, Awards = plans.Select(p => p.AwardId).ToArray() }));
        return ok ? 0 : ErrorCodes.Unknown;
    }

    // ------------------------------------------------------------------ 32300 activity dividends

    /// <summary>
    /// GuildSalaryConfig grade of a value: the row of that type with the highest _kActive minimum the value reaches. The
    /// top rows carry a maximum too (member 5000, daily 2000); a value above it keeps the top grade (inferred).
    /// </summary>
    private JsonElement? Salary(int type, long value) => data.Table("GuildSalaryConfig").Values
        .Where(r => GameData.Int(r, "_iType") == type && r.TryGetProperty("_kActive", out var range) && range.GetArrayLength() >= 1
            && value >= (GameData.Number(range[0]) ?? 0))
        .OrderByDescending(r => GameData.Number(r.GetProperty("_kActive")[0]) ?? 0)
        .Select(r => (JsonElement?)r).FirstOrDefault();

    /// <summary>
    /// Last week's dividend: guild grade (a guild younger than GuildSpecialWeekActiveTime weeks has at least grade
    /// GuildSpecialWeekActiveLevel), the member's own activity grade, and the member's daily-activity grade. (inferred)
    /// This server keeps no weekly daily-activity total, so the third part is its lowest grade. Only a member who was
    /// in the guild when the week turned has one.
    /// </summary>
    private long[] DividendAwards(GuildStore.State g, GuildStore.Member m)
    {
        if (m.EnterTime / 1000 >= WeekStart) return [];
        var guildRow = Salary(1, LastWeekActivity(g));
        long ageWeeks = (NowMs() - g.CreateTime) / (Week * 1000);
        if (ageWeeks < Setting("GuildSpecialWeekActiveTime", 2)[0])
        {
            long protectedId = Setting("GuildSpecialWeekActiveLevel", 2)[0];
            if (guildRow is not { } r || GameData.Int(r, "_iServerRate") > protectedId)
                guildRow = data.Table("GuildSalaryConfig").Values.Where(x => GameData.Int(x, "_iType") == 1
                    && GameData.Int(x, "_iServerRate") == protectedId).Select(x => (JsonElement?)x).FirstOrDefault() ?? guildRow;
        }
        var rows = new[] { guildRow, Salary(2, LastWeekActivity(g, m)), Salary(3, 0) };
        return rows.Where(r => r != null).Select(r => GameData.Int(r!.Value, "_iAward")).Where(a => a > 0).ToArray();
    }

    private long Amount(GameSession s, long[] groups) => groups.Sum(group =>
    {
        try { return awards.Resolve(s, group).Grants.Where(x => x.Entry == Zeny).Sum(x => (long)x.Count); }
        catch (AwardResolutionException) { return 0; }
    });

    /// <summary>32231 tp=14: the dividend line of the panel (one Zeny item, LC_DAL_Guild.lua:3278-3340); the full info has none.</summary>
    private Task PushDividendAsync(GameSession s, GuildStore.State g)
    {
        var info = new GuildBonusInfo();
        if (g.Members.TryGetValue(s.Role!.Id, out var m) && DividendAwards(g, m) is { Length: > 0 } groups && Amount(s, groups) is var zeny and > 0)
        {
            info.BonusItems.Add(new ItemBaseInfo { ItemEntry = Zeny, Stack = checked((int)zeny) });
            info.HasSendActivityBonus = s.Guild?.ActiveRewardReceived.GetValueOrDefault(DividendMarker) == WeekStart;
        }
        return s.SendGameAsync(BaseChange, new GuildBaseInfoChangeNtf { Tp = GuildBaseInfoChangeNtf.Types.ChangeType.ChangeActivityBonus, GuildBonusData = info });
    }

    public Task TakeActivityBonusAsync(GameSession s) => RunGuildAsync(s, [], [], () => TakeActivityBonusCoreAsync(s), characterGate: true);

    private async Task TakeActivityBonusCoreAsync(GameSession s)
    {
        var g = Own(s);
        int code = g == null || s.Guild == null || !g.Members.TryGetValue(s.Role!.Id, out var member) ? ErrorCodes.NotInGuild
            : !functions.IsOpen(s, DividendFunction) ? ErrorCodes.FunctionLocked
            : s.Guild.ActiveRewardReceived.GetValueOrDefault(DividendMarker) == WeekStart ? ErrDividendClaimed : 0;
        long[] groups = code == 0 ? DividendAwards(g!, g!.Members[s.Role!.Id]) : [];
        if (code == 0 && groups.Length == 0) code = ErrorCodes.RewardMissing;
        long zeny = code == 0 ? Amount(s, groups) : 0;
        if (code == 0) code = await ClaimPersonalAsync(s, groups, DividendMarker, $"guild-dividend/{s.Role!.Id}/{WeekStart}");
        var res = new TakeActivityBonusRes { Result = code };
        if (code == 0) res.BaseInfo.Add(new ItemBaseInfo { ItemEntry = Zeny, Stack = checked((int)zeny) });
        await s.SendGameAsync(TakeActivityBonus, res);
        if (code == 0) { await SyncRole(s); await PushDividendAsync(s, g!); }
    }

    // ------------------------------------------------------------------ 32291 Fund Rankings

    public Task MoneyRankAsync(GameSession s) => RunGuildAsync(s, [], [], () => MoneyRankCoreAsync(s));

    /// <summary>Every current member (the client's list update iterates without a nil check; LC_GuildProtoParse.lua:191-209).</summary>
    private Task MoneyRankCoreAsync(GameSession s)
    {
        var res = new GuildMoneyRankResp();
        if (Own(s) is { } g)
            res.PlayerHistoryData.AddRange(g.Members.Values.OrderByDescending(m => m.ContributeMoney).Select(m => new PlayerHistoryInfo
            {
                PlayerId = m.RoleId, ContributeMoney = m.ContributeMoney, Official = m.Official,
                HistoryContribute = (int)Math.Min(int.MaxValue, m.HistoryContribute)
            }));
        return s.SendGameAsync(MoneyRank, res);
    }
}

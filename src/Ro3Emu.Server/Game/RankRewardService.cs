using System.Globalization;
using System.Text.Json;
using Ro3Emu.Server.Accounts;
using Ro3Emu.Server.Accounts.Persistence;
using Ro3Emu.Server.Data;
using Ro3Emu.Server.Data.Config;
using Ro3Emu.Server.Micro;

namespace Ro3Emu.Server.Game;

/// <summary>
/// Leaderboard rank rewards. A LeaderBoardsConfig row with _iRankRewards lists LeaderBoardsGiftConfig rank bands; the
/// rank reward widgets (UI_Sys_RankRewards*, UI_Sys_Rank_TipAwardWidgetCtrl) preview each band's _kReward
/// (MultiverseGroupConfig, GetSeasonAwardPreview) and Lua_CFG_LeaderBoardsGiftConfig.GetGiftIdByGiftList picks the
/// first band whose [first, last] holds a place. _kPrizesTimeNew is the settlement time UI_Sys_RankPowerWidgetCtrl
/// prints: [1, hour] "Daily Ranking Reward Settlement at ${1}" (23716), [2, weekday, hour] weekly (23714). Boards
/// 1011 (Elf Tower race) and 1301 (Fashion Rating) have rewards, both [[1, 5]]: every day at 05:00 realm time.
/// At each settlement the board's places then are paid by its _iMailId letter (1104 / 1057, ${1} the place), once per
/// role, board and settlement (the delivery key). server_state rank_rewards keeps each board's last settlement. (inferred)
/// The board as it stands at that moment is the result (_iRefresh 4 never resets these boards), ties keep the board's
/// own order, a settlement missed while the server was down is paid once at the next start, a board never settled
/// before (a new realm, a new board) starts with its next settlement, and weekday 1 is Monday. Type 3 (after an
/// activity ends) has no rewarded board.
/// </summary>
public sealed partial class RankRewardService(GameData data, RankService ranks, MailService mail, AwardService awards,
    RoleStore roles, AccountStore accounts, SeasonService seasons, GameLoop loop, TimeProvider clock,
    ILogger<RankRewardService> log) : IGameTicker
{
    private const long Daily = 1, Weekly = 2;
    private const string StateKey = "rank_rewards";

    private Dictionary<long, string>? _settled;
    private bool _running;
    private TimeSpan _lastTick = TimeSpan.MinValue;

    /// <summary>A board's standings, best first (tests replace it).</summary>
    internal Func<int, int, Task<IReadOnlyList<(long Id, long Score)>>> Standings { get; set; } = ranks.RoleTopAsync;

    public void Tick(GameTime time)
    {
        if (_running || _lastTick != TimeSpan.MinValue && time.Now - _lastTick < TimeSpan.FromSeconds(5)) return;
        _lastTick = time.Now;
        var settled = _settled ??= Load();
        var due = Due().Where(d => settled.GetValueOrDefault(d.Board.Id) != d.Period).ToList();
        if (due.Count == 0) return;
        _running = true;
        loop.PostRuntime(async () =>
        {
            try
            {
                foreach (var (board, period) in due)
                {
                    // A board never settled here starts with its next settlement instead of paying the current board.
                    if (settled.ContainsKey(board.Id) && !await SettleAsync(board, period)) continue;
                    settled[board.Id] = period;
                    using var scope = loop.Owners is { } owners
                        ? await owners.EnterAsync("rank reward state", Ownership.ReservationKey.Entity("server_state", StateKey)) : null;
                    await accounts.WriteLegacyAsync<int>(new SeasonStateWritePlan(StateKey, JsonSerializer.Serialize(settled)));
                }
            }
            finally { _running = false; }
        });
    }

    private Dictionary<long, string> Load() =>
        accounts.ReadSync<string?>(new SeasonStateReadPlan(StateKey)) is { Length: > 0 } saved
            ? JsonSerializer.Deserialize<Dictionary<long, string>>(saved) ?? [] : [];

    /// <summary>Each rewarded board with the key of its latest settlement time not after now.</summary>
    internal IEnumerable<(LeaderBoardsConfig Board, string Period)> Due()
    {
        var local = TimeZoneInfo.ConvertTime(clock.GetUtcNow(), clock.LocalTimeZone).DateTime;
        foreach (var board in LeaderBoardsConfig.All(data).Where(b => b.RankRewards.Length > 0 && b.MailId > 0))
            if (LastSettlement(board.PrizesTime, local) is { } at)
                yield return (board, at.ToString("yyyyMMddHH", CultureInfo.InvariantCulture));
    }

    /// <summary>The latest settlement time of <paramref name="times"/> at or before <paramref name="local"/>.</summary>
    internal static DateTime? LastSettlement(long[][] times, DateTime local)
    {
        DateTime? latest = null;
        foreach (var time in times)
        {
            DateTime? at = time switch
            {
                [Daily, >= 0 and < 24, ..] => Back(local.Date.AddHours(time[1]), local, 1),
                [Weekly, >= 1 and <= 7, >= 0 and < 24, ..] => Weekday(local, (int)time[1], (int)time[2]),
                _ => null,
            };
            if (at is { } found && (latest == null || found > latest)) latest = found;
        }
        return latest;
    }

    private static DateTime Weekday(DateTime local, int weekday, int hour)
    {
        int back = ((int)local.DayOfWeek - weekday % 7 + 7) % 7; // weekday 7 is Sunday (DayOfWeek 0)
        return Back(local.Date.AddDays(-back).AddHours(hour), local, 7);
    }

    /// <summary>A time of the current period, or the period before when it is still ahead.</summary>
    private static DateTime Back(DateTime at, DateTime local, int days) => at <= local ? at : at.AddDays(-days);

    /// <summary>Mails one settlement of a board; false when a letter failed (retried at the next tick).</summary>
    internal async Task<bool> SettleAsync(LeaderBoardsConfig board, string period)
    {
        var bands = board.RankRewards.Select(id => LeaderBoardsGiftConfig.Get(data, id)).OfType<LeaderBoardsGiftConfig>()
            .Where(b => b.ParaNum.Length >= 2 && b.Reward > 0).ToList();
        int last = bands.Count == 0 ? 0 : (int)Math.Min(bands.Max(b => b.ParaNum[1]), 10_000);
        if (last <= 0) return true;
        var standings = await Standings((int)board.Id, last);
        for (int i = 0; i < standings.Count; i++)
        {
            int place = i + 1;
            if (bands.FirstOrDefault(b => b.ParaNum[0] <= place && place <= b.ParaNum[1]) is not { Reward: > 0 } band) continue;
            if (!await MailAsync(board, period, standings[i].Id, place, band.Reward)) return false;
        }
        return true;
    }

    private async Task<bool> MailAsync(LeaderBoardsConfig board, string period, long role, int place, long reward)
    {
        if (roles.Get(role) == null) return true;
        IReadOnlyList<AwardGrant> goods;
        try { goods = awards.ResolveForProgress(roles.LoadProgress(role), reward, seasons.PlayBook).Grants; }
        catch (AwardResolutionException e)
        {
            LogAwardMissing(log, e, board.Id, reward);
            return true; // nothing payable; retrying would not change the data
        }
        try
        {
            var attachments = goods.GroupBy(g => (g.Entry, g.BindType))
                .Select(g => new MailService.Attachment(g.Key.Entry, checked(g.Sum(x => x.Count)), g.Key.BindType)).ToArray();
            if (attachments.Length == 0) return true;
            using var scope = loop.Owners is { } owners
                ? await owners.EnterAsync("rank reward mail", Ownership.ReservationKey.Role(role)) : null;
            await mail.SendTemplateOnceAsync(role, (int)board.MailId, attachments,
                $"rank-reward:{board.Id}:{period}:{role}", _ => { }, scope?.Context, contentParams: [place]);
            return true;
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            LogMailFailed(log, e, board.Id, period, role);
            return false;
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Rank reward of board {Board}: award {Reward} does not resolve")]
    private static partial void LogAwardMissing(ILogger logger, Exception error, long board, long reward);

    [LoggerMessage(Level = LogLevel.Error, Message = "Rank reward of board {Board} ({Period}) to role {Role} failed")]
    private static partial void LogMailFailed(ILogger logger, Exception error, long board, string period, long role);
}

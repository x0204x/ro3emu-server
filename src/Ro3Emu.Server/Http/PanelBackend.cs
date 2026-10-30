using System.Globalization;
using System.Net;
using System.Reflection;
using Microsoft.Extensions.Options;
using Ro3Emu.Panel;
using Ro3Emu.Server.Accounts;
using Ro3Emu.Server.Accounts.Persistence;
using Ro3Emu.Server.Data;
using Ro3Emu.Server.Data.Config;
using Ro3Emu.Server.Game;
using Ro3Emu.Server.Micro;

namespace Ro3Emu.Server.Http;

/// <summary>
/// The server's side of the web panel (Ro3Emu.Panel, docs/guide/panel.md). Reads go through the stores' read pool or
/// the loop's census; writes through the store's writer, in one transaction with their audit row; whatever touches a
/// live session (the ban's kick, the online list) runs on the game loop. Nothing here writes the database directly.
/// </summary>
public sealed partial class PanelBackend(IOptions<ServerOptions> options, AccountStore accounts, RankService ranks, GameData data,
    GameLoop loop, SessionRegistry sessions, ReconnectService reconnect, Diagnostics.SessionCensus census,
    Diagnostics.ServerMetrics metrics, CredentialLimiter limiter, TimeProvider clock) : IPanelBackend
{
    /// <summary>JobConfig 1000, Novice: the root of every class line (TrainingService.RootJob); its children are the classes.</summary>
    private const long Novice = 1000;
    private const int BoardSize = 100;
    /// <summary>(inferred) How stale the public online list and the level board may be: one loop visit per interval,
    /// whatever the traffic.</summary>
    private static readonly TimeSpan OnlineTtl = TimeSpan.FromSeconds(5), LevelBoardTtl = TimeSpan.FromSeconds(30);

    /// <summary>The boards on the rankings page: the level board read here, the others RankService's own.</summary>
    private static readonly (PanelBoardInfo Info, int RankBoard)[] BoardList =
    [
        (new("level", "Base level", "Job level", Guild: false, ByClass: true), 0),
        (new("power", "Hero Registry", "Power", Guild: false, ByClass: true), RankService.HeroAll),
        (new("tower", "Spirit Tower", "Floor", Guild: false, ByClass: false), RankService.SpiritTower),
        (new("fashion", "Fashion", "Fashion", Guild: false, ByClass: false), RankService.Fashion),
        (new("survivor", "Survivor Battle", "Points", Guild: false, ByClass: false), RankService.SurvivorOverall),
        (new("dragon", "Dragon Island", "Points", Guild: false, ByClass: false), RankService.DragonOverall),
        (new("merit", "Battle Merit", "Merit", Guild: false, ByClass: false), RankService.BattleGlory),
        (new("guild-prestige", "Guild prestige", "Prestige", Guild: true, ByClass: false), RankService.GuildPrestige),
        (new("guild-activity", "Guild activity this week", "Activity", Guild: true, ByClass: false), RankService.GuildActivity),
    ];

    private readonly object _gate = new();
    private Task<OnlineRow[]>? _online;
    private DateTimeOffset _onlineAt;
    private Task<List<PanelRoleRow>>? _levels;
    private DateTimeOffset _levelsAt;
    private Task<(int, int)>? _counts;
    private DateTimeOffset _countsAt;

    public PanelServerInfo Info { get; } = new(options.Value.ClusterName, options.Value.AreaName, ServerVersion(), clock.GetUtcNow());

    public bool Accepting => loop.Phase == GameLoopPhase.Accepting;

    public bool TryAcquireCredentialCheck(IPAddress? remote) => limiter.TryAcquire(remote);

    public bool IsAdmin(string account) => options.Value.GmAccounts.Contains(account, StringComparer.Ordinal);

    public int PlayersOnline() => census.Current.Roles.Count;

    /// <summary>Read at most once per <see cref="LevelBoardTtl"/>: the home page asks on every visit.</summary>
    public async Task<PanelCounts> CountsAsync()
    {
        Task<(int, int)> task;
        lock (_gate)
        {
            var now = clock.GetUtcNow();
            if (_counts == null || _counts.IsFaulted || _counts.IsCompleted && now - _countsAt >= LevelBoardTtl)
            {
                _counts = accounts.ReadAsync<(int, int)>(new PanelCountsRead());
                _countsAt = now;
            }
            task = _counts;
        }
        var (accountCount, characterCount) = await task;
        return new PanelCounts(accountCount, characterCount);
    }

    private long Now => clock.GetUtcNow().ToUnixTimeSeconds();

    /// <summary>The build's version (tools/release/pack.ps1 -Version), without the source revision.</summary>
    private static string ServerVersion()
    {
        string version = typeof(ServerHost).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "";
        int plus = version.IndexOf('+');
        return plus >= 0 ? version[..plus] : version;
    }

    public async Task<IReadOnlyList<PanelNewsItem>> NewsAsync(int limit) =>
        [.. (await accounts.ReadAsync<List<PanelNewsRow>>(new PanelNewsRead(limit, Now)))
            .Select(n => new PanelNewsItem(n.Id, n.Title, n.Body, n.Tag, DateTimeOffset.FromUnixTimeSeconds(n.PublishedUtc), n.Author, n.Pinned))];

    // ---------------------------------------------------------------- who is online

    private sealed record OnlineRow(long RoleId, string Name, int Career, int Level, long Scene, string Account);

    public async Task<IReadOnlyList<PanelOnlineRole>> OnlineAsync() =>
        [.. (await OnlineRowsAsync()).OrderBy(r => r.Name, StringComparer.OrdinalIgnoreCase)
            .Select(r => new PanelOnlineRole(r.Name, ClassName(r.Career), r.Level, MapName(r.Scene), r.Account, ClassIcon(r.Career), r.Scene))];

    /// <summary>The roles in the world, read on the loop where sessions change, at most once per <see cref="OnlineTtl"/>.</summary>
    private async Task<OnlineRow[]> OnlineRowsAsync()
    {
        Task<OnlineRow[]> task;
        lock (_gate)
        {
            var now = clock.GetUtcNow();
            if (_online == null || _online.IsFaulted || _online.IsCanceled || _online.IsCompleted && now - _onlineAt >= OnlineTtl)
            {
                _online = loop.InvokeAsync(() => Task.FromResult(sessions.All()
                    .Select(s => s.Role is { } role
                        ? new OnlineRow(role.Id, role.Name, s.Progress?.Career ?? role.Career, s.Progress?.Level ?? role.Level, s.SceneEntry, s.Account ?? "")
                        : null)
                    .OfType<OnlineRow>().ToArray()));
                _onlineAt = now;
            }
            task = _online;
        }
        try { return await task; }
        catch (OperationCanceledException) { return []; } // the loop no longer admits work: the server is stopping
    }

    // ---------------------------------------------------------------- rankings

    public IReadOnlyList<PanelBoardInfo> Boards() => [.. BoardList.Select(b => b.Info)];

    public IReadOnlyList<PanelClass> Classes() =>
        [.. JobConfig.All(data).Where(j => j.ParentId == Novice).OrderBy(j => j.Id).Select(j => new PanelClass(j.Id, ClassName(j.Id), ClassIcon(j.Id)))];

    public async Task<PanelBoard?> BoardAsync(string board, long rootClass)
    {
        var entry = BoardList.FirstOrDefault(b => b.Info.Id == board);
        if (entry.Info == null) return null;
        if (!entry.Info.ByClass) rootClass = 0;
        IEnumerable<PanelBoardRow> rows = entry.RankBoard == 0 ? await LevelRowsAsync(rootClass)
            : entry.Info.Guild ? await GuildRowsAsync(entry.RankBoard) : await RoleRowsAsync(entry.RankBoard, rootClass);
        return new PanelBoard(entry.Info, [.. rows.Take(BoardSize).Select((r, i) => r with { Rank = i + 1 })]);
    }

    private bool InClass(int career, long rootClass) => rootClass == 0 || TrainingService.RootJob(data, career) == rootClass;

    private async Task<IEnumerable<PanelBoardRow>> LevelRowsAsync(long rootClass)
    {
        Task<List<PanelRoleRow>> task;
        lock (_gate)
        {
            var now = clock.GetUtcNow();
            if (_levels == null || _levels.IsFaulted || _levels.IsCompleted && now - _levelsAt >= LevelBoardTtl)
            {
                // Enough rows that a class filter still fills its board on a realm of a few thousand characters.
                _levels = accounts.ReadAsync<List<PanelRoleRow>>(new PanelRolesRead(null, 3000));
                _levelsAt = now;
            }
            task = _levels;
        }
        return (await task).Where(r => InClass(r.Career, rootClass))
            .Select(r => new PanelBoardRow(0, r.Name, ClassName(r.Career), r.Level, r.JobLevel, ClassIcon(r.Career)));
    }

    /// <summary>A character board of RankService, its rows named from the role table; deleted characters drop out.</summary>
    private async Task<IEnumerable<PanelBoardRow>> RoleRowsAsync(int board, long rootClass)
    {
        var top = await ranks.TopAsync(board, rootClass == 0 ? BoardSize : 3000);
        var names = await accounts.ReadAsync<Dictionary<long, (string Name, int Career, int Level)>>(new PanelRoleNamesRead([.. top.Select(t => t.Id)]));
        return top.Where(t => names.ContainsKey(t.Id) && InClass(names[t.Id].Career, rootClass))
            .Select(t => new PanelBoardRow(0, names[t.Id].Name, ClassName(names[t.Id].Career), names[t.Id].Level, t.Score, ClassIcon(names[t.Id].Career)));
    }

    private async Task<IEnumerable<PanelBoardRow>> GuildRowsAsync(int board)
    {
        var top = await ranks.TopAsync(board, BoardSize);
        var guilds = (await accounts.ReadAsync<List<GuildStore.State>>(new GuildReadPlan(GuildReadKind.List))).ToDictionary(g => g.Id);
        return top.Where(t => guilds.ContainsKey(t.Id)).Select(t => new PanelBoardRow(0, guilds[t.Id].Name, "", guilds[t.Id].Level, t.Score));
    }

    // ---------------------------------------------------------------- names

    /// <summary>The class's English name (JobConfig._iName), or its id when the data has none.</summary>
    private string ClassName(long career) =>
        JobConfig.Get(data, career)?.Name is [var text, ..] && data.Text(text) is { Length: > 0 } name
            ? name : "Class " + career.ToString(CultureInfo.InvariantCulture);

    private string MapName(long scene) =>
        data.MapName(scene) is { Length: > 0 } name ? name : "Map " + scene.ToString(CultureInfo.InvariantCulture);
}

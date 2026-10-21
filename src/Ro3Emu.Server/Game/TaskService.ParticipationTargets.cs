using System.Runtime.CompilerServices;

namespace Ro3Emu.Server.Game;

/// <summary>
/// Event sources of the participation and state targets declared in TaskObjectives.cs (growth emblem titles 3-6, the
/// side quests and the To-Do / guild activity tracking quests). The owning services raise their committed outcome; the
/// progress runs on the role's owner loop afterwards (PostOwned), outside the source's own locks, except for the dungeon
/// clear, which already runs where DungeonService awaits its Completed handlers (as 101018 does).
/// </summary>
public sealed partial class TaskService
{
    /// <summary>TaskConfig type 16, the guild activity quests (1601010-1603042), and 23, the Adventure Party quests.</summary>
    private const int GuildQuestType = 16, AdventureGroupQuestType = 23;

    private readonly ConditionalWeakTable<Ownership.AttachmentLease, StrongBox<long>> _friendCounts = new();

    /// <summary>The size of the role's friend list (FriendService relation via SocialOwnerCache), null when unknown.</summary>
    public Func<GameSession, Task<long?>>? LoadFriendCount { get; set; }

    /// <summary>103019: the friend count read for this attachment, -1 (keep the stored value) before the first read.</summary>
    private long FriendCount(GameSession s) =>
        s.Attachment is { } attachment && _friendCounts.TryGetValue(attachment, out var count) ? count.Value : -1;

    /// <summary>After login and every relation change: re-read the friend list, then the snapshot targets.</summary>
    public void QueueFriendCount(GameSession s)
    {
        if (s.Tasks == null || LoadFriendCount is not { } load) return;
        _loop.PostOwned(s, async () =>
        {
            var attachment = s.Attachment;
            if (attachment == null || s.Tasks == null || s.Role == null) return;
            if (await load(s) is not { } count || !ReferenceEquals(s.Attachment, attachment)) return;
            _friendCounts.AddOrUpdate(attachment, new StrongBox<long>(count));
            await RefreshAsync(s);
        }, "quest friends");
    }

    /// <summary>FriendService.RelationChanged: the role's friend list changed.</summary>
    public void OnRelationChanged(long role)
    {
        if (_sessions.All().FirstOrDefault(s => s.Role?.Id == role) is { } session) QueueFriendCount(session);
    }

    /// <summary>A state change outside CharacterService (guild membership): re-read the snapshot targets.</summary>
    public void OnStateChanged(GameSession s) => QueueRefresh(s);

    /// <summary>
    /// 106602: a committed successful clear of the TenGameplay boss <paramref name="boss"/> by <paramref name="party"/>
    /// (DungeonService.MultiBossCleared). Guild and Adventure Party quests also need a fellow member in that party.
    /// </summary>
    public async Task OnTenBossClearedAsync(GameSession s, long boss, IReadOnlyCollection<GameSession> party)
    {
        using var gate = await LockTasksAsync(s);
        if (BeforeProgress is { } before) await before(s);
        long guild = s.Guild?.GuildId ?? 0, group = s.AdventureGroupRole?.AdventureGroupId ?? 0;
        bool guildMate = guild > 0 && party.Any(o => !ReferenceEquals(o, s) && o.Guild?.GuildId == guild);
        bool groupMate = group > 0 && party.Any(o => !ReferenceEquals(o, s) && o.AdventureGroupRole?.AdventureGroupId == group);
        await ProgressCoreAsync(s, TTenBossWin, t => t is [TTenBossWin, _, _, ..] && t[2..].Contains(boss) ? 1 : 0,
            def => def.Type switch { GuildQuestType => guildMate, AdventureGroupQuestType => groupMate, _ => true });
    }

    /// <summary>106802: one committed dig at the Treasure Map's point.</summary>
    public void OnTreasureDug(GameSession s) =>
        _loop.PostOwned(s, () => ProgressAsync(s, TTreasureDig, t => t.Length == 2 ? 1 : 0), "quest treasure dig");

    /// <summary>103014: present at a settled Ilmata (Guild Challenge) stage attempt.</summary>
    public void OnGveJoined(GameSession s) =>
        _loop.PostOwned(s, () => ProgressAsync(s, TGveJoin, t => t.Length == 2 ? 1 : 0), "quest gve");

    /// <summary>
    /// 106603 / 106019: one committed Guild League match of <paramref name="round"/> (1 preliminaries, 2 semifinals,
    /// 3 finals); <paramref name="scores"/> holds each fighter's personal score in it.
    /// </summary>
    public void OnGuildLeagueMatch(int round, IReadOnlyDictionary<long, long> scores)
    {
        foreach (var s in _sessions.All().Where(s => s.Role != null && scores.ContainsKey(s.Role.Id)).ToArray())
        {
            long score = scores[s.Role!.Id];
            _loop.PostOwned(s, () => ProgressAsync(s, null, t => t switch
            {
                [TLeagueJoin, var stage, _] when stage == 0 || stage == round => 1,
                [TLeagueScore, var stage, var points, _] when stage + 1 == round && score > points => 1,
                _ => 0,
            }), "quest guild league");
        }
    }

    /// <summary>106611 / 106605 / 106606: a settled siege the role was loaded into, its personal score and whether its
    /// guild won.</summary>
    public void OnSiegeSettled(long role, long score, bool won)
    {
        if (_sessions.All().FirstOrDefault(s => s.Role?.Id == role) is not { } session) return;
        _loop.PostOwned(session, () => ProgressAsync(session, null, t => t switch
        {
            [TSiegeScore, var points, _] when score >= points => 1,
            [TGvgJoin, _] => 1,
            [TGvgWin, _] when won => 1,
            _ => 0,
        }), "quest siege");
    }

    /// <summary>317002: levels gained by one committed Soul Echo upgrade.</summary>
    public void OnEchoUpgraded(GameSession s, int levels)
    {
        if (levels <= 0) return;
        _loop.PostOwned(s, () => ProgressAsync(s, TEchoUpgrade, t => t.Length == 2 ? levels : 0), "quest echo upgrade");
    }

    /// <summary>101007 / 101010: one settled Spirit Tower floor attempt, and whether it was won.</summary>
    public void OnTowerSettled(GameSession s, bool won) =>
        _loop.PostOwned(s, () => ProgressAsync(s, null, t => t switch
        {
            [TTowerJoin, _] => 1,
            [TTowerWin, _] when won => 1,
            _ => 0,
        }), "quest tower");

    /// <summary>106902: one committed like of another role's profile.</summary>
    public void OnProfileLiked(GameSession s) =>
        _loop.PostOwned(s, () => ProgressAsync(s, TLike, t => t.Length == 2 ? 1 : 0), "quest like");

    /// <summary>106018: the member is at the Bonfire Party (once per event day, GuildCampfireService).</summary>
    public void OnCampfireJoined(GameSession s) =>
        _loop.PostOwned(s, () => ProgressAsync(s, TCampfire, t => t.Length == 2 ? 1 : 0), "quest campfire");
}

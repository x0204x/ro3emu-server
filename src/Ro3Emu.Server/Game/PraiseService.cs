using Romsg;

namespace Ro3Emu.Server.Game;

/// <summary>
/// MVP likes: 82585 MULTI_DUNGEON_PRAISE / 82586 MULTI_DUNGEON_PRAISE_NOTIFY. The result screens send the liked roles
/// (LC_DunGeon_Helper.MultiDungeonPraiseReq): a dungeon's MVP and role widgets (UI_Sys_Dungeon_MvpWidgetCtrl._onLikeAll,
/// UI_Sys_Dungeon_RoleWidgetCtrl._onClickLike), Survivor Battle's data panel and Dragon Island's best players
/// (UI_Sys_Chicken_LikeItemWidgetCtrl, UI_Sys_DragonIsland_BestWidgetCtrl). LC_DAL_MultiPlayerDungeon:
/// <list type="bullet">
/// <item>the response's praiseResult maps each target to a code; one target with a nonzero code pops 56180 "You've
/// already given this a Like", several pop 56182 "You've used all your Likes" (_processLikePlayer);</item>
/// <item>the notify replaces the DungeonLikeMap asset: praiseInfo = likes each role received, likePlayer = whom the
/// recipient liked, praisedPlayerIds = who liked the recipient (mutual-like flags, _calcLikeDiff), praisePlayerId =
/// the liker of this event, which LC_DunGeon_Base.OnLikeByOtherTips names in a tip.</item>
/// </list>
/// A like is once per target per run; the client tables define no daily like limit for this (PersonalLikeCD and
/// DragonLike* belong to the profile card and the Starshatter Dragon ritual). A run's board is the group the role last
/// played with (a dungeon run or a timed battle match) and lasts two hours (inferred).
/// </summary>
public sealed class PraiseService(SessionRegistry sessions, TimeProvider clock)
{
    public const uint Praise = 82585, PraiseNotify = 82586;
    // ErrorCodeConfig 1521 "You cannot like or assist yourself", 1522 "Unable to like right now", 1523 "Already liked".
    public const int ErrSelf = 1521, ErrUnavailable = 1522, ErrAlreadyLiked = 1523;
    private static readonly TimeSpan Lifetime = TimeSpan.FromHours(2);

    private sealed class Board
    {
        public required long Id;
        public required int Scene;
        public required HashSet<long> Members;
        public required DateTimeOffset Created;
        /// <summary>Target role -> the roles that liked it.</summary>
        public Dictionary<long, HashSet<long>> LikedBy { get; } = new();
    }

    private readonly Dictionary<long, Board> _boards = new();
    private readonly Dictionary<long, long> _latest = new();

    /// <summary>A group run started: its members can like each other from now on (until their next run).</summary>
    public void Remember(long id, long scene, IEnumerable<long> members)
    {
        var now = clock.GetUtcNow();
        foreach (var stale in _boards.Values.Where(b => now - b.Created > Lifetime).Select(b => b.Id).ToArray()) _boards.Remove(stale);
        var board = new Board { Id = id, Scene = (int)scene, Members = members.ToHashSet(), Created = now };
        _boards[id] = board;
        foreach (long role in board.Members) _latest[role] = id;
        foreach (long role in _latest.Where(p => !_boards.ContainsKey(p.Value)).Select(p => p.Key).ToArray()) _latest.Remove(role);
    }

    private Board? BoardOf(long role) => _latest.TryGetValue(role, out long id) && _boards.TryGetValue(id, out var board)
        && clock.GetUtcNow() - board.Created <= Lifetime ? board : null;

    public async Task PraiseAsync(GameSession s, MultiDungeonPraiseReq req)
    {
        long me = s.Role?.Id ?? 0;
        var board = me == 0 ? null : BoardOf(me);
        var response = new MultiDungeonPraiseResp { Result = board == null ? ErrUnavailable : 0 };
        var liked = new List<long>();
        if (board != null)
            foreach (long target in req.TargetPlayerIds.Distinct().Take(200)) // no run holds more (2220104 _iRoleCntLimit 160)
            {
                int code = target == me ? ErrSelf : !board.Members.Contains(target) ? ErrUnavailable
                    : board.LikedBy.GetValueOrDefault(target)?.Contains(me) == true ? ErrAlreadyLiked : 0;
                if (code == 0)
                {
                    if (!board.LikedBy.TryGetValue(target, out var likers)) board.LikedBy[target] = likers = new();
                    likers.Add(me);
                    liked.Add(target);
                }
                response.PraiseResult[target] = code;
            }
        await s.SendGameAsync(Praise, response);
        if (board == null || liked.Count == 0) return;
        foreach (var member in sessions.All().Where(p => p.Role != null && board.Members.Contains(p.Role.Id)).ToArray())
        {
            long id = member.Role!.Id;
            var notify = new MultiDungeonPraiseNotify { DungeonId = board.Scene, PraisePlayerId = liked.Contains(id) ? me : 0 };
            foreach (long role in board.Members) notify.PraiseInfo[role] = board.LikedBy.GetValueOrDefault(role)?.Count ?? 0;
            foreach (var (target, likers) in board.LikedBy) if (likers.Contains(id)) notify.LikePlayer[target] = true;
            notify.PraisedPlayerIds.Add(board.LikedBy.GetValueOrDefault(id)?.Order() ?? Enumerable.Empty<long>());
            try { await member.SendGameAsync(PraiseNotify, notify); }
            catch (Exception e) when (e is IOException or ObjectDisposedException or System.Net.Sockets.SocketException) { }
        }
    }
}

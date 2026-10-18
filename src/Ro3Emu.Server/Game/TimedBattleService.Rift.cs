using Romsg;
using System.Globalization;
using System.Text.Json;
using Ro3Emu.Server.Data;
using Ro3Emu.Server.Maps;

namespace Ro3Emu.Server.Game;

/// <summary>
/// Rift Crisis stones (OnePiece, DailyActivityConfig[200]): "Every day from 10:00-22:00, Rift Gates randomly appear on
/// various maps every two hours" (text 68001). Each round places stones on OnePieceMapConfig maps; a stone carries its
/// star and fight scene as CustomData 4900700, a party clears it once, and a cleared stone leaves the map.
/// Inferred policies (no server data): a round lasts two hours from the window start; it uses
/// PublicParamConfig.OnePieceMapRefresh [3,4] maps picked by _iMapWeight; every resolvable _kMonsterPoint of a picked
/// map gets one stone; its star is drawn by the map's _kStarWeight among stars whose OnePieceMonsterConfig
/// _iRefreshCount budget is left; its scene by that star's _kDungeonList weights. Stars 2-5 fight the next variant of
/// the scene's boss (NPCConfig 16000001-16000005 share a name and scale 80%-166%); star 6 scenes have one boss each.
/// One stone set serves every line of a map.
/// </summary>
public sealed partial class TimedBattleService
{
    private const int RiftRoundSeconds = 7200;
    private const long StarNpcInfo = 4900700;
    /// <summary>ChatConfig 9001 "A Rift Stone has appeared on Channel ${2} in ${1}" / 9002 "...resolving this Rift Crisis."</summary>
    private const int RiftOpenChat = 9001, RiftClosedChat = 9002;
    private const uint SystemBroadcast = 1022;
    /// <summary>Below 2^53 (unit ids are doubles in the client) and above the per-role treasure hunt NPCs at 2^46 + role id.</summary>
    private const long RiftUidBase = 1L << 46 | 1L << 45;

    internal sealed class RiftStone
    {
        public required long Uid { get; init; }
        public required long Map { get; init; }
        public required MapPoint Point { get; init; }
        /// <summary>OnePieceMonsterConfig key and NPCConfig stone, 16100001-16100006.</summary>
        public required int StoneNpc { get; init; }
        public required int Star { get; init; }
        /// <summary>OnePieceRewardConfig key, the 30400 sceneEntry.</summary>
        public required long Scene { get; init; }
        public required int Boss { get; init; }
        public GameSession? Challenger { get; set; }
    }

    private readonly Dictionary<long, RiftStone> riftStones = new();
    private readonly Dictionary<long, int> riftMapTotals = new();
    private long riftRound = -1, riftSeq;
    /// <summary>The round's 9002 went out: its last stone was cleared.</summary>
    private bool riftResolved;

    internal IReadOnlyCollection<RiftStone> RiftStones => riftStones.Values;

    /// <summary>Tests: the running round keeps only the stones <paramref name="keep"/> accepts.</summary>
    internal void KeepRiftStonesForTest(Func<RiftStone, bool> keep)
    {
        foreach (var stone in riftStones.Values.Where(s => !keep(s)).ToArray()) riftStones.Remove(stone.Uid);
    }

    internal static bool IsRiftUid(long uid) => uid >= RiftUidBase && uid < 1L << 47;

    /// <summary>Unix seconds of the running round's start, or -1 outside the window.</summary>
    private long CurrentRiftRound()
    {
        if (!schedule.IsOpen(200)) return -1;
        var (start, end) = TreasureWindow();
        long now = time.GetUtcNow().ToUnixTimeSeconds();
        return now < start || now >= end ? -1 : start + (now - start) / RiftRoundSeconds * RiftRoundSeconds;
    }

    private long RiftRoundEnd()
    {
        var (start, end) = TreasureWindow();
        long round = riftRound >= 0 ? riftRound : start;
        return Math.Min(end, round + RiftRoundSeconds);
    }

    private void TickRift()
    {
        long round = CurrentRiftRound();
        if (round == riftRound) return;
        riftRound = round;
        riftResolved = false;
        var removed = riftStones.Values.ToArray();
        riftStones.Clear();
        riftMapTotals.Clear();
        if (round >= 0) PlaceRiftRound(Random.Shared);
        var added = riftStones.Values.ToArray();
        // A round change also refreshes RestrictInfo1020 once the restriction day has turned.
        foreach (var session in sessions.All().Where(s => s.Role != null))
            loop.PostOwned(session, async () =>
            {
                await RiftViewAsync(session, removed, added);
                await SendOnePieceMapAsync(session);
                await SyncRiftRestrictAsync(session);
            });
        if (added.Length > 0)
            foreach (long map in added.Select(s => s.Map).Distinct())
                loop.PostRuntime(() => RiftChatAsync(RiftOpenChat, data.ClientMapName(map), "1"));
    }

    private void PlaceRiftRound(Random rng)
    {
        var maps = data.Table("OnePieceMapConfig").Values
            .Select(row => (Row: row, Map: GameData.Int(row, "_iId"), Points: RiftPoints(row)))
            .Where(m => m.Points.Count > 0).ToList();
        int low = 3, high = 4;
        if (data.Table("PublicParamConfig").TryGetValue("OnePieceMapRefresh", out var refresh)
            && refresh.ValueKind == JsonValueKind.Array && refresh.GetArrayLength() == 2)
        { low = (int)(GameData.Number(refresh[0]) ?? 3); high = Math.Max(low, (int)(GameData.Number(refresh[1]) ?? 4)); }
        int count = Math.Min(maps.Count, rng.Next(low, high + 1));
        var budget = data.Table("OnePieceMonsterConfig").Values
            .ToDictionary(r => (int)GameData.Int(r, "_iStarId"), r => (int)GameData.Int(r, "_iRefreshCount"));
        for (int i = 0; i < count; i++)
        {
            var pick = Weighted(maps, m => GameData.Int(m.Row, "_iMapWeight"), rng);
            maps.Remove(pick);
            foreach (var point in pick.Points)
            {
                var stars = Pairs(pick.Row, "_kStarWeight").Where(p => budget.GetValueOrDefault((int)p.Key) > 0).ToList();
                if (stars.Count == 0) break;
                int star = (int)Weighted(stars, p => p.Value, rng).Key;
                if (AddRiftStone(pick.Map, point, star, null, rng) != null) budget[star]--;
            }
        }
    }

    private List<MapPoint> RiftPoints(JsonElement row)
    {
        var list = new List<MapPoint>();
        if (worldData.Map(GameData.Int(row, "_iId")) is not { } map || !row.TryGetProperty("_kMonsterPoint", out var points)
            || points.ValueKind != JsonValueKind.Array) return list;
        foreach (var p in points.EnumerateArray())
            if (GameData.Number(p) is { } id && map.Points.TryGetValue((long)id, out var point)) list.Add(point);
        return list;
    }

    /// <summary>[[key, weight], ...] rows such as _kStarWeight and _kDungeonList.</summary>
    private static List<KeyValuePair<long, long>> Pairs(JsonElement row, string field)
    {
        var list = new List<KeyValuePair<long, long>>();
        if (!row.TryGetProperty(field, out var pairs) || pairs.ValueKind != JsonValueKind.Array) return list;
        foreach (var p in pairs.EnumerateArray())
            if (p.ValueKind == JsonValueKind.Array && p.GetArrayLength() == 2
                && GameData.Number(p[0]) is { } key && GameData.Number(p[1]) is { } weight)
                list.Add(new((long)key, (long)weight));
        return list;
    }

    private static T Weighted<T>(IReadOnlyList<T> items, Func<T, long> weight, Random rng)
    {
        long total = items.Sum(x => Math.Max(0, weight(x)));
        if (total <= 0) return items[rng.Next(items.Count)];
        long roll = rng.NextInt64(total);
        foreach (var item in items)
            if ((roll -= Math.Max(0, weight(item))) < 0) return item;
        return items[^1];
    }

    private JsonElement? StarRow(int star) => data.Table("OnePieceMonsterConfig").Values
        .Where(r => GameData.Int(r, "_iStarId") == star).Cast<JsonElement?>().FirstOrDefault();

    private RiftStone? AddRiftStone(long map, MapPoint point, int star, long? scene, Random rng)
    {
        if (StarRow(star) is not { } row) return null;
        var scenes = Pairs(row, "_kDungeonList");
        long chosen = scene ?? (scenes.Count > 0 ? Weighted(scenes, p => p.Value, rng).Key : 0);
        if (data.Row("OnePieceRewardConfig", chosen) is not { } reward) return null;
        int stoneNpc = (int)GameData.Int(row, "_iId");
        long uid = RiftUidBase + map * 4096 + riftSeq++ % 4096;
        var stone = new RiftStone { Uid = uid, Map = map, Point = point, StoneNpc = stoneNpc, Star = star, Scene = chosen,
            Boss = RiftBoss((int)GameData.Int(reward, "_iMonsterId"), star) };
        riftStones[uid] = stone;
        riftMapTotals[map] = riftMapTotals.GetValueOrDefault(map) + 1;
        return stone;
    }

    /// <summary>Stars 2-5 use the scene boss's later variants when NPCConfig has one with the same name.</summary>
    private int RiftBoss(int baseBoss, int star)
    {
        int variant = baseBoss + star - 1;
        return star > 1 && data.Row("NPCConfig", baseBoss) is { } first && data.Row("NPCConfig", variant) is { } next
            && data.TextOf(first, "_kName") == data.TextOf(next, "_kName") ? variant : baseBoss;
    }

    private EntityBase RiftEntity(RiftStone stone)
    {
        var npc = data.Row("NPCConfig", stone.StoneNpc);
        var entity = SceneService.NpcEntity(new WorldNpc(stone.Uid, stone.Map, WorldNpcKind.Npc, stone.StoneNpc,
            npc is { } n ? data.ClientTextOf(n, "_kName") : "", npc is { } c ? (int)GameData.Int(c, "_iCamp") : 0,
            stone.Point.X, stone.Point.Y, stone.Point.Z, stone.Point.Face, default));
        entity.CustomDatas.Add(RiftInfo(stone));
        return entity;
    }

    /// <summary>
    /// CustomData 4900700 OnePieceStarNpcInfo [star, scene, state]: the dialog's Challenge
    /// (UI_Sys_NpcDialogListWidgetCtrl._buildOnePieceExtraParams) sends [2] as 30400's sceneEntry and stops with a tip
    /// when it is missing; LC_HudElement_OnePiece shows [1] as stars and names the affix from [2];
    /// LC_CustomDataHandler_OnePiece reads [3] == 1 as not fighting.
    /// </summary>
    private static CustomData RiftInfo(RiftStone stone)
    {
        var param = new CustomParam { Tp = CustomParam.Types.ParamType.ListLong };
        param.ListLongParam.Add([stone.Star, stone.Scene, stone.Challenger == null ? 1 : 0]);
        return new CustomData { Id = StarNpcInfo, CustomParam = param };
    }

    private static bool OnRiftMap(GameSession session, long map) =>
        session.Role != null && !session.SceneLoading && session.InstanceId == 0 && session.SceneEntry == map;

    private int RiftRemaining(long map) => riftStones.Values.Count(s => s.Map == map);

    private async Task RiftViewAsync(GameSession session, IEnumerable<RiftStone> removed, IEnumerable<RiftStone> added)
    {
        if (session.Role == null || session.SceneLoading || session.InstanceId != 0) return;
        var view = new AoIViewChangeData();
        foreach (var stone in removed) if (stone.Map == session.SceneEntry) view.LeaveAoi.Add((ulong)stone.Uid);
        foreach (var stone in added) if (stone.Map == session.SceneEntry) view.Bases.Add(RiftEntity(stone));
        if (view.LeaveAoi.Count + view.Bases.Count > 0) await session.SendGameAsync(GameAoiSink.SyncAoiView, view);
    }

    private void PostRiftMap(long map, Func<GameSession, Task> send)
    {
        foreach (var session in sessions.All().Where(s => OnRiftMap(s, map)))
            loop.PostOwned(session, () => send(session));
    }

    private void PostRiftState(RiftStone stone) => PostRiftMap(stone.Map, s => s.SendGameAsync(WorldCoreService.CustomChange,
        new CustomDataChange { EntityId = stone.Uid, CustomDatas = { RiftInfo(stone) } }));

    /// <summary>
    /// A party's 30400 claims the stone; a win removes it, anything else frees it for the next party. ChatConfig 9002
    /// "Congratulations to all Adventurers for successfully resolving this Rift Crisis." goes out once, when the round's
    /// last stone is cleared; a window that closes with stones left says nothing.
    /// </summary>
    private void ReleaseRiftStone(Treasure a, bool cleared)
    {
        if (a.Stone is not { } stone || riftStones.GetValueOrDefault(stone.Uid) != stone || stone.Challenger != a.Leader) return;
        if (!cleared) { stone.Challenger = null; PostRiftState(stone); return; }
        riftStones.Remove(stone.Uid);
        PostRiftMap(stone.Map, async s => { await RiftViewAsync(s, [stone], []); await SendOnePieceMapAsync(s); });
        if (riftStones.Count == 0 && riftRound >= 0 && !riftResolved)
        {
            riftResolved = true;
            loop.PostRuntime(() => RiftChatAsync(RiftClosedChat));
        }
    }

    /// <summary>1022 marquee to everyone online, parameters encoded as MvpService does (inferred).</summary>
    private async Task RiftChatAsync(int chatId, params string[] args)
    {
        if (data.Row("ChatConfig", chatId) == null) return;
        var ntf = new SystemBoardCastMsgNtf { Source = MsgSource.System, ChatId = chatId };
        for (int i = 0; i < args.Length; i++)
            ntf.ParamList[i + 1] = new ChatParameter { Params = { args[i] }, Type = ChatParamType.ChatParamString };
        await RoleFanout.EachAsync(sessions.All().Where(p => p.Role != null), p => p.SendGameAsync(SystemBroadcast, ntf));
    }

    private string NpcName(int npc) => data.Row("NPCConfig", npc) is { } row ? data.TextOf(row, "_kName") : npc.ToString(CultureInfo.InvariantCulture);

    /// <summary>!rift: test aid, not original behaviour.</summary>
    private async Task RiftGmAsync(GameSession s, string[] args)
    {
        if (args.Length == 0)
        {
            string maps = string.Join("; ", riftMapTotals.Keys.Select(m => $"{data.MapName(m)} {m} {RiftRemaining(m)}/{riftMapTotals[m]}"));
            await GmCommands.ReplyAsync(s, riftRound < 0 ? "Rift Crisis is closed (open 10:00-22:00)."
                : $"Rift round until {DateTimeOffset.FromUnixTimeSeconds(RiftRoundEnd()).ToLocalTime():HH:mm}: {maps}");
            return;
        }
        if (args[0].Equals("party", StringComparison.OrdinalIgnoreCase))
        {
            RiftTestMinParty = args.Length > 1 && int.TryParse(args[1], out int floor) && floor >= 1 ? floor : null;
            var (min, max) = RiftPartySize();
            await GmCommands.ReplyAsync(s, $"A Rift stone now admits a party of {min}-{max} (OnePieceMapMembersNum is "
                + $"{(RiftTestMinParty == null ? "in force" : "overridden; !rift party resets it")}).");
            return;
        }
        if (riftRound < 0) { await GmCommands.ReplyAsync(s, "Rift Crisis is closed (open 10:00-22:00): stones cannot be fought now."); return; }
        if (args[0].Equals("new", StringComparison.OrdinalIgnoreCase))
        {
            riftRound = long.MinValue; // the next tick places a fresh round
            await GmCommands.ReplyAsync(s, "A new Rift round starts within a second.");
            return;
        }
        if (!int.TryParse(args[0], out int star) || StarRow(star) == null || s.Role == null || s.InstanceId != 0)
        { await GmCommands.ReplyAsync(s, "Usage: !rift | !rift new | !rift party [min] | !rift <star 1-6> [boss name] (on a field map)"); return; }
        long? scene = null;
        if (args.Length > 1)
        {
            string wanted = string.Join(' ', args[1..]);
            var match = data.Table("OnePieceRewardConfig").Values.FirstOrDefault(r =>
                NpcName((int)GameData.Int(r, "_iMonsterId")).Contains(wanted, StringComparison.OrdinalIgnoreCase));
            if (match.ValueKind == JsonValueKind.Undefined)
            {
                string bosses = string.Join(", ", data.Table("OnePieceRewardConfig").Values.Select(r => NpcName((int)GameData.Int(r, "_iMonsterId"))));
                await GmCommands.ReplyAsync(s, $"No Rift boss matches '{wanted}'. Bosses: {bosses}");
                return;
            }
            scene = GameData.Int(match, "_iId");
        }
        var here = new MapPoint(s.Position.X + 2, s.Position.Y, s.Position.Z, 0);
        if (AddRiftStone(s.SceneEntry, here, star, scene, Random.Shared) is not { } stone)
        { await GmCommands.ReplyAsync(s, "No Rift stone could be made for that star."); return; }
        PostRiftMap(stone.Map, async p => { await RiftViewAsync(p, [], [stone]); await SendOnePieceMapAsync(p); });
        var (least, most) = RiftPartySize();
        await GmCommands.ReplyAsync(s, $"Placed a {star}-star Rift Stone next to you: {NpcName(stone.Boss)} (scene {stone.Scene}). "
            + $"Challenge needs a party of {least}-{most} on this map with you as leader.");
    }
}

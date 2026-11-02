using Romsg;
using Ro3Emu.Server.Data;
using Ro3Emu.Server.Data.Config;
using Ro3Emu.Server.Economy;
using Ro3Emu.Server.Maps;

namespace Ro3Emu.Server.Game;

/// <summary>
/// The Bonfire Party's transformations and prize draw, and the fuel performance others see. From the client data:
/// <list type="bullet">
/// <item>A Transformation Manual (NPC 7020027, interaction 41002109) casts CampFireHenshinSkill 5701501 "MonsterFruit",
/// whose _kBuff1 lists the five transform buffs of size 1 (CampFireBonus keys 7020101-7020501: Poring, Lunatic, Savage
/// Babe, Baby Desert Wolf, Baphomet Jr.); CampFireHenshinTimeout is 1200 s.</item>
/// <item>CampFireAffixNewConfig action 5 at stages 2/3/4 is the prize draw: [[2], [prize level 3/2/1], [award group
/// 170002/3/4], then [min players, max players, winners] bands from 10 players. GuildCampFireLuckyPlayerNotify
/// (awardLevel, playerList: playerId, buffId, awardList, bonusShow) feeds UI_Sys_GuildTinder_RewardDetailPanel ("First/
/// Second/Third Prize Winners", UIWords 110143/110142/110141; the winner's items are the MultiverseGroupConfig ids of
/// awardList) and an empty list is ChatConfig 6038 "Too few players joined the party". The stage names the
/// transformation that can win (GuildCampFireStageInfo.nowHenshinType; chats 6040-6042 "Players in [type], get ready!",
/// 6039 "no players with the corresponding transformation type").</item>
/// <item>A winner's own prize is CampFireBonus of the transform buff it holds, at the draw's prize level.</item>
/// <item>A fuel shows CampFireFuel._iInteractPerformId's InteractPerformance: gameplay status 8 "add accelerant" for
/// _iAnimTime ms.</item>
/// </list>
/// Inferred: a fruit monster's kill drops one or two Manuals that last 30 s (the superseded CampFireAction 29
/// [[5],[7020027],[1,2],[30],[3000]]); a Manual picks one of the five transformations at random and refuses a player
/// already transformed; the stage's transformation is a stable pick among the five, like the shows; the players counted
/// for the bands are the eligible members present; winners are drawn at random among the present holders of the stage's
/// transformation; awardList is the draw's group then the winner's CampFireBonus group, both paid at once. No server
/// source enlarges a transformation (sizes 2/3 and the enlargement buffs 7022401-7022801 stay unused here).
/// </summary>
public sealed partial class GuildCampfireService
{
    internal const int ManualNpc = 7020027, ManualInteraction = 41002109, PrizeDrawAction = 5;
    private const int ManualPointBase = 2001, ManualPointCount = 1000, ManualLifeSeconds = 30;
    private const float ManualReach = 4.5f; // NPCConfig 7020027 _iInteractDistance 4, plus client lag

    private sealed record Manual(long Room, MapPoint Position, long ExpireSecond);
    private readonly Dictionary<long, Manual> _manuals = [];
    private int _nextManual;
    /// <summary>Roles showing a fuel performance: their room and the end in ms.</summary>
    private readonly Dictionary<long, (InstanceService.Room Room, long EndMs)> _performing = [];
    private readonly HashSet<(long Room, long Day, int Stage)> _prizePosted = [];
    private readonly AwardService _awards;

    private long NowMs() => _time.GetUtcNow().ToUnixTimeMilliseconds();

    private long PublicParam(string key, long fallback) =>
        _data.Table("PublicParamConfig").TryGetValue(key, out var value) ? (long)(GameData.Number(value) ?? fallback) : fallback;

    /// <summary>The size-1 transform buffs MonsterFruit grants, in CampFireBonus order: index + 1 is the henshin type.</summary>
    private long[] TransformBuffs() => SkillConfig.Get(_data, PublicParam("CampFireHenshinSkill", 5701501)) is { } skill
        ? [.. skill.Buff1.Select(b => b.Length > 0 ? b[0] : 0).Where(id => CampFireBonus.Get(_data, id) != null).Order()]
        : [];

    private CampFireBonus? TransformOf(GameSession s) => s.Buffs
        .Select(b => CampFireBonus.Get(_data, b.ConfigId)).FirstOrDefault(row => row != null);

    /// <summary>The stage's winning transformation (1-5; 0 outside the draw stages).</summary>
    private int HenshinType(long guild, int stage)
    {
        int types = TransformBuffs().Length;
        return types == 0 || stage is < 2 or > 4 ? 0 : (int)((guild + Today() + stage * 7) % types) + 1;
    }

    // ---- fuel performance ----

    /// <summary>InteractPerformance of the committed fuel, on the player and everyone in the room.</summary>
    private async Task PerformFuelAsync(GameSession s, InstanceService.Room room, long fuelId)
    {
        if (CampFireFuel.Get(_data, fuelId) is not { } fuel || InteractPerformance.Get(_data, fuel.InteractPerformId) is not { } show
            || GameplayStatusConfig.Get(_data, show.GameStatusId) is not { } status || s.Role == null) return;
        var info = new InteractInfo
        {
            EType = (InteractInfo.Types.EState)status.StatusType, StateId = (int)show.GameStatusId, ActiveObjId = Uid(room, 413),
        };
        _performing[s.Role.Id] = (room, NowMs() + Math.Max(0, show.AnimTime));
        await ToRoomAsync(room, WorldInteractionService.Change, new InteractChangeNtf { Uid = s.Role.Id, DataInfo = info });
    }

    private void FinishPerformances()
    {
        long now = NowMs();
        foreach (var (role, (room, end)) in _performing.Where(p => p.Value.EndMs <= now).ToArray())
        {
            _performing.Remove(role);
            var idle = new InteractChangeNtf { Uid = role, DataInfo = new InteractInfo { EType = InteractInfo.Types.EState.Idle } };
            _loop.TrackLegacyDetached("campfire performance end", () => ToRoomAsync(room, WorldInteractionService.Change, idle));
        }
    }

    private static async Task ToRoomAsync(InstanceService.Room room, uint id, Google.Protobuf.IMessage message)
    {
        foreach (var member in room.ActiveMembers)
            if (member.Session.SceneEntry == Dungeon && !member.Session.SceneLoading) await member.Session.SendGameAsync(id, message);
    }

    // ---- Transformation Manuals ----

    /// <summary>One or two Manuals around a dead fruit monster, added to the room's view and to the 83245 notice.</summary>
    private async Task DropManualsAsync(InstanceService.Room room, Vec3 dead, GuildCampFireCreateMonsterFruitNotify notice)
    {
        if (_data.Row("NPCConfig", ManualNpc) == null) return;
        var view = new AoIViewChangeData();
        int count = Random.Shared.Next(1, 3);
        for (int i = 0; i < count; i++)
        {
            int point = ManualPointBase + _nextManual++ % ManualPointCount;
            float angle = Random.Shared.NextSingle() * MathF.Tau;
            var at = new MapPoint(dead.X + 1.5f * MathF.Cos(angle), dead.Y, dead.Z + 1.5f * MathF.Sin(angle), 0);
            long uid = Uid(room, point);
            _manuals[uid] = new Manual(room.Id, at, _time.GetUtcNow().ToUnixTimeSeconds() + ManualLifeSeconds);
            AddNpc(view, room, point, ManualNpc, at);
            notice.Infos.Add(new CreateMonsterFruitInfo { NpcGuid = uid, CreatePosition = new Vec3 { X = at.X, Y = at.Y, Z = at.Z } });
        }
        await ToRoomAsync(room, GameAoiSink.SyncAoiView, view);
    }

    private void AddManuals(AoIViewChangeData view, InstanceService.Room room)
    {
        foreach (var (uid, manual) in _manuals.Where(m => m.Value.Room == room.Id))
            AddNpc(view, room, PointFromUid(room, uid), ManualNpc, manual.Position);
    }

    private async Task RemoveManualAsync(InstanceService.Room room, long uid)
    {
        if (!_manuals.Remove(uid)) return;
        var view = new AoIViewChangeData { LeaveAoi = { (ulong)uid } };
        await ToRoomAsync(room, GameAoiSink.SyncAoiView, view);
    }

    private void ExpireManuals(long second)
    {
        var live = _home.Rooms.ToDictionary(r => r.Room.Id, r => r.Room);
        foreach (var (uid, manual) in _manuals.Where(m => m.Value.ExpireSecond <= second || !live.ContainsKey(m.Value.Room)).ToArray())
        {
            if (live.TryGetValue(manual.Room, out var room))
                _loop.TrackLegacyDetached("campfire manual expiry", () => RemoveManualAsync(room, uid));
            else _manuals.Remove(uid);
        }
    }

    private static bool IsManualPoint(int point) => point is >= ManualPointBase and < ManualPointBase + ManualPointCount;

    /// <summary>41002109 on a Manual: the Manual goes and the player takes a random size-1 transformation.</summary>
    private async Task<int> UseManualAsync(GameSession s, InstanceService.Room room, NpcInteractionReq req)
    {
        if (req.InteractionId != ManualInteraction) return ErrorCodes.NpcParameter;
        if (!_manuals.TryGetValue(req.NpcUid, out var manual) || manual.Room != room.Id) return ErrorCodes.NpcMissing;
        if (CurrentStage() is < 2 or > 4) return ErrorCodes.FeatureUnavailable;
        if (!Near(s, manual.Position, ManualReach)) return ErrorCodes.NpcTooFar;
        if (TransformOf(s) != null) return ErrorCodes.RewardClaimed;
        var buffs = TransformBuffs();
        if (buffs.Length == 0) return ErrorCodes.RewardMissing;
        await RemoveManualAsync(room, req.NpcUid);
        int ms = (int)Math.Clamp(PublicParam("CampFireHenshinTimeout", 1200) * 1000, 1000, int.MaxValue);
        await _buffs.AddAsync(s, (int)buffs[Random.Shared.Next(buffs.Length)], ms, s.Role!.Id);
        return 0;
    }

    // ---- the prize draw ----

    private CampFireAffixNewConfig? PrizeDraw(int stage) =>
        CampFireAffixNewConfig.All(_data).FirstOrDefault(r => r.Phase == stage && r.ActionType == PrizeDrawAction) is { Row.ValueKind: System.Text.Json.JsonValueKind.Object } row
            ? row : null;

    /// <summary>The stage's draw is due: _iDelayTime after the stage's CampFireTimeLine start (stage 2: 20:04:30).</summary>
    private bool PrizeDue(int stage, long second) => PrizeDraw(stage) is { } draw
        && CampFireTimeLine.Get(_data, stage) is { } line && second >= StartTime() + (line.TimeLine + draw.DelayTime) / 1000;

    /// <summary>Tick: each live room's due draw, posted once per room, day and stage.</summary>
    private void RunDueDraws(IEnumerable<(long Guild, InstanceService.Room Room)> live, int stage, long day, long second)
    {
        if (stage is < 2 or > 4 || !PrizeDue(stage, second)) return;
        foreach (var (guild, room) in live)
            if (_prizePosted.Add((room.Id, day, stage)))
                _loop.TrackLegacyDetached("campfire prize draw", () => PrizeDrawAsync(guild, room, stage));
    }

    /// <summary>CampFireAffixNewConfig action 5 of the stage: once per guild and event day.</summary>
    private async Task PrizeDrawAsync(long guild, InstanceService.Room room, int stage)
    {
        if (room.Closed || PrizeDraw(stage) is not { ActionParams: [_, [var level, ..], [var group, ..], .. var bands] }) return;
        using var scope = await EnterWithGuildGateAsync("campfire/prize draw", guild,
            room.ActiveMembers.Select(m => m.Session.Role?.Id ?? 0));
        List<GameSession> winners;
        try
        {
            var state = Read(guild);
            if (!state.PrizeStages.Add(stage)) return;
            var present = room.ActiveMembers.Select(m => m.Session)
                .Where(s => s.Role != null && s.InstanceId == room.Id && !s.SceneLoading && Eligible(s)).ToList();
            int count = bands.FirstOrDefault(b => b is [var min, var max, _] && present.Count >= min && present.Count <= max)
                is [_, _, var n] ? (int)n : 0;
            long type = HenshinType(guild, stage) is var t and > 0 ? TransformBuffs().Select(b => CampFireBonus.Get(_data, b)!.Value.TypeId)
                .ElementAt(t - 1) : 0;
            winners = [.. present.Where(s => TransformOf(s)?.TypeId == type).OrderBy(_ => Random.Shared.Next()).Take(count)];
            if (present.FirstOrDefault() is { } actor) await SaveAsync(guild, state, actor);
        }
        finally { Gate(guild).Release(); }
        var notice = new GuildCampFireLuckyPlayerNotify { AwardLevel = level };
        foreach (var winner in winners)
            if (await PayPrizeAsync(winner, guild, stage, level, group) is { } info) notice.PlayerList.Add(info);
        await ToRoomAsync(room, Lucky, notice);
    }

    private async Task<GuildCampFireLuckyPlayerInfo?> PayPrizeAsync(GameSession s, long guild, int stage, long level, long group)
    {
        if (TransformOf(s) is not { } bonus || s.Role is not { } role) return null;
        var (own, show) = bonus.Prize(level);
        long[] groups = [.. new[] { group, own }.Where(g => g > 0)];
        var grants = groups.SelectMany(g => _awards.Resolve(s, g).Grants).ToArray();
        bool paid = await _character.ApplyAwardAsync(s, new AwardPlan(0, 0, grants), persist: tx =>
        {
            if (grants.Any(g => g.Entry is not (100 or 101)))
                tx.WithEventEconomy(EventEconomy.ForRole(_data, s, "guild-campfire-prize",
                    $"guild-campfire/prize/{guild}/{Today()}/{stage}/{role.Id}", [], grants,
                    new { Guild = guild, Stage = stage, Level = level, Groups = groups }));
        });
        if (!paid) return null;
        return new GuildCampFireLuckyPlayerInfo { PlayerId = role.Id, BuffId = bonus.BuffId, BonusShow = show, AwardList = { groups } };
    }
}

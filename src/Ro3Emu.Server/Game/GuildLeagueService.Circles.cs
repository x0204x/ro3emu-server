using Romsg;
using Ro3Emu.Server.Maps;

namespace Ro3Emu.Server.Game;

/// <summary>
/// What the client draws of the capture points and flags: unit custom data (Config/Define/Lua_CustomDataDefine.lua,
/// handlers in Config/InjectConfig/Lua_CustomDataConfigExt.lua). Each Main Battlefield point is an OccupationCircle unit
/// (NPCConfig 4800000, _iNPCType 12) carrying 12 CircleProgress, 13 CircleLockState, 14 CircleState, 15 CirclePlayerCount,
/// 16 CircleID: LC_CustomDataHandler_GuildLeagueRing.UpdateCircleData builds GuildLeagueCircleAllInfo from them only when
/// all five are present, and adds the LC_PerformGfx_OccupyCircle ring. A flag carrier has 19 FlagState, the courtyard Boss
/// that drops the flag 20 IsFlag (LC_MapModule_AOIUnit, UI_Com_MonsterDirectionTipWidgetCtrl).
/// </summary>
public sealed partial class GuildLeagueService
{
    private const long CircleProgressKey = 12, CircleLockKey = 13, CircleStateKey = 14, CircleCountKey = 15, CircleIdKey = 16,
        FlagStateKey = 19, IsFlagKey = 20;
    private const int CircleNpc = 4800000;
    /// <summary>LC_DunGeon_VarDefine 10103 PGuildLeagueOccupyNpcId: the circle the player stands in (the BattleVS bar).</summary>
    private const int OccupyNpcVar = 10103;
    private const long CircleUidBase = 1L << 54;
    private long _circleSerial;

    /// <summary>GuildSettingConfig GuildLeagueOccupyNoticeType [[42,1],[43,2],[44,0]]: region -> circle 0-2, the a/b/c of
    /// Lua_ImageDefine.GuildLeagueOccupationCircleMap[state * 3 + id].</summary>
    private int CircleId(int region)
    {
        if (_data.Table("GuildSettingConfig").TryGetValue("GuildLeagueOccupyNoticeType", out var row)
            && row.TryGetProperty("_kValue", out var pairs) && pairs.ValueKind == System.Text.Json.JsonValueKind.Array)
            foreach (var pair in pairs.EnumerateArray())
                if (pair.ValueKind == System.Text.Json.JsonValueKind.Array && pair.GetArrayLength() > 1
                    && Ro3Emu.Server.Data.GameData.Number(pair[0]) == region)
                    return (int)(Ro3Emu.Server.Data.GameData.Number(pair[1]) ?? 0);
        return Array.IndexOf(CapturePoints, region);
    }

    private static CustomData LongData(long id, long value) =>
        new() { Id = id, CustomParam = new CustomParam { Tp = CustomParam.Types.ParamType.Long, LongParam = value } };

    /// <summary>
    /// The circle's five values. Camps are 1 blue, 2 red (GuildLeagueCamp). Progress runs from -100 (blue holds it) to 100
    /// (red holds it): LC_PerformGfx_OccupyCircle shows ±100 as occupied and the sign as the side gaining it, and the BattleVS
    /// bar fills |progress|. (inferred) A challenger moves it linearly from the holder's end to its own over the stay time.
    /// Lock is the camp whose capture is fortified (Guard ring); the count list is [blue, red] players inside (the bar's
    /// table.unpack against GuildLeagueFightPeopleNum).
    /// </summary>
    private CustomData[] CircleData(Match m, Point p, long now, long stay)
    {
        static long Side(int camp) => camp == 1 ? -100 : camp == 2 ? 100 : 0;
        int owner = m.Camp(p.Owner);
        long progress = Side(owner);
        if (p.Challenger != 0 && now >= p.FortifiedUntil)
        {
            double frac = stay <= 0 ? 1 : Math.Clamp((now - p.Since) / (double)stay, 0, 1);
            progress = (long)Math.Round(progress + (Side(m.Camp(p.Challenger)) - progress) * frac);
        }
        var counts = new CustomParam { Tp = CustomParam.Types.ParamType.ListLong };
        counts.ListLongParam.Add([p.Blue, p.Red]);
        return
        [
            LongData(CircleProgressKey, progress), LongData(CircleLockKey, now < p.FortifiedUntil ? owner : 0),
            LongData(CircleStateKey, owner), new CustomData { Id = CircleCountKey, CustomParam = counts },
            LongData(CircleIdKey, p.Circle),
        ];
    }

    /// <summary>(inferred) The unit is an NPC entity (type 2) sent straight to everyone on the battlefield, as NPCConfig
    /// 4800000's _iMonsterSightDis 100000 keeps it in view from anywhere (the minimap and BattleVS widgets list all three).</summary>
    private EntityBase CircleEntity(Match m, Point p, long now, long stay)
    {
        var entity = new EntityBase
        {
            Uuid = (ulong)p.Uid, EntityTemplateID = CircleNpc, EntityType = 2, Name = "", EntityCamp = 0,
            State = EntityState.Alive, NotifyType = EntityBase.Types.NotifyType.All,
            MovementInfo = new MovementInfo
            {
                Pos = new Vec3 { X = p.At.X, Y = p.At.Y, Z = p.At.Z },
                ServerTime = MonsterService.NowMs, MoveType = MovementType.MoveTypeNone
            },
        };
        entity.CustomDatas.Add(CircleData(m, p, now, stay));
        return entity;
    }

    private async Task ShowCirclesAsync(GameSession s, Match m)
    {
        if (m.Main.Points.Count == 0) return;
        long now = Now, stay = Setting("GuildLeagueTriggerStayAreaTime", 6);
        var view = new AoIViewChangeData();
        foreach (var p in m.Main.Points.Values) view.Bases.Add(CircleEntity(m, p, now, stay));
        await s.SendGameAsync(GameAoiSink.SyncAoiView, view);
        if (s.Role != null && m.Main.Fighters.TryGetValue(s.Role.Id, out var f)) f.InCircle = 0;
    }

    /// <summary>31018 for every circle whose values changed, to the whole Main Battlefield.</summary>
    private async Task PublishCirclesAsync(Match m, long now, long stay)
    {
        foreach (var p in m.Main.Points.Values)
        {
            var values = CircleData(m, p, now, stay);
            string shown = string.Join(";", values.Select(v => v.CustomParam.ToString()));
            if (shown == p.Shown) continue;
            p.Shown = shown;
            await SendAllAsync(Present(m.Main), WorldCoreService.CustomChange, new CustomDataChange { EntityId = p.Uid, CustomDatas = { values } });
        }
    }

    /// <summary>The personal var 10103 in a var-change event (event type 4, as the shared vars; inferred for a personal one).</summary>
    private static DungeonEventNotify OccupyVar(Match m, long circle)
    {
        var change = new DungeonEventNotify { DungeonEntry = m.Main.Scene, EventType = (DungeonEventType)4, StageNum = 2, LevelNum = 1 };
        change.Vars.Add(Var(OccupyNpcVar, circle));
        return change;
    }

    /// <summary>FlagState follows Carriers in the carrier's own scope: 1 while carrying, 0 after (Lua IsHaveFlag is "&gt; 0").</summary>
    private void PostFlag(GameSession s) => _loop.PostOwned(s, () => SyncFlagAsync(s), "guild league flag");

    private async Task SyncFlagAsync(GameSession s)
    {
        if (s.Role == null || s.Progress == null || s.SceneLoading) return;
        long wanted = Locate(s) is { } at && at.Match.Carriers.Contains(s.Role.Id) ? 1 : 0;
        bool known = s.WorldState.CustomData.TryGetValue(FlagStateKey, out var shown);
        if (known ? shown!.CustomParam.LongParam == wanted : wanted == 0) return;
        // As WorldCoreService.SetCustomAsync: kept in the session's custom data (its AOI entity carries it), 31018 to self and viewers.
        var value = LongData(FlagStateKey, wanted);
        s.WorldState.CustomData[FlagStateKey] = value.Clone();
        var change = new CustomDataChange { EntityId = s.Role.Id, CustomDatas = { value } };
        await SendAllAsync(_world.Viewers(s).Prepend(s).ToArray(), WorldCoreService.CustomChange, change);
    }
}

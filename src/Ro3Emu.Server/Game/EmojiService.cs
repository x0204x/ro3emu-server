using System.Text.Json;
using Google.Protobuf;
using Microsoft.Extensions.Options;
using Romsg;
using Ro3Emu.Server.Accounts;
using Ro3Emu.Server.Data;

namespace Ro3Emu.Server.Game;

/// <summary>
/// Emotes and sitting (MG_EmojiPlay 82601-82610, AUTO_SYNC 5006 DBEmojiPlayInfo).
/// The request animates nothing: every emote, the owner's own included, is driven by the entity's
/// AoiBehaviorStateMachineInfo (31048 behavior_state_machine, EntityBase.behaviorStateMachineInfo).
/// START_PLAY puts the player in 17 Prepare; the client answers with a second START_PLAY once its model is ready
/// (the duo partner after walking to its spot), and the server then sends 14 Play with the start time in epoch ms.
/// The client never ends an emote by itself (a non-loop emote holds its last frame), so the server ends it on STOP,
/// on its PerformanceConfig duration, on movement, combat, death and scene change, and for a duo on either side.
/// </summary>
public sealed partial class EmojiService : IGameTicker
{
    public const uint Sync = 5006, Invite = 82601, StartPlay = 82602, SetIdle = 82603, InviteNtf = 82604,
        Accept = 82605, Refuse = 82606, StopPlay = 82607, AddCollection = 82608, DelCollection = 82609,
        InviteResponseNtf = 82610;
    private const uint AoiAttrChange = 31048;

    // ErrorCodeConfig emote block (3601-3618) and generic action errors.
    private const int NoConfig = 3601, Locked = 3602, Busy = 3603, CannotInvite = 3605, RepeatInvite = 3607,
        NoInvite = 3608, InviteExpired = 3609, NoInviter = 3610, TooFar = 3611, InCombat = 3612, AlreadyPerforming = 3618,
        TooFrequent = 1211, Dead = 49, ForbiddenHere = 813;

    private const int SingleSort = 1, DoubleSort = 2, IdleSort = 4;
    private const long FightBit = 1L << 15; // CombatService: eObjectStateBattling
    private const long PrepareTimeoutMs = 10_000; // (inferred) a Prepare the client never confirms
    private const long UnlockRecheckMs = 5_000;   // appearance light-up/expiry has no hook: recheck online roles

    /// <summary>Runtime emote of one player. Duo partners each hold one, naming the other in Targets.</summary>
    public sealed class Emote
    {
        public ObjectState State { get; set; }
        public int EmojiId { get; set; }
        public int PlayIndex { get; set; } = 1;
        public long[] Targets { get; set; } = [];
        public bool Ready { get; set; }
        public long StateMs { get; set; }
        public long EndMs { get; set; }
        public long CombatMark { get; set; }
    }

    private sealed record PendingInvite(long Owner, long Invitee, int EmojiId, long ExpireMs);

    private readonly GameData _data;
    private readonly RoleStore _roles;
    private readonly Maps.World _world;
    private readonly SessionRegistry _sessions;
    private readonly TaskService _tasks;
    private readonly CharacterService _character;
    private readonly GameLoop _loop;
    private readonly TimeProvider _clock;
    private readonly ILogger<EmojiService> _log;
    private readonly string _sceneEditorDir;
    private readonly HashSet<GameSession> _active = [];
    private readonly List<PendingInvite> _invites = [];
    private readonly Dictionary<long, Dictionary<long, Region>> _regions = [];
    private TimeSpan _lastUnlockCheck;
    private bool _queued;

    private sealed record Region(float X, float Z, float Width, float Length, float Direction)
    {
        public bool Contains(Vec3 p)
        {
            double radians = Direction * Math.PI / 180, dx = p.X - X, dz = p.Z - Z;
            return Math.Abs(dx * Math.Cos(radians) + dz * Math.Sin(radians)) <= Width / 2
                && Math.Abs(dz * Math.Cos(radians) - dx * Math.Sin(radians)) <= Length / 2;
        }
    }

    public EmojiService(GameData data, RoleStore roles, Maps.World world, SessionRegistry sessions, TaskService tasks,
        CharacterService character, CombatService combat, SceneService scene, GameLoop loop, TimeProvider clock,
        IOptions<ServerOptions> options, ILogger<EmojiService> log)
    {
        this._data = data; this._roles = roles; this._world = world; this._sessions = sessions; this._tasks = tasks;
        this._character = character;
        this._loop = loop; this._clock = clock; this._log = log;
        _sceneEditorDir = Path.Combine(options.Value.GameDataDir ?? options.Value.DataDir, "sceneeditor");
        character.ProgressHandlers.Add(RefreshUnlocksAsync);
        character.RegisterItemAction(EmoticonCollectionAction, UseEmoticonItemAsync);
        combat.PlayerDied.Add(s => EndAsync(s));
        scene.Leaving.Add(LeavingAsync);
    }

    private long NowMs => _clock.GetUtcNow().ToUnixTimeMilliseconds();

    private long Param(string key, long fallback) =>
        _data.Table("PublicParamConfig").TryGetValue(key, out var v) && GameData.Number(v) is { } n ? (long)n : fallback;

    private JsonElement? Row(int emojiId) => _data.Row("PerformanceConfig", emojiId);

    // ---------------------------------------------------------------- 5006 DBEmojiPlayInfo

    public async Task LoginAsync(GameSession session)
    {
        var saved = _roles.LoadEmojiPlay(session.Role!.Id);
        session.EmojiPlay = saved != null ? DBEmojiPlayInfo.Parser.ParseFrom(saved) : new DBEmojiPlayInfo();
        session.Emote = null;
        if (Recompute(session)) Save(session);
        await CharacterService.SyncAsync(session, Sync, session.EmojiPlay.Clone());
        await LoginEmoticonsAsync(session);
    }

    /// <summary>
    /// emojiUnlocks = PerformanceConfig rows whose _iUnlockCondition holds: [1002,N] base level, [1104,A] appearance A
    /// lit up (inferred from _iUnlockDescribe "Unlocked by obtaining Appearance"). An idle emote or favourite whose
    /// row locks again is dropped (inferred).
    /// </summary>
    private bool Recompute(GameSession session)
    {
        var info = session.EmojiPlay!;
        var unlocked = _data.Table("PerformanceConfig").Values.Where(r => Unlocked(session, r))
            .Select(r => (int)GameData.Int(r, "_iID")).Where(id => id > 0).Order().ToList();
        bool changed = !info.EmojiUnlocks.SequenceEqual(unlocked);
        if (changed) { info.EmojiUnlocks.Clear(); info.EmojiUnlocks.AddRange(unlocked); }
        foreach (int id in info.EmojiCollections.Where(id => !unlocked.Contains(id)).ToArray()) { info.EmojiCollections.Remove(id); changed = true; }
        if (info.IdleEmojiID != 0 && !unlocked.Contains(info.IdleEmojiID)) { info.IdleEmojiID = 0; changed = true; }
        return changed;
    }

    private bool Unlocked(GameSession session, JsonElement row)
    {
        if (_data.Row("LC_ConditionList", GameData.Int(row, "_iUnlockCondition")) is not { } cond) return GameData.Int(row, "_iUnlockCondition") == 0;
        if (!cond.TryGetProperty("m_kSubConditionList", out var subs) || subs.ValueKind != JsonValueKind.Array) return true;
        foreach (var sub in subs.EnumerateArray())
        {
            if (!sub.TryGetProperty("m_kData", out var d) || d.ValueKind != JsonValueKind.Array || d.GetArrayLength() < 2) continue;
            long type = (long)(GameData.Number(d[0]) ?? 0), param = (long)(GameData.Number(d[1]) ?? 0);
            bool ok = type switch
            {
                1002 => session.Progress?.Level >= param,
                1104 => session.Wardrobe?.AllLightUpAppearances.ContainsKey((int)param) == true,
                _ => false,
            };
            if (!ok) return false;
        }
        return true;
    }

    private async Task RefreshUnlocksAsync(GameSession session)
    {
        if (session.EmojiPlay == null || !Recompute(session)) return;
        Save(session);
        await SyncAsync(session);
    }

    private void Save(GameSession session) => _roles.SaveEmojiPlay(session.Role!.Id, session.EmojiPlay!.ToByteArray());

    private Task SyncAsync(GameSession session) => CharacterService.SyncAsync(session, Sync, session.EmojiPlay!.Clone());

    public async Task SetIdleAsync(GameSession session, SetIdleEmojiReq req)
    {
        int result = session.EmojiPlay == null ? Busy
            : req.EmojiId == 0 ? 0
            : Row(req.EmojiId) is not { } row ? NoConfig
            : GameData.Int(row, "_iSort") != IdleSort ? NoConfig
            : !session.EmojiPlay.EmojiUnlocks.Contains(req.EmojiId) ? Locked : 0;
        if (result == 0)
        {
            session.EmojiPlay!.IdleEmojiID = req.EmojiId;
            Save(session);
        }
        await session.SendGameAsync(SetIdle, new SetIdleEmojiRsp { Result = result, EmojiId = req.EmojiId });
        if (result != 0) return;
        await SyncAsync(session);
        // Sent on every request, the same id included: that makes the owner and viewers play it once now.
        await ToPlayerAndViewersAsync(session, new AOIAttrChange
        {
            EntityUuid = (ulong)session.Role!.Id, IdleInfo = new AoiIdleInfo { EmojiId = req.EmojiId },
        });
    }

    public async Task CollectionAsync(GameSession session, int emojiId, bool add)
    {
        var info = session.EmojiPlay;
        int result = info == null ? Busy
            : Row(emojiId) is not { } row ? NoConfig
            : GameData.Int(row, "_iSort") == IdleSort ? NoConfig // idle emotes cannot be favourites (client 66031)
            : add && !info.EmojiUnlocks.Contains(emojiId) ? Locked : 0;
        if (result == 0)
        {
            bool changed = add ? !info!.EmojiCollections.Contains(emojiId) : info!.EmojiCollections.Remove(emojiId);
            if (add && changed) info.EmojiCollections.Add(emojiId);
            if (changed) Save(session);
        }
        if (add) await session.SendGameAsync(AddCollection, new AddEmojiCollectionRsp { Result = result, EmojiId = emojiId });
        else await session.SendGameAsync(DelCollection, new DelEmojiCollectionRsp { Result = result, EmojiId = emojiId });
        if (result == 0) await SyncAsync(session);
    }

    // ---------------------------------------------------------------- playing

    public async Task StartAsync(GameSession session, EmojiStartPlayReq req)
    {
        long now = NowMs;
        // The client's "ready" answer to our Prepare (LC_State_Unit_Emoji: Ready -> Readied -> START_PLAY).
        if (session.Emote is { State: ObjectState.EObjectStateSmemojiPlayPrepare, Ready: false } prepared && prepared.EmojiId == req.EmojiId)
        {
            prepared.Ready = true;
            await session.SendGameAsync(StartPlay, new EmojiStartPlayRsp());
            await PlayWhenReadyAsync(session);
            return;
        }
        int result = Validate(session, req.EmojiId, SingleSort, now);
        if (result == 0)
        {
            session.LastEmoteRequestMs = now;
            // Switching single emotes while performing is allowed (LC_Skill_Function.CheckEmojiCanCast).
            session.Emote = new Emote
            {
                State = ObjectState.EObjectStateSmemojiPlayPrepare, EmojiId = req.EmojiId, StateMs = now,
                CombatMark = session.LastCombatMs,
            };
            _active.Add(session);
        }
        await session.SendGameAsync(StartPlay, new EmojiStartPlayRsp { Result = result });
        if (result == 0) await BroadcastStateAsync(session);
    }

    /// <summary>Common checks for starting or inviting to an emote of the given PerformanceConfig._iSort.</summary>
    private int Validate(GameSession session, int emojiId, int sort, long now)
    {
        if (session.Role == null || session.EmojiPlay == null || session.SceneLoading) return Busy;
        if (session.Dead) return Dead;
        if ((session.StatusBits & FightBit) != 0) return InCombat;
        if (Row(emojiId) is not { } row || GameData.Int(row, "_iSort") != sort) return NoConfig;
        if (!session.EmojiPlay.EmojiUnlocks.Contains(emojiId)) return Locked;
        int sex = (int)GameData.Int(row, "_iSex");
        if (sex != 0 && sex != session.Role.Sex) return NoConfig;
        if (Forbidden(session.SceneEntry)) return ForbiddenHere;
        if (now - session.LastEmoteRequestMs < GameData.Int(row, "_iPerformanceCD")) return TooFrequent;
        if (session.Emote is { Targets.Length: > 0 }) return AlreadyPerforming; // in a duo
        return 0;
    }

    private bool Forbidden(long scene) =>
        _data.Row("MapConfig", scene) is { } map && GameData.Int(map, "_iPerformanceForbidden") == 1
        || _data.Row("DungeonConfig", scene) is { } dungeon && GameData.Int(dungeon, "_iPerformanceForbidden") == 1;

    /// <summary>Everyone in the group confirmed Prepare: all go to 14 with the same start time.</summary>
    private async Task PlayWhenReadyAsync(GameSession session)
    {
        var group = Group(session);
        if (group.Any(s => s.Emote is not { State: ObjectState.EObjectStateSmemojiPlayPrepare, Ready: true })) return;
        long now = NowMs;
        foreach (var member in group)
        {
            var emote = member.Emote!;
            emote.State = ObjectState.EObjectStateSmemojiPlay;
            emote.StateMs = now;
            long duration = Row(emote.EmojiId) is { } row ? GameData.Int(row, "_iDuration") : 0;
            emote.EndMs = duration > 0 ? now + duration : 0; // 0 = loop (Meditate) until something ends it
            emote.CombatMark = member.LastCombatMs;
        }
        foreach (var member in group) await BroadcastStateAsync(member);
        foreach (var member in group) await QuestAsync(member);
    }

    /// <summary>The player and, for a duo, the partners it names (only those still in the same emote).</summary>
    private List<GameSession> Group(GameSession session)
    {
        var group = new List<GameSession> { session };
        foreach (long id in session.Emote?.Targets ?? [])
            if (_sessions.All().FirstOrDefault(s => s.Role?.Id == id) is { Emote: { } other } partner
                && other.EmojiId == session.Emote!.EmojiId && other.Targets.Contains(session.Role!.Id))
                group.Add(partner);
        return group;
    }

    public async Task StopAsync(GameSession session)
    {
        await session.SendGameAsync(StopPlay, new EmojiStopPlayRsp());
        await EndAsync(session);
    }

    /// <summary>Ends the player's emote (and its duo partners'): 13 SMIdle with the old param as lastParam.</summary>
    public async Task EndAsync(GameSession session, bool silent = false)
    {
        if (session.Emote == null) return;
        var group = Group(session);
        foreach (var member in group)
        {
            var old = member.Emote;
            member.Emote = null;
            _active.Remove(member);
            if (old == null || silent && member == session) continue;
            await ToPlayerAndViewersAsync(member, new AOIAttrChange
            {
                EntityUuid = (ulong)member.Role!.Id,
                BehaviorStateMachine = new AoiBehaviorStateMachineInfo
                {
                    CurState = ObjectState.EObjectStateSmidle, LastState = old.State, StartStateTime = NowMs,
                    LastParam = Param(old),
                },
            });
        }
    }

    /// <summary>Scene change: the player leaves every viewer's AOI, so its own state just goes; a partner ends.</summary>
    private Task LeavingAsync(GameSession session)
    {
        CancelInvites(session.Role?.Id ?? 0);
        return EndAsync(session, silent: true);
    }

    /// <summary>Movement ends a playing emote; walking in Prepare does not (the duo partner walks to its spot).</summary>
    public Task PlayerMovedAsync(GameSession session) =>
        session.Emote is { State: ObjectState.EObjectStateSmemojiPlay } && session.Walking ? EndAsync(session) : Task.CompletedTask;

    private static StateParam Param(Emote emote)
    {
        var play = new StateParamEmojiPlay { EmojiId = emote.EmojiId, PlayIndex = emote.PlayIndex, IsReady = emote.Ready };
        play.TargetPlayerIds.AddRange(emote.Targets);
        return new StateParam { EmojiPlay = play };
    }

    private static AoiBehaviorStateMachineInfo Info(Emote emote) => new()
    {
        CurState = emote.State, Param = Param(emote), StartStateTime = emote.StateMs,
    };

    private Task BroadcastStateAsync(GameSession session) => ToPlayerAndViewersAsync(session, new AOIAttrChange
    {
        EntityUuid = (ulong)session.Role!.Id, BehaviorStateMachine = Info(session.Emote!),
    });

    /// <summary>The emote state and idle emote carried by the entity a viewer creates (31037, ENTITYSELF).</summary>
    public static void PopulateEntity(GameSession session, EntityBase entity)
    {
        if (session.Emote is { } emote) entity.BehaviorStateMachineInfo = Info(emote);
        if (session.EmojiPlay is { IdleEmojiID: > 0 } info) entity.IdleInfo = new AoiIdleInfo { EmojiId = info.IdleEmojiID };
    }

    private async Task ToPlayerAndViewersAsync(GameSession p, IMessage body)
    {
        await p.SendGameAsync(AoiAttrChange, body);
        foreach (var viewer in _world.Viewers(p).Where(v => v != p)) await viewer.SendGameAsync(AoiAttrChange, body);
    }

    /// <summary>Target 106903 CheckAreaAction [map, region, emojiId]: counted when the emote really plays.</summary>
    private Task QuestAsync(GameSession session) =>
        _tasks.OnEmotePlayedAsync(session, session.Emote!.EmojiId,
            (map, region) => session.SceneEntry == map && RegionsOf(map).TryGetValue(region, out var r) && r.Contains(session.Position));

    private Dictionary<long, Region> RegionsOf(long map)
    {
        if (_regions.TryGetValue(map, out var cached)) return cached;
        var found = new Dictionary<long, Region>();
        if (_data.Row("MapConfig", map) is { } row && row.TryGetProperty("_kResSceneEditorName", out var editor)
            && DataFiles.Find(_sceneEditorDir, editor.GetString() + ".json") is { } path)
        {
            using var doc = JsonDocument.Parse(File.ReadAllBytes(path));
            if (doc.RootElement.TryGetProperty("m_kRegins", out var all) && all.ValueKind == JsonValueKind.Object)
                foreach (var region in all.EnumerateObject())
                    if (long.TryParse(region.Name, out long id) && region.Value.TryGetProperty("center", out var center))
                        found[id] = new(center.GetProperty("x").GetSingle(), center.GetProperty("z").GetSingle(),
                            region.Value.GetProperty("width").GetSingle(), region.Value.GetProperty("length").GetSingle(),
                            region.Value.TryGetProperty("direction", out var dir) ? dir.GetSingle() : 0);
        }
        return _regions[map] = found;
    }

    // ---------------------------------------------------------------- duo emotes

    private GameSession? Online(long roleId) => _sessions.All().FirstOrDefault(s => s.Role?.Id == roleId && s.Handoff == null);

    private bool Near(GameSession a, GameSession b)
    {
        float dx = a.Position.X - b.Position.X, dz = a.Position.Z - b.Position.Z;
        float range = Param("PerformanceDistance", 1000) / 100f; // centimetres, like _iDisplacement (inferred)
        return a.SceneEntry == b.SceneEntry && a.LineNumber == b.LineNumber && dx * dx + dz * dz <= range * range;
    }

    private static bool Free(GameSession s) => !s.Dead && !s.SceneLoading && (s.StatusBits & FightBit) == 0
        && s.Emote is not { Targets.Length: > 0 } && s.Emote is not { State: ObjectState.EObjectStateSmemojiPlayPrepare };

    public async Task InviteAsync(GameSession session, EmojiInviteReq req)
    {
        long now = NowMs;
        var target = req.Uuids.Count == 1 ? Online((long)req.Uuids[0]) : null;
        int result = Validate(session, req.EmojiId, DoubleSort, now);
        if (result == 0)
            result = target == null || target == session ? CannotInvite
                : !Near(session, target) ? TooFar
                : !Free(target) ? Busy
                : _invites.Any(i => i.Owner == session.Role!.Id && i.Invitee == target.Role!.Id && i.EmojiId == req.EmojiId && i.ExpireMs > now) ? RepeatInvite
                : 0;
        var rsp = new EmojiInviteRsp { Result = result, EmojiId = req.EmojiId };
        if (result != 0) { await session.SendGameAsync(Invite, rsp); return; }
        session.LastEmoteRequestMs = now;
        long expire = now + Param("InviteWaitingTime", 30_000);
        _invites.Add(new PendingInvite(session.Role!.Id, target!.Role!.Id, req.EmojiId, expire));
        rsp.ExpireTime = expire;
        rsp.InviteeId.Add((ulong)target.Role.Id);
        await session.SendGameAsync(Invite, rsp);
        await target.SendGameAsync(InviteNtf, new EmojiInviteNtf
        {
            OwnerId = session.Role.Id, OwnerName = session.Role.Name, EmojiId = req.EmojiId, ExpireTime = expire,
        });
    }

    public async Task AcceptAsync(GameSession session, EmojiInviteAcceptReq req)
    {
        long now = NowMs, me = session.Role?.Id ?? 0;
        var invite = _invites.FirstOrDefault(i => i.Owner == req.OwnerId && i.Invitee == me && i.EmojiId == req.EmojiId);
        var owner = Online(req.OwnerId);
        int result = invite == null ? NoInvite
            : invite.ExpireMs <= now ? InviteExpired
            : owner == null ? NoInviter
            : !Near(owner, session) ? TooFar
            : !Free(owner) || !Free(session) || owner.Dead || session.Dead ? Busy : 0;
        if (invite != null && (result == 0 || result == InviteExpired)) _invites.Remove(invite);
        await session.SendGameAsync(Accept, new EmojiInviteAcceptRsp { Result = result });
        if (result != 0) return;
        // The client clears all of both players' invites on acceptance (LC_DAL_EmojiAction._deleteAllEmojiInviteAndWaiting).
        _invites.RemoveAll(i => i.Owner == me || i.Invitee == me || i.Owner == owner!.Role!.Id || i.Invitee == owner.Role!.Id);
        await owner!.SendGameAsync(InviteResponseNtf, new EmojiInviteResponseNtf { IsAccept = true, EmojiId = req.EmojiId, InviteeId = me });
        // Both prepare: the inviter plays index 1 in place, the partner index 2 walks to its spot next to it.
        foreach (var (member, other, index) in new[] { (owner, session, 1), (session, owner, 2) })
        {
            member.Emote = new Emote
            {
                State = ObjectState.EObjectStateSmemojiPlayPrepare, EmojiId = req.EmojiId, PlayIndex = index,
                Targets = [other.Role!.Id], StateMs = now, CombatMark = member.LastCombatMs,
            };
            _active.Add(member);
        }
        await BroadcastStateAsync(owner);
        await BroadcastStateAsync(session);
    }

    public async Task RefuseAsync(GameSession session, EmojiInviteRefuseReq req)
    {
        long me = session.Role?.Id ?? 0;
        var invite = _invites.FirstOrDefault(i => i.Owner == req.OwnerId && i.Invitee == me && i.EmojiId == req.EmojiId);
        if (invite != null) _invites.Remove(invite);
        await session.SendGameAsync(Refuse, new EmojiInviteRefuseRsp { Result = invite == null ? NoInvite : 0 });
        if (invite != null && Online(req.OwnerId) is { } owner)
            await owner.SendGameAsync(InviteResponseNtf, new EmojiInviteResponseNtf { IsAccept = false, EmojiId = req.EmojiId, InviteeId = me });
    }

    /// <summary>Invites of a player who left: the inviter learns the invitee became unavailable (inferred: 3605).</summary>
    private void CancelInvites(long roleId)
    {
        if (roleId == 0) return;
        foreach (var invite in _invites.Where(i => i.Owner == roleId || i.Invitee == roleId).ToArray())
        {
            _invites.Remove(invite);
            if (invite.Invitee == roleId && Online(invite.Owner) is { } owner)
                _loop.PostRuntime(() => owner.SendGameAsync(InviteResponseNtf, new EmojiInviteResponseNtf
                { IsAccept = false, EmojiId = invite.EmojiId, InviteeId = roleId, ErrorCode = CannotInvite }));
        }
    }

    // ---------------------------------------------------------------- tick: timers, combat, death, partners

    public void Tick(GameTime time)
    {
        if (_queued) return;
        long now = NowMs;
        _invites.RemoveAll(i => i.ExpireMs <= now);
        var online = _sessions.All();
        var ending = new List<GameSession>();
        foreach (var s in _active)
        {
            var emote = s.Emote;
            if (emote == null || s.Role == null || !online.Contains(s)) { ending.Add(s); continue; }
            bool partnerGone = emote.Targets.Any(id => Online(id) is not { Emote: { } other } || other.EmojiId != emote.EmojiId);
            if (s.Dead || s.LastCombatMs != emote.CombatMark || partnerGone
                || emote.State == ObjectState.EObjectStateSmemojiPlay && emote.EndMs > 0 && now >= emote.EndMs
                || emote.State == ObjectState.EObjectStateSmemojiPlayPrepare && now - emote.StateMs >= PrepareTimeoutMs)
                ending.Add(s);
        }
        bool recheck = time.Now - _lastUnlockCheck >= TimeSpan.FromMilliseconds(UnlockRecheckMs);
        if (ending.Count == 0 && !recheck) return;
        if (recheck) _lastUnlockCheck = time.Now;
        _queued = true;
        _loop.PostRuntime(async () =>
        {
            try
            {
                foreach (var s in ending)
                {
                    if (!online.Contains(s) || s.Role == null) { _active.Remove(s); CancelInvites(s.Role?.Id ?? 0); if (s.Emote != null) await EndAsync(s, silent: true); continue; }
                    await EndAsync(s);
                }
                if (recheck)
                    foreach (var s in _sessions.All().Where(s => s.Role != null && s.EmojiPlay != null && s.Handoff == null))
                        await RefreshUnlocksAsync(s);
            }
            catch (Exception e) { _log.LogWarning(e, "emote tick"); }
            finally { _queued = false; }
        });
    }
}

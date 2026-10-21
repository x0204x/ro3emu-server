using Romsg;
using Ro3Emu.Server.Accounts;

namespace Ro3Emu.Server.Game;

/// <summary>
/// The creation ritual ("sworn brothers", DAL:2001-2190). There is no client dungeon script: dungeon 1010001 (gameplay
/// type 25, lc_dungeon_base) is driven by quests 2000500-2000502, the sapling / mentor NPC actions and the group pushes.
/// <list type="number">
/// <item>2000500 [322001,1]: the leader hangs the declaration on a sapling (NPC action 123 → 34015 saplingId = the NPC's
/// config id 200032..200041, HLP:2515-2543). The 34012 BaseData with saplingData.saplingId flips the saplings'
/// interactions to Watering / View (condition 5010).</item>
/// <item>2000501 [322003,10,0]: every member waters it (action 119 → 34021).</item>
/// <item>2000502 [322002,10]: every member gives the declaration at NPC 200042 ("Enter Adventurer Guild Declaration",
/// dialogue 20004201 option gated by CheckRoleHaveTask 2000502; OathPanel → 34016).</item>
/// </list>
/// The totals the client shows are the dungeon variable CCurPlayerCount (DungeonVariableConfig 127). When every member
/// has declared, the group becomes Normal (260029: "All Team Members present when the Adv. Guild is created will become
/// official Members"; absent support members are released, inferred) and the room closes after
/// AdventureGroupDungeonLiveTime seconds.
/// </summary>
public sealed partial class AdventureGroupService
{
    private sealed class Ritual
    {
        public long GroupId;
        public InstanceService.Room Room = null!;
        public long Leader;
        public long StartMs;
        public readonly HashSet<long> Participants = new();
        /// <summary>0 hang the declaration, 1 water, 2 declare, 3 founded.</summary>
        public int Step;
        public readonly HashSet<long> Watered = new(), Declared = new();
    }

    /// <summary>Running rituals by group (in memory: a restart ends them, the group stays in its earlier status).</summary>
    private readonly Dictionary<long, Ritual> _rituals = new();

    private const int VarPlayerCount = 127, VarPlayerList = 145;

    private Ritual? RitualOf(long groupId) =>
        _rituals.TryGetValue(groupId, out var r) && !r.Room.Closed ? r : null;

    private Ritual? RitualHere(GameSession s, AdventureGroupStore.State g) =>
        RitualOf(g.Id) is { } r && s.InstanceId == r.Room.Id && s.SceneEntry == DungeonScene ? r : null;

    private void AbortRitual(long groupId) => _rituals.Remove(groupId);

    private GameSession[] RoomMembers(Ritual r) =>
        r.Room.ActiveMembers.Select(m => m.Session).Where(s => s.Role != null && r.Participants.Contains(s.Role.Id)).ToArray();

    // ---------------------------------------------------------------- party admission

    /// <summary>
    /// LaunchBrotherhoodCreation's checks on the server (HLP:2333-2482): the caller leads a party of at least
    /// AdventureGroupHelperCreateSuccessLimit online members, all on the caller's map near it (10217 "A party Member is
    /// not nearby"; 30 m is inferred), none in another group (10218), every one with the function open (10249).
    /// </summary>
    private int PartyCheck(GameSession leader, AdventureGroupStore.State? g)
    {
        long self = leader.Role!.Id;
        var (team, teamLeader) = _teams.TeamOf(self);
        if (team == 0 || teamLeader != self) return ErrPermission;
        var party = _teams.MembersOf(self).Select(Online).OfType<GameSession>().ToArray();
        if (party.Length < MinimumMembers) return ErrFewMembers;
        if (party.Length > MemberLimit) return ErrFull;
        foreach (var member in party)
        {
            if (member.SceneLoading || member.InstanceId != 0 || member.SceneEntry != leader.SceneEntry) return ErrNotNearby;
            float dx = member.Position.X - leader.Position.X, dz = member.Position.Z - leader.Position.Z;
            if (dx * dx + dz * dz > 30f * 30f) return ErrNotNearby;
            if (_store.ForRole(member.Role!.Id) is { } other && other.Id != g?.Id) return ErrOtherGroup;
            if (!Open(member)) return ErrTeamLocked;
        }
        return 0;
    }

    private int OwnerOfPending(GameSession s, out AdventureGroupStore.State? g)
    {
        g = Own(s);
        return g == null ? ErrNotInGroup : g.Owner != s.Role!.Id ? ErrPermission : g.Status == StatusNormal ? ErrJoined : 0;
    }

    /// <summary>Online members of the pending group and of the owner's party (a mentor-created group holds only its
    /// owner until the ritual starts).</summary>
    private int OnlineCandidates(GameSession s, AdventureGroupStore.State g)
    {
        var ids = g.Members.Keys.ToHashSet();
        if (_teams.TeamOf(s.Role!.Id) is { TeamId: not 0 } team && team.Leader == s.Role.Id) ids.UnionWith(_teams.MembersOf(s.Role.Id));
        return ids.Count(id => Online(id) != null);
    }

    /// <summary>34013: may the owner start the creation? At least three members online (10216).</summary>
    public async Task CheckBrothersAsync(GameSession s)
    {
        int code = OwnerOfPending(s, out var g);
        if (code == 0 && OnlineCandidates(s, g!) < MinimumMembers) code = ErrFewMembers;
        await s.SendGameAsync(CheckBrothers, new CheckCanStartBecomeSwornBrothersRes { Result = code });
    }

    /// <summary>
    /// 34006 "Invite all responding Members to a party and begin creating the Adventure Group?" (UIWords 260123): the
    /// owner leads a party (created when needed) and the online members get the ordinary party invitation; the client then
    /// paths the owner to the mentor (DAL:2126-2172).
    /// </summary>
    public async Task StartBrothersAsync(GameSession s)
    {
        int code = OwnerOfPending(s, out var g);
        if (code == 0 && OnlineCandidates(s, g!) < MinimumMembers) code = ErrFewMembers;
        long self = s.Role!.Id;
        if (code == 0 && _teams.TeamOf(self) is { TeamId: not 0 } joined && joined.Leader != self) code = ErrPermission;
        await s.SendGameAsync(StartBrothers, new StartBecomeSwornBrothersRes { Result = code });
        if (code != 0) return;
        if (_teams.TeamOf(self).TeamId == 0)
            await _teams.CreateAsync(s, new TeamCreateReq { TeamType = TeamType.Five, TargetId = 0 });
        var party = _teams.MembersOf(self).ToHashSet();
        foreach (long member in g!.Members.Keys.Where(id => id != self && !party.Contains(id)))
            if (Online(member) != null && _teams.TeamOf(member).TeamId == 0)
                await _teams.InviteAsync(s, new InviteIntoTeamReq { Guid = member });
    }

    // ---------------------------------------------------------------- 34014

    public async Task EnterRitualAsync(GameSession s)
    {
        int code = OwnerOfPending(s, out var g);
        if (code == 0 && RitualOf(g!.Id) != null) code = ErrCreating;
        if (code == 0) code = PartyCheck(s, g);
        if (code == 0 && (s.Dead || (s.StatusBits & (1L << 15)) != 0)) code = ErrorCodes.ActionInBattle;
        if (code == 0 && _scene.BornPoint(DungeonScene) == null) code = ErrorCodes.DungeonDataMissing;
        GameSession[] party = [];
        if (code == 0)
        {
            party = _teams.MembersOf(s.Role!.Id).Select(Online).OfType<GameSession>().ToArray();
            // The party becomes the group's roster for the ritual; nobody is dropped before the founding.
            foreach (var member in party)
                if (!g!.Members.ContainsKey(member.Role!.Id)) g.Members[member.Role.Id] = NewMember(member.Role.Id);
            foreach (var m in g!.Members.Values) m.Declaration = null;
            g.Status = StatusTemp;
            g.SaplingId = 0;
            g.SaplingWatering = 0;
            foreach (var member in party) g.Applications.Remove(member.Role!.Id);
            if (!await SaveAsync(g)) code = ErrOtherGroup;
        }
        await s.SendGameAsync(EnterRitual, new EnterSwornBrothersDungeonRes { Result = code });
        if (code != 0) return;
        var room = _instances.Create(DungeonScene, party);
        var ritual = new Ritual { GroupId = g!.Id, Room = room, Leader = s.Role!.Id, StartMs = _clock.GetUtcNow().ToUnixTimeMilliseconds() };
        foreach (var member in party) ritual.Participants.Add(member.Role!.Id);
        _rituals[g.Id] = ritual;
        _log.LogInformation("Adventure group {Id} {Name}: creation ritual in room {Room} with {Count} members", g.Id, g.Name, room.Id, party.Length);
        var chain = SwornTasks;
        foreach (var member in party)
        {
            await SendFullAsync(member, g, joined: true);
            await SyncRelationAsync(member, g);
            Pin(member);
            await _tasks.StartAdventureRitualAsync(member, chain);
        }
        foreach (var member in party) await _instances.EnterAsync(room, member, 3);
    }

    private void Pin(GameSession member)
    {
        if (_loop.Owners?.Current is { RuntimeOnly: false } context && member.Owner is { } owner && context.CanPin(owner))
            context.Pin(owner);
    }

    /// <summary>Scene loaded inside a ritual room: the dungeon snapshot (InitData field 15, CCurPlayerCount) for everyone
    /// present, and the group so the oath panel has its roster.</summary>
    private async Task OnSceneLoadedAsync(GameSession s)
    {
        if (s.SceneEntry != DungeonScene || s.InstanceId == 0) return;
        var ritual = _rituals.Values.FirstOrDefault(r => r.Room.Id == s.InstanceId && !r.Room.Closed);
        if (ritual == null || _store.Get(ritual.GroupId) is not { } g) return;
        await SendFullAsync(s, g);
        await BroadcastSnapshotAsync(ritual);
    }

    private Task BroadcastSnapshotAsync(Ritual r)
    {
        var present = RoomMembers(r);
        long limit = _data.Row("DungeonConfig", DungeonScene) is { } row ? Data.GameData.Int(row, "_iLimitTime") : 36000000;
        var snapshot = new DungeonBaseInfoNotify
        {
            DungeonEntry = DungeonScene, DungeonId = r.Room.Id, Stage = 1, Level = 1,
            ChangeType = (DungeonBaseInfoNotify.Types.eChangeType)2,
            InitData = new DungeonInitData { AdventureGroupDungeonInitData = new AdventureGroupDungeonInitData { AdventureGroupId = r.GroupId } },
            StageModuleData = new DungeonStageModuleData
            {
                MapState = 1, Stage = 1, MapStateStartTime = r.StartMs, StageStartTime = r.StartMs,
                MapStateEndTime = r.StartMs + limit, StageEndTime = r.StartMs + limit,
            },
        };
        var count = new DungeonVar { VarId = VarPlayerCount };
        count.Vi64.Add(Math.Max(1, present.Length));
        var list = new DungeonVar { VarId = VarPlayerList };
        list.Vi64.Add(present.Select(p => p.Role!.Id));
        snapshot.Vars.Add(count);
        snapshot.Vars.Add(list);
        return Task.WhenAll(present.Select(p => p.SendGameAsync(DungeonService.BaseInfo, snapshot)));
    }

    // ---------------------------------------------------------------- the three steps

    /// <summary>34015: the leader hangs the declaration on the chosen sapling (step 1 of the ritual).</summary>
    public async Task SelectSaplingAsync(GameSession s, SelectSaplingReq req)
    {
        var g = Own(s);
        var r = g == null ? null : RitualHere(s, g);
        int code = g == null ? ErrNotInGroup : r == null ? ErrNotInDungeon : g.Owner != s.Role!.Id ? ErrPermission
            : r.Step != 0 || g.SaplingId != 0 ? ErrSaplingTaken : !SaplingNpcs.Contains(req.SaplingId) ? ErrorCodes.Parameter : 0;
        if (code == 0)
        {
            g!.SaplingId = req.SaplingId;
            if (!await SaveAsync(g)) code = ErrorCodes.Unknown;
        }
        await s.SendGameAsync(SelectSapling, new SelectSaplingRes { Result = code, SaplingId = req.SaplingId });
        if (code != 0) return;
        r!.Step = 1;
        await BroadcastBaseAsync(g!);
        var chain = SwornTasks;
        foreach (var member in RoomMembers(r))
        {
            Pin(member);
            await _tasks.SetAdventureRitualAsync(member, chain[0], 1, complete: true, next: chain.Length > 1 ? chain[1] : 0);
        }
    }

    /// <summary>34021: the ritual's watering (step 2) or a normal group's daily watering of its tree in Prontera.</summary>
    public async Task WaterAsync(GameSession s, WateringSaplingReq req)
    {
        var g = Own(s);
        long self = s.Role!.Id;
        var resp = new WateringSaplingRes();
        int code = g == null ? ErrNotInGroup : 0;
        Ritual? r = g == null ? null : RitualHere(s, g);
        if (code == 0 && r != null)
        {
            code = r.Step == 0 ? (g!.Owner == self ? ErrNoSaplingOwner : ErrNoSaplingMember)
                : req.SaplingId != g!.SaplingId ? ErrWrongSapling
                : r.Step != 1 || r.Watered.Contains(self) ? ErrWatered : 0;
            if (code == 0)
            {
                r.Watered.Add(self);
                g.SaplingWatering++;
                if (!await SaveAsync(g)) { r.Watered.Remove(self); code = ErrorCodes.Unknown; }
            }
        }
        else if (code == 0)
        {
            var member = g!.Members[self];
            code = g.Status != StatusNormal ? ErrNotInGroup
                : g.SaplingId == 0 ? (g.Owner == self ? ErrNoSaplingOwner : ErrNoSaplingMember)
                : req.SaplingId != g.SaplingId || s.SceneEntry != SaplingScene || s.InstanceId != 0 ? ErrWrongSapling
                : TodayWatering(member) >= WaterLimit ? ErrWatered : 0;
            if (code == 0)
            {
                member.WateringCount = TodayWatering(member) + 1;
                member.WaterDay = DayStart;
                g.SaplingWatering++;
                AddEvent(g, AdventureGroupEventType.EventTypeWatering, self);
                if (!await SaveAsync(g)) code = ErrorCodes.Unknown;
            }
        }
        if (code == 0) resp.UpdateBaseData = MemberInfo(g!.Members[self]);
        resp.Result = code;
        await s.SendGameAsync(Water, resp);
        if (code != 0) return;
        await BroadcastMemberAsync(g!, g!.Members[self], ChangeType.ChangePlayerChange);
        await BroadcastBaseAsync(g);
        if (r == null)
        {
            await _tasks.OnSaplingWateredAsync(s);
            return;
        }
        var present = RoomMembers(r);
        bool done = present.All(p => r.Watered.Contains(p.Role!.Id));
        var chain = SwornTasks;
        if (done) r.Step = 2;
        foreach (var member in present)
        {
            Pin(member);
            await _tasks.SetAdventureRitualAsync(member, chain[1], r.Watered.Count, complete: done, next: done && chain.Length > 2 ? chain[2] : 0);
        }
    }

    /// <summary>34016: the declaration (text 1-30 characters or a voice of at most 15 s and 100 KB, HLP:103-146).</summary>
    public async Task DeclareAsync(GameSession s, AdventureGroupDeclarationReq req)
    {
        var g = Own(s);
        long self = s.Role!.Id;
        var d = req.DeclarationData ?? new DeclarationInfo();
        var (minText, maxText) = Range("AdventureGroupDeclareContentLength", 1, 30);
        var (minVoice, maxVoice) = Range("AdventureGroupSpeechLength", 1, 15);
        int code = g == null ? ErrNotInGroup
            : d.DeclarationType == DeclarationType.TextType
                ? (TextLength(d.Content) < minText || TextLength(d.Content) > maxText ? ErrDeclaration : Banned(d.Content) ? ErrBanned : 0)
            : d.DeclarationType == DeclarationType.VoiceType
                ? (d.Voice.Length is 0 or > 102400 || d.VoiceDuration < minVoice || d.VoiceDuration > maxVoice ? ErrDeclaration : 0)
            : ErrDeclaration;
        Ritual? r = g == null ? null : RitualOf(g.Id);
        if (code == 0 && r != null && (!r.Participants.Contains(self) || r.Step != 2)) code = ErrNoTask;
        if (code == 0)
        {
            var member = g!.Members[self];
            member.Declaration = new AdventureGroupStore.Declaration
            {
                Content = d.DeclarationType == DeclarationType.TextType ? d.Content : "", Type = (int)d.DeclarationType,
                Voice = d.DeclarationType == DeclarationType.VoiceType ? d.Voice.ToByteArray() : null,
                VoiceDuration = d.DeclarationType == DeclarationType.VoiceType ? d.VoiceDuration : 0,
            };
            if (!await SaveAsync(g)) code = ErrorCodes.Unknown;
        }
        await s.SendGameAsync(Declare, new AdventureGroupDeclarationRes { Result = code, DeclarationData = d });
        if (code != 0) return;
        await BroadcastMemberAsync(g!, g!.Members[self], ChangeType.ChangePlayerChange, except: self);
        if (r == null) return;
        r.Declared.Add(self);
        var present = RoomMembers(r);
        bool done = present.All(p => r.Declared.Contains(p.Role!.Id));
        var chain = SwornTasks;
        foreach (var member in present)
        {
            Pin(member);
            await _tasks.SetAdventureRitualAsync(member, chain[^1], r.Declared.Count, complete: done);
        }
        if (done) await FoundAsync(r);
    }

    /// <summary>Every member present has declared: the group is founded (status 3, level 1).</summary>
    private async Task FoundAsync(Ritual r)
    {
        if (_store.Get(r.GroupId) is not { } g) return;
        var present = RoomMembers(r).Select(p => p.Role!.Id).ToHashSet();
        present.Add(g.Owner); // (inferred) the founder keeps the group even when it dropped out of the room
        var released = g.Members.Keys.Where(id => !present.Contains(id)).ToArray();
        foreach (long id in released) g.Members.Remove(id);
        if (!g.Members.ContainsKey(g.Owner)) g.Owner = g.Members.Keys.First();
        g.Status = StatusNormal;
        g.Level = Math.Max(1, g.Level);
        g.CreateTime = Now;
        g.RecruitTime = 0;
        g.Applications.Clear();
        AddEvent(g, AdventureGroupEventType.EventTypeCreate, g.Owner, 0, g.Name);
        if (!await SaveAsync(g))
        {
            _log.LogWarning("Adventure group {Id}: founding commit failed", g.Id);
            return;
        }
        r.Step = 3;
        _log.LogInformation("Adventure group {Id} {Name} founded with {Count} members", g.Id, g.Name, g.Members.Count);
        foreach (var member in OnlineMembers(g))
        {
            await member.SendGameAsync(CreatedNotify, new AdventureGroupCreateNotify());
            await SendFullAsync(member, g, joined: true);
            await SyncRelationAsync(member, g);
            await RefreshGroupQuestsAsync(member);
        }
        foreach (long id in released)
            if (Online(id) is { } former)
            {
                await SendClearAsync(former);
                await SyncRelationAsync(former, null);
            }
        var room = r.Room;
        long life = Number("AdventureGroupDungeonLiveTime", 300);
        _loop.ScheduleLegacy(TimeSpan.FromSeconds(life), async () =>
        {
            _rituals.Remove(r.GroupId);
            foreach (var member in room.ActiveMembers.Select(m => m.Session).ToArray())
                if (member.InstanceId == room.Id && !member.SceneLoading) await _instances.LeaveAsync(member);
        });
    }

    /// <summary>A member of a normal group gets the group quests (DailyActivityConfig 3200, 3210-3213) at once.</summary>
    private async Task RefreshGroupQuestsAsync(GameSession s)
    {
        if (!s.DailyReady || s.Tasks == null) return;
        Pin(s);
        await _tasks.EnsureDailyTrackingAsync(s, _daily.TrackingTasks(s));
    }
}

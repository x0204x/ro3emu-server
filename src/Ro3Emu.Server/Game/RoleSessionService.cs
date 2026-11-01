using Google.Protobuf;
using Romsg;
using Ro3Emu.Server.Accounts;

namespace Ro3Emu.Server.Game;

/// <summary>
/// A role's session: create and select, the login -> world sequence, position and
/// settings persistence, the handover cleanup and role switching. Its messages are mapped by CoreMessages.
/// </summary>
public sealed partial class RoleSessionService(RoleStore roles, Data.GameData data, BuffService buffs, SceneService scene, Micro.FriendService friends, SeasonService seasons, GuildService guilds, WildAfkService afk, TimeProvider clock, Deadlines deadlines, Ownership.RoleHandover handover, SessionRegistry sessions, Persistence.PositionPersistence positions, Persistence.SettingsPersistence settings, ReconnectService reconnects, RoleSystems systems, ILogger<RoleSessionService> log,
    Microsoft.Extensions.Options.IOptions<ServerOptions> options)
{
    /// <summary>Novice; JobConfig[1000] picks the male/female unit.</summary>
    private const int NoviceCareer = 1000;

    /// <summary>Speed attribute (enum_unitAttrMap 113) when JobConfig is unavailable; Novice's _kAttri has 700.</summary>
    public const long DefaultSpeed = 700;

    private const long AttrSpeed = 113;
    private const long AttrBaseAtkSpeed = 115;

    private const int ErrNameExist = 2;
    private const int ErrNameLengthInvalid = 32;

    /// <summary>WALK is reported many times a second; the position goes to the database at most this often (and on stop).</summary>
    private static readonly TimeSpan PositionSaveInterval = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Avatar RPC "SwitchToAccount" (HappyEngineClient.SwitchToAccount, after PRE_SWITCH_ROLE): the server answers
    /// with "SwitchToAccountCB"; the client drops its entities and reconnects as the account (ConnectServerReq
    /// SwitchToAccount, handled by the gate).
    /// </summary>
    public async Task SwitchToAccountAsync(GameSession session)
    {
        await handover.Begin(session);
        log.LogInformation("{Account} leaves its role for the role screen", session.Account);
        await session.SendRpcAsync("SwitchToAccountCB");
    }

    public Task OnDisconnectedAsync(GameSession session) => handover.Begin(session);

    /// <summary>C/D 15: the handover cleanup reserves the role, its lifecycle and account plus the shared keys its
    /// steps write - its own guild's keys (offline publication) and its timed battle's leave and settlement - not
    /// Realm. LogoutScopeAuditTests and the owner audit inventory guard that nothing else is needed.</summary>
    internal Ownership.ReservationKey[] CleanupReservation(GameSession session) =>
        [.. session.Role is { } role ? new[] { Ownership.ReservationKey.Lifecycle(role.Id) } : [],
            .. session.Account is { } account ? new[] { Ownership.ReservationKey.Account(account) } : [],
            .. systems.LeaveKeys(session)];

    /// <summary>Essential unlinking runs after admitted role work, under the handover's cleanup capability.</summary>
    internal Func<string, GameSession, Exception?>? CleanupFailureForTest { get; set; }
    internal async Task CleanupRoleAsync(GameSession session)
    {
        var failures = new List<Exception>();
        long? snapshotRole = session.Role?.Id;
        async Task Step(string name, Func<Task> work)
        {
            try
            {
                if (CleanupFailureForTest?.Invoke(name, session) is { } injected) throw injected;
                await work();
            }
            catch (Exception e) { failures.Add(e); }
        }
        await systems.LeaveAsync(session, Step, () => SavePosition(session));
        if (snapshotRole is { } released) roles.ReleaseLoginSnapshot(released);
        if (failures.Count != 0) throw new AggregateException("Essential role cleanup failed.", failures);
    }

    internal async Task CreateRoleAsync(GameSession session, AccountLoginCreateRoleReq req)
    {
        // A replacement may have claimed the account while this request waited for its account reservation.
        if (!sessions.IsWinner(session) || session.Channel.PeerClosed) return;
        string name = req.RoleName.Trim();
        int result = 0;
        Role? role = null;
        if (name.Length is < 2 or > 16)
        {
            result = ErrNameLengthInvalid;
        }
        else if ((await roles.ForAccountAsync(session.Account!)).Count >= options.Value.AntiCheat.MaxRolesPerAccount)
        {
            // The role screen has m_iMaxRoleNumber (6) slots and no create button past them (LC_SelectRoleManager).
            scene.Cheats.Refused(session, CheatMonitor.RoleLimit, $"role create past {options.Value.AntiCheat.MaxRolesPerAccount} roles");
            result = ErrorCodes.Parameter;
        }
        else
        {
            req.Appearances.TryGetValue(RoleProto.HairSlot, out int hair);
            int sex = req.Sex is 1 or 2 ? req.Sex : 1;
            // The first quest (10101010) waits next to South Gate's born point, ~200 m from SpawnScenePos.
            // C/D 15: one insert with that position on the writer thread, under the account's reservation; the
            // unique name index decides a race between accounts.
            var born = scene.BornPoint(RoleStore.SpawnSceneEntry);
            role = await roles.CreateAsync(session.Account!, req.AreaId, name, sex, NoviceCareer, hair,
                born == null ? 0 : RoleStore.SpawnSceneEntry, born?.X ?? 0, born?.Y ?? 0, born?.Z ?? 0, born?.Face ?? 0);
            if (role == null) result = ErrNameExist;
        }
        log.LogInformation("Create role '{Name}' for {Account}: result {Result} id {Id}", name, session.Account, result, role?.Id);

        var resp = new AccountLoginCreateRoleResp { Result = result };
        if (role != null)
        {
            resp.RoleBaseInfo = RoleProto.BaseInfo(role);
            resp.Appearances.Add(RoleProto.Appearances(role));
        }
        await session.SendGameAsync(MsgId.LoginRoleCreate, resp);
    }

    internal Func<string, GameSession, Task>? SelectionPhaseForTest { get; set; }

    private bool SelectionAvailable(GameSession session) => sessions.IsWinner(session) && !session.Channel.PeerClosed;

    private void EnsureSelection(GameSession session, Ownership.AttachmentLease lease)
    {
        if (!SelectionAvailable(session) || lease.Cancelled ||
            !ReferenceEquals(session.Attachment, lease) ||
            !ReferenceEquals(session.Owner?.Attachment, lease) ||
            session.Owner.State != Ownership.RoleOwnerState.Loading)
            throw new OperationCanceledException("Role selection was superseded or its connection closed.");
    }

    internal async Task SelectRoleAsync(GameSession session, AccountLoginSelectRoleReq req)
    {
        var role = await roles.GetAsync(req.Id);
        if (role == null || role.Account != session.Account)
        {
            log.LogWarning("Select role {Id} refused for {Account}", req.Id, session.Account);
            await session.SendGameAsync(MsgId.LoginRoleSelect, new AccountLoginSelectRoleResp { Result = 1 });
            return;
        }
        // A target is validated before the valid old role is touched. Neither predecessor wait nor
        // old-role cleanup owns the target or Realm reservation.
        await session.PredecessorFence;
        if (session.Role != null) await handover.Begin(session);
        if (!SelectionAvailable(session)) return;
        // C/D 14: read the whole role at one SQLite snapshot on a reader worker, after the old role's handover and
        // before the Realm scope. The loads below are served from it instead of synchronous SQL on the loop; every
        // write committed after the read began invalidates its part, so nothing older than a commit is served.
        bool prepared = await roles.PrepareLoginSnapshotAsync(req.Id, session.Account!);
        try
        {
            if (!SelectionAvailable(session)) return;
            await SelectPreparedRoleAsync(session, req);
        }
        finally
        {
            // C/D 16 part 4: an attached role keeps its snapshot for the session (blob reads after login and
            // write-through of its own saves); role cleanup releases it. A failed or abandoned select releases now.
            if (prepared && !(session.Role?.Id == req.Id && session.Owner?.State == Ownership.RoleOwnerState.Attached))
                roles.ReleaseLoginSnapshot(req.Id);
        }
    }

    /// <summary>Team entry, guild online state and the scene guild id publish together under the role's own guild
    /// keys (not Realm, which starves behind sustained owned work). A membership change while waiting re-admits.</summary>
    internal async Task<Ownership.LegacyOwnerScope> EnterScenePublicationAsync(Ownership.OwnerRegistry owners, GameSession session)
    {
        while (true)
        {
            var scope = await owners.EnterAsync("scene online publication",
                [.. guilds.OwnGuildKeys(session), .. session.Role is { } role ? new[] { Ownership.ReservationKey.Lifecycle(role.Id) } : []]);
            if (owners.Reservations.Covers(scope.Context.Operation, guilds.OwnGuildKeys(session))) return scope;
            scope.Dispose();
        }
    }

    private async Task<Ownership.ReservationKey[]> LoginReservationAsync(long roleId, string account) =>
        [Ownership.ReservationKey.Role(roleId), Ownership.ReservationKey.Account(account), Ownership.ReservationKey.Lifecycle(roleId),
            .. await systems.LoginKeysAsync(roleId)];

    /// <summary>The login keys follow guild membership and pending obligations, which may change while the select
    /// waits: re-admit until the admitted reservation still covers them.</summary>
    private async Task<Ownership.LegacyOwnerScope> EnterSelectAsync(Ownership.OwnerRegistry owners, long roleId, string account)
    {
        while (true)
        {
            var scope = await owners.EnterAsync("role select", await LoginReservationAsync(roleId, account));
            try
            {
                if (owners.Reservations.Covers(scope.Context.Operation, await LoginReservationAsync(roleId, account))) return scope;
            }
            catch { scope.Dispose(); throw; }
            scope.Dispose();
        }
    }

    private async Task SelectPreparedRoleAsync(GameSession session, AccountLoginSelectRoleReq req)
    {
        Role? role;
        var owners = session.Owners ?? throw new InvalidOperationException("Selection has no owner registry.");
        Ownership.AttachmentLease? lease = null;
        try
        {
            // C/D 15: the select reserves its role, account and lifecycle plus the keys its login callees write (own
            // Prontera settlements, own Eve cycle, current territory season, its guild's caravan and challenge gates,
            // its pending MVP, dungeon, Mock Battle and timed-battle obligations) - not Realm. Realm-shared maintenance
            // runs on the services' own tickers; LoginScopeAuditTests guards that nothing else is needed.
            using (var scope = await EnterSelectAsync(owners, req.Id, session.Account!))
            {
                if (!SelectionAvailable(session)) return;
                role = roles.Get(req.Id);
                if (role == null || role.Account != session.Account)
                {
                    await session.SendGameAsync(MsgId.LoginRoleSelect, new AccountLoginSelectRoleResp { Result = 1 });
                    return;
                }
                if (SelectionPhaseForTest is { } beforeAttach)
                    await beforeAttach("BeforeAttach", session);
                if (!SelectionAvailable(session)) return;
                await roles.TouchLoginAsync(role.Id);
                if (!SelectionAvailable(session)) return;
                session.ClearRoleLoadState();
                session.Role = role;
                owners.BindLegacySelection(session);
                lease = session.Attachment!;
                session.InstanceId = 0;
                if (SelectionPhaseForTest is { } afterBind)
                    await afterBind("AfterBindBeforeLoad", session);
                EnsureSelection(session, lease);
                // Stage G: the services' login blob writes go out as one asynchronous commit, still under this scope.
                roles.BeginLoginStaging(role.Id);
                try
                {
                    var arrival = await InitializeSelectedRoleAsync(session, role);
                    await PublishSelectedRoleAsync(session, role, arrival, lease);
                    await roles.CommitLoginStagingAsync(role.Id);
                }
                finally { roles.FlushLoginStaging(role.Id); } // a failed or cancelled selection keeps what it staged
                EnsureSelection(session, lease);
                session.Owner!.State = Ownership.RoleOwnerState.Attached;
            }
        }
        catch (OperationCanceledException) when (!SelectionAvailable(session) || lease?.Cancelled == true)
        {
            if (lease != null) await handover.Begin(session);
            else session.Role = null;
        }
        catch
        {
            if (lease != null) await handover.Begin(session);
            else session.Role = null;
            throw;
        }
    }

    private async Task<Maps.MapPoint> InitializeSelectedRoleAsync(GameSession session, Role role)
    {
        deadlines.Attach(session);
        session.SkillPlans = null; // each selected role loads its own 5025 state
        session.SkillPlanSwitchUtc = default;
        session.Lottery = null;
        session.LotteryDay = 0;
        session.Training = null;
        var arrival = scene.LoginPoint(role.SceneEntry, new Vec3 { X = role.X, Y = role.Y, Z = role.Z }, role.Face);
        session.SceneRecoveryRequired = false;
        session.Handoff = null;
        session.LineNumber = 1;
        session.RuntimeKind = Maps.RuntimeMapKind.PublicLine;
        session.SceneEntry = role.SceneEntry;
        session.Position = new Vec3 { X = arrival.X, Y = arrival.Y, Z = arrival.Z };
        session.Face = arrival.Face;
        session.RolePositionReady = true;
        session.LastWalkUtc = DateTime.UtcNow;
        session.Speed = data.JobAttributes(role.Career).GetValueOrDefault(AttrSpeed, DefaultSpeed);
        session.PositionSavedUtc = DateTime.UtcNow;
        session.DeferFunctionSync = true;
        session.DeferTaskSync = true;
        session.SkillProtocol = new();
        session.SkillModeCds.Clear();
        session.SkillCds.Clear();
        session.Buffs.Clear();
        session.BuffEffectCds.Clear();
        session.ImmuneDeathOffer = null;
        session.ImmuneRecoveryUntilMs = 0;
        session.StatusBits = 0;
        session.CurrentHp = null;
        session.CurrentSp = null;
        session.Dead = false;
        session.CareerSpecial = null; session.CareerPendants = [];
        session.Personal = null;
        session.Mounts = null; session.MountState = new(); session.Wardrobe = null; session.Totems = null; session.Genre = null;
        session.TotemSwitchUtc = default;
        session.SceneLoading = true;
        await systems.LoadAsync(session);
        log.LogInformation("Role {Name} ({Id}) of {Account} enters scene {Scene} at {X},{Z}", role.Name, role.Id, session.Account, role.SceneEntry, role.X, role.Z);
        return arrival;
    }

    private async Task PublishSelectedRoleAsync(GameSession session, Role role, Maps.MapPoint arrival,
        Ownership.AttachmentLease lease)
    {
        if (SelectionPhaseForTest is { } beforePublish)
            await beforePublish("BeforePublish", session);
        EnsureSelection(session, lease);
        await session.SendGameAsync(MsgId.LoginRoleSelect, new AccountLoginSelectRoleResp
        {
            Result = 0,
            RoleAssets = new RoleAssets
            {
                BasicInfo = RoleProto.BaseInfo(role),
                ServerTimeMs = NowMs(),
                ServerInfo = new ServerBasicInfo
                {
                    ServerOpenTime = (ulong)seasons.StartTime,
                    ServerLogicTime = (ulong)clock.GetUtcNow().ToUnixTimeSeconds(),
                },
            },
        });
        EnsureSelection(session, lease);

        // The avatar entity replaces ClientAccount; from now on client game messages arrive raw. It carries the
        // ReconnectInfo, and OnGetAuth follows.
        await reconnects.CreateAvatarAsync(session, $"ava-{role.Id}");
        EnsureSelection(session, lease);

        // Saved client settings (intro skipped, control mode, key bindings...) via the generic DB increment sync.
        session.Settings = LoadSettings(role.Id); // C/D 17: kept on the session, merged and written lazily from now on
        session.SettingsPart = null;
        await session.SendGameAsync(MsgId.AutoSyncSettings, SettingsSync(session.Settings));
        EnsureSelection(session, lease);
        await systems.LoginAsync(session, () => EnsureSelection(session, lease));

        await friends.OnlineChangedAsync(role.Id, online: true);
        EnsureSelection(session, lease);

        // The world is entered by a server push of 70010 with reason Login.
        if (!await scene.EnterSceneAsync(session, role.SceneEntry, arrival, reason: 7))
        {
            EnsureSelection(session, lease);
            // (inferred) A saved scene the server cannot place the role on (unknown, a dungeon scene outside its
            // instance, one the client has no scene for): the spawn map's born point, where a new character starts.
            LogMovedToSpawn(log, role.Name, role.Id, role.SceneEntry, data.MapName(role.SceneEntry),
                scene.Unplaceable(role.SceneEntry) ?? "refused", RoleStore.SpawnSceneEntry);
            await scene.EnterSceneAsync(session, RoleStore.SpawnSceneEntry, null, reason: 7);
            EnsureSelection(session, lease);
        }
        EnsureSelection(session, lease);
    }

    internal async Task WalkAsync(GameSession session, WalkReq walk)
    {
        if (session.Role == null || walk.Pos == null || session.SceneLoading) return;
        if (session.Dead || buffs.HasForbidden(session, ForbiddenState.Walk))
        {
            // The client cannot move now: what it reports later may not include distance covered meanwhile.
            SceneService.HoldWalk(session, DateTime.UtcNow, walking: false);
            return;
        }
        if (!await scene.AcceptWalkAsync(session, walk)) return;
        session.PreviousPosition = session.Position;
        session.Position = walk.Pos;
        session.Face = walk.Face;
        session.WalkDir = float.IsFinite(walk.Dir) ? walk.Dir : walk.Face;
        session.Walking = !walk.BStop && walk.SpeedRate > 0;
        positions.MarkDirty(session, urgent: walk.BStop); // C/D 18: written lazily, never on the loop
        await systems.MovedAsync(session, walk);
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Role {Name} ({Id}) was saved on scene {Scene} {Map}, where it cannot log in ({Why}): moved to the spawn map {Spawn}")]
    private static partial void LogMovedToSpawn(ILogger logger, string name, long id, long scene, string map, string why, long spawn);

    /// <summary>The entity a remote client renders for this player (same shape as ENTITYSELF).</summary>
    public static EntityBase PlayerEntity(GameSession session) => SelfEntity(session);

    private void SavePosition(GameSession session)
    {
        // A private floor is not a persistent world location; entry saved the outside return position.
        if (session.Role is not { } role || session.InstanceId != 0) return;
        var p = session.Position;
        roles.SavePosition(role.Id, session.SceneEntry, p.X, p.Y, p.Z, session.Face);
        session.PositionSavedUtc = DateTime.UtcNow;
        positions.Persisted(session); // a durable barrier: no older lazy capture may follow it
    }

    /// <summary>
    /// SETTING_SAVE_SETTING_INFO carries only the changed parts; merge them into the stored DBSettingInfo
    /// (map entries overwrite by key, sub-messages merge field by field).
    /// </summary>
    internal async Task SaveSettingAsync(GameSession session, SaveSettingReq req, ReadOnlyMemory<byte> wire)
    {
        if (session.Role is { } role && req.DbSettingInfo != null)
        {
            var stored = session.Settings ??= LoadSettings(role.Id);
            // The client's maps (clientSetting, commonSetting) take any key: settings past MaxSettingsBytes are a modified
            // client filling the role's row, which every login sends back (inferred bound, far above the client's own).
            var merged = stored.Clone();
            MergeSettings(merged, wire);
            if (merged.CalculateSize() > MaxSettingsBytes && merged.CalculateSize() > stored.CalculateSize())
            {
                scene.Cheats.Refused(session, CheatMonitor.Payload, $"settings of {merged.CalculateSize()} bytes");
                await session.SendGameAsync(MsgId.SaveSetting, new SaveSettingResp { Result = ErrorCodes.Parameter });
                return;
            }
            MergeSettings(stored, wire);
            if (req.Type.Contains(SettingType.WildAfk) && stored.WildAFKInfo != null)
            {
                // AFK accounting follows this preference: durable now, together with its reconciliation.
                roles.SaveSettings(role.Id, stored.ToByteArray());
                settings.Persisted(session);
                await afk.SettingsChangedAsync(session, stored.WildAFKInfo);
                await session.SendGameAsync(MsgId.AutoSyncSettings, SettingsSync(stored));
            }
            else settings.MarkDirty(session, urgent: false);
        }
        await session.SendGameAsync(MsgId.SaveSetting, new SaveSettingResp { Result = 0 });
    }

    /// <summary>
    /// LC_DAL_OnHook._onSaveOnHookSetting sends only edited keys. Merge the wire submessage: parsing into the existing message
    /// preserves explicitly encoded false and leaves absent siblings alone. MergeFrom(a parsed proto3 object) would lose that
    /// presence information.
    /// </summary>
    private static void MergeSettings(DBSettingInfo into, ReadOnlyMemory<byte> wire)
    {
        using var input = new CodedInputStream(wire.ToArray());
        for (uint tag; (tag = input.ReadTag()) != 0;)
            if (tag == 18) into.MergeFrom(input.ReadBytes()); // SaveSettingReq.dbSettingInfo, field 2
            else input.SkipLastField();
    }

    /// <summary>(inferred) Largest saved settings of one role: the key bindings and panels of a real client stay far below.</summary>
    private const int MaxSettingsBytes = 256 * 1024;

    private DBSettingInfo LoadSettings(long roleId)
    {
        var settings = roles.LoadSettings(roleId) is { } bytes ? DBSettingInfo.Parser.ParseFrom(bytes) : new DBSettingInfo();
        settings.WildAFKInfo ??= new SettingWildAFKInfo(); // OnHook's setting editor requires a real object.
        return settings;
    }

    /// <summary>Full-state increment (syncAllMark): each field N of DBSettingInfo travels as field N*10 { all = value }.</summary>
    private static DBSettingInfoIncrement SettingsSync(DBSettingInfo s)
    {
        var inc = new DBSettingInfoIncrement { Version = 1, SyncAllMark = true };
        if (s.GuildSettingInfo != null) inc.GuildSettingInfo = new() { All = s.GuildSettingInfo };
        if (s.ShowDetailInfo != null) inc.ShowDetailInfo = new() { All = s.ShowDetailInfo };
        if (s.WildAFKInfo != null) inc.WildAFKInfo = new() { All = s.WildAFKInfo };
        foreach (var (k, v) in s.ClientSetting) inc.ClientSetting[k] = new() { All = v };
        foreach (var (k, v) in s.CommonSetting) inc.CommonSetting[k] = new() { All = v };
        return inc;
    }

    private static EntityBase SelfEntity(GameSession session)
    {
        var (role, position, face) = (session.Role!, session.Position, session.Face);
        var entity = new EntityBase
        {
            State = session.Dead ? EntityState.Dead : EntityState.Alive,
            PlayerLifeStatus = (EPlayerLifeStatus)(session.Dead ? 1 : 0),
            WatchState = (WatchState)(session.WorldState.Watching ? 1 : 0),
            Uuid = (ulong)role.Id,
            EntityTemplateID = (ulong)role.Career,
            EntityType = 3, // UT_Player
            Gender = role.Sex,
            Name = role.Name,
            EntityCamp = session.EventFaction > 0 ? session.EventFaction : 1,
            Face = face,
            MovementInfo = new MovementInfo
            {
                Pos = position.Clone(),
                Face = face,
                ServerTime = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            },
            NotifyType = EntityBase.Types.NotifyType.All,
        };
        entity.CurUseAppearances.Add(session.Wardrobe?.CurUseAppearances.ToDictionary(p => p.Key, p => p.Value) ?? RoleProto.Appearances(role));
        entity.CurRider = MountService.Rider(session);
        // AttributeConfig ids: STR..LUK 1-6, derived 101-116, HP/SP 2000/2001, level 2002 (CharacterService.Refresh).
        var properties = session.EntityProperties.Length > 0
            ? session.EntityProperties
            : [(101, 100), (2000, 100), (102, 50), (2001, 50), (AttrSpeed, session.Speed), (AttrBaseAtkSpeed, 100)];
        foreach (var (idx, value) in properties)
        {
            entity.Property.Add(new AoIProperty { Idx = idx, Data = value, BaseData = value });
        }
        entity.EquipInfo.Add(session.EquipLook);
        BuffService.PopulateEntity(session, entity);
        EmojiService.PopulateEntity(session, entity);
        CareerSpecialService.PopulateEntity(session, entity);
        entity.CustomDatas.Add(session.WorldState.CustomData.Values);
        return entity;
    }

    internal ulong NowMs() => (ulong)clock.GetUtcNow().ToUnixTimeMilliseconds();
}

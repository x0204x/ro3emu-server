using System.Text.Json;
using Romsg;
using Ro3Emu.Server.Data;
using Ro3Emu.Server.Maps;

namespace Ro3Emu.Server.Game;

/// <summary>
/// Open-world life collection: RefreshInfo population, 32700/32702 cast and 33516 selected-game settlement.
/// Placement, refresh-time units, and terminal Idle ordering are inferred from the client;
/// the concrete source/config/client contracts are cited there. All mutable state runs on GameLoop.
/// </summary>
public sealed class GatheringService : IGameTicker
{
    public const uint Interaction = 32700;
    public const uint StateChange = 32702;
    public const uint EntityBaseChange = 31049;
    public const uint MiniGame = 33516;
    private const long UidBase = 1L << 42;
    private const float InteractionSlack = 1.5f;
    private readonly Dictionary<long, GatheringUnit> _nodes = new();
    private readonly Dictionary<GameSession, PendingGather> _pending = new();
    private readonly HashSet<long> _settlingNodes = new();
    private readonly Dictionary<(RuntimeMapKey Runtime, int Group, int Refresh), GatherGroup> _groups = new();
    private readonly Dictionary<RuntimeMapKey, GatherRuntime> _runtimes = new();
    private readonly Random _random = new();
    private readonly World _world;
    private readonly WorldData _worldData;
    private readonly GameData _data;
    private readonly GameLoop _loop;
    private readonly LifeJobService _lifeJobs;
    private readonly CharacterService _character;
    private readonly FunctionService _functions;
    private readonly ILogger<GatheringService> _log;
    private long _serial;

    public GatheringService(SceneService scene, World world, WorldData worldData, GameData data, GameLoop loop,
        LifeJobService lifeJobs, CharacterService character, FunctionService functions, PublicLineManager lines,
        ILogger<GatheringService> log)
    {
        this._world = world;
        this._worldData = worldData;
        this._data = data;
        this._loop = loop;
        this._lifeJobs = lifeJobs;
        this._character = character;
        this._functions = functions;
        this._log = log;
        scene.Loaded.Add(s => s.InstanceId == 0 ? EnsureSpawnedAsync(s.RuntimeMap) : Task.CompletedTask);
        scene.Leaving.Add(s => InterruptAsync(s));
        lines.HasPendingWork.Add(HasPendingWork);
        lines.Retiring.Insert(0, RetireRuntimeAsync);
    }

    private sealed class GatherRuntime
    {
        public bool Retired;
        public int Pending;
        public readonly List<(TimeSpan Due, Func<Task> Work, GameSession? Session)> Deadlines = new();
        public readonly TaskCompletionSource Quiesced = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private sealed class GatherGroup(RuntimeMapKey runtime, GatherRuntime owner, IReadOnlyList<GatheringSpawnPoint> points)
    {
        public RuntimeMapKey Runtime { get; } = runtime;
        public GatherRuntime Owner { get; } = owner;
        public IReadOnlyList<GatheringSpawnPoint> Points { get; } = points;
        public HashSet<int> Occupied { get; } = new();
        public int Target => Math.Min(Points.Count, Math.Max(0, Points[0].TargetPopulation));
        public int Minimum => Math.Min(Target, Math.Max(0, Points[0].MinimumPopulation));
        public int RefreshSeconds => Math.Max(1, Points[0].RefreshParameter); // inferred unit; spec §5.3
    }

    private sealed class PendingGather(GatheringUnit node, Vec3 startPosition, long startedMs, long endMs,
        int gameId, string sourceKey)
    {
        public GatheringUnit Node { get; } = node;
        public Vec3 StartPosition { get; } = startPosition;
        public long StartedMs { get; } = startedMs;
        public long EndMs { get; } = endMs;
        public int GameId { get; } = gameId;
        public string SourceKey { get; } = sourceKey;
        public LifeJobService.GatherResolution? Resolution { get; set; }
        public RewardSettlement? Settlement { get; set; }
        public int? ExtraAwards { get; set; }
        public int Failures { get; set; }
        public bool Committed { get; set; }
    }

    public bool OwnsNode(long uid) => _nodes.ContainsKey(uid);

    /// <summary>Spawn RefreshInfo target population at nav-valid authored points; never turn these into MonsterUnits.</summary>
    public Task EnsureSpawnedAsync(long scene) => EnsureSpawnedAsync(RuntimeMapKey.Public(scene));

    public async Task EnsureSpawnedAsync(RuntimeMapKey runtime)
    {
        if (runtime.Kind != RuntimeMapKind.PublicLine || _worldData.Map(runtime.SceneEntry) is not { } map
            || _runtimes.ContainsKey(runtime)) return;
        var owner = new GatherRuntime();
        _runtimes[runtime] = owner;
        owner.Pending++;
        try
        {
            foreach (var sourceGroup in map.GatheringPoints.GroupBy(p => (p.GroupId, p.RefreshId)))
            {
                if (owner.Retired) break;
                var points = sourceGroup.OrderBy(p => p.Id).Where(p => ValidPoint(map, p)).ToArray();
                if (points.Length == 0) continue;
                var group = new GatherGroup(runtime, owner, points);
                _groups[(runtime, sourceGroup.Key.GroupId, sourceGroup.Key.RefreshId)] = group;
                await FillAsync(group, group.Target);
            }
        }
        finally { EndPending(owner); }
    }

    private static void EndPending(GatherRuntime owner)
    {
        if (--owner.Pending == 0 && owner.Retired) owner.Quiesced.TrySetResult();
    }

    public bool HasPendingWork(RuntimeMapKey runtime) => _runtimes.TryGetValue(runtime, out var owner)
        && (owner.Pending > 0 || owner.Deadlines.Count > 0);

    private void Schedule(GatherRuntime owner, TimeSpan delay, Func<Task> work, GameSession? session = null)
    {
        if (!owner.Retired) owner.Deadlines.Add((_loop.Now + delay, work, session));
    }

    public void Tick(GameTime time)
    {
        foreach (var owner in _runtimes.Values.ToArray())
        {
            if (owner.Retired || owner.Deadlines.Count == 0) continue;
            var due = owner.Deadlines.Where(d => d.Due <= time.Now).ToArray();
            owner.Deadlines.RemoveAll(d => d.Due <= time.Now);
            foreach (var deadline in due)
            {
                owner.Pending++;
                _loop.TrackRuntimeDetached("gathering deadline", () => RunDeadlineAsync(owner, deadline.Work));
            }
        }
    }

    private async Task RunDeadlineAsync(GatherRuntime owner, Func<Task> work)
    {
        try { if (!owner.Retired) await work(); }
        catch (Exception e) { _log.LogError(e, "Gathering deadline failed"); }
        finally { EndPending(owner); }
    }

    public async Task RetireRuntimeAsync(RuntimeMapKey runtime)
    {
        if (!_runtimes.Remove(runtime, out var owner)) return;
        owner.Retired = true;
        owner.Deadlines.Clear();
        if (owner.Pending > 0) await owner.Quiesced.Task;
        foreach (var pending in _pending.Where(p => p.Value.Node.RuntimeMap == runtime).Select(p => p.Key).ToArray())
            _pending.Remove(pending);
        foreach (var node in _nodes.Values.Where(n => n.RuntimeMap == runtime).ToArray())
        {
            _nodes.Remove(node.Uid);
            _settlingNodes.Remove(node.Uid);
            await _world.RemoveUnitAsync(node);
        }
        foreach (var key in _groups.Keys.Where(k => k.Runtime == runtime).ToArray()) _groups.Remove(key);
    }

    private static bool ValidPoint(MapWorld map, GatheringSpawnPoint p)
    {
        var grounded = map.Grounded(p.Position);
        return map.Nav == null || map.IsWalkable(grounded.X, grounded.Y, grounded.Z);
    }

    private async Task FillAsync(GatherGroup group, int target)
    {
        if (group.Owner.Retired) return;
        var map = _worldData.Map(group.Runtime.SceneEntry);
        if (map == null) return;
        foreach (var point in group.Points)
        {
            if (group.Owner.Retired || group.Occupied.Count >= target) break;
            if (group.Occupied.Contains(point.Id)) continue;
            var npcId = WeightedTemplate(point.Candidates);
            if (npcId == 0 || _data.Row("NPCConfig", npcId) is not { } npc) continue;
            long collectionId = GameData.Int(npc, "_iSubTypeValue");
            if (_data.Row("LifeCollectionConfig", collectionId) == null) continue;
            int interactionId = FirstInt(npc, "_iInteractID");
            if (interactionId <= 0 || _data.Row("NPCInteractConfig", interactionId) is not { } interaction
                || GameData.Int(interaction, "_iStatusID") != 5) continue;
            var pos = map.Grounded(point.Position);
            var node = new GatheringUnit
            {
                Uid = UidBase | ++_serial, SceneEntry = group.Runtime.SceneEntry, RuntimeMap = group.Runtime,
                X = pos.X, Y = pos.Y, Z = pos.Z, Face = point.Position.Face,
                Source = point, NpcId = npcId, CollectionId = collectionId, InteractionId = interactionId,
                GenerationId = Guid.NewGuid().ToString("N"),
                Name = _data.ClientTextOf(npc, "_kName"), Camp = (int)GameData.Int(npc, "_iCamp"),
                InteractDistance = Math.Max(1, GameData.Int(npc, "_iInteractDistance")),
                SharedLimit = NthInt(npc, "_iCTimes", 0), PersonalLimit = NthInt(npc, "_iCTimes", 1),
                CooldownSeconds = Math.Max(0, (int)GameData.Int(npc, "_iInterval")),
                BornTimeMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            };
            group.Occupied.Add(point.Id);
            _nodes[node.Uid] = node;
            await _world.AddUnitAsync(node);
            if (group.Owner.Retired && _nodes.Remove(node.Uid)) await _world.RemoveUnitAsync(node);
        }
        _log.LogInformation("Gathering group {Group}/{Refresh} on {Scene}: {Count}/{Target} authored points live",
            group.Points[0].GroupId, group.Points[0].RefreshId, group.Runtime, group.Occupied.Count, group.Target);
    }

    private int WeightedTemplate(IReadOnlyList<(int NpcId, int Weight)> candidates)
    {
        int total = candidates.Sum(c => c.Weight);
        if (total <= 0) return 0;
        int roll = _random.Next(total);
        foreach (var (id, weight) in candidates)
        {
            roll -= weight;
            if (roll < 0) return id;
        }
        return candidates[^1].NpcId;
    }

    private static int FirstInt(JsonElement row, string field) => NthInt(row, field, 0);
    private static int NthInt(JsonElement row, string field, int index) =>
        row.TryGetProperty(field, out var values) && values.ValueKind == JsonValueKind.Array && values.GetArrayLength() > index
            ? (int)(GameData.Number(values[index]) ?? 0) : 0;

    /// <summary>5010 map/point count wins for the client; read it for server refusal across AOI/reconnect too (§2.2).</summary>
    private static int PersonalCount(GameSession session, GatheringUnit node)
    {
        int live = node.PersonalCounts.GetValueOrDefault(session.Role!.Id);
        int saved = session.LifeJob?.GeneralCollectionTimesByMap.GetValueOrDefault(node.SceneEntry)?
            .Times.GetValueOrDefault(node.Source.Id) ?? 0;
        return Math.Max(live, saved);
    }

    private int InteractionCondition(GameSession session, GatheringUnit node)
    {
        if (_data.Row("NPCInteractConfig", node.InteractionId) is not { } interaction
            || _data.Row("NPCConfig", node.NpcId) is not { } npc) return ErrorCodes.NpcParameter;
        if (GameData.Int(interaction, "_iNPCCondition") != 0) return ErrorCodes.NpcParameter;
        return !_functions.ConditionMet(session, GameData.Int(npc, "_kNpcShowCondition"))
            || !_functions.ConditionMet(session, GameData.Int(interaction, "_iCondition"))
            || !_functions.ConditionMet(session, GameData.Int(interaction, "_iButtenCond"))
            ? ErrorCodes.LifeRequirements : 0;
    }

    /// <summary>32700 must echo req; player position/time/target fields never authorize an actor (spec §3.1).</summary>
    public async Task<bool> TryInteractAsync(GameSession session, NpcInteractionReq req)
    {
        if (!_nodes.TryGetValue(req.NpcUid, out var node)) return false;
        long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        int interactionError = session.Role == null ? ErrorCodes.NpcMissing : InteractionCondition(session, node);
        int code = session.Role == null ? ErrorCodes.NpcMissing
            : session.SceneLoading || session.SceneEntry != node.SceneEntry || session.WorldScene != node.WorldScene
              || !_runtimes.TryGetValue(node.RuntimeMap, out var owner) || owner.Retired
              || req.SceneConfigId != session.SceneEntry ? ErrorCodes.SwitchingScenes
            : session.Dead || req.InteractionId != node.InteractionId ? ErrorCodes.NpcParameter
            : !session.VisibleUnits.Contains(node) || !Near(session, node) ? ErrorCodes.NpcTooFar
            : node.Depleted || _settlingNodes.Contains(node.Uid) ? ErrorCodes.Cooldown
            : _pending.ContainsKey(session) ? ErrorCodes.Cooldown
            : node.PersonalLimit > 0 && PersonalCount(session, node) >= node.PersonalLimit ? ErrorCodes.Cooldown
            : node.Cooldowns.GetValueOrDefault(session.Role.Id) >= now / 1000 ? ErrorCodes.Cooldown
            : interactionError != 0 ? interactionError
            : _lifeJobs.CheckGather(session, node.CollectionId);
        await session.SendGameAsync(Interaction, new NpcInteractionRsp { Result = code, Req = req });
        if (code != 0) return true;

        var collection = _data.Row("LifeCollectionConfig", node.CollectionId)!.Value;
        int duration = Math.Max(1, _lifeJobs.GatheringDuration(session, node.CollectionId));
        int playId = FirstInt(collection, "_iPlayParam");
        int gameId = SelectedGame(collection);
        if (gameId > 0 && _data.Row("LifeGameConfig", gameId) is { } game)
        {
            // The client starts the game after PublicParamConfig's countdown, then runs
            // LifeGameConfig._iDuration before posting 33516 (inferred from UI/config).
            var param = _data.Table("PublicParamConfig");
            int countdown = param.TryGetValue("LifeJobGameCountdown", out var seconds)
                ? Math.Max(0, (int)(GameData.Number(seconds) ?? 0)) * 1000 : 0;
            duration = countdown + Math.Max(1, (int)GameData.Int(game, "_iDuration"));
        }
        else gameId = 0;

        long end = now + duration;
        var state = new CollectionState { PlayInfo = new InteractPlayInfo { PlayConfigId = playId, PlayEndTimeStamp = end } };
        if (gameId > 0)
            state.MinigameInfo = new CollectMinigameInfo
            {
                MiniGameId = gameId, Round = 1, NpcId = node.Uid, JobId = (int)GameData.Int(collection, "_iJobId")
            };
        // (inferred) The wire has no retry token. This server-admitted cast owns one source;
        // a refreshed actor, even on the same authored point, is a new generation.
        string source = $"gather/{RewardSourceId.ProcessRun}/{node.GenerationId}/{Guid.NewGuid():N}";
        _pending[session] = new PendingGather(node, session.Position.Clone(), now, end, gameId, source);
        await session.SendGameAsync(StateChange, new InteractChangeNtf
        {
            Uid = session.Role!.Id,
            DataInfo = new InteractInfo
            {
                EType = InteractInfo.Types.EState.Collection, StateId = 5,
                ActiveObjId = node.Uid, InteractConfigId = node.InteractionId, CollectionInfo = state
            },
        });
        if (gameId == 0)
            Schedule(_runtimes[node.RuntimeMap], TimeSpan.FromMilliseconds(duration), () => CompleteAsync(session, node.Uid), session);
        else
            Schedule(_runtimes[node.RuntimeMap], TimeSpan.FromMilliseconds(duration + 5000), () => ExpireMiniGameAsync(session, node.Uid), session);
        return true;
    }

    /// <summary>Choose an _iGameplay weighted entry; only an explicit configured game id enters 33516 (inferred).</summary>
    private int SelectedGame(JsonElement collection)
    {
        if (!collection.TryGetProperty("_iGameplay", out var options) || options.ValueKind != JsonValueKind.Array) return 0;
        var choices = new List<(int Weight, int GameId)>();
        foreach (var option in options.EnumerateArray())
        {
            if (option.ValueKind != JsonValueKind.Array || option.GetArrayLength() < 2) continue;
            int weight = (int)(GameData.Number(option[0]) ?? 0);
            int gameId = (int)(GameData.Number(option[1]) ?? 0);
            if (weight > 0) choices.Add((weight, gameId));
        }
        int total = choices.Sum(c => c.Weight);
        if (total <= 0) return 0;
        int roll = _random.Next(total);
        foreach (var (weight, gameId) in choices)
        {
            roll -= weight;
            if (roll < 0) return gameId > 0 && _data.Row("LifeGameConfig", gameId) != null ? gameId : 0;
        }
        return 0;
    }

    private static bool Near(GameSession session, GatheringUnit node)
    {
        float dx = session.Position.X - node.X, dz = session.Position.Z - node.Z;
        float reach = node.InteractDistance + InteractionSlack;
        float dy = session.Position.Y - node.Y;
        return dx * dx + dy * dy + dz * dz <= reach * reach;
    }

    private async Task CompleteAsync(GameSession session, long nodeUid)
    {
        if (!_pending.TryGetValue(session, out var pending) || pending.Node.Uid != nodeUid || pending.GameId != 0) return;
        if (DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() < pending.EndMs)
        {
            if (_runtimes.TryGetValue(pending.Node.RuntimeMap, out var owner))
                Schedule(owner, TimeSpan.FromMilliseconds(pending.EndMs - DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()),
                    () => CompleteAsync(session, nodeUid), session);
            return;
        }
        await SettleAsync(session, pending, 0);
    }

    /// <summary>33516 accepts only a selected, still-pending node/game; replay cannot grant twice (spec §3.3).</summary>
    public async Task FinishMiniGameAsync(GameSession session, FinishLifeJobGameReq req)
    {
        int code = !_pending.TryGetValue(session, out var pending) || pending.GameId == 0
            || pending.GameId != req.GameId || pending.Node.Uid != req.NpcGuid
            || req.Rating is < 1 or > 5 || _data.Row("LifeGamePlayAward", req.Rating) is null
            ? ErrorCodes.NpcParameter : 0;
        if (code == 0)
        {
            long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            if (now < pending!.EndMs || now > pending.EndMs + 5000) code = ErrorCodes.Cooldown;
            else
            {
                // The exact rating is client-reported; the server bounds it to the selected pending
                // game, configured rating rows and completed game window. Scoring is not observable here.
                int extra = Math.Max(0, (int)GameData.Int(_data.Row("LifeGamePlayAward", req.Rating)!.Value, "_iExtraAwardNum"));
                code = await SettleAsync(session, pending, extra);
            }
        }
        await session.SendGameAsync(MiniGame, new FinishLifeJobGameResp { Result = code });
    }

    private async Task ExpireMiniGameAsync(GameSession session, long nodeUid)
    {
        if (_pending.TryGetValue(session, out var pending) && pending.Node.Uid == nodeUid && pending.GameId > 0)
            await InterruptAsync(session);
    }

    private async Task<int> SettleAsync(GameSession session, PendingGather pending, int extraAwards)
    {
        if (!_pending.TryGetValue(session, out var current) || !ReferenceEquals(current, pending))
            return ErrorCodes.Cooldown;
        var node = pending.Node;
        if (!_runtimes.TryGetValue(node.RuntimeMap, out var owner) || owner.Retired) return ErrorCodes.NpcMissing;
        if (pending.ExtraAwards is { } fixedExtra && fixedExtra != extraAwards) return ErrorCodes.NpcParameter;
        owner.Deadlines.RemoveAll(d => d.Session == session);
        // Reward sends can yield on CharacterService.Lock. Reserve the node before any yield so two
        // characters cannot both consume a shared-limit-one actor in the same game-loop turn.
        if (!_settlingNodes.Add(node.Uid))
        {
            await SendIdleAsync(session, interrupted: true);
            return ErrorCodes.Cooldown;
        }
        owner.Pending++;
        bool retainReservation = false;
        try
        {
            int code = 0;
            if (!pending.Committed)
            {
                if (pending.Failures > 0)
                {
                    bool claimed;
                    try { claimed = await _character.RewardSourceCommittedAsync(pending.SourceKey); }
                    catch { retainReservation = true; throw; }
                    if (claimed)
                    {
                        retainReservation = true;
                        throw new InvalidOperationException("Committed gathering source requires role reload");
                    }
                }
                code = ValidatePending(session, pending);
                if (code == 0)
                {
                    using var held = await CharacterService.Lock(session,
                        [Ownership.ReservationKey.Entity("gathering", node.GenerationId)]);
                    code = ValidatePending(session, pending);
                    if (code == 0 && pending.Resolution == null)
                    {
                        var resolved = _lifeJobs.ResolveGather(session, node.CollectionId, extraAwards,
                            node.SceneEntry, node.Source.Id);
                        code = resolved.Error;
                        pending.Resolution = resolved.Resolution;
                        if (code == 0)
                        {
                            pending.ExtraAwards = extraAwards;
                            pending.Settlement = new RewardSettlement(pending.SourceKey, "gather-yield");
                        }
                    }
                    if (code == 0)
                    {
                        try
                        {
                            code = await _lifeJobs.CompleteGatherAsync(session, pending.Resolution!,
                                pending.Settlement!, () =>
                                {
                                    // The callback runs after the writer commit and before any economic notice.
                                    node.TotalCollectCount++;
                                    node.PersonalCounts[session.Role!.Id] = PersonalCount(session, node);
                                    if (node.CooldownSeconds > 0)
                                        node.Cooldowns[session.Role.Id] = DateTimeOffset.UtcNow.ToUnixTimeSeconds() + node.CooldownSeconds;
                                    pending.Committed = true;
                                });
                        }
                        catch (Exception e)
                        {
                            if (pending.Committed && pending.Settlement!.Committed)
                            {
                                _log.LogWarning(e, "Committed gathering {Source} failed to publish a later notice", pending.SourceKey);
                                code = 0;
                            }
                            else
                            {
                                bool committed;
                                try { committed = await _character.RewardSourceCommittedAsync(pending.SourceKey); }
                                catch
                                {
                                    retainReservation = true;
                                    throw;
                                }
                                if (committed)
                                {
                                    retainReservation = true;
                                    throw new InvalidOperationException("Committed gathering source requires role reload", e);
                                }
                                _log.LogError(e, "Gathering {Source} failed before commit", pending.SourceKey);
                                if (++pending.Failures == 1 && !owner.Retired)
                                {
                                    Schedule(owner, TimeSpan.FromSeconds(1), async () =>
                                    {
                                        await SettleAsync(session, pending, pending.ExtraAwards!.Value);
                                    });
                                    return ErrorCodes.Cooldown;
                                }
                                code = ErrorCodes.Cooldown;
                            }
                        }
                    }
                }
            }
            _pending.Remove(session); // settlement is terminal before any fallible AOI presentation
            if (code == 0)
            {
                try { await _world.ToUnitViewersAsync(node, EntityBaseChange, node.CollectionChange()); }
                finally { if (node.Depleted) await DepleteAsync(node); }
            }
            await SendIdleAsync(session, interrupted: code != 0);
            return code;
        }
        finally
        {
            try
            {
                if (!retainReservation) _settlingNodes.Remove(node.Uid);
                else
                {
                    if (session.Owner is { } roleOwner) roleOwner.State = Ownership.RoleOwnerState.RecoveryRequired;
                    session.CancelPendingCharacterIntents();
                    session.AbortForRecovery();
                    // This deadline belongs to the resource, so disconnect cannot cancel reconciliation.
                    ScheduleSourceRecovery(session, pending, owner);
                }
            }
            finally { EndPending(owner); }
        }
    }

    private void ScheduleSourceRecovery(GameSession session, PendingGather pending, GatherRuntime owner)
    {
        if (owner.Retired) return;
        var delay = TimeSpan.FromSeconds(5);
        // Only the runtime callback claims this sentinel; the ordinary ticker cannot steal recovery work.
        var deadline = (TimeSpan.MaxValue, (Func<Task>)(() => ReconcileSourceAsync(session, pending, owner)), (GameSession?)null);
        owner.Deadlines.Add(deadline);
        // An Apply fault quarantines the role and therefore blocks the legacy Realm ticker.
        // Source reconciliation needs only a read and this resource's runtime; it must still run.
        try
        {
            _loop.ScheduleRuntime(delay, async () =>
        {
            if (owner.Retired || !owner.Deadlines.Remove(deadline)) return;
            owner.Pending++;
            await RunDeadlineAsync(owner, deadline.Item2);
        });
        }
        catch { owner.Deadlines.Remove(deadline); throw; }
    }

    private async Task ReconcileSourceAsync(GameSession session, PendingGather pending, GatherRuntime owner)
    {
        if (owner.Retired) return;
        bool paid;
        try { paid = await _character.RewardSourceCommittedAsync(pending.SourceKey); }
        catch
        {
            ScheduleSourceRecovery(session, pending, owner);
            return;
        }
        var node = pending.Node;
        if (paid && !pending.Committed)
        {
            pending.Committed = true;
            node.TotalCollectCount++;
            if (pending.Resolution is { } resolved)
            {
                if (resolved.Life.GeneralCollectionTimesByMap.TryGetValue(node.SceneEntry, out var counts))
                    node.PersonalCounts[resolved.RoleId] = counts.Times.GetValueOrDefault(node.Source.Id);
                if (node.CooldownSeconds > 0)
                    node.Cooldowns[resolved.RoleId] = DateTimeOffset.UtcNow.ToUnixTimeSeconds() + node.CooldownSeconds;
            }
        }
        if (_pending.TryGetValue(session, out var current) && ReferenceEquals(current, pending)) _pending.Remove(session);
        _settlingNodes.Remove(node.Uid);
        if (node.Depleted) await DepleteAsync(node);
    }

    private int ValidatePending(GameSession session, PendingGather pending)
    {
        var node = pending.Node;
        if (session.Role == null || session.SceneLoading || session.Dead || session.SceneEntry != node.SceneEntry
            || session.WorldScene != node.WorldScene || session.LastCombatMs >= pending.StartedMs
            || !_runtimes.TryGetValue(node.RuntimeMap, out var owner) || owner.Retired
            || !_nodes.ContainsKey(node.Uid) || node.Depleted || !Near(session, node)
            || Moved(session.Position, pending.StartPosition)) return ErrorCodes.NpcMissing;
        if (node.PersonalLimit > 0 && PersonalCount(session, node) >= node.PersonalLimit
            || node.Cooldowns.GetValueOrDefault(session.Role.Id) >= DateTimeOffset.UtcNow.ToUnixTimeSeconds())
            return ErrorCodes.Cooldown;
        int interactionError = InteractionCondition(session, node);
        return interactionError != 0 ? interactionError : _lifeJobs.CheckGather(session, node.CollectionId);
    }

    private async Task DepleteAsync(GatheringUnit node)
    {
        if (!_nodes.Remove(node.Uid)) return;
        var key = (node.RuntimeMap, node.Source.GroupId, node.Source.RefreshId);
        if (!_groups.TryGetValue(key, out var group)) { await _world.RemoveUnitAsync(node); return; }
        group.Occupied.Remove(node.Source.Id);
        Schedule(group.Owner, TimeSpan.FromSeconds(group.RefreshSeconds), async () =>
        {
            if (group.Owner.Retired) return;
            // One timer per consumed node restores every depletion. Under the configured minimum,
            // refill to target at once (inferred RefreshInfo population policy).
            await FillAsync(group, group.Occupied.Count < group.Minimum
                ? group.Target : Math.Min(group.Target, group.Occupied.Count + 1));
        });
        await _world.RemoveUnitAsync(node);
    }

    /// <summary>An accepted WALK interrupts only once the authoritative position actually moved (spec §4).</summary>
    public Task PlayerMovedAsync(GameSession session)
    {
        if (!_pending.TryGetValue(session, out var pending)) return Task.CompletedTask;
        return Moved(session.Position, pending.StartPosition) ? InterruptAsync(session) : Task.CompletedTask;
    }

    private static bool Moved(Vec3 current, Vec3 start)
    {
        float dx = current.X - start.X, dy = current.Y - start.Y, dz = current.Z - start.Z;
        return dx * dx + dy * dy + dz * dz > 0.01f;
    }

    /// <summary>Scene leave/disconnect/movement cancels any pending reward. Disconnect may set notify=false.</summary>
    public async Task InterruptAsync(GameSession session, bool notify = true)
    {
        if (!_pending.Remove(session, out var pending)) return;
        if (_runtimes.TryGetValue(pending.Node.RuntimeMap, out var owner)) owner.Deadlines.RemoveAll(d => d.Session == session);
        if (notify && session.Role != null) await SendIdleAsync(session, interrupted: true);
    }

    private static Task SendIdleAsync(GameSession session, bool interrupted) => session.Role == null ? Task.CompletedTask
        : session.SendGameAsync(StateChange, new InteractChangeNtf
        {
            Uid = session.Role.Id, DataInfo = new InteractInfo { EType = InteractInfo.Types.EState.Idle }, Interrupt = interrupted,
        });
}

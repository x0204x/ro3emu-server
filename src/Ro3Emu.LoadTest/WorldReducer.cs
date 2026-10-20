using System.Collections.Concurrent;
using System.Diagnostics;
using Ro3Emu.Bot;
using Romsg;
using Ro3Emu.Server.Game;
using Ro3Emu.Server.Maps;

namespace Ro3Emu.LoadTest;

/// <summary>Bounded causal view of one client's scene. Counts every packet but retains only current unit identities.</summary>
public sealed class WorldReducer(int maxEntities = 4096)
{
    private const int MaxReplyIds = 32;
    private const int MaxReplyBodyBytes = 256;
    private readonly object _lock = new();
    private readonly Dictionary<ulong, EntityBase> _entities = new();
    private readonly Dictionary<uint, long> _bytes = new();
    private readonly Dictionary<uint, long> _counts = new();
    private readonly List<string> _faults = new();
    private readonly Dictionary<uint, ReplySummary> _lastReplies = new();
    private readonly LinkedList<uint> _replyOrder = new();
    private readonly Dictionary<uint, LinkedListNode<uint>> _replyNodes = new();
    private ulong _selfUid;
    private bool _selfSeen;
    private int _readyScenes;
    private SceneLocation _location = SceneLocation.Unknown;
    public SceneLocation Location => Volatile.Read(ref _location);
    public Action<MovementChangeNtf>? OnMovement { get; set; }
    public Action<GetRewardNotify>? OnReward { get; set; }
    public Action<ClimTowerEndNotify>? OnTowerEnd { get; set; }
    public int MaxVisible { get; private set; }
    public long InboundBytes { get; private set; }
    public long InboundCount { get; private set; }

    /// <summary>Retain only the most recent typed game reply per message ID for this measured phase.</summary>
    public void RecordReply(BotEvent reply, double elapsedMs)
    {
        if (reply.Kind != BotEventKind.Game) return; // Non-game events have no protocol message ID.
        lock (_lock)
        {
            if (_replyNodes.Remove(reply.MsgId, out var existing)) _replyOrder.Remove(existing);
            else if (_lastReplies.Count >= MaxReplyIds)
            {
                uint evicted = _replyOrder.First!.Value;
                _replyOrder.RemoveFirst();
                _replyNodes.Remove(evicted);
                _lastReplies.Remove(evicted);
            }
            _replyNodes[reply.MsgId] = _replyOrder.AddLast(reply.MsgId);
            _lastReplies[reply.MsgId] = new ReplySummary(reply.Body.Length,
                reply.Body.AsSpan(0, Math.Min(reply.Body.Length, MaxReplyBodyBytes)).ToArray(),
                reply.Body.Length > MaxReplyBodyBytes, double.IsFinite(elapsedMs) && elapsedMs >= 0 ? elapsedMs : 0);
        }
    }

    public void Observe(BotEvent e)
    {
        GetRewardNotify? reward = null;
        ClimTowerEndNotify? towerEnd = null;
        lock (_lock)
        {
            InboundCount++;
            InboundBytes += e.Body.Length;
            if (e.Kind == BotEventKind.Disconnect) { _location = _location with { Ready = false }; Fault("unrequested disconnect"); return; }
            if (e.Kind != BotEventKind.Game) return;
            _bytes[e.MsgId] = _bytes.GetValueOrDefault(e.MsgId) + e.Body.Length;
            _counts[e.MsgId] = _counts.GetValueOrDefault(e.MsgId) + 1;
            try
            {
                if (e.MsgId == MsgId.SwitchSceneLine)
                {
                    var scene = SwitchSceneLineResp.Parser.ParseFrom(e.Body);
                    // A refusal does not invalidate the currently loaded world.
                    if (scene.Result != 0) return;
                    _entities.Clear(); _selfUid = 0; _selfSeen = false;
                    _location = new(scene.SceneEntry, scene.SceneID, scene.Linenum, false);
                }
                else if (e.MsgId == MsgId.EntitySelf)
                {
                    var self = EntitySelf.Parser.ParseFrom(e.Body).Base;
                    _selfUid = self.Uuid;
                    _selfSeen = true;
                    _entities[self.Uuid] = self;
                }
                else if (e.MsgId == MsgId.SelfInfoEndFrameNotify)
                {
                    if (!_selfSeen) Fault("scene end before EntitySelf");
                    else { _readyScenes++; _location = _location with { Ready = true }; }
                }
                else if (e.MsgId == GameAoiSink.SyncAoiView)
                {
                    var change = AoIViewChangeData.Parser.ParseFrom(e.Body);
                    if (change.BReset)
                    {
                        var self = _selfUid != 0 && _entities.TryGetValue(_selfUid, out var currentSelf) ? currentSelf : null;
                        _entities.Clear();
                        if (self != null) _entities[_selfUid] = self;
                    }
                    foreach (var uid in change.LeaveAoi) _entities.Remove(uid);
                    foreach (var entity in change.Bases)
                    {
                        if (!_entities.ContainsKey(entity.Uuid) && _entities.Count >= maxEntities)
                            Fault("entity capacity exceeded");
                        else _entities[entity.Uuid] = entity;
                    }
                    MaxVisible = Math.Max(MaxVisible, _entities.Values.Count(x => x.EntityType == 3 && x.Uuid != _selfUid));
                }
                else if (e.MsgId == GameAoiSink.MovementChange)
                {
                    var movement = MovementChangeNtf.Parser.ParseFrom(e.Body);
                    if (!_entities.ContainsKey((ulong)movement.Uid)) Fault($"movement before base: {movement.Uid}");
                    else
                    {
                        if (movement.MovementInfo != null) _entities[(ulong)movement.Uid].MovementInfo = movement.MovementInfo;
                        OnMovement?.Invoke(movement);
                    }
                }
                else if (e.MsgId == CharacterService.AoiAttrChange)
                {
                    var update = AOIAttrChange.Parser.ParseFrom(e.Body);
                    if (!_entities.ContainsKey(update.EntityUuid)) Fault($"attribute before base: {update.EntityUuid}");
                }
                else if (e.MsgId == CharacterService.EntityBaseChange)
                {
                    var update = EntityChange.Parser.ParseFrom(e.Body);
                    foreach (var entity in update.Bases)
                    {
                        if (!_entities.ContainsKey(entity.Uuid)) Fault($"entity update before base: {entity.Uuid}");
                        // 31049 carries a partial EntityBase; keep the original identity and position.
                    }
                }
                else if (e.MsgId == CharacterService.GetRewardNotify)
                    reward = GetRewardNotify.Parser.ParseFrom(e.Body);
                else if (e.MsgId == TowerService.End)
                    towerEnd = ClimTowerEndNotify.Parser.ParseFrom(e.Body);
                else if (e.MsgId == CharacterService.OnePropertyChange)
                    _ = OnePropertyChange.Parser.ParseFrom(e.Body); // self-only property lane, not AOI identity
            }
            catch (Exception ex) when (ex is Google.Protobuf.InvalidProtocolBufferException or ArgumentException)
            {
                Fault($"invalid {e.MsgId}: {ex.Message}");
            }
        }
        if (reward != null) OnReward?.Invoke(reward);
        if (towerEnd != null) OnTowerEnd?.Invoke(towerEnd);
    }

    private void Fault(string text)
    {
        if (_faults.Count < 32) _faults.Add(text);
    }

    public ReducerSnapshot Snapshot()
    {
        lock (_lock) return new(InboundCount, InboundBytes, MaxVisible, _entities.Count, _readyScenes,
            new Dictionary<uint, long>(_counts), new Dictionary<uint, long>(_bytes), _faults.ToArray(),
            _lastReplies.ToDictionary(x => x.Key, x => x.Value with { BodyPrefix = x.Value.BodyPrefix.ToArray() }))
        { Location = _location };
    }

    /// <summary>Start a measured phase without discarding the causal entity bases learned in setup.</summary>
    public void BeginMeasurement()
    {
        lock (_lock)
        {
            InboundCount = 0;
            InboundBytes = 0;
            _bytes.Clear();
            _counts.Clear();
            _lastReplies.Clear();
            _replyOrder.Clear();
            _replyNodes.Clear();
            MaxVisible = _entities.Values.Count(x => x.EntityType == 3 && x.Uuid != _selfUid);
        }
    }

    /// <summary>Other players currently shown, with a position.</summary>
    public EntityBase[] Players()
    {
        lock (_lock) return _entities.Values.Where(x => x.EntityType == 3 && x.Uuid != _selfUid && x.MovementInfo?.Pos != null)
            .Select(x => x.Clone()).ToArray();
    }

    /// <summary>The own entity's last known position (after a revive teleport, for example).</summary>
    public Vec3? SelfPosition()
    {
        lock (_lock) return _selfUid != 0 && _entities.TryGetValue(_selfUid, out var self) ? self.MovementInfo?.Pos?.Clone() : null;
    }

    public EntityBase[] Monsters()
    {
        lock (_lock) return _entities.Values.Where(x => x.EntityType == 1 && x.MovementInfo?.Pos != null).Select(x => x.Clone()).ToArray();
    }

    public bool Sees(long role) { lock (_lock) return _entities.ContainsKey((ulong)role) && (ulong)role != _selfUid; }
    public EntityBase[] GatheringNodes()
    {
        lock (_lock) return _entities.Values.Where(x => x.CollectionInfo != null && x.MovementInfo?.Pos != null)
            .Select(x => x.Clone()).ToArray();
    }
}

public sealed record ReducerSnapshot(long Count, long Bytes, int MaxVisiblePlayers, int CurrentEntities, int ReadyScenes,
    Dictionary<uint, long> ByIdCount, Dictionary<uint, long> ByIdBytes, string[] Faults,
    Dictionary<uint, ReplySummary> LastReplies)
{
    public SceneLocation Location { get; init; } = SceneLocation.Unknown;
}

/// <summary>70010 identifies the runtime, including separate private rooms with the same line number.</summary>
public sealed record SceneLocation(long SceneEntry, long SceneId, int LineNumber, bool Ready)
{
    public static readonly SceneLocation Unknown = new(0, 0, 0, false);
    public bool SameRuntime(SceneLocation other) => Ready && other.Ready && SceneEntry > 0 && SceneId != 0 && LineNumber > 0 &&
        SceneEntry == other.SceneEntry && SceneId == other.SceneId && LineNumber == other.LineNumber;
}

public static class PeerPairing
{
    public static BotActor? Select(BotActor actor, IReadOnlyList<BotActor> candidates, bool requireVisibility)
    {
        int start = 0;
        for (int i = 0; i < candidates.Count; i++) if (candidates[i].RoleId == actor.RoleId) { start = i + 1; break; }
        // Ring pairing spreads simultaneous chat expectations over receivers (the inbox has a fixed bound).
        for (int offset = 0; offset < candidates.Count; offset++)
        {
            var peer = candidates[(start + offset) % candidates.Count];
            if (peer.RoleId != actor.RoleId && actor.Location.SameRuntime(peer.Location) &&
                (!requireVisibility || peer.Reducer.Sees(actor.RoleId))) return peer;
        }
        return null;
    }
}

public sealed record ReplySummary(int OriginalLength, byte[] BodyPrefix, bool Truncated, double ElapsedMs);

/// <summary>Harness-side same-process movement association; no timestamp is smuggled through client positions.</summary>
public sealed class MovementCorrelation
{
    private sealed class OfferRecord(long stamp, long viewer, SceneLocation location)
    {
        public long Stamp { get; } = stamp;
        public long Viewer { get; } = viewer;
        public SceneLocation Location { get; } = location;
        public int Seen;
    }
    private readonly ConcurrentDictionary<(long Role, int X, int Z, bool Stop), OfferRecord> _pending = new();
    private readonly ConcurrentQueue<((long Role, int X, int Z, bool Stop) Key, OfferRecord Record)> _order = new();
    private readonly FixedLatency _latencies = new();
    private readonly ConcurrentDictionary<long, byte> _offeredRoles = new();
    private readonly ConcurrentDictionary<long, (long Viewer, SceneLocation Location)> _lastPair = new();
    private int _queueCount;
    private long _unmatched;
    private long _neverSeen;
    private long _offers;
    private long _observedOffers;
    private long _ambiguous;
    public void Offer(long role, float x, float z, bool stop, long viewer = 0, SceneLocation? location = null)
    {
        var key = (role, BitConverter.SingleToInt32Bits(x), BitConverter.SingleToInt32Bits(z), stop);
        Interlocked.Increment(ref _offers);
        var record = new OfferRecord(Stopwatch.GetTimestamp(), viewer, location ?? SceneLocation.Unknown);
        _offeredRoles.TryAdd(role, 0);
        _lastPair[role] = (viewer, record.Location);
        if (_pending.TryGetValue(key, out var previous))
        {
            Interlocked.Increment(ref _ambiguous);
            if (Volatile.Read(ref previous.Seen) == 0) Interlocked.Increment(ref _neverSeen);
        }
        _pending[key] = record;
        _order.Enqueue((key, record));
        Interlocked.Increment(ref _queueCount);
        while (Volatile.Read(ref _queueCount) > 8192 && _order.TryDequeue(out var old))
        {
            Interlocked.Decrement(ref _queueCount);
            if (((ICollection<KeyValuePair<(long Role, int X, int Z, bool Stop), OfferRecord>>)_pending)
                .Remove(new KeyValuePair<(long Role, int X, int Z, bool Stop), OfferRecord>(old.Key, old.Record)) &&
                Volatile.Read(ref old.Record.Seen) == 0)
                Interlocked.Increment(ref _neverSeen);
        }
    }
    public void Seen(MovementChangeNtf movement, long viewer = 0, SceneLocation? location = null)
    {
        var pos = movement.MovementInfo?.Pos;
        if (!_offeredRoles.ContainsKey(movement.Uid)) return;
        bool stop = movement.MovementInfo?.MoveType == MovementType.MoveTypeNone;
        if (pos == null || !_pending.TryGetValue((movement.Uid, BitConverter.SingleToInt32Bits(pos.X),
                BitConverter.SingleToInt32Bits(pos.Z), stop), out var offer))
        {
            // Only the registered viewer is a proof source. Other fan-out packets are workload, not unmatched proofs.
            if (viewer == 0 || _lastPair.TryGetValue(movement.Uid, out var pair) && pair.Viewer == viewer &&
                pair.Location.SameRuntime(location ?? SceneLocation.Unknown)) Interlocked.Increment(ref _unmatched);
            return;
        }
        if (offer.Viewer != viewer || viewer != 0 && !offer.Location.SameRuntime(location ?? SceneLocation.Unknown)) return;
        if (Interlocked.Exchange(ref offer.Seen, 1) == 0) Interlocked.Increment(ref _observedOffers);
        _latencies.Add(Stopwatch.GetElapsedTime(offer.Stamp).TotalMilliseconds);
    }
    public (LatencySummary Latencies, long Unmatched, long NeverSeen, long Offers, long ObservedOffers, long Ambiguous) Snapshot() =>
        (_latencies.Snapshot(), Interlocked.Read(ref _unmatched),
            Interlocked.Read(ref _neverSeen) + _pending.Values.LongCount(x => Volatile.Read(ref x.Seen) == 0),
            Interlocked.Read(ref _offers), Interlocked.Read(ref _observedOffers), Interlocked.Read(ref _ambiguous));
}

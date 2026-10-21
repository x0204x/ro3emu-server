using System.Globalization;
using System.Text.Json;
using Google.Protobuf;
using Romsg;
using Ro3Emu.Server.Accounts;
using Ro3Emu.Server.Data;
using Ro3Emu.Server.Maps;

namespace Ro3Emu.Server.Game;

/// <summary>
/// Player and system stalls (LC_DAL_Stall and LC_StallHelper). 5058 is metadata;
/// 33408/33411 and 33409/33413 own the seller/buyer stock views. MarketStore commits escrow and mail together.
/// Uncaptured backend policies are marked (inferred) at their implementation sites.
/// </summary>
public sealed partial class StallsService
{
    public const uint Sync = 5058, Start = 33401, Put = 33403, Remove = 33404, Update = 33405, Buy = 33406,
        End = 33407, Info = 33408, View = 33409, Rename = 33410, Change = 33411, BuyerChange = 33413,
        Leave = 33414, Prices = 33415, Settlement = 33416, Top = 33417, Share = 33418;
    private const string Kind = "stall";
    private readonly MarketStore _market;
    private readonly GameData _data;
    private readonly CharacterService _character;
    private readonly TradeService _trade;
    private readonly RoleStore _roles;
    private readonly SessionRegistry _sessions;
    private readonly FunctionService _functions;
    private readonly SeasonService _seasons;
    private readonly World _world;
    private readonly PublicLineManager _lines;
    private readonly TimeProvider _clock;
    private readonly GameLoop _loop;
    private readonly int _nameMaxLength;
    private readonly Dictionary<(string Key, int Line), StallActor> _actors = new();
    private long _nextProjectionId = StallActor.ProjectionIdBase;
    private readonly HashSet<RuntimeMapKey> _activeLines = new();
    private readonly Dictionary<RuntimeMapKey, int> _pendingProjections = new();
    private readonly Dictionary<RuntimeMapKey, int> _openingClaims = new();
    private readonly Dictionary<RuntimeMapKey, HashSet<string>> _pinnedPlayerStalls = new();
    private readonly Dictionary<long, long> _lastShare = new();
    // Visited public scenes whose system stalls the one-second maintenance populates, replaces and restocks (C/D 15:
    // logins and scene loads add their scene). Before, one scene (the last loaded) was kept.
    private readonly HashSet<long> _maintenanceScenes = [];
    internal Func<Task>? OpeningCommittedForTests { get; set; }

    public StallsService(MarketStore market, GameData data, CharacterService character, TradeService trade,
        RoleStore roles, SessionRegistry sessions, FunctionService functions, SeasonService seasons,
        World world, SceneService scenes, PublicLineManager lines, TimeProvider clock, GameLoop loop,
        Microsoft.Extensions.Options.IOptions<ServerOptions> options)
    {
        this._market = market; this._data = data; this._character = character; this._trade = trade; this._roles = roles;
        this._sessions = sessions; this._functions = functions; this._seasons = seasons; this._world = world;
        this._lines = lines; this._clock = clock; this._loop = loop; _nameMaxLength = options.Value.StallNameMaxLength;
        // C/D 15: the realm-wide stall maintenance (stall collection, owner roles, AOI actors) no longer runs inside the
        // scene's hook scope, nor delays the scene handoff. A scene's first visit starts its system stall population
        // as its own operation; later loads of a visited scene leave it to the one-second maintenance.
        scenes.Loaded.Add(s => { PopulateFirstVisit(s); return Task.CompletedTask; });
        scenes.Leaving.Add(s => { s.ViewedStall = s.ViewedSystemStall = 0; return Task.CompletedTask; });
        scenes.PublicLineOpened.Add(ActivateLine);
        lines.HasPendingWork.Add(HasPendingWork);
        lines.Retiring.Insert(0, RetireLineAsync);
        loop.ScheduleRuntime(TimeSpan.FromSeconds(1), MaintainAsync);
    }

    /// <summary>JSON escrow records contain complete instance clones, including enchantments and string itemInfos.</summary>
    public sealed class StallRow
    {
        public string Key { get; set; } = "";
        public long Owner { get; set; }
        public long SystemId { get; set; }
        public long Scene { get; set; }
        public int LineNumber { get; set; } = 1; // Player stall's opening line; system stock is shared across lines.
        public string Title { get; set; } = "";
        public string OwnerName { get; set; } = "";
        public int Career { get; set; }
        public int Sex { get; set; }
        public int Level { get; set; }
        public int Area { get; set; } = 1;
        public Dictionary<int, int> Appearance { get; set; } = new();
        public float X { get; set; }
        public float Y { get; set; }
        public float Z { get; set; }
        public long OpenTime { get; set; }
        public long Timeout { get; set; }
        public long RestockTime { get; set; }
        public int MaxGrid { get; set; }
        public long NextItemId { get; set; } = 1;
        public bool Ended { get; set; }
        public int EndMailId { get; set; }
        public List<StallItem> Stock { get; set; } = new();
        public List<byte[]> Records { get; set; } = new();
        public Dictionary<int, long> Gains { get; set; } = new();
        public long TaxRate { get; set; }
    }

    public sealed class StallItem
    {
        public long Id { get; set; }
        public InvItem Item { get; set; } = new();
        public int Price { get; set; }
        public int Currency { get; set; }
        public int SystemConfig { get; set; }
        public Dictionary<long, int> Bought { get; set; } = new();
    }

    public async Task LoginAsync(GameSession s)
    {
        s.ViewedStall = s.ViewedSystemStall = 0;
        s.Stalls = _roles.LoadStalls(s.Role!.Id) is { } saved ? DBStallsInfo.Parser.ParseFrom(saved) : NewMetadata();
        await CharacterService.SyncAsync(s, Sync, s.Stalls.Clone());
        // C/D 15: the login no longer runs the realm stall maintenance (it reserved the stall collection and its
        // participants, and published AOI actors under Realm). The one-second maintenance restores and settles the
        // stalls; the login's scene load populates a first-visited scene's system stalls.
    }

    public async Task<bool> HandleAsync(GameSession s, uint id, ReadOnlyMemory<byte> body)
    {
        if (id is < Start or > Share) return false;
        await RefreshWorldAsync(s);
        switch (id)
        {
            case Start: await StartAsync(s, PlayerStartStallsReq.Parser.ParseFrom(body.Span)); break;
            case Put: await PutAsync(s, PlayerStallsPutItemReq.Parser.ParseFrom(body.Span)); break;
            case Remove: await RemoveAsync(s, PlayerStallsRemoveItemReq.Parser.ParseFrom(body.Span)); break;
            case Update: await UpdateAsync(s, PlayerStallsUpdateItemPriceReq.Parser.ParseFrom(body.Span)); break;
            case Buy: await BuyAsync(s, PlayerStallsBuyItemReq.Parser.ParseFrom(body.Span)); break;
            case End: await EndAsync(s); break;
            case Info: await InfoAsync(s); break;
            case View: await ViewAsync(s, PlayerStallsSyncBuyerStallsInfoReq.Parser.ParseFrom(body.Span)); break;
            case Rename: await RenameAsync(s, PlayerUpdateStallsNameReq.Parser.ParseFrom(body.Span)); break;
            case Leave: s.ViewedStall = s.ViewedSystemStall = 0; break; // DAL sends this empty; no bound response.
            case Prices: await PricesAsync(s, PlayerStallsQueryItemPriceReq.Parser.ParseFrom(body.Span)); break;
            case Top: await TopAsync(s, PlayerStallsTopItemReq.Parser.ParseFrom(body.Span)); break;
            case Share: await ShareAsync(s, PlayerStallsShareCheckReq.Parser.ParseFrom(body.Span)); break;
            default: return false; // 33402's types are absent; 33411/13/16 are server-only.
        }
        return true;
    }

    private long Now => _clock.GetUtcNow().ToUnixTimeSeconds();
    private long Param(string key, long fallback = 0) => _data.Table("PublicParamConfig").TryGetValue(key, out var x) ? (long)(GameData.Number(x) ?? fallback) : fallback;
    private long[] ParamList(string key) => _data.Table("PublicParamConfig").TryGetValue(key, out var x) ? Numbers(x) : [];
    private static long[] Numbers(JsonElement x) => x.ValueKind == JsonValueKind.Array ? x.EnumerateArray().Select(v => (long)(GameData.Number(v) ?? 0)).ToArray() : [];
    private static long[] Field(JsonElement row, string name) => row.TryGetProperty(name, out var x) ? Numbers(x) : [];
    private static string PlayerKey(long id) => "p:" + id.ToString(CultureInfo.InvariantCulture);
    private static string SystemKey(long id) => "s:" + id.ToString(CultureInfo.InvariantCulture);
    private static bool Active(StallRow? row) => row is { Ended: false };
    private StallRow? Own(MarketTransaction tx, GameSession s) => tx.Get<StallRow>(Kind, PlayerKey(s.Role!.Id));

    private bool InSeason(JsonElement row)
    {
        var books = Field(row, "_kMultiverseArray");
        return books.Length == 0 || books.Contains(0) || books.Contains(_seasons.PlayBook);
    }

    /// <summary>CheckCanStall in Lua_CFG_TradeConfig accepts _iTrade == Stall (2), not TradeAndStall (3).</summary>
    private JsonElement? SaleConfig(int entry) => _data.Row("TradeConfig", entry) is { } row
        && GameData.Int(row, "_iTrade") == 2 && InSeason(row) ? row : null;

    /// <summary>
    /// DAL 2064–2221: Top uses 10/10; ordinary steps are floor(base*percent/10+0.5).
    /// Choosing the configured stable range without hidden server supply dynamics is (inferred).
    /// </summary>
    private static (int Min, int Max) Range(JsonElement row)
    {
        var range = Field(row, "_kStableRange");
        if (GameData.Int(row, "_iTradeType") == 1 || range.Length < 2) return (10, 10);
        return ((int)Math.Clamp(range[0], 1, 1000), (int)Math.Clamp(range[1], range[0], 1000));
    }

    private bool ValidPrice(JsonElement row, int price, int currency)
    {
        if (price <= 0 || price > Param("TradeLimitPrice", 999999999) || currency != GameData.Int(row, "_iMoney")) return false;
        var (min, max) = Range(row);
        long basis = GameData.Int(row, "_iBasePrice");
        return Enumerable.Range(min, max - min + 1).Any(step => (basis * step + 5) / 10 == price);
    }

    private async Task PricesAsync(GameSession s, PlayerStallsQueryItemPriceReq req)
    {
        var reply = new PlayerStallsQueryItemPriceResp();
        foreach (long id in req.ItemID.Distinct())
        {
            if (id is <= 0 or > int.MaxValue || SaleConfig((int)id) is not { } row) { reply.Result = ErrorCodes.NotForSale; break; }
            var (min, max) = Range(row);
            reply.ItemPrice[id] = new StallItemPrice { MinPercent = min, MaxPercent = max };
        }
        if (req.ItemID.Count == 0) reply.Result = ErrorCodes.Parameter;
        await s.SendGameAsync(Prices, reply);
    }

    private StallsBaseInfo Base(StallRow row, int line = 0) => new()
    {
        StallsID = row.Owner, SystemStallID = row.SystemId, StallsTitle = row.Title,
        SceneID = 1, SceneEntryID = row.Scene, SceneLineNum = line > 0 ? line : row.LineNumber, OpenTs = row.OpenTime,
    };

    private StallsItemInfo ItemView(StallItem item) => new()
    {
        StallsItemID = item.Id, Itembase = _character.ItemInfoOf(item.Item), Price = item.Price,
        Pricetype = item.Currency, SystemStallCfgID = item.SystemConfig,
    };

    private StallsInfo SellerView(StallRow row)
    {
        var info = new StallsInfo
        {
            Baseinfo = Base(row), Timeout = row.Timeout, Pos = new Vec3 { X = row.X, Y = row.Y, Z = row.Z },
            Maxgrid = row.MaxGrid, BEnd = row.Ended, EndMailid = row.EndMailId,
            CurTax = new LongLongPair { Key = row.TaxRate },
        };
        info.Itembase.Add(row.Stock.Select(ItemView));
        info.Allgainmoney.Add(row.Gains);
        info.Record.Add(row.Records.Select(StallsRecord.Parser.ParseFrom));
        return info;
    }

    private StallsSyncToBuyerInfo BuyerView(StallRow row, int line = 0)
    {
        var info = new StallsSyncToBuyerInfo
        {
            Baseinfo = Base(row, line), Timeout = row.Timeout, Pos = new Vec3 { X = row.X, Y = row.Y, Z = row.Z },
            SceneEntryID = row.Scene, SceneLineNum = line > 0 ? line : row.LineNumber, BEnd = row.Ended,
            Avatarinfo = new PlayerAvatarInfo { Sendid = row.Owner, Sendername = row.OwnerName, Area = row.Area, Level = row.Level, Job = row.Career },
        };
        info.Itembase.Add(row.Stock.Select(ItemView));
        return info;
    }

    private long WeekStart()
    {
        // Monday 00:00 UTC is the (inferred) weekly reset boundary; no original timezone was recovered.
        var day = _clock.GetUtcNow().Date;
        return new DateTimeOffset(day.AddDays(-(((int)day.DayOfWeek + 6) % 7)), TimeSpan.Zero).ToUnixTimeSeconds();
    }

    private long TaxRate(long gross) => _data.Table("TradeConfigTax").Values.OrderBy(r => GameData.Int(r, "_iAmountBooth"))
        .Where(r => gross >= GameData.Int(r, "_iAmountBooth")).Select(r => GameData.Int(r, "_iTaxBooth")).LastOrDefault();

    private DBStallsInfo NewMetadata() => new()
    {
        WeekMoney = new LongLongPair { Key = WeekStart() }, CurTax = new LongLongPair { Key = TaxRate(0) },
    };

    private DBStallsInfo Metadata(MarketTransaction tx, long owner)
    {
        var info = tx.LoadBlob(owner, "stalls_info") is { } blob ? DBStallsInfo.Parser.ParseFrom(blob) : NewMetadata();
        if (info.WeekMoney == null || info.WeekMoney.Key != WeekStart()) info.WeekMoney = new LongLongPair { Key = WeekStart() };
        info.CurTax = new LongLongPair { Key = TaxRate(info.WeekMoney.Value) };
        return info;
    }

    private void Save(MarketTransaction tx, StallRow row, DBStallsInfo? meta = null)
    {
        tx.Put(Kind, row.Key, row);
        if (row.Owner == 0) return;
        meta ??= Metadata(tx, row.Owner);
        meta.StallsSceneID = row.Ended ? 0 : 1;
        meta.StallEndTs = row.Ended ? 0 : row.Timeout;
        meta.CurTax = new LongLongPair { Key = row.TaxRate };
        tx.SaveBlob(row.Owner, "stalls_info", meta.ToByteArray());
    }

    public void ActivateLine(RuntimeMapKey runtime)
    {
        if (runtime.Kind == RuntimeMapKind.PublicLine) _activeLines.Add(runtime);
    }

    public bool HasPendingWork(RuntimeMapKey runtime) => _pendingProjections.GetValueOrDefault(runtime) > 0
        || _openingClaims.GetValueOrDefault(runtime) > 0
        || _pinnedPlayerStalls.TryGetValue(runtime, out var pinned) && pinned.Count > 0;

    private void OpeningStart(RuntimeMapKey runtime) => _openingClaims[runtime] = _openingClaims.GetValueOrDefault(runtime) + 1;

    private void OpeningEnd(RuntimeMapKey runtime)
    {
        if (_openingClaims.GetValueOrDefault(runtime) <= 1) _openingClaims.Remove(runtime);
        else _openingClaims[runtime]--;
    }

    private void Pin(StallRow row)
    {
        if (row.Owner == 0) return;
        var runtime = RuntimeMapKey.Public(row.Scene, row.LineNumber);
        if (row.Ended)
        {
            if (_pinnedPlayerStalls.TryGetValue(runtime, out var pinned) && pinned.Remove(row.Key) && pinned.Count == 0)
                _pinnedPlayerStalls.Remove(runtime);
        }
        else
        {
            if (!_pinnedPlayerStalls.TryGetValue(runtime, out var pinned)) _pinnedPlayerStalls[runtime] = pinned = new();
            pinned.Add(row.Key);
        }
    }

    /// <summary>Remove only this line's AOI projections; persisted player escrow remains on its opening line.</summary>
    public async Task RetireLineAsync(RuntimeMapKey runtime)
    {
        if (HasPendingWork(runtime)) throw new InvalidOperationException($"Line {runtime} still owns a player stall or projection.");
        _activeLines.Remove(runtime);
        foreach (var actor in _actors.Where(x => x.Key.Line == runtime.LineNumber && x.Value.Row.Scene == runtime.SceneEntry)
                     .Select(x => x.Value).ToArray())
        {
            _actors.Remove((actor.Row.Key, runtime.LineNumber));
            await _world.RemoveUnitAsync(actor);
        }
    }

    private void ProjectionStart(RuntimeMapKey runtime) => _pendingProjections[runtime] = _pendingProjections.GetValueOrDefault(runtime) + 1;
    private void ProjectionEnd(RuntimeMapKey runtime)
    {
        if (_pendingProjections.GetValueOrDefault(runtime) <= 1) _pendingProjections.Remove(runtime);
        else _pendingProjections[runtime]--;
    }

    /// <summary>Post-commit delivery; AOI actor and both UI views always reflect the same durable escrow.</summary>
    private async Task PublishAsync(StallRow row, bool notify = true, Func<StallRow, Task>? beforeProjection = null)
    {
        // The world projection retains its conservative bridge until the world family is converted.
        // Reload after admission: an older response may have waited behind a newer committed stall.
        Ownership.OwnerNotices? notices = null;
        using (var scope = _loop.Owners is { } owners
            ? await owners.EnterAsync("stall projection", Ownership.ReservationKey.Realm) : null)
        {
            using var capture = scope == null ? null : notices = new(scope.Context);
            row = _market.ReadCurrent<StallRow>(Kind, row.Key)
                ?? throw new InvalidOperationException("Committed stall is missing before actor publication.");
            Pin(row);
            if (beforeProjection != null) await beforeProjection(row);
            await PublishCoreAsync(row, notify);
        }
        if (notices != null) await notices.FlushAsync();
    }

    private async Task PublishCoreAsync(StallRow row, bool notify)
    {
        var lines = row.SystemId == 0 ? new[] { RuntimeMapKey.Public(row.Scene, row.LineNumber) }
            : _activeLines.Where(x => x.SceneEntry == row.Scene).ToArray();
        foreach (var runtime in lines)
        {
            ProjectionStart(runtime);
            try
            {
                var key = (row.Key, runtime.LineNumber);
                if (row.Ended || !_activeLines.Contains(runtime))
                {
                    if (_actors.Remove(key, out var old)) await _world.RemoveUnitAsync(old);
                }
                else if (!_actors.TryGetValue(key, out var actor))
                {
                    actor = new StallActor(row, runtime, Param("StallsModelId", 25000002),
                        () => Interlocked.Increment(ref _nextProjectionId));
                    _actors.Add(key, actor);
                    await _world.AddUnitAsync(actor);
                    if (!_activeLines.Contains(runtime) && _actors.Remove(key)) await _world.RemoveUnitAsync(actor);
                }
                else
                {
                    actor.Row = row;
                    if (notify) await _world.ToUnitViewersAsync(actor, CharacterService.EntityBaseChange, actor.Entity());
                }
            }
            finally { ProjectionEnd(runtime); }
        }
        if (!notify) return;
        foreach (var s in _sessions.All().Where(s => s.Role != null))
        {
            try
            {
                if (s.Role!.Id == row.Owner)
                {
                    // Market Apply already published every seller's committed metadata before any notice.
                    await CharacterService.SyncAsync(s, Sync, (s.Stalls ?? NewMetadata()).Clone());
                    var info = SellerView(row);
                    if (row.Ended)
                    {
                        await s.SendGameAsync(Settlement, new EndStallsInfoNtf { Info = info.Clone() });
                        info.Baseinfo.StallsID = 0;
                    }
                    await s.SendGameAsync(Change, new PlayerStallsChangeNtf { LastAliveTime = Now, Info = info });
                }
                if ((row.Owner != 0 && s.ViewedStall == row.Owner) || (row.SystemId != 0 && s.ViewedSystemStall == row.SystemId))
                {
                    await s.SendGameAsync(BuyerChange, new PlayerStallsSyncBuyerStallsInfoNtf
                    { Info = BuyerView(row, row.SystemId > 0 && s.SceneEntry == row.Scene ? s.LineNumber : 0) });
                    if (row.Ended) s.ViewedStall = s.ViewedSystemStall = 0;
                }
            }
            catch (Exception e) when (e is IOException or System.Net.Sockets.SocketException or ObjectDisposedException) { }
        }
    }

    private sealed class StallActor : WorldUnit
    {
        /// <summary>Below 2^53: the client keeps unit ids in a double array (see DungeonService.SceneActorUidBase).</summary>
        internal const long ProjectionIdBase = 1L << 49;
        public StallRow Row { get; set; }
        private readonly long _template;
        public StallActor(StallRow row, RuntimeMapKey runtime, long template, Func<long> nextProjectionId)
        {
            Row = row; this._template = template;
            // Stable separate AOI IDs and StallsID=owner are (inferred); DAL compares StallsID with its role UUID.
            Uid = row.SystemId != 0 && runtime.LineNumber > 1 ? nextProjectionId()
                : row.SystemId != 0 ? 7_100_000_000_000L + row.SystemId : 7_000_000_000_000L + row.Owner;
            SceneEntry = row.Scene; RuntimeMap = runtime; X = row.X; Y = row.Y; Z = row.Z;
        }
        public override EntityBase Entity()
        {
            var stall = new NpcStallsData { StallsID = Row.Owner, SystemStallID = Row.SystemId, Carrer = Row.Career, Sex = Row.Sex, Level = Row.Level };
            stall.CurUseAppearances.Add(Row.Appearance);
            return new EntityBase
            {
                Uuid = (ulong)Uid, EntityTemplateID = (ulong)_template, EntityType = 9, Name = Row.Title,
                State = EntityState.Alive, StallsInfo = stall, NotifyType = EntityBase.Types.NotifyType.All,
                MovementInfo = new MovementInfo { Pos = Pos, MoveType = MovementType.MoveTypeNone },
            };
        }
    }
}

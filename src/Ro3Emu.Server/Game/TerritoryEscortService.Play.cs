using System.Collections.Concurrent;
using System.Text.Json;
using Google.Protobuf;
using Romsg;
using Ro3Emu.Server.Accounts;
using Ro3Emu.Server.Accounts.Persistence;
using Ro3Emu.Server.Data;
using Ro3Emu.Server.Economy;
using Ro3Emu.Server.Maps;

namespace Ro3Emu.Server.Game;

public sealed partial class TerritoryEscortService
{
    private static readonly int[] StartMaps = [30303, 30302, 30307];
    private static readonly int[] StartPoints = [3001, 2001, 7001];
    private static readonly int[] ScoreThresholds = [3000, 7000, 10000, 15000, 20000];
    private readonly ConcurrentDictionary<(long Team, int Type), EscortMarkPos> _marks = new();
    private readonly ConcurrentDictionary<long, TransferInvitation> _transferInvites = new();
    private readonly ConcurrentDictionary<(long Scene, long Guild), int> _lastSceneCounts = new();
    private long _nextInvite = 1;
    private int _lastStageType = int.MinValue;
    private sealed record TransferInvitation(long Inviter, long Recipient, long Scene, Vec3 Position, long Until);

    /// <summary>83085 and 83078 share the three fields read by LC_DAL_TerritoryWars._processTerritoryWarsEscortStageInfo.</summary>
    private TerritoryWarsEscortStageRes StageData() => new()
    {
        // (inferred) Client phase names map to the config's escort-pre and escort-play stage types.
        Stage = (TerritoryWarsEscortStage)(Trading ? 2 : Playing ? 4 : 0),
        StartTime = Trading || Playing ? _clock.Current.StartTime : 0,
        EndTime = Trading || Playing ? _clock.Current.EndTime : 0,
    };

    private Task SendStageAsync(GameSession s) => s.SendGameAsync(Stage, StageData());

    public async Task ReconcileAsync()
    {
        if (_loop.IsWorkerActive && !_loop.IsOnLoop)
        {
            await _loop.InvokeAsync(ReconcileAsync);
            return;
        }
        bool stageChanged = _lastStageType != _clock.Current.Type || Volatile.Read(ref _committedEpoch) != Epoch;
        using var reservation = await EnterGateAsync("escort/reconciliation",
            includeOnlineRoles: ledger.SeasonStart != _clock.SeasonStart || ledger.PlayBook != _clock.PlayBook || ledger.Cycle != _clock.Cycle,
            includeAllGuilds: true);
        using var stagedEscortLedger = StageLedger();
        try
        {
            bool changed = false;
            bool resetTransient = false;
            var alignedRoles = new List<(GameSession Peer, long RoleId, object? Attachment, RoleState State)>();
            if (ledger.SeasonStart != _clock.SeasonStart || ledger.PlayBook != _clock.PlayBook || ledger.Cycle != _clock.Cycle)
            {
                // (inferred) A season/playbook epoch owns its own escrow and round numbers. Return
                // every unfinished reservation before archiving the old epoch; IDs remain monotonic.
                foreach (var listing in ledger.Listings.Values.Where(x => x.State == 0))
                {
                    ledger.Goods[listing.Seller] = checked(ledger.Goods.GetValueOrDefault(listing.Seller) + listing.Count);
                    listing.State = 2; listing.Applications.Clear();
                }
                foreach (var cart in ledger.Carts.Values)
                    ledger.Goods[cart.Guild] = checked(ledger.Goods.GetValueOrDefault(cart.Guild) + cart.Count);
                if (ledger.SeasonStart != 0 || ledger.PlayBook != 0)
                    ledger.Archives[TerritoryWarsClock.EpochOf(ledger.SeasonStart, ledger.PlayBook, ledger.Cycle)] = new SeasonArchive
                    {
                        Goods = ledger.Goods, Listings = ledger.Listings, Play = ledger.Play,
                        ScoresByRound = ledger.ScoresByRound, StockRounds = ledger.StockRounds,
                        SkillCooldowns = ledger.SkillCooldowns
                    };
                if (ledger.Archives.Remove(Epoch, out var restored))
                {
                    ledger.Goods = restored.Goods; ledger.Listings = restored.Listings;
                    ledger.Play = restored.Play; ledger.ScoresByRound = restored.ScoresByRound;
                    ledger.StockRounds = restored.StockRounds; ledger.SkillCooldowns = restored.SkillCooldowns;
                }
                else
                {
                    ledger.Goods = new(); ledger.Listings = new(); ledger.Play = new();
                    ledger.ScoresByRound = new(); ledger.StockRounds = new(); ledger.SkillCooldowns = new();
                }
                ledger.Carts = new(); ledger.Cargo = new();
                ledger.SeasonStart = _clock.SeasonStart; ledger.PlayBook = _clock.PlayBook; ledger.Cycle = _clock.Cycle;
                resetTransient = true;
                foreach (var peer in _sessions.All().Where(s => s.Role != null && s.TerritoryEscort != null))
                {
                    var aligned = JsonSerializer.Deserialize<RoleState>(JsonSerializer.SerializeToUtf8Bytes(peer.TerritoryEscort))!;
                    if (AlignRole(aligned)) alignedRoles.Add((peer, peer.Role!.Id, peer.Attachment, aligned));
                }
                changed = true;
            }
            if (Trading)
            {
                // (inferred) No 1258000 award source is configured. A guild formed before this trading
                // window with the configured 20-member qualification receives one normal-cart load per round.
                // StockRounds is committed with Goods so ticks, relogins and restarts cannot mint it twice.
                int required = (int)(GameData.Number(_data.Table("PublicParamConfig")["TerritoryAuctionMinGuildMemberCount"]) ?? 20);
                int load = (int)GameData.Int(_data.Row("EscortCartConfig", 4910001)!.Value, "_iMaxTransportNum");
                foreach (var guild in _guilds.List().Where(g => g.CreateTime <= _clock.Current.StartTime * 1000
                    && g.Members.Count >= required))
                {
                    if (!ledger.StockRounds.TryGetValue(guild.Id, out var rounds))
                        ledger.StockRounds[guild.Id] = rounds = new HashSet<int>();
                    if (!rounds.Add(Round)) continue;
                    ledger.Goods[guild.Id] = checked(ledger.Goods.GetValueOrDefault(guild.Id) + load);
                    var play = GetPlay(guild.Id);
                    play.ToTransport = ledger.Goods[guild.Id];
                    changed = true;
                }
            }
            if (!Trading)
            {
                // (inferred) An unaccepted listing must return escrow when trading closes; the transition is idempotent.
                foreach (var x in ledger.Listings.Values.Where(x => x.State == 0))
                {
                    ledger.Goods[x.Seller] = checked(ledger.Goods.GetValueOrDefault(x.Seller) + x.Count);
                    x.State = 2; x.Applications.Clear(); changed = true;
                }
            }
            if (!Playing)
            {
                foreach (var x in ledger.Carts.Values.ToArray())
                {
                    ledger.Goods[x.Guild] = checked(ledger.Goods.GetValueOrDefault(x.Guild) + x.Count);
                    ledger.Carts.Remove(x.Id); changed = true;
                }
            }
            foreach (var cargo in ledger.Cargo.Values.Where(x => !x.Picked && (!Playing || x.Expires <= _clock.Now)))
            { cargo.Picked = true; changed = true; }
            if (changed)
            {
                var transaction = _accounts.NewTransaction();
                foreach (var peer in alignedRoles) SaveRole(peer.RoleId, peer.State, transaction);
                await CommitAsync(transaction, "reconcile", EscortReconcileSource(_committedLedger, ledger));
                foreach (var peer in alignedRoles)
                    if (peer.Peer.Role?.Id == peer.RoleId && ReferenceEquals(peer.Peer.Attachment, peer.Attachment))
                        peer.Peer.TerritoryEscort = peer.State;
                if (resetTransient) { _marks.Clear(); _transferInvites.Clear(); _lastSceneCounts.Clear(); }
            }
        }
        finally { stagedEscortLedger.Dispose(); _gate.Release(); reservation?.Dispose(); }
        _lastStageType = _clock.Current.Type;
        await RefreshCartActorsAsync();
        await RefreshCargoActorsAsync();
        if (stageChanged)
            await RoleFanout.EachAsync(_sessions.All().Where(x => x.Role != null),
                peer => peer.SendGameAsync(InfoNtf, StageData())); // (inferred) 83078 uses the 83085 three-field wire shape.
        await PublishSceneCountsAsync();
    }

    public void Tick(GameTime time)
    {
        if (_ticking || _reconciledOnce && time.Now - _lastTick < TimeSpan.FromSeconds(1)) return;
        _reconciledOnce = true;
        _lastTick = time.Now;
        _ticking = true;
        _loop.PostRuntime(async () => { try { await ReconcileAsync(); } finally { _ticking = false; } });
    }

    /// <summary>83094: changed guild population in a territory scene, from active authenticated sessions.</summary>
    public async Task PublishSceneCountsAsync()
    {
        var present = _sessions.All().Where(s => s.Role != null && !s.SceneLoading && EscortLine(s)
            && s.SceneEntry is >= 30301 and <= 30336)
            .Select(s => (Session: s, Guild: _guilds.ForRole(s.Role!.Id)?.Id ?? 0)).Where(x => x.Guild > 0).ToArray();
        var next = present.GroupBy(x => (Scene: x.Session.SceneEntry, x.Guild)).ToDictionary(g => g.Key, g => g.Count());
        foreach (var key in _lastSceneCounts.Keys.Union(next.Keys).ToArray())
        {
            int count = next.GetValueOrDefault(key);
            if (_lastSceneCounts.GetValueOrDefault(key) == count) continue;
            if (count == 0) _lastSceneCounts.TryRemove(key, out _); else _lastSceneCounts[key] = count;
            await RoleFanout.EachAsync(_sessions.All().Where(s => s.Role != null && s.SceneEntry == key.Scene && EscortLine(s)),
                peer => peer.SendGameAsync(SceneCountNtf,
                    new TerritoryWarsGuildScenePlayerCountNtf { GuildId = key.Guild, PlayerCount = count }));
        }
    }

    private async Task SendCartsAsync(GameSession s)
    {
        var ntf = new TerritoryWarsEscortAllNtf();
        foreach (var c in ledger.Carts.Values.Where(c => EscortLine(s) &&
            (_sessions.All().FirstOrDefault(owner => owner.Role?.Id == c.Owner)?.SceneEntry ?? c.Scene) == s.SceneEntry))
            ntf.CartInfo.Add(CartWire(c));
        await s.SendGameAsync(AllCartsNtf, ntf);
    }

    private TerritoryWarEscortCartInfo CartWire(CartState c)
    {
        var owner = _sessions.All().FirstOrDefault(x => x.Role?.Id == c.Owner);
        return new TerritoryWarEscortCartInfo
        {
            CartId = c.Id, OwnerPlayerId = c.Owner,
            FollowPlayerId = c.Owner, MapId = owner?.SceneEntry ?? c.Scene, PointConfigId = c.Point,
            Pos = owner?.Position?.Clone() ?? new Vec3()
        };
    }

    private PlayState GetPlay(long guildId)
    {
        if (ledger.Play.TryGetValue(guildId, out var p) && p.Round == Round) return p;
        p = new PlayState
        {
            Round = Round, Start = _clock.Stages.FirstOrDefault(x => x.Round == Round && x.Type == 2)?.StartTime ?? 0,
            ToTransport = ledger.Goods.GetValueOrDefault(guildId)
        };
        ledger.Play[guildId] = p;
        return p;
    }

    private TerritoryWarsEscortGuildPlayInfo PlayWire(GuildStore.State g)
    {
        var p = ledger.Play.TryGetValue(g.Id, out var current) && current.Round == Round
            ? current : new PlayState { Round = Round };
        return new TerritoryWarsEscortGuildPlayInfo
        {
            GuildId = g.Id, GuildName = g.Name,
            HeadIcon = new GuildHeadIcon { BackgroundType = g.Background, IconType = g.Icon },
            IsBuyer = ledger.Listings.Values.Any(x => x.Buyer == g.Id),
            BuyNum = p.BuyNum, SellNum = p.SellNum, StartPlayTime = p.Start,
            TransportNum = p.Transported, ToTransportNum = ledger.Goods.GetValueOrDefault(g.Id),
            StartCartNum = p.StartedCarts, TransportCartNum = p.FinishedCarts,
            PointId = p.Point != 0 ? p.Point : GuildPoint?.Invoke(g.Id) ?? 0
        };
    }

    private async Task GuildPlayAsync(GameSession s, TerritoryWarsEscortGuildPlayInfoReq req)
    {
        var g = Own(s);
        var resp = new TerritoryWarsEscortGuildPlayInfoRes();
        if (g != null) resp.PlayInfo = PlayWire(g);
        await s.SendGameAsync(GuildPlay, resp);
    }

    private async Task PlayersPlayAsync(GameSession s, TerritoryWarsEscortGuildPlayersPlayInfoReq req)
    {
        var g = Own(s);
        var resp = new TerritoryWarsEscortGuildPlayersPlayInfoRes();
        if (g != null)
            foreach (var m in g.Members.Values)
            {
                var rs = m.RoleId == s.Role!.Id ? s.TerritoryEscort : LoadRole(m.RoleId);
                resp.PlayerInfos.Add(new TerritoryWarsEscortPlayerPlayInfo
                {
                    RoleId = m.RoleId,
                    PlayerName = m.Name, Score = rs?.ScoreByRound.GetValueOrDefault(Round) ?? 0,
                    AwardStates = ClaimBits(rs, Round)
                });
            }
        await s.SendGameAsync(PlayersPlay, resp);
    }

    private RoleState? LoadRole(long id) => _roles.LoadTerritoryEscort(id) is { } b
        ? JsonSerializer.Deserialize<RoleState>(b) : null;
    private static long ClaimBits(RoleState? s, int round)
    {
        long bits = 0;
        if (s?.ClaimedByRound.TryGetValue(round, out var indices) == true)
            foreach (var i in indices.Where(i => i is >= 0 and <= 4)) bits |= 1L << i;
        return bits;
    }

    private async Task TimesAsync(GameSession s)
    {
        var resp = new TerritoryWarsEscortGuildPlayTimelistRes();
        long id = Own(s)?.Id ?? 0;
        if (ledger.Play.TryGetValue(id, out var p) && p.Start > 0) resp.TimeList.Add(p.Start);
        await s.SendGameAsync(Times, resp);
    }

    private async Task PlayTimeAsync(GameSession s, TerritoryWarsEscortPlayTimeReq req)
    {
        var g = Own(s);
        int result = g == null ? ErrorCodes.NotInGuild : Right(g, RoleId(s), 17) ? 0 : ErrorCodes.GuildPermission;
        var playStage = _clock.Stages.FirstOrDefault(x => x.Round == Round && x.Type == 2);
        if (result == 0 && (!Trading || playStage == null || req.StartDaySeconds is < 0 or >= 86400)) result = ErrorCodes.Parameter;
        if (result == 0)
        {
            using var reservation = await EnterGateAsync("escort/play", s);
            using var stagedEscortLedger = StageLedger();
            try
            {
                var p = GetPlay(g!.Id);
                // (inferred) Start-day seconds schedule the configured 30-minute convoy inside the next play stage.
                long day = playStage!.StartTime - playStage.StartTime % 86400;
                long selected = day + req.StartDaySeconds;
                if (selected < playStage.StartTime || selected + 1800 > playStage.EndTime || p.StartedCarts > 0)
                    result = ErrorCodes.Parameter;
                else { p.Start = selected; await CommitAsync(_accounts.NewTransaction(), "play-time", actor: s); }
            }
            finally { stagedEscortLedger.Dispose(); _gate.Release(); reservation?.Dispose(); }
        }
        await s.SendGameAsync(PlayTime, new TerritoryWarsEscortPlayTimeRes { StartDaySeconds = req.StartDaySeconds, Result = result });
    }

    private async Task GuildsAsync(GameSession s, TerritoryWarsEscortGuildsReq req)
    {
        var resp = new TerritoryWarsEscortGuildsRes { StateId = req.StateId };
        var zone = new TerritoryWarsEscortZoneGuildPlayTimeInfo { StateId = req.StateId };
        foreach (var g in _guilds.List().Where(g => ledger.Play.TryGetValue(g.Id, out var p) && p.Round == Round))
        {
            var p = ledger.Play[g.Id];
            zone.Guilds.Add(new TerritoryWarsEscortGuildPlayTimeInfo
            {
                GuildId = g.Id, GuildName = g.Name,
                StartPlayTime = p.Start, PointId = p.Point, TotalGoods = p.ToTransport + p.Transported,
                ToTransGoods = ledger.Goods.GetValueOrDefault(g.Id)
            });
            zone.GuildPlayInfos.Add(PlayWire(g));
        }
        resp.ZoneGuildPlayTimeInfos.Add(zone);
        await s.SendGameAsync(Guilds, resp);
    }

    private async Task RankAsync(GameSession s)
    {
        var resp = new TerritoryWarsEscortRankRsp();
        foreach (var p in ledger.Play.Where(x => x.Value.Round == Round).OrderByDescending(x => x.Value.Transported).Take(100))
            resp.SubmitRank.Add(new TerritoryWarsEscortRankInfo { GuildId = p.Key, Score = p.Value.Transported });
        foreach (var p in ledger.Play.Where(x => x.Value.Round == Round).OrderByDescending(x => x.Value.Score - x.Value.Transported).Take(100))
            resp.RobRank.Add(new TerritoryWarsEscortRankInfo { GuildId = p.Key, Score = Math.Max(0, p.Value.Score - p.Value.Transported) });
        await s.SendGameAsync(Rank, resp);
    }

    private async Task SendScoreAsync(GameSession s)
    {
        var resp = new TerritoryWarsEscortGuildPlayerScoreRes { Score = s.TerritoryEscort?.ScoreByRound.GetValueOrDefault(Round) ?? 0 };
        for (int i = 0; i < 5; i++)
            resp.AwardStates.Add(s.TerritoryEscort?.ClaimedByRound.GetValueOrDefault(Round)?.Contains(i) == true ? 2
                : resp.Score >= ScoreThresholds[i] ? 1 : 0);
        await s.SendGameAsync(Score, resp);
    }

    private async Task CartAsync(GameSession s, TerritoryWarsEscortCartReq req)
    {
        var g = Own(s);
        int result = g == null ? ErrorCodes.NotInGuild : !Playing || s.Dead || !EscortLine(s) ? ErrorCodes.FeatureUnavailable : 0;
        int start = Array.IndexOf(StartPoints, req.PointId);
        if (result == 0 && (req.CartType is not (0 or 1) || start < 0 || s.SceneEntry != StartMaps[start]
            || s.SceneLoading || !NearPoint(s.Position, s.SceneEntry, req.PointId, 6)))
            result = ErrorCodes.Parameter;
        if (result == 0 && req.CartType == 1 && !Right(g, RoleId(s), 18)) result = ErrorCodes.GuildPermission;
        int cap = req.CartType == 0 ? 12000 : 45000;
        long id = 0; int count = 0; CartState? startedCart = null;
        using var reservation = await EnterGateAsync("escort/play", s);
        using var stagedEscortLedger = StageLedger();
        try
        {
            if (result == 0 && (s.Dead || s.SceneLoading || !EscortLine(s))) result = ErrorCodes.FeatureUnavailable;
            var stock = ledger.Goods.GetValueOrDefault(g?.Id ?? 0);
            if (result == 0 && stock <= 0) result = ErrorCodes.ItemsShort;
            if (result == 0 && ledger.Carts.Values.Any(c => c.Owner == RoleId(s))) result = ErrorCodes.Cooldown;
            if (result == 0 && (_clock.Now < GetPlay(g!.Id).Start || _clock.Now >= GetPlay(g.Id).Start + 1800))
                result = ErrorCodes.FeatureUnavailable;
            if (result == 0)
            {
                count = (int)Math.Min(cap, stock); id = ledger.NextCartId++;
                var transaction = _accounts.NewTransaction();
                ledger.Goods[g!.Id] = stock - count;
                ledger.Carts[id] = startedCart = new CartState
                {
                    Id = id, Guild = g.Id, Owner = RoleId(s),
                    Round = Round, Type = req.CartType, Point = req.PointId, Count = count, OriginCount = count,
                    Started = _clock.Now, Scene = s.SceneEntry
                };
                var p = GetPlay(g.Id); p.StartedCarts++; p.ToTransport = Math.Max(0, p.ToTransport - count); p.Point = req.PointId;
                await CommitAsync(transaction, "cart-start", $"escort-cart:{Epoch}:{id}:start", s);
            }
        }
        finally { stagedEscortLedger.Dispose(); _gate.Release(); reservation?.Dispose(); }
        await s.SendGameAsync(Cart, new TerritoryWarsEscortCartRes
        {
            CartType = req.CartType, CartId = id,
            TransportNum = count, Result = result
        });
        if (result == 0)
        {
            foreach (var handler in CartStarted) handler(s);
            await RefreshCartActorsAsync();
            var c = startedCart!;
            var play = ledger.Play[g!.Id];
            await RoleFanout.EachAsync(_sessions.All().Where(x => x.WorldScene == s.WorldScene && !x.SceneLoading), async peer =>
            {
                await peer.SendGameAsync(CartNtf, new TerritoryWarsEscortCartNtf { CartInfo = CartWire(c), Reason = (TerritoryWarEscortCartChangeReason)0 });
                await peer.SendGameAsync(CartVarNtf, new TerritoryWarsEscortCartInfoChange
                {
                    Uuid = id,
                    CartVarinfo = new EscortCartVarInfo { CurNum = count, FollowPlayer = RoleId(s) }
                });
            });
            if (play.StartedCarts == 1)
                await RoleFanout.EachAsync(_sessions.All().Where(x => x.Role != null && _guilds.ForRole(x.Role.Id)?.Id == g.Id),
                    peer => peer.SendGameAsync(GuildStartNtf, new TerritoryWarsEscortGuildStartPlayNtf
                    {
                        PlayTimeInfo = new TerritoryWarsEscortGuildPlayTimeInfo
                        {
                            GuildId = g.Id, GuildName = g.Name,
                            StartPlayTime = play.Start, PointId = req.PointId,
                            TotalGoods = play.ToTransport + count, ToTransGoods = ledger.Goods.GetValueOrDefault(g.Id)
                        },
                        SceneEntryId = (int)s.SceneEntry,
                    }));
        }
    }

    private async Task SubmitAsync(GameSession s, TerritoryWarsEscortSubmitReq req)
    {
        // The configured terminal NPCs are 12001/12002 on map 30312. Scene and owner are server state;
        // the request's point/cart IDs only select them. (inferred) A following cart travels with its owner.
        CartState? cart = null;
        bool accepted = false;
        using var reservation = await EnterGateAsync("escort/play", s);
        using var stagedEscortLedger = StageLedger();
        try
        {
            if (Playing && !s.Dead && s.SceneEntry == 30312 && EscortLine(s) && !s.SceneLoading && req.PointId is 12001 or 12002
                && NearPoint(s.Position, s.SceneEntry, req.PointId, 6)
                && ledger.Carts.TryGetValue(req.CartId, out var c) && c.Owner == RoleId(s) && c.Round == Round
                && Own(s)?.Id == c.Guild && _clock.Now > c.Started)
            {
                cart = c;
                var transaction = _accounts.NewTransaction();
                ledger.Carts.Remove(c.Id);
                var p = GetPlay(c.Guild); p.Transported += c.Count; p.FinishedCarts++; AddGuildScore(c.Guild, c.Count);
                long roleId = s.Role!.Id;
                var attachment = s.Attachment;
                var stagedRole = CopyRole(s.TerritoryEscort!);
                stagedRole.ScoreByRound[Round] = checked(stagedRole.ScoreByRound.GetValueOrDefault(Round) + (long)(c.Count * 0.3));
                SaveRole(roleId, stagedRole, transaction);
                await CommitAsync(transaction, "cart-submit", $"escort-cart:{Epoch}:{c.Id}:submit", s);
                if (s.Role?.Id == roleId && ReferenceEquals(s.Attachment, attachment))
                    s.TerritoryEscort = stagedRole;
                accepted = true;
            }
        }
        finally { stagedEscortLedger.Dispose(); _gate.Release(); reservation?.Dispose(); }
        if (!accepted) return; // NullArg response; no success packet on a refusal.
        await RefreshCartActorsAsync();
        await s.SendGameAsync(SuccessNtf, new TerritoryWarsEscortSuccessNtf
        {
            CartType = cart!.Type,
            CartId = cart.Id, GuildId = cart.Guild, TeamId = _teams.TeamOf(RoleId(s)).TeamId,
            OriginTransportNum = cart.OriginCount == 0 ? cart.Count : cart.OriginCount,
            FinalTransportNum = cart.Count, PlayerId = RoleId(s)
        });
        await RoleFanout.EachAsync(_sessions.All().Where(x => x.WorldScene == s.WorldScene && !x.SceneLoading),
            peer => peer.SendGameAsync(CartNtf, new TerritoryWarsEscortCartNtf { CartInfo = CartWire(cart), Reason = (TerritoryWarEscortCartChangeReason)2 }));
        await SendScoreAsync(s);
    }

    private async Task DropSubmitAsync(GameSession s)
    {
        var guild = Own(s);
        if (!Playing || s.Dead || s.SceneEntry != 30312 || !EscortLine(s) || s.SceneLoading || guild == null
            || !(NearPoint(s.Position, 30312, 12001, 6) || NearPoint(s.Position, 30312, 12002, 6))) return;
        // (inferred) Use the two configured escort terminal points as the guarded interaction area;
        // EscortResSubmissionNpcConfigID identifies submission template 200029.
        // The configured pack converts to 1000 guild resource 126 in the item exchange transaction.
        long guildId = guild.Id, roleId = RoleId(s);
        using var reservation = await EnterGateAsync("escort/drop submit", s);
        List<InvItem>? granted;
        try
        {
            using var held = await CharacterService.Lock(s);
            if (s.Dead || s.SceneLoading || !Playing || !EscortLine(s) || Own(s)?.Id != guildId || s.SceneEntry != 30312
                || !(NearPoint(s.Position, 30312, 12001, 6) || NearPoint(s.Position, 30312, 12002, 6))) return;
            long count = _character.CountOf(s, 1258002);
            if (count <= 0 || count > long.MaxValue / 1000) return;
            long materialCount = checked(count * 1000);
            var item = new EconomyAsset(EconomyAssetKind.Item, 1258002);
            var materials = new EconomyAsset(EconomyAssetKind.GuildResource, 3);
            string operationId = Guid.NewGuid().ToString("N");
            var mutation = new EventEconomyMutation(EventEconomy.Catalog(_data), operationId,
                s.Account!, roleId, "event/escort-drop-submit",
                $"escort-drop-submit:{Epoch}:{roleId}:{operationId}",
                DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), [new(item, count)], [new(materials, materialCount)],
                [new(new(EconomyOwnerKind.Role, roleId.ToString(System.Globalization.CultureInfo.InvariantCulture)), item, -count),
                 new(new(EconomyOwnerKind.Guild, guildId.ToString(System.Globalization.CultureInfo.InvariantCulture)), materials, materialCount)],
                JsonSerializer.SerializeToUtf8Bytes(new { Role = roleId, Guild = guildId, Count = count }));
            granted = await _character.ExchangeLockedAsync(s, [(1258002, count)], [], persist: tx =>
            {
                if (s.Dead || s.SceneLoading || !Playing) throw new InvalidOperationException("Escort submitter unavailable");
                _guilds.EnlistMaterialContribution(roleId, guildId, materialCount, tx);
                tx.WithEventEconomy(mutation);
            }, asyncCommit: true);
        }
        finally { _gate.Release(); reservation?.Dispose(); }
        if (granted != null) await _guildService.RefreshResourceAsync(guildId);
    }

    private async Task ClaimAsync(GameSession s, TerritoryWarsEscortGuildPlayerGetScoreAwardReq req)
    {
        int index = req.AwardIndex;
        var roleState = s.TerritoryEscort;
        if (roleState == null || index is < 0 or > 4 || roleState.ScoreByRound.GetValueOrDefault(Round) < ScoreThresholds[index]
            || roleState.ClaimedByRound.GetValueOrDefault(Round)?.Contains(index) == true) return;
        using var reservation = await EnterGateAsync("escort/claim", s);
        bool accepted = false;
        try
        {
            using var held = await CharacterService.Lock(s);
            roleState = s.TerritoryEscort;
            if (roleState == null || roleState.ScoreByRound.GetValueOrDefault(Round) < ScoreThresholds[index]
                || roleState.ClaimedByRound.GetValueOrDefault(Round)?.Contains(index) == true) return;
            var plan = _awards.Resolve(s, 1001);
            var grants = plan.Grants.Select(g => (g.Entry, g.Count)).ToArray();
            var stagedRoleState = JsonSerializer.Deserialize<RoleState>(JsonSerializer.SerializeToUtf8Bytes(roleState))!;
            int round = Round;
            long roleId = s.Role!.Id;
            var attachment = s.Attachment;
            if (!stagedRoleState.ClaimedByRound.TryGetValue(round, out var claims))
                stagedRoleState.ClaimedByRound[round] = claims = new HashSet<int>();
            claims.Add(index);
            var mutation = EventEconomy.ForRole(_data, s, "escort-claim",
                $"escort-claim:{Epoch}:{round}:{roleId}:{index}", [], plan.Grants,
                new { Round = round, Role = roleId, Index = index });
            var granted = await _character.ExchangeLockedAsync(s, [], grants, persist: tx =>
            {
                tx.RoleBlob(roleId, "territory_escort", JsonSerializer.SerializeToUtf8Bytes(stagedRoleState));
                tx.WithEventEconomy(mutation);
            }, asyncCommit: true, appliedBeforeNotify: () =>
            {
                if (s.Role?.Id == roleId && ReferenceEquals(s.Attachment, attachment))
                    s.TerritoryEscort = stagedRoleState;
            });
            accepted = granted != null && s.Role?.Id == roleId && ReferenceEquals(s.Attachment, attachment);
        }
        finally { _gate.Release(); reservation?.Dispose(); }
        if (accepted) await SendScoreAsync(s);
    }

    private Task QueryOwnerAsync(GameSession s, EscortQueryGuildByOwnerReq req)
    {
        bool has = req.PlayerId > 0 && ledger.Carts.Values.Any(c => c.Owner == req.PlayerId && c.Guild == req.GuildId);
        return s.SendGameAsync(QueryOwner, new EscortQueryGuildByOwnerRsp { IsHave = has });
    }

    private async Task MarkAsync(GameSession s, EscortSetMarkPosReq req)
    {
        long team = _teams.TeamOf(RoleId(s)).TeamId;
        int result = team == 0 || !Playing || s.Dead || s.SceneLoading || !EscortLine(s) ? ErrorCodes.FeatureUnavailable : 0;
        if (result == 0 && (req.Pos == null || !Near(s.Position, req.Pos.Pos, 15))) result = ErrorCodes.Parameter;
        if (result == 0)
        {
            if (req.BDel) _marks.TryRemove((team, (int)req.MarkType), out _);
            else _marks[(team, (int)req.MarkType)] = req.Pos!.Clone();
        }
        await s.SendGameAsync(Mark, new EscortSetMarkPosRsp { Result = result });
        if (result == 0)
        {
            var ntf = new EscortSetMarkPosNtf { MarkType = req.MarkType };
            if (_marks.TryGetValue((team, (int)req.MarkType), out var pos)) ntf.Pos.Add(pos);
            await RoleFanout.EachAsync(_sessions.All().Where(x => x.Role != null && _teams.TeamOf(x.Role.Id).TeamId == team),
                peer => peer.SendGameAsync(MarkNtf, ntf));
        }
    }

    private Task GatherAsync(GameSession s, EscortGatherReq req)
    {
        long team = _teams.TeamOf(RoleId(s)).TeamId;
        int result = !s.Dead && !s.SceneLoading && EscortLine(s) && team != 0
            && _marks.TryGetValue((team, (int)req.MarkType), out var p) && p.Id == req.MarkId
            ? 0 : ErrorCodes.Parameter;
        return s.SendGameAsync(Gather, new EscortGatherRsp { Result = result });
    }

    /// <summary>Issue a server-chosen group transfer invitation; request 83099 must match this token and destination.</summary>
    public async Task<long> InviteGroupTransferAsync(GameSession inviter, GameSession recipient)
    {
        if (inviter.Role == null || recipient.Role == null || !Playing
            || inviter.Dead || recipient.Dead || inviter.SceneLoading || recipient.SceneLoading || !EscortLine(inviter) ||
            _teams.TeamOf(inviter.Role.Id).TeamId == 0 || _teams.TeamOf(inviter.Role.Id).TeamId != _teams.TeamOf(recipient.Role.Id).TeamId)
            return 0;
        long token = Interlocked.Increment(ref _nextInvite);
        var pos = inviter.Position.Clone();
        _transferInvites[token] = new TransferInvitation(inviter.Role.Id, recipient.Role.Id, inviter.SceneEntry, pos, _clock.Now + 20);
        await recipient.SendGameAsync(GroupTransferNtf, new EscortGroupTransferNtf
        {
            SceneId = inviter.SceneEntry,
            TransferPos = pos, InviterPlayerId = inviter.Role.Id, InviterUid = token
        });
        return token;
    }

    private async Task GroupTransferAsync(GameSession s, EscortGroupTransferReq req)
    {
        int result = ErrorCodes.Parameter;
        TransferInvitation? valid = null;
        if (Playing && !s.Dead && !s.SceneLoading
            && _transferInvites.TryGetValue(req.InviterUid, out var invite) && invite.Recipient == RoleId(s)
            && invite.Until >= _clock.Now && invite.Scene == req.SceneId && Near(invite.Position, req.TransferPos, 0.1f)
            && _teams.TeamOf(invite.Inviter).TeamId != 0 && _teams.TeamOf(invite.Inviter).TeamId == _teams.TeamOf(RoleId(s)).TeamId)
        {
            // (inferred) The invitation's server-recorded position is the only accepted destination.
            if (_transferInvites.TryRemove(req.InviterUid, out _)) valid = invite;
        }
        if (valid != null && await _scene.EnterSceneAsync(s, valid.Scene,
            new MapPoint(valid.Position.X, valid.Position.Y, valid.Position.Z, s.Face))) result = 0;
        await s.SendGameAsync(GroupTransfer, new EscortGroupTransferRsp { Result = result });
    }

    private async Task UseNpcSkillAsync(GameSession s, EscortUseNpcSkillReq req)
    {
        var g = Own(s);
        int result = g == null || !Playing || s.Dead || s.SceneLoading || !EscortLine(s) ? ErrorCodes.FeatureUnavailable : 0;
        var configuredPoint = _data.Table("EscortPointAreaConfig").Values.Where(row =>
            GameData.Int(row, "_iPointId") == req.PointId && GameData.Int(row, "_iMapId") == s.SceneEntry)
            .SelectMany(row => row.TryGetProperty("_kOutpostlNpcConfig", out var points)
                && points.ValueKind == JsonValueKind.Array ? points.EnumerateArray().ToArray() : Array.Empty<JsonElement>())
            .FirstOrDefault(t => t.ValueKind == JsonValueKind.Array && t.GetArrayLength() >= 5
                && GameData.Number(t[4]) == req.SkillId && NearPoint(s.Position, s.SceneEntry,
                    (long)(GameData.Number(t[1]) ?? 0), 6));
        if (result == 0 && configuredPoint.ValueKind != JsonValueKind.Array) result = ErrorCodes.Parameter;
        long pointOwner = OwnerOf?.Invoke(req.PointId) ?? 0;
        if (result == 0 && pointOwner > 0 && pointOwner != g!.Id)
            result = ErrorCodes.GuildPermission;
        var key = $"{g?.Id ?? 0}:{req.PointId}:{req.SkillId}";
        long cooldownEnd = 0;
        using var reservation = await EnterGateAsync("escort/play", s);
        using var stagedEscortLedger = StageLedger();
        try
        {
            if (result == 0 && (s.Dead || s.SceneLoading || !EscortLine(s))) result = ErrorCodes.FeatureUnavailable;
            if (result == 0 && ledger.SkillCooldowns.GetValueOrDefault(key) > _clock.Now) result = ErrorCodes.Cooldown;
            if (result == 0)
            {
                int seconds = (int)(GameData.Number(configuredPoint[3]) ?? 60);
                cooldownEnd = _clock.Now + seconds;
                ledger.SkillCooldowns[key] = cooldownEnd;
                await CommitAsync(_accounts.NewTransaction(), "npc-skill", actor: s);
            }
        }
        finally { stagedEscortLedger.Dispose(); _gate.Release(); reservation?.Dispose(); }
        if (result != 0) return; // NullArg response.
        var info = new EscortNpcSkill
        {
            PointId = req.PointId, GuildId = g!.Id, SkillId = req.SkillId,
            CDEndTime = cooldownEnd, Success = true,
            EffectEndTime = _clock.Now + (req.SkillId == 5800801 ? 300 : 20)
        };
        await RoleFanout.EachAsync(_sessions.All().Where(x => x.WorldScene == s.WorldScene), async peer =>
        {
            var ntf = new EscortNpcSkillNtf(); ntf.NpcSkills.Add(info);
            await peer.SendGameAsync(NpcSkillNtf, ntf);
            await peer.SendGameAsync(NpcSkillSuccessNtf, new EscortNpcSkillSuccessfulNtf
            {
                SkillId = req.SkillId,
                EffectEndTime = info.EffectEndTime
            });
            if (req.SkillId == 5800801)
            {
                var detected = new EscortDetectMiniMapNtf();
                foreach (var cart in ledger.Carts.Values.Where(c => c.Scene == s.SceneEntry && c.Guild != g.Id))
                    detected.EscortInfo.Add(new MiniMapInfo
                    {
                        Uid = (ulong)cart.Id, Pos = CartWire(cart).Pos,
                        NpcConfigId = (ulong)(cart.Type == 0 ? 4910001 : 4910002),
                        SpecialType = (MiniMapInfoSpecialType)1,
                        EscortCartInfo = new MiniMapInfoEscortCart { OwnerId = cart.Owner, GuildId = cart.Guild, CartType = cart.Type }
                    });
                await peer.SendGameAsync(DetectNtf, detected);
            }
        });
        if (req.SkillId == 5800901)
        {
            // (inferred) The configured group-transfer outpost skill invites up to its 20 team members;
            // recipients still need the server-issued one-time token before accepting 83099.
            long team = _teams.TeamOf(RoleId(s)).TeamId;
            if (team != 0)
                await RoleFanout.EachAsync(_sessions.All().Where(x => x != s && x.Role != null
                    && _teams.TeamOf(x.Role.Id).TeamId == team && Own(x)?.Id == g.Id).Take(20),
                    peer => InviteGroupTransferAsync(s, peer));
        }
    }

    private static bool Near(Vec3? a, Vec3? b, float radius) => a != null && b != null
        && float.IsFinite(a.X) && float.IsFinite(a.Y) && float.IsFinite(a.Z)
        && float.IsFinite(b.X) && float.IsFinite(b.Y) && float.IsFinite(b.Z)
        && (a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y) + (a.Z - b.Z) * (a.Z - b.Z) <= radius * radius;

    private bool NearPoint(Vec3 position, long sceneId, long pointId, float radius)
    {
        if (_maps.Map(sceneId)?.Points.TryGetValue(pointId, out var point) != true) return false;
        float dx = position.X - point.X, dy = position.Y - point.Y, dz = position.Z - point.Z;
        return float.IsFinite(dx) && float.IsFinite(dy) && float.IsFinite(dz)
            && dx * dx + dy * dy + dz * dz <= radius * radius;
    }
}

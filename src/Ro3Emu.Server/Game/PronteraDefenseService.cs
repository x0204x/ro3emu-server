using System.Text.Json;
using Romsg;
using Ro3Emu.Server.Accounts;
using Ro3Emu.Server.Accounts.Persistence;
using Ro3Emu.Server.Data;
using Ro3Emu.Server.Data.Config;
using Ro3Emu.Server.Maps;
using Ro3Emu.Server.Economy;

namespace Ro3Emu.Server.Game;

/// <summary>
/// Battle for Prontera 118. TimeLimit 1012 opens the activity; the four
/// PronteraDefenseIntensityConfig offsets are measured from that opening (inferred: measuring from season
/// start would place two configured rounds before the activity opens). Round/map/point/monster config owns
/// the entities, CombatService owns kills, and 82976 only accepts server-checked item 600 or guild stations.
/// The invaders march on the city's heart and fight the defenders on the way (<see cref="SpawnAsync"/>); City Defense
/// falls each minute by the invasion intensity and kills restore it (<see cref="DamageDefensesAsync"/>).
/// </summary>
public sealed class PronteraDefenseService : IGameTicker
{
    public const int ActivityId = 118, TimeLimitId = 1012, SupplyItem = 600;
    private const int UnlockLevel = 99;
    /// <summary>(inferred) City Defense lost each minute = the current intensity / this (see DamageDefensesAsync).</summary>
    internal const long ErosionDivisor = 100;
    /// <summary>(inferred) An invader stops within this many metres of its own spot at the city's heart.</summary>
    internal const float HeartRadius = 2f;
    /// <summary>(inferred) The invaders' spots spread this far around the heart, so a wave does not stand in one cell.</summary>
    internal const float HeartSpread = 4f;
    private readonly GameData _data;
    private readonly ActivityClock _clock;
    private readonly RoleStore _roles;
    private readonly AccountStore _accounts;
    private readonly GuildStore _guilds;
    private readonly CharacterService _character;
    private readonly AwardService _awards;
    private readonly FunctionService _functions;
    private readonly MonsterService _monsters;
    private readonly WorldData _worldData;
    private readonly World _world;
    private readonly SessionRegistry _sessions;
    private readonly GameLoop _loop;
    private readonly ILogger<PronteraDefenseService> _log;
    private readonly Dictionary<long, MonsterUnit> _actors = new();
    /// <summary>City Defense a kill of each live invader restores (its difficulty's _iKillPoint).</summary>
    private readonly Dictionary<long, long> _restore = [];
    private readonly Dictionary<int, MonsterTemplate> _invaders = [];
    private readonly HashSet<(int Round, int Point, int Wave)> _spawned = new();
    private readonly Dictionary<long, long> _defensePoints = new();
    private readonly Dictionary<long, long> _station = new();
    private int _activeRound;
    private long _lastSecond = -1;
    private long _lastDamageMinute = -1;
    private bool _advancing;

    public Func<GameSession, Task>? Changed { get; set; }

    public sealed class State
    {
        public long SeasonOpen { get; set; }
        public long ContributionPoint { get; set; }
        public Dictionary<int, long> RoundContribution { get; set; } = new();
        public Dictionary<int, long> RoundMap { get; set; } = new();
        public HashSet<int> SettledRounds { get; set; } = new();
    }

    public PronteraDefenseService(GameData data, ActivityClock clock, RoleStore roles, AccountStore accounts,
        GuildStore guilds, CharacterService character, AwardService awards, FunctionService functions, MonsterService monsters, WorldData worldData, World world,
        CombatService combat, SessionRegistry sessions, GameLoop loop, GmCommands gm, ILogger<PronteraDefenseService> log)
    {
        this._data = data; this._clock = clock; this._roles = roles; this._accounts = accounts; this._guilds = guilds;
        this._character = character; this._awards = awards; this._functions = functions; this._monsters = monsters; this._worldData = worldData; this._world = world;
        this._sessions = sessions; this._loop = loop; this._log = log;
        LoadWorld();
        combat.Killed.Add(OnKilledAsync);
        gm.Register("defense", "round <1-4> | end | status (Battle for Prontera test aid: TimeLimit 1012 opens so that round n starts now, "
            + "or back to the real schedule; status lists City Defense and the invaders)", GmAsync);
    }

    public async Task LoginAsync(GameSession session)
    {
        if (session.Role == null) return;
        session.Defense = _roles.LoadTimedActivity(session.Role.Id) is { } bytes
            ? JsonSerializer.Deserialize<State>(bytes) ?? new State() : new State();
        await SettleClosedOwnedAsync(session);
    }

    /// <summary>5013 activity 118 projection. Root's bridge handles empty-map wire initialization.</summary>
    public PlayerActivityPronteraDefense BuildPlayerState(GameSession session) => new()
    {
        ContributionPoint = session.Defense?.SeasonOpen == Opening() ? session.Defense.ContributionPoint : 0,
        GlobalInfo = BuildGlobal(),
    };

    public DBPronteraDefenseInfo BuildGlobal()
    {
        long now = _clock.UtcNow.ToUnixTimeSeconds();
        var result = new DBPronteraDefenseInfo { CurRound = RoundAt(now), CurIntensity = IntensityAt(now) };
        foreach (int round in Enumerable.Range(1, 4))
        {
            if (RoundWindow(round) is not { } w) continue;
            var one = new DBPronteraDefenseRoundInfo { RoundId = round, StartTime = w.Start * 1000, EndTime = w.End * 1000 };
            foreach (var row in _data.Table("PronteraDefenseRoundConfig").Values.Where(r => GameData.Int(r, "_iRoundId") == round))
                one.MapIds[GameData.Int(row, "_iMapId")] = true;
            result.RoundInfo[round] = one;
        }
        foreach (var row in _data.Table("PronteraDefenseMapConfig").Values)
        {
            long map = GameData.Int(row, "_iMapId"), max = GameData.Int(row, "_iDefensePoint");
            if (map <= 0 || max <= 0) continue;
            long hp = _defensePoints.GetValueOrDefault(map, max);
            var one = new DBPronteraDefenseMapInfo
            {
                MapId = map, DefensePoint = hp, MaxDefensePoint = max, IsFall = hp <= 0,
                FallRound = hp <= 0 ? result.CurRound : 0,
                CurResistLevel = 0, DefendRoundCount = Math.Max(0, result.CurRound - 1),
            };
            foreach (var (guild, at) in _station.Where(x => x.Value == map))
            {
                one.DefenseGuilds[guild] = true;
                one.GuildContributionBoard[guild] = new DBPronteraDefenseGuildBoardInfo { GuildId = guild };
            }
            result.MapInfo[map] = one;
        }
        foreach (var (guild, _) in _station)
            if (_guilds.Get(guild) is { } g)
                result.GuildInfo[guild] = new DBPronteraDefenseGuildInfo { GuildId = guild, GuildPrepLv = g.Level };
        return result;
    }

    /// <summary>Complete station/event participants selected before Common's activity gate admission.</summary>
    internal Ownership.ReservationKey[] OwnerKeys(GameSession session, CommonActivityReq req)
    {
        var keys = new List<Ownership.ReservationKey>
        { Ownership.ReservationKey.Entity("prontera_event", Opening().ToString(System.Globalization.CultureInfo.InvariantCulture)) };
        if (req.PronteraDefenseGuildDefense != null && session.Role != null
            && _guilds.ForRole(session.Role.Id) is { } guild)
        {
            keys.Add(Ownership.ReservationKey.Entity("guild", guild.Id.ToString(System.Globalization.CultureInfo.InvariantCulture)));
            keys.Add(Ownership.ReservationKey.Entity("prontera_station", $"{Opening()}/{guild.Id}"));
        }
        return keys.ToArray();
    }

    /// <summary>82976 activity 118; caller sends response and invokes 5013 projection after success.</summary>
    public async Task<CommonActivityRes> HandleAsync(GameSession session, CommonActivityReq req)
    {
        var response = new CommonActivityRes { ActivityId = ActivityId, RequestId = req.RequestId };
        if (session.Role == null || session.Progress == null || session.Defense == null || req.ActivityId != ActivityId)
        { response.Result = ErrorCodes.Parameter; return response; }
        long now = _clock.UtcNow.ToUnixTimeSeconds();
        if (session.Progress.Level < UnlockLevel || !_functions.IsOpen(session, 68)
            || !_clock.IsOpen(TimeLimitId, _clock.UtcNow))
        { response.Result = ErrorCodes.FeatureUnavailable; return response; }
        if (req.PronteraDefenseDonate is { } donation)
        {
            response.PronteraDefenseDonate = new PronteraDefenseDonateResp { Req = donation.Clone() };
            long count = donation.ItemCount;
            if (count <= 0 || count > int.MaxValue) response.Result = ErrorCodes.Parameter;
            else if (_character.CountOf(session, SupplyItem) < count) response.Result = ErrorCodes.ItemsShort;
            else
            {
                var next = CopyState(session.Defense);
                next.ContributionPoint = checked(next.ContributionPoint + count); // one point per supply: inferred.
                int round = RoundAt(now);
                if (round > 0) next.RoundContribution[round] = next.RoundContribution.GetValueOrDefault(round) + count;
                byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(next);
                var committed = await _character.ExchangeAsync(session, [(SupplyItem, count)], [], popup: false,
                    persist: tx =>
                    {
                        tx.RoleBlob(session.Role.Id, "timed_activity", bytes);
                        tx.WithEventEconomy(EventEconomy.ForRole(_data, session, "prontera-donate",
                            $"prontera/donate/{Opening()}/{session.Role.Id}/{Guid.NewGuid():N}",
                            [(SupplyItem, count)], [], new { Count = count, next.ContributionPoint }));
                    },
                    committed: () => session.Defense = next);
                response.Result = committed == null ? ErrorCodes.Unknown : 0;
                if (committed != null) await NotifyAsync(session);
            }
        }
        else if (req.PronteraDefenseGuildDefense is { } enlist)
        {
            response.PronteraDefenseGuildDefense = new PronteraDefenseGuildDefenseResp { Req = enlist.Clone() };
            var guild = _guilds.ForRole(session.Role.Id);
            int round = RoundAt(now);
            response.Result = guild == null || !guild.Members.TryGetValue(session.Role.Id, out var member) || member.Official != 1
                ? ErrorCodes.FeatureUnavailable
                : !resultMap(round, enlist.MapId) ? ErrorCodes.Parameter : 0;
            if (response.Result == 0)
            {
                int changed = await _accounts.WriteAsync<int>(new PronteraDefenseStationPlan(
                    Opening(), guild!.Id, enlist.MapId, enlist.BStation));
                response.Result = !enlist.BStation && changed == 0 ? ErrorCodes.Parameter : 0;
                if (response.Result == 0)
                { if (enlist.BStation) _station[guild.Id] = enlist.MapId; else _station.Remove(guild.Id); await NotifyAllAsync(); }
            }
        }
        else if (req.ActivityCase == CommonActivityReq.ActivityOneofCase.None)
        {
            response.Result = 0; // global-info query carries only activityId (LC_PronteraDefenseHelper.lua, inferred reply).
            await NotifyAsync(session);
        }
        else response.Result = ErrorCodes.Parameter;
        return response;
    }

    private bool resultMap(int round, long map) => round > 0 && _data.Table("PronteraDefenseRoundConfig").Values.Any(r =>
        GameData.Int(r, "_iRoundId") == round && GameData.Int(r, "_iMapId") == map);

    public void Tick(GameTime _)
    {
        long now = _clock.UtcNow.ToUnixTimeSeconds();
        if (now == _lastSecond || _advancing) return;
        _lastSecond = now;
        _advancing = true;
        _loop.PostRuntime(async () => { try { await AdvanceAsync(now); } finally { _advancing = false; } });
    }

    private async Task AdvanceAsync(long now)
    {
        bool roundChanged;
        long opening = Opening();
        var reservations = AdvanceReservations(opening, PronteraDefenseMapConfig.All(_data).Select(row => row.MapId));
        using (var scope = _loop.Owners is { } owners
            ? await owners.EnterAsync("defense/advance", reservations) : null)
        {
            int round = RoundAt(now);
            roundChanged = round != _activeRound;
            if (roundChanged)
            {
                foreach (var actor in _actors.Values.ToArray())
                { actor.Dead = true; actor.Target = null; await _world.RemoveUnitAsync(actor); }
                _actors.Clear(); _restore.Clear(); _spawned.Clear(); _activeRound = round;
                _lastDamageMinute = now / 60; // the first loss comes a full minute into the round
            }
            if (round != 0 && RoundWindow(round) is { } w)
            {
                await DamageDefensesAsync(now, round, opening);
                foreach (var point in _data.Table("PronteraDefensePointConfig").Values)
                {
                    int pointId = (int)GameData.Int(point, "_iId");
                    long map = GameData.Int(point, "_iMapId");
                    if (!resultMap(round, map) || !point.TryGetProperty("_iPointId", out var ids)
                        || ids.ValueKind != JsonValueKind.Array || ids.GetArrayLength() == 0) continue;
                    long scenePoint = (long)(GameData.Number(ids[0]) ?? 0);
                    if (_worldData.Map(map)?.Points.TryGetValue(scenePoint, out var at) != true) continue;
                    if (!point.TryGetProperty("_iRoundMonster", out var waves) || waves.ValueKind != JsonValueKind.Array || waves.GetArrayLength() == 0)
                    {
                        // Point rows 1-20 have no wave tuples; one configured difficulty-matched defender target (inferred).
                        if (_spawned.Add((round, pointId, 0)))
                        {
                            int selector = (int)GameData.Int(point, "_iDifficulty");
                            await SpawnAsync(map, selector, at);
                        }
                        continue;
                    }
                    int index = 0;
                    foreach (var wave in waves.EnumerateArray())
                    {
                        if (wave.ValueKind != JsonValueKind.Array || wave.GetArrayLength() < 3 || GameData.Number(wave[0]) is not { } offset)
                        { index++; continue; }
                        if (now < w.Start + (long)offset || !_spawned.Add((round, pointId, ++index))) continue;
                        for (int p = 1; p + 1 < wave.GetArrayLength(); p += 2)
                        {
                            int selector = (int)(GameData.Number(wave[p]) ?? 0), count = (int)(GameData.Number(wave[p + 1]) ?? 0);
                            for (int n = 0; n < Math.Clamp(count, 0, 20); n++)
                                await SpawnAsync(map, selector, at with { X = at.X + (n % 4) * 1.5f, Z = at.Z + (n / 4) * 1.5f });
                        }
                    }
                }
            }
        }
        if (roundChanged)
            await RoleFanout.EachAsync(_sessions.All().Where(s => s.Role != null && s.ActivitiesReady),
                async s => { await SettleClosedOwnedAsync(s); await NotifyAsync(s); });
    }

    internal static Ownership.ReservationKey[] AdvanceReservations(long opening, IEnumerable<long> maps)
    {
        string open = opening.ToString(System.Globalization.CultureInfo.InvariantCulture);
        return [Ownership.ReservationKey.Entity("prontera_event", open),
            .. maps.Where(map => map > 0).Distinct().Select(map => Ownership.ReservationKey.Entity("prontera_point",
                $"{open}:{map.ToString(System.Globalization.CultureInfo.InvariantCulture)}"))];
    }

    /// <summary>
    /// UITipsConfig 70015, the help of the in-city defense bar (UI_Sys_Season_DefenceWarProgressWidget), text 10960000247:
    /// "City Defense decreases every minute based on the intensity of the monster invasion. Defeat monsters to restore
    /// City Defense. If City Defense falls to @{1}, the city will be overrun." (@{1} = "0"). Each minute every map of the
    /// live round (DBPronteraDefenseRoundInfo.mapIds, the maps the client shows as under attack) loses the current
    /// intensity (PronteraDefenseIntensityConfig _kIntensity, 80000-140000, shown divided by 10000) / ErosionDivisor;
    /// a kill restores its difficulty's _iKillPoint (<see cref="RestoreAsync"/>). The divisor is inferred: with it one
    /// minute at the opening intensity costs 800, exactly one difficulty-1 kill (PronteraDefenseDifficultyConfig 1), and
    /// an undefended Prontera loses 30 800 of its 100 000 in a round, so it falls in the fourth.
    /// </summary>
    private async Task DamageDefensesAsync(long now, int round, long opening)
    {
        long minute = now / 60;
        if (minute == _lastDamageMinute) return;
        long loss = IntensityAt(now) / ErosionDivisor;
        var changed = new Dictionary<long, long>();
        foreach (var city in PronteraDefenseMapConfig.All(_data))
        {
            if (city.MapId <= 0 || city.DefensePoint <= 0 || !resultMap(round, city.MapId)) continue;
            long previous = _defensePoints.GetValueOrDefault(city.MapId, city.DefensePoint);
            long next = Math.Max(0, previous - loss);
            if (next != previous) changed[city.MapId] = next;
        }
        if (changed.Count == 0) { _lastDamageMinute = minute; return; }
        var tx = _accounts.NewTransaction();
        foreach (var (map, point) in changed) tx.Add(new PronteraDefensePointPlan(opening, map, point));
        await tx.CommitAsync(_accounts, "prontera-defense/damage", _ =>
        {
            foreach (var (map, point) in changed) _defensePoints[map] = point;
            _lastDamageMinute = minute;
        });
        await NotifyAllAsync();
    }

    /// <summary>
    /// A kill restores City Defense up to the map's maximum (UITipsConfig 70015 above); how much is its difficulty's
    /// PronteraDefenseDifficultyConfig _iKillPoint (800/5000/20000/60000, inferred reading of the column). A city at 0
    /// has fallen ("overrun") and stays fallen for the event (inferred).
    /// </summary>
    private async Task RestoreAsync(long map, long points)
    {
        long max = PronteraDefenseMapConfig.All(_data).Where(c => c.MapId == map).Select(c => c.DefensePoint).FirstOrDefault();
        long previous = _defensePoints.GetValueOrDefault(map, max);
        if (points <= 0 || max <= 0 || previous <= 0 || previous >= max) return;
        long next = Math.Min(max, previous + points);
        var tx = _accounts.NewTransaction();
        tx.Add(new PronteraDefensePointPlan(Opening(), map, next));
        await tx.CommitAsync(_accounts, "prontera-defense/restore", _ => _defensePoints[map] = next);
        await NotifyAllAsync();
    }

    /// <summary>
    /// One invader. What it does is not in the client data: PronteraDefenseMonsterConfig/PointConfig/RoundConfig only
    /// say who appears where and when, and the main invaders' AI (AIConfig 20200006: no roaming, 5 m view, a 9990 m
    /// leash and hatred that never fades) leaves the moving to the server. The broadcasts of the event (ChatConfig
    /// 12009-12038) tell the story the point schedule follows: the enemy gathers south, breaks in at the South Gate
    /// (point 837 at 30 s), presses on the Inn and Library and the flanks (851/852/853/854) and closes on "the heart of
    /// Prontera" (855/856/858-861 from 1440 s, Osiris at 858 at 1560 s). Inferred from that: every invader walks from
    /// its point to the city's heart, the map's default born point (Prontera: 156,182, the central plaza beside the
    /// scene editor's defense group 832 at 156,203), attacks the players it meets on the way (aggressive while it
    /// invades) and holds the heart. NPCs without a monster template (the rift 21001068, NPCType 2) do not appear.
    /// </summary>
    private async Task SpawnAsync(long map, int selector, MapPoint point)
    {
        if (PronteraDefenseMonsterConfig.Get(_data, selector) is not { } config || config.NpcId <= 0
            || Invader((int)config.NpcId) is not { } template) return;
        if (await _monsters.SpawnSingleAsync(map, template.NpcId, point, customTemplate: template) is not { } unit) return;
        _actors[unit.Uid] = unit;
        _restore[unit.Uid] = PronteraDefenseDifficultyConfig.Get(_data, config.Difficulty)?.KillPoint ?? 0;
        if (Heart(map) is { } heart) unit.March = new MonsterMarch(heart.X, heart.Z, HeartRadius) { LeashX = unit.X, LeashZ = unit.Z };
    }

    /// <summary>The live round's invaders (read on the game loop).</summary>
    internal IEnumerable<MonsterUnit> Invaders => _actors.Values;

    private MonsterTemplate? Invader(int npc)
    {
        if (_invaders.TryGetValue(npc, out var cached)) return cached;
        if (_monsters.Template(npc) is not { } template) return null;
        return _invaders[npc] = template with { Aggressive = true };
    }

    /// <summary>The invader's own spot at the city's heart: the default born point, spread a little, on walkable ground.</summary>
    private MapPoint? Heart(long map)
    {
        if (_worldData.Map(map) is not { Born: { } born } world) return null;
        double angle = Random.Shared.NextDouble() * Math.PI * 2, radius = Random.Shared.NextDouble() * HeartSpread;
        return world.Standable(born with { X = born.X + (float)(Math.Cos(angle) * radius), Z = born.Z + (float)(Math.Sin(angle) * radius) });
    }

    private async Task OnKilledAsync(GameSession killer, MonsterUnit monster)
    {
        if (!_actors.ContainsKey(monster.Uid) || killer.Role == null || killer.Defense == null || _activeRound == 0
            || RoundAt(_clock.UtcNow.ToUnixTimeSeconds()) != _activeRound) return;
        var role = killer.Role;
        var state = killer.Defense;
        var attachment = killer.Attachment;
        var owner = killer.Owner;
        long? epoch = owner?.LifecycleEpoch;
        using var scope = _loop.Owners is { } owners
            ? await owners.EnterAsync("defense/kill contribution", [
                EventKey(), PointKey(monster.SceneEntry),
                Ownership.ReservationKey.Entity("prontera_actor", monster.Uid.ToString(System.Globalization.CultureInfo.InvariantCulture)),
                Ownership.ReservationKey.Role(role.Id)]) : null;
        if (!ReferenceEquals(killer.Role, role) || !ReferenceEquals(killer.Defense, state)
            || !ReferenceEquals(killer.Attachment, attachment) || !ReferenceEquals(killer.Owner, owner)
            || owner?.LifecycleEpoch != epoch || RoundAt(_clock.UtcNow.ToUnixTimeSeconds()) != _activeRound
            || !_actors.ContainsKey(monster.Uid)) return;
        if (owner != null) scope?.Context.Pin(owner);
        var next = CopyState(state);
        next.ContributionPoint++;
        next.RoundContribution[_activeRound] = next.RoundContribution.GetValueOrDefault(_activeRound) + 1;
        next.RoundMap[_activeRound] = monster.SceneEntry;
        var tx = _roles.NewTransaction();
        tx.RoleBlob(role.Id, "timed_activity", JsonSerializer.SerializeToUtf8Bytes(next));
        await _roles.CommitAsync(tx, "prontera-defense/kill", _ =>
        { killer.Defense = next; _actors.Remove(monster.Uid); });
        await NotifyAsync(killer);
        if (_restore.Remove(monster.Uid, out long restore)) await RestoreAsync(monster.SceneEntry, restore);
    }

    /// <summary>C/D 15: the role's own round settlement entities, reserved up front by the role select (the season
    /// comes from the role's blob, served by the login snapshot).</summary>
    internal Ownership.ReservationKey[] LoginKeys(long roleId)
    {
        long season = _roles.LoadTimedActivity(roleId) is { } bytes
            ? (JsonSerializer.Deserialize<State>(bytes) ?? new State()).SeasonOpen : new State().SeasonOpen;
        return SettlementKeys(roleId, season);
    }

    private static Ownership.ReservationKey[] SettlementKeys(long roleId, long season) =>
        Enumerable.Range(1, 4).Select(round =>
            Ownership.ReservationKey.Entity("prontera_round_settlement", $"{season}/{round}/{roleId}")).ToArray();

    private async Task SettleClosedOwnedAsync(GameSession session)
    {
        if (session.Role == null || session.Defense == null) return;
        long roleId = session.Role.Id;
        Ownership.ReservationKey[] keys =
            [Ownership.ReservationKey.Role(roleId), Ownership.ReservationKey.Lifecycle(roleId), .. SettlementKeys(roleId, session.Defense.SeasonOpen)];
        if (_loop.Owners is { } owners)
            await owners.RunSessionAsync("defense/settle", keys, session, () => SettleClosedAsync(session));
        else await SettleClosedAsync(session);
    }

    private async Task SettleClosedAsync(GameSession session)
    {
        if (session.Role == null || session.Defense == null) return;
        foreach (int round in Enumerable.Range(1, 4))
        {
            if (RoundWindowFor(session.Defense.SeasonOpen, round) is not { } w || _clock.UtcNow.ToUnixTimeSeconds() < w.End
                || session.Defense.SettledRounds.Contains(round)
                || session.Defense.RoundContribution.GetValueOrDefault(round) <= 0) continue;
            long map = session.Defense.RoundMap.GetValueOrDefault(round, 10100);
            var next = CopyState(session.Defense); next.SettledRounds.Add(round);
            // One award after actual contribution and round closure is an inferred eligibility rule.
            string key = map == 10100 ? "PronteraDefenseMainCityDefendAward" : "PronteraDefenseOtherCityDefendAward";
            long group = _data.Table("ActivityConstant").TryGetValue(key, out var configured)
                ? (long)(GameData.Number(configured) ?? 0) : 0;
            try
            {
                var plan = _awards.Resolve(session, group, 101);
                if (plan.Grants.Count == 0) throw new AwardResolutionException($"Prontera round {round} has empty award {group}");
                if (await _character.ApplyAwardAsync(session, plan, persist: tx =>
                {
                    tx.RoleBlob(session.Role.Id, "timed_activity", JsonSerializer.SerializeToUtf8Bytes(next));
                    if (plan.Grants.Any(grant => grant.Entry is not (100 or 101)))
                        tx.WithEventEconomy(EventEconomy.ForRole(_data, session, "prontera-round",
                            $"prontera/round/{next.SeasonOpen}/{round}/{session.Role.Id}", [], plan.Grants,
                            new { Season = next.SeasonOpen, Round = round, Map = map }));
                }, committed: () => session.Defense = next))
                { await NotifyAsync(session); }
                else _log.LogWarning("Prontera round {Round} reward transaction failed for {Role} on {Map}", round, session.Role.Id, map);
            }
            catch (AwardResolutionException e)
            { _log.LogWarning(e, "Prontera round {Round} reward {Group} rejected for {Role}", round, group, session.Role.Id); }
        }
        if (session.Defense.SeasonOpen != Opening() && Enumerable.Range(1, 4).All(round =>
            session.Defense.RoundContribution.GetValueOrDefault(round) <= 0
            || session.Defense.SettledRounds.Contains(round)))
        {
            var next = new State { SeasonOpen = Opening() };
            var tx = _roles.NewTransaction();
            tx.RoleBlob(session.Role.Id, "timed_activity", JsonSerializer.SerializeToUtf8Bytes(next));
            await _roles.CommitAsync(tx, "prontera-defense/new-season", _ => session.Defense = next);
            await NotifyAsync(session);
        }
    }

    private Task NotifyAsync(GameSession s) => Changed?.Invoke(s) ?? Task.CompletedTask;
    private Task NotifyAllAsync() => RoleFanout.EachAsync(
        _sessions.All().Where(s => s.Role != null && s.ActivitiesReady), NotifyAsync);
    private static State CopyState(State source) => JsonSerializer.Deserialize<State>(JsonSerializer.SerializeToUtf8Bytes(source))!;

    private long Opening() => _clock.TryWindow(TimeLimitId, out var start, out _) ? start.ToUnixTimeSeconds() : 0;
    private (long Start, long End)? RoundWindow(int id) => RoundWindowFor(Opening(), id);
    private (long Start, long End)? RoundWindowFor(long open, int id)
    {
        if (open <= 0 || _data.Row("PronteraDefenseIntensityConfig", id) is not { } row
            || !row.TryGetProperty("_iTime", out var t) || t.ValueKind != JsonValueKind.Array || t.GetArrayLength() < 2) return null;
        return (open + (long)(GameData.Number(t[0]) ?? 0), open + (long)(GameData.Number(t[1]) ?? 0));
    }
    private int RoundAt(long now) => _clock.IsOpen(TimeLimitId, DateTimeOffset.FromUnixTimeSeconds(now))
        ? Enumerable.Range(1, 4).FirstOrDefault(id => RoundWindow(id) is { } w && w.Start <= now && now < w.End) : 0;
    private long IntensityAt(long now)
    {
        int id = RoundAt(now);
        if (id == 0 || RoundWindow(id) is not { } w || _data.Row("PronteraDefenseIntensityConfig", id) is not { } row) return 0;
        return row.TryGetProperty("_kIntensity", out var stages) && stages.ValueKind == JsonValueKind.Array
            ? stages.EnumerateArray().Where(s => s.ValueKind == JsonValueKind.Array && s.GetArrayLength() >= 2
                && w.Start + (long)(GameData.Number(s[0]) ?? 0) <= now).Select(s => (long)(GameData.Number(s[1]) ?? 0)).LastOrDefault() : 0;
    }

    public (int Round, long Intensity) ProjectionPhase(long now) => (RoundAt(now), IntensityAt(now));

    /// <summary>5013's global round/intensity projection changes at these configured instants.</summary>
    public long NextProjectionBoundary(long now)
    {
        long next = long.MaxValue;
        void Consider(long at) { if (at > now && at < next) next = at; }
        foreach (int id in Enumerable.Range(1, 4))
        {
            if (RoundWindow(id) is not { } window) continue;
            Consider(window.Start);
            Consider(window.End);
            if (_data.Row("PronteraDefenseIntensityConfig", id) is not { } row
                || !row.TryGetProperty("_kIntensity", out var stages) || stages.ValueKind != JsonValueKind.Array) continue;
            foreach (var stage in stages.EnumerateArray())
            {
                if (stage.ValueKind != JsonValueKind.Array || stage.GetArrayLength() < 2) continue;
                long at = window.Start + (long)(GameData.Number(stage[0]) ?? 0);
                if (at < window.End) Consider(at);
            }
        }
        return next;
    }

    /// <summary>
    /// !defense (test aid): a stand-in window for TimeLimit 1012 (ActivityClock.TestWindows) placed so that round n of
    /// PronteraDefenseIntensityConfig starts now; "end" returns to the real schedule. The event's state is keyed by the
    /// window's opening, so either one starts from that opening's own City Defense and stations.
    /// </summary>
    private async Task GmAsync(GameSession s, string[] args)
    {
        long now = _clock.UtcNow.ToUnixTimeSeconds();
        string reply;
        using (var scope = _loop.Owners is { } owners ? await owners.EnterAsync("defense/gm", [EventKey()]) : null)
        {
            if (args is ["round", var text] && int.TryParse(text, out int round)
                && PronteraDefenseIntensityConfig.Get(_data, round) is { Time: [var from, _, ..] })
            {
                long length = _clock.TryWindow(TimeLimitId, out var start, out var end) && end > start
                    ? (long)(end - start).TotalSeconds : 1814400;
                long open = now - from;
                _clock.TestWindows[TimeLimitId] = new ActivityClock.Window("Start", open, open + length);
                reply = $"Battle for Prontera test window: round {round} started now (opening {open}).";
            }
            else if (args is ["end"])
            {
                _clock.TestWindows.TryRemove(TimeLimitId, out _);
                reply = "Battle for Prontera test window removed; real schedule restored.";
            }
            else if (args is [] or ["status"]) reply = Status(now);
            else throw new ArgumentException("Use !defense round <1-4> | end | status");
            if (args is not ([] or ["status"]))
            {
                _defensePoints.Clear();
                _station.Clear();
                LoadWorld();
                _lastSecond = -1; // the next tick takes the new window up, also within the same second
                _activeRound = -1; // and starts its round afresh: the old invaders go, the waves come again
            }
        }
        if (args is not ([] or ["status"])) await NotifyAllAsync();
        await GmCommands.ReplyAsync(s, reply);
    }

    private string Status(long now)
    {
        int round = RoundAt(now);
        var culture = System.Globalization.CultureInfo.InvariantCulture;
        var text = new System.Text.StringBuilder(string.Create(culture, $"Round {round}, intensity {IntensityAt(now)}."));
        foreach (var city in PronteraDefenseMapConfig.All(_data).Where(c => resultMap(round, c.MapId)))
        {
            var here = _actors.Values.Where(a => !a.Dead && a.SceneEntry == city.MapId).ToArray();
            text.Append(culture, $" Map {city.MapId}: defense {_defensePoints.GetValueOrDefault(city.MapId, city.DefensePoint)}/{city.DefensePoint}, ")
                .Append(culture, $"{here.Length} invaders ({here.Count(a => a.March is { Arrived: true })} at the heart, {here.Count(a => a.Target != null)} fighting).");
        }
        return text.ToString();
    }

    private Ownership.ReservationKey EventKey() =>
        Ownership.ReservationKey.Entity("prontera_event", Opening().ToString(System.Globalization.CultureInfo.InvariantCulture));

    /// <summary>A map's City Defense row, which PronteraDefensePointPlan writes (the erosion and a kill's restore).</summary>
    private Ownership.ReservationKey PointKey(long map) => Ownership.ReservationKey.Entity("prontera_point",
        string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{Opening()}:{map}"));

    private void LoadWorld()
    {
        long open = Opening();
        var rows = _accounts.ReadSync<PronteraDefenseWorldRows>(new PronteraDefenseWorldReadPlan(open));
        foreach (var (map, point) in rows.Points) _defensePoints[map] = point;
        foreach (var (guild, map) in rows.Stations) _station[guild] = map;
    }
}

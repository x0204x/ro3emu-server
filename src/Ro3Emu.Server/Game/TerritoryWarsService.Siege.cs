using Google.Protobuf;
using Romsg;
using Ro3Emu.Server.Accounts;
using Ro3Emu.Server.Data;

namespace Ro3Emu.Server.Game;

public sealed partial class TerritoryWarsService
{
    public const uint SiegeBaseQuery = 44402, SiegeHistoryQuery = 44403, SiegeResultNotify = 44404;
    public const uint SiegeToolAssign = 32282, SiegeToolExchange = 32283, SiegeToolList = 32284,
        SiegeToolPermissionNotify = 32285, SiegeToolChangeNotify = 32286;
    public const uint SiegeToolPrepay = 32270, SiegeToolBuild = 32271, SiegeToolCancel = 32272,
        SiegeToolAdd = 32275, SiegeVehicleUse = 32281;
    private const int SiegeActivity = 115;
    private bool? _siegeTestOpen;
    private long _siegeTestStart;
    private long _siegeTestClosedUntil;

    /// <summary>
    /// DailyActivityConfig[115] gives the local weekly occurrence (Wednesday 20:30-21:30 in the 7 Oct 2026 client, Saturday 21:30-22:30 in the 16 Oct one). The GM override is
    /// an ephemeral test aid (inferred), deliberately outside the durable Territory Wars state.
    /// </summary>
    private TimedTodoSchedule.Occurrence? SiegeWindow()
    {
        if (_siegeTestOpen == true)
        {
            if (_clock.Now < _siegeTestStart + 3600) return new(_siegeTestStart, _siegeTestStart + 3600);
            _siegeTestOpen = null;
        }
        if (_siegeTestOpen == false && _clock.Now >= _siegeTestClosedUntil) _siegeTestOpen = null;
        var scheduled = _siegeSchedule.CurrentOrNext(SiegeActivity);
        return _siegeTestOpen == false && scheduled?.Contains(_clock.Now) == true
            ? _siegeSchedule.CurrentOrNext(SiegeActivity, scheduled.End) : scheduled;
    }

    private SiegeCityActivityState SiegeActivityState(TimedTodoSchedule.Occurrence? window)
        => window?.Contains(_clock.Now) == true ? SiegeCityActivityState.Fighting : SiegeCityActivityState.NotOpen;

    /// <summary>
    /// (inferred) The sole exported Territory Wars root is SiegeConfig area 1. Its sorted three capitals
    /// correspond to that area's three senior cities. There is no ownership source for areas 2-10.
    /// </summary>
    private int SiegeZone(int city)
    {
        if (city == 1001) return Zones.Where(z => GameData.Int(z, "_iType") == 1)
            .Select(z => (int)Id(z)).Order().FirstOrDefault();
        if (city is >= 1101 and <= 1103) return Zones.Where(z => GameData.Int(z, "_iType") == 2 && RootOf((int)Id(z)) == SiegeZone(1001))
            .Select(z => (int)Id(z)).Order().Skip(city - 1101).FirstOrDefault();
        return 0;
    }

    private (long Attack, long Defense) SiegeGuilds(int city)
    {
        var window = SiegeWindow();
        if (window != null && state.SiegeRuns.TryGetValue(SiegeKey(city, window.Start), out var frozen))
            return (frozen.Attack, frozen.Defense);
        int zone = SiegeZone(city);
        if (zone == 0) return (0, 0);
        long defense = OwnerOf(zone);
        if (defense == 0 || _guildStore.Get(defense) == null) return (0, 0);
        int round = ZoneType(zone) == 1 ? 3 : 2;
        var battle = state.Battles.GetValueOrDefault(BattleKey(round, zone));
        // (inferred) A live, recorded Territory challenger with the highest observed score attacks;
        // a tie resolves by stable guild id. No test aid creates guild qualifications.
        long attack = QualifiedFor(zone, round).Concat(battle?.Guilds ?? [])
            .Distinct().Where(id => id != defense && _guildStore.Get(id) != null)
            .OrderByDescending(id => battle?.Scores.GetValueOrDefault(id) ?? 0).ThenBy(id => id).FirstOrDefault();
        return (attack, defense);
    }

    // No exported 44401 exists. 44402's nested SiegeBaseInfoNtf is the registered DAL update payload.
    private SiegeBaseInfoNtf SiegeBase(GameSession? viewer = null)
    {
        var window = SiegeWindow();
        var info = new SiegeBaseInfoNtf();
        foreach (var row in _data.Table("SiegeConfig").Values.OrderBy(r => GameData.Int(r, "_iID")))
        {
            int id = (int)GameData.Int(row, "_iID");
            var city = new SiegeCityBattleInfo
            {
                CityId = id,
                StartTime = window?.Start ?? 0,
                EndTime = window?.End ?? 0,
                ActivityState = SiegeActivityState(window),
                // The parser retains nil guild data as an empty object; explicit empty snapshots clear
                // the panel's built-in "Guild Master" text when a city has no paired guild (inferred).
                AttackGuildInfo = EmptySiegeGuild(), DefenseGuildInfo = EmptySiegeGuild(),
            };
            var (attack, defense) = SiegeGuilds(id);
            var run = window != null ? state.SiegeRuns.GetValueOrDefault(SiegeKey(id, window.Start)) : null;
            if (run != null)
            {
                city.AttackGuildInfo = GuildSnapInfo.Parser.ParseFrom(run.AttackSnapshot);
                city.DefenseGuildInfo = GuildSnapInfo.Parser.ParseFrom(run.DefenseSnapshot);
                if (run.Settled) { city.ActivityState = SiegeCityActivityState.End; city.EndTime = run.SettledAt; }
            }
            else
            {
                if (attack > 0 && _guildStore.Get(attack) is { } attacker) city.AttackGuildInfo = SiegeSnapshot(attacker);
                if (defense > 0 && _guildStore.Get(defense) is { } defender) city.DefenseGuildInfo = SiegeSnapshot(defender);
            }
            info.BattleInfos[id] = city;
        }
        if (viewer != null && Own(viewer) is { } guild)
            info.CurrentCityId = info.BattleInfos.Values.Where(c => c.AttackGuildInfo.Guid == guild.Id || c.DefenseGuildInfo.Guid == guild.Id)
                .Select(c => c.CityId).Order().FirstOrDefault();
        return info;
    }

    private static GuildSnapInfo EmptySiegeGuild()
    {
        // (inferred client decoder compatibility) The real Lua bridge omits empty repeated fields;
        // ParseGuildSnapInfo unconditionally iterates guildManagerData. These zero-identity entries
        // represent absence, not a real guild or officer.
        var guild = new GuildSnapInfo { Name = "", LeaderName = "", Icon = new GuildHeadIcon() };
        guild.HistoryNames.Add("");
        guild.GuildManagerData.Add(new GuildManagerInfo());
        return guild;
    }

    private GuildSnapInfo SiegeSnapshot(GuildStore.State guild)
    {
        var snap = _guilds.Snapshot(guild);
        // GuildPositionConfig[4] is Commander; the Siege panel has a distinct strategist label.
        snap.StrategistName = guild.Members.Values.FirstOrDefault(m => m.Official == 4)?.Name ?? "";
        return snap;
    }

    private Task SiegeBaseAsync(GameSession s, GetSiegeBaseInfoReq req)
    {
        int error = req.PlayerId is not 0 && req.PlayerId != s.Role!.Id || req.GuildId is not 0 && req.GuildId != (Own(s)?.Id ?? 0)
            ? ErrorCodes.NoPermission : 0;
        return Send(s, SiegeBaseQuery, new GetSiegeBaseInfoRsp { Result = error, SiegeBaseInfo = SiegeBase(s) });
    }

    private Task SiegeHistoryAsync(GameSession s, SiegeCityHistoryInfoReq req)
    {
        var ids = req.CityIds.Count > 0 ? req.CityIds.Distinct().ToArray() : req.CityId > 0 ? [req.CityId] : [];
        var response = new SiegeCityHistoryInfoResp { CityId = req.CityId };
        if (ids.Length == 0 || ids.Length > 40 || ids.Any(id => _data.Row("SiegeConfig", id) == null))
            response.Result = ErrorCodes.Parameter;
        else foreach (int id in ids)
            if (state.SiegeHistory.TryGetValue(id, out var history))
                response.Infos.AddRange(history.AsEnumerable().Reverse().Select(SiegeCityHistoryInfo.Parser.ParseFrom));
        if (response.Result == 0 && response.Infos.Count == 0)
            response.Result = ErrorCodes.DataNotFound; // (inferred) Avoid nil infos in no_decode_default_array Lua.
        return Send(s, SiegeHistoryQuery, response);
    }

    /// <summary>The panel's War Machine page queries 32284. No server ownership or inventory semantics
    /// for assignable machines are exported, so its list is empty and mutations refuse (inferred).</summary>
    private Task SiegeToolListAsync(GameSession s) => Send(s, SiegeToolList,
        new GetGuildWartimeToolBelongListRsp { Result = Own(s) == null ? ErrorCodes.TerritoryGuild : ErrorCodes.DataNotFound });

    private Task SiegeToolAssignAsync(GameSession s, GuildWartimeToolBelongOperReq req) => Send(s, SiegeToolAssign,
        new GuildWartimeToolBelongOperRsp
        {
            Result = Own(s) == null ? ErrorCodes.TerritoryGuild : ErrorCodes.FeatureUnavailable,
            ToolType = req.ToolType, BagIndex = req.BagIndex, RoleGuildId = req.RoleGuildId
        });

    private Task SiegeToolExchangeAsync(GameSession s, GuildWartimeToolExchangeOperReq req) => Send(s, SiegeToolExchange,
        new GuildWartimeToolExchangeOperRsp
        {
            Result = Own(s) == null ? ErrorCodes.TerritoryGuild : ErrorCodes.FeatureUnavailable,
            ToolType = req.ToolType, SrcBagIndex = req.SrcBagIndex, TargetBagIndex = req.TargetBagIndex
        });

    private async Task PublishSiegeIfChangedAsync()
    {
        var window = SiegeWindow();
        string token = $"{window?.Start}:{window?.End}:{SiegeActivityState(window)}"
            + $":{string.Join(',', state.Owners.OrderBy(x => x.Key).Select(x => $"{x.Key}={x.Value}"))}"
            + $":{string.Join(',', new[] { 1001, 1101, 1102, 1103 }.Select(id => $"{id}={SiegeGuilds(id)}"))}"
            + $":{string.Join(',', state.SiegeRuns.Values.Where(r => r.Start == window?.Start).OrderBy(r => r.City)
                .Select(r => $"{r.City}={r.Settled}:{r.SettledAt}"))}";
        // A realm change goes to everyone; a session whose guild changed (or that has not had the panel yet) gets
        // it alone. Each login used to change one shared token and resend the panel, with DB reads, to every
        // online role: O(N^2) during a login storm.
        bool realm = token != _siegePublishedState;
        _siegePublishedState = token;
        await RoleFanout.EachAsync(_sessions.All().Where(s => s.Role != null && s.TerritoryWars != null), async s =>
        {
            long guild = s.Guild?.GuildId ?? 0;
            var last = _siegePublishedGuild.GetValue(s, static _ => new(long.MinValue));
            if (!realm && last.Value == guild) return;
            try
            {
                // Guild qualification is recomputed by SyncSiegeAsync; the published role projection
                // already has the committed honor/fatigue values and needs no loop-side blob reload.
                await SyncSiegeAsync(s, committedRole: s.TerritoryWars);
                await Send(s, SiegeBaseQuery, new GetSiegeBaseInfoRsp { SiegeBaseInfo = SiegeBase(s) });
                last.Value = guild;
            }
            catch (Exception e) when (e is IOException or ObjectDisposedException or System.Net.Sockets.SocketException)
            { _log.LogDebug(e, "Siege panel delivery deferred for disconnected role {Role}", s.Role?.Id); }
        });
    }

    /// <summary>GM-only session clock override (inferred test aid); no ownership or rewards change.</summary>
    public async Task<string> SiegeGmAsync(string[] args)
    {
        bool seed = args.Length == 4 && args[0].Equals("seed", StringComparison.OrdinalIgnoreCase);
        if (!seed && (args.Length != 1 || args[0].ToLowerInvariant() is not ("open" or "close" or "state")))
            throw new ArgumentException("open|close|state|seed <city> <attackGuild> <defenseGuild>");
        long[] gmGuilds = seed ? [long.Parse(args[2]), long.Parse(args[3])] : [];
        using var reservation = await EnterGateAsync("territory/siege gm", additionalGuilds: gmGuilds,
            includeParticipants: true);
        using var stagedTerritoryState = StageState();
        try
        {
            switch (args[0].ToLowerInvariant())
            {
                case "seed":
                    // Load-test aid (not original): the city's zone is owned by the defender and the attacker is the
                    // recorded challenger of the zone's final round, the two facts SiegeGuilds reads. Persisted like
                    // a real outcome; only before an occurrence froze its guilds.
                    int city = int.Parse(args[1]), zone = SiegeZone(city);
                    long attack = long.Parse(args[2]), defense = long.Parse(args[3]);
                    if (zone == 0) throw new ArgumentException($"no siege zone for city {city}");
                    if (attack == defense || _guildStore.Get(attack) == null || _guildStore.Get(defense) == null)
                        throw new ArgumentException("two different existing guilds");
                    int round = ZoneType(zone) == 1 ? 3 : 2;
                    state.Owners[zone] = defense;
                    state.Battles[BattleKey(round, zone)] = new BattleState
                    {
                        Zone = zone, Round = round, Settled = true,
                        Winner = defense, Guilds = [attack, defense], Scores = new() { [attack] = 1 }
                    };
                    await SaveAsync(state);
                    break;
                case "open":
                    // Reuse an active natural or test occurrence. Opening mid-window must not
                    // mint a second run key, settlement or reward stream for the same city.
                    var activeWindow = SiegeWindow();
                    _siegeTestStart = activeWindow?.Contains(_clock.Now) == true ? activeWindow.Start : _clock.Now;
                    _siegeTestOpen = true;
                    break;
                case "close":
                    var closingWindow = SiegeWindow();
                    _siegeTestClosedUntil = closingWindow?.Contains(_clock.Now) == true ? closingWindow.End : _clock.Now + 1;
                    _siegeTestOpen = false;
                    foreach (var key in state.SiegeRuns.Where(x => !x.Value.Settled).Select(x => x.Key).ToArray())
                        await SiegeSettleAsync(key, state.SiegeRuns[key].Defense);
                    break;
            }
            await PublishSiegeIfChangedAsync();
            var window = SiegeWindow();
            return $"Siege TEST AID: {SiegeActivityState(window)}; window {window?.Start ?? 0}-{window?.End ?? 0}; cities {_data.Table("SiegeConfig").Count}. Override is in memory only.";
        }
        finally
        {
            stagedTerritoryState.Dispose(); _gate.Release(); reservation?.Dispose();
            await FlushActivityAsync();
        }
    }
}

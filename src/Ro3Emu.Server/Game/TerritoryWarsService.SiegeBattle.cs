using System.Text.Json;
using Google.Protobuf;
using Romsg;
using Ro3Emu.Server.Accounts.Persistence;
using Ro3Emu.Server.Data;
using Ro3Emu.Server.Maps;

namespace Ro3Emu.Server.Game;

public sealed partial class TerritoryWarsService
{
    private const long SiegeScene = 2230101;
    private const int SiegeCrystal = 4900009, SiegeCrystalPoint = 44;
    private bool _siegeDirty;

    /// <summary>After a committed siege settlement, per loaded participant: role, personal score, whether its guild
    /// won (quest target 106611). Listeners post their work.</summary>
    public List<Action<long, long, bool>> SiegeSettled { get; } = new();

    public sealed class SiegeRunState
    {
        public int City { get; set; }
        public long Start { get; set; }
        public long End { get; set; }
        public long Attack { get; set; }
        public long Defense { get; set; }
        public long StatisticsId { get; set; }
        public byte[] AttackSnapshot { get; set; } = [];
        public byte[] DefenseSnapshot { get; set; } = [];
        public long CrystalHp { get; set; }
        public long SettledAt { get; set; }
        public bool Settled { get; set; }
        public long Winner { get; set; }
        public Dictionary<long, Participant> Players { get; set; } = new();
        public byte[]? Result { get; set; }
    }

    public sealed class SiegeRun(TerritoryWarsService owner, string key, InstanceService.Room room)
    {
        public string Key { get; } = key;
        public string Epoch { get; } = owner.state.Epoch;
        public InstanceService.Room Room { get; } = room;
        public SiegeRunState State => owner._seasonCleanupState is { } retiring && retiring.Epoch == Epoch
            ? retiring.SiegeRuns.GetValueOrDefault(Key)
              ?? throw new InvalidOperationException("Retiring territory siege state is missing.")
            : owner.state.Epoch == Epoch && owner.state.SiegeRuns.TryGetValue(Key, out var current) ? current
              : throw new InvalidOperationException("Territory siege has no active or retiring epoch state.");
        public MonsterUnit? Crystal { get; set; }
        public bool Spawning { get; set; }
    }

    private static string SiegeKey(int city, long start) => $"{start}:{city}";
    private bool SiegeActive(SiegeRun battle) => !battle.State.Settled && _clock.Now >= battle.State.Start && _clock.Now < battle.State.End;
    private bool SiegeMember(GameSession s, SiegeRun battle) => s.Role != null && s.InstanceId == battle.Room.Id
        && !s.SceneLoading && battle.Room.ActiveMembers.Any(m => ReferenceEquals(m.Session, s))
        && battle.State.Players.TryGetValue(s.Role.Id, out var p) && OwnId(s) == p.Guild
        && p.Guild is > 0 && (p.Guild == battle.State.Attack || p.Guild == battle.State.Defense);

    /// <summary>30400 shares the normal instance ingress. The requested city is only a selector among
    /// server-derived pairings; client-supplied SiegeInitData cannot appoint either guild (inferred).</summary>
    public async Task<bool> SiegeEnterAsync(GameSession s, EnterDungeonReq req)
    {
        if (req.SceneEntry != SiegeScene) return false;
        using var reservation = await EnterGateAsync("territory/siege entry", s);
        using var stagedTerritoryState = StageState();
        try
        {
            await ReconcileLockedAsync();
            var window = SiegeWindow();
            if (window?.Contains(_clock.Now) != true)
            { await Send(s, 30400, new EnterDungeonRsp { Result = ErrorCodes.TerritoryWarClosed }); return true; }
            if (s.Role == null || s.Progress == null || s.Dead || s.SceneLoading || s.InstanceId != 0)
            { await Send(s, 30400, new EnterDungeonRsp { Result = ErrorCodes.Parameter }); return true; }
            if (!_functions.IsOpen(s, 67))
            { await Send(s, 30400, new EnterDungeonRsp { Result = ErrorCodes.FeatureUnavailable }); return true; }
            long guild = Own(s)?.Id ?? 0;
            if (guild == 0)
            { await Send(s, 30400, new EnterDungeonRsp { Result = ErrorCodes.TerritoryGuild }); return true; }
            int requested = req.InitData?.SiegeInitData?.CityId ?? 0;
            int[] eligible = _data.Table("SiegeConfig").Values.Select(r => (int)GameData.Int(r, "_iID"))
                .Where(id => { var (attack, defense) = SiegeGuilds(id); return guild == attack || guild == defense; })
                .Order().ToArray();
            int city = requested == 0 ? eligible.FirstOrDefault() : requested;
            if (!eligible.Contains(city))
            { await Send(s, 30400, new EnterDungeonRsp { Result = ErrorCodes.TerritoryNotQualified }); return true; }
            string key = SiegeKey(city, window.Start);
            if (!state.SiegeRuns.TryGetValue(key, out var record))
            {
                var pair = SiegeGuilds(city);
                if (pair.Attack == 0 || pair.Defense == 0)
                { await Send(s, 30400, new EnterDungeonRsp { Result = ErrorCodes.TerritoryNotQualified }); return true; }
                record = new SiegeRunState
                {
                    City = city, Start = window.Start, End = window.End,
                    Attack = pair.Attack, Defense = pair.Defense, StatisticsId = await AllocateStatisticsIdAsync(),
                    AttackSnapshot = SiegeSnapshot(_guildStore.Get(pair.Attack)!).ToByteArray(),
                    DefenseSnapshot = SiegeSnapshot(_guildStore.Get(pair.Defense)!).ToByteArray()
                };
                state.SiegeRuns[key] = record;
                await SaveAsync(state);
            }
            if (record.Settled || guild != record.Attack && guild != record.Defense
                || record.Players.TryGetValue(s.Role.Id, out var previousPlayer) && previousPlayer.Guild != guild)
            { await Send(s, 30400, new EnterDungeonRsp { Result = ErrorCodes.TerritoryNotQualified }); return true; }
            if (!_siegeBattles.TryGetValue(key, out var battle) || battle.Room.Closed)
                _siegeBattles[key] = battle = new SiegeRun(this, key, _instances.Create(SiegeScene, [s]));
            else if (_data.Row("DungeonConfig", SiegeScene) is { } dungeon && GameData.Int(dungeon, "_iRoleCntLimit") is > 0 and var limit
                && battle.Room.ActiveMembers.Count >= limit)
            { await Send(s, 30400, new EnterDungeonRsp { Result = ErrorCodes.Parameter }); return true; }
            else if (!_instances.AddParticipant(battle.Room, s))
            { await Send(s, 30400, new EnterDungeonRsp { Result = ErrorCodes.Parameter }); return true; }
            bool added = record.Players.TryAdd(s.Role.Id, new Participant
            {
                Id = s.Role.Id, Guild = guild,
                Name = s.Role.Name, Job = s.Progress.Career, Level = s.Progress.Level, Joined = _clock.Now
            });
            await SaveAsync(state);
            await Send(s, 30400, new EnterDungeonRsp());
            // DungeonConfig's default camp relation is client-side. The crystal is camp 2;
            // attackers use 1 and defenders 2 so opposite guilds can be targeted (inferred).
            s.EventFaction = guild == record.Attack ? 1 : 2;
            bool entered;
            try { entered = await _instances.EnterAsync(battle.Room, s, 0); }
            catch { s.EventFaction = 0; throw; }
            if (!entered)
            { s.EventFaction = 0; if (added) { record.Players.Remove(s.Role.Id); await SaveAsync(state); } return true; }
            s.SiegeBattle = battle;
            await SiegeSpawnCrystalAsync(battle);
        }
        finally { stagedTerritoryState.Dispose(); _gate.Release(); reservation?.Dispose(); }
        return true;
    }

    private async Task SiegeSpawnCrystalAsync(SiegeRun battle)
    {
        if (battle.Spawning || battle.Room.Closed || battle.State.Settled || battle.Crystal is { Dead: false }) return;
        battle.Spawning = true;
        try
        {
            var map = _maps.Map(SiegeScene);
            if (map == null || !map.Points.TryGetValue(SiegeCrystalPoint, out var point))
                throw new InvalidOperationException("Siege crystal point 44 is absent from scene 2230101");
            var actor = await _instances.SpawnAsync(battle.Room, SiegeCrystal, map.Grounded(point));
            if (actor == null) throw new InvalidOperationException("Configured Siege crystal 4900009 could not spawn");
            if (battle.State.CrystalHp > 0) actor.Hp = Math.Min(actor.Hp, battle.State.CrystalHp);
            battle.State.CrystalHp = actor.Hp;
            battle.Crystal = actor;
            await SaveAsync(state);
        }
        finally { battle.Spawning = false; }
    }

    private async Task SiegeLoadedAsync(GameSession s)
    {
        if (s.SiegeBattle is not { } battle || s.InstanceId != battle.Room.Id) return;
        if (!SiegeMember(s, battle)) { await _instances.LeaveAsync(s); return; }
        var p = battle.State.Players[s.Role!.Id];
        p.Loaded = true; await SaveAsync(state);
        await Send(s, 30401, SiegeBaseInfo(battle));
        // DungeonLua_2230101: point 73 defender, point 75 attacker.
        int born = p.Guild == battle.State.Defense ? 73 : 75;
        if (_maps.Map(SiegeScene)?.Points.GetValueOrDefault(born) is { } point)
            await _scene.TeleportInMapAsync(s, point);
    }

    private DungeonBaseInfoNotify SiegeBaseInfo(SiegeRun battle)
    {
        var r = battle.State;
        return new DungeonBaseInfoNotify
        {
            DungeonEntry = SiegeScene, DungeonId = battle.Room.Id,
            Stage = r.Settled ? 3 : 2, Level = 99,
            InitData = new DungeonInitData
            {
                SiegeInitData = new SiegeInitData
                {
                    CityId = r.City,
                    AttackGuildData = new SiegeGuildDungeonData { GuildId = r.Attack },
                    DefenseGuildData = new SiegeGuildDungeonData { GuildId = r.Defense }
                }, PlaybookId = _clock.PlayBook
            },
            StageModuleData = new DungeonStageModuleData
            {
                MapState = r.Settled ? 3 : 2, Stage = r.Settled ? 3 : 2,
                StageStartTime = r.Start * 1000, StageEndTime = r.End * 1000,
                MapStateStartTime = r.Start * 1000, MapStateEndTime = r.End * 1000
            },
            ChangeType = (DungeonBaseInfoNotify.Types.eChangeType)2
        };
    }

    private Task SiegeLeavingAsync(GameSession s)
    {
        if (s.SiegeBattle != null) s.EventFaction = 0;
        s.SiegeBattle = null;
        return Task.CompletedTask;
    }

    private bool SiegeHostile(GameSession a, GameSession b) => a.SiegeBattle is { } battle
        && ReferenceEquals(b.SiegeBattle, battle) && SiegeActive(battle) && SiegeMember(a, battle)
        && SiegeMember(b, battle) && OwnId(a) != OwnId(b);

    private bool SiegeCanHit(GameSession s, MonsterUnit m, SiegeRun battle)
        => m != battle.Crystal || SiegeActive(battle) && SiegeMember(s, battle) && OwnId(s) == battle.State.Attack;

    /// <summary>The configured crystal sits in a blocked navigation footprint at point 44. Preserve
    /// straight-line path checks up to its target cell, and allow only that terminal cell to be blocked
    /// for a live, authorized attacker (inferred siege objective geometry).</summary>
    private bool SiegeCrystalBlockedEndpointAllowed(GameSession s, MonsterUnit m, Vec3 origin, Vec3 target)
    {
        if (s.SiegeBattle is not { } battle || m != battle.Crystal || !SiegeActive(battle)
            || !SiegeMember(s, battle) || Own(s)?.Id != battle.State.Attack
            || m.InstanceId != battle.Room.Id || m.WorldScene != s.WorldScene
            || target.X != m.Pos.X || target.Y != m.Pos.Y || target.Z != m.Pos.Z
            || _maps.Map(SiegeScene)?.Nav is not { } nav || !nav.IsWalkable(origin.X, origin.Y, origin.Z)) return false;
        float dx = target.X - origin.X, dz = target.Z - origin.Z;
        float distance = MathF.Sqrt(dx * dx + dz * dz);
        if (distance <= 0) return false;
        int steps = (int)MathF.Ceiling(distance / 0.25f);
        float lastX = origin.X, lastZ = origin.Z;
        for (int i = 1; i <= steps; i++)
        {
            float t = (float)i / steps, x = origin.X + dx * t, z = origin.Z + dz * t;
            if (nav.IsWalkable(x, origin.Y, z)) { lastX = x; lastZ = z; continue; }
            return MathF.Floor(x) == MathF.Floor(target.X) && MathF.Floor(z) == MathF.Floor(target.Z)
                && distance * (1 - t) <= MathF.Sqrt(2)
                && nav.CanWalk(origin.X, origin.Y, origin.Z, lastX, lastZ);
        }
        return false;
    }

    private async Task SiegeDamageRecordedAsync(GameSession s, MonsterUnit m, long amount)
    {
        if (s.SiegeBattle is not { } battle || !SiegeActive(battle) || !SiegeMember(s, battle)
            || m.InstanceId != battle.Room.Id || s.Role == null) return;
        using var reservation = await EnterGateAsync("territory/siege damage", s);
        using var stagedTerritoryState = StageState();
        try
        {
            if (!ReferenceEquals(s.SiegeBattle, battle) || !SiegeActive(battle) || !SiegeMember(s, battle)) return;
            battle.State.Players[s.Role.Id].Damage += Math.Max(0, amount);
            if (m == battle.Crystal) battle.State.CrystalHp = m.Hp;
            await SaveAsync(state);
        }
        finally { stagedTerritoryState.Dispose(); _gate.Release(); reservation?.Dispose(); }
    }

    private async Task SiegeHealRecordedAsync(GameSession s, long amount)
    {
        if (s.SiegeBattle is not { } battle || !SiegeActive(battle) || !SiegeMember(s, battle)
            || s.Role == null) return;
        using var reservation = await EnterGateAsync("territory/siege heal", s);
        using var stagedTerritoryState = StageState();
        try
        {
            if (!ReferenceEquals(s.SiegeBattle, battle) || !SiegeActive(battle) || !SiegeMember(s, battle)) return;
            battle.State.Players[s.Role.Id].Healed += Math.Max(0, amount);
            await SaveAsync(state);
        }
        finally { stagedTerritoryState.Dispose(); _gate.Release(); reservation?.Dispose(); }
    }

    private async Task SiegePvpRecordedAsync(GameSession s, GameSession victim, long amount, bool lethal)
    {
        if (!SiegeHostile(s, victim)) return;
        using var reservation = await EnterGateAsync("territory/siege pvp", s);
        using var stagedTerritoryState = StageState();
        try
        {
            if (!SiegeHostile(s, victim)) return;
            var battle = s.SiegeBattle!;
            var attacker = battle.State.Players[s.Role!.Id]; var defender = battle.State.Players[victim.Role!.Id];
            attacker.Damage += Math.Max(0, amount); defender.Taken += Math.Max(0, amount);
            if (lethal) { attacker.Kills++; attacker.Score++; } // (inferred) one point per player kill.
            await SaveAsync(state);
        }
        finally { stagedTerritoryState.Dispose(); _gate.Release(); reservation?.Dispose(); }
    }

    private async Task SiegeKilledAsync(GameSession s, MonsterUnit m)
    {
        if (s.SiegeBattle is not { } battle || m != battle.Crystal || !SiegeActive(battle) || !SiegeMember(s, battle)) return;
        var role = s.Role;
        var attachment = s.Attachment;
        using var reservation = await EnterGateAsync("territory/siege crystal kill", s,
            includeParticipants: true);
        using var stagedTerritoryState = StageState();
        try
        {
            if (!ReferenceEquals(s.Role, role) || !ReferenceEquals(s.Attachment, attachment)
                || !SiegeActive(battle) || !SiegeMember(s, battle)) return;
            if (battle.State.Settled || Own(s)?.Id != battle.State.Attack) return;
            battle.State.Players[s.Role!.Id].Score++; // (inferred) objective credit; crystal kill decides victory.
            await SiegeSettleAsync(battle.Key, battle.State.Attack);
        }
        finally
        {
            stagedTerritoryState.Dispose(); _gate.Release(); reservation?.Dispose();
            await FlushActivityAsync();
        }
    }

    private async Task SiegeClockAsync()
    {
        foreach (var (key, run) in state.SiegeRuns.ToArray())
        {
            if (!run.Settled && _clock.Now >= run.End) await SiegeSettleAsync(key, run.Defense);
            var current = state.SiegeRuns[key];
            if (_siegeBattles.TryGetValue(key, out var battle) && !battle.Room.Closed)
            {
                await RoleFanout.EachAsync(battle.Room.ActiveMembers.Select(m => m.Session), async s =>
                {
                    if (s.SiegeBattle == battle && !s.SceneLoading && !SiegeMember(s, battle))
                        await _instances.LeaveAsync(s);
                });
                // The attacker client waits 2s for the crystal and 5s for the result panel;
                // keep the scene for a bounded presentation grace (inferred).
                if (current.Settled && _clock.Now >= current.SettledAt + 30)
                    await RoleFanout.EachAsync(battle.Room.ActiveMembers.Select(m => m.Session), async s =>
                    { if (s.SiegeBattle == battle && !s.SceneLoading) await _instances.LeaveAsync(s); });
                else if (!current.Settled) await SiegeSpawnCrystalAsync(battle);
            }
        }
        if (_siegeDirty) { await SaveAsync(state); _siegeDirty = false; }
    }

    /// <summary>Crystal destruction wins for attackers; otherwise the defender holds at the 60-minute deadline.
    /// Result, Territory ownership, role counters and once-only reward mails commit together (inferred settlement).</summary>
    private async Task SiegeSettleAsync(string key, long winner)
    {
        if (!state.SiegeRuns.TryGetValue(key, out var previous) || previous.Settled) return;
        var next = Copy(state); var run = next.SiegeRuns[key];
        if (winner != run.Attack && winner != run.Defense) throw new InvalidOperationException("Invalid Siege winner");
        run.Winner = winner; run.Settled = true; run.SettledAt = _clock.Now;
        int zone = SiegeZone(run.City);
        AssignTerritoryOwner(next, zone, winner);
        var result = new DungeonResultInfo
        {
            FirstResultType = 1, SecondResultType = 1, Result = 1,
            WinEntityId = winner, StatisticsId = run.StatisticsId, DungeonId = (int)SiegeScene,
            StatisticsTime = _clock.Now * 1000, DungeonStartTimestamp = run.Start * 1000
        };
        result.SceneStatistics[run.StatisticsId] = SiegeStatistics(run).SceneStatistics[run.StatisticsId];
        run.Result = result.ToByteArray();
        var outcome = new SiegeResultInfo
        {
            Result = winner == run.Attack ? 1 : 2, Time = _clock.Now,
            WinGuildInfo = GuildSnapInfo.Parser.ParseFrom(winner == run.Attack ? run.AttackSnapshot : run.DefenseSnapshot),
            LoseGuildInfo = GuildSnapInfo.Parser.ParseFrom(winner == run.Attack ? run.DefenseSnapshot : run.AttackSnapshot)
        };
        int week = (int)Math.Max(1, (run.Start - _clock.SeasonStart) / (7 * 86400) + 1); // (inferred) season-relative week.
        var history = new SiegeCityHistoryInfo
        {
            CityId = run.City, Week = week,
            GuildInfo = outcome.WinGuildInfo.Clone(), BattleInfo = new SiegeHistoryBattleInfo
            {
                ResultInfo = outcome, CommonResultInfo = result
            }
        };
        history.SiegeWinInfo[run.City] = 1;
        if (!next.SiegeHistory.TryGetValue(run.City, out var entries)) next.SiegeHistory[run.City] = entries = new();
        entries.Add(history.ToByteArray());
        if (entries.Count > 20) entries.RemoveRange(0, entries.Count - 20); // (inferred) bounded history.
        var rolesChanged = new Dictionary<long, RoleState>();
        foreach (var p in run.Players.Values.Where(p => p.Loaded))
        {
            var role = LoadRole(p.Id);
            role.Honor += p.Score; role.Valor += p.Kills; // (inferred) same counter units as Territory Wars.
            rolesChanged[p.Id] = role;
        }
        var recipients = run.Players.Values.Where(p => p.Loaded).ToArray();
        using (await _mail.LockRecipientsAsync(recipients.Select(p => p.Id)))
        {
            var transaction = _accounts.NewTransaction();
            Save(next, transaction);
            foreach (var (roleId, role) in rolesChanged) SaveRole(roleId, role, transaction);
            var mailAwards = new Dictionary<long, List<MailService.Attachment>>();
            foreach (var p in recipients)
            {
                bool win = p.Guild == winner;
                int template = (int)Param(win ? "SiegeWinMailId" : "SiegeLoseMailId", win ? 10500 : 10501);
                // SiegeConfig selects the city's player award; the royal city matches the
                // win/loss mail template awards, while senior cities select group 30093.
                int award = _data.Row("SiegeConfig", run.City) is { } row ? (int)GameData.Int(row,
                    win ? "_iWinPlayerAward" : "_iLosePlayerAward") : 0;
                var goods = SiegeMailAwards(award);
                mailAwards[p.Id] = goods;
                _mail.QueueTemplateLocked(p.Id, $"siege:{key}:{p.Id}", template, goods, transaction);
            }
            if (SiegeMailSource(mailAwards, $"territory-siege-settle:{key}") is { } reward)
                transaction.WithEventEconomy(reward);
            await _roles.CommitAsync(transaction, "territory/siege-settle");
        }
        PublishCommitted(next);
        foreach (var p in recipients)
            foreach (var handler in SiegeSettled)
                try { handler(p.Id, p.Score, p.Guild == winner); }
                catch (Exception e) { _log.LogError(e, "Siege settlement hook failed for role {Role}", p.Id); }
        foreach (var p in recipients)
            try { await _mail.NotifyRecipientAsync(p.Id); }
            catch (Exception e) { _log.LogWarning(e, "Siege mail notification deferred for role {Role}", p.Id); }
        var activeRoom = _siegeBattles.GetValueOrDefault(key);
        await RoleFanout.EachAsync(_sessions.All().Where(s => s.Role != null && run.Players.ContainsKey(s.Role.Id)), async s =>
        {
            s.TerritoryWars = LoadRole(s.Role!.Id);
            // 44404 drives the client's current dungeon settlement UI. A historical participant
            // keeps earned mail/counters/history, but cannot settle a different current scene.
            if (activeRoom == null || activeRoom.Room.Closed || !ReferenceEquals(s.SiegeBattle, activeRoom)
                || !run.Players[s.Role.Id].Loaded || !SiegeMember(s, activeRoom)) return;
            try { await Send(s, SiegeResultNotify, new SiegeDungeonResultNtf { Result = 0, CityId = run.City, History = history.BattleInfo }); }
            catch (Exception e) when (e is IOException or ObjectDisposedException or System.Net.Sockets.SocketException)
            { _log.LogDebug(e, "Siege result delivery deferred for disconnected role {Role}", s.Role?.Id); }
        });
        await RoleFanout.EachAsync(_sessions.All().Where(s => s.Role != null &&
            (run.Players.ContainsKey(s.Role.Id) || Own(s)?.Id == run.Attack || Own(s)?.Id == run.Defense)), async s =>
        {
            try { await SyncSiegeAsync(s); }
            catch (Exception e) when (e is IOException or ObjectDisposedException or System.Net.Sockets.SocketException)
            { _log.LogDebug(e, "Siege qualification delivery deferred for disconnected role {Role}", s.Role?.Id); }
        });
        // Siege transfers the same durable territory owner read by the Territory Wars map.
        await PublishAsync();
        await PublishSiegeIfChangedAsync();
    }

    internal async Task SettleSiegeForTestAsync(string key, long winner)
    {
        using var reservation = await EnterGateAsync("territory/siege fixture settlement", includeParticipants: true);
        using var stagedTerritoryState = StageState();
        try { await SiegeSettleAsync(key, winner); }
        finally { stagedTerritoryState.Dispose(); _gate.Release(); reservation?.Dispose(); }
        await FlushActivityAsync();
    }

    private List<MailService.Attachment> SiegeMailAwards(int groupId)
    {
        if (_data.Row("MultiverseGroupConfig", groupId) is not { } group || GameData.Int(group, "_itype") != 0)
            throw new InvalidOperationException($"Siege reward group {groupId} is unavailable");
        long id = GameData.Int(group, $"_iValue_{_clock.PlayBook}");
        if (id == 0) id = GameData.Int(group, "_iValue_default");
        if (_data.Row("AwardConfig", id) is not { } raw)
            throw new InvalidOperationException($"Siege reward {id} is unavailable");
        var row = raw.ValueKind == JsonValueKind.Array ? raw[0] : raw;
        if (!row.TryGetProperty("_kAward", out var fixedItems)
            || fixedItems.ValueKind != JsonValueKind.Array)
            throw new InvalidOperationException($"Siege reward {id} is unavailable");
        return fixedItems.EnumerateArray().Where(a => a.ValueKind == JsonValueKind.Array && a.GetArrayLength() >= 2)
            .Select(a => new MailService.Attachment((int)(GameData.Number(a[0]) ?? 0), (int)(GameData.Number(a[1]) ?? 0),
                a.GetArrayLength() > 2 ? (int)(GameData.Number(a[2]) ?? 0) : 0)).ToList();
    }

    private DungeonStatisticsInfoWithDungeon SiegeStatistics(SiegeRunState run)
    {
        var sceneStats = new SceneStatisticsInfo { SceneId = run.StatisticsId, DungeonId = (int)SiegeScene };
        foreach (var p in run.Players.Values)
        {
            var role = new RoleStatisticsInfo
            {
                BaseInfo = new StatisticsRoleBaseInfo
                {
                    Id = p.Id, Area = 1, Name = p.Name, Job = p.Job, GuildId = p.Guild,
                    RewardData = new StatisticsAwardCheckData { PlayerLevel = p.Level, Job = p.Job }
                },
                TotalDamage = p.Damage, TotalBeDamage = p.Taken, TotalCure = p.Healed, Totalscore = p.Score
            };
            // The client indexes extraData directly while decoding Siege results. These are the
            // schema's EStatisticsModuleEnum keys; zero means the unsupported action did not occur.
            role.ExtraData[1001001] = new ExtraValueData { LongValue = p.Damage }; // Damage
            role.ExtraData[1001002] = new ExtraValueData { LongValue = p.Taken }; // TakeDamage
            role.ExtraData[1001003] = new ExtraValueData { LongValue = p.Healed }; // heal
            role.ExtraData[1001004] = new ExtraValueData { LongValue = p.Kills }; // KillPlayer
            role.ExtraData[1001005] = new ExtraValueData(); // AssistKill
            role.ExtraData[1021001] = new ExtraValueData(); // AttackCityValue (no supported formula)
            role.ExtraData[1021002] = new ExtraValueData(); // DestroyMachinery (not implemented)
            role.ExtraData[1021003] = new ExtraValueData(); // MachineryDamage (not implemented)
            role.ExtraData[1021004] = new ExtraValueData(); // DefenseValue (no supported formula)
            role.ExtraData[1021006] = new ExtraValueData(); // TitleHistoryList
            role.ExtraData[1021007] = new ExtraValueData { LongValue = p.Score }; // CurrentScore
            role.ExtraData[1021008] = new ExtraValueData(); // TitleRank
            sceneStats.RoleStatistics[p.Id] = role;
        }
        foreach (var (id, bytes) in new[] { (run.Attack, run.AttackSnapshot), (run.Defense, run.DefenseSnapshot) })
        {
            var guild = GuildSnapInfo.Parser.ParseFrom(bytes);
            sceneStats.GuildStatistics[id] = new GuildStatisticsInfo
            {
                GuildId = id, GuildName = guild.Name,
                GuildIcon = guild.Icon?.Clone() ?? new GuildHeadIcon()
            };
        }
        return new DungeonStatisticsInfoWithDungeon
        {
            StatisticsId = run.StatisticsId, DungeonId = (int)SiegeScene,
            StatisticsTime = (run.Settled ? run.SettledAt : _clock.Now) * 1000,
            SceneStatistics = { [run.StatisticsId] = sceneStats }
        };
    }

    /// <summary>Shared report IDs for Siege players; 30106 cannot author a winner or objective event.</summary>
    public async Task<bool> SiegeSharedAsync(GameSession s, uint id, ReadOnlyMemory<byte> body)
    {
        if (s.Role == null) return false;
        if (id == 30114)
        {
            var req = DungeonStatisticsInfoWithDungeonReq.Parser.ParseFrom(body.Span);
            var records = state.SiegeRuns.Values.Where(r => req.StaisticsIds.Contains(r.StatisticsId)).ToArray();
            if (records.Length == 0) return false;
            var response = new DungeonStatisticsInfoWithDungeonResp();
            if (req.StaisticsIds.Count > 20 || records.Length != req.StaisticsIds.Distinct().Count()
                || records.Any(r => !r.Players.ContainsKey(s.Role.Id))) response.Result = ErrorCodes.NoPermission;
            else response.DungeonStatistics.AddRange(records.Select(SiegeStatistics));
            await Send(s, id, response); return true;
        }
        if (s.SiegeBattle is not { } battle) return false;
        if (id == 30106) return true;
        if (!SiegeMember(s, battle)) return false;
        var report = SiegeStatistics(battle.State);
        if (id == 30117) await Send(s, id, new GetDungeonStatisticsInDungeonResp { DungeonStatistics = report });
        else if (id == 30116)
        {
            var response = new DungeonStatisticsModuleRsp
            {
                StatisticsId = report.StatisticsId,
                DungeonId = report.DungeonId, StatisticsTime = report.StatisticsTime
            };
            response.SceneStatistics.Add(report.SceneStatistics); await Send(s, id, response);
        }
        else if (id == 30110)
        {
            var response = new DungeonStatisticsRes(); var map = new DungeonStatisticsInfo { DungeonMapId = SiegeScene };
            foreach (var p in battle.State.Players.Values)
                map.DungeonRolesInfo.Add(new DungeonRoleInfo
                {
                    RoleBase = new ObjScoreBaseInfo
                    {
                        Id = p.Id, Name = p.Name, Job = p.Job, Area = 1
                    }, TotalDamage = p.Damage,
                    TotalBeDamage = p.Taken, TotalCure = p.Healed, Totalscore = p.Score
                });
            response.DungeonRolesInfo[battle.State.StatisticsId] = map; await Send(s, id, response);
        }
        else return false;
        return true;
    }
}

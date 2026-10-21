using Google.Protobuf;
using Romsg;
using Ro3Emu.Server.Accounts;
using Ro3Emu.Server.Data;
using Ro3Emu.Server.Maps;

namespace Ro3Emu.Server.Game;

/// <summary>
/// Group EXP, levels and their rewards, rallies, the group's periods and the GM test aid.
/// <list type="bullet">
/// <item>Group EXP is Resource.adventGroupMoney: the Adventure Bond Points (item 130) a member receives (the group quests'
/// AwardConfig 100191100-100191103 grant 50/100) also count for the group, capped per week by
/// AdventureGroupMoneyWeeklyLimit (weekMoney; inferred: the cap is the group's). The level is the number of
/// AdventureGroupLevelAwardConfig._iNum thresholds reached + 1 (HomePanel:1056-1100 draws the bar between them).</item>
/// <item>Level rewards (34009 Ope_TakenLevelUpReward): once per role and level, recorded in the role's 5068
/// takenGroupRewardLevel (LevelRewardListWidget:191-209); the grant and the record commit as one owned award
/// ("cd13e/advguild-award").</item>
/// <item>Rallies (34017-34019): _iConveneNum per day by group level (HLP:2807-2857), AdventureGroupConveneCD seconds
/// between calls, an answer within AdventureGroupConveneTime seconds; DungeonConfig / MapConfig
/// _kAdventureGroupWhitelist [start, accept, -] (inferred meaning) limits where.</item>
/// </list>
/// </summary>
public sealed partial class AdventureGroupService
{
    private readonly Dictionary<long, (long Group, long Caller, long Scene, MapPoint Point, long Expires)> _rallies = new();
    private long _lastMaintenance;

    // ---------------------------------------------------------------- EXP and level

    private int LevelFor(long money)
    {
        var levels = Levels();
        int level = levels.Count == 0 ? 1 : levels.Keys.First();
        foreach (var (lv, row) in levels)
            if (money >= row.Num && levels.ContainsKey(lv + 1)) level = lv + 1;
        return level;
    }

    /// <summary>Adds group EXP (weekly cap), levels up, publishes. Runs posted under the Realm reservation.</summary>
    internal async Task<long> AddExpAsync(long roleId, long amount)
    {
        if (_store.ForRole(roleId) is not { Status: StatusNormal } g || amount <= 0) return 0;
        Roll(g);
        long gained = Math.Min(amount, Math.Max(0, WeeklyExpLimit - g.WeekMoney));
        if (gained <= 0) return 0;
        g.WeekMoney += (int)gained;
        g.Money += gained;
        int before = g.Level, after = Math.Max(before, LevelFor(g.Money));
        for (int level = before + 1; level <= after; level++) AddEvent(g, AdventureGroupEventType.EventTypeLevelUp, level);
        g.Level = after;
        if (!await SaveAsync(g)) return 0;
        await BroadcastBaseAsync(g);
        if (after != before)
        {
            _log.LogInformation("Adventure group {Id} {Name} reaches level {Level}", g.Id, g.Name, after);
            foreach (var member in OnlineMembers(g)) await SyncRelationAsync(member, g);
        }
        return gained;
    }

    private async Task<int> ClaimLevelRewardAsync(GameSession s, int level)
    {
        var g = Own(s);
        if (g == null || g.Status != StatusNormal) return ErrNotInGroup;
        var levels = Levels();
        if (!levels.TryGetValue(level, out var row)) return ErrorCodes.Parameter;
        if (g.Level < level) return ErrLevel;
        var personal = Personal(s);
        if (personal.TakenGroupRewardLevel.Contains(level)) return ErrClaimed;
        var next = personal.Clone();
        next.TakenGroupRewardLevel.Add(level);
        long role = s.Role!.Id;
        AwardPlan plan;
        try
        {
            int? playBook = s.Synced.GetValueOrDefault(SeasonService.SeasonSync) is DBSeasonInfo { Current: { } current }
                ? (int)current.PlayBookId : null;
            plan = _awards.Resolve(s, row.Award, playBook);
        }
        catch (AwardResolutionException e)
        {
            _log.LogWarning("Adventure group level {Level} reward unresolved for {Role}: {Reason}", level, role, e.Message);
            return ErrorCodes.Unknown;
        }
        byte[] bytes = next.ToByteArray();
        bool ok;
        try
        {
            ok = plan.Grants.Count == 0
                ? await SaveClaimOnlyAsync(s, next)
                : await _character.ApplyAwardAsync(s, plan,
                    persist: tx => _roles.SaveAdventureGroup(role, bytes, tx),
                    committed: () => s.AdventureGroupRole = next,
                    ownedAward: new CharacterService.OwnedAwardSource("cd13e/advguild-award", $"advguild-level/{role}/{level}",
                        new { Version = 1, Group = g.Id, Level = level, Award = row.Award, plan.AwardId }));
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            _log.LogError(e, "Adventure group level {Level} reward failed for {Role}", level, role);
            return ErrorCodes.Unknown;
        }
        if (!ok) return ErrorCodes.Unknown;
        await SyncRelationAsync(s, g);
        _log.LogInformation("{Name} claims adventure group level {Level} reward", s.Role!.Name, level);
        return 0;
    }

    private async Task<bool> SaveClaimOnlyAsync(GameSession s, DBAdventureGroupRelationData next)
    {
        await SavePersonalAsync(s, next);
        return true;
    }

    // ---------------------------------------------------------------- rallies

    /// <summary>DungeonConfig (instances) or MapConfig _kAdventureGroupWhitelist[index] == 1.</summary>
    private bool Whitelisted(GameSession s, int index)
    {
        var row = (s.InstanceId != 0 ? _data.Row("DungeonConfig", s.SceneEntry) : null) ?? _data.Row("MapConfig", s.SceneEntry);
        if (row is not { } r || !r.TryGetProperty("_kAdventureGroupWhitelist", out var list)
            || list.ValueKind != System.Text.Json.JsonValueKind.Array || list.GetArrayLength() <= index) return false;
        return GameData.Number(list[index]) == 1;
    }

    public async Task ConveneAsync(GameSession s)
    {
        var g = Own(s);
        long self = s.Role!.Id;
        int code = g == null || g.Status != StatusNormal ? ErrNotInGroup : 0;
        if (code == 0)
        {
            Roll(g!);
            int cap = Levels().TryGetValue(g!.Level, out var row) && row.Convene > 0 ? row.Convene : 3;
            code = g.ConveneUseTime >= cap ? ErrNoRally
                : g.LastConveneTime + Number("AdventureGroupConveneCD", 10) > Now ? ErrRallyCooldown
                : !Whitelisted(s, 0) ? ErrRallyStartArea
                : (s.StatusBits & (1L << 15)) != 0 ? ErrRallyBattle : 0;
        }
        if (code == 0)
        {
            g!.ConveneUseTime++;
            g.LastConveneTime = Now;
            if (!await SaveAsync(g)) code = ErrorCodes.Unknown;
        }
        await s.SendGameAsync(Convene, new AdventureGroupConveneRes { Result = code, UseConveneTimes = g?.ConveneUseTime ?? 0 });
        if (code != 0) return;
        long token = NewToken();
        _rallies[token] = (g!.Id, self, s.SceneEntry, new MapPoint(s.Position.X, s.Position.Y, s.Position.Z, s.Face),
            Now + Number("AdventureGroupConveneTime", 30));
        await BroadcastBaseAsync(g);
        foreach (var member in OnlineMembers(g).Where(m => m.Role!.Id != self))
            await member.SendGameAsync(ConveneNotify, new AdventureGroupConveneNtf { PlayerId = self, Token = token });
    }

    public async Task ConfirmConveneAsync(GameSession s, AdventureGroupConfirmConveneReq req)
    {
        var g = Own(s);
        int code = !_rallies.TryGetValue(req.Token, out var rally) || g == null || rally.Group != g.Id ? ErrNoInvite
            : rally.Expires < Now ? ErrInviteExpired : 0;
        if (code == 0 && req.IsAgree)
            code = (s.StatusBits & (1L << 15)) != 0 ? ErrRallyBattle
                : s.SceneLoading || s.InstanceId != 0 || !Whitelisted(s, 1) ? ErrRallyAcceptArea
                : rally.Caller == s.Role!.Id ? ErrorCodes.Parameter : 0;
        await s.SendGameAsync(ConfirmConvene, new AdventureGroupConfirmConveneRes { Result = code });
        if (code != 0 || !req.IsAgree) return;
        if (rally.Scene == s.SceneEntry) await _scene.TeleportInMapAsync(s, rally.Point);
        else await _scene.EnterSceneAsync(s, rally.Scene, rally.Point);
    }

    // ---------------------------------------------------------------- periods

    /// <summary>Once a minute: support groups whose AdventureGroupHelperCreateTimeLimit passed without a running ritual
    /// disband (260030), stale invitations and rallies are dropped.</summary>
    public void Tick(GameTime time)
    {
        long now = Now;
        if (now - _lastMaintenance < 60) return;
        _lastMaintenance = now;
        foreach (var (token, invite) in _invites.ToArray()) if (invite.Expires < now) _invites.Remove(token);
        foreach (var (token, rally) in _rallies.ToArray()) if (rally.Expires < now) _rallies.Remove(token);
        foreach (var (id, ritual) in _rituals.ToArray()) if (ritual.Room.Closed && ritual.Step < 3) _rituals.Remove(id);
        _loop.PostLegacy(ExpireSupportGroupsAsync);
    }

    private async Task ExpireSupportGroupsAsync()
    {
        foreach (var g in _store.List().Where(g => g.Status == StatusHelper && !HelperLive(g) && RitualOf(g.Id) == null))
            await DisbandAsync(g);
    }

    // ---------------------------------------------------------------- GM

    /// <summary>
    /// "!advguild create &lt;name&gt;" founds a normal group with the caller as owner and sapling 200032 (no party, no ritual:
    /// a solo demo of the group panel, tree and tasks); "add &lt;role&gt;" puts another role (online or not) into the
    /// caller's group; "exp &lt;n&gt;" adds group EXP past the weekly cap; "water" resets the caller's daily watering;
    /// "info"; "disband".
    /// </summary>
    private async Task GmAsync(GameSession s, string[] args)
    {
        string op = args.Length > 0 ? args[0].ToLowerInvariant() : "info";
        long self = s.Role!.Id;
        string reply;
        switch (op)
        {
            case "create" when args.Length > 1:
                {
                    string name = string.Join(' ', args.Skip(1));
                    if (Own(s) != null) { reply = "Already in an adventure group."; break; }
                    if (_store.NameExists(name)) { reply = "Name taken."; break; }
                    var g = new AdventureGroupStore.State
                    {
                        Name = name, Status = StatusNormal, CreateType = 2, CreateTime = Now, Owner = self, Level = 1,
                        SaplingId = SaplingNpcs[0], DayResetTime = DayStart, WeekResetTime = WeekStart,
                    };
                    g.Members[self] = NewMember(self);
                    AddEvent(g, AdventureGroupEventType.EventTypeCreate, self, 0, name);
                    if (!await SaveAsync(g)) { reply = "Create failed (name or membership conflict)."; break; }
                    await SendFullAsync(s, g, joined: true);
                    await SyncRelationAsync(s, g);
                    await RefreshGroupQuestsAsync(s);
                    reply = $"Adventure group {g.Id} '{name}' founded (normal, sapling {g.SaplingId}).";
                    break;
                }
            case "add" when args.Length > 1:
                {
                    var g = Own(s);
                    var target = await _roles.FindByNameAsync(string.Join(' ', args.Skip(1)));
                    if (g == null) { reply = "Not in an adventure group."; break; }
                    if (target == null) { reply = "No such role."; break; }
                    if (_store.ForRole(target.Id) != null) { reply = "That role is already in a group."; break; }
                    if (g.Members.Count >= MemberLimit) { reply = "Group full."; break; }
                    reply = await AdmitAsync(g, target.Id) ? $"{target.Name} joined {g.Name}." : "Add failed.";
                    break;
                }
            case "exp" when args.Length > 1 && long.TryParse(args[1], out long amount):
                {
                    var g = Own(s);
                    if (g is not { Status: StatusNormal }) { reply = "Not in a normal adventure group."; break; }
                    g.WeekMoney = 0;
                    g.WeekResetTime = WeekStart;
                    if (!await SaveAsync(g)) { reply = "Save failed."; break; }
                    long gained = 0;
                    for (long left = amount; left > 0;)
                    {
                        long step = Math.Min(left, WeeklyExpLimit);
                        long got = await AddExpAsync(self, step);
                        if (got <= 0) break;
                        gained += got;
                        left -= step;
                        if (left > 0 && _store.ForRole(self) is { } again)
                        {
                            again.WeekMoney = 0;
                            await SaveAsync(again);
                        }
                    }
                    var after = Own(s)!;
                    reply = $"+{gained} group EXP: {after.Money}, level {after.Level}.";
                    break;
                }
            case "water":
                {
                    var g = Own(s);
                    if (g == null) { reply = "Not in an adventure group."; break; }
                    g.Members[self].WateringCount = 0;
                    reply = await SaveAsync(g) ? "Today's watering reset." : "Save failed.";
                    if (g.Members.TryGetValue(self, out var m)) await BroadcastMemberAsync(g, m, ChangeType.ChangePlayerChange);
                    break;
                }
            case "disband":
                {
                    var g = Own(s);
                    reply = g == null ? "Not in an adventure group." : await DisbandAsync(g) ? "Disbanded." : "Disband failed.";
                    break;
                }
            default:
                {
                    var g = Own(s);
                    reply = g == null ? "Not in an adventure group." :
                        $"{g.Id} '{g.Name}' status {g.Status} level {g.Level} exp {g.Money} (week {g.WeekMoney}) members {g.Members.Count} " +
                        $"sapling {g.SaplingId} watered {g.SaplingWatering} ritual step {RitualOf(g.Id)?.Step.ToString() ?? "-"}";
                    break;
                }
        }
        await GmCommands.ReplyAsync(s, reply);
    }

}

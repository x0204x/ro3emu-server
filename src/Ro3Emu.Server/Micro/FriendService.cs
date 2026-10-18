using Google.Protobuf;
using Ro3Emu.Server.Accounts;
using Ro3Emu.Server.Accounts.Persistence;
using Ro3Emu.Server.Game;
using Ro3Emu.Server.Game.Ownership;

namespace Ro3Emu.Server.Micro;

/// <summary>
/// Friends (Logic/Friend/LC_FriendServer.lua): micro calls with string-keyed args and string uids,
/// answered by method name; changes reach the other player as MsPush (onFriendApply / onFriendAccept / onFriendRemove /
/// onBeBlackListChange / onFriendSyncStatus). Errors are the client's TID_MS_FRIEND_* strings in Ret[2].
/// </summary>
public sealed class FriendService(AccountStore accounts, SocialOwnerCache cache, OwnerRegistry owners, GameLoop loop,
    SessionRegistry sessions, MicroSessions micro, ILogger<FriendService> log) : IMicroService
{
    /// <summary>PublicParamConfig FriendsMaxNum / FriendsRequestSaveNumMaxNum (the client only displays them).</summary>
    private const int MaxFriends = 500, MaxApplicants = 50;

    public string Name => "FriendService";

    /// <summary>A role's friend list or a friend's affection changed (accepted friendship, gift intimacy); achievements
    /// 106016/106017 re-read the relation. Called after the owner keys are released; listeners post their work.</summary>
    public List<Action<long>> RelationChanged { get; } = new();

    private void NotifyRelation(long role)
    {
        foreach (var handler in RelationChanged)
            try { handler(role); }
            catch (Exception e) { log.LogError(e, "Friend relation hook failed for {Role}", role); }
    }

    public async ValueTask<object?> InvokeAsync(MicroSession session, string method, object? args) =>
        await loop.InvokeAsync(() => InvokeOnLoopAsync(session, method, args));

    /// <summary>
    /// The caller's own role: the selected role of the game session bound to the micro session's verified account
    /// (LoginService.verifySdk). Null before verifySdk or while no role is selected. Read on the loop.
    /// </summary>
    private long? CallerRole(MicroSession? session)
    {
        string? account = session?.Account;
        if (string.IsNullOrEmpty(account)) return null;
        return sessions.All().FirstOrDefault(s => s.Role != null
            && string.Equals(s.Account, account, StringComparison.Ordinal))?.Role!.Id;
    }

    private async Task<object?> InvokeOnLoopAsync(MicroSession session, string method, object? args)
    {
        if (method is "queryNotification" or "readNotification" or "closeChat") return Ok(new());
        bool fetch = method is "fetchFriendsInfo" or "fetchApplicants" or "fetchBlacklist" or "fetchBeBlacklist";
        if (!fetch && method is not ("apply" or "accept" or "acceptAll" or "refuse" or "refuseAll" or "refuseBatch"
            or "remove" or "addBlacklist" or "delBlacklist")) return null;

        // Every friend list belongs to the caller's selected role. The client still sends its own role id as Uid
        // (LC_FriendServer.lua: tostring(GetSelfGuid())); it must match and never chooses whose lists are read or changed.
        if (CallerRole(session) is not long self) return MicroServiceRouter.Fail("no selected role");
        if (Uid(args, "Uid") != self)
        {
            log.LogWarning("Friend {Method} refused: Uid {Uid} is not the caller's role {Role}", method, Uid(args, "Uid"), self);
            return MicroServiceRouter.Fail("role mismatch");
        }

        if (fetch)
        {
            using var scope = await owners.EnterAsync("micro/friend/read", [ReservationKey.Role(self)]);
            if (CallerRole(session) != self) return MicroServiceRouter.Fail("no selected role");
            var read = await cache.LoadSocialAsync([self]);
            var value = read[self];
            // Legacy fetches also expose surviving relation rows for a soft-deleted Uid (now only the caller's own
            // selected role, so this is a defensive path for a role deleted while selected).
            // Keep that projection read-only; the tombstone must never enter OwnerRegistry.
            var tombstone = value is null ? await cache.TombstoneRowsAsync(self) : null;
            long[] friends = value?.Friends ?? tombstone?.Friends ?? [],
                applicants = value?.Applicants ?? tombstone?.Applicants ?? [],
                blacklist = value?.Blacklist ?? tombstone?.Blacklist ?? [],
                blacklistedBy = value?.BlacklistedBy ?? tombstone?.BlacklistedBy ?? [];
            // Every key stays present: a nil key skips the client's Replace and leaves stale lists.
            return method switch
            {
                "fetchFriendsInfo" => Ok(new()
                {
                    ["Friends"] = UidRows(friends), ["Blacklist"] = UidRows(blacklist),
                    ["BeBlacklist"] = blacklistedBy.Select(f => (object)f.ToString()).ToArray(),
                    ["RelationData"] = friends.Select(f => (object)new Dictionary<string, object?>
                    {
                        ["Uid"] = f.ToString(), ["DataMap"] = new Dictionary<string, object?>
                            { ["intimacy"] = value?.Intimacy[f] ?? tombstone!.Intimacy[f] },
                    }).ToArray(),
                }),
                "fetchApplicants" => Ok(new() { ["Applicants"] = UidRows(applicants) }),
                "fetchBlacklist" => Ok(new() { ["Blacklist"] = UidRows(blacklist) }),
                _ => Ok(new() { ["BeBlacklist"] = blacklistedBy.Select(f => (object)f.ToString()).ToArray() }),
            };
        }

        // Discover the complete participant set before admission. A missing or changed projection restarts
        // without retaining a provisional reservation; no owner is looked up through an online session.
        while (true)
        {
            var first = await cache.LoadSocialAsync([self]);
            var mine = first[self];
            if (mine is null) throw new OwnerRevisionConflictException();
            long other = method switch
            {
                "apply" or "remove" or "addBlacklist" or "delBlacklist" => Uid(args, "Target"),
                "accept" or "refuse" => Uid(args, "Applicant"),
                _ => 0,
            };
            long[] participants = (method is "acceptAll" or "refuseAll" or "refuseBatch"
                    ? new[] { self }.Concat(mine.Applicants) : new[] { self, other })
                .Where(id => id > 0).Distinct().ToArray();
            var loaded = await cache.LoadSocialAsync(participants);
            var notices = new List<Push>();
            try
            {
                using (var scope = await owners.EnterAsync("micro/friend/" + method,
                    participants.Select(id => ReservationKey.Role(id)).ToArray()))
                {
                    // The role may have been deselected while the participant set was staged.
                    if (CallerRole(session) != self) return MicroServiceRouter.Fail("no selected role");
                    if (participants.Any(id => loaded[id] is not null && cache.Social(id) is null) ||
                        (method is "acceptAll" or "refuseAll" or "refuseBatch" &&
                        !mine.Applicants.SequenceEqual(cache.Social(self)!.Applicants)))
                        continue;
                    var snapshots = participants.ToDictionary(id => id, id => cache.Social(id));
                    mine = snapshots[self]!;
                    if (method is "acceptAll" or "refuseAll" or "refuseBatch")
                        return await BulkAsync(method, self, mine, snapshots, notices);
                    var result = await StageCommitAsync(method, self, other, mine, snapshots);
                    notices.AddRange(result.Pushes);
                    return result.Reply;
                }
            }
            finally
            {
                // A later bulk candidate may fail after earlier candidates committed. Deliver their
                // queued notices after releasing every owner key, including on that failure path.
                foreach (var push in notices) await PushAsync(push.Role, push.Method, self.ToString());
                foreach (var push in notices.Where(p => p.Method == "onFriendAccept")) { NotifyRelation(push.Role); NotifyRelation(self); }
            }
        }
    }

    private sealed record Push(long Role, string Method);
    private sealed record Outcome(object Reply, Push[] Pushes);
    private static object[] UidRows(IEnumerable<long> roles) => roles.Select(id => (object)new Dictionary<string, object?>
        { ["uid"] = id.ToString() }).ToArray();

    private async Task<Outcome> StageCommitAsync(string method, long self, long other, SocialSnapshot mine,
        IReadOnlyDictionary<long, SocialSnapshot?> snapshots)
    {
        SocialSnapshot? target = snapshots.GetValueOrDefault(other);
        string? error = method switch
        {
            "apply" => other == self || target is null ? "TID_NO_DOCUMENT"
                : target.Blacklist.Contains(self) || mine.Blacklist.Contains(other) ? "TID_MS_FRIEND_BLACKLIST_TARGET"
                : mine.Friends.Contains(other) ? "TID_MS_FRIEND_APPLY_ALREADY_IN_FRIENDS"
                : target.Applicants.Contains(self) ? "TID_MS_FRIEND_APPLY_ALREADY_IN_APPLICATIONS"
                : mine.Friends.Length >= MaxFriends ? "TID_MS_FRIEND_APPLY_FRIEND_FULL"
                : target.Applicants.Length >= MaxApplicants ? "TID_MS_FRIEND_APPLY_APPLICANT_FULL" : null,
            "accept" => !mine.Applicants.Contains(other) ? "TID_MS_FRIEND_ACCEPT_APPLICANT_NOT_IN"
                : mine.Friends.Length >= MaxFriends ? "TID_MS_FRIEND_ACCEPT_FRIEND_FULL_SELF"
                : target is { Friends.Length: >= MaxFriends } ? "TID_MS_FRIEND_ACCEPT_FRIEND_FULL_OTHER" : null,
            _ => null,
        };
        if (method is "apply" or "accept")
            log.LogInformation("Friend {Method} {Self} / {Other}: {Result}", method, self, other, error ?? "ok");
        if (error is not null) return new(MicroServiceRouter.Fail(error), []);
        if (target is null) throw new OwnerRevisionConflictException();

        var writes = new List<FriendWritePlan>();
        long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        switch (method)
        {
            case "apply": writes.Add(new(FriendWriteKind.Apply, other, self, now)); break;
            case "accept": writes.Add(new(FriendWriteKind.Link, self, other, now)); break;
            case "refuse": writes.Add(new(FriendWriteKind.RemoveApplication, self, other)); break;
            case "remove": writes.Add(new(FriendWriteKind.Unlink, self, other)); break;
            case "addBlacklist": writes.Add(new(FriendWriteKind.Block, self, other)); break;
            case "delBlacklist": writes.Add(new(FriendWriteKind.Unblock, self, other)); break;
        }
        if (writes.Count != 0)
        {
            var plan = new RoleItemPlanBuilder(accounts);
            foreach (var snapshot in snapshots.Values.OfType<SocialSnapshot>()) plan.ExpectRole(snapshot.Vector);
            foreach (var write in writes) plan.Add(new SocialFriendWritePlan(write, snapshots.Keys));
            await plan.CommitAsync(accounts, "micro/friend/" + method,
                receipts => cache.ApplyRelation(snapshots, (byte[])receipts[0]!));
        }
        object reply = method switch
        {
            "accept" or "refuse" => Ok(new() { ["Applicant"] = other.ToString() }),
            "remove" or "addBlacklist" or "delBlacklist" => Ok(new() { ["Target"] = other.ToString() }),
            _ => Ok(new()),
        };
        Push[] pushes = method switch
        {
            "apply" => [new(other, "onFriendApply")],
            "accept" => [new(other, "onFriendAccept")],
            "remove" => [new(other, "onFriendRemove")],
            "addBlacklist" or "delBlacklist" => [new(other, "onBeBlackListChange")],
            _ => [],
        };
        return new(reply, pushes);
    }

    private async Task<object> BulkAsync(string method, long self, SocialSnapshot mine,
        IReadOnlyDictionary<long, SocialSnapshot?> snapshots, List<Push> notices)
    {
        var accepted = new List<object>();
        foreach (long applicant in mine.Applicants)
        {
            mine = cache.Social(self)!;
            if (method == "acceptAll" && mine.Friends.Length >= MaxFriends) break;
            if (snapshots[applicant] is null) throw new OwnerRevisionConflictException();
            var plan = new RoleItemPlanBuilder(accounts);
            plan.ExpectRole(mine.Vector);
            var peer = cache.Social(applicant)!;
            plan.ExpectRole(peer.Vector);
            var published = new Dictionary<long, SocialSnapshot?> { [self] = mine, [applicant] = peer };
            FriendWritePlan write = method == "acceptAll"
                ? new FriendWritePlan(FriendWriteKind.Link, self, applicant, DateTimeOffset.UtcNow.ToUnixTimeSeconds())
                : new FriendWritePlan(FriendWriteKind.RemoveApplication, self, applicant);
            plan.Add(new SocialFriendWritePlan(write, published.Keys));
            await plan.CommitAsync(accounts, "micro/friend/" + method,
                receipts => cache.ApplyRelation(published, (byte[])receipts[0]!));
            if (method == "acceptAll")
            {
                notices.Add(new(applicant, "onFriendAccept"));
                accepted.Add(applicant.ToString());
            }
        }
        return method == "acceptAll" ? Ok(new() { ["Candidates"] = accepted.ToArray() }) : Ok(new());
    }

    /// <summary>onFriendSyncStatus to the role's online friends when it logs in or out (tips 23610 / 23679).</summary>
    public async Task OnlineChangedAsync(long role, bool online)
    {
        var status = new Dictionary<string, object?> { ["Uid"] = role.ToString(), ["Status"] = online ? "online" : "offline" };
        var snapshots = await cache.LoadSocialAsync([role]);
        foreach (var friend in snapshots[role]?.Friends ?? []) await PushAsync(friend, "onFriendSyncStatus", status);
    }

    private async Task PushAsync(long role, string method, object? arg)
    {
        var account = sessions.All().FirstOrDefault(s => s.Role?.Id == role)?.Account;
        if (account != null) await micro.PushAsync(account, Name, method, arg);
    }

    /// <summary>LC_DAL_Friend:1757-1799 updates displayed intimacy from onRelationDataChange.</summary>
    public Task IntimacyChangedAsync(long role, long friend, int intimacy)
    {
        NotifyRelation(role);
        return IntimacyPushAsync(role, friend, intimacy);
    }

    private Task IntimacyPushAsync(long role, long friend, int intimacy) => PushAsync(role, "onRelationDataChange",
        new Dictionary<string, object?>
        {
            ["Uid"] = friend.ToString(), ["Data"] = new Dictionary<string, object?> { ["intimacy"] = intimacy },
        });

    private static Dictionary<string, object?> Ok(Dictionary<string, object?> body)
    {
        body["Ret"] = MicroServiceRouter.Ret(true);
        return body;
    }

    /// <summary>A uid argument: the client sends tostring(uid); numbers are accepted too.</summary>
    internal static long Uid(object? args, string key) =>
        args is IDictionary<object, object?> map && map.TryGetValue(key, out var v) && v != null && long.TryParse(Convert.ToString(v, System.Globalization.CultureInfo.InvariantCulture), out long id) ? id : 0;
}

/// <summary>NameService.getUidByName {Type:"Area0", Name} -> {Uid}; a numeric name is taken as a uid (role ids are 6 digits, the search box sends ≤ 7 characters here).</summary>
public sealed class NameService(RoleStore roles) : IMicroService
{
    public string Name => "NameService";

    public async ValueTask<object?> InvokeAsync(MicroSession session, string method, object? args)
    {
        if (method != "getUidByName") return null;
        string name = (MicroServiceRouter.MapString(args, "Name") ?? "").Trim();
        Role? role = await roles.FindByNameAsync(name) ?? (long.TryParse(name, out long id) ? await roles.GetAsync(id) : null);
        return role == null
            ? MicroServiceRouter.Fail("TID_NO_DOCUMENT")
            : new Dictionary<string, object?> { ["Ret"] = MicroServiceRouter.Ret(true), ["Uid"] = role.Id.ToString() };
    }
}

/// <summary>
/// PartyService: only the party search directory; membership is romsg 42000-42052 on the game gate.
/// Empty lists keep the "Search Party" panel open without "not implemented" errors.
/// </summary>
public sealed class PartyService(TeamService teams, FriendStore friends, SessionRegistry sessions, RoleStore roles, GameLoop loop) : IMicroService
{
    public string Name => "PartyService";

    public async ValueTask<object?> InvokeAsync(MicroSession session, string method, object? args)
    {
        // Team rosters and their protobuf directory snapshots belong to GameLoop, while the micro gate
        // reads on its network thread. Build the entire reply on the loop, not just the first lookup.
        return await loop.InvokeAsync(async () => method switch
        {
            "getPartysSummary" => Summary(args),
            "getSubPartysCountByType" => new Dictionary<string, object?> { ["Ret"] = MicroServiceRouter.Ret(true), ["GroupCount"] = teams.TargetCounts(Arg(args, 1) ?? "") },
            "getParty" => GetParty(args),
            "getPartysByIDs" => await GetGroupsAsync(session, args),
            _ => null,
        });
    }

    private object Summary(object? args)
    {
        var list = args as object?[] ?? [];
        long target = 0, playBook = 0;
        int map = 0;
        string area = "";
        if (list.Length > 1 && list[1] is object?[] strings)
            foreach (var value in strings.OfType<IDictionary<object, object?>>())
                if (Convert.ToString(Field(value, "Key")) == "area") area = Convert.ToString(Field(value, "Value")) ?? "";
        if (list.Length > 2 && list[2] is object?[] integers)
            foreach (var value in integers.OfType<IDictionary<object, object?>>())
            {
                long n = Number(Field(value, "MinValue"));
                switch (Convert.ToString(Field(value, "Key")))
                {
                    case "target": target = n; break;
                    case "playBook": playBook = n; break;
                    case "map": map = (int)n; break;
                }
            }
        int offset = list.Length > 4 ? (int)Number(list[4]) : 0;
        int limit = list.Length > 5 ? (int)Number(list[5]) : 100;
        return new Dictionary<string, object?>
        {
            ["Ret"] = MicroServiceRouter.Ret(true),
            ["SummaryList"] = teams.Directory(target, area, playBook, map, offset, limit),
        };
    }

    private object GetParty(object? args)
    {
        string? id = Arg(args, 1);
        return long.TryParse(id, out long teamId) && teams.DirectoryParty(teamId) is { } party
            ? new Dictionary<string, object?> { ["Ret"] = MicroServiceRouter.Ret(true), ["Party"] = party }
            : MicroServiceRouter.Fail("TID_NO_DOCUMENT");
    }

    private async Task<object> GetGroupsAsync(MicroSession session, object? args)
    {
        // LC_DAL_Friend:2451-2618 consumes PartyList[].MemberList[].GameMemberData.
        // Group IDs are sequential; a caller's supplied ID is never authorization.
        string? account = session.Account;
        if (string.IsNullOrEmpty(account)) return MicroServiceRouter.Fail("TID_NO_DOCUMENT");
        var active = sessions.All().Where(s => s.Role != null && string.Equals(s.Account, account, StringComparison.Ordinal))
            .Select(s => s.Role!.Id).ToHashSet();
        var allowed = active.Count > 0 ? active : (await roles.ForAccountAsync(account)).Select(r => r.Id).ToHashSet();
        var ids = args is IDictionary<object, object?> map && map.TryGetValue("PartyIDs", out var value) && value is object?[] list
            ? list.Select(Convert.ToString).Where(id => id != null).Cast<string>() : [];
        var parties = new List<object>();
        foreach (string id in ids)
        {
            var group = await friends.GroupAsync(id);
            if (group == null || !group.Members.Any(m => allowed.Contains(m.Role))) continue;
            var members = group.Members.Select(m => (object)new Dictionary<string, object?>
            {
                ["UID"] = m.Role.ToString(),
                ["GameMemberData"] = new Romsg.ChatGroupMemberData
                {
                    Title = m.Role == group.Owner ? Romsg.ChatGroupTitle.Master : Romsg.ChatGroupTitle.Member,
                    DoNotDisturb = m.Dnd,
                }.ToByteArray(),
            }).ToArray();
            parties.Add(new Dictionary<string, object?>
            {
                ["ID"] = group.Id, ["PartyData"] = new Romsg.ChatGroupData { GroupName = group.Name, CreateTimeSec = group.Created }.ToByteArray(),
                ["MemberList"] = members,
            });
        }
        return new Dictionary<string, object?> { ["Ret"] = MicroServiceRouter.Ret(true), ["PartyList"] = parties.ToArray() };
    }

    private static string? Arg(object? args, int index) => args is IDictionary<object, object?> map && map.TryGetValue(index, out var value)
        ? Convert.ToString(value) : args is object?[] list && list.Length >= index ? Convert.ToString(list[index - 1]) : null;

    private static long Number(object? value) => long.TryParse(Convert.ToString(value), out long number) ? number : 0;
    private static object? Field(IDictionary<object, object?> map, string key) => map.TryGetValue(key, out var value) ? value : null;
}

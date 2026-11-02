using Google.Protobuf;
using Romsg;
using Ro3Emu.Server.Accounts.Persistence;

namespace Ro3Emu.Server.Accounts;

/// <summary>Persistent friend metadata and chat group membership.</summary>
public sealed partial class FriendStore
{

    public DBFriendSystemInfo LoadMetadata(long role)
    {
        var (data, groups) = Read<(byte[]?, IReadOnlyList<ChatGroup>)>(FriendReadKind.Metadata, role);
        var result = data == null ? new DBFriendSystemInfo() : DBFriendSystemInfo.Parser.ParseFrom(data);
        result.RecentlyChats ??= new FriendChatRecordList();
        result.TopChats ??= new FriendChatRecordList();
        result.FriendPartitions ??= new FriendGroupList();
        result.RecentlyData ??= new FriendChatRecordList();
        result.GroupIds.Clear();
        result.OwnerGroupIds.Clear();
        foreach (var group in groups)
        {
            result.GroupIds.Add(group.Id);
            if (group.Owner == role) result.OwnerGroupIds.Add(group.Id);
        }
        return result;
    }

    public async Task<DBFriendSystemInfo> LoadMetadataAsync(long role)
    {
        var (data, groups) = await ReadAsync<(byte[]?, IReadOnlyList<ChatGroup>)>(FriendReadKind.Metadata, role);
        var result = data == null ? new DBFriendSystemInfo() : DBFriendSystemInfo.Parser.ParseFrom(data);
        result.RecentlyChats ??= new FriendChatRecordList();
        result.TopChats ??= new FriendChatRecordList();
        result.FriendPartitions ??= new FriendGroupList();
        result.RecentlyData ??= new FriendChatRecordList();
        result.GroupIds.Clear();
        result.OwnerGroupIds.Clear();
        foreach (var group in groups)
        {
            result.GroupIds.Add(group.Id);
            if (group.Owner == role) result.OwnerGroupIds.Add(group.Id);
        }
        return result;
    }

    public void SaveMetadata(long role, DBFriendSystemInfo data, RoleItemPlanBuilder? transaction = null)
    {
        byte[] captured = data.ToByteArray();
        if (transaction == null)
        {
            Write(new(FriendWriteKind.SaveMetadata, role, blob: captured));
            return;
        }
        transaction.FriendMetadata(role, captured);
    }
    public Task<bool> SaveMetadataAsync(long role, DBFriendSystemInfo data)
        => CommitOwnedAsync<bool>(new(FriendWriteKind.SaveMetadata, role, blob: data.ToByteArray()), [role]);

    private async Task<T> CommitOwnedAsync<T>(FriendWritePlan write, IEnumerable<long> participants, long groupId = 0)
    {
        var plan = new RoleItemPlanBuilder(_accounts);
        foreach (long role in participants.Where(role => role > 0).Distinct())
            plan.ExpectRole(await _accounts.ReadAsync<RoleRevisionVector>(new RoleRevisionReadPlan(role)));
        if (groupId > 0)
        {
            var key = RevisionStore.Entity("friend_group", groupId);
            plan.CaptureEntity(key, await _accounts.ReadAsync<OwnerRevisionValue>(new OwnerRevisionReadPlan(key)));
        }
        int result = plan.Add(write);
        var receipts = await plan.CommitAsync(_accounts, write.Label);
        return (T)receipts[result]!;
    }

    public sealed record ChatGroup(string Id, long Owner, string Name, long Created, IReadOnlyList<ChatMember> Members);
    public sealed record ChatMember(long Role, bool Dnd);

    public ChatGroup? Group(string id)
    {
        if (!long.TryParse(id, out long number) || number <= 0) return null;
        var group = Read<ChatGroup?>(FriendReadKind.Group, number);
        return group is null ? null : group with { Id = id };
    }

    public async Task<ChatGroup?> GroupAsync(string id)
    {
        if (!long.TryParse(id, out long number) || number <= 0) return null;
        var group = await ReadAsync<ChatGroup?>(FriendReadKind.Group, number);
        return group is null ? null : group with { Id = id };
    }

    public IReadOnlyList<ChatGroup> GroupsFor(long role)
        => Read<IReadOnlyList<ChatGroup>>(FriendReadKind.GroupsFor, role);
    public Task<IReadOnlyList<ChatGroup>> GroupsForAsync(long role) => ReadAsync<IReadOnlyList<ChatGroup>>(FriendReadKind.GroupsFor, role);

    public ChatGroup CreateGroup(long owner, string name, IReadOnlyCollection<long> members)
        => _accounts.WriteLegacySync<ChatGroup>(new FriendWritePlan(FriendWriteKind.CreateGroup, owner, time: DateTimeOffset.UtcNow.ToUnixTimeSeconds(), name: name, members: members));
    public Task<ChatGroup> CreateGroupAsync(long owner, string name, IReadOnlyCollection<long> members)
        => CommitOwnedAsync<ChatGroup>(new FriendWritePlan(FriendWriteKind.CreateGroup, owner,
            time: DateTimeOffset.UtcNow.ToUnixTimeSeconds(), name: name, members: members), members);

    public void RenameGroup(string id, string name) => Write(new(FriendWriteKind.RenameGroup, long.Parse(id), name: name));
    public void DismissGroup(string id) => Write(new(FriendWriteKind.DismissGroup, long.Parse(id)));

    public Task<bool> RenameGroupAsync(ChatGroup group, string name) =>
        CommitOwnedAsync<bool>(new(FriendWriteKind.RenameGroup, long.Parse(group.Id), name: name,
            participants: group.Members.Select(member => member.Role)),
            group.Members.Select(member => member.Role), long.Parse(group.Id));
    public Task<bool> DismissGroupAsync(ChatGroup group) =>
        CommitOwnedAsync<bool>(new(FriendWriteKind.DismissGroup, long.Parse(group.Id),
            participants: group.Members.Select(member => member.Role)),
            group.Members.Select(member => member.Role), long.Parse(group.Id));
    public Task<bool> ChangeMembersAsync(ChatGroup group, IEnumerable<long> roles, bool add)
    {
        long[] changed = [.. roles];
        long[] participants = [.. group.Members.Select(member => member.Role).Concat(changed).Distinct()];
        return CommitOwnedAsync<bool>(new(FriendWriteKind.ChangeMembers, long.Parse(group.Id), value: add ? 1 : 0,
            members: changed, participants: participants), participants, long.Parse(group.Id));
    }
    public Task<bool> SetDndAsync(ChatGroup group, long role, bool dnd) =>
        CommitOwnedAsync<bool>(new(FriendWriteKind.SetDnd, long.Parse(group.Id), role, value: dnd ? 1 : 0,
            participants: group.Members.Select(member => member.Role)),
            group.Members.Select(member => member.Role), long.Parse(group.Id));

    public void ChangeMembers(string id, IEnumerable<long> roles, bool add)
        => Write(new(FriendWriteKind.ChangeMembers, long.Parse(id), value: add ? 1 : 0, members: roles));

    public void SetDnd(string id, long role, bool dnd)
        => Write(new(FriendWriteKind.SetDnd, long.Parse(id), role, value: dnd ? 1 : 0));

    public int Intimacy(long owner, long other)
        => Read<int>(FriendReadKind.Intimacy, owner, other);
    public Task<int> IntimacyAsync(long owner, long other) => ReadAsync<int>(FriendReadKind.Intimacy, owner, other);

    public void SetIntimacy(long owner, long other, int value, RoleItemPlanBuilder? transaction = null)
    {
        if (transaction == null) { Write(new(FriendWriteKind.SetIntimacy, owner, other, value: value)); return; }
        transaction.FriendIntimacy(owner, other, value);
    }

    public void RecordTeammates(long joined, IEnumerable<long> existing)
        => Write(new(FriendWriteKind.RecordTeammates, joined, time: DateTimeOffset.UtcNow.ToUnixTimeSeconds(), members: existing));
    public Task<bool> RecordTeammatesAsync(long joined, IEnumerable<long> existing)
    {
        long[] members = [.. existing];
        return CommitOwnedAsync<bool>(new(FriendWriteKind.RecordTeammates, joined,
            time: DateTimeOffset.UtcNow.ToUnixTimeSeconds(), members: members), members.Append(joined));
    }
    public async Task RecordMatchedTeammatesAsync(IReadOnlyList<long> members)
    {
        var plan = new RoleItemPlanBuilder(_accounts);
        long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        foreach (long role in members.Distinct())
            plan.ExpectRole(await _accounts.ReadAsync<RoleRevisionVector>(new RoleRevisionReadPlan(role)));
        foreach (long member in members)
            plan.Add(new FriendWritePlan(FriendWriteKind.RecordTeammates, member, time: now,
                members: members.Where(id => id != member)));
        await plan.CommitAsync(_accounts, "friend/matched-teammates");
    }

    public IReadOnlyList<long> RecentTeammates(long role, int max = 100)
        // PublicParamConfig.SuggestFriendsTimesCd=1 does not prove the history unit; 24h is inferred.
        => Read<IReadOnlyList<long>>(FriendReadKind.RecentTeammates, role, max: max, cutoff: DateTimeOffset.UtcNow.AddDays(-1).ToUnixTimeSeconds());
}

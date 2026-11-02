using Google.Protobuf;
using Romsg;
using Ro3Emu.Server.Accounts;
using Ro3Emu.Server.Economy;
using System.Globalization;
using System.Security.Cryptography;

namespace Ro3Emu.Server.Game;

/// <summary>82200-82210 friend metadata, chat groups and gifts.</summary>
public sealed partial class FriendExtraService(FriendStore friends, RoleStore roles, SessionRegistry sessions,
    CharacterService character, ChatService chat, Micro.FriendService micro, GameLoop loop, PersonalService personal)
{
    public const uint CreateGroup = 82200, RenameGroup = 82201, InviteGroup = 82202, KickGroup = 82203,
        DismissGroup = 82204, LeaveGroup = 82205, SetGroupDnd = 82206, FetchMetadata = 82207,
        SetMetadata = 82208, GiveGift = 82209, Recommend = 82210, GroupNotify = 82250, GiftNotify = 82251;

    private GameSession? Online(long id) => sessions.All().FirstOrDefault(s => s.Role?.Id == id);
    private static bool Member(FriendStore.ChatGroup? group, long id) => group?.Members.Any(m => m.Role == id) == true;
    private static int Denied => ErrorCodes.NoPermission; // exact original code is unverified.

    public Task CreateAsync(GameSession s, CreateFriendChatGroupReq req) =>
        RunSocialAsync(s, "friend/group-create", req.InviteGuids, () => CreateCoreAsync(s, req));

    private async Task CreateCoreAsync(GameSession s, CreateFriendChatGroupReq req)
    {
        long self = s.Role!.Id;
        long[] others = [.. req.InviteGuids.Distinct()];
        int code = string.IsNullOrWhiteSpace(req.GroupName) || req.GroupName.Length > 7 || others.Length == 0 || others.Length + 1 > 50 ? ErrorCodes.Parameter
            : (await friends.GroupsForAsync(self)).Count >= 10 ? ErrorCodes.PartyFull : 0;
        foreach (long id in others)
            if (code == 0 && (id == self || await roles.GetAsync(id) == null || !await friends.AreFriendsAsync(self, id)
                || (await friends.GroupsForAsync(id)).Count >= 10)) code = Denied;
        if (code != 0) { await s.SendGameAsync(CreateGroup, new CreateFriendChatGroupResp { Result = code }); return; }
        var group = await friends.CreateGroupAsync(self, req.GroupName.Trim(), [self, .. others]);
        await s.SendGameAsync(CreateGroup, new CreateFriendChatGroupResp { Uid = group.Id });
        await NotifyAsync(group.Members.Select(m => m.Role), new FriendChatGroupNotify
        {
            Type = FriendChatGroupNotifyType.Join, GroupId = group.Id, GroupName = group.Name,
            RoleId = { group.Members.Select(m => m.Role) },
        }, group.Members.Select(m => m.Role));
    }

    public Task RenameAsync(GameSession s, RenameFriendChatGroupReq req) =>
        RunGroupAsync(s, "friend/group-rename", req.GroupId, [], group => RenameCoreAsync(s, req, group));

    private async Task RenameCoreAsync(GameSession s, RenameFriendChatGroupReq req, FriendStore.ChatGroup? group)
    {
        int code = group?.Owner != s.Role!.Id ? Denied : string.IsNullOrWhiteSpace(req.NewName) || req.NewName.Length > 7 ? ErrorCodes.Parameter : 0;
        if (code == 0) await friends.RenameGroupAsync(group!, req.NewName.Trim());
        await s.SendGameAsync(RenameGroup, new RenameFriendChatGroupResp { Result = code });
        if (code != 0) return;
        var renamed = group!;
        await NotifyAsync(renamed.Members.Select(m => m.Role), new FriendChatGroupNotify
        {
            Type = FriendChatGroupNotifyType.Rename, GroupId = renamed.Id, GroupName = req.NewName.Trim(), RoleId = { renamed.Members.Select(m => m.Role) },
        });
    }

    public Task InviteAsync(GameSession s, InviteFriendChatGroupReq req) =>
        RunGroupAsync(s, "friend/group-invite", req.GroupId, req.InviteRoleIds,
            group => InviteCoreAsync(s, req, group));

    private async Task InviteCoreAsync(GameSession s, InviteFriendChatGroupReq req, FriendStore.ChatGroup? group)
    {
        long[] invited = [.. req.InviteRoleIds.Distinct()];
        int code = !Member(group, s.Role!.Id) ? Denied
            : invited.Length == 0 || group!.Members.Count + invited.Length > 50 ? ErrorCodes.Parameter
            : 0;
        foreach (long id in invited)
            if (code == 0 && (Member(group, id) || await roles.GetAsync(id) == null
                || !await friends.AreFriendsAsync(s.Role.Id, id) || (await friends.GroupsForAsync(id)).Count >= 10)) code = Denied;
        if (code == 0) await friends.ChangeMembersAsync(group!, invited, true);
        await s.SendGameAsync(InviteGroup, new InviteFriendChatGroupResp { Result = code });
        if (code != 0) return;
        var changed = (await friends.GroupAsync(group!.Id))!;
        await NotifyAsync(changed.Members.Select(m => m.Role), new FriendChatGroupNotify
        {
            Type = FriendChatGroupNotifyType.Join, GroupId = changed.Id, GroupName = changed.Name,
            RoleId = { invited },
        }, invited);
    }

    public Task KickAsync(GameSession s, KickFriendChatGroupReq req) =>
        RunGroupAsync(s, "friend/group-kick", req.GroupId, req.KickRoleIds,
            group => KickCoreAsync(s, req, group));

    private async Task KickCoreAsync(GameSession s, KickFriendChatGroupReq req, FriendStore.ChatGroup? group)
    {
        long[] kicked = [.. req.KickRoleIds.Distinct()];
        int code = group?.Owner != s.Role!.Id ? Denied
            : kicked.Length == 0 || kicked.Contains(group.Owner) || kicked.Any(id => !Member(group, id)) ? ErrorCodes.Parameter : 0;
        if (code == 0)
        {
            await friends.ChangeMembersAsync(group!, kicked, false);
            foreach (long role in kicked) if (Online(role) is { } target) chat.FriendMembershipChanged(target, group!.Id);
        }
        await s.SendGameAsync(KickGroup, new KickFriendChatGroupResp { Result = code });
        if (code != 0) return;
        var original = group!;
        await NotifyAsync(original.Members.Select(m => m.Role), new FriendChatGroupNotify
        {
            Type = FriendChatGroupNotifyType.Leave, GroupId = original.Id, GroupName = original.Name, RoleId = { kicked },
        }, kicked);
    }

    public Task DismissAsync(GameSession s, DismissFriendChatGroupReq req) =>
        RunGroupAsync(s, "friend/group-dismiss", req.GroupId, [], group => DismissCoreAsync(s, req, group));

    private async Task DismissCoreAsync(GameSession s, DismissFriendChatGroupReq req, FriendStore.ChatGroup? group)
    {
        int code = group?.Owner == s.Role!.Id ? 0 : Denied;
        if (code == 0)
        {
            await friends.DismissGroupAsync(group!);
            foreach (var member in group!.Members) if (Online(member.Role) is { } target) chat.FriendMembershipChanged(target, group.Id);
        }
        await s.SendGameAsync(DismissGroup, new DismissFriendChatGroupResp { Result = code });
        if (code != 0) return;
        var dismissed = group!;
        await NotifyAsync(dismissed.Members.Select(m => m.Role), new FriendChatGroupNotify
        {
            Type = FriendChatGroupNotifyType.Dismiss, GroupId = dismissed.Id, GroupName = dismissed.Name,
            RoleId = { dismissed.Members.Select(m => m.Role) },
        });
    }

    public Task LeaveAsync(GameSession s, LeaveFriendChatGroupReq req) =>
        RunGroupAsync(s, "friend/group-leave", req.GroupId, [], group => LeaveCoreAsync(s, req, group));

    private async Task LeaveCoreAsync(GameSession s, LeaveFriendChatGroupReq req, FriendStore.ChatGroup? group)
    {
        int code = !Member(group, s.Role!.Id) || group!.Owner == s.Role.Id ? Denied : 0;
        if (code == 0)
        {
            await friends.ChangeMembersAsync(group!, [s.Role.Id], false);
            chat.FriendMembershipChanged(s, group!.Id);
        }
        await s.SendGameAsync(LeaveGroup, new LeaveFriendChatGroupResp { Result = code });
        if (code != 0) return;
        var former = group!;
        await NotifyAsync(former.Members.Select(m => m.Role), new FriendChatGroupNotify
        {
            Type = FriendChatGroupNotifyType.Leave, GroupId = former.Id, GroupName = former.Name, RoleId = { s.Role.Id },
        }, [s.Role.Id]);
    }

    public Task DndAsync(GameSession s, SetFriendChatGroupDNDReq req) =>
        RunGroupAsync(s, "friend/group-dnd", req.GroupId, [], group => DndCoreAsync(s, req, group));

    private async Task DndCoreAsync(GameSession s, SetFriendChatGroupDNDReq req, FriendStore.ChatGroup? group)
    {
        int code = Member(group, s.Role!.Id) ? 0 : Denied;
        if (code == 0) await friends.SetDndAsync(group!, s.Role.Id, req.DoNotDisturb);
        await s.SendGameAsync(SetGroupDnd, new SetFriendChatGroupDNDResp { Result = code, GroupId = req.GroupId, DoNotDisturb = req.DoNotDisturb });
    }

    public async Task FetchAsync(GameSession s)
    {
        var data = await friends.LoadMetadataAsync(s.Role!.Id);
        var resp = new FetchFriendSystemMetadataResp
        {
            RecentlyChats = data.RecentlyChats.Clone(), TopChats = data.TopChats.Clone(), FriendPartitions = data.FriendPartitions.Clone(),
            RecentlyData = data.RecentlyData.Clone(),
        };
        resp.GroupIds.Add(data.GroupIds);
        resp.SendGiftRecords.Add(data.SendGiftRecords);
        resp.ReceiveGiftRecords.Add(data.ReceiveGiftRecords);
        await s.SendGameAsync(FetchMetadata, resp);
    }

    /// <summary>82208 is partial; echo every changed parent message so LC_DAL_Friend:2797-2874 can dereference it.</summary>
    public Task SetAsync(GameSession s, SetFriendSystemMetadataReq req) =>
        RunSocialAsync(s, "friend/metadata", req.FriendPartitions?.CustomGroupList
            .SelectMany(group => group.MemberList) ?? [], () => SetCoreAsync(s, req));

    private async Task SetCoreAsync(GameSession s, SetFriendSystemMetadataReq req)
    {
        var data = await friends.LoadMetadataAsync(s.Role!.Id);
        int code = req.FriendPartitions != null &&
            (req.FriendPartitions.CustomGroupList.Count > 10 || req.FriendPartitions.CustomGroupList.Any(g =>
                string.IsNullOrWhiteSpace(g.GroupName) || g.GroupName.Length > 7 || g.MemberList.Count is 0 or > 100))
            ? ErrorCodes.Parameter : 0;
        if (code == 0 && req.FriendPartitions != null)
            foreach (var group in req.FriendPartitions.CustomGroupList)
                foreach (long id in group.MemberList)
                    if (!await friends.AreFriendsAsync(s.Role.Id, id)) code = ErrorCodes.Parameter;
        var resp = new SetFriendSystemMetadataResp { Result = code };
        if (code != 0) { await s.SendGameAsync(SetMetadata, resp); return; }
        if (req.RecentlyChats != null) resp.RecentlyChats = data.RecentlyChats = req.RecentlyChats.Clone();
        if (req.TopChats != null) resp.TopChats = data.TopChats = req.TopChats.Clone();
        if (req.FriendPartitions != null) resp.FriendPartitions = data.FriendPartitions = req.FriendPartitions.Clone();
        if (req.RecentlyData != null) resp.RecentlyData = data.RecentlyData = req.RecentlyData.Clone();
        await friends.SaveMetadataAsync(s.Role.Id, data);
        await s.SendGameAsync(SetMetadata, resp);
    }

    /// <summary>82209 pays the inventory cost and saves both gift records in one SQLite transaction; intimacy +1 is inferred.</summary>
    public async Task GiftAsync(GameSession s, GiveFriendGiftReq req)
    {
        int? changedIntimacy = null;
        await RunSocialAsync(s, "friend/gift", [req.RoleId], () => GiftCoreAsync(s, req,
            intimacy => changedIntimacy = intimacy));
        if (changedIntimacy is { } intimacy)
        {
            await micro.IntimacyChangedAsync(req.RoleId, s.Role!.Id, intimacy);
            await micro.IntimacyChangedAsync(s.Role.Id, req.RoleId, intimacy);
        }
    }

    private async Task GiftCoreAsync(GameSession s, GiveFriendGiftReq req, Action<int> committed)
    {
        long self = s.Role!.Id;
        int code = req.ItemId is < 1004000 or > 1004006 || req.Count <= 0 || req.Message.Length > 20 ? ErrorCodes.Parameter
            : req.RoleId == self || await roles.GetAsync(req.RoleId) == null || !await friends.AreFriendsAsync(self, req.RoleId) ? Denied : 0;
        if (code != 0) { await s.SendGameAsync(GiveGift, new GiveFriendGiftResp { Result = code }); return; }
        var owners = loop.Owners!;
        var recipientVector = await roles.ReadRevisionVectorAsync(req.RoleId);
        if (recipientVector.Deleted)
        {
            await s.SendGameAsync(GiveGift, new GiveFriendGiftResp { Result = Denied });
            return;
        }
        var recipient = owners.GetOrRegister(recipientVector);
        if (!owners.Current!.CanPin(recipient) || recipient.State == Ownership.RoleOwnerState.Loading)
        {
            await s.SendGameAsync(GiveGift, new GiveFriendGiftResp { Result = Denied });
            return;
        }
        owners.Current.Pin(recipient);
        int item = (int)req.ItemId;
        var sender = await friends.LoadMetadataAsync(self);
        var receiver = await friends.LoadMetadataAsync(req.RoleId);
        int intimacy = (int)Math.Min(180000L, (long)await friends.IntimacyAsync(self, req.RoleId) + req.Count); // (inferred) one per gift unit.
        var record = new FriendGiftRecord
        {
            Sender = self, Receiver = req.RoleId, ItemId = req.ItemId, Count = req.Count, Message = req.Message,
            IsAnonymous = req.IsAnonymous, SendTime = DateTimeOffset.UtcNow.ToUnixTimeSeconds(), Intimacy = intimacy,
        };
        (record.Popularity, record.Affection) = personal.GiftValues(req.ItemId, req.Count);
        sender.SendGiftRecords.Add(record);
        receiver.ReceiveGiftRecords.Add(record);
        while (sender.SendGiftRecords.Count > 100) sender.SendGiftRecords.RemoveAt(0);
        while (receiver.ReceiveGiftRecords.Count > 100) receiver.ReceiveGiftRecords.RemoveAt(0);
        long receiverId = req.RoleId;
        byte[] senderBlob = sender.ToByteArray(), receiverBlob = receiver.ToByteArray();
        string operationId = Guid.NewGuid().ToString("N");
        var envelope = new EconomyEnvelope(operationId, s.Account!, self, "friend-gift",
            Convert.ToHexString(SHA256.HashData(req.ToByteArray())), "friend-gift:" + operationId,
            DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            [new EconomyEffect(new EconomyAsset(EconomyAssetKind.Item, item),
                new EconomyOwner(EconomyOwnerKind.Role, self.ToString(CultureInfo.InvariantCulture)),
                EconomyCompartment.Inventory, -req.Count, EconomyLegKind.Sink, EconomyReason.FriendGiftCost)],
            record.ToByteArray());
        using var characterLock = await CharacterService.Lock(s, owners.Current);
        var paid = await character.ExchangeLockedAsync(s, [(item, (long)req.Count)], [], popup: false, persist: tx =>
        {
            tx.ExpectRole(recipientVector);
            tx.FriendMetadata(self, senderBlob);
            tx.FriendMetadata(receiverId, receiverBlob);
            tx.FriendIntimacy(self, receiverId, intimacy);
            tx.FriendIntimacy(receiverId, self, intimacy);
            tx.WithEconomy(envelope);
        }, asyncCommit: true);
        if (paid == null) { await s.SendGameAsync(GiveGift, new GiveFriendGiftResp { Result = ErrorCodes.ItemsShort }); return; }
        committed(intimacy);
        // (inferred) Popularity is no asset: credited in its own commit once the gift is paid.
        await personal.CreditGiftAsync(self, receiverId, record.Popularity, record.Affection);
        await s.SendGameAsync(GiveGift, new GiveFriendGiftResp { FriendGiftRecord = record });
        if (Online(req.RoleId) is { } target) await target.SendGameAsync(GiftNotify, record);
    }

    /// <summary>
    /// 82210: online peers and recorded recent teammates, ranked by role id inside each category (inferred).
    /// LC_DAL_Friend:3057-3069 takes # of all four arrays, so a success requires every category nonempty.
    /// </summary>
    public async Task RecommendAsync(GameSession s)
    {
        long self = s.Role!.Id;
        var recent = friends.RecentTeammates(self);
        var online = sessions.All().Where(x => x.Role != null).ToDictionary(x => x.Role!.Id);
        var candidates = online.Keys.Concat(recent).Distinct().Where(id => id != self && roles.Get(id) is { Level: >= 15 }
            && !friends.AreFriends(self, id) && !friends.Blocks(self, id) && !friends.Blocks(id, self))
            .Select(id => (Id: id, Role: roles.Get(id)!, Session: online.GetValueOrDefault(id))).ToArray();
        int myLevel = s.Progress?.Level ?? s.Role.Level;
        int myJob = s.Progress?.Career ?? s.Role.Career;
        var result = new FetchRecommendFriendsResp();
        result.DiffSex.Add(candidates.Where(c => c.Role.Sex != s.Role.Sex).OrderBy(c => c.Id).Take(10).Select(c => c.Id));
        result.LevelRange.Add(candidates.Where(c => Math.Abs((c.Session?.Progress?.Level ?? c.Role.Level) - myLevel) <= 5)
            .OrderBy(c => c.Id).Take(10).Select(c => c.Id));
        result.RecentTeam.Add(recent.Where(id => candidates.Any(c => c.Id == id)).Take(10));
        result.SameJob.Add(candidates.Where(c => (c.Session?.Progress?.Career ?? c.Role.Career) == myJob)
            .OrderBy(c => c.Id).Take(10).Select(c => c.Id));
        if (result.DiffSex.Count == 0 || result.LevelRange.Count == 0 || result.RecentTeam.Count == 0 || result.SameJob.Count == 0)
            result.Result = ErrorCodes.Unknown; // (inferred) no-candidate refusal, avoids nil-array Lua parse.
        await s.SendGameAsync(Recommend, result);
    }

    private async Task NotifyAsync(IEnumerable<long> roles, FriendChatGroupNotify message, IEnumerable<long>? selfAffected = null)
    {
        var selfSet = selfAffected?.ToHashSet();
        foreach (long role in roles.Distinct())
            if (Online(role) is { } online)
            {
                var copy = message.Clone();
                copy.IsSelf = selfSet?.Contains(role) == true;
                await online.SendGameAsync(GroupNotify, copy);
            }
    }
}

using System.Text.Json;
using Microsoft.Extensions.Options;
using Romsg;
using Ro3Emu.Server.Accounts;
using Ro3Emu.Server.Data;

namespace Ro3Emu.Server.Game;

/// <summary>
/// Guild voice channels (MG_GuildVoiceChannel 83700-83723; client LC_DAL_GuildVoice / LC_GuildVoiceServer /
/// LC_GuildVoiceHelper): GuildSettingConfig.GuildVoiceChannelCount channels per guild, 0-based on the wire, each in
/// Free mode (anyone talks) or Order mode (the leader, holders of the GuildVoice right 23 and members given the mic).
/// The client rebuilds its panel from every 83709 UpdateNotify (a receiver missing from members leaves), so every
/// change is pushed in full to everybody in the channel; 83710/83712/83713 are empty handlers in this client build
/// and 83714/83715 only log, so they are not sent. In memory only: channels start empty after a restart.
///
/// Leaving the guild or rank changes come in through <see cref="GuildService.MembershipChanged"/>, and edited rank
/// rights through <see cref="GuildService.RightsChanged"/>. Role handover removes a logout immediately; a
/// once-a-second sweep also drops replaced sessions and members no longer in that guild. The audio
/// itself is the voice relay's: room "2_&lt;guildId&gt;_&lt;index&gt;" admits who <see cref="IsSeated"/> and holds the mic
/// off for whoever may not speak (<see cref="MaySpeak"/>).
/// Invitations last 30 seconds. Their target-wide cooldown and reject-all window come from
/// GuildVoiceChannelInviteCooldown and GuildVoiceChannelInviteRejectDuration. Like the channels, invitation state is
/// in memory only; 83718/83719 are never sent by this client.
/// </summary>
public sealed class GuildVoiceService(GameData data, GuildStore store, GuildService guilds, SessionRegistry sessions,
    GameLoop loop, TimeProvider clock, IOptions<ServerOptions> options) : Messaging.IMessageModule, IGameTicker
{
    public const uint JoinMsg = 83700, LeaveMsg = 83701, SetSpeakModeMsg = 83702, ApplyMicMsg = 83703, HandleApplyMsg = 83704;
    public const uint KickMsg = 83705, UnbanMsg = 83706, InviteMsg = 83707, InviteResponseMsg = 83708;
    public const uint UpdateNotify = 83709, KickedNotify = 83711, InviteNotify = 83716, InviteResponseNotify = 83717;
    public const uint NotInCooldownMsg = 83720, BlackListMsg = 83721, SetSpeakPermissionMsg = 83722, AllMemberCountMsg = 83723;

    /// <summary>enum_GuildPermission.GuildVoice (Lua_GameDefine), GuildPermissionConfig row 23.</summary>
    public const int VoiceRight = 23;

    /// <summary>The client sets no limit on the mic request reason; this bound is ours.</summary>
    public const int MaxReasonLength = 50;

    private const int DefaultChannelCount = 3;
    private const long DefaultBlockMs = 86_400_000;
    private const long DefaultInviteCooldownMs = 15_000;
    private const long DefaultInviteRejectMs = 600_000;
    private const int InviteTimeoutSeconds = 30;
    private const long InviteTimeoutMs = InviteTimeoutSeconds * 1_000L;
    private static readonly TimeSpan SweepInterval = TimeSpan.FromSeconds(1);

    private sealed class Seat(GameSession session, long joinMs)
    {
        public GameSession Session { get; } = session;
        public long JoinMs { get; } = joinMs;
        public bool Granted { get; set; }
    }

    private sealed record Application(long Role, string Reason, long AtMs);

    private sealed record PendingInvite(long Guild, int Channel, long Inviter, GameSession InviterSession,
        GameSession TargetSession, long ExpiresMs);

    private sealed class InviteState
    {
        public long LastInviteMs { get; set; }
        public long RejectUntilMs { get; set; }
    }

    private sealed class Channel(int index)
    {
        public int Index { get; } = index;
        public GuildSpeakAuthorityType Mode { get; set; } = GuildSpeakAuthorityType.Free;
        public Dictionary<long, Seat> Members { get; } = new();
        public List<Application> Applications { get; } = new();
        public Dictionary<long, long> Blocked { get; } = new(); // role -> blocked until, unix ms
    }

    private readonly object _sync = new();
    private readonly Dictionary<long, Channel[]> _byGuild = new();
    private readonly Dictionary<long, (long Guild, int Index)> _where = new();
    private readonly Dictionary<long, PendingInvite> _pendingInvites = new(); // target role -> one live invitation
    private readonly Dictionary<(long Guild, long Role), InviteState> _inviteStates = new();
    private readonly HashSet<long> _rechecking = new(); // roles with a sweep recheck already posted
    private TimeSpan _lastSweep;

    public void Map(Messaging.MessageRoutes routes)
    {
        routes.Map(AllMemberCountMsg, "guildVoice", (s, _, _) => s.SendGameAsync(AllMemberCountMsg, Counts(s)));
        routes.Map(JoinMsg, "guildVoice", (s, _, body) => JoinAsync(s, GuildVoiceChannelJoinReq.Parser.ParseFrom(body.Span)));
        routes.Map(LeaveMsg, "guildVoice", (s, _, body) => LeaveAsync(s, GuildVoiceChannelLeaveReq.Parser.ParseFrom(body.Span)));
        routes.Map(SetSpeakModeMsg, "guildVoice", (s, _, body) => SetModeAsync(s, GuildVoiceChannelSetSpeakModeReq.Parser.ParseFrom(body.Span)));
        routes.Map(ApplyMicMsg, "guildVoice", (s, _, body) => ApplyAsync(s, GuildVoiceChannelApplyMicReq.Parser.ParseFrom(body.Span)));
        routes.Map(HandleApplyMsg, "guildVoice", (s, _, body) => HandleApplyAsync(s, GuildVoiceChannelHandleApplyReq.Parser.ParseFrom(body.Span)));
        routes.Map(SetSpeakPermissionMsg, "guildVoice", (s, _, body) => SetPermissionAsync(s, GuildVoiceChannelSetSpeakPermissionReq.Parser.ParseFrom(body.Span)));
        routes.Map(KickMsg, "guildVoice", (s, _, body) => KickAsync(s, GuildVoiceChannelKickPlayerReq.Parser.ParseFrom(body.Span)));
        routes.Map(UnbanMsg, "guildVoice", (s, _, body) => UnbanAsync(s, GuildVoiceChannelUnbanPlayerReq.Parser.ParseFrom(body.Span)));
        routes.Map(InviteMsg, "guildVoice", (s, _, body) => InviteAsync(s, GuildVoiceChannelInviteReq.Parser.ParseFrom(body.Span)));
        routes.Map(InviteResponseMsg, "guildVoice", (s, _, body) => InviteResponseAsync(s,
            GuildVoiceChannelInviteResponseReq.Parser.ParseFrom(body.Span)));
        routes.Map(NotInCooldownMsg, "guildVoice", (s, _, _) => NotInCooldownAsync(s));
        routes.Map(BlackListMsg, "guildVoice", (s, _, body) => BlackListAsync(s, GuildVoiceChannelGetBlackListReq.Parser.ParseFrom(body.Span)));
    }

    /// <summary>A member's guild or rank changed (GuildService.MembershipChanged, inside its commit): checked on the loop.</summary>
    public void OnMembershipChanged(GameSession session)
    {
        if (session.Role is { } role) loop.Post(() => RecheckAsync(role.Id));
    }

    /// <summary>Logout/role switch: remove this exact attachment's seat and publish the remaining channel.</summary>
    public async Task LeaveRoleAsync(GameSession session)
    {
        if (session.Role is not { } role) return;
        GuildStore.State? guild = null;
        Channel? left = null;
        lock (_sync)
        {
            CancelInvites(role.Id, session);
            if (!OwnsSeat(role.Id, session) || !_where.TryGetValue(role.Id, out var at)) return;
            guild = store.Get(at.Guild);
            left = Unseat(role.Id);
        }
        if (guild != null && left != null) await PushAsync(guild, left);
    }

    /// <summary>Rank rights were committed: refresh each occupied channel once, even when no rank changed.</summary>
    public void OnRightsChanged(long guildId) => loop.Post(async () =>
    {
        if (store.Get(guildId) is not { } guild) return;
        Channel[] occupied;
        lock (_sync)
            occupied = _byGuild.TryGetValue(guildId, out var channels)
                ? channels.Where(c => c.Members.Count > 0).ToArray() : [];
        foreach (var channel in occupied) await PushAsync(guild, channel);
    });

    public void Tick(GameTime time)
    {
        if (time.Now - _lastSweep < SweepInterval) return;
        _lastSweep = time.Now;
        List<long> seated;
        lock (_sync)
        {
            PruneBlocks();
            seated = _where.Keys.ToList();
        }
        if (seated.Count == 0) return;
        var online = sessions.All().Where(s => s.Role != null).ToDictionary(s => s.Role!.Id);
        foreach (long role in seated)
            if (!OwnsSeat(role, online.GetValueOrDefault(role)) || store.ForRole(role)?.Id != Where(role)?.Guild)
            {
                lock (_sync) if (!_rechecking.Add(role)) continue;
                loop.Post(async () =>
                {
                    try { await RecheckAsync(role); }
                    finally { lock (_sync) _rechecking.Remove(role); }
                });
            }
    }

    /// <summary>For the relay: <paramref name="role"/> sits in channel <paramref name="index"/> of <paramref name="guildId"/>.</summary>
    public bool IsSeated(long role, long guildId, int index)
    {
        lock (_sync) return _where.TryGetValue(role, out var at) && at.Guild == guildId && at.Index == index;
    }

    /// <summary>For the relay: false only for a seated member of a channel in Order mode who has not been given the
    /// mic (the leader and holders of the GuildVoice right always may).</summary>
    public bool MaySpeak(long role)
    {
        lock (_sync)
        {
            if (!_where.TryGetValue(role, out var at) || !_byGuild.TryGetValue(at.Guild, out var channels)) return true;
            var channel = channels[at.Index];
            if (channel.Mode == GuildSpeakAuthorityType.Free || !channel.Members.TryGetValue(role, out var seat)) return true;
            return store.Get(at.Guild) is not { } guild || CanSpeak(guild, channel, role, seat);
        }
    }

    // ---- requests ----

    private GuildVoiceChannelGetAllMemberCountRsp Counts(GameSession s)
    {
        var reply = new GuildVoiceChannelGetAllMemberCountRsp();
        if (Guild(s) is not { } guild) { reply.ErrorCode = ErrorCodes.GuildVoiceNotInGuild; return reply; }
        lock (_sync)
            foreach (var channel in Channels(guild.Id))
                reply.ChannelCounts.Add(new ChannelMemberCount { ChannelIndex = channel.Index, MemberCount = channel.Members.Count });
        return reply;
    }

    private async Task JoinAsync(GameSession s, GuildVoiceChannelJoinReq req)
    {
        long self = s.Role!.Id;
        var guild = Guild(s);
        int code;
        GuildVoiceChannelInfo? info = null;
        Channel? joined = null;
        Channel? left = null;
        GuildStore.State? formerGuild = null;
        lock (_sync)
        {
            // A fresh login may join before the next sweep. Remove its predecessor's seat and mic request
            // in the same transition as the new join, before any notification can suspend this command.
            if (_where.TryGetValue(self, out var previous) && !OwnsSeat(self, s))
            {
                formerGuild = store.Get(previous.Guild);
                left = Unseat(self);
            }
            code = guild == null ? ErrorCodes.GuildVoiceNotInGuild
                : !ValidIndex(req.ChannelIndex) ? ErrorCodes.GuildVoiceBadChannel
                : _where.TryGetValue(self, out var at) ? (at.Guild == guild.Id && at.Index == req.ChannelIndex
                    ? ErrorCodes.GuildVoiceAlreadyInChannel : ErrorCodes.GuildVoiceInAnotherChannel)
                : IsBlocked(Channels(guild.Id)[req.ChannelIndex], self) ? ErrorCodes.GuildVoiceBlocked
                : 0;
            if (code == 0)
            {
                joined = Channels(guild!.Id)[req.ChannelIndex];
                joined.Members[self] = new Seat(s, NowMs());
                _where[self] = (guild.Id, req.ChannelIndex);
                info = Info(guild, joined);
            }
        }
        await s.SendGameAsync(JoinMsg, new GuildVoiceChannelJoinRsp { ErrorCode = code, ChannelInfo = info });
        if (left != null && left != joined && formerGuild != null) await PushAsync(formerGuild, left);
        if (joined != null) await PushAsync(guild!, joined, except: self);
    }

    private async Task LeaveAsync(GameSession s, GuildVoiceChannelLeaveReq req)
    {
        long self = s.Role!.Id;
        var guild = Guild(s);
        Channel? left = null;
        lock (_sync)
            if (guild != null && OwnsSeat(self, s) && _where.TryGetValue(self, out var at)
                && at.Guild == guild.Id && at.Index == req.ChannelIndex)
                left = Unseat(self);
        await s.SendGameAsync(LeaveMsg, new GuildVoiceChannelLeaveRsp
        {
            ErrorCode = guild == null ? ErrorCodes.GuildVoiceNotInGuild : left == null ? ErrorCodes.GuildVoiceNotInChannel : 0,
        });
        if (left != null) await PushAsync(guild!, left);
    }

    private async Task SetModeAsync(GameSession s, GuildVoiceChannelSetSpeakModeReq req)
    {
        var (code, guild, channel) = Managed(s, req.ChannelIndex);
        if (code == 0)
            lock (_sync) SwitchMode(channel!, req.SpeakMode);
        await s.SendGameAsync(SetSpeakModeMsg, new GuildVoiceChannelSetSpeakModeRsp { ErrorCode = code });
        if (code == 0) await PushAsync(guild!, channel!);
    }

    private async Task ApplyAsync(GameSession s, GuildVoiceChannelApplyMicReq req)
    {
        long self = s.Role!.Id;
        var (code, guild, channel) = Seated(s, req.ChannelIndex);
        string reason = req.Reason ?? "";
        lock (_sync)
            if (code == 0)
            {
                // Seated() let go of the lock: the seat may be gone by now (a Leave or a Kick in between).
                code = !channel!.Members.TryGetValue(self, out var seat) ? ErrorCodes.GuildVoiceNotInChannel
                    : channel.Mode == GuildSpeakAuthorityType.Free ? ErrorCodes.GuildVoiceFreeMode
                    : IsManager(guild!, self) ? ErrorCodes.GuildVoiceManagerNeedsNoApply
                    : seat.Granted ? ErrorCodes.GuildVoiceAlreadyCanSpeak
                    : channel.Applications.Any(a => a.Role == self) ? ErrorCodes.GuildVoiceAlreadyApplied
                    : reason.Length > MaxReasonLength ? ErrorCodes.GuildVoiceReasonTooLong
                    : 0;
                if (code == 0) channel.Applications.Add(new Application(self, reason, NowMs()));
            }
        await s.SendGameAsync(ApplyMicMsg, new GuildVoiceChannelApplyMicRsp { ErrorCode = code });
        if (code == 0) await PushAsync(guild!, channel!);
    }

    private async Task HandleApplyAsync(GameSession s, GuildVoiceChannelHandleApplyReq req)
    {
        var (code, guild, channel) = Managed(s, req.ChannelIndex);
        lock (_sync)
            if (code == 0)
            {
                code = channel!.Mode != GuildSpeakAuthorityType.Order ? ErrorCodes.GuildVoiceNotOrderMode
                    : channel.Applications.RemoveAll(a => a.Role == req.TargetPlayerId) == 0 ? ErrorCodes.GuildVoiceNotApplied
                    : 0;
                if (code == 0 && req.IsAgree && channel.Members.TryGetValue(req.TargetPlayerId, out var seat)) seat.Granted = true;
            }
        await s.SendGameAsync(HandleApplyMsg, new GuildVoiceChannelHandleApplyRsp { ErrorCode = code });
        if (code == 0) await PushAsync(guild!, channel!);
    }

    /// <summary>The permission panel's Save (UI_Sys_Chat_PermissionSetupWidgetCtrl._onClickSave): the dropdown's mode
    /// and the changed mics in one request. This client never sends 83702, so the mode is taken from here.</summary>
    private async Task SetPermissionAsync(GameSession s, GuildVoiceChannelSetSpeakPermissionReq req)
    {
        var (code, guild, channel) = Managed(s, req.ChannelIndex);
        lock (_sync)
            if (code == 0)
            {
                // All or nothing: the batch is checked before any seat changes.
                foreach (var item in req.PermissionItems)
                {
                    if (!channel!.Members.ContainsKey(item.TargetPlayerId)) { code = ErrorCodes.GuildVoiceNotInChannel; break; }
                    if (!item.GrantPermission && IsLeader(guild!, item.TargetPlayerId)) { code = ErrorCodes.GuildVoiceLeaderCannotMute; break; }
                }
                if (code == 0) SwitchMode(channel!, req.SpeakMode); // first, so mics given in this Save survive its reset
                if (code == 0)
                    foreach (var item in req.PermissionItems)
                    {
                        channel!.Members[item.TargetPlayerId].Granted = item.GrantPermission;
                        channel.Applications.RemoveAll(a => a.Role == item.TargetPlayerId);
                    }
            }
        await s.SendGameAsync(SetSpeakPermissionMsg, new GuildVoiceChannelSetSpeakPermissionRsp { ErrorCode = code });
        if (code == 0) await PushAsync(guild!, channel!);
    }

    private async Task KickAsync(GameSession s, GuildVoiceChannelKickPlayerReq req)
    {
        long self = s.Role!.Id;
        var (code, guild, channel) = Managed(s, req.ChannelIndex);
        lock (_sync)
            if (code == 0)
            {
                code = req.TargetPlayerId == self ? ErrorCodes.GuildVoiceCannotKickSelf
                    : IsLeader(guild!, req.TargetPlayerId) ? ErrorCodes.GuildVoiceCannotKickLeader
                    : !channel!.Members.ContainsKey(req.TargetPlayerId) ? ErrorCodes.GuildVoiceNotInChannel
                    : 0;
                if (code == 0)
                {
                    Unseat(req.TargetPlayerId);
                    channel!.Blocked[req.TargetPlayerId] = NowMs() + Setting("GuildVoiceChannelBlacklistDuration", DefaultBlockMs);
                }
            }
        await s.SendGameAsync(KickMsg, new GuildVoiceChannelKickPlayerRsp { ErrorCode = code });
        if (code != 0) return;
        if (Online(req.TargetPlayerId) is { } target)
            await target.SendGameAsync(KickedNotify, new GuildVoiceChannelKickedNotify { ChannelIndex = channel!.Index });
        await PushAsync(guild!, channel!);
    }

    private async Task UnbanAsync(GameSession s, GuildVoiceChannelUnbanPlayerReq req)
    {
        var (code, _, channel) = Managed(s, req.ChannelIndex, mustBeSeated: false);
        lock (_sync)
            if (code == 0 && !(channel!.Blocked.Remove(req.TargetPlayerId, out long until) && until > NowMs()))
                code = ErrorCodes.GuildVoiceNotBlocked;
        await s.SendGameAsync(UnbanMsg, new GuildVoiceChannelUnbanPlayerRsp { ErrorCode = code, TargetPlayerId = req.TargetPlayerId });
    }

    private async Task BlackListAsync(GameSession s, GuildVoiceChannelGetBlackListReq req)
    {
        var (code, _, channel) = Managed(s, req.ChannelIndex, mustBeSeated: false);
        var reply = new GuildVoiceChannelGetBlackListRsp { ErrorCode = code };
        lock (_sync)
            if (code == 0)
            {
                long now = NowMs();
                foreach (var (role, until) in channel!.Blocked)
                    if (until > now) reply.BlackList[role] = until;
            }
        await s.SendGameAsync(BlackListMsg, reply);
    }

    private async Task InviteAsync(GameSession s, GuildVoiceChannelInviteReq req)
    {
        var (code, guild, channel) = Seated(s, req.ChannelIndex);
        var reply = new GuildVoiceChannelInviteRsp { ErrorCode = code };
        List<(GameSession Session, GuildVoiceChannelInviteNotify Notify)> notifications = [];
        lock (_sync)
        {
            if (code == 0 && req.TargetPlayerIds.Count == 0) reply.ErrorCode = code = ErrorCodes.Parameter;
            // No guild-voice-specific full code was recovered. Refuse with the repository's explicit generic failure.
            if (code == 0 && ChannelFull(channel!)) reply.ErrorCode = code = ErrorCodes.Unknown;
            if (code == 0) AddInvites(s, guild!, channel!, req.TargetPlayerIds, reply, notifications);
        }
        await s.SendGameAsync(InviteMsg, reply);
        foreach (var (target, notify) in notifications)
            if (ReferenceEquals(Online(target.Role!.Id), target)) await target.SendGameAsync(InviteNotify, notify);
    }

    private void AddInvites(GameSession inviter, GuildStore.State guild, Channel channel, IEnumerable<long> targets,
        GuildVoiceChannelInviteRsp reply, List<(GameSession, GuildVoiceChannelInviteNotify)> notifications)
    {
        long now = NowMs();
        foreach (long target in targets)
        {
            int code = InviteFailure(guild, channel, target, now, out var targetSession);
            if (code != 0)
            {
                reply.PlayersResult.Add(new GuildVoiceChannelInviteFailedPlayer { PlayerId = target, ErrorCode = code });
                continue;
            }
            InviteState state = StateFor(guild.Id, target);
            state.LastInviteMs = now;
            _pendingInvites[target] = new PendingInvite(guild.Id, channel.Index, inviter.Role!.Id, inviter,
                targetSession!, now + InviteTimeoutMs);
            notifications.Add((targetSession!, new GuildVoiceChannelInviteNotify
            {
                ChannelIndex = channel.Index, InviterPlayerId = inviter.Role.Id, TimeoutSeconds = InviteTimeoutSeconds,
            }));
        }
    }

    private async Task InviteResponseAsync(GameSession s, GuildVoiceChannelInviteResponseReq req)
    {
        long self = s.Role!.Id;
        GuildStore.State? guild = Guild(s);
        Channel? channel = null;
        PendingInvite? invite = null;
        GuildVoiceChannelInfo? info = null;
        int code;
        lock (_sync)
        {
            code = guild == null ? ErrorCodes.GuildVoiceNotInGuild
                : !ValidIndex(req.ChannelIndex) ? ErrorCodes.GuildVoiceBadChannel : 0;
            if (code == 0) code = ValidateResponse(s, guild!, req, out invite, out channel);
            if (invite != null) _pendingInvites.Remove(self); // a matching response consumes the one-shot invitation
            if (code == 0)
            {
                if (!req.IsAccept && req.RejectAll)
                    StateFor(guild!.Id, self).RejectUntilMs = NowMs() + InviteRejectMs();
                if (req.IsAccept) info = AcceptInvite(s, guild!, channel!, out code);
            }
        }
        await s.SendGameAsync(InviteResponseMsg, new GuildVoiceChannelInviteResponseRsp { ErrorCode = code, ChannelInfo = info });
        if (code != 0 || invite == null) return;
        if (ReferenceEquals(Online(invite.Inviter), invite.InviterSession))
            await invite.InviterSession.SendGameAsync(InviteResponseNotify, new GuildVoiceChannelInviteResponseNotify
            {
                ChannelIndex = invite.Channel, TargetPlayerId = self, IsAccept = req.IsAccept,
            });
        if (req.IsAccept) await PushAsync(guild!, channel!, except: self);
    }

    private int ValidateResponse(GameSession target, GuildStore.State guild, GuildVoiceChannelInviteResponseReq req,
        out PendingInvite? invite, out Channel? channel)
    {
        var pending = ActiveInvite(target.Role!.Id, NowMs());
        invite = null;
        channel = null;
        // No dedicated missing/expired-invite code is present in the recovered guild-voice range. Use the same
        // explicit generic request failure as TeamService instead of acknowledging an unmatched response.
        if (pending == null || pending.Guild != guild.Id || pending.Channel != req.ChannelIndex
            || pending.Inviter != req.InviteUserId || !ReferenceEquals(pending.TargetSession, target)) return ErrorCodes.Parameter;
        invite = pending;
        channel = Channels(guild.Id)[req.ChannelIndex];
        if (!req.IsAccept) return 0;
        if (_where.TryGetValue(target.Role.Id, out var at))
            return at.Guild == guild.Id && at.Index == req.ChannelIndex
                ? ErrorCodes.GuildVoiceAlreadyInChannel : ErrorCodes.GuildVoiceInAnotherChannel;
        if (ChannelFull(channel)) return ErrorCodes.Unknown;
        return IsBlocked(channel, target.Role.Id) ? ErrorCodes.GuildVoiceBlocked : 0;
    }

    private GuildVoiceChannelInfo? AcceptInvite(GameSession target, GuildStore.State guild, Channel channel, out int code)
    {
        long self = target.Role!.Id;
        if (!ReferenceEquals(Online(self), target) || store.ForRole(self)?.Id != guild.Id)
        {
            code = ErrorCodes.GuildVoiceTargetOffline;
            return null;
        }
        channel.Members[self] = new Seat(target, NowMs());
        _where[self] = (guild.Id, channel.Index);
        code = 0;
        return Info(guild, channel);
    }

    private async Task NotInCooldownAsync(GameSession s)
    {
        GuildStore.State? guild = Guild(s);
        var reply = new GuildVoiceGetNotInCdPlayerResp();
        lock (_sync)
        {
            if (guild == null) reply.ErrorCode = ErrorCodes.GuildVoiceNotInGuild;
            else if (!_where.TryGetValue(s.Role!.Id, out var at) || at.Guild != guild.Id || !OwnsSeat(s.Role.Id, s))
                reply.ErrorCode = ErrorCodes.GuildVoiceNotInChannel;
            else if (ChannelFull(Channels(guild.Id)[at.Index])) reply.ErrorCode = ErrorCodes.Unknown;
            else
            {
                long now = NowMs();
                var channel = Channels(guild.Id)[at.Index];
                foreach (long role in guild.Members.Keys.Order())
                    if (role != s.Role.Id && InviteFailure(guild, channel, role, now, out _) == 0) reply.UserIds.Add(role);
            }
        }
        await s.SendGameAsync(NotInCooldownMsg, reply);
    }

    // ---- membership ----

    /// <summary>Drops <paramref name="role"/> from its channel when its session is gone/replaced or it left the guild; on a
    /// rank change (it stays) the channel is pushed again, since who may speak in Order mode can have changed.</summary>
    private async Task RecheckAsync(long role)
    {
        var at = Where(role);
        if (at == null || store.Get(at.Value.Guild) is not { } guild) { await DropAsync(role); return; }
        bool stays = OwnsSeat(role, Online(role)) && store.ForRole(role)?.Id == guild.Id;
        Channel channel;
        lock (_sync)
        {
            channel = Channels(guild.Id)[at.Value.Index];
            if (!stays) Unseat(role);
        }
        await PushAsync(guild, channel);
    }

    /// <summary>The guild itself is gone: the seat just goes.</summary>
    private Task DropAsync(long role)
    {
        lock (_sync)
        {
            RecheckInvites(role);
            Unseat(role);
        }
        return Task.CompletedTask;
    }

    /// <summary>
    /// A switch of mode starts a fresh round: nobody holds a granted mic and no request is open. Setting the mode the
    /// channel already has (the panel sends it with every Save) changes nothing. Under the lock.
    /// </summary>
    private static void SwitchMode(Channel channel, GuildSpeakAuthorityType requested)
    {
        var mode = requested == GuildSpeakAuthorityType.Order ? GuildSpeakAuthorityType.Order : GuildSpeakAuthorityType.Free;
        if (channel.Mode == mode) return;
        channel.Mode = mode;
        foreach (var seat in channel.Members.Values) seat.Granted = false;
        channel.Applications.Clear();
    }

    /// <summary>Removes <paramref name="role"/> from its channel (with its mic request); under the lock.</summary>
    private Channel? Unseat(long role)
    {
        if (!_where.Remove(role, out var at) || !_byGuild.TryGetValue(at.Guild, out var channels)) return null;
        var channel = channels[at.Index];
        channel.Members.Remove(role);
        channel.Applications.RemoveAll(a => a.Role == role);
        CancelInvites(role);
        return channel;
    }

    private int InviteFailure(GuildStore.State guild, Channel channel, long target, long now,
        out GameSession? targetSession)
    {
        targetSession = null;
        if (!guild.Members.ContainsKey(target)) return ErrorCodes.GuildVoiceTargetNotInGuild;
        targetSession = Online(target);
        if (targetSession == null || store.ForRole(target)?.Id != guild.Id) return ErrorCodes.GuildVoiceTargetOffline;
        if (_where.ContainsKey(target)) return ErrorCodes.GuildVoiceTargetAlreadyInChannel;
        if (IsBlocked(channel, target)) return ErrorCodes.GuildVoiceBlocked;
        InviteState state = StateFor(guild.Id, target);
        if (state.RejectUntilMs > now) return ErrorCodes.GuildVoiceInviteDeclined;
        if (ActiveInvite(target, now) != null) return ErrorCodes.GuildVoiceInvitePending;
        return state.LastInviteMs > 0 && state.LastInviteMs + InviteCooldownMs() > now
            ? ErrorCodes.GuildVoiceInviteCooldown : 0;
    }

    /// <summary>Returns a still actionable, session-bound invitation; stale records are consumed. Under the lock.</summary>
    private PendingInvite? ActiveInvite(long target, long now)
    {
        if (!_pendingInvites.TryGetValue(target, out var invite)) return null;
        bool inviterSeated = _where.TryGetValue(invite.Inviter, out var at) && at.Guild == invite.Guild
            && at.Index == invite.Channel && _byGuild.TryGetValue(invite.Guild, out var channels)
            && channels[invite.Channel].Members.TryGetValue(invite.Inviter, out var seat)
            && ReferenceEquals(seat.Session, invite.InviterSession);
        if (invite.ExpiresMs > now && inviterSeated && ReferenceEquals(Online(target), invite.TargetSession)
            && ReferenceEquals(Online(invite.Inviter), invite.InviterSession)
            && store.ForRole(target)?.Id == invite.Guild && store.ForRole(invite.Inviter)?.Id == invite.Guild) return invite;
        _pendingInvites.Remove(target);
        return null;
    }

    /// <summary>Removes invitations received or sent by a role. A session narrows logout cleanup to that attachment.</summary>
    private void CancelInvites(long role, GameSession? session = null)
    {
        if (_pendingInvites.TryGetValue(role, out var received)
            && (session == null || ReferenceEquals(received.TargetSession, session))) _pendingInvites.Remove(role);
        foreach (var (target, invite) in _pendingInvites.ToList())
            if (invite.Inviter == role && (session == null || ReferenceEquals(invite.InviterSession, session)))
                _pendingInvites.Remove(target);
    }

    /// <summary>Consumes only invitation records made stale by a membership/session change. Under the lock.</summary>
    private void RecheckInvites(long role)
    {
        long now = NowMs();
        if (_pendingInvites.ContainsKey(role)) ActiveInvite(role, now);
        foreach (long target in _pendingInvites.Where(x => x.Value.Inviter == role).Select(x => x.Key).ToArray())
            ActiveInvite(target, now);
    }

    private InviteState StateFor(long guild, long role)
    {
        var key = (guild, role);
        if (!_inviteStates.TryGetValue(key, out var state)) _inviteStates[key] = state = new InviteState();
        return state;
    }

    // ---- state helpers ----

    /// <summary>The caller's channel <paramref name="index"/> when the caller sits in it.</summary>
    private (int Code, GuildStore.State? Guild, Channel? Channel) Seated(GameSession s, int index)
    {
        if (Guild(s) is not { } guild) return (ErrorCodes.GuildVoiceNotInGuild, null, null);
        if (!ValidIndex(index)) return (ErrorCodes.GuildVoiceBadChannel, guild, null);
        lock (_sync)
            return Where(s.Role!.Id) is { } at && at.Guild == guild.Id && at.Index == index && OwnsSeat(s.Role.Id, s)
                ? (0, guild, Channels(guild.Id)[index])
                : (ErrorCodes.GuildVoiceNotInChannel, guild, null);
    }

    /// <summary>As <see cref="Seated"/>, and the caller is the leader or holds the GuildVoice right.</summary>
    private (int Code, GuildStore.State? Guild, Channel? Channel) Managed(GameSession s, int index, bool mustBeSeated = true)
    {
        var (code, guild, channel) = mustBeSeated ? Seated(s, index) : Unseated(s, index);
        if (code != 0) return (code, guild, channel);
        return IsManager(guild!, s.Role!.Id) ? (0, guild, channel) : (ErrorCodes.GuildVoiceNoPermission, guild, channel);
    }

    /// <summary>Block-list reads and unblocking: a manager need not sit in the channel.</summary>
    private (int Code, GuildStore.State? Guild, Channel? Channel) Unseated(GameSession s, int index)
    {
        if (Guild(s) is not { } guild) return (ErrorCodes.GuildVoiceNotInGuild, null, null);
        if (!ValidIndex(index)) return (ErrorCodes.GuildVoiceBadChannel, guild, null);
        lock (_sync) return (0, guild, Channels(guild.Id)[index]);
    }

    private Channel[] Channels(long guildId)
    {
        if (!_byGuild.TryGetValue(guildId, out var channels))
            _byGuild[guildId] = channels = Enumerable.Range(0, ChannelCount()).Select(i => new Channel(i)).ToArray();
        return channels;
    }

    private (long Guild, int Index)? Where(long role)
    {
        lock (_sync) return _where.TryGetValue(role, out var at) ? at : null;
    }

    private bool OwnsSeat(long role, GameSession? session)
    {
        lock (_sync)
            return session != null && _where.TryGetValue(role, out var at)
                && _byGuild[at.Guild][at.Index].Members.TryGetValue(role, out var seat)
                && ReferenceEquals(seat.Session, session);
    }

    private bool IsBlocked(Channel channel, long role)
    {
        if (!channel.Blocked.TryGetValue(role, out long until)) return false;
        if (until > NowMs()) return true;
        channel.Blocked.Remove(role); // expired
        return false;
    }

    /// <summary>Forgets blocks that ran out, and the channels of guilds with nobody seated and nobody blocked; under
    /// the lock.</summary>
    private void PruneBlocks()
    {
        long now = NowMs();
        foreach (var (target, invite) in _pendingInvites.ToList())
            if (invite.ExpiresMs <= now) _pendingInvites.Remove(target);
        foreach (var (key, state) in _inviteStates.ToList())
            if (state.RejectUntilMs <= now && state.LastInviteMs + InviteCooldownMs() <= now
                && (!_pendingInvites.TryGetValue(key.Role, out var invite) || invite.Guild != key.Guild))
                _inviteStates.Remove(key);
        foreach (var (guildId, channels) in _byGuild.ToList())
        {
            foreach (var channel in channels)
                foreach (var (role, until) in channel.Blocked.ToList())
                    if (until <= now) channel.Blocked.Remove(role);
            if (channels.All(c => c.Members.Count == 0 && c.Blocked.Count == 0 && c.Mode == GuildSpeakAuthorityType.Free))
                _byGuild.Remove(guildId);
        }
    }

    private bool ValidIndex(int index) => index >= 0 && index < ChannelCount();

    private static bool IsLeader(GuildStore.State guild, long role) => guild.Members.TryGetValue(role, out var m) && m.Official == 1;

    private bool IsManager(GuildStore.State guild, long role) => guilds.HasRight(guild, role, VoiceRight);

    private bool CanSpeak(GuildStore.State guild, Channel channel, long role, Seat seat) =>
        channel.Mode == GuildSpeakAuthorityType.Free || seat.Granted || IsManager(guild, role);

    /// <summary>The full channel state the client replaces its own with; under the lock.</summary>
    private GuildVoiceChannelInfo Info(GuildStore.State guild, Channel channel)
    {
        var info = new GuildVoiceChannelInfo { ChannelIndex = channel.Index, SpeakMode = channel.Mode, TotalMemberCount = channel.Members.Count };
        foreach (var (role, seat) in channel.Members.OrderBy(m => m.Value.JoinMs))
            info.Members.Add(new GuildVoiceChannelMember { PlayerId = role, JoinTime = seat.JoinMs, HasSpeakPermission = CanSpeak(guild, channel, role, seat) });
        foreach (var a in channel.Applications)
            info.ApplyList.Add(new ApplyInfo { PlayerId = a.Role, Reason = a.Reason, ApplyTime = a.AtMs });
        return info;
    }

    /// <summary>83709 with the whole channel to everybody in it (optionally but one, who just got it in a reply).</summary>
    private async Task PushAsync(GuildStore.State guild, Channel channel, long except = 0)
    {
        GuildVoiceChannelUpdateNotify notify;
        List<(long Role, GameSession Session)> members;
        lock (_sync)
        {
            notify = new GuildVoiceChannelUpdateNotify { ChannelIndex = channel.Index, ChannelInfo = Info(guild, channel) };
            members = channel.Members.Where(m => m.Key != except).Select(m => (m.Key, m.Value.Session)).ToList();
        }
        foreach (var (role, session) in members)
            if (ReferenceEquals(Online(role), session)) await session.SendGameAsync(UpdateNotify, notify);
    }

    private GuildStore.State? Guild(GameSession s) => s.Role == null ? null : store.ForRole(s.Role.Id);

    private GameSession? Online(long role) => sessions.All().FirstOrDefault(s => s.Role?.Id == role);

    private long NowMs() => clock.GetUtcNow().ToUnixTimeMilliseconds();

    private bool ChannelFull(Channel channel) => channel.Members.Count >= options.Value.Voice.MaxRoomSize;

    private long InviteCooldownMs() => Math.Max(0, Setting("GuildVoiceChannelInviteCooldown", DefaultInviteCooldownMs));

    private long InviteRejectMs() => Math.Max(0, Setting("GuildVoiceChannelInviteRejectDuration", DefaultInviteRejectMs));

    private int ChannelCount() => (int)Math.Clamp(Setting("GuildVoiceChannelCount", DefaultChannelCount), 1, 16);

    private long Setting(string key, long fallback)
    {
        if (!data.Table("GuildSettingConfig").TryGetValue(key, out var row)
            || !row.TryGetProperty("_kValue", out var value)) return fallback;
        return value.ValueKind == JsonValueKind.Number ? (long)(GameData.Number(value) ?? fallback) : fallback;
    }
}

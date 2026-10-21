using Google.Protobuf;
using Romsg;
using Ro3Emu.Server.Accounts;

namespace Ro3Emu.Server.Game;

public sealed partial class GuildService
{
    public const uint CancelCreate = 32215, HelperOpe = 32220, HelperLeave = 32221, HelperChange = 32222,
        Recruit = 32257;
    private readonly Dictionary<long, long> _guildlessRecruitMs = new();

    /// <summary>32257 publishes to the client channel-8 IM surface; recipient-independent cooldown is inferred.</summary>
    private async Task RecruitCoreAsync(GameSession s, GuildRecruitReq req)
    {
        var guild = Own(s);
        long now = NowMs();
        long delay = guild?.Status == (int)GuildStatus.PreCreate
            ? Setting("GuildResponseReleaseRecruitment", 300)[0] : Setting("GuildRecruitButtonCD", 300)[0];
        long previous = guild?.LastRecruitMs ?? (s.Role == null ? 0 : _guildlessRecruitMs.GetValueOrDefault(s.Role.Id));
        int code = s.Role == null ? ErrorCodes.Parameter
            : guild != null && !HasRight(guild, s.Role.Id, 7) ? ErrorCodes.GuildPermission
            : string.IsNullOrWhiteSpace(req.Content) || req.Content.Length > Setting("GuildRecruitNoticeMaxNum", 80)[0]
            ? ErrorCodes.GuildTextLength : Sensitive(req.Content) ? ErrorCodes.TextSensitive
            : previous + delay * 1000 > now ? ErrorCodes.Cooldown : 0;
        if (code == 0)
        {
            if (guild == null) _guildlessRecruitMs[s.Role!.Id] = now;
            else { guild.LastRecruitMs = now; await SaveGuildAsync(guild); }
            await chat.PublishRecruitmentAsync(s, req.Content, guild?.Id ?? 0,
                guild?.Status == (int)GuildStatus.PreCreate, guild?.Members.Count ?? 0);
        }
        await s.SendGameAsync(Recruit, new GuildRecruitResp
        {
            Result = code, Content = req.Content,
            NextTime = code == 0 ? now / 1000 + delay : previous / 1000 + delay,
            IsNormal = guild?.Status == (int)GuildStatus.Normal
        });
    }

    /// <summary>32215: only the pre-create founder can cancel; all helper membership indices are released.</summary>
    private async Task CancelCreateCoreAsync(GameSession s)
    {
        var guild = Own(s);
        int code = guild == null ? ErrorCodes.NotInGuild : guild.Status != (int)GuildStatus.PreCreate
            || guild.Members[s.Role!.Id].Official != 1 ? ErrorCodes.GuildPermission : 0;
        if (code == 0)
        {
            await DeleteGuildAsync(guild!);
            await s.SendGameAsync(CancelCreate, new GuildCancleCreateResp { Result = 0 });
            foreach (var member in guild!.Members.Values)
            {
                if (Online(member.RoleId) is { } online) await ClearMember(online, guild);
            }
            return;
        }
        await s.SendGameAsync(CancelCreate, new GuildCancleCreateResp { Result = code });
    }

    /// <summary>
    /// 32220: a guildless player responds to guilds being founded: joins one at once when its founder needs no
    /// approval, otherwise waits in each founder's list (GuildService.Responders).
    /// </summary>
    private async Task HelperOpeCoreAsync(GameSession s, GuildCreateHelperOpeReq req)
    {
        var rsp = new GuildCreateHelperOpeResp();
        if (s.Role == null || req.GuildIds.Count == 0) rsp.Result = ErrorCodes.Parameter;
        else if (Own(s) != null) rsp.Result = ErrorCodes.AlreadyInGuild;
        else
        {
            int failure = 0;
            foreach (var id in req.GuildIds.Distinct())
            {
                int code = Own(s) != null ? ErrorCodes.AlreadyInGuild : await RespondAsync(s, store.Get(id));
                if (code != 0 && failure == 0) failure = code;
                rsp.HelperResult.Add(new GuildCreateHelperOpeResp.Types.helperResultData { GuildId = id, Success = code == 0 });
            }
            if (!rsp.HelperResult.Any(x => x.Success)) rsp.Result = failure;
        }
        await s.SendGameAsync(HelperOpe, rsp);
    }

    private async Task HelperLeaveCoreAsync(GameSession s, GuildCreateHelperLeaveReq req)
    {
        var guild = Own(s);
        int code = guild == null || guild.Id != req.GuildId ? ErrorCodes.NotInGuild
            : guild.Status != (int)GuildStatus.PreCreate || guild.Members[s.Role!.Id].Official == 1
            ? ErrorCodes.GuildPermission : 0;
        if (code == 0)
        {
            var member = guild!.Members[s.Role!.Id];
            guild.Members.Remove(member.RoleId);
            await SaveGuildAsync(guild, [member.RoleId]);
            await s.SendGameAsync(HelperLeave, new GuildCreateHelperLeaveResp { Result = 0, GuildId = guild.Id });
            foreach (var peer in guild.Members.Values)
                if (Online(peer.RoleId) is { } online)
                    await online.SendGameAsync(HelperChange, new GuildCreateHelperMemberChangeNtf
                    {
                        Status = GuildCreateHelperMemberChangeNtf.Types.ChangeType.Delete,
                        Info = new GuildCreateHelperInfo { PlayerID = member.RoleId }
                    });
            await ClearMember(s, guild);
            return;
        }
        await s.SendGameAsync(HelperLeave, new GuildCreateHelperLeaveResp { Result = code, GuildId = req.GuildId });
    }

    private async Task ExpireHelpersAsync()
    {
        foreach (var guild in store.List().Where(g => g.Status == (int)GuildStatus.PreCreate && g.HelperDeadlineMs <= NowMs()))
        {
            await DeleteGuildAsync(guild);
            foreach (var member in guild.Members.Values)
            {
                if (Online(member.RoleId) is { } online) await ClearMember(online, guild);
            }
        }
    }
}

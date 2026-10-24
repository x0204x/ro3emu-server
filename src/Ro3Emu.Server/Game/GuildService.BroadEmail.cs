using Romsg;
using Ro3Emu.Server.Accounts;

namespace Ro3Emu.Server.Game;

/// <summary>
/// Guild group mail (32241 GUILD_BROAD_EMAIL, UI_Sys_Guild_MailWidgetCtrl): a member with GuildPermissionConfig 6 SendMail
/// writes one letter to every member. The client sends only the text, titled with the guild's name
/// (LC_GuildServer.RequestGuildMailBroadCast), so the server fills in the sender. The guild may send GuildMassEmailNum
/// letters a day, the count being the guild's and resetting at 05:00 (UITips 10960000023), with GuildMassEmailCD seconds
/// between them. The answer and 32231 Change_BroadEmail carry the next allowed time (ms) and today's count, which the
/// member list's mail button shows (UI_Sys_Guild_Member_InfoWidgetCtrl). ErrorCodeConfig: 10025 today's letters used,
/// 10026 empty text, 10027 longer than GuildEmailContentNumNew, 161 on cooldown.
/// (inferred) Each letter is raw Guild Mail (category 3) from the member, titled with the guild's current name: MailConfig
/// 1019 "Guild Group Mail" carries an unrelated body (an MVP reissue) and a template would replace the sender with its NPC.
/// </summary>
public sealed partial class GuildService
{
    public const uint BroadEmail = 32241;
    private const int SendMailRight = 6;

    /// <summary>Today's 05:00 local, unix seconds (the group mail count's reset).</summary>
    private long DayStart
    {
        get
        {
            var local = clock.GetLocalNow();
            return new DateTimeOffset(local.AddHours(-5).Date.AddHours(5), local.Offset).ToUnixTimeSeconds();
        }
    }

    private int BroadEmailsToday(GuildStore.State g) => g.BroadEmailDay == DayStart ? g.BroadEmailCount : 0;

    public Task BroadEmailAsync(GameSession s, GuildBroadEmailReq req) => RunGuildAsync(s, [], [], () => BroadEmailCoreAsync(s, req));

    private async Task BroadEmailCoreAsync(GameSession s, GuildBroadEmailReq req)
    {
        var g = Own(s);
        string content = req.Req?.Content ?? "";
        long now = NowMs();
        int code = g == null ? ErrorCodes.NotInGuild
            : !HasRight(g, s.Role!.Id, (int)Setting("GuildSendEmailPermission", SendMailRight)[0]) ? ErrorCodes.GuildPermission
            : string.IsNullOrWhiteSpace(content) ? ErrorCodes.GuildMailContent
            : content.Length > Setting("GuildEmailContentNumNew", 0, 80)[^1] ? ErrorCodes.GuildTextLength
            : Sensitive(content) ? ErrorCodes.TextSensitive
            : BroadEmailsToday(g) >= Setting("GuildMassEmailNum", 10)[0] ? ErrorCodes.GuildMailDaily
            : now < g.NextBroadEmailMs ? ErrorCodes.Cooldown : 0;
        if (code != 0 || g == null)
        {
            await s.SendGameAsync(BroadEmail, new GuildBroadEmailResp
            {
                Result = code, NextBroadEmailTime = g?.NextBroadEmailMs ?? 0, BroadEmailLimit = g == null ? 0 : BroadEmailsToday(g)
            });
            return;
        }
        g.BroadEmailCount = BroadEmailsToday(g) + 1;
        g.BroadEmailDay = DayStart;
        g.NextBroadEmailMs = now + Setting("GuildMassEmailCD", 3600)[0] * 1000;
        await SaveGuildAsync(g);
        await s.SendGameAsync(BroadEmail, new GuildBroadEmailResp
        { Result = 0, NextBroadEmailTime = g.NextBroadEmailMs, BroadEmailLimit = g.BroadEmailCount });
        await PushBase(g, new GuildBaseInfoChangeNtf
        {
            Tp = GuildBaseInfoChangeNtf.Types.ChangeType.ChangeBroadEmail,
            NextBroadEmailTime = g.NextBroadEmailMs, BroadEmailLimit = g.BroadEmailCount
        });
        var context = ownerRegistry.Current;
        foreach (long role in g.Members.Keys.Order())
        {
            try { await mail.SendGuildAsync(role, g.Name, content, s.Role!.Name, s.Role.Id, context); }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                LogGroupMailFailed(log, e, g.Id, role);
            }
        }
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "Guild {Guild} group mail to role {Role} failed")]
    private static partial void LogGroupMailFailed(ILogger logger, Exception error, long guild, long role);
}

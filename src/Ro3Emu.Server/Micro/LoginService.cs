using MessagePack;
using Ro3Emu.Server.Accounts;

namespace Ro3Emu.Server.Micro;

/// <summary>
/// "LoginService" on the micro gate. verifySdk (Phonest.Client LoginAgent.VerifyWithSdk) turns the HTTP SDK token
/// into the engine token the client later presents to the game gate (ConnectServerReq.token).
/// </summary>
public sealed class LoginService(AccountStore accounts, RoleStore roles, MicroSessions micro, ILogger<LoginService> log) : IMicroService
{
    public string Name => "LoginService";

    public async ValueTask<object?> InvokeAsync(MicroSession session, string method, object? args)
    {
        return method switch
        {
            "verifySdk" => await VerifySdkAsync(session, MicroServiceRouter.MapString(args, "Ticket")),
            "getRoleInfo" => await GetRoleInfoAsync(session),
            _ => null,
        };
    }

    /// <summary>
    /// Lua LC_DAL_LoginRole sends [verifyToken, ""]; the account is already bound to this micro connection by
    /// verifySdk. Each RoleInfos entry: area + romsg.RoleBaseInfo bytes (+ optional DBWardRobeInfo bytes).
    /// </summary>
    private async Task<Dictionary<string, object?>> GetRoleInfoAsync(MicroSession session)
    {
        if (session.Account == null) return MicroServiceRouter.Fail("not verified");
        var roleList = await roles.ForAccountAsync(session.Account);
        var list = new List<object>();
        foreach (var r in roleList)
            list.Add(new Dictionary<string, object?>
            {
                ["area"] = r.Area,
                ["baseInfo"] = Google.Protobuf.MessageExtensions.ToByteArray(Game.RoleProto.BaseInfo(r)),
                ["appearances"] = Game.RoleProto.WardrobeBytes(r, await roles.LoadWardrobeAsync(r.Id)),
            });
        log.LogInformation("getRoleInfo: {Account} has {Count} role(s)", session.Account, list.Count);
        return new() { ["Ret"] = MicroServiceRouter.Ret(true), ["RoleInfos"] = list.ToArray() };
    }

    private async Task<Dictionary<string, object?>> VerifySdkAsync(MicroSession session, string? ticket)
    {
        var account = await accounts.ResolveSdkTokenAsync(ticket);
        if (account == null)
        {
            log.LogWarning("verifySdk: unknown ticket from {Remote}", session.Channel.Remote);
            return MicroServiceRouter.Fail("invalid ticket");
        }
        micro.SignIn(session, account);
        // Phonest.Common.MixedLoginToken, [MessagePackObject(true)] => string-keyed map.
        var engineToken = MessagePackSerializer.Serialize(new Dictionary<string, object>
        {
            ["Username"] = account,
            ["Expired"] = DateTimeOffset.UtcNow.AddDays(1).ToUnixTimeSeconds(),
            ["Sign"] = await accounts.IssueEngineSignAsync(account),
        });
        log.LogInformation("verifySdk: {Account} verified", account);
        return new() { ["Token"] = engineToken, ["Ret"] = MicroServiceRouter.Ret(true) };
    }
}

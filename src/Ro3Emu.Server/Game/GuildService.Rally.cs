using System.Globalization;
using Romsg;
using Ro3Emu.Server.Data;
using Ro3Emu.Server.Economy;
using Ro3Emu.Server.Maps;

namespace Ro3Emu.Server.Game;

/// <summary>
/// Guild Rally (function 1330): 32297 calls the online members to the
/// caller, 32298 asks each of them, 32299 accepts (Cancel sends nothing).
/// <list type="bullet">
/// <item>Client (proven): the button needs guild right 22 and its local cooldown GuildGatherCD (300 s, 10032); the
/// panel prices it GuildGatherCost [104, 5] against the guild funds; the answer window is GuildGatherInviteTime (60 s,
/// LC_DAL_Guild.lua:1656-1699).</item>
/// <item>(inferred, after AdventureGroupService rallies) The caller's scene must allow it (MapConfig/DungeonConfig
/// _kGuildWhitelist[0], 10031), the answering member's [1] (10033); nobody in battle; the cooldown is the guild's;
/// the funds go through the ledger as a guild treasury sink.</item>
/// </list>
/// </summary>
public sealed partial class GuildService
{
    public const uint Convene = 32297, ConveneNotify = 32298, ConfirmConvene = 32299;
    // ErrorCodeConfig (English): 10030 The invitation to Join a guild has expired, 10031 Guild summons cannot be used in
    // the current area, 10032 Guild summon is on cooldown, 10033 Guild summon teleportation is not allowed in the current
    // area.
    public const int ErrRallyExpired = 10030, ErrRallyArea = 10031, ErrRallyCooldown = 10032, ErrRallyTargetArea = 10033;
    private const int RallyRight = 22;

    private sealed record Rally(long GuildId, long Caller, long Scene, MapPoint Point, long ExpiresMs);
    private readonly Dictionary<long, Rally> _rallies = new();
    private readonly Dictionary<long, long> _lastRallyMs = new();

    private bool GuildWhitelisted(GameSession s, int index)
    {
        var row = (s.InstanceId != 0 ? data.Row("DungeonConfig", s.SceneEntry) : null) ?? data.Row("MapConfig", s.SceneEntry);
        if (row is not { } r || !r.TryGetProperty("_kGuildWhitelist", out var list)
            || list.ValueKind != System.Text.Json.JsonValueKind.Array || list.GetArrayLength() <= index) return false;
        return GameData.Number(list[index]) == 1;
    }

    public Task ConveneAsync(GameSession s) => RunGuildAsync(s, [], [], () => ConveneCoreAsync(s));

    private async Task ConveneCoreAsync(GameSession s)
    {
        var g = Own(s);
        long now = NowMs();
        var cost = Setting("GuildGatherCost", 104, 5);
        long price = cost.Length > 1 ? cost[1] : 5;
        int code = g == null ? ErrorCodes.NotInGuild : g.Status != (int)GuildStatus.Normal ? ErrorCodes.FeatureUnavailable
            : !HasRight(g, s.Role!.Id, RallyRight) ? ErrorCodes.GuildPermission
            : _lastRallyMs.GetValueOrDefault(g.Id) + Setting("GuildGatherCD", 300)[0] * 1000 > now ? ErrRallyCooldown
            : !GuildWhitelisted(s, 0) ? ErrRallyArea
            : (s.StatusBits & (1L << 15)) != 0 ? ErrRallyArea
            : g.Money < price ? ErrorCodes.GuildFundsShort : 0;
        if (code == 0)
        {
            g!.Money -= price;
            var economy = new EconomyEnvelope(Guid.NewGuid().ToString("N"), s.Account!, s.Role!.Id, "guild-rally",
                g.Id.ToString(CultureInfo.InvariantCulture), $"guild-rally:{g.Id}:{now}", now,
                [new(new(EconomyAssetKind.GuildResource, 1), new(EconomyOwnerKind.Guild, g.Id.ToString(CultureInfo.InvariantCulture)),
                    EconomyCompartment.GuildTreasury, -price, EconomyLegKind.Sink, EconomyReason.GuildResourceSpend)], []);
            if (!await SaveGuildAsync(g, economy: economy)) code = ErrorCodes.GuildMissing;
        }
        await s.SendGameAsync(Convene, new GuildConveneRes { Result = code });
        if (code != 0) return;
        _lastRallyMs[g!.Id] = now;
        foreach (var stale in _rallies.Where(r => r.Value.ExpiresMs <= now).Select(r => r.Key).ToArray()) _rallies.Remove(stale);
        long token = ++_nextToken;
        _rallies[token] = new Rally(g.Id, s.Role!.Id, s.SceneEntry,
            new MapPoint(s.Position.X, s.Position.Y, s.Position.Z, s.Face), now + Setting("GuildGatherInviteTime", 60)[0] * 1000);
        await PushBase(g, new GuildBaseInfoChangeNtf { Tp = GuildBaseInfoChangeNtf.Types.ChangeType.ChangeResource, Resource = Resource(g) });
        foreach (var member in g.Members.Values.Where(m => m.RoleId != s.Role.Id))
            if (Online(member.RoleId) is { } peer)
                await peer.SendGameAsync(ConveneNotify, new GuildConveneNtf { PlayerId = s.Role.Id, Token = token });
    }

    /// <summary>The checks run in the guild scope; the move itself follows it, like any other scene change.</summary>
    public async Task ConfirmConveneAsync(GameSession s, ConfirmConveneReq req)
    {
        Rally? go = null;
        await RunGuildAsync(s, [], [], async () => go = await ConfirmConveneCoreAsync(s, req));
        if (go == null) return;
        if (go.Scene == s.SceneEntry) await scene.TeleportInMapAsync(s, go.Point);
        else await scene.EnterSceneAsync(s, go.Scene, go.Point);
    }

    private async Task<Rally?> ConfirmConveneCoreAsync(GameSession s, ConfirmConveneReq req)
    {
        var g = Own(s);
        int code = !_rallies.TryGetValue(req.Token, out var rally) || g == null || rally.GuildId != g.Id
            || rally.Caller == s.Role!.Id ? ErrInvitationInvalid
            : rally.ExpiresMs < NowMs() ? ErrRallyExpired
            : (s.StatusBits & (1L << 15)) != 0 || s.SceneLoading || s.InstanceId != 0 || !GuildWhitelisted(s, 1)
            ? ErrRallyTargetArea : 0;
        await s.SendGameAsync(ConfirmConvene, new ConfirmConveneRes { Result = code });
        return code == 0 ? rally : null;
    }
}

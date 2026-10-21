using System.Text.Json;
using Romsg;
using Ro3Emu.Server.Data;

namespace Ro3Emu.Server.Game;

public sealed partial class WorldCoreService
{
    /// <summary>World Tree's paid skill keeps its immediate revive (inferred), using the shared claim/quota owner.</summary>
    private async Task<bool> ReviveItemSkillAsync(GameSession source, GameSession target, JsonElement skill)
    {
        if (!Ready(source) || !Ready(target) || source.Dead || source.WorldScene != target.WorldScene
            || _dungeons.ExternalReviveError(target) != 0 || !_combat.TryClaimRevive(target)) return false;
        try
        {
            long hp = 1000, sp = 1000;
            if (skill.TryGetProperty("_kDamageParamExtra", out var extra) && extra.ValueKind == JsonValueKind.Array)
                foreach (var row in extra.EnumerateArray())
                    if (row.ValueKind == JsonValueKind.Array && row.GetArrayLength() >= 3 && GameData.Number(row[0]) == 2)
                    {
                        if (GameData.Number(row[2]) == 101) hp = (long)(GameData.Number(row[1]) ?? 1000);
                        if (GameData.Number(row[2]) == 102) sp = (long)(GameData.Number(row[1]) ?? 1000);
                    }
            if (!_combat.CommitReviveClaim(target, hp, sp)) return false;
            _dungeons.RecordExternalRevive(target);
            await _combat.PublishReviveAsync(target, null);
            await _dungeons.PublishExternalReviveAsync(target);
            return true;
        }
        finally { _combat.ReleaseReviveClaim(target); }
    }

    /// <summary>LC_SceneCom_Revive.SendReliveHelpReq sends the dead player's own id.</summary>
    public async Task HelpAsync(GameSession s, EntityReliveHelpReq req)
    {
        long cooldown = _data.Table("PublicParamConfig").TryGetValue("SOSCD", out var value) ? (long)(GameData.Number(value) ?? 8) * 1000 : 8000;
        int error = req.TargetEntityID != s.Role!.Id ? ErrorCodes.Parameter : !s.Dead ? ErrorCodes.TargetAlive
            : !Ready(s) ? ErrorCodes.SwitchingScenes : Now - s.WorldState.LastHelpMs < cooldown ? ErrorCodes.ReviveHelpCooldown : 0;
        await s.SendGameAsync(ReliveHelp, new EntityReliveHelpResp { Result = error });
        if (error != 0) return;
        // PublicParamConfig.SOSCD; the client exposes SOS separately from a resurrection skill.
        s.WorldState.LastHelpMs = Now;
        await ToSelfAndViewersAsync(s, LifeStatus, new PlayerLifeStatusNotify { EntityId = s.Role.Id, PlayerLifeStatus = (EPlayerLifeStatus)2 });
    }

    private async Task OfferReviveAsync(GameSession source, GameSession target, JsonElement skill)
    {
        if (!target.Dead || !Ready(source) || !Ready(target) || source.WorldScene != target.WorldScene || _dungeons.ExternalReviveError(target) != 0) return;
        var state = target.WorldState;
        state.ReviveSource = source.Role!.Id;
        state.ReviveExpiresMs = Now + 30000; // Inferred invitation timeout; wire uses absolute milliseconds.
        state.ReviveHpRate = 10000; state.ReviveSpRate = 10000;
        if (skill.TryGetProperty("_kDamageParamExtra", out var extra) && extra.ValueKind == JsonValueKind.Array)
            foreach (var row in extra.EnumerateArray())
                if (row.ValueKind == JsonValueKind.Array && row.GetArrayLength() >= 3 && GameData.Number(row[0]) == 2)
                {
                    if (GameData.Number(row[2]) == 101) state.ReviveHpRate = (long)(GameData.Number(row[1]) ?? 0);
                    if (GameData.Number(row[2]) == 102) state.ReviveSpRate = (long)(GameData.Number(row[1]) ?? 0);
                }
        await target.SendGameAsync(ReliveInvitation, new EntityReliveReceiveInviteNotify
        {
            SourceEntityID = source.Role.Id, TargetEntityID = target.Role!.Id,
            Position = target.Position.Clone(), EndTimestampMs = state.ReviveExpiresMs,
        });
    }

    public async Task AcceptReviveAsync(GameSession s, EntityReliveInviteAcceptReq req)
    {
        var state = s.WorldState;
        var source = Player(req.SourceEntityID);
        int error = !s.Dead ? ErrorCodes.TargetAlive : state.ReviveSource != req.SourceEntityID || state.ReviveSource == 0 ? ErrorCodes.ReviveNotAwaited
            : state.ReviveExpiresMs <= Now ? ErrorCodes.ReviveInvitationExpired : source == null || source.Dead || !Ready(source) || !Ready(s) || source.WorldScene != s.WorldScene ? ErrorCodes.SkillReviveNotAllowed : _dungeons.ExternalReviveError(s);
        long hp = state.ReviveHpRate, sp = state.ReviveSpRate;
        bool claimed = false;
        if (error == 0 && req.IsAccept)
        {
            claimed = _combat.TryClaimRevive(s);
            if (!claimed || !_combat.CommitReviveClaim(s, hp, sp)) error = s.Dead ? ErrorCodes.FeatureUnavailable : ErrorCodes.TargetAlive;
            else _dungeons.RecordExternalRevive(s);
        }
        if (error == 0 && !req.IsAccept) { state.ReviveSource = 0; state.ReviveExpiresMs = 0; }
        try
        {
            await s.SendGameAsync(ReliveAccept, new EntityReliveInviteAcceptResp
            { Result = error, SourceEntityID = req.SourceEntityID, TargetEntityID = s.Role!.Id });
            if (error != 0) return;
            var notify = new EntityReliveInviteInfoNotify { SourceEntityID = req.SourceEntityID, TargetEntityID = s.Role.Id, IsAccept = req.IsAccept };
            await ToSelfAndViewersAsync(s, ReliveAccepted, notify);
            if (source != s && !_world.Viewers(s).Contains(source!)) await source!.SendGameAsync(ReliveAccepted, notify);
            if (req.IsAccept)
            {
                await _combat.PublishReviveAsync(s, null);
                await _dungeons.PublishExternalReviveAsync(s);
            }
        }
        finally { if (claimed) _combat.ReleaseReviveClaim(s); }
    }

    /// <summary>Map-owned item revive is separate from the ordinary return-to-resurrection-point request.</summary>
    public async Task ItemReviveAsync(GameSession s)
    {
        int error = !s.Dead ? ErrorCodes.TargetAlive : !Ready(s) ? ErrorCodes.SwitchingScenes : _dungeons.ExternalReviveError(s);
        int item = 0; long count = 0;
        if (_data.Row("MapConfig", s.SceneEntry) is { } map && map.TryGetProperty("_kItemRevive", out var cost)
            && cost.ValueKind == JsonValueKind.Array && cost.GetArrayLength() == 2)
        { item = (int)(GameData.Number(cost[0]) ?? 0); count = (long)(GameData.Number(cost[1]) ?? 0); }
        if (error == 0 && (item <= 0 || count <= 0 || count > int.MaxValue)) error = ErrorCodes.ItemReviveNotAllowed;
        bool claimed = false, committed = false;
        if (error == 0 && !(claimed = _combat.TryClaimRevive(s))) error = s.Dead ? ErrorCodes.FeatureUnavailable : ErrorCodes.TargetAlive;
        string source = error == 0
            ? $"world-revive/{_character.RewardRealmId}/{s.Role!.Id}/{s.ReviveDeathSource ??= RewardSourceId.ProcessRun + "/" + Guid.NewGuid().ToString("N")}" : "";
        try
        {
            // Do not await a bag packet after payment but before committing revival: the claim then
            // stays held and a transport failure cannot leave a paid, dead player able to pay again.
            if (error == 0)
            {
                try
                {
                    var paid = await _character.ExchangeAsync(s, [(item, count)], [], popup: false, sync: false,
                        rewardSettlement: new RewardSettlement(source, "world-revive"), committed: () =>
                        {
                            if (!_combat.CommitReviveClaim(s))
                                throw new InvalidOperationException("Paid revive lost its exclusive claim after commit.");
                            committed = true;
                            _dungeons.RecordExternalRevive(s);
                        });
                    if (paid == null) error = ErrorCodes.ItemsShort;
                }
                catch
                {
                    if (!await _character.RewardSourceCommittedAsync(source)) throw;
                    if (s.Owner?.State == Ownership.RoleOwnerState.RecoveryRequired
                        || s.Attachment?.Cancelled == true)
                    {
                        // The paid source is durable, but failed owned Apply left this attachment's
                        // bag/progress snapshot unsafe. Reconnect before any revive success projection.
                        s.CancelPendingCharacterIntents();
                        s.AbortForRecovery();
                        throw new InvalidOperationException("Paid revive needs role recovery after committed Apply fault.");
                    }
                    if (!committed)
                    {
                        if (!_combat.CommitReviveClaim(s))
                            throw new InvalidOperationException("Committed paid revive lost its exclusive claim.");
                        committed = true;
                        _dungeons.RecordExternalRevive(s);
                    }
                }
            }
            if (committed) await _character.ItemsChangedAsync(s, stats: false);
            await s.SendGameAsync(ReliveItem, new EntityReliveCostItemResp { Result = error });
            if (committed)
            {
                await _combat.PublishReviveAsync(s, null);
                await _dungeons.PublishExternalReviveAsync(s);
            }
        }
        finally { if (claimed) _combat.ReleaseReviveClaim(s); }
    }
}

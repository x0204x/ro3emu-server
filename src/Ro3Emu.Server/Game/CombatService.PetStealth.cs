using Romsg;
using Ro3Emu.Server.Data;

namespace Ro3Emu.Server.Game;

public sealed partial class CombatService
{
    private static bool GuardianStealthed(GameSession player) => player.Buffs.Any(b => b.IsGuardianOwned
        && b.ExpireMs > Now && b.ConfigId / 100 == GuardianGroup.ShinobiStealth);

    // SpecialStatusConfig 2 is stealth. (inferred) Hide from hostile viewers and monster target selection;
    // friends retain their AOI, while Scout's named detector reveals only to its owner inside its configured radius.
    internal bool GuardianCanObserve(GameSession viewer, GameSession subject)
    {
        if (viewer == subject || !GuardianStealthed(subject) || !CanPlayerHit(viewer, subject) && !CanPlayerHit(subject, viewer)) return true;
        return viewer.Buffs.Any(b => b.IsGuardianOwned && b.ExpireMs > Now && b.ConfigId / 100 == GuardianGroup.ScoutDetection
            && _data.Row("BuffConfig", b.ConfigId) is { } row
            && Distance(viewer.Position, subject.Position) <= Numbers(row, "_kLogicData").FirstOrDefault() / 100);
    }

    private readonly System.Runtime.CompilerServices.ConditionalWeakTable<GameSession, object> _guardianVisibilityOwners = [];

    private async Task GuardianVisibilityChangedAsync(GameSession player)
    {
        bool relevant = player.Buffs.Any(b => b.IsGuardianOwned && b.ExpireMs > Now && b.ConfigId / 100 is GuardianGroup.ShinobiStealth or GuardianGroup.ScoutDetection);
        if (!relevant && !_guardianVisibilityOwners.TryGetValue(player, out _)) return;
        if (relevant) _guardianVisibilityOwners.GetOrCreateValue(player);
        else _guardianVisibilityOwners.Remove(player);
        // Buff removal has already recomputed the ordinary state, so refresh even when the last detector expired.
        await _world.RefreshVisibilityAsync(player);
        foreach (var viewer in _world.Viewers(player)) await GuardianRevealStateAsync(viewer, player);
    }

    private Task GuardianRevealStateAsync(GameSession viewer, GameSession subject) => subject.Role == null
        || !GuardianStealthed(subject) || !GuardianCanObserve(viewer, subject) ? Task.CompletedTask
        : viewer.SendGameAsync(AoiAttrChange, new AOIAttrChange
        {
            EntityUuid = (ulong)subject.Role.Id,
            StateInfo = new AoiObjStateInfo { Data = new CommonBigMask { Values = { (ulong)subject.StatusBits & ~(1UL << 1) } } },
        });

    private Task EndGuardianStealthAsync(GameSession source) => _buffs.ConsumeGuardianGroupAsync(source, GuardianGroup.ShinobiStealth);

    private async Task GuardianShinobiMoveAsync(GameSession source, GuardianPetRules.Skill pet, ActiveSkillCast action)
    {
        if (GuardianPetRules.Script(pet.Effect) != "renfamizong" || !CurrentCastMap(source, action)) return;
        double distance = Numbers(pet.Active, "_kLogicData").ElementAtOrDefault(1) / 100;
        double angle = action.Request.Face * Math.PI / 180;
        var start = source.Position.Clone();
        var end = WalkableEnd(source.SceneEntry, start, (float)(Math.Sin(angle) * distance), (float)(Math.Cos(angle) * distance));
        var move = MovementEffect(GameData.Int(pet.Active, "_iScriptShow"));
        if (move.Id != 0) await ToPlayerAndViewersAsync(source, SkillMove, new SkillStartMoveNotify
        {
            EntityID = (ulong)action.OwnerId, StartPos = start, EndPos = end, SkillConfigId = pet.Id,
            EffectId = move.Id, StartTimestampMs = Now, MoveType = 1, Face = action.Request.Face,
            MoveTimeMs = Math.Max(1, move.Ms),
        });
        if (!CurrentCastMap(source, action)) return;
        source.PreviousPosition = start; source.Position = end;
        await _world.MoveAsync(source, new WalkReq { Pos = end, Dir = source.Face, Face = source.Face, BStop = true }, forceTeleport: true);
    }
}

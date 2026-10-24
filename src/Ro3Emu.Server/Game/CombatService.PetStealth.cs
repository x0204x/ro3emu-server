using Romsg;
using Ro3Emu.Server.Data;

namespace Ro3Emu.Server.Game;

public sealed partial class CombatService
{
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

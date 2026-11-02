using Ro3Emu.Server.Accounts;
using Ro3Emu.Server.Maps;

namespace Ro3Emu.Server.Game;

/// <summary>Territory and escort authorization for the shared skill-cast and event-PvP damage path.</summary>
public sealed partial class CombatService
{
    private bool CanTerritoryHit(GameSession source, GameSession target) => !ReferenceEquals(source, target)
        && source.Role != null && source.Progress != null && !source.Dead && !source.SceneLoading
        && target.Role != null && target.Progress != null && !target.Dead && !target.SceneLoading
        && source.SceneEntry != 0 && source.WorldScene == target.WorldScene
        && _sessions.Contains(source) && _sessions.Contains(target)
        && _world.PlayersIn(source.WorldScene).Contains(source) && _world.PlayersIn(source.WorldScene).Contains(target)
        && PlayerHostile?.Invoke(source, target) == true;

    private bool CanHitMonster(GameSession source, MonsterUnit target) =>
        !target.Dead && target.WorldScene == source.WorldScene
        && (MonsterTargetAllowed?.Invoke(source, target) ?? true);

    private bool CurrentPlayerHit(GameSession source, GameSession target, Role attackerRole, Role targetRole, long sceneId) =>
        ReferenceEquals(source.Role, attackerRole) && ReferenceEquals(target.Role, targetRole)
        && !source.SceneLoading && !target.SceneLoading && source.WorldScene == sceneId && target.WorldScene == sceneId
        && _sessions.Contains(source) && _sessions.Contains(target);
}

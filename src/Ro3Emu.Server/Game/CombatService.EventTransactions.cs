using Ro3Emu.Server.Maps;
using Ro3Emu.Server.Accounts;
using Ro3Emu.Server.Game.Ownership;

namespace Ro3Emu.Server.Game;

/// <summary>The event owner proved that its source did not commit, so the unpublished lethal hit may be retried.</summary>
internal sealed class EventSettlementRolledBackException(string message, Exception inner) : Exception(message, inner);
/// <summary>The source probe failed, so neither rollback nor publication is safe until it can be read.</summary>
internal sealed class EventSettlementOutcomeUnknownException(string message, Exception inner) : Exception(message, inner);

public sealed partial class CombatService
{
    /// <summary>E05e: frozen event entitlement precedes lethal wire publication; ordinary kill callbacks retain their order.</summary>
    public List<Func<GameSession, MonsterUnit, long, Task>> BeforeLethalPublication { get; } = new();
    /// <summary>Finalize a committed event death even when the initiating attachment stopped during commit.</summary>
    public List<Func<MonsterUnit, Task>> AfterLethalPublication { get; } = new();
    /// <summary>Undo unpublished combat-side scores when the event source is proven absent.</summary>
    public List<Action<MonsterUnit>> LethalHitRolledBack { get; } = new();
    internal Action<MonsterUnit, long, long>? LethalRollbackObservedForTest { get; set; }

    internal void RestoreUnpublishedLethalHit(MonsterUnit monster, long beforeHp)
    {
        monster.Hp = beforeHp;
        foreach (var handler in LethalHitRolledBack) handler(monster);
        LethalRollbackObservedForTest?.Invoke(monster, beforeHp, monster.Hp);
    }

    /// <summary>Complete the ordinary reward barrier while the original event killer is still admitted.</summary>
    internal async Task CommitRetainedLethalRewardAsync(GameSession killer, Role? role, AttachmentLease? attachment,
        long sceneGeneration, long worldScene, MonsterUnit monster, long beforeHp)
    {
        if (!monster.DeathDurable)
        {
            if (role == null || !ReferenceEquals(killer.Role, role)
                || !ReferenceEquals(killer.Attachment, attachment) || attachment?.Cancelled == true
                || killer.SceneLoading || killer.SceneGeneration != sceneGeneration
                || killer.WorldScene != worldScene || killer.WorldScene != monster.WorldScene
                || !_sessions.All().Contains(killer))
                throw new OperationCanceledException("Retained kill lost its original reward recipient.");
            await CommitMonsterDeathAsync(killer, monster, beforeHp, monster.StagedLethalDamage,
                role, attachment, sceneGeneration, worldScene);
        }
        // A prior proven refusal left the actor alive at 1 HP. Once the same
        // ordinary source is durable, restore its retained lethal transition.
        monster.Hp = 0;
    }

    /// <summary>Publish both durable sources after the original cast stops, subject to its recipient fence.</summary>
    internal async Task PublishRetainedLethalHitAsync(GameSession killer, Role? role, AttachmentLease? attachment,
        long sceneGeneration, long worldScene, MonsterUnit monster)
    {
        bool CurrentCaster() => role != null && ReferenceEquals(killer.Role, role)
            && ReferenceEquals(killer.Attachment, attachment)
            && killer.SceneGeneration == sceneGeneration && killer.WorldScene == worldScene
            && !killer.SceneLoading && _sessions.All().Contains(killer);
        if (!monster.DeathDurable) throw new InvalidOperationException("Retained death lacks its ordinary reward barrier.");
        try
        {
            await _monsters.KillAsync(monster);
            await PublishDurableKillAsync(killer, monster, CurrentCaster);
        }
        finally
        {
            // KillAsync can fail while sending the death. Once it marked the monster dead,
            // the durable event obligation still needs its independent finalizer.
            await FinalizeLethalPublicationAsync(monster);
        }
    }
    /// <summary>Durable event scores complete before their corresponding hit is published.</summary>
    public List<Func<GameSession, GameSession, long, bool, Task>> PvpDealtAsync { get; } = new();
    public List<Func<GameSession, MonsterUnit, long, Task>> EventDamageRecorded { get; } = new();
    public List<Func<GameSession, long, Task>> HealedAsync { get; } = new();
}

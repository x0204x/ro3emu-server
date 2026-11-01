namespace Ro3Emu.Server.Game;

public sealed partial class CombatService
{
    // 原命中提交前捕捉受害者；反應只依這次受害者生命期，不依攻擊者是否仍在施法。
    internal List<Func<GameSession, long, Func<bool>, Task>> PlayerTakenPublished { get; } = [];
    internal Func<GameSession, Func<Task>>? CaptureNoteLeapDeath { get; set; }

    private static Func<bool> CaptureVictimHit(GameSession target)
    {
        var current = CapturePlayerSkillTarget(target);
        var protocol = target.SkillProtocol;
        return () => current() && ReferenceEquals(target.SkillProtocol, protocol);
    }
}

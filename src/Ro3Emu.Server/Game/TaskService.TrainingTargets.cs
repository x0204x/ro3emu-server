namespace Ro3Emu.Server.Game;

public sealed partial class TaskService
{
    // G TaskConfig 10200120「木樁挑戰」目標 [210006,1]；LC_DunGeon_Pile 以
    // CTrainFinishTime 與30403結果結束挑戰。以伺服器有效計時完成計次為推定政策
    // (inferred)，不以 MaxDps > 0 代替參與，避免零傷害完成永遠無法交付。
    private const int TTrainingChallenge = 210006;

    /// <summary>挑戰結果與任務進度／待結算義務共用同一交易；重送由原 attempt guard 與任務門檻限制。</summary>
    internal CharacterService.FirstCardSightPlan? StageTrainingChallenge(GameSession session, string source) =>
        StageEventTarget(session, "training/" + source, target => target is [TTrainingChallenge, > 0]);
}

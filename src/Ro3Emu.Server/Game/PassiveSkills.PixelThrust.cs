using System.Runtime.CompilerServices;
using Ro3Emu.Server.Data.Config;
using Ro3Emu.Server.Game.Ownership;

namespace Ro3Emu.Server.Game;

public sealed partial class PassiveSkills
{
    private const int PixelThrustGrant = 11130501, PhantomThrustGroup = 11308, PixelThrustTargetMark = 1133601;
    private readonly ConditionalWeakTable<GameSession, PixelThrustMark> _pixelThrustMarks = [];

    private sealed class PixelThrustMark(GameSession source, ActiveBuff buff, SkillConfig skill,
        Func<bool> currentSource, Func<bool> currentTarget)
    {
        public GameSession Source { get; } = source;
        public ActiveBuff Buff { get; } = buff;
        public SkillConfig Skill { get; } = skill;
        public Func<bool> CurrentSource { get; } = currentSource;
        public Func<bool> CurrentTarget { get; } = currentTarget;
        public bool Consumed;
    }

    private void InitializePixelThrust(EmblemService emblems)
    {
        _buffs.Changed.Add(TrackPixelThrustMarkAsync);
        _combat.PlayerDied.Add(PixelThrustTargetDiedAsync);
        emblems.EffectsChanged.Add(RevokePixelThrustMarksAsync);
        _character.JobChanged.Add(RevokePixelThrustMarksAsync);
    }

    // 11308 mode1 的真 Buff2 為1133601/10000ms；成功附加才建立資格，不造未命中的標記。
    // 原 MultiSource=0：單一目標只保留目前的 SourceId，同 GUID 刷新仍是新 ActiveBuff 物件。
    private Task TrackPixelThrustMarkAsync(GameSession target)
    {
        var buff = target.Buffs.FirstOrDefault(b => b.ConfigId == PixelThrustTargetMark && b.ExpireMs > MonsterService.NowMs);
        if (buff == null) { _pixelThrustMarks.Remove(target); return Task.CompletedTask; }
        if (_pixelThrustMarks.TryGetValue(target, out var previous) && ReferenceEquals(previous.Buff, buff))
            return Task.CompletedTask; // 包含已消費／已撤權的舊標記，其他Buff變動不可重新武裝。
        _pixelThrustMarks.Remove(target);
        if (target.Dead || target.Role is not { } role
            || _sessions.All().FirstOrDefault(s => s.Role?.Id == buff.SourceId) is not { } source
            || !EmblemService.ResonanceSkills(source, _data).Contains(PixelThrustGrant)
            || LearnedCompanionSkill(source, PhantomThrustGroup) is not { } learned
            || _combat.EffectiveSkillRow(source, (int)learned.Id) is not { } row
            || new SkillConfig(row) is not { Mode: 1 } skill || !_combat.Hostile(source, target))
            return Task.CompletedTask;
        var sourceLease = CapturePassiveTarget(source);
        var sourceEpoch = source.SkillProtocol; var targetEpoch = target.SkillProtocol;
        var targetAttachment = target.Attachment;
        long scene = target.WorldScene, generation = target.SceneGeneration;
        bool CurrentTarget() => ReferenceEquals(target.Role, role) && ReferenceEquals(target.SkillProtocol, targetEpoch)
            && ReferenceEquals(target.Attachment, targetAttachment) && targetAttachment?.Cancelled != true
            && target.WorldScene == scene && target.SceneGeneration == generation && !target.SceneLoading
            && _sessions.All().Contains(target);
        bool CurrentSource() => sourceLease() && ReferenceEquals(source.SkillProtocol, sourceEpoch)
            && _sessions.All().Contains(source) && EmblemService.ResonanceSkills(source, _data).Contains(PixelThrustGrant)
            && LearnedCompanionSkill(source, PhantomThrustGroup) != null;
        _pixelThrustMarks.Add(target, new(source, buff, skill, CurrentSource, CurrentTarget));
        return Task.CompletedTask;
    }

    // 原文為「該玩家死亡」，不要求標記者取得擊殺。DeadClear=0 保留標記到此事件。
    // 死亡當下驗10秒期限，一次消費；異步回來源owner重置已施放mode，沿用既有公共CD下限。
    private Task PixelThrustTargetDiedAsync(GameSession target)
    {
        if (!_pixelThrustMarks.TryGetValue(target, out var mark) || mark.Consumed || !target.Dead
            || target.Role is not { } role || mark.Buff.ExpireMs <= MonsterService.NowMs
            || !target.Buffs.Any(b => ReferenceEquals(b, mark.Buff)) || !mark.CurrentSource() || !mark.CurrentTarget())
            return Task.CompletedTask;
        mark.Consumed = true;
        string? death = target.ReviveDeathSource;
        _loop.PostOwned(mark.Source, [ReservationKey.Role(role.Id)], async () =>
        {
            if (!mark.CurrentSource() || !mark.CurrentTarget() || !target.Dead || target.ReviveDeathSource != death) return;
            await CombatService.ResetOwnCooldownAsync(mark.Source, mark.Skill);
        }, "Pixel Thrust target death");
        return Task.CompletedTask;
    }

    // 卸裝／換方案／轉職撤權後，穿回不能讓已存在的標記復活；必須重新命中附加新revision。
    private Task RevokePixelThrustMarksAsync(GameSession source)
    {
        if (!EmblemService.ResonanceSkills(source, _data).Contains(PixelThrustGrant))
            foreach (var target in _sessions.All())
                if (_pixelThrustMarks.TryGetValue(target, out var mark) && ReferenceEquals(mark.Source, source))
                    mark.Consumed = true;
        return Task.CompletedTask;
    }
}

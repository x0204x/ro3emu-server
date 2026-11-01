using System.Runtime.CompilerServices;
using System.Text.Json;
using Romsg;
using Ro3Emu.Server.Data.Config;
using Ro3Emu.Server.Maps;

namespace Ro3Emu.Server.Game;

public sealed partial class PassiveSkills
{
    private const int NoteLeapGrant = 11130601, NoteLeapCounter = 1134301;
    private readonly ConditionalWeakTable<GameSession, NoteLeapState> _noteLeap = [];

    private sealed class NoteLeapState(Func<bool> current, Func<bool> identity)
    {
        public Func<bool> Current { get; } = current;
        public Func<bool> Identity { get; } = identity;
        public HashSet<ActiveSkillCast> CountedCasts { get; } = [];
    }

    private void InitializeNoteLeap(EmblemService emblems, SceneService scenes)
    {
        _combat.CastResolved.Add(NoteLeapHitAsync);
        _combat.SkillCastCompleted.Add(NoteLeapCompletedAsync);
        emblems.EffectsChanged.Add(ReconcileNoteLeapAsync);
        _character.JobChanged.Add(ReconcileNoteLeapAsync);
        scenes.AddOwnedAfterLoaded(ClearNoteLeapAsync);
        _combat.CaptureNoteLeapDeath = CaptureNoteLeapDeath;
    }

    private bool HasNoteLeap(GameSession source) => source.Role != null && !source.Dead && !source.SceneLoading
        && EmblemService.ResonanceSkills(source, _data).Contains(NoteLeapGrant)
        && LearnedCompanionSkill(source, 11310) != null && LearnedCompanionSkill(source, 11316) != null;

    private ActiveBuff? NoteLeapBuff(GameSession source) => source.Buffs.FirstOrDefault(b =>
        b.ConfigId == NoteLeapCounter && b.SourceId == source.Role?.Id && b.ExpireMs > MonsterService.NowMs);

    // 原11130601給3秒、每次+1、5層及850%；1134301是原五層強化百矛計數器。
    // (inferred) 兩者配對、音速完成後初始1層沿原Buff預設層數；不為下一發虛造額外能量。
    private async Task NoteLeapCompletedAsync(GameSession source, ActiveSkillCast cast, JsonElement row)
    {
        if (cast.Request.IsExtraSkill || !_combat.PassiveCastCurrent(source, cast)) return;
        var skill = new SkillConfig(row);
        if (skill.SkillId == 11310 && skill.Mode == 4)
        {
            // 成功完成才消費；拒絕與中斷不清音符，已接受施放的原energy成本不退款。
            await ClearNoteLeapAsync(source);
            return;
        }
        if (skill.SkillId != 11316 || !HasNoteLeap(source)
            || SkillConfig.Get(_data, NoteLeapGrant) is not { } grant || BuffConfig.Get(_data, NoteLeapCounter) == null) return;
        int duration = (int)(Value(grant.DescData, 0) * 1000);
        if (duration <= 0) return;
        var lease = CapturePassiveTarget(source); var epoch = source.SkillProtocol;
        var role = source.Role; var attachment = source.Attachment;
        long scene = source.WorldScene, generation = source.SceneGeneration;
        bool Identity() => ReferenceEquals(source.Role, role) && ReferenceEquals(source.SkillProtocol, epoch)
            && ReferenceEquals(source.Attachment, attachment) && attachment?.Cancelled != true
            && source.WorldScene == scene && source.SceneGeneration == generation && !source.SceneLoading;
        bool Current() => lease() && ReferenceEquals(source.SkillProtocol, epoch) && HasNoteLeap(source);
        _noteLeap.Remove(source);
        _noteLeap.Add(source, new(Current, Identity));
        epoch.NoteLeapReady = () => Current() && NoteLeapBuff(source)?.PileCount >= (int)Value(grant.DescData, 2);
        await _buffs.SetPileCountAsync(source, NoteLeapCounter, duration, cast.OwnerId, 1);
    }

    // 原文沒有PvE限制。每原施放只算一次非Miss命中（含全盾，原服盾語義inferred）；多目標／飄字不倍疊。
    // Miss、追加技能與過期狀態不疊；本次的死亡目標仍可提供已發布的有效命中。
    internal async Task NoteLeapHitAsync(GameSession source, ActiveSkillCast cast,
        IReadOnlyList<MonsterUnit> processed, SkillHitInfo hit)
    {
        if (cast.Request.IsExtraSkill || !_combat.PassiveCastCurrent(source, cast)
            || new SkillConfig(cast.Row) is not { SkillId: 11310, Mode: 1 or 3 }
            || !_noteLeap.TryGetValue(source, out var state) || !state.Current() || state.CountedCasts.Contains(cast)
            || NoteLeapBuff(source) is not { } buff || SkillConfig.Get(_data, NoteLeapGrant) is not { } grant
            || !hit.Dsts.Any(d => d.HitResult != DamageHitResult.Miss && processed.Any(m => m.Uid == d.DstID))
                && !cast.PublishedPlayerHits.Any(p => p.Result != DamageHitResult.Miss)) return;
        int maximum = (int)Value(grant.DescData, 2), amount = (int)Value(grant.DescData, 1);
        int duration = (int)(Value(grant.DescData, 0) * 1000);
        if (maximum <= 0 || amount <= 0 || duration <= 0) return;
        state.CountedCasts.Add(cast);
        await _buffs.SetPileCountAsync(source, NoteLeapCounter, duration, cast.OwnerId, Math.Min(maximum, buff.PileCount + amount));
    }

    private Task ReconcileNoteLeapAsync(GameSession source) => !HasNoteLeap(source)
        || _noteLeap.TryGetValue(source, out var state) && !state.Current()
        ? ClearNoteLeapAsync(source) : Task.CompletedTask;

    // 死亡入口在首個await前捕獲；晚到的舊死亡不得清掉新角色／新epoch剛建立的音符。
    internal Func<Task> CaptureNoteLeapDeath(GameSession source)
    {
        _noteLeap.TryGetValue(source, out var state);
        return () => state != null && state.Identity() && source.Dead
            && _noteLeap.TryGetValue(source, out var current) && ReferenceEquals(state, current)
            ? ClearNoteLeapAsync(source) : Task.CompletedTask;
    }

    // 原計數器SceneClear=0，不能只依該旗標把上一場景或上一角色的五層帶進新施放。
    private async Task ClearNoteLeapAsync(GameSession source)
    {
        _noteLeap.Remove(source);
        source.SkillProtocol.NoteLeapReady = null;
        var role = source.Role; var epoch = source.SkillProtocol; var attachment = source.Attachment;
        foreach (var buff in source.Buffs.Where(b => b.ConfigId == NoteLeapCounter).ToArray())
        {
            if (!ReferenceEquals(source.Role, role) || !ReferenceEquals(source.SkillProtocol, epoch)
                || !ReferenceEquals(source.Attachment, attachment)) return;
            await _buffs.RemoveAsync(source, buff);
        }
    }
}

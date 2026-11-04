using System.Runtime.CompilerServices;
using System.Text.Json;
using Romsg;
using Ro3Emu.Server.Data;
using Ro3Emu.Server.Data.Config;
using Ro3Emu.Server.Maps;

namespace Ro3Emu.Server.Game;

public sealed partial class CardEffects
{
    private sealed class SkillEventState(long role, object epoch)
    {
        public long Role = role;
        public object Epoch = epoch;
        public bool Healing;
        public Dictionary<int, long> ResetReady { get; } = [];
        public Dictionary<long, long> ProcReady { get; } = [];
        public HashSet<long> EvaluatedCasts { get; } = [];
        public Queue<long> CastOrder { get; } = [];
    }
    private readonly ConditionalWeakTable<GameSession, SkillEventState> _skillEvents = [];
    private SkillEventState SkillEvents(GameSession s)
    {
        if (!_skillEvents.TryGetValue(s, out var state) || state.Role != s.Role!.Id || !ReferenceEquals(state.Epoch, s.SkillProtocol))
            _skillEvents.AddOrUpdate(s, state = new(s.Role!.Id, s.SkillProtocol));
        return state;
    }

    private bool SkillEventReady(GameSession source, long family, long cooldown)
    {
        var state = SkillEvents(source);
        long now = MonsterService.NowMs;
        if (state.ProcReady.GetValueOrDefault(family) > now) return false;
        state.ProcReady[family] = now + cooldown;
        return true;
    }

    private void InitializeSkillEvents()
    {
        _combat.DealtAsync.Add(DrainMonsterSpAsync);
        _combat.PvpSkillHitPublished.Add(DrainPlayerSpAsync);
        _combat.SkillCastCompleted.Add(ResetAfterCastAsync);
        _combat.HealingApplied.Add(HealAfterHealingAsync);
    }

    private bool IsCardReaction(GameSession s, JsonElement row) => RuntimeFor(s).ResolvingAutocast
        || SkillEvents(s).Healing || row.TryGetProperty("_ro3CardReflection", out var reflection) && reflection.ValueKind == JsonValueKind.True;

    private long CardSpBurn(GameSession source, JsonElement row, long damage, long currentSp)
    {
        if (source.Role == null || source.Dead || damage <= 0 || currentSp <= 0 || IsCardReaction(source, row)) return 0;
        foreach (var (basis, group, chance, desc) in Procs([.. Passives(source)]))
        {
            if (group != 20230 || desc.Length < 4 || new BuffConfig(basis).LogicData is not [0, var skillId]
                || SkillConfig.Get(_data, skillId) is not { ScriptLogic: "ReduceMP_Card" } skill) continue;
            var logic = skill.LogicData;
            if (logic.Length < 2 || Roll(10000) >= chance || !SkillEventReady(source, group, (long)(desc[3] * 1000))) continue;
            // Skill 2003101: 目前 SP 20%，上限 400；整數向下取整為本地可追溯政策。
            return Math.Min(currentSp, Math.Min((long)logic[1], currentSp * (long)logic[0] / 100));
        }
        return 0;
    }

    internal async Task DrainMonsterSpAsync(GameSession source, MonsterUnit target, JsonElement row, long damage)
    {
        if (target.Dead || target.Hp <= 0) return;
        long amount = CardSpBurn(source, row, damage, target.CurrentSp);
        if (amount <= 0) return;
        target.CurrentSp -= amount;
        var notice = new AOIAttrChange
        {
            EntityUuid = (ulong)target.Uid,
            Property = { new AoIProperty { Idx = 2001, Data = target.CurrentSp, BaseData = target.CurrentSp } }
        };
        foreach (var viewer in target.Viewers.ToArray()) await viewer.SendGameAsync(CombatService.AoiAttrChange, notice);
    }

    internal async Task DrainPlayerSpAsync(GameSession source, GameSession target, JsonElement row, long damage, DamageHitResult result)
    {
        if (source.Role == null || target.Role == null || target.Dead || result == DamageHitResult.Miss) return;
        var sourceRole = source.Role; var targetRole = target.Role;
        var sourceLease = source.Attachment; var targetLease = target.Attachment;
        long sourceGeneration = source.SceneGeneration, targetGeneration = target.SceneGeneration, scene = source.WorldScene;
        using var held = await CharacterService.Lock(target);
        if (!ReferenceEquals(source.Role, sourceRole) || !ReferenceEquals(target.Role, targetRole)
            || !ReferenceEquals(source.Attachment, sourceLease) || sourceLease?.Cancelled == true
            || !ReferenceEquals(target.Attachment, targetLease) || targetLease?.Cancelled == true
            || source.SceneGeneration != sourceGeneration || target.SceneGeneration != targetGeneration
            || source.Dead || target.Dead || source.SceneLoading || target.SceneLoading
            || source.WorldScene != scene || target.WorldScene != scene) return;
        long current = _character.Stats(target)[2001], amount = CardSpBurn(source, row, damage, current);
        if (amount > 0) await _character.SetHpSpAsync(target, null, current - amount);
    }

    internal async Task ResetAfterCastAsync(GameSession source, ActiveSkillCast cast, JsonElement cooldownRow)
    {
        if (source.Role == null || source.Dead || IsCardReaction(source, cooldownRow)) return;
        var skill = new SkillConfig(cooldownRow);
        long cooldown = skill.Cd;
        int group = (int)skill.SkillId;
        if (cooldown <= 0 || group <= 0) return;
        var passive = Passives(source).FirstOrDefault(p => GameData.Int(p.Row, "_iBuffID") == 20529);
        if (passive == null) return;
        var logic = new BuffConfig(passive.Row).LogicData;
        var desc = Description(passive.Row);
        var state = SkillEvents(source);
        if (!state.EvaluatedCasts.Add(cast.Id)) return;
        state.CastOrder.Enqueue(cast.Id);
        while (state.CastOrder.Count > 1024) state.EvaluatedCasts.Remove(state.CastOrder.Dequeue());
        long now = MonsterService.NowMs;
        if (logic.Length < 3 || desc.Length < 2 || state.ResetReady.GetValueOrDefault(group) > now || Roll(10000) >= logic[0]) return;
        state.ResetReady[group] = now + (long)(cooldown * desc[1]);
        await CombatService.ResetOwnCooldownAsync(source, skill);
    }

    internal async Task HealAfterHealingAsync(GameSession source, GameSession recipient, JsonElement row, long actual)
    {
        if (source.Role == null || recipient.Role == null || source.Dead || recipient.Dead || source.SceneLoading
            || recipient.SceneLoading || source.WorldScene != recipient.WorldScene || actual <= 0
            || new SkillConfig(row).SkillId <= 0 || IsCardReaction(source, row)) return;
        var passive = Passives(source).FirstOrDefault(p => GameData.Int(p.Row, "_iBuffID") == 20531);
        if (passive == null) return;
        var logic = new BuffConfig(passive.Row).LogicData;
        var desc = Description(passive.Row);
        if (logic is not [var chance, var skillId] || desc.Length < 2 || SkillConfig.Get(_data, skillId) is not { } heal
            || Roll(10000) >= chance || !SkillEventReady(source, 20531, (long)(desc[1] * 1000))) return;
        var state = SkillEvents(source);
        state.Healing = true;
        try
        {
            await _combat.CastExtraOnAlliesAsync(source, heal.Row,
                ReferenceEquals(source, recipient) ? [source] : [recipient, source]);
        }
        finally { state.Healing = false; }
    }
}

using System.Runtime.CompilerServices;
using Romsg;
using Ro3Emu.Server.Data;
using Ro3Emu.Server.Data.Config;
using Ro3Emu.Server.Maps;

namespace Ro3Emu.Server.Game;

public sealed partial class CombatService
{
    private sealed class GloriaTargets(Func<bool> sourceCurrent)
    {
        public Func<bool> SourceCurrent { get; } = sourceCurrent;
        public Dictionary<object, Func<bool>> Targets { get; } = new(ReferenceEqualityComparer.Instance);
        public HashSet<long> Applied { get; } = [];
    }

    private readonly ConditionalWeakTable<ActiveSkillCast, GloriaTargets> _gloriaTargets = [];

    private static int GloriaSp(SkillConfig row) => row.SkillId == 11501 && row.ScriptLogic == "GloriaDomini"
        && int.TryParse(row.DescData.ElementAtOrDefault(3), out int amount) ? Math.Max(0, amount) : 0;

    internal void CaptureGloriaDominiTargets(GameSession source, ActiveSkillCast cast,
        IEnumerable<MonsterUnit> monsters, IEnumerable<GameSession> players)
    {
        if (cast.Request.IsExtraSkill || GloriaSp(new SkillConfig(cast.Row)) <= 0) return;
        var sourceCurrent = CaptureVictimHit(source);
        var state = _gloriaTargets.GetValue(cast, _ => new(() => sourceCurrent() && CurrentCastMap(source, cast)));
        foreach (var player in players)
            state.Targets.TryAdd(player, CaptureVictimHit(player));
        foreach (var monster in monsters)
        {
            long scene = monster.WorldScene;
            string generation = monster.SpawnGeneration;
            state.Targets.TryAdd(monster, () => !monster.Removed && !monster.Dead && monster.Hp > 0
                && monster.WorldScene == scene && scene == cast.WorldScene && monster.SpawnGeneration == generation
                && _world.Map(scene).Contains(monster));
        }
    }

    internal async Task GloriaDominiSpAsync(GameSession source, ActiveSkillCast cast,
        IReadOnlyList<MonsterUnit> monsters, SkillHitInfo hit)
    {
        if (!_gloriaTargets.TryGetValue(cast, out var state) || !state.SourceCurrent()) return;
        var skill = new SkillConfig(cast.Row);
        int amount = GloriaSp(skill);
        if (amount <= 0) return;
        foreach (var target in monsters)
        {
            var result = hit.Dsts.FirstOrDefault(d => d.DstID == target.Uid);
            if (result == null || result.HitResult == DamageHitResult.Miss || !state.SourceCurrent()
                || !state.Targets.TryGetValue(target, out var current) || !current()
                || !state.Applied.Add(target.Uid) || _buffs.ResourceDrainImmune(target)
                || !RollGloriaResource(skill, source, null, target)) continue;
            target.CurrentSp = Math.Max(0, target.CurrentSp - amount);
            var notice = new AOIAttrChange
            {
                EntityUuid = (ulong)target.Uid,
                Property = { new AoIProperty { Idx = 2001, Data = target.CurrentSp, BaseData = target.CurrentSp } },
            };
            foreach (var viewer in target.Viewers.ToArray())
            {
                if (!state.SourceCurrent() || !current()) break;
                await viewer.SendGameAsync(AoiAttrChange, notice);
            }
        }
        foreach (var published in cast.PublishedPlayerHits)
        {
            var target = published.Target;
            if (published.Result == DamageHitResult.Miss || !state.SourceCurrent()
                || !state.Targets.TryGetValue(target, out var current) || !current() || !published.Current()
                || !state.Applied.Add(published.TargetId)) continue;
            using var held = await CharacterService.Lock(target);
            if (!state.SourceCurrent() || !current() || !published.Current() || !CanPlayerHit(source, target)
                || _buffs.ResourceDrainImmune(target) || !RollGloriaResource(skill, source, target, null)) continue;
            long before = _character.Stats(target)[2001];
            await _character.SetHpSpAsync(target, null, Math.Max(0, before - amount));
        }
    }

    private bool RollGloriaResource(SkillConfig skill, GameSession source, GameSession? player, MonsterUnit? monster)
    {
        // 保留原Buff2 tuple機率。1150801..10不存在，因此不建立假的ActiveBuff。
        foreach (var tuple in Tuples(skill.Row, "_kBuff2"))
            if (tuple.GetArrayLength() >= 3 && GameData.Number(tuple[0]) == 1150800 + skill.Level)
                return RollHostileBuff(tuple, source, null, player, monster, out _, out _);
        return false;
    }
}

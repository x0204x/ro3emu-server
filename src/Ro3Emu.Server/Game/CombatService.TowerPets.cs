using System.Text.Json;
using Romsg;
using Ro3Emu.Server.Data;
using Ro3Emu.Server.Maps;

namespace Ro3Emu.Server.Game;

public sealed partial class CombatService
{
    /// <summary>
    /// Synchronous monster damage modifiers (TowerService.PowerSuppress): a pet's hit on a monster (false), a monster's hit
    /// on a pet or a player (true), after the monster's DamageRatio. A player's hit on a monster uses DamageAdjusters.
    /// </summary>
    internal List<Func<MonsterUnit, long, bool, long>> MonsterDamageAdjusters { get; } = new();

    /// <summary>(inferred) Use the existing attribute-group calculator for a pet's configured stats and live buffs.</summary>
    internal StatSheet TowerPetStats(TowerPetUnit pet)
    {
        var raw = pet.BaseAttributes.ToDictionary(p => p.Key, p => p.Value);
        foreach (var (id, value) in _buffs.UnitAttributes(pet)) raw[(int)id] = raw.GetValueOrDefault((int)id) + value;
        var stats = StatCalculator.Compute(raw, _data);
        stats[101] = Math.Max(1, stats[101]);
        stats[2000] = pet.Hp;
        stats[2001] = stats[102];
        stats[2002] = pet.Level;
        return stats;
    }

    internal void PopulateTowerPetBuffs(TowerPetUnit pet, EntityBase entity) => _buffs.PopulateEntity(pet, entity);

    private Task PublishTowerPetStatsAsync(WorldUnit unit)
    {
        if (unit is not TowerPetUnit pet) return Task.CompletedTask;
        var changed = new AOIAttrChange { EntityUuid = (ulong)pet.Uid };
        var stats = pet.CombatStats;
        // (inferred) Clear expired members too: a viewer can have received e.g. 1032 in a late AOI snapshot.
        foreach (int id in stats.Data.Keys.Union(pet.PublishedAttributeIds).Order().ToArray())
        {
            changed.Property.Add(new AoIProperty { Idx = id, Data = stats[id], BaseData = stats.Base.GetValueOrDefault(id) });
            pet.PublishedAttributeIds.Add(id);
        }
        return _world.ToUnitViewersAsync(pet, AoiAttrChange, changed);
    }

    private bool LiveTowerPet(GameSession owner, TowerPetUnit pet) => ReferenceEquals(owner, pet.Owner)
        && owner.Role != null && !owner.Dead && !owner.SceneLoading && !pet.Dead && pet.Hp > 0
        && owner.WorldScene == pet.WorldScene && owner.TowerAttempt is { Loaded: true, Settled: false } a
        && a.InstanceId == pet.InstanceId && a.Pets.Contains(pet);

    /// <summary>
    /// (verified) SkillConfig/Localization_en distinguish Bobo Bubble and the named allied heal/buff skills
    /// from attacks; _kBuff1 tuples give buff ID, chance and duration. (inferred) The owner and living tower
    /// pets are allies; select self then nearest recipients, capped by _iTargetMax, within _kRange[0].
    /// Original server AI and hit timings are unavailable; use the same 40%-action timing as tower attacks.
    /// </summary>
    public async Task TowerPetSupportAsync(GameSession owner, TowerPetUnit pet, int skillId)
    {
        if (!LiveTowerPet(owner, pet) || _data.Row("SkillConfig", skillId) is not { } row
            || PetCombatRules.SupportFor(row) is not { } support
            || pet.SupportSkills.FirstOrDefault(s => s.Id == skillId) is not { } skill
            || !pet.TryBeginCast(skill, Now)) return;
        var role = owner.Role;
        long castId = ++_sessionIds;
        int actionMs = pet.ActionMsFor(skill);
        var use = new UseInSkillInfo
        {
            SrcID = pet.Uid, SkillID = skillId, PetConfigId = pet.PetId, TargetId = pet.Uid,
            TargetPos = pet.Pos, Face = pet.Face, SessionId = castId, SkillMs = actionMs, StartTime = Now,
        };
        await _world.ToUnitViewersAsync(pet, UseInSkill, use);
        if (!pet.Viewers.Contains(owner)) await owner.SendGameAsync(UseInSkill, use);
        ScheduleRuntime(pet.WorldScene, TimeSpan.FromMilliseconds(actionMs * 0.4), async () =>
        {
            if (!LiveTowerPet(owner, pet) || !ReferenceEquals(role, owner.Role)) return;
            float radius = (float)(Numbers(row, "_kRange").FirstOrDefault() / 100);
            int maximum = (int)Math.Max(1, GameData.Int(row, "_iTargetMax"));
            // (verified) Choco's target count is ${2}=10 in its description, while _iTargetMax=1.
            // (inferred) Prefer the explicit allied count for this conflicting row, rather than self-only casting.
            if (Script(row) == "PetSkill_1_QiaoKeHou") maximum = (int)Numbers(row, "_kDescData").ElementAtOrDefault(1);
            var recipients = owner.TowerAttempt!.Pets.Where(p => !p.Dead && p.Hp > 0 && p.WorldScene == pet.WorldScene
                    && (p == pet || !support.SelfOnly && MonsterService.Distance(pet.X, pet.Z, p.X, p.Z) <= radius))
                .Select(p => (Id: p.Uid, Pet: (TowerPetUnit?)p, Distance: MonsterService.Distance(pet.X, pet.Z, p.X, p.Z))).ToList();
            if (!support.SelfOnly && MonsterService.Distance(pet.X, pet.Z, owner.Position.X, owner.Position.Z) <= radius)
                recipients.Add((owner.Role!.Id, null, MonsterService.Distance(pet.X, pet.Z, owner.Position.X, owner.Position.Z)));
            var casterStats = pet.CombatStats;
            var hit = new SkillHitInfo
            {
                SrcID = pet.Uid, SkillId = skillId, SessionId = castId, SrcElement = pet.Element,
                ShowPoint = HitEffect(GameData.Int(row, "_iScriptShow"))
            };
            foreach (var recipient in recipients.OrderBy(r => r.Id == pet.Uid ? 0 : 1)
                         .ThenBy(r => r.Distance).ThenBy(r => r.Id).Take(maximum))
            {
                if (!LiveTowerPet(owner, pet) || !ReferenceEquals(role, owner.Role)) return;
                var stats = recipient.Pet?.CombatStats ?? _character.Stats(owner);
                foreach (var tuple in Tuples(row, "_kBuff1"))
                {
                    if (!RollBuff(tuple, out int id, out int duration) || _data.Row("BuffConfig", id) is not { } buff) continue;
                    long shield = PetCombatRules.AttributeAmount(buff, casterStats, stats);
                    if (recipient.Pet is { } ally) await _buffs.AddSupportAsync(ally, id, duration, pet.Uid, shield);
                    else await _buffs.AddSupportAsync(owner, id, duration, pet.Uid, shield);
                }
                long amount = support.Kind switch
                {
                    PetCombatRules.SupportKind.AttributeHeal => PetCombatRules.AttributeAmount(row, casterStats, stats),
                    PetCombatRules.SupportKind.MagicHeal => Math.Max(0, (long)Math.Floor(casterStats[104]
                        * Numbers(row, "_kDamageParam1").FirstOrDefault() / 10000
                        + Numbers(row, "_kDamageParam2").FirstOrDefault())),
                    _ => 0,
                };
                if (support.Kind == PetCombatRules.SupportKind.Buff) continue;
                long actual = Math.Min(amount, Math.Max(0, stats[101] - stats[2000]));
                if (recipient.Pet is { } healed)
                {
                    healed.Hp += actual;
                    await _world.ToUnitViewersAsync(healed, AoiAttrChange, healed.HpChange());
                }
                else
                {
                    using var held = await CharacterService.Lock(owner);
                    if (owner.Dead || owner.SceneLoading || !ReferenceEquals(role, owner.Role)) return;
                    var current = _character.Stats(owner);
                    actual = Math.Min(amount, Math.Max(0, current[101] - current[2000]));
                    await _character.SetHpSpAsync(owner, current[2000] + actual, null);
                }
                hit.Dsts.Add(new DamageInfo { DstID = recipient.Id, Damage = actual, DamageType = DamageType.Heal });
                foreach (var handler in Healed) handler(owner, actual);
                foreach (var handler in HealedAsync) await handler(owner, actual);
            }
            if (hit.Dsts.Count > 0)
                foreach (var viewer in pet.Viewers.Append(owner).Distinct()) await viewer.SendGameAsync(SkillHit, hit);
        });
    }

    /// <summary>
    /// (verified) BuffConfig uses 1216 for Alice's reduction, 1230 for Goat's ranged physical reduction,
    /// and 315 for Squidgitte's water resistance. (inferred) Apply their live sum multiplicatively before
    /// shields; this is restricted to pet support contributions rather than changing all combat attributes.
    /// </summary>
    private static long ApplyPetSupportResistance(long damage, IEnumerable<(long Id, double Value)> changes,
        JsonElement? skill, int fallbackElement)
    {
        if (damage <= 0) return damage;
        var values = changes.GroupBy(p => p.Id).ToDictionary(g => g.Key, g => g.Sum(p => p.Value));
        double reduction = values.GetValueOrDefault(1216);
        if (skill is { } row)
        {
            if (GameData.Int(row, "_iDistanceType") == 2 && GameData.Int(row, "_iDmgType") == 1)
                reduction += values.GetValueOrDefault(1230);
            int element = (int)GameData.Int(row, "_iEleType");
            if ((element > 1 ? element : fallbackElement) == 5) reduction += values.GetValueOrDefault(315);
        }
        return Math.Max(0, (long)Math.Round(damage * Math.Clamp(1 - reduction / 10000, 0, 10)));
    }
}

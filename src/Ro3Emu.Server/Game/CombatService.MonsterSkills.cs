using System.Text.Json;
using Romsg;
using Ro3Emu.Server.Data;
using Ro3Emu.Server.Maps;

namespace Ro3Emu.Server.Game;

public sealed partial class CombatService
{
    /// <summary>
    /// A monster skill landing on players (MVP minion blasts, boss ground fields): the row's damage from the caster's
    /// stats (the same inferred formula as a monster swing), the protected HP transition, one 31101 per player from
    /// <paramref name="sourceId"/> (the caster or its ground object) and the row's _kBuff2 on each surviving player.
    /// Returns the number of players hit. A boss special passes its cast's <paramref name="session"/>, its damage percent
    /// (BossSkillOptions.DamagePercent) and <paramref name="bossDebuffsOnly"/>: a _kBuff2 without a positive time is an
    /// encounter script's marker (Maya's 4340101, -1 ms) that the script also removed, and one of BuffConfig _iType 1
    /// (a buff) is the boss's own (inferred); neither goes on the players.
    /// </summary>
    internal async Task<int> MonsterSkillHitAsync(MonsterUnit caster, JsonElement row, IEnumerable<GameSession> targets, long sourceId,
        long session = 0, int damagePercent = 100, bool bossDebuffsOnly = false)
    {
        if (caster.Dead || caster.Hp <= 0) return 0;
        int skillId = (int)GameData.Int(row, "_iID");
        if (session <= 0) session = ++_sessionIds;
        int show = HitEffect(GameData.Int(row, "_iScriptShow"));
        int element = (int)GameData.Int(row, "_iEleType") is var e and > 1 ? e : caster.Template.Element;
        var attacker = new Fighter(caster.Template.Atk, caster.Template.Matk, caster.Template.Hit, caster.Template.Cri,
            caster.Template.Level, caster.Template.Element);
        int hits = 0;
        foreach (var p in targets.ToArray())
        {
            var targetRole = p.Role;
            if (targetRole == null || p.Dead || p.SceneLoading || p.Progress == null || p.WorldScene != caster.WorldScene) continue;
            long scene = p.WorldScene, generation = p.SceneGeneration;
            var lease = p.Attachment;
            bool Current() => ReferenceEquals(p.Role, targetRole) && p.WorldScene == scene && p.SceneGeneration == generation
                && ReferenceEquals(p.Attachment, lease) && lease?.Cancelled != true;
            var stats = character.Stats(p);
            var defender = new Fighter(0, 0, 0, 0, p.Progress.Level, 1) { Def = stats[105], Mdef = stats[106], Flee = stats[110], ResCri = stats[112] };
            var (damage, result) = Damage(attacker, defender, row);
            damage = ScaleMonsterDamage(damage, caster.DamageRatio);
            if (damagePercent != 100) damage = damagePercent <= 0 ? 0 : ScaleMonsterDamage(damage, damagePercent * 100);
            foreach (var adjust in MonsterDamageAdjusters) damage = adjust(caster, damage, true);
            if (damage > 0) foreach (var modifier in IncomingDamageAdjusters) damage = Math.Max(0, await modifier(caster, p, result, damage));
            var committed = await CommitPlayerDamageAsync(p, damage, row, Current, sourceElement: element);
            if (committed == null) continue;
            (damage, long hp, _, long shieldDamage) = committed.Value;
            hits++;
            try
            {
                foreach (var handler in Taken) handler(caster, p, damage, hp == 0);
                var hit = new SkillHitInfo
                {
                    SrcID = sourceId, SkillId = skillId, ShowPoint = show, SessionId = session, SrcElement = element,
                    Dsts = { new DamageInfo { DstID = targetRole.Id, Damage = damage, SheildDamage = shieldDamage, HitResult = result,
                        DstElement = 1, IsDead = hp == 0 } },
                };
                foreach (var viewer in world.Viewers(p).Append(p).Distinct()) await viewer.SendGameAsync(SkillHit, hit);
                await character.SetHpSpAsync(p, null, null);
                foreach (var handler in TakenAsync) await handler(caster, p, damage);
                await FightingAsync(p);
                if (hp == 0)
                {
                    await PlayerDiesAsync(p);
                    continue;
                }
                foreach (var tuple in Tuples(row, "_kBuff2"))
                    if (Current() && (!bossDebuffsOnly || BossDebuff(tuple)) && RollHostileBuff(tuple, null, caster, p, null, out int buff, out int ms)) await buffs.AddAsync(p, buff, ms, caster.Uid);
            }
            finally { if (hp == 0) ReleaseDeathPublication(p); }
        }
        return hits;
    }

    private bool BossDebuff(JsonElement tuple) => tuple.GetArrayLength() >= 3 && GameData.Number(tuple[2]) > 0
        && GameData.Number(tuple[0]) is double id && !(data.Row("BuffConfig", (long)id) is { } buff && GameData.Int(buff, "_iType") == 1);
}

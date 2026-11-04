using System.Text.Json;
using Ro3Emu.Server.Data.Config;

namespace Ro3Emu.Server.Game;

public sealed partial class CombatService
{
    /// <summary>
    /// Skills that keep dealing damage while their caster walks: the description's total is the row's ratio times the show's
    /// attack points after the first. Myriad Stars Chase the Moon (Lv. 5): [20000], points 0, 0.5, 0.7 ... 1.7, "PATK * 1400%".
    /// Aerial Bombardment (Lv. 3): [30000], points 0.017, 1.65, 1.83 ... 2.67, "totaling PATK * 2100%". Other multi-point
    /// shows are single hits as far as their descriptions tell, so this is by name, as GuardianPetRules.Pulses (inferred).
    /// </summary>
    private static readonly HashSet<string> ChannelScripts = ["VolleyArrows", "HighAltitudeBombing", "ShieldsProtection"];

    /// <summary>
    /// Skills whose text gives a total of one hit per attack point, every point dealing: Spinning Axe [6000] on 2 points, "2 hits
    /// totaling PATK * 120%". Piercing Shadow [11000,11000] on 2, "a total of PATK*220%". Spear Formation - Crescent Moon [3000]
    /// on 3, "hits of PATK * 30%". Earthshaker's spinning row [1000,25000] on 11, "a total of PATK * 350%": ten of 10%, then 250%;
    /// the rows that name their hits another way are <see cref="StagedHits"/>. Other multi-point rows read as one hit (inferred).
    /// </summary>
    private static bool HitsEveryPoint(SkillConfig skill, int points) => points > 1 && skill.SkillId switch
    {
        16306 or 15326 or 11628 => true,
        11624 => points >= 11,
        // Rapid Throw's mode row, whose show 111101 adds a second point to Spear Throw's: "you throw one Extra Spear".
        11110 => skill.ScriptLogic == "BoomerangBD01",
        _ => false,
    };

    /// <summary>Water, Fire, Earth, Wind: "sequentially deals" (the bolts' _iEleType name them).</summary>
    private static readonly long[] TetraElements = [5, 4, 3, 2];

    /// <summary>Tetra Vortex's _kBuff2 is [Stun, Burn, Petrify, Frozen, the guarantee]; the hit each element gives, in that order.</summary>
    private static readonly int[] TetraStatuses = [3, 1, 2, 0];

    /// <summary>
    /// How many hits a row's text total comes to, for rows whose ratio is one hit's and whose data holds no count. The total
    /// over the ratio is the same at every level: Axe Tornado 750 = 6 x 100 + 150 at Lv. 1, 1252 = 6 x 167 + 250 at Lv. 5;
    /// Arm Cannon "4 shells totaling 40%" is 2 hits of 20% in 2 damage words each. Spacing them over the show is (inferred).
    /// </summary>
    private static int HitsByText(string script) => script switch
    {
        "AxeTornado" or "AxeTornadoS1BD1" or "AxeTornadoS1BD2" => 6,
        "AxeTornadoPro" => 10,
        "AxeTornadoProS1BD1" or "AxeTornadoProS1BD2" => 12,
        "ArmCannon" => 2,
        "ArmCannonPro" or "ArmCannonS1BD1" or "ArmCannonProS1BD2" => 4,
        _ => 0,
    };

    /// <summary>
    /// Rows whose hits are not one per point. _kDamageParam1 holds one value per stage, and a stage's _kShowDmg only divides it
    /// into words: Sonic Wave's client text reads "3 segments totaling" its one value. So Soul Expansion [40%,40%] is two hits,
    /// Gravitational Impact [66.5%,133%] and Fearless Charge [30%,120%] a hit and a later one. Tetra Vortex holds four values of
    /// the number its text calls a total: the row counts (inferred), four hits of it, each with its element and its own 50%.
    /// Points past the row's stages belong to another form of the show, and a stage's point is (inferred).
    /// </summary>
    private static (double Point, JsonElement Row)[]? StagedHits(SkillConfig skill, double[] all)
    {
        string script = skill.ScriptLogic;
        var ratios = skill.DamageParam1;
        if (all.Length == 0 || ratios.Length == 0) return null;
        var plain = new SkillConfig(skill.WithoutBuff1());
        JsonElement Damage(long ratio) => plain.WithDamage([ratio], skill.DamageParam2);
        if (script.StartsWith("TetraVortex", StringComparison.Ordinal) && ratios.Length >= 4 && all.Length >= 4
            && skill.Buff2 is { Length: >= 5 } statuses)
            return [.. Enumerable.Range(0, 4).Select(i => (all[i], new SkillConfig(Damage(ratios[i])).AsStage(
                element: TetraElements[i], statuses: [statuses[TetraStatuses[i]]], guaranteed: i == 3 ? statuses[4] : null)))];
        if (script.StartsWith("SoulExpansion", StringComparison.Ordinal) && ratios.Length >= 2 && all.Length >= 2)
            return [(all[0], Damage(ratios[0])), (all[1], Damage(ratios[1]))];
        if ((script.StartsWith("VectorShock", StringComparison.Ordinal) || script == "PinpointAttack2") && ratios.Length >= 2 && all.Length >= 2)
        {
            var words = skill.ShowDmg;
            return [(all[0], Damage(ratios[0])), (all[^1], new SkillConfig(Damage(ratios[1])).AsStage(
                words: words.Length > 1 ? words[1] : 0, still: script == "PinpointAttack2"))];
        }
        if (HitsByText(script) is not (> 0 and var hits)) return null;
        var tick = Damage(ratios[0]);
        if (ratios.Length > 1 && all.Length >= 2) // the ticks up to the last point, which deals the second ratio
        {
            double from = all[1], step = (all[^1] - from) / hits;
            var final = new SkillConfig(Damage(ratios[1])).AsStage(words: skill.ShowDmg.Length > 1 ? skill.ShowDmg[1] : 0);
            return [.. Enumerable.Range(0, hits).Select(i => (from + i * step, tick)), (all[^1], final)];
        }
        if (all.Length == hits) return [.. all.Select(point => (point, tick))];
        if (all.Length == hits + 1) return [.. all.Skip(1).Select(point => (point, tick))];
        double gap = (all[^1] - all[0]) / Math.Max(1, hits - 1);
        return [.. Enumerable.Range(0, hits).Select(i => (all[0] + i * gap, tick))];
    }

    /// <summary>
    /// A channelled row's hits: the show's point and the row it deals. The first point only opens the channel (inferred: no
    /// total above counts it). A second ratio is the last point's: Myriad Stars' talent row [20000,15000] has 11 hits, ten
    /// of 200% for its "2000" and "When the skill ends, it deals one instance of 150%" (SkillConfig 11336301). A second flat
    /// goes with it: Rapid Throw's [ratio, 5000] / [flat, 220], "PATK * 50% + 220".
    /// </summary>
    internal static (double Point, JsonElement Row)[] ChannelHits(JsonElement row, JsonElement? show)
    {
        var skill = new SkillConfig(row);
        double[] all = [.. SkillShow.AttackPoints(show)];
        if (StagedHits(skill, all) is { } hits) return hits;
        bool staged = HitsEveryPoint(skill, all.Length);
        if (!staged && !ChannelScripts.Contains(Script(row))) return [];
        double[] points = staged ? all : [.. all.Skip(1)];
        if (points.Length == 0) return [];
        var plain = skill.WithoutBuff1();
        var last = skill.DamageParam1 is [_, var final, ..]
            ? new SkillConfig(plain).WithDamage([final], skill.DamageParam2 is [_, var flat, ..] ? [flat] : skill.DamageParam2) : plain;
        // Earthshaker: "The final HIT inflicts Knockback", and only it.
        var hit = skill.ScriptLogic == "PinpointAttack3" ? new SkillConfig(plain).AsStage(still: true) : plain;
        return [.. points.Select((point, index) => (point, index == points.Length - 1 ? last : hit))];
    }

    /// <summary>
    /// The first attack point of a channelled cast: its caster buffs, then one effect pass per later point while the cast
    /// lives (moving is allowed, _bMovement 1; an interrupt or the next cast ends it). The area follows the caster and keeps
    /// the cast's aim (inferred: the stretched main effect 1332003 is bound to the moving DRoot). Procs of "after using" a
    /// skill see the first hit only, so they fire once per cast.
    /// </summary>
    private async Task<bool> StartChannelAsync(GameSession s, ActiveSkillCast action)
    {
        // An authored cast already owns its per-point deadlines. In particular, Tetra's four authored
        // stages must not each start a second four-hit channel from the same original cast.
        if (action.AuthoredStageCount > 0) return false;
        var show = _data.SkillTemplate(new SkillConfig(action.Row).ScriptShow);
        var hits = ChannelHits(action.Row, show);
        if (hits.Length == 0) return false;
        action.AuthoredStageCount = hits.Length; // 保留施放生命期，直到最後一段與完成事件結束。
        foreach (var buff in new SkillConfig(action.Row).Buff1)
            if (buff is [var id, var chance, var duration, ..] && id > 0 && Rolls.Proc(10000) < chance
                && SkillBuffAllowed?.Invoke(s, s, (int)id) != false)
                await _buffs.AddAsync(s, (int)id, (int)duration, s.Role!.Id);
        // 沿上游的各攻擊點與傷害列排程；逐段串接可避免等待交易時同一施放重入。
        void Schedule(int index) => ScheduleCastDeadline(action,
            ImpactOffset(show, hits[index].Point, action.ChantMs, action.ActionMs), async () =>
            {
                if (!Live(s, action)) return;
                TraceImpact(action, $"channel-{index}-resolve-entered");
                await ChannelHitAsync(s, action, hits[index].Row, index == 0);
                if (!Live(s, action)) return;
                if (index + 1 < hits.Length) { Schedule(index + 1); return; }
                action.AuthoredStageIndex = index;
                action.AuthoredStagesFinished = true;
                await CompleteCastOnceAsync(s, action);
                if (Now >= action.StartMs + action.ChantMs + action.ActionMs && ReferenceEquals(s.SkillCast, action))
                    s.SkillCast = null;
            });
        Schedule(0);
        return true;
    }

    private async Task ChannelHitAsync(GameSession s, ActiveSkillCast action, JsonElement row, bool first)
    {
        if (!Live(s, action)) return;
        var request = action.Request.Clone();
        request.IsExtraSkill |= !first;
        var hit = new ActiveSkillCast
        {
            Id = action.Id, OwnerId = action.OwnerId, WorldScene = action.WorldScene, SceneGeneration = action.SceneGeneration,
            Attachment = action.Attachment, Request = request, Row = row,
            // Fearless Charge: "Move ... then inflict Knock-Up ... to the targets", those of the area it started in (inferred).
            Origin = Script(row) == "PinpointAttack2" ? action.Origin : s.Position.Clone(), StatusLanded = action.StatusLanded ??= [],
            StartMs = action.StartMs, ChantMs = action.ChantMs, ActionMs = action.ActionMs, Operator = action.Operator,
            SourceEligible = () => Live(s, action),
            CompletionPublished = true, // 子命中不發布本地卡片完成事件；由原施放最後一段統一發布。
        };
        await ApplyEffectsAsync(s, hit, request.TargetPos ?? hit.Origin);
        // "Deals ... and inflicts Knockback ... Move ${12} meters in that direction, then": the move follows its first hit.
        if (first && Live(s, action) && AlsoMovesCaster(action.Row) is { } move) await MoveCasterAsync(s, action, move);
    }
}

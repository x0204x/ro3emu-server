using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Romsg;
using Ro3Emu.Server.Data.Config;
using Ro3Emu.Server.Maps;

namespace Ro3Emu.Server.Game;

/// <summary>Skills with a first press and a second: High-Energy Impact's flight and landing, Move back Shadow Echo's shadow and return.</summary>
public sealed partial class CombatService
{
    private const int FlyingBuff = 1632901, ImpactShieldBuff = 1633301, PulseBuff = 1633501, PulseGroup = 16335, TornadoEnhanceBuff = 1633601, TornadoEnhanceGroup = 16336;
    private const int ShadowEchoGroup = 15329, ShadowReturnBuffGroup = 115334;
    private const long PulseRatioBasis = 100;
    private const float PulseRadius = 8;
    private const int PulseTargets = 14, FlightOutlastsMs = 500;

    private sealed class ShadowSpot { public Vec3? At; public long Until; public bool Ending; }
    private readonly ConditionalWeakTable<GameSession, ShadowSpot> _shadowSpots = [];

    /// <summary>High-Energy Impact: "become Flying and gain a Shield ... You can Move and have [Immunity] during this time. Tap the skill again or wait until the Flying duration ends to attack Below".
    /// The first row's damage is its landing's, whose mode row (ten up) the flight buff switches the skill to. The flight lasts the row's action time.</summary>
    private async Task HighEnergyTakeOffAsync(GameSession s, ActiveSkillCast action)
    {
        var skill = new SkillConfig(action.Row);
        await ApplyEffectsAsync(s, WithRow(action, skill.WithDamage([], [])), action.Origin);
        int flight = Math.Max(1, action.ActionMs);
        await _buffs.AddAsync(s, FlyingBuff, flight + FlightOutlastsMs, s.Role!.Id); // the landing ends it, and it must still be there for that
        await _buffs.AddAsync(s, ControlImmunity, flight, s.Role.Id);
        if (SkillConfig.Get(_data, skill.Id + 10) is not { } landing) return;
        ScheduleRuntime(action.WorldScene, TimeSpan.FromMilliseconds(flight), async () =>
        {
            if (s.Dead || s.Role?.Id != action.OwnerId || s.WorldScene != action.WorldScene || !s.Buffs.Any(b => b.ConfigId == FlyingBuff)) return;
            var request = action.Request.Clone();
            request.SkillId = (int)landing.Id;
            request.TargetPos = s.Position.Clone();
            var fall = new ActiveSkillCast
            {
                Id = ++_sessionIds, OwnerId = action.OwnerId, WorldScene = action.WorldScene, SceneGeneration = s.SceneGeneration, Attachment = s.Attachment,
                Request = request, Row = new SkillConfig(landing.WithoutBuff1()).Row, Origin = s.Position.Clone(), StartMs = Now, ChantMs = 0, ActionMs = 1,
            };
            await ApplyEffectsAsync(s, fall, s.Position.Clone());
        });
    }

    /// <summary>The landing: "For every ${7} enemies hit by this attack, gain @{2} stacks of Shield equal to ${8}% of your Max HP ... This also Enhances your next Axe Tornado, Launching
    /// [High-Energy Pulse] a number of times based on the number of enemies hit". The pulse's row is not in the data: its ${11}% to up to 14 within 8 m is the text's (inferred).</summary>
    private async Task HighEnergyLandingAsync(GameSession s, SkillConfig skill, IReadOnlyList<MonsterUnit> processed)
    {
        foreach (var flying in s.Buffs.Where(b => b.ConfigId == FlyingBuff).ToArray()) await _buffs.RemoveAsync(s, flying);
        if (processed.Count == 0) return;
        var stats = _character.Stats(s);
        int shieldMs = skill.DescData.Length > 9 && int.TryParse(skill.DescData[9], NumberStyles.Integer, CultureInfo.InvariantCulture, out int seconds) ? seconds * 1000 : 10000;
        foreach (var _ in processed) await ApplySkillBuffAsync(stats, s, ImpactShieldBuff, shieldMs, s.Role!.Id);
        await _buffs.AddPilesAsync(s, PulseBuff, UntilSpentMs, s.Role!.Id, Math.Min(processed.Count, 5));
        await _buffs.AddAsync(s, TornadoEnhanceBuff, UntilSpentMs, s.Role.Id);
    }

    /// <summary>The Axe Tornado that follows a landing: its mode row runs, and it spends the enhancement and its pulses.</summary>
    private async Task HighEnergyPulsesAsync(GameSession s, SkillConfig tornado)
    {
        if (!_buffs.HasBuffPiles(s, TornadoEnhanceGroup, 1)) return;
        int pulses = s.Buffs.Where(b => b.ConfigId / 100 == PulseGroup).Sum(b => b.PileCount);
        foreach (var used in s.Buffs.Where(b => b.ConfigId / 100 is PulseGroup or TornadoEnhanceGroup).ToArray()) await _buffs.RemoveAsync(s, used);
        if (SkillConfig.Get(_data, 1630701) is not { DescData: { Length: > 10 } desc } || !long.TryParse(desc[10], NumberStyles.Integer, CultureInfo.InvariantCulture, out long percent)) return;
        var pulse = tornado.AsDescribedHit(percent * PulseRatioBasis, 0, 1, PulseTargets);
        for (int i = 0; i < pulses; i++)
        {
            var around = MonstersNear(s, s.Position, PulseRadius).Take(PulseTargets).ToList();
            if (around.Count > 0) await CastExtraAsync(s, pulse, around);
        }
    }

    /// <summary>Move back Shadow Echo: "Leaves a shadow in place. Cast in a selected direction to move ... Cast again within ${2} seconds to return to the shadow." Its cooldown waits for the
    /// return or the end of that time, or the second press would meet it; which the client asks is (inferred) and on the list for a client run.</summary>
    private bool ReturnsToShadow(GameSession s, JsonElement row) => Script(row) is "RetreatingShadows" or "RetreatingShadowsS1"
        && _shadowSpots.TryGetValue(s, out var spot) && spot.At != null && spot.Until > Now;

    private void LeaveShadow(GameSession s, ActiveSkillCast action)
    {
        var skill = new SkillConfig(action.Row);
        double seconds = skill.DescData.Length > 1 && double.TryParse(skill.DescData[1], NumberStyles.Float, CultureInfo.InvariantCulture, out double read) ? read : 6;
        var spot = _shadowSpots.GetOrCreateValue(s);
        spot.At = s.Position.Clone();
        spot.Until = Now + (long)(seconds * 1000);
        var row = action.Row;
        ScheduleRuntime(action.WorldScene, TimeSpan.FromSeconds(seconds), async () =>
        {
            if (spot.At == null || s.Role?.Id != action.OwnerId) return; // it returned, and that started the cooldown
            spot.At = null;
            spot.Ending = true;
            await StartCooldownAsync(s, row);
            spot.Ending = false;
        });
    }

    /// <summary>The first press starts no cooldown: LeaveShadow starts it when the time is up, and the return when it comes.</summary>
    private bool ShadowEchoWaits(GameSession s, JsonElement row) => Script(row) is "RetreatingShadows" or "RetreatingShadowsS1"
        && !(_shadowSpots.TryGetValue(s, out var spot) && (spot.Ending || spot.At != null && spot.Until > Now));

    private async Task ReturnToShadowAsync(GameSession s, ActiveSkillCast action)
    {
        if (!_shadowSpots.TryGetValue(s, out var spot) || spot.At is not { } at) return;
        spot.At = null;
        var start = s.Position.Clone();
        int effect = MovementEffect(new SkillConfig(action.Row).ScriptShow).Id;
        await ToPlayerAndViewersAsync(s, SkillMove, new SkillStartMoveNotify
        {
            EntityID = (ulong)s.Role!.Id, StartPos = start, EndPos = at, SkillConfigId = action.Request.SkillId,
            EffectId = effect, StartTimestampMs = Now, MoveType = 1, Face = action.Request.Face, MoveTimeMs = 200,
        });
        s.PreviousPosition = start;
        s.Position = at.Clone();
        await _world.MoveAsync(s, new WalkReq { Pos = at, Dir = s.Face, Face = s.Face, BStop = true }, forceTeleport: true);
        foreach (var mark in s.Buffs.Where(b => b.ConfigId / 100 is ShadowEchoGroup or ShadowReturnBuffGroup).ToArray()) await _buffs.RemoveAsync(s, mark);
    }
}

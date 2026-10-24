using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Romsg;
using Ro3Emu.Server.Data;
using Ro3Emu.Server.Data.Config;
using Ro3Emu.Server.Maps;

namespace Ro3Emu.Server.Game;

/// <summary>
/// What hiding and detection change outside the hit checks (CombatService.Stealth).
/// <list type="bullet">
/// <item>Lua_GameDefine.enum_unitAttrMap: Stealth 3010 is an enum_showStealth mask, ShowStealth1 3011 a radius in cm.
/// LC_UnitBase hands a unit whose Stealth is set to LC_UnitStealthManager, which shows it while it has the ShowStealth1
/// bit (2) and stands within the local player's ShowStealth1 * 0.01 m. Every hiding buff sets that bit (inferred: the
/// ShowStealth2/3 bits have no source in the data).</item>
/// <item>A hiding buff's _kLogicData is [linked group, dealing damage reveals, hits taken, skill on reveal]: Disguise
/// [15007, 1, 1..3] "You are revealed after taking damage @{1} times or dealing damage"; Enhance Cloaking [15305, 1, 5],
/// whose talent row adds 11532701 "When the [Enhance Camouflage] status is removed by dealing or taking damage, unleash a
/// ring of discs around yourself"; the monsters' Hiding [50031, 0, 1] "Revealed after taking damage @{1} times". Disguise
/// "immediately ends [Camouflage] and [Hiding]": the linked group goes with it. Thief's Hiding buff (15007) carries the
/// same numbers alone: "This speed boost is immediately removed after attacking or taking damage a total of ${3}
/// times".</item>
/// <item>Ruwach "locates one enemy ... who is under the [Disguise] Status Effect. Removes their [Disguise] Status Effect and
/// deals Holy Ranged Magic damage ... The holy spirit lasts ${3} seconds and disappears after Discovering a target." Its
/// buff's _kLogicData[0] is the hit row 1400601, whose _kLogicData[0] is the category removed (31) and whose _kRange (7 m)
/// is the search radius (inferred: the description's ${1} 20 is not a radius the hit row carries).</item>
/// <item>Disguise: "Passive: Using Back Slide also grants [Camouflage] for ${4} seconds." The Camouflage buff is the
/// first of its learned level's _kBuff1 (1500602 at level 1).</item>
/// </list>
/// </summary>
public sealed class StealthEffects : IGameTicker
{
    private const uint AoiAttrChange = CombatService.AoiAttrChange;
    private const int StealthAttr = 3010, ShowStealthAttr = 3011, ShowStealth1 = 2;
    private const int HidingSpeedGroup = 15007, RuwachGroup = 14005, BackSlide = 15104, Disguise = 15112;

    private readonly GameData _data;
    private readonly CombatService _combat;
    private readonly BuffService _buffs;
    private readonly SkillService _skills;
    private readonly World _world;
    private readonly SessionRegistry _sessions;
    private readonly GameLoop _loop;
    private readonly ConditionalWeakTable<GameSession, State> _states = [];
    private TimeSpan _lastTick;

    private sealed class State
    {
        public (long Mask, long Radius) Sent;
        public readonly Dictionary<long, int> Hits = [];
    }

    public StealthEffects(GameData data, CombatService combat, BuffService buffs, CharacterService character, SceneService scenes,
        SkillService skills, World world, SessionRegistry sessions, GameLoop loop)
    {
        _data = data;
        _combat = combat;
        _buffs = buffs;
        _skills = skills;
        _world = world;
        _sessions = sessions;
        _loop = loop;
        character.DynamicProperties.Add(Properties);
        buffs.Changed.Add(PublishAsync);
        scenes.AddOwnedAfterLoaded(PublishAsync);
        skills.Learned.Add(PublishAsync);
        character.JobChanged.Add(PublishAsync);
        combat.CastLanded.Add(BackSlideCamouflageAsync);
        combat.DealtAsync.Add((s, _, _, damage) => damage > 0 ? DealtAsync(s) : Task.CompletedTask);
        combat.TakenAsync.Add((_, s, damage) => damage > 0 ? TakenAsync(s) : Task.CompletedTask);
        combat.PvpDealtAsync.Add(async (source, target, damage, _) =>
        {
            if (damage <= 0) return;
            await TakenAsync(target);
            await DealtAsync(source);
        });
    }

    private (long Mask, long Radius) Visibility(GameSession s) =>
        (_combat.Stealthed(s) ? ShowStealth1 : 0, _combat.DetectionRadiusCm(s));

    private IEnumerable<(int Id, long Value)> Properties(GameSession s)
    {
        var (mask, radius) = Visibility(s);
        yield return (StealthAttr, mask);
        yield return (ShowStealthAttr, radius);
    }

    /// <summary>The two attributes to the player and its viewers, when either changed.</summary>
    private async Task PublishAsync(GameSession s)
    {
        if (s.Role == null) return;
        var now = Visibility(s);
        var state = _states.GetOrCreateValue(s);
        if (state.Sent == now) return;
        state.Sent = now;
        var change = new AOIAttrChange
        {
            EntityUuid = (ulong)s.Role.Id,
            Property =
            {
                new AoIProperty { Idx = StealthAttr, Data = now.Mask, BaseData = now.Mask },
                new AoIProperty { Idx = ShowStealthAttr, Data = now.Radius, BaseData = now.Radius },
            },
        };
        await s.SendGameAsync(AoiAttrChange, change);
        foreach (var viewer in _world.Viewers(s).Where(v => v != s)) await viewer.SendGameAsync(AoiAttrChange, change);
    }

    /// <summary>The player's live buffs that end on damage: hiding (category 31) and Thief's Hiding, with their numbers.</summary>
    private List<(ActiveBuff Buff, BuffConfig Row)> Breakable(GameSession s)
    {
        long now = MonsterService.NowMs;
        return [.. s.Buffs.Where(b => b.ExpireMs > now).Select(b => (Buff: b, Row: BuffConfig.Get(_data, b.ConfigId)))
            .Where(x => x.Row is { LogicData.Length: >= 3 } row && (CombatService.StealthBuff(row) || row.BuffId == HidingSpeedGroup))
            .Select(x => (x.Buff, x.Row!.Value))];
    }

    private async Task DealtAsync(GameSession s)
    {
        foreach (var (buff, row) in Breakable(s).Where(x => x.Row.LogicData[1] == 1)) await RevealAsync(s, buff, row);
    }

    private async Task TakenAsync(GameSession s)
    {
        var hits = _states.GetOrCreateValue(s).Hits;
        var breakable = Breakable(s);
        foreach (long gone in hits.Keys.Where(guid => breakable.All(x => x.Buff.Guid != guid)).ToArray()) hits.Remove(gone);
        foreach (var (buff, row) in breakable)
        {
            if (row.LogicData[2] <= 0) continue;
            int taken = hits.GetValueOrDefault(buff.Guid) + 1;
            if (taken < row.LogicData[2]) hits[buff.Guid] = taken;
            else await RevealAsync(s, buff, row);
        }
    }

    /// <summary>The buff ends with its linked group; a talent row's skill on reveal hits around the player.</summary>
    private async Task RevealAsync(GameSession s, ActiveBuff buff, BuffConfig row)
    {
        _states.GetOrCreateValue(s).Hits.Remove(buff.Guid);
        if (!s.Buffs.Any(b => b.Guid == buff.Guid)) return;
        await _buffs.RemoveAsync(s, buff);
        await RemoveGroupAsync(s, row.LogicData[0]);
        if (row.LogicData is [_, _, _, var follow, ..] && SkillConfig.Get(_data, follow) is { } skill)
        {
            var around = _combat.MonstersNear(s, s.Position, skill.Range.FirstOrDefault() / 100f)
                .Take((int)Math.Max(1, skill.TargetMax)).ToList();
            await _combat.CastExtraAsync(s, skill.Row, around);
        }
    }

    private async Task RemoveGroupAsync(GameSession s, long group)
    {
        foreach (var linked in s.Buffs.Where(b => BuffConfig.Get(_data, b.ConfigId)?.BuffId == group).ToArray())
            await _buffs.RemoveAsync(s, linked);
    }

    private async Task BackSlideCamouflageAsync(GameSession s, JsonElement row)
    {
        if (new SkillConfig(row).SkillId != BackSlide || s.Role == null || s.Progress is not { } progress
            || _skills.Level(progress, Disguise) is not (> 0 and var level)
            || SkillConfig.Get(_data, _skills.RealId(Disguise, level)) is not { Buff1: [[var camouflage, ..], ..] } disguise
            || !double.TryParse(disguise.DescData.ElementAtOrDefault(3), NumberStyles.Float, CultureInfo.InvariantCulture, out double seconds))
            return;
        await _buffs.AddAsync(s, (int)camouflage, (int)(seconds * 1000), s.Role.Id);
    }

    /// <summary>A live Ruwach finds the nearest hidden enemy within its hit row's range.</summary>
    public void Tick(GameTime time)
    {
        if (time.Now - _lastTick < TimeSpan.FromMilliseconds(200)) return;
        _lastTick = time.Now;
        long now = MonsterService.NowMs;
        foreach (var s in _sessions.All())
        {
            if (s.Role == null || s.Dead || s.SceneLoading
                || s.Buffs.FirstOrDefault(b => b.ExpireMs > now && b.ConfigId / 100 == RuwachGroup) is not { } ruwach
                || BuffConfig.Get(_data, ruwach.ConfigId) is not { LogicData: [var hitId, ..] }
                || SkillConfig.Get(_data, hitId) is not { } hit
                || RuwachTarget(s, hit) is null) continue;
            _loop.PostLegacy(() => RuwachAsync(s, ruwach, hit), "ruwach");
        }
    }

    private GameSession? RuwachTarget(GameSession s, SkillConfig hit) => _world.PlayersIn(s.WorldScene)
        .Where(p => p != s && !p.Dead && p.Role != null && _combat.Hostile(s, p) && _combat.Stealthed(p)
            && MonsterService.Distance(s.Position.X, s.Position.Z, p.Position.X, p.Position.Z) * 100 <= hit.Range.FirstOrDefault())
        .OrderBy(p => MonsterService.Distance(s.Position.X, s.Position.Z, p.Position.X, p.Position.Z)).ThenBy(p => p.Role!.Id)
        .FirstOrDefault();

    private async Task RuwachAsync(GameSession s, ActiveBuff ruwach, SkillConfig hit)
    {
        if (s.Dead || !s.Buffs.Any(b => b.Guid == ruwach.Guid) || RuwachTarget(s, hit) is not { } target) return;
        long category = hit.LogicData.FirstOrDefault();
        foreach (var buff in target.Buffs.Where(b => b.ExpireMs > MonsterService.NowMs).ToArray())
            if (BuffConfig.Get(_data, buff.ConfigId) is { } row && row.BuffCategory.Contains(category)
                && target.Buffs.Any(b => b.Guid == buff.Guid))
            {
                await _buffs.RemoveAsync(target, buff);
                if (row.LogicData.Length > 0) await RemoveGroupAsync(target, row.LogicData[0]);
            }
        await _buffs.RemoveAsync(s, ruwach);
        await _combat.CastExtraOnPlayerAsync(s, hit, target);
    }
}

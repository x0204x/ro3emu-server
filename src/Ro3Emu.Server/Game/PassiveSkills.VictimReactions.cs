using System.Runtime.CompilerServices;

namespace Ro3Emu.Server.Game;

public sealed partial class PassiveSkills
{
    private readonly ConditionalWeakTable<GameSession, VictimState> _victimStates = new();

    private sealed class VictimState
    {
        public object? Role;
        public object? Protocol;
        public readonly Dictionary<int, long> ReadyAt = [];
        public long LastHitMs;
        public long HitSerial;
    }

    // 可固定安靜期的時鐘進行測試；實際機率、冷卻、持續時間均仍讀原表。
    internal Func<long> VictimNow { get; set; } = () => MonsterService.NowMs;
    internal Action<TimeSpan, Func<Task>>? ScheduleVictimQuiet { get; set; }

    private VictimState VictimStateFor(GameSession player)
    {
        var state = _victimStates.GetOrCreateValue(player);
        if (ReferenceEquals(state.Role, player.Role) && ReferenceEquals(state.Protocol, player.SkillProtocol)) return state;
        state.Role = player.Role; state.Protocol = player.SkillProtocol;
        state.ReadyAt.Clear(); state.LastHitMs = 0; state.HitSerial = 0;
        return state;
    }

    private static Func<bool> CaptureVictim(GameSession player)
    {
        var role = player.Role;
        var protocol = player.SkillProtocol;
        var attachment = player.Attachment;
        long scene = player.WorldScene, generation = player.SceneGeneration;
        return () => role != null && ReferenceEquals(player.Role, role)
            && ReferenceEquals(player.SkillProtocol, protocol)
            && ReferenceEquals(player.Attachment, attachment) && attachment?.Cancelled != true
            && player.WorldScene == scene && player.SceneGeneration == generation && !player.SceneLoading && !player.Dead;
    }

    private bool VictimReady(VictimState state, int group, long cooldown)
    {
        long now = VictimNow();
        if (state.ReadyAt.GetValueOrDefault(group) > now) return false;
        state.ReadyAt[group] = now + cooldown;
        return true;
    }

    private async Task VictimTakenAsync(GameSession target, long damage, Func<bool> current)
    {
        if (damage <= 0 || !current()) return;
        var state = VictimStateFor(target);
        state.LastHitMs = VictimNow();
        long serial = ++state.HitSerial;
        // 原12003描述「受到傷害」，不限魔物；保留既有12014→12015減傷與冷卻。
        if (Learned(target, EnergyCoat) is { Skill: var coat, Own: { LogicData: [var buff, var ms, ..] } }
            && VictimReady(state, EnergyCoat, (long)(Value(coat.DescData, 2) * 1000)))
            await _buffs.AddAsync(target, (int)buff, (int)ms, target.Role!.Id);
        if (!current()) return;
        await EmergencyVictimAsync(target, state, current);
        if (!current() || Learned(target, PrayerOfGratitude) is not { Skill: var prayer }) return;
        long quiet = (long)(Value(prayer.DescData, 4) * 1000);
        Func<Task> callback = async () =>
        {
            if (current() && state.HitSerial == serial && VictimNow() - state.LastHitMs >= quiet)
                await PrayerShieldAsync(target, current);
        };
        if (ScheduleVictimQuiet is { } schedule) schedule(TimeSpan.FromMilliseconds(quiet + 50), callback);
        else _loop.ScheduleRuntime(TimeSpan.FromMilliseconds(quiet + 50), callback);
    }

    private async Task EmergencyVictimAsync(GameSession target, VictimState state, Func<bool> current)
    {
        if (Learned(target, EmergencyTreatment) is not { Skill: var treatment, Own: { LogicData: [var shield, ..] } }) return;
        var stats = _character.Stats(target);
        var desc = treatment.DescData;
        if (stats[AttrHp] * 100 >= stats[AttrMaxHp] * Value(desc, 0)
            || !VictimReady(state, EmergencyTreatment, (long)(Value(desc, 3) * 1000))) return;
        // 保留既有技能描述百分比；16308 Buff公式與描述有差異，未在補PvP入口時擅改數值。
        await _buffs.AddSupportAsync(target, (int)shield, 0, target.Role!.Id,
            (long)(stats[AttrMaxHp] * Value(desc, 1) / 100));
        if (!current() || Learned(target, EmergencyTreatment)?.Skill.Id != treatment.Id) return;
        // 護盾發布可能等待其他真命中；只能在最新HP上補血，不能覆蓋等待期間已扣的傷害。
        stats = _character.Stats(target);
        await _character.SetHpSpAsync(target,
            Math.Min(stats[AttrMaxHp], stats[AttrHp] + (long)(stats[AttrMaxHp] * Value(desc, 2) / 100)), null);
    }
}

namespace Ro3Emu.Server.Game;

/// <summary>
/// Quest readers of the Territory War (TaskService.EventTargets.cs, TaskService.DungeonTasks.cs).
/// <list type="bullet">
/// <item>106609: the guild's season points, every round's battle scores summed (inferred: the client shows only one
/// round's sum, TerritoryWarsGuildData.guildScore).</item>
/// <item>The siege's dungeon tasks (TaskConfig 4900000, 4900020-4900024, _iDungeonID 2230101): 200000 "Reach Dungeon Stage
/// ${1}" (${1} = 1) is met once the siege leaves its Prepare stage 0 for the Gate stage 1 (TerritoryWarsService.SiegeStages.cs),
/// 210003 "${1} Assists" reads the participant's assists and 200001 "Rating: ${1}" its personal score (one point per
/// player kill and for the crystal, inferred in SiegeBattle).</item>
/// <item>(inferred) A siege assist is a hit on the victim by another participant within the Battle Merit assist window
/// (<see cref="GloryAssistWindowMs"/>, the world's rule) before the killing blow; the hits are kept in memory only.</item>
/// </list>
/// </summary>
public sealed partial class TerritoryWarsService
{
    /// <summary>The siege's tracker step from the Gate stage on: its stage 200000 is passed (0 in Prepare).</summary>
    private const int SiegeFightStep = 1;
    private const int RatingTarget = 200001, AssistsTarget = 210003;

    /// <summary>A loaded siege participant's dungeon task tracker changed (TaskService.OnDungeonRun): scene, instance,
    /// step. Listeners post their work.</summary>
    public List<Action<GameSession, long, long, int>> SiegeTracked { get; } = [];

    /// <summary>106609: the season points of the role's guild, 0 without a guild.</summary>
    public long GuildWarPoints(GameSession s) =>
        OwnId(s) is var guild and > 0 ? state.Battles.Values.Sum(b => b.Scores.GetValueOrDefault(guild)) : 0;

    /// <summary>200001 / 210003 of a loaded siege participant, -1 outside a siege.</summary>
    public long SiegeTrackerValue(GameSession s, int target) =>
        s.SiegeBattle is { } battle && s.Role != null && s.InstanceId == battle.Room.Id
        && battle.State.Players.TryGetValue(s.Role.Id, out var p)
            ? target switch { RatingTarget => p.Score, AssistsTarget => p.Assists, _ => -1 } : -1;

    /// <summary>A non-lethal hit for a later assist.</summary>
    private static void SiegeHit(SiegeRun battle, long attacker, long victim, long now) =>
        (battle.Hits.TryGetValue(victim, out var hits) ? hits : battle.Hits[victim] = [])[attacker] = now;

    /// <summary>A kill: the other recent hitters get an assist each; returns them.</summary>
    private static long[] SiegeAssists(SiegeRun battle, long killer, long victim, long now)
    {
        if (!battle.Hits.Remove(victim, out var hits)) return [];
        var helpers = hits.Where(h => h.Key != killer && now - h.Value <= GloryAssistWindowMs
            && battle.State.Players.ContainsKey(h.Key)).Select(h => h.Key).ToArray();
        foreach (long helper in helpers) battle.State.Players[helper].Assists++;
        return helpers;
    }

    private void SiegeTrack(SiegeRun battle, IEnumerable<long> roles)
    {
        int step = battle.State.Stage >= SiegeGateStage ? SiegeFightStep : 0;
        foreach (var member in battle.Room.ActiveMembers.Where(m => m.Session.Role is { } role && roles.Contains(role.Id)))
            foreach (var handler in SiegeTracked) handler(member.Session, SiegeScene, battle.Room.Id, step);
    }
}

namespace Ro3Emu.Server.Game;

/// <summary>
/// The guild To-Do tasks "Participate in the Guild Escort / Stronghold / Regional Capital / Royal City War": hidden
/// DailyActivityConfig rows 3008-3010 and the "(Auction Check)" rows 3103-3106 accept them through _iTaskReceiveAction
/// [31,1,task] and LC_ConditionList [4004,task] (DailyActivityService.Defs). Their only target is [106010,5], the
/// Tavern's BarroomToDayActTime, which the client data reuses as a stand-in (TaskObjectives.TBarroomActTime).
/// (inferred) Taking part in the event completes the task at once: the original counter is not in the client.
/// Task 1602080 (Gloom over Longgu Island) has no To-Do row and stays without a producer.
/// </summary>
public sealed partial class TaskService
{
    public enum GuildEvent { Escort, Stronghold, RegionalCapital, RoyalCity }

    private static readonly Dictionary<GuildEvent, long[]> GuildEventTasks = new()
    {
        [GuildEvent.Escort] = [1602050, 1603013],
        [GuildEvent.Stronghold] = [1602060, 1603014],
        [GuildEvent.RegionalCapital] = [1602070, 1603011],
        [GuildEvent.RoyalCity] = [1603012],
    };

    private static bool GuildEventTask(long task) => GuildEventTasks.Values.Any(tasks => tasks.Contains(task));

    /// <summary>A member took part in a guild event: its To-Do tasks complete.</summary>
    public void OnGuildEventJoined(GameSession s, GuildEvent guildEvent)
    {
        var tasks = GuildEventTasks[guildEvent];
        _loop.PostOwned(s, () => ProgressAsync(s, TBarroomActTime, t => t.Length == 2 ? t[1] : 0, def => tasks.Contains(def.Id)),
            "quest guild event");
    }

    public void OnGuildEventJoined(long role, GuildEvent guildEvent)
    {
        if (_sessions.All().FirstOrDefault(s => s.Role?.Id == role) is { } session) OnGuildEventJoined(session, guildEvent);
    }

    /// <summary>A settled territory battle the role was loaded into. (inferred) Round 1 is the Stronghold War and later
    /// rounds the Regional Capital War, as the settlement's winner rule (the incumbent from round 2) reads them.</summary>
    public void OnTerritoryBattleSettled(long role, int round) =>
        OnGuildEventJoined(role, round >= 2 ? GuildEvent.RegionalCapital : GuildEvent.Stronghold);
}

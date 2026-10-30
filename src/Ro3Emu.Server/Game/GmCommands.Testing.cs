namespace Ro3Emu.Server.Game;

public sealed partial class GmCommands
{
    /// <summary>
    /// "!testing": Ro3Emu:Testing (TestingSwitches, TEST AIDS, not original) shown or changed in memory until the next
    /// start; "reset" brings back the configured values. The online roles get their To-Do rows (5004) and dungeon reward
    /// counts (5062) again at once, so the client shows the change without a relog.
    /// </summary>
    private async Task TestingAsync(GameSession s, string[] args)
    {
        var testing = services.GetRequiredService<TestingSwitches>();
        string sub = args.Length == 0 ? "" : args[0].ToLowerInvariant();
        bool? on = args.Length == 2 ? args[1].ToLowerInvariant() switch { "on" => true, "off" => false, _ => null } : null;
        if (sub == "alwaysopen" && on is { } open) testing.AlwaysOpen = open;
        else if (sub == "unlimited" && on is { } unlimited) testing.UnlimitedRewards = unlimited;
        else if (sub == "reset" && args.Length == 1) testing.Reset();
        else if (args.Length != 0) throw new ArgumentException("alwaysopen on|off | unlimited on|off | reset");
        if (args.Length != 0)
        {
            var daily = services.GetRequiredService<DailyActivityService>();
            var dungeons = services.GetRequiredService<DungeonService>();
            await RoleFanout.EachAsync(sessions.All().Where(p => p.Role != null && p.DailyReady), async p =>
            {
                await daily.SyncEventsAsync(p);
                await dungeons.SyncAsync(p);
            });
        }
        await ReplyAsync(s, $"Testing: {testing.Describe()} (TEST AIDS, not original behaviour; in memory until the next start).");
    }
}

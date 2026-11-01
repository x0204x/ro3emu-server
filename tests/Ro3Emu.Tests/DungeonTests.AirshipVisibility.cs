using Romsg;
using Ro3Emu.Bot;
using Ro3Emu.Server.Maps;

namespace Ro3Emu.Tests;

public sealed partial class DungeonTests
{
    // 依31037進出視野及31081更新位置，不用出生點冒充已接近的船，也不從伺服器表取目標ID。
    private static async Task<EntityBase[]> ClientAirships(BotClient bot, int expected = 3)
    {
        var deadline = DateTime.UtcNow + Wait;
        while (DateTime.UtcNow < deadline)
        {
            var units = new Dictionary<ulong, EntityBase>();
            foreach (var frame in bot.Inbox.Snapshot())
            {
                if (frame.IsGame(GameAoiSink.SyncAoiView))
                {
                    var view = AoIViewChangeData.Parser.ParseFrom(frame.Body);
                    foreach (ulong gone in view.LeaveAoi) units.Remove(gone);
                    foreach (var unit in view.Bases) units[unit.Uuid] = unit;
                }
                else if (frame.IsGame(GameAoiSink.MovementChange))
                {
                    var move = MovementChangeNtf.Parser.ParseFrom(frame.Body);
                    if (units.TryGetValue((ulong)move.Uid, out var unit)) unit.MovementInfo = move.MovementInfo.Clone();
                }
            }
            var ships = units.Values.Where(unit => unit.EntityTemplateID is 5300015 or 5300053)
                .OrderBy(unit => unit.EntityTemplateID).ThenBy(unit => unit.Uuid).ToArray();
            if (ships.Length == expected) return ships;
            await Task.Delay(20);
        }
        throw new TimeoutException($"The client did not hold {expected} enemy airships through 31037.");
    }
}

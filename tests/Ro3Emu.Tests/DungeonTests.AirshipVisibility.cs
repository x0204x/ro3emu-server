using Romsg;
using Ro3Emu.Bot;
using Ro3Emu.Server.Maps;

namespace Ro3Emu.Tests;

public sealed partial class DungeonTests
{
    // 依客戶端實際收到的 31037 進出視野重建清單，不從伺服器怪物表取得目標 ID。
    private static async Task<EntityBase[]> ClientAirships(BotClient bot, int expected = 2)
    {
        var deadline = DateTime.UtcNow + Wait;
        while (DateTime.UtcNow < deadline)
        {
            var units = new Dictionary<ulong, EntityBase>();
            foreach (var frame in bot.Inbox.Snapshot().Where(e => e.IsGame(GameAoiSink.SyncAoiView)))
            {
                var view = AoIViewChangeData.Parser.ParseFrom(frame.Body);
                foreach (ulong gone in view.LeaveAoi) units.Remove(gone);
                foreach (var unit in view.Bases) units[unit.Uuid] = unit;
            }
            var ships = units.Values.Where(unit => unit.EntityTemplateID is 5300015 or 5300053)
                .OrderBy(unit => unit.EntityTemplateID).ToArray();
            if (ships.Length == expected) return ships;
            await Task.Delay(20);
        }
        throw new TimeoutException($"The client did not hold {expected} enemy airships through 31037.");
    }
}

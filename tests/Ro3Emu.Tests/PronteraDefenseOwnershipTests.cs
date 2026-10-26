using Ro3Emu.Server.Game;
using Ro3Emu.Server.Game.Ownership;

namespace Ro3Emu.Tests;

public sealed class PronteraDefenseOwnershipTests
{
    [Fact]
    public void AdvanceReservesTheEventAndEveryPersistedDefensePoint()
    {
        var keys = PronteraDefenseService.AdvanceReservations(20261026, [10100, 10101, 10100, 0, -1]);

        Assert.Equal(
        [
            ReservationKey.Entity("prontera_event", "20261026"),
            ReservationKey.Entity("prontera_point", "20261026:10100"),
            ReservationKey.Entity("prontera_point", "20261026:10101"),
        ], keys);
    }
}

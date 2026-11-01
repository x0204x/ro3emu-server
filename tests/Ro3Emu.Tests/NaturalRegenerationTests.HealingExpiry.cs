using Ro3Emu.Server.Game;

namespace Ro3Emu.Tests;

public sealed partial class NaturalRegenerationTests
{
    [Fact]
    public async Task ExpiredRecoveryBuffCannotAddHpOrSpBeforeItsNextCleanupTick()
    {
        await using var bot = await _host.InWorldAsync("RecoveryExpiry");
        var session = Session(bot);
        await Service<GameLoop>().InvokeAsync(async () =>
        {
            session.CurrentHp = 50; session.CurrentSp = 20;
            var now = MonsterService.NowMs;
            // 自造增益只測生命週期，不複製原客戶端資料列。
            session.Buffs.Add(new ActiveBuff(998871, 998871, now - 1, 1000,
                [(107, 100d), (108, 100d)], session.Role!.Id));
            _regenWork = Service<GameLoop>().CurrentWork;
            await Service<CombatService>().RegenAsync(session, () => true);
            Assert.Equal(55, session.CurrentHp);
            Assert.Equal(27, session.CurrentSp);
        });
    }
}

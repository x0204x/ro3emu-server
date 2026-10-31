using Ro3Emu.Server.Data;
using Ro3Emu.Server.Game;
using Ro3Emu.Server.Maps;

namespace Ro3Emu.Tests;

public sealed partial class StoryBossSkillTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MonsterSkillAwaitingDamageAdjustmentCannotCommitAfterSourceRemoval(bool removeSource)
    {
        await using var host = await SyntheticHost.StartAsync(o => o.PreloadGameData = false);
        SureHitRolls.Install(host.App);
        await using var bot = await host.InWorldAsync("PendingHit");
        var player = host.Service<SessionRegistry>().All().Single(s => s.Role != null);
        var source = await RemovalSourceAsync(host, player);
        var combat = host.Service<CombatService>();
        var loop = host.Service<GameLoop>();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        long before = 0;
        await loop.InvokeAsync(async () =>
        {
            var character = host.Service<CharacterService>();
            character.ExtraAttributes.Add(s => ReferenceEquals(s, player) ? [(101, 10000d)] : []);
            await character.SetHpSpAsync(player, long.MaxValue, null);
            before = player.CurrentHp!.Value;
            Assert.True(before >= 10000);
            combat.IncomingDamageAdjusters.Add(async (caster, target, _, damage) =>
            {
                if (ReferenceEquals(caster, source) && ReferenceEquals(target, player))
                {
                    entered.TrySetResult();
                    await release.Task;
                }
                return damage;
            });
        });
        var hit = loop.InvokeAsync(() => combat.MonsterSkillHitAsync(source,
            host.Service<GameData>().Row("SkillConfig", RemovalSwing)!.Value, [player], source.Uid));
        try
        {
            await entered.Task.WaitAsync(RemovalWait);
            if (removeSource) await loop.InvokeAsync(() => host.Service<World>().RemoveUnitAsync(source));
            release.TrySetResult();
            int committed = await hit.WaitAsync(RemovalWait);
            long after = await loop.InvokeAsync(() => Task.FromResult(player.CurrentHp!.Value));
            if (removeSource)
            {
                Assert.Equal(0, committed);
                Assert.Equal(before, after);
                AssertNoRemovalHit(bot, source.Uid);
            }
            else
            {
                Assert.Equal(1, committed);
                Assert.True(after < before, "正向對照必須真的扣血，不能只確認回呼跑完。");
                await AssertRemovalHitAsync(bot, source.Uid);
            }
        }
        finally
        {
            release.TrySetResult();
            await hit.WaitAsync(RemovalWait);
        }
    }
}

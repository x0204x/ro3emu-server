using Microsoft.Extensions.DependencyInjection;
using Ro3Emu.Server.Data;
using Ro3Emu.Server.Data.Config;
using Ro3Emu.Server.Game;

namespace Ro3Emu.Tests;

public sealed partial class SkillCombatTests
{
    [ClientDataFact] // a corpse does not walk on, and stands where it was when it died
    public async Task ADeadMonsterStopsWhereItIs()
    {
        await using var alice = await InWorld("Alice");
        var dummy = await Target("alice", stationary: true, npcId: 5000002);
        var monsters = _app.Services.GetRequiredService<MonsterService>();
        await Loop.InvokeAsync(async () =>
        {
            long now = MonsterService.NowMs;
            dummy.FromX = dummy.X; dummy.FromY = dummy.Y; dummy.FromZ = dummy.Z;
            dummy.ToX = dummy.X + 100; dummy.ToY = dummy.Y; dummy.ToZ = dummy.Z;
            dummy.StartMs = now - 1000; dummy.EndMs = now + 9000; dummy.SegmentSpeed = 10;
            dummy.Moving = true;
            float before = dummy.X;
            await monsters.KillAsync(dummy);
            Assert.False(dummy.Moving);
            Assert.InRange(dummy.X - before, 5f, 15f);
        });
    }

    [ClientDataFact] // losing its target, it turns to another who engaged it
    public async Task AMonsterTurnsToItsNextAttacker()
    {
        await using var alice = await InWorld("Alice"); await using var bob = await InWorld("Bob");
        var (a, b) = (Session("alice"), Session("bob"));
        var dummy = await Target("alice", npcId: 5000001);
        var monsters = _app.Services.GetRequiredService<MonsterService>();
        await Loop.InvokeAsync(() =>
        {
            dummy.X = a.Position.X + 1; dummy.Z = a.Position.Z;
            b.Position = a.Position.Clone();
            monsters.Engage(dummy, b);
            monsters.Engage(dummy, a);
            Assert.Equal(2, dummy.Hated.Count);
            Assert.Same(a, dummy.Target);
            return Task.CompletedTask;
        });
        await Loop.InvokeAsync(() => { a.Dead = true; return Task.CompletedTask; });
        var deadline = DateTime.UtcNow + Wait;
        while (!ReferenceEquals(dummy.Target, b) && DateTime.UtcNow < deadline) await Task.Delay(50);
        Assert.Same(b, dummy.Target);
        await Loop.InvokeAsync(() => { a.Dead = false; return Task.CompletedTask; });
    }

    [ClientDataFact] // m_kWarningRange: the data holds a warning for boss shows, and the view reads its time
    public async Task AGroundWarningReadsItsDuration()
    {
        await using var alice = await InWorld("Alice");
        var effects = _app.Services.GetRequiredService<GameData>().SkillTemplateTable("Lua_Config_EffectData");
        Assert.Equal(1.25, SkillEffectData.WarningSeconds(effects["4110506"]));
        Assert.Null(SkillEffectData.WarningSeconds(effects["52001013"]));
    }
}

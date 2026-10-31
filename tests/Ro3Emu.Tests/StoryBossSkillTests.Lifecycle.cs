using System.Text.Json;
using System.Reflection;
using Ro3Emu.Server.Data;
using Ro3Emu.Server.Game;
using Ro3Emu.Server.Maps;

namespace Ro3Emu.Tests;

public sealed partial class StoryBossSkillTests
{
    private static MonsterUnit StateBoss(long instance = 7)
    {
        var skill = new BossSkill
        {
            Id = 5360601, Row = JsonSerializer.SerializeToElement(new { _iID = 5360601 }),
            ActionMs = 1200, TargetsSelf = true
        };
        var book = new BossSkillBook { Family = "自造狀態測試", Skills = [skill] };
        return new MonsterUnit
        {
            Uid = 77, SceneEntry = SyntheticData.Town, InstanceId = instance, Hp = 10,
            Template = new MonsterTemplate(5300006, 1, 10, 0, 1, 1, 0, 0, 1, 0, 0, 0, 1, 1, 1, 2,
                false, 0, 1000, 5, 10, 0, 1200, .1f, 1200, 1, 500)
            { Skills = book },
            Group = new SpawnGroup(-1, false, 1, 0, [], []), Home = new SpawnPoint(-1, 0, 0, 0, 0, 5300006),
            BossSkills = new BossSkillRuntime(book) { CastSerial = 1 },
        };
    }

    [Fact]
    public async Task MayaStateHasNoInventedExpiryAndFinishedCastCleanupRemovesIt()
    {
        await using var host = await SyntheticHost.StartAsync(o => o.PreloadGameData = false);
        var monsters = host.Service<MonsterService>();
        var buffs = host.Service<BuffService>();
        _ = host.Service<CombatService>();
        await host.Service<GameLoop>().InvokeAsync(async () =>
        {
            // 僅綁定識別碼；群組、動作長度與單位能力均為自造資料。
            Row(host.Service<GameData>(), "BuffConfig", 5366001, new { _iID = 5366001, _iBuffID = 991001, _iTime = -1 });
            var boss = StateBoss();
            var rt = boss.BossSkills!;
            var skill = rt.Book.Skills[0];
            await monsters.CastBossSkill!(boss, null, null, skill, 1);
            await monsters.CastBossSkill!(boss, null, null, skill, 1);
            var buff = Assert.Single(buffs.UnitBuffs(boss));
            Assert.Equal(5366001, buff.ConfigId);
            Assert.True(buff.IsPassive);
            Assert.Equal(long.MaxValue, buff.ExpireMs);
            Assert.Equal(0, buff.DurationMs);
            // 已不在施放中也必須清掉常駐狀態，不能依賴 60 秒逾時。
            await monsters.BossSkillsEnded!(boss, rt, false);
            Assert.Empty(buffs.UnitBuffs(boss));
            await monsters.BossSkillsEnded!(boss, rt, false);
            Assert.Empty(buffs.UnitBuffs(boss));
        });
    }

    [Fact]
    public async Task StaleDeadOrRemovedCastCannotAddStateOrCallScenePresentation()
    {
        await using var host = await SyntheticHost.StartAsync(o => o.PreloadGameData = false);
        var monsters = host.Service<MonsterService>();
        var combat = host.Service<CombatService>();
        var buffs = host.Service<BuffService>();
        int presentations = 0;
        combat.StoryBossCastStarted += (_, _, _) => { presentations++; return Task.CompletedTask; };
        await host.Service<GameLoop>().InvokeAsync(async () =>
        {
            Row(host.Service<GameData>(), "BuffConfig", 5366001, new { _iID = 5366001, _iBuffID = 991001, _iTime = -1 });
            var boss = StateBoss();
            var skill = boss.BossSkills!.Book.Skills[0];
            await monsters.CastBossSkill!(boss, null, null, skill, 0);
            boss.Dead = true;
            await monsters.CastBossSkill!(boss, null, null, skill, 1);
            boss.Dead = false;
            boss.Removed = true;
            await monsters.CastBossSkill!(boss, null, null, skill, 1);
            Assert.Empty(buffs.UnitBuffs(boss));
            Assert.Equal(0, presentations);
            boss.Removed = false;
            await monsters.CastBossSkill!(boss, null, null, skill, 1);
            Assert.Single(buffs.UnitBuffs(boss));
            Assert.Equal(1, presentations);
            await monsters.BossSkillsEnded!(boss, boss.BossSkills, false);
        });
    }

    [Fact]
    public async Task OldMayaReleaseContinuationCannotClearStateFromANewerCast()
    {
        await using var host = await SyntheticHost.StartAsync(o => o.PreloadGameData = false);
        var monsters = host.Service<MonsterService>();
        var combat = host.Service<CombatService>();
        var buffs = host.Service<BuffService>();
        await host.Service<GameLoop>().InvokeAsync(async () =>
        {
            Row(host.Service<GameData>(), "BuffConfig", 5366001, new { _iID = 5366001, _iBuffID = 991001, _iTime = -1 });
            var boss = StateBoss();
            var state = boss.BossSkills!.Book.Skills[0];
            var release = new BossSkill { Id = 5365401, Row = JsonSerializer.SerializeToElement(new { _iID = 5365401 }) };
            // 模擬舊釋放在扣血 await 中讓出執行，新施放已透過正式入口補上狀態。
            boss.BossSkills.CastSerial = 2;
            await monsters.CastBossSkill!(boss, null, null, state, 2);
            var newerState = Assert.Single(buffs.UnitBuffs(boss));
            var completed = typeof(CombatService).GetMethod("StoryBossImpactCompletedAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
            await (Task)completed.Invoke(combat, [boss, release, 1L, true])!;
            Assert.Same(newerState, Assert.Single(buffs.UnitBuffs(boss)));
            // 正向對照：目前世代的釋放仍會清除，不能把所有釋放都忽略。
            boss.BossSkills.CastSerial = 3;
            await (Task)completed.Invoke(combat, [boss, release, 3L, true])!;
            Assert.Empty(buffs.UnitBuffs(boss));
        });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task InvalidatedStartContinuationCannotRestoreCleanedMayaState(bool removed)
    {
        await using var host = await SyntheticHost.StartAsync(o => o.PreloadGameData = false);
        var monsters = host.Service<MonsterService>();
        var combat = host.Service<CombatService>();
        var buffs = host.Service<BuffService>();
        await host.Service<GameLoop>().InvokeAsync(async () =>
        {
            Row(host.Service<GameData>(), "BuffConfig", 5366001, new { _iID = 5366001, _iBuffID = 991001, _iTime = -1 });
            var boss = StateBoss();
            var rt = boss.BossSkills!;
            var skill = rt.Book.Skills[0];
            await monsters.CastBossSkill!(boss, null, null, skill, 1);
            Assert.Single(buffs.UnitBuffs(boss));
            await monsters.BossSkillsEnded!(boss, rt, false);
            Assert.Empty(buffs.UnitBuffs(boss));
            if (removed) boss.Removed = true;
            else rt.CastSerial++;
            // 明確重放讓出執行後才回來的 start continuation，不只驗證施放入口。
            var started = typeof(CombatService).GetMethod("StoryBossSkillStartedAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
            await (Task)started.Invoke(combat, [boss, skill, 1L, boss.Pos])!;
            Assert.Empty(buffs.UnitBuffs(boss));
        });
    }
}

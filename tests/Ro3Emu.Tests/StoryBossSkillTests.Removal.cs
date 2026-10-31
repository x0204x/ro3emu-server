using Romsg;
using Ro3Emu.Bot;
using Ro3Emu.Server.Data;
using Ro3Emu.Server.Game;
using Ro3Emu.Server.Maps;

namespace Ro3Emu.Tests;

public sealed partial class StoryBossSkillTests
{
    private const int RemovalSwing = 991102;
    private static readonly TimeSpan RemovalWait = TimeSpan.FromSeconds(5);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task APlayerSwingAlreadyStartedCannotHitAfterSourceRemoval(bool removeSource)
    {
        await using var host = await SyntheticHost.StartAsync(o => o.PreloadGameData = false);
        SureHitRolls.Install(host.App);
        await using var bot = await host.InWorldAsync("RemovalPlayer");
        var player = host.Service<SessionRegistry>().All().Single(s => s.Role != null);
        var source = await RemovalSourceAsync(host, player);
        long before = 0;
        await host.Service<GameLoop>().InvokeAsync(async () =>
        {
            var character = host.Service<CharacterService>();
            // 最小 SyntheticData 沒有職業／等級 HP 配置；明確提供自造最大 HP，讓正向對照有血可扣。
            // 使用原始屬性提供者，不能固定覆蓋目前 HP，否則會掩蓋實際扣血。
            character.ExtraAttributes.Add(s => ReferenceEquals(s, player) ? [(101, 10000d)] : []);
            await character.SetHpSpAsync(player, long.MaxValue, null);
            before = player.CurrentHp!.Value;
            Assert.True(before >= 10000, "正向對照必須先建立非零最大 HP。");
            await host.Service<MonsterService>().Attack!(source, player);
            Assert.True(host.Service<CombatService>().HasPendingMapWork(source.RuntimeMap));
            if (removeSource) await host.Service<World>().RemoveUnitAsync(source);
        });
        await AssertRemovalSwingBeganAsync(bot, source.Uid);
        await PendingSwingsDrainedAsync(host, source.RuntimeMap);
        long after = await host.Service<GameLoop>().InvokeAsync(() => Task.FromResult(player.CurrentHp!.Value));
        if (removeSource)
        {
            Assert.Equal(before, after);
            AssertNoRemovalHit(bot, source.Uid);
        }
        else
        {
            var hit = await AssertRemovalHitAsync(bot, source.Uid);
            Assert.Contains(hit.Dsts, target => target.DstID == player.Role!.Id && target.Damage > 0);
            Assert.True(after < before, $"正向對照必須確實扣血：施放前 {before}，命中後 {after}。");
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ACompanionSwingAlreadyStartedCannotHitAfterSourceRemoval(bool removeSource)
    {
        await using var host = await SyntheticHost.StartAsync(o => o.PreloadGameData = false);
        SureHitRolls.Install(host.App);
        await using var bot = await host.InWorldAsync("RemovalCompanion");
        var player = host.Service<SessionRegistry>().All().Single(s => s.Role != null);
        var source = await RemovalSourceAsync(host, player);
        var template = source.Template with { NpcId = 991104, MaxHp = 10000, Camp = 1, Skills = null };
        var companion = new CompanionUnit(new CompanionUnit.Kit(991104, template, [], 0, 1000, 1000, 0, 0),
            host.Service<World>(), host.Service<CombatService>())
        {
            Uid = CompanionUnit.UidBase + 77, SceneEntry = player.SceneEntry, RuntimeMap = player.RuntimeMap,
            X = source.X, Y = source.Y, Z = source.Z, Point = 1, HomeX = source.X, HomeZ = source.Z,
            Credit = () => player, Fell = _ => Task.CompletedTask,
        };
        long before = companion.Hp;
        await host.Service<GameLoop>().InvokeAsync(async () =>
        {
            await host.Service<MonsterService>().AttackCompanion!(source, companion);
            Assert.True(host.Service<CombatService>().HasPendingMapWork(source.RuntimeMap));
            if (removeSource) await host.Service<World>().RemoveUnitAsync(source);
        });
        await AssertRemovalSwingBeganAsync(bot, source.Uid);
        await PendingSwingsDrainedAsync(host, source.RuntimeMap);
        long after = await host.Service<GameLoop>().InvokeAsync(() => Task.FromResult(companion.Hp));
        if (removeSource)
        {
            Assert.Equal(before, after);
            AssertNoRemovalHit(bot, source.Uid);
        }
        else
        {
            Assert.True(after < before, "正向對照必須確實命中同伴，避免空測試誤判通過。");
            await AssertRemovalHitAsync(bot, source.Uid);
        }
    }

    private static Task<MonsterUnit> RemovalSourceAsync(SyntheticHost host, GameSession player) =>
        host.Service<GameLoop>().InvokeAsync(async () =>
        {
            Row(host.Service<GameData>(), "SkillConfig", RemovalSwing, new
            {
                _iID = RemovalSwing, _iSkillID = 99110, _iLevel = 1, _iBasicType = 1,
                _iRelease = 1, _iDmgType = 1, _iActionTime = 900, _iDistanceMax = 500,
                _kDamageParam1 = new[] { 10000 }, _kDamageParam2 = new[] { 50 },
            });
            var source = StateBoss(instance: 0);
            source.SceneEntry = player.SceneEntry;
            source.X = player.Position.X; source.Y = player.Position.Y; source.Z = player.Position.Z;
            source.BossSkills = null;
            source.Template = source.Template with
            {
                NpcId = 991103,
                Atk = 5,
                NormalSkill = RemovalSwing,
                SkillMs = 900,
                HitPointSeconds = 0.5f,
                AttackRange = 5,
                Skills = null
            };
            // 不登錄怪物 AI：只由測試呼叫實際普攻入口，避免額外攻擊干擾斷言。
            await host.Service<World>().AddUnitAsync(source);
            return source;
        });

    private static async Task PendingSwingsDrainedAsync(SyntheticHost host, RuntimeMapKey map)
    {
        var deadline = DateTime.UtcNow + RemovalWait;
        while (await host.Service<GameLoop>().InvokeAsync(() =>
            Task.FromResult(host.Service<CombatService>().HasPendingMapWork(map))))
        {
            Assert.True(DateTime.UtcNow < deadline, "延遲普攻工作未於期限內完成。");
            await Task.Delay(20);
        }
    }

    private static async Task AssertRemovalSwingBeganAsync(BotClient bot, long source) =>
        _ = await bot.Inbox.WaitAsync(e => e.IsGame(CombatService.UseInSkill)
            && UseInSkillInfo.Parser.ParseFrom(e.Body) is var use && use.SrcID == source && use.SkillID == RemovalSwing, RemovalWait);

    private static async Task<SkillHitInfo> AssertRemovalHitAsync(BotClient bot, long source)
    {
        var packet = await bot.Inbox.WaitAsync(e => e.IsGame(CombatService.SkillHit)
            && SkillHitInfo.Parser.ParseFrom(e.Body) is var hit && hit.SrcID == source && hit.SkillId == RemovalSwing, RemovalWait);
        return SkillHitInfo.Parser.ParseFrom(packet.Body);
    }

    private static void AssertNoRemovalHit(BotClient bot, long source) =>
        Assert.DoesNotContain(bot.Inbox.Snapshot(), e => e.IsGame(CombatService.SkillHit)
            && SkillHitInfo.Parser.ParseFrom(e.Body) is var hit && hit.SrcID == source && hit.SkillId == RemovalSwing);
}

using Microsoft.Data.Sqlite;
using Romsg;
using Ro3Emu.Server.Accounts;
using Ro3Emu.Server.Data;
using Ro3Emu.Server.Game;
using Ro3Emu.Server.Game.Ownership;
using Ro3Emu.Server.Maps;

namespace Ro3Emu.Tests;

public sealed partial class PartyExperiencePipelineTests
{
    /// <summary>僅鎖定現行本地政策；遠距、死亡、各人等級不是原服分享資格的證明。</summary>
    [Fact]
    public async Task RecipientPolicySeparatesRuntimeAndAttachmentFromUiDistanceAndDeath()
    {
        string fixture = CreateFixture();
        try
        {
            await using var host = await SyntheticHost.StartAsync(o => o.GameDataDir = fixture);
            await using var alice = await host.InWorldAsync("PolicyAlice");
            await using var bob = await host.InWorldAsync("PolicyBob");
            await using var carol = await host.InWorldAsync("PolicyCarol");
            await CreateParty(alice, bob, carol);
            var a = Session(host, (long)alice.Self!.Uuid);
            var b = Session(host, (long)bob.Self!.Uuid);
            var teams = host.Service<TeamService>();
            await host.Service<GameLoop>().InvokeAsync(() =>
            {
                var position = b.Position;
                bool dead = b.Dead, loading = b.SceneLoading;
                long? hp = b.CurrentHp;
                int line = b.LineNumber;
                long instance = b.InstanceId;
                var progress = b.Progress ?? throw new InvalidOperationException("缺少測試角色進度");
                var attachment = b.Attachment ?? throw new InvalidOperationException("缺少測試租約");
                int level = progress.Level, job = progress.JobLevel;
                bool cancelled = attachment.Cancelled;
                void Eligible(int count)
                {
                    Assert.Equal(count, teams.ExperienceRecipients(a).Count);
                    Assert.Equal(count == 3 ? 17000 : 12000, teams.ExpBonus(a));
                }
                try
                {
                    b.Position = new Vec3 { X = 10000, Z = 10000 };
                    b.Dead = true; b.CurrentHp = 0;
                    progress.Level = 10; progress.JobLevel = 8;
                    Eligible(3); // 目前不以視野55m、死亡或個人等級排除，不宣稱原服如此。
                    b.LineNumber = line + 1;
                    Assert.Equal(a.SceneEntry, b.SceneEntry);
                    Assert.NotEqual(a.WorldScene, b.WorldScene);
                    Eligible(2); b.LineNumber = line;
                    b.SceneLoading = true; Eligible(2); b.SceneLoading = loading;
                    b.InstanceId = 777; Eligible(2); b.InstanceId = instance;
                    attachment.Cancelled = true; Eligible(2); attachment.Cancelled = cancelled;
                    b.Progress = null; Eligible(2); b.Progress = progress;
                }
                finally
                {
                    b.Position = position; b.Dead = dead; b.CurrentHp = hp; b.SceneLoading = loading;
                    b.LineNumber = line; b.InstanceId = instance; b.Progress = progress;
                    progress.Level = level; progress.JobLevel = job; attachment.Cancelled = cancelled;
                }
                return Task.CompletedTask;
            });
        }
        finally { SqliteConnection.ClearAllPools(); Directory.Delete(fixture, true); }
    }

    [Fact]
    public async Task IndependentBaseAndJobRatesApplyOnceBeforeSlotRemainders()
    {
        string fixture = CreateFixture();
        try
        {
            await using var host = await SyntheticHost.StartAsync(o =>
            { o.GameDataDir = fixture; o.RewardRates.BaseExperience = 2m; o.RewardRates.JobExperience = 3m; });
            await using var alice = await host.InWorldAsync("RateAlice");
            await using var bob = await host.InWorldAsync("RateBob");
            await using var carol = await host.InWorldAsync("RateCarol");
            await CreateParty(alice, bob, carol);
            long[] ids = [(long)alice.Self!.Uuid, (long)bob.Self!.Uuid, (long)carol.Self!.Uuid];
            var before = ids.Select(id => RateProgress(host, id)).ToArray();
            var killer = Session(host, ids[0]); var monster = NewPipelineMonster(killer, 990306);
            var reward = host.Service<RewardService>();
            await host.Service<GameLoop>().InvokeAsync(() => reward.KilledAsync(killer, monster));
            // 自造掉落7乘配置17000/10000先取整11；Base乘2成22、Job乘3成33，再依席位分餘數。
            Assert.Equal(new long[] { 8, 7, 7 }, ids.Select((id, i) => RateProgress(host, id).Base - before[i].Base));
            Assert.Equal(new long[] { 11, 11, 11 }, ids.Select((id, i) => RateProgress(host, id).Job - before[i].Job));
            Assert.All(ids, id => Assert.Equal(2, host.Service<RoleStore>().LoadProgress(id).Level));
            string source = Assert.IsType<RewardSourceId>(monster.RewardSource).Key;
            var obligations = Obligations(host, source);
            Assert.All(obligations, row => Assert.True(row.Delivered));
            var settled = ids.Select(id => RateProgress(host, id)).ToArray();
            await host.Service<GameLoop>().InvokeAsync(() => reward.KilledAsync(killer, monster));
            Assert.Equal(settled, ids.Select(id => RateProgress(host, id)).ToArray());
            Assert.Equal(obligations.ToArray(), Obligations(host, source).ToArray());
        }
        finally { SqliteConnection.ClearAllPools(); Directory.Delete(fixture, true); }
    }

    private static (long Base, long Job) RateProgress(SyntheticHost host, long role)
    {
        var progress = host.Service<RoleStore>().LoadProgress(role);
        var data = host.Service<GameData>();
        // 自造首級門檻為6；倍率份額7／8會升至2級，須把已消耗門檻加回入帳總額。
        long consumed = Enumerable.Range(1, Math.Max(0, progress.Level - 1))
            .Sum(level => data.Row("BaseLvConfig", level) is { } row ? GameData.Int(row, "_iBaseExp") : 0);
        Assert.Equal(1, progress.JobLevel); // 本案JOB份額11仍低於自造門檻100。
        return (checked(consumed + progress.Exp), progress.JobExp);
    }

    [Fact]
    public async Task CommittedShareSurvivesRecipientHandoverAndRestartExactlyOnce()
    {
        string fixture = CreateFixture();
        try
        {
            await using var host = await SyntheticHost.StartAsync(o => o.GameDataDir = fixture);
            long role; string source; (long Base, long Job) before;
            {
                await using var alice = await host.InWorldAsync("DebtAlice");
                await using var bob = await host.InWorldAsync("DebtBob");
                await using var carol = await host.InWorldAsync("DebtCarol");
                await CreateParty(alice, bob, carol);
                role = (long)bob.Self!.Uuid; before = Progress(host, role);
                var killer = Session(host, (long)alice.Self!.Uuid); var member = Session(host, role);
                var monster = NewPipelineMonster(killer, 990307); var reward = host.Service<RewardService>();
                Task? detach = null;
                await host.Service<GameLoop>().InvokeAsync(async () =>
                {
                    await reward.PrepareKilledAsync(killer, monster); // 真實擊殺交易先凍結收件人／份額。
                    detach = host.Service<RoleHandover>().Begin(member); // 在發送死亡通知前撤銷同一租約。
                    await reward.KilledAsync(killer, monster);
                });
                await (detach ?? throw new InvalidOperationException("沒有進入交接")).WaitAsync(Wait);
                source = Assert.IsType<RewardSourceId>(monster.RewardSource).Key;
                Assert.Equal(before, Progress(host, role));
                var pending = Assert.Single(Obligations(host, source), row => row.Role == role);
                Assert.False(pending.Delivered); Assert.Equal(4, pending.Base); Assert.Equal(4, pending.Job);
            }
            await host.RestartAsync();
            await using var again = await host.InWorldAsync("DebtBob", create: false);
            Assert.Equal((before.Base + 4, before.Job + 4), Progress(host, role));
            var resumed = Session(host, role);
            await host.Service<GameLoop>().InvokeAsync(async () =>
            {
                await host.Service<RewardService>().RecoverPartyExperienceAsync(resumed);
                await host.Service<RewardService>().RecoverPartyExperienceAsync(resumed);
            });
            Assert.Equal((before.Base + 4, before.Job + 4), Progress(host, role));
            Assert.True(Assert.Single(Obligations(host, source), row => row.Role == role).Delivered);
        }
        finally { SqliteConnection.ClearAllPools(); Directory.Delete(fixture, true); }
    }

    private static MonsterUnit NewPipelineMonster(GameSession killer, long uid) => new()
    {
        Uid = uid, SceneEntry = killer.SceneEntry, RuntimeMap = killer.RuntimeMap,
        Template = new MonsterTemplate(Npc, 1, 10, 0, 1, 1, 0, 0, 1, 0, 0, 100, 1, 1, 1, 1,
            false, 0, 1000, 5, 10, 0, 1000, .1f, 1000, 1, 500),
        Group = new SpawnGroup(-1, false, 1, 0, [], []), Home = new SpawnPoint(-1, 0, 0, 0, 0, Npc), Hp = 0
    };
}

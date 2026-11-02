using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;
using Ro3Emu.Server.Accounts;
using Ro3Emu.Server.Game;
using Ro3Emu.Server.Game.Ownership;

namespace Ro3Emu.Tests;

public sealed partial class PartyExperiencePipelineTests
{
    [Fact]
    public async Task ConvertedJobExperienceIsCreditedOnceToKillerAndPartyMember()
    {
        string fixture = ConversionFixture();
        try
        {
            await using var host = await SyntheticHost.StartAsync(o => o.GameDataDir = fixture);
            await using var alice = await host.InWorldAsync("ConvertAlice");
            await using var bob = await host.InWorldAsync("ConvertBob");
            await CreateParty(alice, bob);
            long[] ids = [(long)alice.Self!.Uuid, (long)bob.Self!.Uuid];
            var before = ids.Select(id => Progress(host, id)).ToArray();
            var killer = Session(host, ids[0]);
            var monster = NewPipelineMonster(killer, 990308);
            var rewards = host.Service<RewardService>();
            await host.Service<GameLoop>().InvokeAsync(() => rewards.KilledAsync(killer, monster));
            // 自造100 Base × 兩人120% = 120，Job按20%為24，凍結後各分60／12。
            Assert.Equal([60, 60], ids.Select((id, i) => Progress(host, id).Base - before[i].Base));
            Assert.Equal([12, 12], ids.Select((id, i) => Progress(host, id).Job - before[i].Job));
            string source = Assert.IsType<RewardSourceId>(monster.RewardSource).Key;
            var obligation = Assert.Single(Obligations(host, source));
            Assert.Equal((ids[1], 60L, 12L, true), obligation);
            var settled = ids.Select(id => Progress(host, id)).ToArray();
            await host.Service<GameLoop>().InvokeAsync(async () =>
            {
                await rewards.KilledAsync(killer, monster);
                await rewards.RecoverPartyExperienceAsync(Session(host, ids[1]));
            });
            Assert.Equal(settled, ids.Select(id => Progress(host, id)).ToArray());
            foreach (var bot in new[] { alice, bob })
            {
                await bot.HeartbeatAsync();
                Assert.Contains(bot.Inbox.Snapshot(), packet => packet.IsGame(CharacterService.RoleBaseInfoSync)
                    && Romsg.RoleBaseInfoIncrement.Parser.ParseFrom(packet.Body) is { HasJobExp: true, JobExp: 12 });
            }
        }
        finally { SqliteConnection.ClearAllPools(); Directory.Delete(fixture, true); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FrozenJobShareSurvivesOfflineRestartWithoutReconversion(bool legacyShare)
    {
        string fixture = ConversionFixture();
        try
        {
            await using var host = await SyntheticHost.StartAsync(o => o.GameDataDir = fixture);
            long role; string source; (long Base, long Job) before;
            long frozenJob = legacyShare ? 60 : 12;
            {
                await using var alice = await host.InWorldAsync("FrozenAlice");
                await using var bob = await host.InWorldAsync("FrozenBob");
                await CreateParty(alice, bob);
                role = (long)bob.Self!.Uuid; before = Progress(host, role);
                var killer = Session(host, (long)alice.Self!.Uuid);
                var member = Session(host, role);
                var monster = NewPipelineMonster(killer, 990309);
                var rewards = host.Service<RewardService>();
                // 舊版已凍結金額的相容性案例：只替換roll邊界，實際交易、義務、重啟回放都走產品。
                if (legacyShare) rewards.ResolveKillForTest = (_, _) => ([], 120, 120);
                Task? detach = null;
                await host.Service<GameLoop>().InvokeAsync(async () =>
                {
                    await rewards.PrepareKilledAsync(killer, monster);
                    detach = host.Service<RoleHandover>().Begin(member);
                    await rewards.KilledAsync(killer, monster);
                });
                await (detach ?? throw new InvalidOperationException("未開始角色交接")).WaitAsync(Wait);
                source = Assert.IsType<RewardSourceId>(monster.RewardSource).Key;
                Assert.Equal(before, Progress(host, role));
                Assert.Equal((role, 60L, frozenJob, false), Assert.Single(Obligations(host, source)));
            }
            await host.RestartAsync();
            await using var resumed = await host.InWorldAsync("FrozenBob", create: false);
            Assert.Equal((before.Base + 60, before.Job + frozenJob), Progress(host, role));
            await host.Service<GameLoop>().InvokeAsync(async () =>
            {
                var rewards = host.Service<RewardService>();
                await rewards.RecoverPartyExperienceAsync(Session(host, role));
                await rewards.RecoverPartyExperienceAsync(Session(host, role));
            });
            Assert.Equal((before.Base + 60, before.Job + frozenJob), Progress(host, role));
            Assert.Equal((role, 60L, frozenJob, true), Assert.Single(Obligations(host, source)));
        }
        finally { SqliteConnection.ClearAllPools(); Directory.Delete(fixture, true); }
    }

    private static string ConversionFixture()
    {
        string root = CreateFixture();
        string config = Path.Combine(root, "config");
        string paramPath = Path.Combine(config, "PublicParamConfig.json");
        var parameters = JsonNode.Parse(File.ReadAllText(paramPath))!.AsObject();
        parameters["BaseToJobConversionPercent"] = 2000;
        File.WriteAllText(paramPath, parameters.ToJsonString());
        // 獨立合成案例避免升級截斷，並用非零轉換抓出重複發給隊友的錯誤。
        string levelsPath = Path.Combine(config, "BaseLvConfig.json");
        var levels = JsonNode.Parse(File.ReadAllText(levelsPath))!.AsObject();
        levels["1"]!["_iBaseExp"] = 10000;
        File.WriteAllText(levelsPath, levels.ToJsonString());
        Replace("JobLvConfig", "101", new { _iID = 101, _iMaxJobLv = 10, _iJobeExp = 10000 });
        Replace("DropGroupConfig", Curve.ToString(System.Globalization.CultureInfo.InvariantCulture), new[]
        {
            new { _kDropItem = 100, _iWeights = 1, _iMinLevelLimit = 1, _iMaxLevelLimit = 5, _iMinNumber = 100, _iMaxNumber = 100 }
        });
        return root;

        void Replace(string table, string key, object row)
        {
            string path = Path.Combine(config, table + ".json");
            var content = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
            content[key] = System.Text.Json.JsonSerializer.SerializeToNode(row);
            File.WriteAllText(path, content.ToJsonString());
        }
    }
}

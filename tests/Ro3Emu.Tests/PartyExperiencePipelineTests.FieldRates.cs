using System.Globalization;
using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;
using Ro3Emu.Server;
using Ro3Emu.Server.Game;

namespace Ro3Emu.Tests;

public sealed partial class PartyExperiencePipelineTests
{
    [Fact]
    public async Task FieldRateAndIndependentRatesFreezeWithTheCommittedPartyOutcome()
    {
        string fixture = CreateFixture();
        try
        {
            string npcPath = Path.Combine(fixture, "config", "NPCConfig.json");
            var npcs = JsonNode.Parse(File.ReadAllText(npcPath))!.AsObject();
            npcs[Npc.ToString(CultureInfo.InvariantCulture)]!["_iWildMonsters"] = 1;
            File.WriteAllText(npcPath, npcs.ToJsonString());
            await using var host = await SyntheticHost.StartAsync(o =>
            {
                o.GameDataDir = fixture;
                o.FieldExpRates = [new FieldExpRule { MinLevel = 1, MaxLevel = 1, Percent = 300 }];
                o.RewardRates.BaseExperience = 2m;
                o.RewardRates.JobExperience = 3m;
            });
            await using var alice = await host.InWorldAsync("FieldRateAlice");
            await using var bob = await host.InWorldAsync("FieldRateBob");
            await using var carol = await host.InWorldAsync("FieldRateCarol");
            await CreateParty(alice, bob, carol);
            long[] ids = [(long)alice.Self!.Uuid, (long)bob.Self!.Uuid, (long)carol.Self!.Uuid];
            var before = ids.Select(id => RateProgress(host, id)).ToArray();
            var killer = Session(host, ids[0]);
            var monster = NewPipelineMonster(killer, 990308);
            var reward = host.Service<RewardService>();
            await host.Service<GameLoop>().InvokeAsync(async () =>
            {
                await reward.PrepareKilledAsync(killer, monster);
                // 已提交來源的隊友義務包含原倍率；正式發送死亡通知前改設定不能重算。
                reward.SetFieldExpRules([new FieldExpRule { MinLevel = 1, MaxLevel = 99, Percent = 900 }]);
                await reward.KilledAsync(killer, monster);
            });

            // 全為自造配置：7 * 17000 / 10000 先成 11，再乘野外 300% 成 33。
            // Base 乘 2、Job 乘 3 後分三席；這只驗本地交易契約，不證原服組隊公式。
            Assert.Equal([22L, 22L, 22L], ids.Select((id, i) => RateProgress(host, id).Base - before[i].Base));
            Assert.Equal([33L, 33L, 33L], ids.Select((id, i) => RateProgress(host, id).Job - before[i].Job));
            string source = Assert.IsType<RewardSourceId>(monster.RewardSource).Key;
            var obligations = Obligations(host, source);
            Assert.Equal(2, obligations.Count);
            Assert.All(obligations, row =>
            {
                Assert.True(row.Delivered);
                Assert.Equal(22, row.Base);
                Assert.Equal(33, row.Job);
            });
            var settled = ids.Select(id => RateProgress(host, id)).ToArray();
            await host.Service<GameLoop>().InvokeAsync(async () =>
            {
                await reward.PrepareKilledAsync(killer, monster);
                await reward.KilledAsync(killer, monster);
            });
            Assert.Equal(settled, ids.Select(id => RateProgress(host, id)).ToArray());
            Assert.Equal(obligations, Obligations(host, source));
        }
        finally { SqliteConnection.ClearAllPools(); Directory.Delete(fixture, true); }
    }
}

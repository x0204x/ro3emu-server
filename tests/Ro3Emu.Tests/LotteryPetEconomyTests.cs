using System.Text.Json;
using Google.Protobuf;
using Microsoft.Extensions.DependencyInjection;
using Romsg;
using Ro3Emu.Server.Accounts;
using Ro3Emu.Server.Data;
using Ro3Emu.Server.Economy;
using Ro3Emu.Server.Game;
using Ro3Emu.LoadTest;

namespace Ro3Emu.Tests;

public sealed partial class LotteryActivityTests
{
    [ClientDataFact]
    public async Task GuaranteedDuplicatePetDrawHasExactTicketSinkAndConfiguredRefundSources()
    {
        await using var bot = await InWorld(create: true);
        await Gm("!lvl 99");
        await Gm("!quest done 10105010");
        await Gm("!item 1250709 21");
        var session = _app.Services.GetRequiredService<SessionRegistry>().All().Single(s => s.Role?.Name == "Lottery");
        var data = _app.Services.GetRequiredService<GameData>();
        var draw = data.Row("DrawConfig", 200)!.Value;
        var groups = draw.GetProperty("_kSubBanner").EnumerateArray()
            .Select(x => (long)(GameData.Number(x) ?? 0)).ToHashSet();
        var petIds = data.Table("DrawPoolConfig").Values
            .Where(row => groups.Contains(GameData.Int(row, "_iGroupID")))
            .SelectMany(row => row.GetProperty("_kItem").EnumerateArray())
            .Where(tuple => tuple.GetArrayLength() == 3 && GameData.Number(tuple[0]) == 1)
            .Select(tuple => (int)(GameData.Number(tuple[1]) ?? 0)).Distinct().ToArray();
        Assert.NotEmpty(petIds);
        // Fixture-only ownership before the opening cut makes every pet roll a duplicate conversion.
        await _app.Services.GetRequiredService<GameLoop>().InvokeAsync(() =>
        {
            var state = session.Pets!.Clone();
            foreach (int pet in petIds) state.ListPet[pet] = new PetData { PetId = pet, Level = 1, Star = 1 };
            session.Pets = state;
            _app.Services.GetRequiredService<RoleStore>().SavePets(session.Role!.Id, state.ToByteArray());
            return Task.CompletedTask;
        });
        var catalog = EconomyCatalog.Load(Path.Combine(RepoRoot(), "data"));
        var opening = S3EconomyReader.Read(_dataDir, catalog);
        int initialTickets = session.Items.Where(i => i.Entry == 1250709).Sum(i => i.Stack);
        var results = new List<LotteryResp>();
        for (int index = 1; index <= 2; index++)
        {
            await bot.SendGameAsync(LotteryService.DrawRequest,
                new LotteryReq { PoolId = 200, Type = ELotteryPoolType.GachaCountMid });
            var response = await Next(bot, LotteryService.DrawRequest, LotteryResp.Parser,
                r => r.PoolData.PoolId == 200 && r.PoolData.DailyCount == index * 10);
            Assert.Equal(0, response.Result);
            Assert.All(response.ItemList.Where(x => x.IsPet), x => Assert.True(x.IsBroken));
            results.Add(response);
        }
        Assert.Contains(results.SelectMany(r => r.ItemList), x => x.IsPet);
        Assert.Equal(initialTickets - 20, session.Items.Where(i => i.Entry == 1250709).Sum(i => i.Stack));
        var closing = S3EconomyReader.Read(_dataDir, catalog);
        var window = S3EconomyReader.ReadLedger(_dataDir, catalog, opening, closing)!;
        var covered = window.Receipts.Select(r => EconomyReceiptReader.Read(Path.Combine(_dataDir, "ro3emu.db"), r.OperationId)!)
            .Where(r => r.CommandKind == "cd13b/pet-lottery").ToArray();
        Assert.Equal(results.Count(r => r.ItemList.Any(x => x.IsPet)), covered.Length);
        Assert.All(covered, receipt =>
        {
            using var outcome = JsonDocument.Parse(receipt.Outcome);
            var expected = new Dictionary<int, long>();
            foreach (var result in outcome.RootElement.GetProperty("Results").EnumerateArray())
            {
                int item = result.GetProperty("ItemId").GetInt32();
                int count = result.GetProperty("ItemCount").GetInt32();
                if (result.GetProperty("IsPet").GetBoolean())
                {
                    Assert.True(result.GetProperty("IsBroken").GetBoolean());
                    var refund = data.Row("PetBaseConfig", item)!.Value.GetProperty("_kDecompose");
                    item = (int)(GameData.Number(refund[0]) ?? 0);
                    count = (int)(GameData.Number(refund[1]) ?? 0);
                }
                expected[item] = expected.GetValueOrDefault(item) + count;
            }
            var bonus = draw.GetProperty("_kRollNum");
            if (bonus.GetArrayLength() == 2)
            {
                int item = (int)(GameData.Number(bonus[0]) ?? 0);
                expected[item] = expected.GetValueOrDefault(item) + (long)(GameData.Number(bonus[1]) ?? 0) * 10;
            }
            var legs = window.Legs.Where(x => x.OperationId == receipt.OperationId).ToArray();
            var debit = Assert.Single(legs, x => x.Kind == EconomyLegKind.Sink);
            Assert.Equal((1250709L, -10L), (debit.Asset.Id, debit.Delta));
            var credits = legs.Where(x => x.Kind == EconomyLegKind.Source)
                .GroupBy(x => (int)x.Asset.Id).ToDictionary(g => g.Key, g => g.Sum(x => x.Delta));
            Assert.Equal(expected.OrderBy(x => x.Key), credits.OrderBy(x => x.Key));
        });
        Assert.Equal(session.Pets!.ToByteArray(), _app.Services.GetRequiredService<RoleStore>().LoadPets(session.Role!.Id));
    }
}

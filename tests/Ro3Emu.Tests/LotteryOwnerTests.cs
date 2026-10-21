using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Romsg;
using Ro3Emu.Bot;
using Ro3Emu.Server.Accounts;
using Ro3Emu.Server.Data;
using Ro3Emu.Server.Economy;
using Ro3Emu.Server.Game;

namespace Ro3Emu.Tests;

public sealed partial class LotteryActivityTests
{
    private async Task<BotClient> OtherWorld()
    {
        var bot = new BotClient(_http, File.ReadAllText(Path.Combine(_dataDir, "gate_rsa_public.pem")));
        await bot.LoginAsync("lotteryother", "pw");
        await bot.EnterGateAsync();
        long id = (await bot.CreateRoleAsync("LotteryOther")).RoleBaseInfo.Id;
        await bot.EnterWorldAsync(id);
        await Next(bot, LotteryService.LotterySync, DBLotteryDataIncrement.Parser, sync => sync.SyncAllMark);
        return bot;
    }

    private long LotteryReceipts()
    {
        using var db = new SqliteConnection($"Data Source={Path.Combine(_dataDir, "ro3emu.db")}");
        db.Open();
        using var cmd = db.CreateCommand();
        cmd.CommandText = "SELECT count(*) FROM operation_receipt WHERE command_kind='event/lottery-draw'";
        return (long)cmd.ExecuteScalar()!;
    }

    private (string Source, long Legs) LatestLotteryReceipt()
    {
        using var db = new SqliteConnection($"Data Source={Path.Combine(_dataDir, "ro3emu.db")}");
        db.Open();
        using var cmd = db.CreateCommand();
        cmd.CommandText = "SELECT source_key,leg_count FROM operation_receipt WHERE command_kind='event/lottery-draw' ORDER BY commit_seq DESC LIMIT 1";
        using var row = cmd.ExecuteReader();
        Assert.True(row.Read());
        return (row.GetString(0), row.GetInt64(1));
    }

    [ClientDataFact]
    public async Task HeldDrawKeepsTicketPityAndResultUnpublishedWhileOtherRoleProgresses()
    {
        await using var buyer = await InWorld(create: true);
        await using var other = await OtherWorld();
        await Gm("!lvl 99");
        await Gm("!item 1250717 1");
        var session = _app.Services.GetRequiredService<SessionRegistry>().All()
            .Single(s => s.Role?.Name == "Lottery");
        int ticketBefore = session.Items.Where(i => i.Entry == 1250717).Sum(i => i.Stack);
        var itemsBefore = session.Items.GroupBy(i => i.Entry).ToDictionary(g => g.Key, g => g.Sum(i => i.Stack));
        var moneyBefore = session.Progress!.Currency.ToDictionary(pair => pair.Key, pair => pair.Value);
        int countBefore = session.Lottery!.LotteryPool[201].DailyCount;
        long receiptsBefore = LotteryReceipts();
        var catalog = EventEconomy.Catalog(_app.Services.GetRequiredService<GameData>());
        string database = _app.Services.GetRequiredService<AccountStore>().DatabasePath;
        var opening = EconomySnapshotReader.Read(database, catalog, "auto");
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var accounts = _app.Services.GetRequiredService<AccountStore>();
        accounts.BeforeOwnedCommitForTest = label =>
        {
            if (label != "economy/event/lottery-draw") return Task.CompletedTask;
            entered.TrySetResult();
            return release.Task;
        };
        try
        {
            await buyer.SendGameAsync(LotteryService.DrawRequest,
                new LotteryReq { PoolId = 201, Type = ELotteryPoolType.GachaCountMin });
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(ticketBefore, session.Items.Where(i => i.Entry == 1250717).Sum(i => i.Stack));
            Assert.Equal(countBefore, session.Lottery.LotteryPool[201].DailyCount);
            Assert.Equal(receiptsBefore, LotteryReceipts());
            await other.SendGameAsync(LotteryService.DrawRequest,
                new LotteryReq { PoolId = 201, Type = ELotteryPoolType.GachaCountMin });
            Assert.NotEqual(0, (await Next(other, LotteryService.DrawRequest, LotteryResp.Parser)).Result);
        }
        finally { release.TrySetResult(); accounts.BeforeOwnedCommitForTest = null; }
        var result = await Next(buyer, LotteryService.DrawRequest, LotteryResp.Parser);
        Assert.Equal(0, result.Result);
        Assert.Single(result.ItemList);
        Assert.Equal(ticketBefore - 1, session.Items.Where(i => i.Entry == 1250717).Sum(i => i.Stack));
        Assert.Equal(countBefore + 1, session.Lottery.LotteryPool[201].DailyCount);
        var item = result.ItemList[0];
        if (item.IsPet) Assert.True(session.Pets!.ListPet.ContainsKey(item.ItemId));
        else
        {
            var row = _app.Services.GetRequiredService<GameData>().Row("ItemConfig", item.ItemId);
            Assert.NotNull(row);
            if (GameData.Int(row.Value, "_iType") == 1)
                Assert.True(session.Progress.Currency.GetValueOrDefault(item.ItemId)
                    >= moneyBefore.GetValueOrDefault(item.ItemId) + item.ItemCount);
            else
                Assert.True(session.Items.Where(i => i.Entry == item.ItemId).Sum(i => i.Stack)
                    >= itemsBefore.GetValueOrDefault(item.ItemId) + item.ItemCount);
        }
        Assert.Equal(receiptsBefore + 1, LotteryReceipts());
        var closing = EconomySnapshotReader.Read(database, catalog, "auto");
        var ledger = EconomyLedgerReader.Read(database, catalog, "auto", opening.Cut.CommitSequence!.Value,
            closing.Cut.CommitSequence!.Value);
        Assert.True(ledger.CompleteHistory, string.Join("; ", ledger.Issues));
        var exactness = EconomyExactnessChecker.Check(opening, closing, ledger);
        Assert.All(exactness.Assets, asset => Assert.Equal("0", asset.Residual));
        Assert.All(exactness.Locations, location => Assert.Equal("0", location.Residual));
        Assert.DoesNotContain(exactness.Issues, issue => issue.Kind is EconomyIssueKind.Corruption or EconomyIssueKind.UnknownSchema);
        var source = LatestLotteryReceipt();
        Assert.StartsWith($"lottery/draw/{session.Role!.Id}/", source.Source);
        Assert.True(source.Legs >= 1); // ticket sink; pet-only draws carry the resolved pet in the outcome
        await buyer.SendGameAsync(LotteryService.DrawRequest,
            new LotteryReq { PoolId = 201, Type = ELotteryPoolType.GachaCountMin });
        Assert.Equal(ErrorCodes.ItemsShort, (await Next(buyer, LotteryService.DrawRequest, LotteryResp.Parser)).Result);
        Assert.Equal(receiptsBefore + 1, LotteryReceipts());
    }
}

using Romsg;
using Ro3Emu.Bot;
using Ro3Emu.Server.Accounts;
using Ro3Emu.Server.Game;
using Ro3Emu.Server.Maps;

namespace Ro3Emu.Tests;

public sealed class QuestSubmitClientTests
{
    private const long Trade = 201120, TradeMap = 10600, TradePoint = 2010801;
    private const int Pearl = 1253001, Clover = 1253002;
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(15);

    /// <summary>真實資料的交付路由測試；前置接取／材料是測試安排，不是完整劇情或原服扣料語意驗收。</summary>
    [ClientDataFact]
    public async Task TheTwoSlotNpcTradeRechecksItsPointAndPaysTheLocalInferredCostsOnce()
    {
        await using var host = await SyntheticHost.StartAsync(options => options.GameDataDir = Path.Combine(ClientData.RepoRoot, "data"));
        long role;
        await using (var bot = await host.InWorldAsync("NpcTrade"))
        {
            role = (long)bot.Self!.Uuid;
            await QuestProbe.AcceptAsync(host.App, host.Http, "npctrade", Trade);
            await host.Service<GameLoop>().InvokeAsync(async () =>
            {
                var session = Session(host, role);
                await host.Service<CharacterService>().GrantBoundAsync(session, [(Pearl, 2, 0), (Clover, 2, 0)]);
                await host.Service<TaskService>().RefreshAsync(session);
            });
            Assert.True(host.Service<TaskService>().TargetsSupported(Trade));
            Assert.Equal(new long[] { 1, 1 }, (await QuestProbe.DoneAsync(host.App, "npctrade", Trade)).CompleteNum);
            Assert.Empty((await Request(bot)).Data); // 尚未抵達配置的 NPC 點位。
            AssertStored(host, role, 2, 0);
            await host.Service<GameLoop>().InvokeAsync(() =>
            {
                var map = host.Service<WorldData>().Map(TradeMap) ?? throw new InvalidOperationException("缺少真實任務地圖");
                if (!map.Points.TryGetValue(TradePoint, out var point)) throw new InvalidOperationException("缺少真實任務 NPC 點位");
                var session = Session(host, role);
                session.SceneEntry = TradeMap;
                session.Position = new Vec3 { X = point.X, Y = point.Y, Z = point.Z };
                return Task.CompletedTask;
            });
            Assert.Single((await Request(bot, duplicate: true)).Data);
            AssertStored(host, role, 1, 1);
            Assert.Empty((await Request(bot)).Data);
            AssertStored(host, role, 1, 1);
        }
        await host.RestartAsync();
        await using var relog = await host.InWorldAsync("NpcTrade", create: false);
        Assert.Empty((await Request(relog)).Data);
        AssertStored(host, role, 1, 1);
    }

    private static GameSession Session(SyntheticHost host, long role) =>
        host.Service<SessionRegistry>().All().Single(session => session.Role?.Id == role);

    private static async Task<TaskDeliveryResp> Request(BotClient bot, bool duplicate = false)
    {
        var request = new TaskDeliveryReq { Req = { new TaskDeliverySingleReq { Id = Trade } } };
        if (duplicate) request.Req.Add(request.Req[0].Clone());
        await bot.SendGameAsync(TaskService.TaskDelivery, request);
        var packet = await bot.Inbox.WaitAsync(envelope => envelope.IsGame(TaskService.TaskDelivery), Wait);
        return TaskDeliveryResp.Parser.ParseFrom(packet.Body);
    }

    private static void AssertStored(SyntheticHost host, long role, int materials, long deliveries)
    {
        var items = host.Service<ItemStore>().Load(role);
        Assert.Equal(materials, items.Where(item => item.Entry == Pearl).Sum(item => item.Stack));
        Assert.Equal(materials, items.Where(item => item.Entry == Clover).Sum(item => item.Stack));
        var saved = host.Service<RoleStore>().LoadTasks(role) ?? throw new InvalidOperationException("沒有真實任務存檔");
        Assert.Equal(deliveries, DBTaskInfo.Parser.ParseFrom(saved).CompleteTaskNum.GetValueOrDefault(Trade));
        Assert.Equal(deliveries, QuestProbe.AwardSourceCount(host.App, role, Trade));
    }
}

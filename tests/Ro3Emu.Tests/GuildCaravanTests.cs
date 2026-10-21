using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using System.Text.Json;
using System.Text.Json.Nodes;
using Romsg;
using Ro3Emu.Bot;
using Ro3Emu.Server;
using Ro3Emu.Server.Accounts;
using Ro3Emu.Server.Economy;
using Ro3Emu.Server.Game;
using Ro3Emu.Server.Game.Ownership;

namespace Ro3Emu.Tests;

public sealed class GuildCaravanTests : IAsyncLifetime
{
    private GameSession Session(BotClient bot) => _app.Services.GetRequiredService<SessionRegistry>().All()
        .Single(s => s.Role?.Id == (long)bot.Self!.Uuid);
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "ro3emu-caravan-" + Guid.NewGuid().ToString("N"));
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(8);
    private readonly TestClock _clock = new();
    private WebApplication _app = null!;
    private string _http = "";

    private sealed class TestClock : TimeProvider
    {
        private long _seconds = new DateTimeOffset(2026, 10, 9, 4, 50, 0, TimeSpan.Zero).ToUnixTimeSeconds();
        public override DateTimeOffset GetUtcNow() => DateTimeOffset.FromUnixTimeSeconds(Interlocked.Read(ref _seconds));
        public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;
        public void Advance(int value) => Interlocked.Add(ref _seconds, value);
    }

    public async Task InitializeAsync()
    {
        await StartServerAsync();
    }

    private async Task StartServerAsync()
    {
        var (web, micro, game) = TestPorts.FreePorts();
        _app = ServerHost.Create([], o =>
        {
            o.DataDir = _dir; o.GameDataDir = Path.Combine(RepoRoot(), "data");
            o.HttpPort = web; o.MicroGatePort = micro; o.GameGatePort = game;
            o.SeasonStart = "2026-10-05"; o.PlayBook = 101;
        }, _clock);
        await _app.StartAsync();
        _http = $"http://127.0.0.1:{web}";
    }

    private async Task RestartAsync()
    {
        await _app.StopAsync(); await _app.DisposeAsync();
        ClearOwnPool();
        await StartServerAsync();
    }

    private void ClearOwnPool()
    {
        // This fixture restarts its server mid-test. A process-wide pool clear can invalidate
        // another parallel fixture's active SQLite handle.
        using var db = new Microsoft.Data.Sqlite.SqliteConnection(
            $"Data Source={Path.Combine(_dir, "ro3emu.db")}");
        Microsoft.Data.Sqlite.SqliteConnection.ClearPool(db);
    }

    public async Task DisposeAsync()
    {
        await _app.StopAsync(); await _app.DisposeAsync();
        ClearOwnPool();
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }

    private async Task<BotClient> InWorld(string name, bool create = true)
    {
        var bot = new BotClient(_http, File.ReadAllText(Path.Combine(_dir, "gate_rsa_public.pem")));
        await bot.LoginAsync(name.ToLowerInvariant(), "pw"); await bot.EnterGateAsync();
        long id = create ? (await bot.CreateRoleAsync(name)).RoleBaseInfo.Id : bot.Roles.Single().Id;
        await bot.EnterWorldAsync(id);
        return bot;
    }

    private static async Task<T> Next<T>(BotClient bot, uint id, Google.Protobuf.MessageParser<T> parser, Func<T, bool>? match = null)
        where T : Google.Protobuf.IMessage<T>
    {
        var e = await bot.Inbox.WaitAsync(e => e.IsGame(id) && (match == null || match(parser.ParseFrom(e.Body))), Wait);
        return parser.ParseFrom(e.Body);
    }

    private async Task Gm(string name, string command)
    {
        using var client = new HttpClient();
        (await client.PostAsync($"{_http}/ro3emu/gm?account={name.ToLowerInvariant()}", new StringContent(command))).EnsureSuccessStatusCode();
    }

    private async Task<long> Guild(BotClient founder)
    {
        await Gm("Alice", "!lvl 34");
        await founder.SendGameAsync(GuildService.Create, new GuildCreateReq
        {
            Name = "Caravan", Icon = new GuildHeadIcon { BackgroundType = 100101, IconType = 200101 },
        });
        Assert.Equal(0, (await Next(founder, GuildService.Create, GuildCreateResp.Parser)).Result);
        return (await Next(founder, GuildService.Full, GuildFullInfoNtf.Parser, x => x.Guid > 0)).Guid;
    }

    private async Task Join(BotClient founder, BotClient member, long guild, string name = "Bob")
    {
        await Gm(name, "!lvl 34");
        await founder.SendGameAsync(GuildService.Edit, new GuildEditReq { Tp = (GuildEditReq.Types.EditType)6, AutoAcceptApply = true });
        Assert.Equal(0, (await Next(founder, GuildService.Edit, GuildEditResp.Parser)).Result);
        await member.SendGameAsync(GuildService.Apply, new GuildEnterApplyReq { GuildIds = { guild } });
        Assert.Equal(0, Assert.Single((await Next(member, GuildService.Apply, GuildEnterApplyResp.Parser)).ApplyResult).Result);
        await Next(member, GuildService.Full, GuildFullInfoNtf.Parser, x => x.Guid == guild);
    }

    private async Task<GuildCatShopShowResp> Show(BotClient bot, long minVersion = 0,
        Func<GuildCatShopShowResp, bool>? match = null)
    {
        await bot.SendGameAsync(GuildCaravanService.Show, new GuildCatShopShowReq());
        return await Next(bot, GuildCaravanService.Show, GuildCatShopShowResp.Parser,
            x => x.DayVersion >= minVersion && (match == null || match(x)));
    }

    private static (int Entry, int Count, int Type) Cargo(WebApplication app, DBGuildCatShopSku sku)
    {
        var row = app.Services.GetRequiredService<Ro3Emu.Server.Data.GameData>().Row("GuildTransportConfig", sku.CfgId)!.Value;
        return ((int)Ro3Emu.Server.Data.GameData.Int(row, "_iItem"),
            (int)Ro3Emu.Server.Data.GameData.Int(row, "_iNum"), (int)Ro3Emu.Server.Data.GameData.Int(row, "_iType"));
    }

    private async Task LoadFive(BotClient bot, string name, GuildCatShopShowResp show)
    {
        Assert.Equal(8, show.SkuData.Count);
        for (int i = 0; i < 5; i++)
        {
            var sku = show.SkuData[i];
            var (entry, count, _) = Cargo(_app, sku);
            await Gm(name, $"!item {entry} {count}");
            await bot.SendGameAsync(GuildCaravanService.Submit, new GuildCatShopItemSubmitReq
            { SkuIndex = sku.Index, DayVersion = show.DayVersion });
            Assert.Equal(0, (await Next(bot, GuildCaravanService.Submit, GuildCatShopItemSubmitResp.Parser)).Result);
        }
    }

    private async Task<GuildCatShopShowResp> LoadFiveAndDepart(BotClient bot, string name, GuildCatShopShowResp show)
    {
        await LoadFive(bot, name, show);
        await bot.SendGameAsync(GuildCaravanService.Depart, new GuildCatShopDepartReq());
        Assert.Equal(0, (await Next(bot, GuildCaravanService.Depart, GuildCatShopDepartResp.Parser)).Result);
        return await Show(bot, show.DayVersion + 1);
    }

    private MailService.Mailbox Mailbox(long roleId) =>
        _app.Services.GetRequiredService<RoleStore>().LoadMailbox(roleId) is { Length: > 0 } blob
            ? JsonSerializer.Deserialize<MailService.Mailbox>(blob) ?? new() : new();

    private int PendingCount(long roleId)
    {
        using var db = _app.Services.GetRequiredService<AccountStore>().Open();
        using var cmd = db.CreateCommand();
        cmd.CommandText = "SELECT state FROM guild_caravan_member WHERE role_id=$id";
        cmd.Parameters.AddWithValue("$id", roleId);
        using var json = JsonDocument.Parse((byte[])cmd.ExecuteScalar()!);
        return json.RootElement.GetProperty("PendingAwards").GetArrayLength();
    }

    private (string Source, bool GuildBooked, Dictionary<int, long> Grants) Pending(long roleId)
    {
        using var db = _app.Services.GetRequiredService<AccountStore>().Open();
        using var cmd = db.CreateCommand();
        cmd.CommandText = "SELECT state FROM guild_caravan_member WHERE role_id=$id";
        cmd.Parameters.AddWithValue("$id", roleId);
        using var json = JsonDocument.Parse((byte[])cmd.ExecuteScalar()!);
        var pending = Assert.Single(json.RootElement.GetProperty("PendingAwards").EnumerateArray());
        return (pending.GetProperty("SourceKey").GetString()!, pending.GetProperty("GuildBooked").GetBoolean(),
            pending.GetProperty("Grants").EnumerateArray().GroupBy(g => g.GetProperty("Entry").GetInt32())
                .ToDictionary(g => g.Key, g => g.Sum(x => x.GetProperty("Count").GetInt64())));
    }

    private (int Receipts, long PendingCurrency, long PersonalCurrency, long GuildMoney) SourceLegs(string source, long role)
    {
        using var db = _app.Services.GetRequiredService<AccountStore>().Open();
        using var cmd = db.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM operation_receipt WHERE source_key=$source";
        cmd.Parameters.AddWithValue("$source", source);
        int receipts = Convert.ToInt32(cmd.ExecuteScalar());
        cmd.CommandText = "SELECT owner_kind,owner_id,compartment_kind,asset_id,delta FROM economy_entry WHERE source_key=$source";
        long pending = 0, personal = 0, guildMoney = 0;
        using var rows = cmd.ExecuteReader();
        while (rows.Read())
        {
            var kind = (EconomyOwnerKind)rows.GetInt32(0);
            string owner = rows.GetString(1);
            var part = (EconomyCompartment)rows.GetInt32(2);
            int entry = rows.GetInt32(3);
            long delta = rows.GetInt64(4);
            if (kind == EconomyOwnerKind.Role && owner == role.ToString() && entry == 102)
            {
                if (part == EconomyCompartment.CaravanPending) pending += delta;
                if (part == EconomyCompartment.Currency) personal += delta;
            }
            if (kind == EconomyOwnerKind.Guild && entry == EconomyDenominations.GuildMoney)
                guildMoney += delta;
        }
        return (receipts, pending, personal, guildMoney);
    }

    private long OrderGuild(long roleId)
    {
        using var db = _app.Services.GetRequiredService<AccountStore>().Open();
        using var cmd = db.CreateCommand();
        cmd.CommandText = "SELECT state FROM guild_caravan_member WHERE role_id=$id";
        cmd.Parameters.AddWithValue("$id", roleId);
        using var json = JsonDocument.Parse((byte[])cmd.ExecuteScalar()!);
        return json.RootElement.GetProperty("GuildId").GetInt64();
    }

    private long RecoveredMoney(long guild)
    {
        using var db = _app.Services.GetRequiredService<AccountStore>().Open();
        using var cmd = db.CreateCommand();
        cmd.CommandText = "SELECT state FROM guild_recovery WHERE guild_id=$guild";
        cmd.Parameters.AddWithValue("$guild", guild);
        using var json = JsonDocument.Parse((byte[])cmd.ExecuteScalar()!);
        return json.RootElement.GetProperty("Money").GetInt64();
    }

    private static string RepoRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "Ro3Emu.slnx"))) return dir.FullName;
        throw new DirectoryNotFoundException("repository root");
    }

    [ClientDataFact]
    public async Task EveryCaravanMessageUsesDurableOrdersAndGuildHelp()
    {
        await using var alice = await InWorld("Alice");
        await using var bob = await InWorld("Bob");
        await bob.SendGameAsync(GuildCaravanService.Show, new GuildCatShopShowReq());
        Assert.Equal(ErrorCodes.NotInGuild, (await Next(bob, GuildCaravanService.Show, GuildCatShopShowResp.Parser)).Result);
        long guild = await Guild(alice);
        await Join(alice, bob, guild);

        var first = await Show(alice);
        Assert.Equal(0, first.Result);
        Assert.Equal(8, first.SkuData.Count);
        Assert.Equal(2, first.SkuData.Count(x => Cargo(_app, x).Type == 2));
        Assert.Equal(6, first.SkuData.Count(x => Cargo(_app, x).Type == 3));
        var original = first.SkuData[0];
        await alice.SendGameAsync(GuildCaravanService.Submit, new GuildCatShopItemSubmitReq
        { SkuIndex = original.Index, DayVersion = first.DayVersion });
        Assert.Equal(ErrorCodes.ItemsShort, (await Next(alice, GuildCaravanService.Submit, GuildCatShopItemSubmitResp.Parser)).Result);
        var (entry, count, _) = Cargo(_app, original);
        await Gm("Alice", $"!item {entry} {count * 8}");
        await alice.SendGameAsync(GuildCaravanService.Submit, new GuildCatShopItemSubmitReq
        { SkuIndex = original.Index, DayVersion = first.DayVersion });
        Assert.Equal(0, (await Next(alice, GuildCaravanService.Submit, GuildCatShopItemSubmitResp.Parser)).Result);
        await alice.SendGameAsync(GuildCaravanService.Submit, new GuildCatShopItemSubmitReq
        { SkuIndex = original.Index, DayVersion = first.DayVersion });
        Assert.Equal(ErrorCodes.CaravanLoaded, (await Next(alice, GuildCaravanService.Submit, GuildCatShopItemSubmitResp.Parser)).Result);

        await alice.SendGameAsync(GuildCaravanService.Refresh, new GuildCatShopRefreshReq { DayVersion = first.DayVersion });
        Assert.Equal(0, (await Next(alice, GuildCaravanService.Refresh, GuildCatShopRefreshResp.Parser)).Result);
        var refreshed = await Show(alice, first.DayVersion + 1);
        Assert.Equal(8, refreshed.SkuData.Count);
        Assert.Equal(original.CfgId, refreshed.SkuData.Single(x => x.Index == original.Index).CfgId);
        Assert.Equal(2, refreshed.SkuData.Count(x => Cargo(_app, x).Type == 2));
        Assert.Equal(6, refreshed.SkuData.Count(x => Cargo(_app, x).Type == 3));
        await alice.SendGameAsync(GuildCaravanService.Submit, new GuildCatShopItemSubmitReq
        { SkuIndex = refreshed.SkuData[1].Index, DayVersion = first.DayVersion });
        Assert.Equal(ErrorCodes.CaravanOrderExpired, (await Next(alice, GuildCaravanService.Submit, GuildCatShopItemSubmitResp.Parser)).Result);

        foreach (var sku in refreshed.SkuData.Where(x => x.Status == 0).Take(4))
        {
            (entry, count, _) = Cargo(_app, sku);
            await Gm("Alice", $"!item {entry} {count}");
            await alice.SendGameAsync(GuildCaravanService.Submit, new GuildCatShopItemSubmitReq
            { SkuIndex = sku.Index, DayVersion = refreshed.DayVersion });
            Assert.Equal(0, (await Next(alice, GuildCaravanService.Submit, GuildCatShopItemSubmitResp.Parser)).Result);
        }
        var remaining = refreshed.SkuData.First(x => x.Status == 0 && x.Index != original.Index
            && !refreshed.SkuData.Where(y => y.Status == 0).Take(4).Any(y => y.Index == x.Index));
        await alice.SendGameAsync(GuildCaravanService.WantHelp, new GuildCatShopItemWantHelpReq
        { SkuIndex = remaining.Index, DayVersion = refreshed.DayVersion });
        Assert.Equal(0, (await Next(alice, GuildCaravanService.WantHelp, GuildCatShopItemWantHelpResp.Parser)).Result);
        await bob.SendGameAsync(GuildCaravanService.HelpList, new GuildCatShopItemHelpListReq());
        var helpList = await Next(bob, GuildCaravanService.HelpList, GuildCatShopItemHelpListResp.Parser);
        Assert.Contains(helpList.Skus, x => x.PlayerID == (long)alice.Self!.Uuid && x.Data.Index == remaining.Index);
        await alice.SendGameAsync(GuildCaravanService.HelpList, new GuildCatShopItemHelpListReq());
        Assert.Contains((await Next(alice, GuildCaravanService.HelpList, GuildCatShopItemHelpListResp.Parser)).Skus,
            x => x.PlayerID == (long)alice.Self!.Uuid);
        await alice.SendGameAsync(GuildCaravanService.HelpOther, new GuildCatShopItemHelpOtherReq
        { PlayerID = (long)alice.Self!.Uuid, SkuIndex = remaining.Index, DayVersion = helpList.DayVersion });
        Assert.Equal(ErrorCodes.CaravanSelfHelp, (await Next(alice, GuildCaravanService.HelpOther, GuildCatShopItemHelpOtherResp.Parser)).Result);
        (entry, count, _) = Cargo(_app, remaining);
        await Gm("Bob", $"!item {entry} {count}");
        await bob.SendGameAsync(GuildCaravanService.HelpOther, new GuildCatShopItemHelpOtherReq
        { PlayerID = (long)alice.Self!.Uuid, SkuIndex = remaining.Index, DayVersion = helpList.DayVersion });
        Assert.Equal(0, (await Next(bob, GuildCaravanService.HelpOther, GuildCatShopItemHelpOtherResp.Parser)).Result);
        Assert.Equal("Bob", (await Next(alice, GuildCaravanService.HelpNotify, GuildCatShopHelpNotify.Parser)).HelperName);
        await bob.SendGameAsync(GuildCaravanService.HelpOther, new GuildCatShopItemHelpOtherReq
        { PlayerID = (long)alice.Self!.Uuid, SkuIndex = remaining.Index, DayVersion = helpList.DayVersion });
        Assert.Equal(ErrorCodes.CaravanRequestMissing, (await Next(bob, GuildCaravanService.HelpOther, GuildCatShopItemHelpOtherResp.Parser)).Result);

        var all = await Show(alice, refreshed.DayVersion,
            x => x.SkuData.Count(y => (int)y.Status == 1) == 6);
        foreach (var sku in all.SkuData.Where(x => x.Status == 0))
        {
            (entry, count, _) = Cargo(_app, sku);
            await Gm("Alice", $"!item {entry} {count}");
            await alice.SendGameAsync(GuildCaravanService.Submit, new GuildCatShopItemSubmitReq
            { SkuIndex = sku.Index, DayVersion = all.DayVersion });
            Assert.Equal(0, (await Next(alice, GuildCaravanService.Submit, GuildCatShopItemSubmitResp.Parser)).Result);
        }
        var roleStore = _app.Services.GetRequiredService<RoleStore>();
        Assert.True(DBRoleGuildInfo.Parser.ParseFrom(roleStore.LoadGuildInfo((long)alice.Self!.Uuid)).IsCatShopAllSubmit);

        var guildStore = _app.Services.GetRequiredService<GuildStore>();
        long moneyBefore = guildStore.Get(guild)!.Money;
        long contributionBefore = guildStore.Get(guild)!.Members[(long)alice.Self.Uuid].CurContribute;
        long personalBefore = _app.Services.GetRequiredService<SessionRegistry>().All()
            .Single(x => x.Role?.Id == (long)alice.Self.Uuid).Progress!.Currency.GetValueOrDefault(102);
        await alice.SendGameAsync(GuildCaravanService.Depart, new GuildCatShopDepartReq());
        var depart = await Next(alice, GuildCaravanService.Depart, GuildCatShopDepartResp.Parser);
        Assert.Equal(0, depart.Result);
        Assert.Equal(2681, depart.AwardId); // Eight loaded slots select GuildTransportTaskConfig threshold 8.
        Assert.Contains(depart.Award.Rewards, x => x.ItemEntry == 104 && x.Stack == 1000);
        Assert.Contains(depart.Award.Rewards, x => x.ItemEntry == 102 && x.Stack == 2000);
        Assert.Equal(moneyBefore + 1000, guildStore.Get(guild)!.Money);
        Assert.Equal(contributionBefore + 2000, guildStore.Get(guild)!.Members[(long)alice.Self.Uuid].CurContribute);
        Assert.Equal(personalBefore + 2000, _app.Services.GetRequiredService<SessionRegistry>().All()
            .Single(x => x.Role?.Id == (long)alice.Self.Uuid).Progress!.Currency.GetValueOrDefault(102));
        var second = await Show(alice, refreshed.DayVersion + 1);
        Assert.Equal(1, second.CurRound);
        Assert.Equal(8, second.SkuData.Count);
        Assert.False(DBRoleGuildInfo.Parser.ParseFrom(roleStore.LoadGuildInfo((long)alice.Self!.Uuid)).IsCatShopAllSubmit);
        await alice.SendGameAsync(GuildCaravanService.Depart, new GuildCatShopDepartReq());
        Assert.Equal(ErrorCodes.CaravanLoadShort, (await Next(alice, GuildCaravanService.Depart, GuildCatShopDepartResp.Parser)).Result);
        await alice.SendGameAsync(GuildCaravanService.Submit, new GuildCatShopItemSubmitReq
        { SkuIndex = second.SkuData[0].Index, DayVersion = refreshed.DayVersion });
        Assert.Equal(ErrorCodes.CaravanOrderExpired, (await Next(alice, GuildCaravanService.Submit, GuildCatShopItemSubmitResp.Parser)).Result);
        Assert.True(_app.Services.GetRequiredService<GuildStore>().Get(guild)!.Money > 0);
    }

    [ClientDataFact]
    public async Task FiveConfiguredRoundsEndAtCapAndOnlyFirstRoyalCountsForDaily()
    {
        await using var alice = await InWorld("Alice");
        await Guild(alice);
        await Gm("Alice", "!quest done 10105090"); // Function 25 opens Daily/ToDo tracking.
        var show = await Show(alice);
        var session = _app.Services.GetRequiredService<SessionRegistry>().All()
            .Single(x => x.Role?.Id == (long)alice.Self!.Uuid);
        long livenessBefore = _app.Services.GetRequiredService<CharacterService>().CountOf(session, 5);
        int[][] mix = [[0, 2, 6], [0, 5, 3], [1, 5, 2], [2, 6, 0], [2, 6, 0]];
        for (int round = 0; round < 5; round++)
        {
            Assert.Equal(round, show.CurRound);
            Assert.Equal(8, show.SkuData.Count);
            for (int type = 1; type <= 3; type++)
                Assert.Equal(mix[round][type - 1], show.SkuData.Count(x => Cargo(_app, x).Type == type));
            show = await LoadFiveAndDepart(alice, "Alice", show);
            Assert.Equal(livenessBefore + 20,
                _app.Services.GetRequiredService<CharacterService>().CountOf(session, 5));
            if (round == 0)
            {
                Assert.True(session.DailyRows!.ActivityData[101].TaskCompleteTimes == 1,
                    $"daily={session.DailyRows.ActivityData[101].TaskCompleteTimes}; task={session.Tasks!.Infos.GetValueOrDefault(500080)}; completed={session.Tasks.CompleteTaskNum.GetValueOrDefault(500080)}; function={_app.Services.GetRequiredService<FunctionService>().IsOpen(session, 1320)}");
            }
        }
        Assert.Equal(5, show.CurRound);
        Assert.Empty(show.SkuData);
        Assert.True(show.IsAwardGot);
        await alice.SendGameAsync(GuildCaravanService.Depart, new GuildCatShopDepartReq());
        Assert.Equal(ErrorCodes.CaravanDeparted,
            (await Next(alice, GuildCaravanService.Depart, GuildCatShopDepartResp.Parser)).Result);
        Assert.Equal(1, session.DailyRows!.ActivityData[101].TaskCompleteTimes);
    }

    [ClientDataFact]
    public async Task ResetAtFiveCompensatesOfflineUnclaimedDepartureExactlyOnce()
    {
        long roleId;
        long oldVersion;
        long guildId;
        await using (var alice = await InWorld("Alice"))
        {
            roleId = (long)alice.Self!.Uuid;
            guildId = await Guild(alice);
            var show = await Show(alice);
            for (int i = 0; i < 5; i++)
            {
                var sku = show.SkuData[i];
                var (entry, count, _) = Cargo(_app, sku);
                await Gm("Alice", $"!item {entry} {count}");
                await alice.SendGameAsync(GuildCaravanService.Submit, new GuildCatShopItemSubmitReq
                { SkuIndex = sku.Index, DayVersion = show.DayVersion });
                Assert.Equal(0, (await Next(alice, GuildCaravanService.Submit, GuildCatShopItemSubmitResp.Parser)).Result);
            }
            oldVersion = show.DayVersion;
            _clock.Advance(9 * 60);
            Assert.Equal(oldVersion, (await Show(alice)).DayVersion);
        }
        Assert.DoesNotContain(Mailbox(roleId).Letters, x => x.TemplateId == 1023);
        var store = _app.Services.GetRequiredService<GuildStore>();
        long fundsBefore = store.Get(guildId)!.Money;
        long guildContributionBefore = store.Get(guildId)!.Members[roleId].CurContribute;
        _clock.Advance(60);
        var loop = _app.Services.GetRequiredService<GameLoop>();
        var caravan = _app.Services.GetRequiredService<GuildCaravanService>();
        await loop.InvokeAsync(caravan.ReconcileAllAsync);
        var compensation = Assert.Single(Mailbox(roleId).Letters, x => x.TemplateId == 1023);
        Assert.Contains(compensation.Attachments, x => x.Entry == 102 && x.Count == 1000);
        Assert.Equal(fundsBefore + 1000, store.Get(guildId)!.Money);
        Assert.Equal(guildContributionBefore + 1000, store.Get(guildId)!.Members[roleId].CurContribute);
        await RestartAsync();
        loop = _app.Services.GetRequiredService<GameLoop>();
        caravan = _app.Services.GetRequiredService<GuildCaravanService>();
        await loop.InvokeAsync(caravan.ReconcileAllAsync);
        Assert.Single(Mailbox(roleId).Letters, x => x.TemplateId == 1023);
        await using var relog = await InWorld("Alice", create: false);
        var today = await Show(relog, oldVersion + 1);
        Assert.Equal(0, today.CurRound);
        Assert.Equal(8, today.SkuData.Count);
        Assert.All(today.SkuData, x => Assert.Equal(0, (int)x.Status));
        Assert.Equal(0, _app.Services.GetRequiredService<SessionRegistry>().All()
            .Single(x => x.Role?.Id == roleId).DailyRows!.ActivityData[101].TaskCompleteTimes);
    }

    [ClientDataFact]
    public async Task ResetAtFiveUsesLatestGuildDeltaRevisionAndRecordsExactSource()
    {
        long roleId;
        long guildId;
        await using (var alice = await InWorld("Alice"))
        {
            roleId = (long)alice.Self!.Uuid;
            guildId = await Guild(alice);
            await LoadFive(alice, "Alice", await Show(alice));
        }

        var store = _app.Services.GetRequiredService<GuildStore>();
        var initialGuild = store.Get(guildId)!;
        long fundsBefore = initialGuild.Money;
        long contributionBefore = initialGuild.Members[roleId].CurContribute;
        var before = MarketEconomyEvidence.Cut(_dir, RepoRoot());
        var accounts = _app.Services.GetRequiredService<AccountStore>();
        int advanced = 0;
        accounts.BeforeOwnedCommitForTest = label =>
        {
            if (label == "economy/guild-caravan-rollover" && Interlocked.CompareExchange(ref advanced, 1, 0) == 0)
            {
                // Advance only metadata after rollover read its guild snapshot and before the
                // mail writer samples the delta revision. The old explicit capture rejects this.
                var fresh = store.Get(guildId)!;
                long revision = fresh.CapturedRevision!.Value.Revision;
                fresh.Notice = "revision advance during rollover";
                Assert.True(store.Save(fresh));
                Assert.Equal(revision + 1, store.Get(guildId)!.CapturedRevision!.Value.Revision);
            }
            return Task.CompletedTask;
        };
        try
        {
            _clock.Advance(10 * 60);
            await _app.Services.GetRequiredService<GameLoop>().InvokeAsync(
                _app.Services.GetRequiredService<GuildCaravanService>().ReconcileAllAsync);
        }
        finally { accounts.BeforeOwnedCommitForTest = null; }

        Assert.Equal(1, advanced);
        var compensation = Assert.Single(Mailbox(roleId).Letters, x => x.TemplateId == 1023);
        Assert.Contains(compensation.Attachments, x => x.Entry == 102 && x.Count == 1000);
        Assert.Equal(fundsBefore + 1000, store.Get(guildId)!.Money);
        Assert.Equal(contributionBefore + 1000, store.Get(guildId)!.Members[roleId].CurContribute);
        Assert.Equal("revision advance during rollover", store.Get(guildId)!.Notice);
        var after = MarketEconomyEvidence.Cut(_dir, RepoRoot());
        var ledger = MarketEconomyEvidence.Exact(_dir, RepoRoot(), before, after);
        // The realm's one-second stall maintenance may commit its own receipt in this window (C/D 15: no longer at login).
        Assert.Single(ledger.Receipts, r => r.CommandKind == "guild-caravan-rollover");
        Assert.Contains(ledger.Legs, leg => leg.Kind == EconomyLegKind.Source
            && leg.Reason == EconomyReason.GuildResourceGrant
            && leg.Asset.Kind == EconomyAssetKind.GuildResource
            && leg.Asset.Id == EconomyDenominations.GuildMoney && leg.Delta == 1000);
    }

    [ClientDataFact]
    public async Task OfflineRolloverAfterDisbandCreditsDurableGuildRecoveryOnce()
    {
        long role, guild, day;
        await using (var alice = await InWorld("Alice"))
        {
            role = (long)alice.Self!.Uuid;
            guild = await Guild(alice);
            var show = await Show(alice);
            day = show.DayVersion / 1_000_000;
            await LoadFive(alice, "Alice", show);
            await alice.SendGameAsync(GuildService.Leave, new GuildMemberLeaveReq());
            Assert.Equal(0, (await Next(alice, GuildService.Leave, GuildMemberLeaveResp.Parser)).Result);
        }
        var guildStore = _app.Services.GetRequiredService<GuildStore>();
        Assert.Null(guildStore.Get(guild));
        Assert.True(guildStore.HasRecovery(guild));
        long before = RecoveredMoney(guild);
        _clock.Advance(11 * 60);
        var loop = _app.Services.GetRequiredService<GameLoop>();
        var caravan = _app.Services.GetRequiredService<GuildCaravanService>();
        await loop.InvokeAsync(caravan.ReconcileAllAsync);
        Assert.Equal(before + 1000, RecoveredMoney(guild));
        Assert.Single(Mailbox(role).Letters, x => x.TemplateId == 1023);
        string source = $"guild-caravan:{role}:{day}:0";
        Assert.Equal(1, SourceLegs(source, role).Receipts);
        Assert.Equal(1000, SourceLegs(source, role).GuildMoney);
        await loop.InvokeAsync(caravan.ReconcileAllAsync);
        Assert.Equal(before + 1000, RecoveredMoney(guild));
        Assert.Equal(1, SourceLegs(source, role).Receipts);
    }

    [ClientDataFact]
    public async Task OfflineRolloverExpiresUnclaimedMailInItsParentReceipt()
    {
        long role, guild, day;
        await using (var alice = await InWorld("Alice"))
        {
            role = (long)alice.Self!.Uuid;
            guild = await Guild(alice);
            var show = await Show(alice);
            day = show.DayVersion / 1_000_000;
            await LoadFive(alice, "Alice", show);
        }

        string expiredId = await _app.Services.GetRequiredService<MailService>().SendRawAsync(role,
            "Expiring reward", "Caravan rollover fixture", [new(2, 7), new(1000000, 3)],
            expiresAt: _clock.GetUtcNow().AddSeconds(1));
        Assert.Contains(Mailbox(role).Letters, letter => letter.Id == expiredId && !letter.Claimed);
        var before = MarketEconomyEvidence.Cut(_dir, RepoRoot());
        long moneyBefore = _app.Services.GetRequiredService<GuildStore>().Get(guild)!.Money;

        _clock.Advance(10 * 60);
        var loop = _app.Services.GetRequiredService<GameLoop>();
        var caravan = _app.Services.GetRequiredService<GuildCaravanService>();
        await loop.InvokeAsync(caravan.ReconcileAllAsync);

        var mailbox = Mailbox(role);
        Assert.DoesNotContain(mailbox.Letters, letter => letter.Id == expiredId);
        var compensation = Assert.Single(mailbox.Letters, letter => letter.TemplateId == 1023);
        Assert.Contains(compensation.Attachments, attachment => attachment.Entry == 102 && attachment.Count == 1000);
        Assert.Equal(moneyBefore + 1000, _app.Services.GetRequiredService<GuildStore>().Get(guild)!.Money);

        var after = MarketEconomyEvidence.Cut(_dir, RepoRoot());
        var ledger = MarketEconomyEvidence.Exact(_dir, RepoRoot(), before, after);
        // The ten-minute advance can also restock the realm's system stalls in this window (their own receipt).
        var receipt = Assert.Single(ledger.Receipts, r => r.CommandKind == "guild-caravan-rollover");
        string source = $"guild-caravan:{role}:{day}:0";
        Assert.Equal(1, SourceLegs(source, role).Receipts);
        Assert.Contains(ledger.Legs, leg => leg.SourceKey == source && leg.Kind == EconomyLegKind.Sink
            && leg.Reason == EconomyReason.MailExpiry && leg.Asset.Kind == EconomyAssetKind.Currency
            && leg.Asset.Id == 2 && leg.Delta == -7 && leg.Location.Compartment == EconomyCompartment.Mail);
        Assert.Contains(ledger.Legs, leg => leg.SourceKey == source && leg.Kind == EconomyLegKind.Sink
            && leg.Reason == EconomyReason.MailExpiry && leg.Asset.Kind == EconomyAssetKind.Item
            && leg.Asset.Id == 1000000 && leg.Delta == -3 && leg.Location.Compartment == EconomyCompartment.Mail);
        Assert.Equal(2, ledger.Legs.Count(leg => leg.Kind == EconomyLegKind.Sink && leg.Reason == EconomyReason.MailExpiry));
        Assert.Contains(ledger.Legs, leg => leg.SourceKey == source && leg.Kind == EconomyLegKind.Source
            && leg.Reason == EconomyReason.ActivityReward && leg.Asset.Id == 102 && leg.Delta == 1000);

        await loop.InvokeAsync(caravan.ReconcileAllAsync);
        Assert.Equal(1, SourceLegs(source, role).Receipts);
        Assert.Single(Mailbox(role).Letters, letter => letter.TemplateId == 1023);
        Assert.Equal(moneyBefore + 1000, _app.Services.GetRequiredService<GuildStore>().Get(guild)!.Money);
    }

    [ClientDataFact]
    public async Task HelpCancellationDoesNotRefundDailyRequestsAndRefreshStopsAtFive()
    {
        await using var alice = await InWorld("Alice");
        await Guild(alice);
        var show = await Show(alice);
        for (int i = 0; i < 5; i++)
        {
            var sku = show.SkuData[i];
            var (entry, count, _) = Cargo(_app, sku);
            await Gm("Alice", $"!item {entry} {count}");
            await alice.SendGameAsync(GuildCaravanService.Submit, new GuildCatShopItemSubmitReq
            { SkuIndex = sku.Index, DayVersion = show.DayVersion });
            Assert.Equal(0, (await Next(alice, GuildCaravanService.Submit, GuildCatShopItemSubmitResp.Parser)).Result);
        }
        foreach (var sku in show.SkuData.Skip(5))
        {
            await alice.SendGameAsync(GuildCaravanService.WantHelp, new GuildCatShopItemWantHelpReq
            { SkuIndex = sku.Index, DayVersion = show.DayVersion });
            Assert.Equal(0, (await Next(alice, GuildCaravanService.WantHelp, GuildCatShopItemWantHelpResp.Parser)).Result);
        }
        await alice.SendGameAsync(GuildCaravanService.WantHelp, new GuildCatShopItemWantHelpReq
        { SkuIndex = show.SkuData[5].Index, DayVersion = show.DayVersion, IsCancle = true });
        Assert.Equal(0, (await Next(alice, GuildCaravanService.WantHelp, GuildCatShopItemWantHelpResp.Parser)).Result);
        await alice.SendGameAsync(GuildCaravanService.WantHelp, new GuildCatShopItemWantHelpReq
        { SkuIndex = show.SkuData[5].Index, DayVersion = show.DayVersion });
        Assert.Equal(ErrorCodes.CaravanHelpLimit,
            (await Next(alice, GuildCaravanService.WantHelp, GuildCatShopItemWantHelpResp.Parser)).Result);
        foreach (var sku in show.SkuData.Skip(6))
        {
            await alice.SendGameAsync(GuildCaravanService.WantHelp, new GuildCatShopItemWantHelpReq
            { SkuIndex = sku.Index, DayVersion = show.DayVersion, IsCancle = true });
            Assert.Equal(0, (await Next(alice, GuildCaravanService.WantHelp, GuildCatShopItemWantHelpResp.Parser)).Result);
        }
        long currentVersion = show.DayVersion;
        for (int i = 0; i < 5; i++)
        {
            var before = await Show(alice, currentVersion);
            await alice.SendGameAsync(GuildCaravanService.Refresh, new GuildCatShopRefreshReq { DayVersion = before.DayVersion });
            Assert.Equal(0, (await Next(alice, GuildCaravanService.Refresh, GuildCatShopRefreshResp.Parser)).Result);
            var after = await Show(alice, before.DayVersion + 1);
            currentVersion = after.DayVersion;
            Assert.Equal(i + 1, after.RefreshTimes);
            Assert.Equal(5, after.SkuData.Count(x => (int)x.Status == 1));
            Assert.Equal(3, after.SkuData.Count(x => (int)x.Status == 0));
            Assert.Equal(2, after.SkuData.Count(x => Cargo(_app, x).Type == 2));
            Assert.Equal(6, after.SkuData.Count(x => Cargo(_app, x).Type == 3));
        }
        show = await Show(alice, currentVersion);
        await alice.SendGameAsync(GuildCaravanService.Refresh, new GuildCatShopRefreshReq { DayVersion = show.DayVersion });
        Assert.Equal(ErrorCodes.CaravanRefreshLimit,
            (await Next(alice, GuildCaravanService.Refresh, GuildCatShopRefreshResp.Parser)).Result);
    }

    [ClientDataFact]
    public async Task FeatureGateRejectsUnderleveledGuildMember()
    {
        await using var alice = await InWorld("Alice");
        await using var bob = await InWorld("Bob");
        long guild = await Guild(alice);
        await Join(alice, bob, guild);
        await Gm("Bob", "!lvl 1");
        await bob.SendGameAsync(GuildCaravanService.Show, new GuildCatShopShowReq());
        Assert.Equal(ErrorCodes.FeatureUnavailable,
            (await Next(bob, GuildCaravanService.Show, GuildCatShopShowResp.Parser)).Result);
        await bob.SendGameAsync(GuildCaravanService.HelpList, new GuildCatShopItemHelpListReq());
        Assert.Equal(ErrorCodes.FeatureUnavailable,
            (await Next(bob, GuildCaravanService.HelpList, GuildCatShopItemHelpListResp.Parser)).Result);
    }

    [ClientDataFact]
    public async Task OfflineHelpAwardSurvivesKickAndDeliversOnGuildlessLogin()
    {
        await using var alice = await InWorld("Alice");
        long guild = await Guild(alice);
        long bobId;
        long expectedContribution;
        long expectedFunds;
        long personalBefore;
        DBGuildCatShopSku request;
        long version;
        await using (var bob = await InWorld("Bob"))
        {
            bobId = (long)bob.Self!.Uuid;
            await Join(alice, bob, guild);
            var show = await Show(bob);
            await LoadFive(bob, "Bob", show);
            request = show.SkuData[5]; version = show.DayVersion;
            await bob.SendGameAsync(GuildCaravanService.WantHelp, new GuildCatShopItemWantHelpReq
            { SkuIndex = request.Index, DayVersion = version });
            Assert.Equal(0, (await Next(bob, GuildCaravanService.WantHelp, GuildCatShopItemWantHelpResp.Parser)).Result);
            var data = _app.Services.GetRequiredService<Ro3Emu.Server.Data.GameData>();
            var row = data.Row("GuildTransportConfig", request.CfgId)!.Value;
            long group = (long)(Ro3Emu.Server.Data.GameData.Number(row.GetProperty("_kAward")[0]) ?? 0);
            var targetGrants = _app.Services.GetRequiredService<AwardService>()
                .ResolveForProgress(_app.Services.GetRequiredService<RoleStore>().LoadProgress(bobId), group).Grants;
            expectedContribution = targetGrants.Where(x => x.Entry == 102).Sum(x => (long)x.Count);
            expectedFunds = targetGrants.Where(x => x.Entry == 104).Sum(x => (long)x.Count);
            personalBefore = _app.Services.GetRequiredService<RoleStore>().LoadProgress(bobId).Currency.GetValueOrDefault(102);
        }
        long guildContributionBefore = _app.Services.GetRequiredService<GuildStore>().Get(guild)!.Members[bobId].CurContribute;
        var (entry, count, _) = Cargo(_app, request);
        var helperConfig = _app.Services.GetRequiredService<Ro3Emu.Server.Data.GameData>()
            .Row("GuildTransportConfig", request.CfgId)!.Value;
        long helperGroup = Ro3Emu.Server.Data.GameData.Int(helperConfig, "_iAward");
        long helperFunds = _app.Services.GetRequiredService<AwardService>()
            .ResolveForProgress(_app.Services.GetRequiredService<RoleStore>().LoadProgress((long)alice.Self!.Uuid), helperGroup)
            .Grants.Where(x => x.Entry == 104).Sum(x => (long)x.Count);
        await Gm("Alice", $"!item {entry} {count}");
        await alice.SendGameAsync(GuildCaravanService.HelpOther, new GuildCatShopItemHelpOtherReq
        { PlayerID = bobId, SkuIndex = request.Index, DayVersion = version });
        Assert.Equal(0, (await Next(alice, GuildCaravanService.HelpOther, GuildCatShopItemHelpOtherResp.Parser)).Result);
        Assert.Equal(1, PendingCount(bobId));
        var pending = Pending(bobId);
        Assert.True(pending.GuildBooked);
        Assert.Equal(expectedContribution, pending.Grants[102]);
        Assert.Equal(expectedFunds, pending.Grants.GetValueOrDefault(104));
        var creation = SourceLegs(pending.Source, bobId);
        Assert.Equal(1, creation.Receipts);
        Assert.Equal(expectedContribution, creation.PendingCurrency);
        Assert.Equal(0, creation.PersonalCurrency);
        Assert.Equal(expectedFunds + helperFunds, creation.GuildMoney);
        Assert.Equal(guildContributionBefore + expectedContribution,
            _app.Services.GetRequiredService<GuildStore>().Get(guild)!.Members[bobId].CurContribute);
        await alice.SendGameAsync(GuildService.Kick, new GuildMemberKickReq
        { TarPlayerIDs = { bobId }, IfSingleKick = true });
        Assert.Equal(0, (await Next(alice, GuildService.Kick, GuildMemberKickResp.Parser)).Result);
        await using var relog = await InWorld("Bob", create: false);
        Assert.Equal(0, PendingCount(bobId));
        Assert.Equal(0, DBRoleGuildInfo.Parser.ParseFrom(
            _app.Services.GetRequiredService<RoleStore>().LoadGuildInfo(bobId)).GuildId);
        Assert.Equal(personalBefore + expectedContribution,
            _app.Services.GetRequiredService<RoleStore>().LoadProgress(bobId).Currency.GetValueOrDefault(102));
        var delivery = SourceLegs(pending.Source + ":deliver", bobId);
        Assert.Equal(1, delivery.Receipts);
        Assert.Equal(-expectedContribution, delivery.PendingCurrency);
        Assert.Equal(expectedContribution, delivery.PersonalCurrency);
        Assert.Equal(0, delivery.GuildMoney); // booked treasury value is not created twice
    }

    [ClientDataFact]
    public async Task LeavingWhileSubmitWaitsForCharacterLockCannotSpendOrReward()
    {
        await using var alice = await InWorld("Alice");
        await using var bob = await InWorld("Bob");
        long guild = await Guild(alice);
        await Join(alice, bob, guild);
        var show = await Show(bob);
        var sku = show.SkuData[0];
        var (entry, count, _) = Cargo(_app, sku);
        await Gm("Bob", $"!item {entry} {count}");
        var session = _app.Services.GetRequiredService<SessionRegistry>().All()
            .Single(x => x.Role?.Id == (long)bob.Self!.Uuid);
        var character = _app.Services.GetRequiredService<CharacterService>();
        long held = character.CountOf(session, entry);
        long funds = _app.Services.GetRequiredService<GuildStore>().Get(guild)!.Money;
        bool locked = false;
        Task? pending = null, leaving = null;
        try
        {
            var loop = _app.Services.GetRequiredService<GameLoop>();
            var caravan = _app.Services.GetRequiredService<GuildCaravanService>();
            await loop.InvokeAsync(async () =>
            {
                // Drain existing Realm work before the synthetic raw-lock hold. After releasing
                // that scope, start both real admissions in this turn so a legacy timer cannot
                // acquire Realm between the lock hold and Leave's independent reservation.
                using (await _app.Services.GetRequiredService<OwnerRegistry>()
                    .EnterAsync("test caravan character-hold setup", ReservationKey.Realm))
                {
                    locked = session.StateLock.Wait(0);
                    Assert.True(locked);
                }
                pending = loop.TrackAsync("test caravan submit", () => caravan.SubmitAsync(session,
                    new GuildCatShopItemSubmitReq { SkuIndex = sku.Index, DayVersion = show.DayVersion }));
                Assert.False(pending.IsCompleted);
                leaving = loop.TrackAsync("test guild leave during caravan admission", () =>
                    _app.Services.GetRequiredService<GuildService>().LeaveAsync(session));
            });
            await leaving!.WaitAsync(Wait);
            Assert.Equal(0, (await Next(bob, GuildService.Leave, GuildMemberLeaveResp.Parser)).Result);
        }
        finally { if (locked) session.StateLock.Release(); }
        await pending!;
        Assert.NotEqual(0, (await Next(bob, GuildCaravanService.Submit, GuildCatShopItemSubmitResp.Parser)).Result);
        Assert.Equal(held, character.CountOf(session, entry));
        Assert.Equal(funds, _app.Services.GetRequiredService<GuildStore>().Get(guild)!.Money);
    }

    [ClientDataFact]
    public async Task HeldHelpCannotCreateYesterdayPendingAwardAcrossOfflineRollover()
    {
        await using var alice = await InWorld("Alice");
        long guild = await Guild(alice);
        long bobId;
        DBGuildCatShopSku request;
        long version;
        await using (var bob = await InWorld("Bob"))
        {
            bobId = (long)bob.Self!.Uuid;
            await Join(alice, bob, guild);
            var show = await Show(bob);
            await LoadFive(bob, "Bob", show);
            request = show.SkuData[5]; version = show.DayVersion;
            await bob.SendGameAsync(GuildCaravanService.WantHelp, new GuildCatShopItemWantHelpReq
            { SkuIndex = request.Index, DayVersion = version });
            Assert.Equal(0, (await Next(bob, GuildCaravanService.WantHelp, GuildCatShopItemWantHelpResp.Parser)).Result);
        }
        var (entry, count, _) = Cargo(_app, request);
        await Gm("Alice", $"!item {entry} {count}");
        await Show(alice); // materialize the helper's order before the held paid exchange
        var session = _app.Services.GetRequiredService<SessionRegistry>().All()
            .Single(x => x.Role?.Id == (long)alice.Self!.Uuid);
        long cargoBefore = _app.Services.GetRequiredService<CharacterService>().CountOf(session, entry);
        var loop = _app.Services.GetRequiredService<GameLoop>();
        var service = _app.Services.GetRequiredService<GuildCaravanService>();
        await session.StateLock.WaitAsync();
        Task? help = null, rollover = null;
        try
        {
            await loop.InvokeAsync(() =>
            {
                help = loop.TrackAsync("test held caravan help", () => service.HelpOtherAsync(session,
                    new GuildCatShopItemHelpOtherReq
                    { PlayerID = bobId, SkuIndex = request.Index, DayVersion = version }));
                return Task.CompletedTask;
            });
            var owners = _app.Services.GetRequiredService<OwnerRegistry>();
            // Waiting for StateLock has not yet acquired guild ownership. A help parked there would hold the guild key
            // continuously, so one free probe proves it; realm guild maintenance (heartbeat) may hold it briefly.
            bool available = false;
            for (int attempt = 0; attempt < 100 && !available; attempt++)
            {
                if (attempt > 0) await Task.Delay(20);
                await loop.InvokeAsync(() =>
                {
                    const long probe = long.MaxValue;
                    available = owners.Reservations.TryReserve(probe, [ReservationKey.Entity("guild", guild.ToString())]);
                    if (available) owners.Reservations.Release(probe);
                    return Task.CompletedTask;
                });
            }
            Assert.True(available);
            _clock.Advance(11 * 60);
            await loop.InvokeAsync(() =>
            {
                rollover = loop.TrackAsync("test caravan rollover collision", service.ReconcileAllAsync);
                return Task.CompletedTask;
            });
            Assert.False(help!.IsCompleted);
            Assert.Equal(0, PendingCount(bobId));
        }
        finally
        {
            session.StateLock.Release();
        }
        await Task.WhenAll(help!, rollover!);
        Assert.NotEqual(0, (await Next(alice, GuildCaravanService.HelpOther, GuildCatShopItemHelpOtherResp.Parser)).Result);
        Assert.Equal(cargoBefore, _app.Services.GetRequiredService<CharacterService>().CountOf(session, entry));
        Assert.Equal(0, PendingCount(bobId));
        Assert.Single(Mailbox(bobId).Letters, x => x.TemplateId == 1023);
        string source = $"guild-caravan:help:{guild}:{bobId}:{version / 1_000_000}:0:{request.Index}";
        Assert.Equal(0, SourceLegs(source, bobId).Receipts);
    }

    [ClientDataFact]
    public async Task HeldOfflineRolloverCommitPrecedesLoginPendingDeliveryExactlyOnce()
    {
        await using var alice = await InWorld("Alice");
        long guild = await Guild(alice);
        long bobId, day, personalBefore;
        DBGuildCatShopSku requested;
        long version;
        await using (var bob = await InWorld("Bob"))
        {
            bobId = (long)bob.Self!.Uuid;
            await Join(alice, bob, guild);
            var show = await Show(bob);
            day = show.DayVersion / 1_000_000;
            await LoadFive(bob, "Bob", show);
            requested = show.SkuData[5]; version = show.DayVersion;
            await bob.SendGameAsync(GuildCaravanService.WantHelp, new GuildCatShopItemWantHelpReq
            { SkuIndex = requested.Index, DayVersion = version });
            Assert.Equal(0, (await Next(bob, GuildCaravanService.WantHelp, GuildCatShopItemWantHelpResp.Parser)).Result);
            personalBefore = _app.Services.GetRequiredService<RoleStore>().LoadProgress(bobId)
                .Currency.GetValueOrDefault(102);
        }
        var (entry, count, _) = Cargo(_app, requested);
        await Gm("Alice", $"!item {entry} {count}");
        await alice.SendGameAsync(GuildCaravanService.HelpOther, new GuildCatShopItemHelpOtherReq
        { PlayerID = bobId, SkuIndex = requested.Index, DayVersion = version });
        Assert.Equal(0, (await Next(alice, GuildCaravanService.HelpOther, GuildCatShopItemHelpOtherResp.Parser)).Result);
        var pending = Pending(bobId);
        Assert.True(pending.GuildBooked);
        long expectedPersonal = pending.Grants[102];
        long fundsBefore = _app.Services.GetRequiredService<GuildStore>().Get(guild)!.Money;
        Assert.Equal(1, SourceLegs(pending.Source, bobId).Receipts);
        _clock.Advance(11 * 60);

        var accounts = _app.Services.GetRequiredService<AccountStore>();
        var owners = _app.Services.GetRequiredService<OwnerRegistry>();
        var loop = _app.Services.GetRequiredService<GameLoop>();
        var entered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var deferred = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        Task? rollover = null;
        Task<BotClient>? login = null;
        BotClient? returned = null;
        try
        {
            accounts.BeforeOwnedCommitForTest = label =>
            {
                if (label != "economy/guild-caravan-rollover") return Task.CompletedTask;
                entered.TrySetResult(true);
                return release.Task;
            };
            owners.DeferredForTest = origin => { if (origin == "role select") deferred.TrySetResult(true); };
            rollover = loop.InvokeAsync(_app.Services.GetRequiredService<GuildCaravanService>().ReconcileAllAsync);
            await entered.Task.WaitAsync(Wait);
            login = InWorld("Bob", create: false);
            await deferred.Task.WaitAsync(Wait);
            Assert.False(login.IsCompleted);
            Assert.Equal(1, PendingCount(bobId));
            Assert.DoesNotContain(Mailbox(bobId).Letters, x => x.TemplateId == 1023);
            Assert.Equal(fundsBefore, _app.Services.GetRequiredService<GuildStore>().Get(guild)!.Money);
            release.TrySetResult(true);
            await rollover.WaitAsync(Wait);
            returned = await login.WaitAsync(Wait);
            Assert.Equal(0, PendingCount(bobId));
            Assert.Single(Mailbox(bobId).Letters, x => x.TemplateId == 1023);
            Assert.Equal(personalBefore + expectedPersonal,
                _app.Services.GetRequiredService<RoleStore>().LoadProgress(bobId).Currency.GetValueOrDefault(102));
            Assert.Equal(fundsBefore + 1000, _app.Services.GetRequiredService<GuildStore>().Get(guild)!.Money);
            Assert.Equal((1, -expectedPersonal, expectedPersonal, 0L),
                SourceLegs(pending.Source + ":deliver", bobId));
            string rolloverSource = $"guild-caravan:{bobId}:{day}:0";
            Assert.Equal((1, 0L, 0L, 1000L), SourceLegs(rolloverSource, bobId));
            await loop.InvokeAsync(_app.Services.GetRequiredService<GuildCaravanService>().ReconcileAllAsync);
            Assert.Equal(1, SourceLegs(pending.Source + ":deliver", bobId).Receipts);
            Assert.Equal(1, SourceLegs(rolloverSource, bobId).Receipts);
        }
        finally
        {
            release.TrySetResult(true);
            accounts.BeforeOwnedCommitForTest = null;
            await loop.InvokeAsync(() => { owners.DeferredForTest = null; return Task.CompletedTask; });
            if (rollover != null) await rollover.WaitAsync(Wait);
            if (login != null) returned ??= await login.WaitAsync(Wait);
            if (returned != null) await returned.DisposeAsync();
        }
    }

    [ClientDataFact]
    public async Task ConcurrentHelpersCanCompleteOneRequestOnlyOnce()
    {
        await using var alice = await InWorld("Alice");
        await using var bob = await InWorld("Bob");
        await using var carol = await InWorld("Carol");
        long guild = await Guild(alice);
        await Join(alice, bob, guild);
        await Join(alice, carol, guild, "Carol");
        var show = await Show(alice);
        await LoadFive(alice, "Alice", show);
        var request = show.SkuData[5];
        await alice.SendGameAsync(GuildCaravanService.WantHelp, new GuildCatShopItemWantHelpReq
        { SkuIndex = request.Index, DayVersion = show.DayVersion });
        Assert.Equal(0, (await Next(alice, GuildCaravanService.WantHelp, GuildCatShopItemWantHelpResp.Parser)).Result);
        var (entry, count, _) = Cargo(_app, request);
        await Gm("Bob", $"!item {entry} {count}");
        await Gm("Carol", $"!item {entry} {count}");
        var character = _app.Services.GetRequiredService<CharacterService>();
        var members = _app.Services.GetRequiredService<SessionRegistry>().All();
        var bobSession = members.Single(x => x.Role?.Id == (long)bob.Self!.Uuid);
        var carolSession = members.Single(x => x.Role?.Id == (long)carol.Self!.Uuid);
        long bobBefore = character.CountOf(bobSession, entry), carolBefore = character.CountOf(carolSession, entry);
        var req = new GuildCatShopItemHelpOtherReq
        { PlayerID = (long)alice.Self!.Uuid, SkuIndex = request.Index, DayVersion = show.DayVersion };
        await Task.WhenAll(bob.SendGameAsync(GuildCaravanService.HelpOther, req),
            carol.SendGameAsync(GuildCaravanService.HelpOther, req));
        var results = new[]
        {
            (await Next(bob, GuildCaravanService.HelpOther, GuildCatShopItemHelpOtherResp.Parser)).Result,
            (await Next(carol, GuildCaravanService.HelpOther, GuildCatShopItemHelpOtherResp.Parser)).Result,
        };
        Assert.Contains(0, results);
        Assert.Contains(ErrorCodes.CaravanRequestMissing, results);
        Assert.Equal(bobBefore + carolBefore - count,
            character.CountOf(bobSession, entry) + character.CountOf(carolSession, entry));
        Assert.Equal(1, (int)(await Show(alice)).SkuData.Single(x => x.Index == request.Index).Status);
    }

    [ClientDataFact]
    public async Task CommittedHelpRepliesWhenRecipientDisconnectsBeforePendingDelivery()
    {
        await using var alice = await InWorld("Alice");
        var bob = await InWorld("Bob");
        bool disposed = false;
        try
        {
            long guild = await Guild(alice);
            await Join(alice, bob, guild);
            var show = await Show(bob);
            await LoadFive(bob, "Bob", show);
            var request = show.SkuData[5];
            await bob.SendGameAsync(GuildCaravanService.WantHelp, new GuildCatShopItemWantHelpReq
            { SkuIndex = request.Index, DayVersion = show.DayVersion });
            Assert.Equal(0, (await Next(bob, GuildCaravanService.WantHelp, GuildCatShopItemWantHelpResp.Parser)).Result);
            var (entry, count, _) = Cargo(_app, request);
            await Gm("Alice", $"!item {entry} {count}");
            await Show(alice);
            long bobId = (long)bob.Self!.Uuid;
            var recipient = Session(bob);
            await recipient.StateLock.WaitAsync();
            Task? help = null;
            try
            {
                var helper = Session(alice);
                var loop = _app.Services.GetRequiredService<GameLoop>();
                await loop.InvokeAsync(() =>
                {
                    help = loop.TrackAsync("test committed help before recipient detach", () =>
                        _app.Services.GetRequiredService<GuildCaravanService>().HelpOtherAsync(helper,
                            new GuildCatShopItemHelpOtherReq
                            { PlayerID = bobId, SkuIndex = request.Index, DayVersion = show.DayVersion }));
                    return Task.CompletedTask;
                });
                for (int i = 0; i < 100 && PendingCount(bobId) == 0; i++) await Task.Delay(20);
                Assert.Equal(1, PendingCount(bobId));
                Assert.False(help!.IsCompleted);
                var attachment = recipient.Attachment;
                await bob.DisposeAsync();
                disposed = true;
                // "Disconnects before the pending delivery" means the server has seen it: RoleHandover.Begin cancels
                // the attachment on the loop without the state lock. Without this wait a loaded machine let the help
                // finish first and deliver to the still-attached Bob (the full-suite failure: no pending award).
                for (int i = 0; i < 250 && attachment is { Cancelled: false }; i++) await Task.Delay(20);
                Assert.True(attachment is null or { Cancelled: true }, "the server never noticed Bob's disconnect");
            }
            finally { recipient.StateLock.Release(); }
            await help!.WaitAsync(Wait);
            Assert.Equal(0, (await Next(alice, GuildCaravanService.HelpOther, GuildCatShopItemHelpOtherResp.Parser)).Result);
            var pending = Pending(bobId);
            Assert.True(pending.GuildBooked);
            Assert.Equal(1, PendingCount(bobId));
            await using var returned = await InWorld("Bob", create: false);
            Assert.Equal(0, PendingCount(bobId));
            Assert.Equal((1, -pending.Grants[102], pending.Grants[102], 0L),
                SourceLegs(pending.Source + ":deliver", bobId));
        }
        finally { if (!disposed) await bob.DisposeAsync(); }
    }

    [ClientDataFact]
    public async Task CommittedHelpRepliesWhileRecipientQuiescesBeforeResourceProjection()
    {
        await using var alice = await InWorld("Alice");
        var bob = await InWorld("Bob");
        bool disposed = false;
        var accounts = _app.Services.GetRequiredService<AccountStore>();
        var handover = _app.Services.GetRequiredService<RoleHandover>();
        var paymentEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releasePayment = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var quiescing = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cleanupEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseCleanup = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task? help = null;
        long bobId = 0, guild = 0, personalBefore = 0, fundsBefore = 0;
        int entry = 0, count = 0;
        try
        {
            guild = await Guild(alice);
            await Join(alice, bob, guild);
            var show = await Show(bob);
            await LoadFive(bob, "Bob", show);
            var request = show.SkuData[5];
            await bob.SendGameAsync(GuildCaravanService.WantHelp, new GuildCatShopItemWantHelpReq
            { SkuIndex = request.Index, DayVersion = show.DayVersion });
            Assert.Equal(0, (await Next(bob, GuildCaravanService.WantHelp, GuildCatShopItemWantHelpResp.Parser)).Result);
            (entry, count, _) = Cargo(_app, request);
            await Gm("Alice", $"!item {entry} {count}");
            await Show(alice);
            bobId = (long)bob.Self!.Uuid;
            var recipient = Session(bob);
            var helper = Session(alice);
            personalBefore = recipient.Progress!.Currency.GetValueOrDefault(102);
            fundsBefore = _app.Services.GetRequiredService<GuildStore>().Get(guild)!.Money;

            accounts.BeforeOwnedCommitForTest = label =>
            {
                if (label != "economy/guild-caravan-award") return Task.CompletedTask;
                paymentEntered.TrySetResult();
                return releasePayment.Task;
            };
            handover.PhaseForTest = (phase, session) =>
            {
                if (phase == "Quiescing" && ReferenceEquals(session, recipient)) quiescing.TrySetResult();
            };
            handover.BeforeCleanupForTest = session =>
            {
                if (!ReferenceEquals(session, recipient)) return Task.CompletedTask;
                cleanupEntered.TrySetResult();
                return releaseCleanup.Task;
            };
            var loop = _app.Services.GetRequiredService<GameLoop>();
            await loop.InvokeAsync(() =>
            {
                help = loop.TrackAsync("test caravan help during recipient quiescing", () =>
                    _app.Services.GetRequiredService<GuildCaravanService>().HelpOtherAsync(helper,
                        new GuildCatShopItemHelpOtherReq
                        { PlayerID = bobId, SkuIndex = request.Index, DayVersion = show.DayVersion }));
                return Task.CompletedTask;
            });
            await paymentEntered.Task.WaitAsync(Wait);
            Assert.Equal(0, PendingCount(bobId));
            await bob.DisposeAsync();
            disposed = true;
            await quiescing.Task.WaitAsync(Wait);
            Assert.Equal(RoleOwnerState.Quiescing, recipient.Owner!.State);
            releasePayment.TrySetResult();

            // Bob's cleanup remains parked while Alice receives the outcome of her
            // already committed payment. The pending award must stay durable for login.
            await help!.WaitAsync(Wait);
            Assert.Equal(0, (await Next(alice, GuildCaravanService.HelpOther, GuildCatShopItemHelpOtherResp.Parser)).Result);
            await cleanupEntered.Task.WaitAsync(Wait);
            Assert.Equal(RoleOwnerState.Quiescing, recipient.Owner!.State);
            var pending = Pending(bobId);
            Assert.True(pending.GuildBooked);
            Assert.Equal(1, PendingCount(bobId));
            Assert.Equal((1, pending.Grants.GetValueOrDefault(102), 0L, pending.Grants.GetValueOrDefault(104)),
                SourceLegs(pending.Source, bobId));
            Assert.Equal(personalBefore, _app.Services.GetRequiredService<RoleStore>()
                .LoadProgress(bobId).Currency.GetValueOrDefault(102));
            Assert.Equal(fundsBefore + pending.Grants.GetValueOrDefault(104),
                _app.Services.GetRequiredService<GuildStore>().Get(guild)!.Money);
            Assert.Equal(0, _app.Services.GetRequiredService<CharacterService>().CountOf(helper, entry));
            long history = _app.Services.GetRequiredService<GuildStore>().Get(guild)!.Members[bobId].HistoryContribute;

            releaseCleanup.TrySetResult();
            await using var returned = await InWorld("Bob", create: false);
            Assert.Equal(0, PendingCount(bobId));
            Assert.Equal(history, Session(returned).Guild!.HistoryContribute);
            Assert.Equal(history, DBRoleGuildInfo.Parser.ParseFrom(_app.Services.GetRequiredService<RoleStore>()
                .LoadGuildInfo(bobId)!).HistoryContribute);
            Assert.Equal(personalBefore + pending.Grants.GetValueOrDefault(102),
                _app.Services.GetRequiredService<RoleStore>().LoadProgress(bobId).Currency.GetValueOrDefault(102));
            Assert.Equal((1, -pending.Grants.GetValueOrDefault(102), pending.Grants.GetValueOrDefault(102), 0L),
                SourceLegs(pending.Source + ":deliver", bobId));
            await Show(returned);
            Assert.Equal(1, SourceLegs(pending.Source + ":deliver", bobId).Receipts);
        }
        finally
        {
            releasePayment.TrySetResult();
            releaseCleanup.TrySetResult();
            accounts.BeforeOwnedCommitForTest = null;
            handover.PhaseForTest = null;
            handover.BeforeCleanupForTest = null;
            if (help != null) _ = await Record.ExceptionAsync(() => help.WaitAsync(Wait));
            if (!disposed) await bob.DisposeAsync();
        }
    }

    [ClientDataFact]
    public async Task MigratedYesterdayOrderRollsOverForOriginalGuildBeforeNewOrder()
    {
        await using var alice = await InWorld("Alice");
        await using var bob = await InWorld("Bob");
        await using var carol = await InWorld("Carol");
        long oldGuild = await Guild(alice);
        await Join(alice, bob, oldGuild);
        var show = await Show(bob);
        await LoadFive(bob, "Bob", show);
        await Gm("Carol", "!lvl 34");
        await carol.SendGameAsync(GuildService.Create, new GuildCreateReq
        { Name = "Second", Icon = new GuildHeadIcon { BackgroundType = 100101, IconType = 200101 } });
        Assert.Equal(0, (await Next(carol, GuildService.Create, GuildCreateResp.Parser)).Result);
        long newGuild = (await Next(carol, GuildService.Full, GuildFullInfoNtf.Parser, x => x.Guid > 0)).Guid;
        await carol.SendGameAsync(GuildService.Edit, new GuildEditReq
        { Tp = (GuildEditReq.Types.EditType)6, AutoAcceptApply = true });
        Assert.Equal(0, (await Next(carol, GuildService.Edit, GuildEditResp.Parser)).Result);
        long bobId = (long)bob.Self!.Uuid;
        await _app.Services.GetRequiredService<GameLoop>().InvokeAsync(() =>
            _app.Services.GetRequiredService<GuildService>().KickAsync(
                Session(alice), new GuildMemberKickReq { TarPlayerIDs = { bobId }, IfSingleKick = true }));
        Assert.Equal(0, (await Next(alice, GuildService.Kick, GuildMemberKickResp.Parser)).Result);
        await bob.SendGameAsync(GuildService.Apply, new GuildEnterApplyReq { GuildIds = { newGuild } });
        Assert.Equal(0, Assert.Single((await Next(bob, GuildService.Apply, GuildEnterApplyResp.Parser)).ApplyResult).Result);
        await Next(bob, GuildService.Full, GuildFullInfoNtf.Parser, x => x.Guid == newGuild);
        Assert.Equal(oldGuild, OrderGuild(bobId));

        long previousDay = show.DayVersion / 1_000_000 - 1;
        var accounts = _app.Services.GetRequiredService<AccountStore>();
        using (var db = accounts.Open())
        using (var cmd = db.CreateCommand())
        {
            cmd.CommandText = "SELECT state FROM guild_caravan_member WHERE role_id=$role";
            cmd.Parameters.AddWithValue("$role", bobId);
            var state = JsonNode.Parse((byte[])cmd.ExecuteScalar()!)!.AsObject();
            state["Day"] = previousDay;
            Assert.Equal(1, FixtureSql.Execute(accounts,
                "UPDATE guild_caravan_member SET state=$state WHERE role_id=$role",
                ("$role", bobId), ("$state", JsonSerializer.SerializeToUtf8Bytes(state))));
        }
        long oldMoney = _app.Services.GetRequiredService<GuildStore>().Get(oldGuild)!.Money;
        long newMoney = _app.Services.GetRequiredService<GuildStore>().Get(newGuild)!.Money;
        await Show(bob);
        Assert.Equal(newGuild, OrderGuild(bobId));
        Assert.Single(Mailbox(bobId).Letters, x => x.TemplateId == 1023);
        Assert.Equal(oldMoney + 1000, _app.Services.GetRequiredService<GuildStore>().Get(oldGuild)!.Money);
        Assert.Equal(newMoney, _app.Services.GetRequiredService<GuildStore>().Get(newGuild)!.Money);
        string source = $"guild-caravan:{bobId}:{previousDay}:0";
        Assert.Equal((1, 0L, 0L, 1000L), SourceLegs(source, bobId));
        await Show(bob);
        Assert.Equal(1, SourceLegs(source, bobId).Receipts);
    }

    [ClientDataFact]
    public async Task MigratedLegacyUnbookedPendingCreditsOriginalGuildAndRoleOnce()
    {
        await using var alice = await InWorld("Alice");
        await using var bob = await InWorld("Bob");
        await using var carol = await InWorld("Carol");
        long oldGuild = await Guild(alice);
        await Join(alice, bob, oldGuild);
        await Show(bob);
        await Gm("Carol", "!lvl 34");
        await carol.SendGameAsync(GuildService.Create, new GuildCreateReq
        { Name = "Second", Icon = new GuildHeadIcon { BackgroundType = 100101, IconType = 200101 } });
        Assert.Equal(0, (await Next(carol, GuildService.Create, GuildCreateResp.Parser)).Result);
        long newGuild = (await Next(carol, GuildService.Full, GuildFullInfoNtf.Parser, x => x.Guid > 0)).Guid;
        await carol.SendGameAsync(GuildService.Edit, new GuildEditReq
        { Tp = (GuildEditReq.Types.EditType)6, AutoAcceptApply = true });
        Assert.Equal(0, (await Next(carol, GuildService.Edit, GuildEditResp.Parser)).Result);
        await _app.Services.GetRequiredService<GameLoop>().InvokeAsync(() =>
            _app.Services.GetRequiredService<GuildService>().KickAsync(
                Session(alice), new GuildMemberKickReq { TarPlayerIDs = { (long)bob.Self!.Uuid }, IfSingleKick = true }));
        Assert.Equal(0, (await Next(alice, GuildService.Kick, GuildMemberKickResp.Parser)).Result);
        await bob.SendGameAsync(GuildService.Apply, new GuildEnterApplyReq { GuildIds = { newGuild } });
        Assert.Equal(0, Assert.Single((await Next(bob, GuildService.Apply, GuildEnterApplyResp.Parser)).ApplyResult).Result);
        await Next(bob, GuildService.Full, GuildFullInfoNtf.Parser, x => x.Guid == newGuild);
        await Show(bob);

        long bobId = (long)bob.Self!.Uuid;
        string source = $"guild-caravan:legacy-migrated:{bobId}:{oldGuild}";
        var accounts = _app.Services.GetRequiredService<AccountStore>();
        using (var db = accounts.Open())
        using (var cmd = db.CreateCommand())
        {
            cmd.CommandText = "SELECT state FROM guild_caravan_member WHERE role_id=$role";
            cmd.Parameters.AddWithValue("$role", bobId);
            var state = JsonNode.Parse((byte[])cmd.ExecuteScalar()!)!.AsObject();
            state["PendingAwards"]!.AsArray().Add(new JsonObject
            {
                ["SourceKey"] = source, ["GuildId"] = oldGuild, ["GuildBooked"] = false,
                ["Grants"] = new JsonArray
                {
                    new JsonObject { ["Entry"] = 102, ["Count"] = 200, ["BindType"] = 0, ["ExpireTimeStamp"] = 0 },
                    new JsonObject { ["Entry"] = 104, ["Count"] = 500, ["BindType"] = 0, ["ExpireTimeStamp"] = 0 }
                }
            });
            Assert.Equal(1, FixtureSql.Execute(accounts,
                "UPDATE guild_caravan_member SET state=$state WHERE role_id=$role",
                ("$role", bobId), ("$state", JsonSerializer.SerializeToUtf8Bytes(state))));
        }
        long personalBefore = Session(bob).Progress!.Currency.GetValueOrDefault(102);
        long oldMoney = _app.Services.GetRequiredService<GuildStore>().Get(oldGuild)!.Money;
        long newMoney = _app.Services.GetRequiredService<GuildStore>().Get(newGuild)!.Money;
        Assert.Equal(newGuild, OrderGuild(bobId));
        await Show(bob);
        Assert.Equal(0, PendingCount(bobId));
        Assert.Equal(personalBefore + 200, Session(bob).Progress!.Currency[102]);
        Assert.Equal(oldMoney + 500, _app.Services.GetRequiredService<GuildStore>().Get(oldGuild)!.Money);
        Assert.Equal(newMoney, _app.Services.GetRequiredService<GuildStore>().Get(newGuild)!.Money);
        Assert.Equal((1, -200L, 200L, 0L), SourceLegs(source + ":deliver", bobId));
        await Show(bob);
        Assert.Equal(1, SourceLegs(source + ":deliver", bobId).Receipts);
    }

    [ClientDataFact]
    public async Task PendingAwardCannotOverwriteOrderMigratedToAnotherGuildDuringAwait()
    {
        await using var alice = await InWorld("Alice");
        await using var bob = await InWorld("Bob");
        await using var carol = await InWorld("Carol");
        long oldGuild = await Guild(alice);
        await Join(alice, bob, oldGuild);
        await Gm("Carol", "!lvl 34");
        await carol.SendGameAsync(GuildService.Create, new GuildCreateReq
        { Name = "Second", Icon = new GuildHeadIcon { BackgroundType = 100101, IconType = 200101 } });
        Assert.Equal(0, (await Next(carol, GuildService.Create, GuildCreateResp.Parser)).Result);
        long newGuild = (await Next(carol, GuildService.Full, GuildFullInfoNtf.Parser, x => x.Guid > 0)).Guid;
        await carol.SendGameAsync(GuildService.Edit, new GuildEditReq
        { Tp = (GuildEditReq.Types.EditType)6, AutoAcceptApply = true });
        Assert.Equal(0, (await Next(carol, GuildService.Edit, GuildEditResp.Parser)).Result);
        var show = await Show(bob);
        await LoadFive(bob, "Bob", show);
        var requested = show.SkuData[5];
        await bob.SendGameAsync(GuildCaravanService.WantHelp, new GuildCatShopItemWantHelpReq
        { SkuIndex = requested.Index, DayVersion = show.DayVersion });
        Assert.Equal(0, (await Next(bob, GuildCaravanService.WantHelp, GuildCatShopItemWantHelpResp.Parser)).Result);
        var (entry, count, _) = Cargo(_app, requested);
        await Gm("Alice", $"!item {entry} {count}");
        var sessions = _app.Services.GetRequiredService<SessionRegistry>();
        var aliceSession = sessions.All().Single(x => x.Role?.Id == (long)alice.Self!.Uuid);
        var bobSession = sessions.All().Single(x => x.Role?.Id == (long)bob.Self!.Uuid);
        long bobId = bobSession.Role!.Id;
        var config = _app.Services.GetRequiredService<Ro3Emu.Server.Data.GameData>()
            .Row("GuildTransportConfig", requested.CfgId)!.Value;
        long targetGroup = (long)(Ro3Emu.Server.Data.GameData.Number(config.GetProperty("_kAward")[0]) ?? 0);
        long expectedPersonal = _app.Services.GetRequiredService<AwardService>()
            .ResolveForProgress(bobSession.Progress!, targetGroup).Grants
            .Where(x => x.Entry == 102).Sum(x => (long)x.Count);
        long beforePersonal = bobSession.Progress!.Currency.GetValueOrDefault(102);
        var caravan = _app.Services.GetRequiredService<GuildCaravanService>();
        var recipientWaiting = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var showWaiting = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseDelivery = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        // Park the actual pre-admission continuations. Holding a naked StateLock here also
        // traps unrelated legacy Realm timers, obscuring the stale-order regression.
        caravan.BeforeCharacterAdmissionForTest = origin =>
        {
            if (origin == "guild-caravan/recipient-delivery") recipientWaiting.TrySetResult();
            else if (origin == "guild-caravan/show-delivery") showWaiting.TrySetResult();
            else return Task.CompletedTask;
            return releaseDelivery.Task;
        };
        Task? oldHelp = null, newShow = null;
        try
        {
            var loop = _app.Services.GetRequiredService<GameLoop>();
            await loop.InvokeAsync(() =>
            {
                oldHelp = loop.TrackAsync("test caravan help", () =>
                    _app.Services.GetRequiredService<GuildCaravanService>().HelpOtherAsync(aliceSession,
                        new GuildCatShopItemHelpOtherReq
                        { PlayerID = bobId, SkuIndex = requested.Index, DayVersion = show.DayVersion }));
                return Task.CompletedTask;
            });
            await recipientWaiting.Task.WaitAsync(Wait);
            Assert.Equal(1, PendingCount(bobId));
            await loop.InvokeAsync(() => _app.Services.GetRequiredService<GuildService>().KickAsync(aliceSession,
                new GuildMemberKickReq { TarPlayerIDs = { bobId }, IfSingleKick = true }));
            Assert.Equal(0, (await Next(alice, GuildService.Kick, GuildMemberKickResp.Parser)).Result);
            await bob.SendGameAsync(GuildService.Apply, new GuildEnterApplyReq { GuildIds = { newGuild } });
            Assert.Equal(0, Assert.Single((await Next(bob, GuildService.Apply, GuildEnterApplyResp.Parser)).ApplyResult).Result);
            await Next(bob, GuildService.Full, GuildFullInfoNtf.Parser, x => x.Guid == newGuild);
            await loop.InvokeAsync(() =>
            {
                newShow = loop.TrackAsync("test caravan show", () =>
                    _app.Services.GetRequiredService<GuildCaravanService>().ShowAsync(bobSession));
                return Task.CompletedTask;
            });
            await showWaiting.Task.WaitAsync(Wait);
            Assert.Equal(newGuild, OrderGuild(bobId));
            Assert.Equal(1, PendingCount(bobId));
            Assert.False(oldHelp!.IsCompleted);
            Assert.False(newShow!.IsCompleted);
        }
        finally
        {
            caravan.BeforeCharacterAdmissionForTest = null;
            releaseDelivery.TrySetResult();
        }
        await Task.WhenAll(oldHelp!, newShow!);
        Assert.Equal(0, (await Next(alice, GuildCaravanService.HelpOther, GuildCatShopItemHelpOtherResp.Parser)).Result);
        Assert.Equal(newGuild, OrderGuild(bobId));
        Assert.Equal(0, PendingCount(bobId));
        Assert.Equal(beforePersonal + expectedPersonal,
            _app.Services.GetRequiredService<RoleStore>().LoadProgress(bobId).Currency.GetValueOrDefault(102));
        var after = await Show(bob);
        Assert.Equal(1, (int)after.SkuData.Single(x => x.Index == requested.Index).Status);
    }

    [ClientDataFact]
    public async Task FailedPersistenceRollsBackCargoAndConfiguredReward()
    {
        await using var alice = await InWorld("Alice");
        long guild = await Guild(alice);
        var show = await Show(alice);
        var sku = show.SkuData[0];
        var (entry, count, _) = Cargo(_app, sku);
        await Gm("Alice", $"!item {entry} {count}");
        var session = _app.Services.GetRequiredService<SessionRegistry>().All()
            .Single(x => x.Role?.Id == (long)alice.Self!.Uuid);
        var character = _app.Services.GetRequiredService<CharacterService>();
        var store = _app.Services.GetRequiredService<GuildStore>();
        var owners = _app.Services.GetRequiredService<OwnerRegistry>();
        long cargoBefore = character.CountOf(session, entry);
        long guildBefore = store.Get(guild)!.Money;
        long personalBefore = session.Progress!.Currency.GetValueOrDefault(102);
        FixtureSql.Execute(_app.Services.GetRequiredService<AccountStore>(),
            $"CREATE TRIGGER fail_caravan BEFORE UPDATE ON guild_caravan_member " +
            $"WHEN NEW.role_id = {(long)alice.Self!.Uuid} BEGIN SELECT RAISE(ABORT, 'caravan test failure'); END");
        try
        {
            var failure = await Assert.ThrowsAsync<Microsoft.Data.Sqlite.SqliteException>(() =>
                _app.Services.GetRequiredService<GameLoop>().InvokeAsync(() =>
                    owners.RunSessionAsync("dispatcher", [ReservationKey.Realm], session, () =>
                        _app.Services.GetRequiredService<GuildCaravanService>().SubmitAsync(session,
                            new GuildCatShopItemSubmitReq { SkuIndex = sku.Index, DayVersion = show.DayVersion }))));
            Assert.Contains("caravan test failure", failure.Message);
            Assert.Equal(cargoBefore, character.CountOf(session, entry));
            Assert.Equal(guildBefore, store.Get(guild)!.Money);
            Assert.Equal(personalBefore, session.Progress!.Currency.GetValueOrDefault(102));
            Assert.Equal(0, (int)(await Show(alice)).SkuData.Single(x => x.Index == sku.Index).Status);
        }
        finally
        {
            FixtureSql.Execute(_app.Services.GetRequiredService<AccountStore>(), "DROP TRIGGER fail_caravan");
        }
        await alice.SendGameAsync(GuildCaravanService.Submit, new GuildCatShopItemSubmitReq
        { SkuIndex = sku.Index, DayVersion = show.DayVersion });
        Assert.Equal(0, (await Next(alice, GuildCaravanService.Submit, GuildCatShopItemSubmitResp.Parser)).Result);
    }
}

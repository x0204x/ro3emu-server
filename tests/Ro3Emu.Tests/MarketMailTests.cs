using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text.Json;
using Google.Protobuf;
using MessagePack;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Romsg;
using Ro3Emu.Bot;
using Ro3Emu.Server;
using Ro3Emu.Server.Accounts;
using Ro3Emu.Server.Data;
using Ro3Emu.Server.Game;
using Ro3Emu.Server.Economy;

namespace Ro3Emu.Tests;

/// <summary>Current MailService micro records and 31602 durable claim.</summary>
[Collection("Market server")]
public sealed class MarketMailTests : IAsyncLifetime
{
    private readonly string _dataDir = Path.Combine(Path.GetTempPath(), "ro3emu-market-mail-" + Guid.NewGuid().ToString("N"));
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(5);
    private WebApplication _app = null!;
    private string _http = "";

    public async Task InitializeAsync()
    {
        var (httpPort, microPort, gamePort) = TestPorts.FreePorts();
        _app = ServerHost.Create([], o =>
        {
            o.DataDir = _dataDir;
            o.GameDataDir = SyntheticData.Dir;
            o.HttpPort = httpPort;
            o.MicroGatePort = microPort;
            o.GameGatePort = gamePort;
        });
        await _app.StartAsync();
        _http = $"http://127.0.0.1:{httpPort}";
    }

    public async Task DisposeAsync()
    {
        await _app.StopAsync();
        await _app.DisposeAsync();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dataDir, true); } catch (IOException) { }
    }

    private async Task<(BotClient Bot, long Id)> InWorld(string name, bool create = true)
    {
        var bot = new BotClient(_http, File.ReadAllText(Path.Combine(_dataDir, "gate_rsa_public.pem")));
        await bot.LoginAsync(name.ToLowerInvariant(), "pw");
        await bot.EnterGateAsync();
        long id = create ? (await bot.CreateRoleAsync(name)).RoleBaseInfo.Id : bot.Roles.Single().Id;
        await bot.EnterWorldAsync(id);
        return (bot, id);
    }

    private async Task<string> Deliver(long role, int template, IEnumerable<InvItem>? items = null,
        IReadOnlyDictionary<int, long>? currency = null, params string[] contentParams)
    {
        var loop = _app.Services.GetRequiredService<GameLoop>();
        var sessions = _app.Services.GetRequiredService<SessionRegistry>();
        var market = _app.Services.GetRequiredService<MarketStore>();
        var capturedItems = items?.ToArray();
        return await loop.InvokeAsync(async () =>
        {
            var owner = sessions.All().Single(s => s.Role?.Id == role);
            string id = "";
            Assert.Equal(0, await market.RunAsync(owner, tx =>
            {
                foreach (var item in capturedItems ?? [])
                    tx.Economy.Source(new(EconomyAssetKind.Item, item.Entry), item.Stack, EconomyReason.GmGrant);
                foreach (var (entry, amount) in currency ?? new Dictionary<int, long>())
                    tx.Economy.Source(new(EconomyAssetKind.Currency, entry), amount, EconomyReason.GmGrant);
                id = tx.DeliverMail(role, template, capturedItems, currency, contentParams);
                return 0;
            }));
            return id;
        });
    }

    private static async Task<Dictionary<object, object?>> List(BotClient bot, long id) =>
        await bot.MicroAsync("MailService", "getUserMailList", new()
        {
            ["Uid"] = id.ToString(), ["Start"] = 0, ["Limit"] = 150, ["MailType"] = 1,
        });

    private static Dictionary<object, object?> Row(Dictionary<object, object?> list) =>
        Assert.IsType<Dictionary<object, object?>>(Assert.Single(Assert.IsType<object?[]>(list["MailList"])));

    private static Dictionary<object, object?> RowById(Dictionary<object, object?> list, string id) =>
        Assert.IsType<Dictionary<object, object?>>(Assert.Single(Assert.IsType<object?[]>(list["MailList"]),
            value => Assert.IsType<Dictionary<object, object?>>(value)["MailId"]!.Equals(id)));

    private async Task RestartAsync()
    {
        await _app.StopAsync();
        await _app.DisposeAsync();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        await InitializeAsync();
    }

    private static bool Ok(Dictionary<object, object?> response) =>
        response["Ret"] is object?[] ret && ret[0] is true;

    private static async Task<Dictionary<object, object?>> Positional(BotClient bot, string method, params object[] args)
    {
        // BotClient's public MicroAsync currently accepts only maps; exercise the actual positional wire RPC.
        var call = typeof(BotClient).GetMethod("CallMicroAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var pending = (Task<byte[]>)call.Invoke(bot, ["MailService", method, args])!;
        return MessagePackSerializer.Deserialize<Dictionary<object, object?>>(await pending);
    }

    private static async Task<T> Next<T>(BotClient bot, uint id, MessageParser<T> parser) where T : IMessage<T> =>
        parser.ParseFrom((await bot.Inbox.WaitAsync(e => e.IsGame(id), Wait)).Body);

    [Fact]
    public async Task BothMailboxFormatsMigrateToTypedWireAndClaimMixedAssetsOnce()
    {
        var (alice, role) = await InWorld("Formats");
        const string auctionId = "1790000000000000011", marketId = "1790000000000000012";
        var typed = new LanguageData();
        typed.Param.Add(new LanguageParam { Tp = LanguageParamType.Int, IntParam = 12345 });
        typed.Param.Add(new LanguageParam { Tp = LanguageParamType.Str, StrParam = "Auction" });
        var roles = _app.Services.GetRequiredService<RoleStore>();
        roles.SaveMailbox(role, JsonSerializer.SerializeToUtf8Bytes(new
        {
            NextId = 1790000000000000012L,
            Letters = new object[]
            {
                new
                {
                    // 1040 (refund): award letters (1042/1045) with string parameters are sent without them.
                    Id = auctionId, TemplateId = 1040, CreatedTime = 1_780_000_000L, RemoveTime = 0,
                    DeliveryKey = "auction:legacy:award", ContentParams = (object)typed.ToByteArray(),
                    Attachments = new[] { new MailService.Attachment(1000000, 3, 2) },
                },
                new
                {
                    Id = marketId, TemplateId = 1064, CreatedTime = 1_780_000_001L, RemoveTime = 0,
                    ContentParams = (object)new[] { "Legacy market" },
                    Items = new[] { new InvItem
                    {
                        Entry = 2010121, Stack = 1, BindType = 1, BindExpire = 9876,
                        Attrs = new() { [30] = 777 }, StringAttrs = new() { [510] = "kept" },
                    } },
                    Currency = new Dictionary<int, long> { [1] = 4_000_000_000L },
                },
            },
        }));

        var before = await List(alice, role);
        var auctionParams = Assert.IsType<Dictionary<object, object?>>(RowById(before, auctionId)["Params"]);
        var auctionLanguage = LanguageData.Parser.ParseFrom(Assert.IsType<byte[]>(auctionParams["content_params"]));
        Assert.Equal(LanguageParamType.Int, auctionLanguage.Param[0].Tp);
        Assert.Equal(12345, auctionLanguage.Param[0].IntParam);
        Assert.Equal(LanguageParamType.Str, auctionLanguage.Param[1].Tp);
        Assert.Equal("Auction", auctionLanguage.Param[1].StrParam);
        Assert.Equal(2, (int)MailItemList.Parser.ParseFrom(Assert.IsType<byte[]>(auctionParams["items"]))
            .Items[0].ItemBindInfo.ItemBindType);
        var marketLanguage = LanguageData.Parser.ParseFrom(Assert.IsType<byte[]>(
            Assert.IsType<Dictionary<object, object?>>(RowById(before, marketId)["Params"])["content_params"]));
        Assert.Equal("Legacy market", Assert.Single(marketLanguage.Param).StrParam);

        await alice.SendGameAsync(MailService.ClaimMail, new ClaimMailReq { MailUid = { auctionId, marketId } });
        var result = await Next(alice, MailService.ClaimMail, ClaimMailRsp.Parser);
        Assert.Equal(0, result.Result);
        Assert.Equal(new[] { auctionId, marketId }, result.MailUid);
        var items = _app.Services.GetRequiredService<ItemStore>().Load(role);
        Assert.Contains(items, i => i.Entry == 1000000 && i.BindType == 2 && i.Stack == 3);
        var exact = Assert.Single(items, i => i.Attrs?.GetValueOrDefault(30) == 777);
        Assert.Equal(1, exact.BindType);
        Assert.Equal(9876, exact.BindExpire);
        Assert.Equal("kept", exact.StringAttrs![510]);
        Assert.Equal(4_000_001_000L, roles.LoadProgress(role).Currency[1]);
        var persisted = JsonDocument.Parse(roles.LoadMailbox(role)!);
        Assert.All(persisted.RootElement.GetProperty("Letters").EnumerateArray(), letter =>
            Assert.Equal(JsonValueKind.String, letter.GetProperty("ContentParams").ValueKind));

        await alice.DisposeAsync();
        await RestartAsync();
        var (again, sameRole) = await InWorld("Formats", false);
        await using var _ = again;
        Assert.Equal(role, sameRole);
        await again.SendGameAsync(MailService.ClaimMail, new ClaimMailReq { MailUid = { auctionId, marketId } });
        Assert.Equal(ErrorCodes.MailAlreadyClaimed, (await Next(again, MailService.ClaimMail, ClaimMailRsp.Parser)).Result);
        Assert.Equal(4_000_001_000L, _app.Services.GetRequiredService<RoleStore>().LoadProgress(role).Currency[1]);
    }

    [Fact]
    public async Task BoundAttachmentNeedsMatchingStackWhenBagIsFull()
    {
        var (alice, role) = await InWorld("BoundBag");
        await using var _ = alice;
        var mail = _app.Services.GetRequiredService<MailService>();
        string id = await mail.SendRawAsync(role, "Bound", "Body", [new(1000000, 1, 2)]);
        var data = _app.Services.GetRequiredService<GameData>();
        int capacity = (int)GameData.Int(data.Row("BackpackConfig", 1)!.Value, "_iTabCapacity");
        var loop = _app.Services.GetRequiredService<GameLoop>();
        var sessions = _app.Services.GetRequiredService<SessionRegistry>();
        var market = _app.Services.GetRequiredService<MarketStore>();
        Assert.Equal(0, await loop.InvokeAsync(async () =>
        {
            var owner = sessions.All().Single(s => s.Role?.Id == role);
            return await market.RunAsync(owner, tx =>
            {
                MarketFixtureEconomy.Edit(tx, tx.Role(role), participant =>
                {
                    var bag = participant.Items;
                    for (int n = bag.Count(i => i.Grid == 0 && i.Pack == 1); n < capacity; n++)
                        bag.Add(new InvItem { Entry = 1000000, Stack = 1, Pack = 1, Sort = n + 1 });
                });
                return 0;
            });
        }));
        await alice.SendGameAsync(MailService.ClaimMail, new ClaimMailReq { MailUid = { id } });
        Assert.Equal(ErrorCodes.BagFull, (await Next(alice, MailService.ClaimMail, ClaimMailRsp.Parser)).Result);
        Assert.Equal(false, Assert.IsType<Dictionary<object, object?>>(Row(await List(alice, role))["Params"])["giftReceived"]);

        Assert.Equal(0, await loop.InvokeAsync(async () =>
        {
            var owner = sessions.All().Single(s => s.Role?.Id == role);
            return await market.RunAsync(owner, tx =>
            {
                tx.Role(role).Items.Where(i => i.Grid == 0 && i.Pack == 1)
                    .OrderByDescending(i => i.Guid).First().BindType = 2;
                return 0;
            });
        }));
        await alice.SendGameAsync(MailService.ClaimMail, new ClaimMailReq { MailUid = { id } });
        Assert.Equal(0, (await Next(alice, MailService.ClaimMail, ClaimMailRsp.Parser)).Result);
        Assert.Contains(_app.Services.GetRequiredService<ItemStore>().Load(role),
            i => i.Entry == 1000000 && i.BindType == 2 && i.Stack == 2);
    }

    [Fact]
    public async Task MixedBoundCurrencyAndLongCurrencyOverflowDoesNotClaim()
    {
        var (alice, role) = await InWorld("CurrencyOverflow");
        await using var _ = alice;
        var roles = _app.Services.GetRequiredService<RoleStore>();
        var box = new MailService.Mailbox
        {
            Letters = [new MailService.Letter
            {
                Id = "1790000000000000021", TemplateId = 1063,
                CreatedTime = DateTimeOffset.UtcNow.ToUnixTimeSeconds(), RemoveTime = 0,
                Attachments = [new(1, 1, 2)],
                Currency = new() { [1] = long.MaxValue - 1000 },
            }],
        };
        byte[] original = JsonSerializer.SerializeToUtf8Bytes(box);
        roles.SaveMailbox(role, original);
        await alice.SendGameAsync(MailService.ClaimMail, new ClaimMailReq { MailUid = { box.Letters[0].Id } });
        var result = await Next(alice, MailService.ClaimMail, ClaimMailRsp.Parser);
        Assert.Equal(ErrorCodes.Parameter, result.Result);
        Assert.Empty(result.MailUid);
        Assert.Equal(1000, roles.LoadProgress(role).Currency[1]);
        Assert.Equal(original, roles.LoadMailbox(role)); // refused claim did not change its durable flag
    }

    [Fact]
    public async Task SingleMixedLetterRollsBackAllAssetFormsThenGrantsOnce()
    {
        var (alice, role) = await InWorld("MixedAtomic");
        await using var _ = alice;
        const string id = "1790000000000000031";
        var roles = _app.Services.GetRequiredService<RoleStore>();
        var items = _app.Services.GetRequiredService<ItemStore>();
        var box = new MailService.Mailbox
        {
            Letters = [new MailService.Letter
            {
                Id = id, TemplateId = 1064, CreatedTime = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                RemoveTime = 0, Attachments = [new(1000000, 3, 2)],
                Items = [new InvItem
                {
                    Entry = 2010121, Stack = 1, BindType = 1, BindExpire = 1234,
                    Attrs = new() { [30] = 909 }, StringAttrs = new() { [510] = "mixed" },
                }],
                Currency = new() { [1] = 4_000_000_000L },
            }],
        };
        roles.SaveMailbox(role, JsonSerializer.SerializeToUtf8Bytes(box));
        FixtureSql.Execute(_app.Services.GetRequiredService<AccountStore>(),
            $"CREATE TRIGGER fail_mixed_mail BEFORE UPDATE OF mailbox ON role WHEN NEW.id={role} BEGIN SELECT RAISE(ABORT, 'mixed rollback'); END");
        try
        {
            await alice.SendGameAsync(MailService.ClaimMail, new ClaimMailReq { MailUid = { id } });
            var failed = await Next(alice, MailService.ClaimMail, ClaimMailRsp.Parser);
            Assert.Equal(ErrorCodes.Unknown, failed.Result);
            Assert.Empty(failed.MailUid);
            Assert.Equal(1000, roles.LoadProgress(role).Currency[1]);
            Assert.DoesNotContain(items.Load(role), i => i.Entry == 1000000 && i.BindType == 2
                || i.Attrs?.GetValueOrDefault(30) == 909);
            Assert.False(JsonSerializer.Deserialize<MailService.Mailbox>(roles.LoadMailbox(role)!)!.Letters.Single().Claimed);
        }
        finally
        {
            FixtureSql.Execute(_app.Services.GetRequiredService<AccountStore>(), "DROP TRIGGER fail_mixed_mail");
        }
        await alice.SendGameAsync(MailService.ClaimMail, new ClaimMailReq { MailUid = { id } });
        Assert.Equal(0, (await Next(alice, MailService.ClaimMail, ClaimMailRsp.Parser)).Result);
        Assert.Contains(items.Load(role), i => i.Entry == 1000000 && i.BindType == 2 && i.Stack == 3);
        var exact = Assert.Single(items.Load(role), i => i.Attrs?.GetValueOrDefault(30) == 909);
        Assert.Equal(1, exact.BindType);
        Assert.Equal(1234, exact.BindExpire);
        Assert.Equal("mixed", exact.StringAttrs![510]);
        Assert.Equal(4_000_001_000L, roles.LoadProgress(role).Currency[1]);
        Assert.True(JsonSerializer.Deserialize<MailService.Mailbox>(roles.LoadMailbox(role)!)!.Letters.Single().Claimed);
        await alice.SendGameAsync(MailService.ClaimMail, new ClaimMailReq { MailUid = { id } });
        Assert.Equal(ErrorCodes.MailAlreadyClaimed, (await Next(alice, MailService.ClaimMail, ClaimMailRsp.Parser)).Result);
        Assert.Equal(4_000_001_000L, roles.LoadProgress(role).Currency[1]);
    }

    [Fact]
    public async Task TemplateWireOwnershipAndExactlyOnceClaim()
    {
        var (alice, aliceId) = await InWorld("Alice");
        var (bob, bobId) = await InWorld("Bob");
        await using var a = alice;
        await using var b = bob;

        var gift = new InvItem
        {
            Entry = 2010121, Stack = 1, Pack = 2, IsLocked = true, BindType = 2, BindExpire = 123456789,
            Attrs = new() { [3] = 1, [30] = 14 }, StringAttrs = new() { [510] = "original" },
            Enchantments = new() { [7] = new ItemEnchantment { Attr = new() { [103] = 5 }, GetTime = 123 } },
        };
        string mailId = await Deliver(aliceId, 1064, [gift], new Dictionary<int, long> { [1] = 37 }, "Buyer");
        Assert.Equal("push:MailService.newMailNotice", (await alice.Inbox.WaitAsync(e => e.Name == "push:MailService.newMailNotice", Wait)).Name);

        var own = Row(await List(alice, aliceId));
        Assert.Equal(mailId, own["MailId"]);
        Assert.IsType<string>(own["MailId"]);
        Assert.Equal(1, Convert.ToInt32(own["MailType"]));
        var parameters = Assert.IsType<Dictionary<object, object?>>(own["Params"]);
        Assert.Equal(1064, Convert.ToInt32(parameters["cfg_id"]));
        Assert.Equal(false, parameters["haveRead"]);
        Assert.Equal(false, parameters["giftReceived"]);
        var bytes = Assert.IsType<byte[]>(parameters["items"]);
        var parsed = MailItemList.Parser.ParseFrom(bytes);
        Assert.Equal(2, parsed.Items.Count); // original gear and a currency icon
        Assert.Equal(2010121, parsed.Items[0].ItemEntry);
        Assert.True(parsed.Items[0].IsLocked);
        Assert.Equal(14, parsed.Items[0].ItemInfos[30].LongVal);
        Assert.Equal(1, parsed.Items[1].ItemEntry);
        Assert.Equal(37, parsed.Items[1].Stack);
        Assert.Single(LanguageData.Parser.ParseFrom(Assert.IsType<byte[]>(parameters["content_params"])).Param);
        Assert.Empty(Assert.IsType<object?[]>((await List(bob, bobId))["MailList"]));

        Assert.False(Ok(await alice.MicroAsync("MailService", "deleteMail", new()
        {
            ["Uid"] = aliceId.ToString(), ["MailIds"] = new[] { mailId },
        })));
        Assert.False(Ok(await bob.MicroAsync("MailService", "getUserMailList", new()
        {
            ["Uid"] = aliceId.ToString(), ["Start"] = 0, ["Limit"] = 150, ["MailType"] = 1,
        })));
        Assert.False(Ok(await bob.MicroAsync("MailService", "deleteMail", new()
        {
            ["Uid"] = bobId.ToString(), ["MailIds"] = new[] { mailId },
        })));
        Assert.True(Ok(await Positional(alice, "getMailContent", aliceId.ToString(), mailId)));
        Assert.True(Ok(await Positional(alice, "setMailHaveRead", aliceId.ToString(), mailId)));
        Assert.Equal(true, Assert.IsType<Dictionary<object, object?>>(Row(await List(alice, aliceId))["Params"])["haveRead"]);

        await bob.SendGameAsync(MailService.ClaimMail, new ClaimMailReq { MailUid = { mailId } });
        Assert.Equal(ErrorCodes.MailMissing, (await Next(bob, MailService.ClaimMail, ClaimMailRsp.Parser)).Result);
        await alice.SendGameAsync(MailService.ClaimMail, new ClaimMailReq { MailUid = { mailId } });
        var claim = await Next(alice, MailService.ClaimMail, ClaimMailRsp.Parser);
        Assert.Equal(0, claim.Result);
        Assert.Equal(mailId, Assert.Single(claim.MailUid));
        var savedItems = _app.Services.GetRequiredService<ItemStore>().Load(aliceId);
        var savedGift = Assert.Single(savedItems, i => i.Attrs?.GetValueOrDefault(30) == 14);
        Assert.True(savedGift.IsLocked);
        Assert.Equal(2, savedGift.BindType);
        Assert.Equal(123456789, savedGift.BindExpire);
        Assert.Equal("original", savedGift.StringAttrs![510]);
        Assert.Equal(5, savedGift.Enchantments[7].Attr[103]);
        Assert.Equal(1037, _app.Services.GetRequiredService<RoleStore>().LoadProgress(aliceId).Currency[1]);
        var claimedRow = Row(await List(alice, aliceId));
        Assert.Equal(true, Assert.IsType<Dictionary<object, object?>>(claimedRow["Params"])["giftReceived"]);
        await alice.SendGameAsync(MailService.ClaimMail, new ClaimMailReq { MailUid = { mailId } });
        Assert.Equal(ErrorCodes.MailAlreadyClaimed, (await Next(alice, MailService.ClaimMail, ClaimMailRsp.Parser)).Result);
        Assert.Single(_app.Services.GetRequiredService<ItemStore>().Load(aliceId), i => i.Attrs?.GetValueOrDefault(30) == 14);
        Assert.Equal(1037, _app.Services.GetRequiredService<RoleStore>().LoadProgress(aliceId).Currency[1]);

        Assert.True(Ok(await alice.MicroAsync("MailService", "deleteMail", new()
        {
            ["Uid"] = aliceId.ToString(), ["MailIds"] = new[] { mailId },
        })));
        Assert.Empty(Assert.IsType<object?[]>((await List(alice, aliceId))["MailList"]));
    }

    [Fact]
    public async Task MalformedAndFullBagClaimsDoNotConsumeMail()
    {
        var (alice, aliceId) = await InWorld("FullBag");
        await using var a = alice;
        string mailId = await Deliver(aliceId, 1064, [new InvItem { Entry = 2010121, Stack = 1, Pack = 2 }]);
        Assert.False(Ok(await Positional(alice, "setMailHaveRead", aliceId.ToString(), "unknown")));
        Assert.False(Ok(await Positional(alice, "syncGroupMail", (aliceId + 1).ToString())));
        await alice.SendGameAsync(MailService.ClaimMail, new ClaimMailReq { MailUid = { mailId, mailId } });
        var deduplicated = await Next(alice, MailService.ClaimMail, ClaimMailRsp.Parser);
        Assert.Equal(0, deduplicated.Result);
        Assert.Equal(mailId, Assert.Single(deduplicated.MailUid));
        Assert.True(Ok(await alice.MicroAsync("MailService", "deleteMail", new()
        { ["Uid"] = aliceId.ToString(), ["MailIds"] = new[] { mailId } })));
        mailId = await Deliver(aliceId, 1064, [new InvItem { Entry = 2010121, Stack = 1, Pack = 2 }]);

        var data = _app.Services.GetRequiredService<GameData>();
        int capacity = (int)GameData.Int(data.Row("BackpackConfig", 2)!.Value, "_iTabCapacity");
        var loop = _app.Services.GetRequiredService<GameLoop>();
        var sessions = _app.Services.GetRequiredService<SessionRegistry>();
        var market = _app.Services.GetRequiredService<MarketStore>();
        Assert.Equal(0, await loop.InvokeAsync(async () =>
        {
            var owner = sessions.All().Single(s => s.Role?.Id == aliceId);
            return await market.RunAsync(owner, tx =>
            {
                MarketFixtureEconomy.Edit(tx, tx.Role(aliceId), role =>
                {
                    int existing = role.Items.Count(i => i.Grid == 0 && i.Pack == 2);
                    for (int n = existing; n < capacity; n++)
                        role.Items.Add(new InvItem { Entry = 2010121, Stack = 1, Pack = 2, Sort = n + 1 });
                });
                return 0;
            });
        }));
        await alice.SendGameAsync(MailService.ClaimMail, new ClaimMailReq { MailUid = { mailId } });
        Assert.Equal(ErrorCodes.BagFull, (await Next(alice, MailService.ClaimMail, ClaimMailRsp.Parser)).Result);
        Assert.Equal(false, Assert.IsType<Dictionary<object, object?>>(Row(await List(alice, aliceId))["Params"])["giftReceived"]);

        // A seller's currency-only payout does not occupy a bag slot, even when the bag is full.
        string payout = await Deliver(aliceId, 1063, currency: new Dictionary<int, long> { [1] = 7 });
        await alice.SendGameAsync(MailService.ClaimMail, new ClaimMailReq { MailUid = { payout } });
        Assert.Equal(0, (await Next(alice, MailService.ClaimMail, ClaimMailRsp.Parser)).Result);
        Assert.Equal(1007, _app.Services.GetRequiredService<RoleStore>().LoadProgress(aliceId).Currency[1]);
    }

    [Fact]
    public async Task LateSqlFailureRollsBackClaimAndRetryGrantsOnce()
    {
        var (alice, aliceId) = await InWorld("Rollback");
        await using var a = alice;
        string mailId = await Deliver(aliceId, 1064,
            [new InvItem { Entry = 2010121, Stack = 1, Pack = 2, Attrs = new() { [30] = 999 } }],
            new Dictionary<int, long> { [1] = 9 });
        var accounts = _app.Services.GetRequiredService<AccountStore>();
        var items = _app.Services.GetRequiredService<ItemStore>();
        var roles = _app.Services.GetRequiredService<RoleStore>();
        const string triggerName = "fail_mail_claim_currency";
        FixtureSql.Execute(accounts, $"""
                CREATE TRIGGER {triggerName} BEFORE UPDATE OF currency ON role
                WHEN NEW.id = {aliceId} BEGIN SELECT RAISE(ABORT, 'forced claim failure'); END
                """);
        try
        {
            await alice.SendGameAsync(MailService.ClaimMail, new ClaimMailReq { MailUid = { mailId } });
            var failed = await Next(alice, MailService.ClaimMail, ClaimMailRsp.Parser);
            Assert.Equal(ErrorCodes.Unknown, failed.Result);
            Assert.Empty(failed.MailUid);
            Assert.DoesNotContain(items.Load(aliceId), i => i.Attrs?.GetValueOrDefault(30) == 999);
            Assert.Equal(1000, roles.LoadProgress(aliceId).Currency[1]);
            var parameters = Assert.IsType<Dictionary<object, object?>>(Row(await List(alice, aliceId))["Params"]);
            Assert.Equal(false, parameters["giftReceived"]);
            Assert.Equal(false, parameters["haveRead"]);
        }
        finally
        {
            FixtureSql.Execute(accounts, $"DROP TRIGGER {triggerName}");
        }
        await alice.SendGameAsync(MailService.ClaimMail, new ClaimMailReq { MailUid = { mailId } });
        var claimed = await Next(alice, MailService.ClaimMail, ClaimMailRsp.Parser);
        Assert.Equal(0, claimed.Result);
        Assert.Equal(mailId, Assert.Single(claimed.MailUid));
        Assert.Single(items.Load(aliceId), i => i.Attrs?.GetValueOrDefault(30) == 999);
        Assert.Equal(1009, roles.LoadProgress(aliceId).Currency[1]);
        Assert.Equal(true, Assert.IsType<Dictionary<object, object?>>(Row(await List(alice, aliceId))["Params"])["giftReceived"]);
    }

    [Fact]
    public async Task PostCommitHookFailureCannotTurnGrantedClaimIntoFailure()
    {
        var (alice, aliceId) = await InWorld("PostCommit");
        await using var a = alice;
        string mailId = await Deliver(aliceId, 1064,
            [new InvItem { Entry = 2010121, Stack = 1, Pack = 2, Attrs = new() { [30] = 888 } }],
            new Dictionary<int, long> { [1] = 11 });
        var loop = _app.Services.GetRequiredService<GameLoop>();
        var character = _app.Services.GetRequiredService<CharacterService>();
        Action<GameSession, int, int> failure = (_, _, _) => throw new InvalidOperationException("forced post-commit hook failure");
        await loop.InvokeAsync(() =>
        {
            character.ItemGained.Add(failure);
            return Task.CompletedTask;
        });
        try
        {
            await alice.SendGameAsync(MailService.ClaimMail, new ClaimMailReq { MailUid = { mailId } });
            var claimed = await Next(alice, MailService.ClaimMail, ClaimMailRsp.Parser);
            Assert.Equal(0, claimed.Result);
            Assert.Equal(mailId, Assert.Single(claimed.MailUid));
        }
        finally
        {
            await loop.InvokeAsync(() =>
            {
                character.ItemGained.Remove(failure);
                return Task.CompletedTask;
            });
        }
        var items = _app.Services.GetRequiredService<ItemStore>();
        var roles = _app.Services.GetRequiredService<RoleStore>();
        Assert.Single(items.Load(aliceId), i => i.Attrs?.GetValueOrDefault(30) == 888);
        Assert.Equal(1011, roles.LoadProgress(aliceId).Currency[1]);
        Assert.Equal(true, Assert.IsType<Dictionary<object, object?>>(Row(await List(alice, aliceId))["Params"])["giftReceived"]);
        await alice.SendGameAsync(MailService.ClaimMail, new ClaimMailReq { MailUid = { mailId } });
        Assert.Equal(ErrorCodes.MailAlreadyClaimed, (await Next(alice, MailService.ClaimMail, ClaimMailRsp.Parser)).Result);
        Assert.Single(items.Load(aliceId), i => i.Attrs?.GetValueOrDefault(30) == 888);
        Assert.Equal(1011, roles.LoadProgress(aliceId).Currency[1]);
    }

    [Fact]
    public async Task RawAndMarketMailShareListBulkClaimAndLongCurrency()
    {
        var (alice, role) = await InWorld("Mixed");
        await using var _ = alice;
        string raw = await _app.Services.GetRequiredService<MailService>().SendRawAsync(role, "Raw", "Body", [new(1000000, 2)]);
        string market = await Deliver(role, 1064, [new InvItem
        {
            Entry = 2010121, Stack = 1, Attrs = new() { [30] = 77 },
            StringAttrs = new() { [510] = "mixed" }, IsLocked = true,
        }], new Dictionary<int, long> { [1] = 5_000_000_000L }, "Mixed item");
        var before = await List(alice, role);
        Assert.Equal(2, Assert.IsType<object?[]>(before["MailList"]).Length);
        var marketRow = RowById(before, market);
        var parameters = Assert.IsType<Dictionary<object, object?>>(marketRow["Params"]);
        Assert.Equal(0L, Convert.ToInt64(marketRow["RemoveTime"]));
        Assert.Equal(1064, Convert.ToInt32(parameters["cfg_id"]));
        Assert.Equal(4, MailItemList.Parser.ParseFrom(Assert.IsType<byte[]>(parameters["items"])).Items.Count);

        await alice.SendGameAsync(MailService.ClaimMail, new ClaimMailReq { MailUid = { raw, market, raw } });
        var claim = await Next(alice, MailService.ClaimMail, ClaimMailRsp.Parser);
        Assert.Equal(0, claim.Result);
        Assert.Equal(new[] { raw, market }, claim.MailUid);
        var items = _app.Services.GetRequiredService<ItemStore>().Load(role);
        var exact = Assert.Single(items, i => i.Attrs?.GetValueOrDefault(30) == 77);
        Assert.Equal("mixed", exact.StringAttrs![510]);
        Assert.True(exact.IsLocked);
        Assert.Equal(5_000_001_000L, _app.Services.GetRequiredService<RoleStore>().LoadProgress(role).Currency[1]);
        var after = await List(alice, role);
        Assert.True((bool)Assert.IsType<Dictionary<object, object?>>(RowById(after, raw)["Params"])["giftReceived"]!);
        Assert.True((bool)Assert.IsType<Dictionary<object, object?>>(RowById(after, market)["Params"])["giftReceived"]!);
    }

    [Fact]
    public async Task AuctionAndMarketMailboxFormatsKeepTypedContentBindingAndExactAssets()
    {
        var (alice, role) = await InWorld("OldFormats");
        await using var _ = alice;
        const string auctionId = "1790000000000000101", marketId = "1790000000000000102";
        var typed = new LanguageData();
        typed.Param.Add(new LanguageParam { Tp = LanguageParamType.Int, IntParam = 42 });
        typed.Param.Add(new LanguageParam { Tp = LanguageParamType.Str, StrParam = "auction" });
        var oldAuction = new
        {
            Id = auctionId, TemplateId = 1024, CreatedTime = 1_780_000_000L,
            DeliveryKey = "auction-settlement-42", ContentParams = Convert.ToBase64String(typed.ToByteArray()),
            Attachments = new[] { new { Entry = 2010121, Count = 1, Bind = 1 } },
        };
        var oldMarket = new
        {
            Id = marketId, TemplateId = 1064, CreatedTime = 1_780_000_001L,
            ContentParams = new[] { "market item" },
            Items = new[] { new InvItem { Entry = 2010121, Stack = 1, Attrs = new() { [30] = 777 },
                StringAttrs = new() { [510] = "original" }, IsLocked = true } },
            Currency = new Dictionary<int, long> { [1] = 4_000_000_000L },
        };
        var roles = _app.Services.GetRequiredService<RoleStore>();
        roles.SaveMailbox(role, JsonSerializer.SerializeToUtf8Bytes(new
        {
            NextId = 1_790_000_000_000_000_102L, Letters = new object[] { oldAuction, oldMarket },
        }));

        var list = await List(alice, role);
        var auctionParams = Assert.IsType<Dictionary<object, object?>>(RowById(list, auctionId)["Params"]);
        var auctionWire = MailItemList.Parser.ParseFrom(Assert.IsType<byte[]>(auctionParams["items"]));
        Assert.Equal((ItemBindType)1, Assert.Single(auctionWire.Items).ItemBindInfo.ItemBindType);
        Assert.Equal(typed, LanguageData.Parser.ParseFrom(Assert.IsType<byte[]>(auctionParams["content_params"])));
        var marketParams = Assert.IsType<Dictionary<object, object?>>(RowById(list, marketId)["Params"]);
        var marketLanguage = LanguageData.Parser.ParseFrom(Assert.IsType<byte[]>(marketParams["content_params"]));
        Assert.Equal("market item", Assert.Single(marketLanguage.Param).StrParam);
        Assert.Equal(LanguageParamType.Str, marketLanguage.Param[0].Tp);

        await alice.SendGameAsync(MailService.ClaimMail, new ClaimMailReq { MailUid = { auctionId, marketId, auctionId } });
        var claimed = await Next(alice, MailService.ClaimMail, ClaimMailRsp.Parser);
        Assert.Equal(0, claimed.Result);
        Assert.Equal(new[] { auctionId, marketId }, claimed.MailUid);
        var bag = _app.Services.GetRequiredService<ItemStore>().Load(role);
        Assert.Single(bag, i => i.Entry == 2010121 && i.BindType == 1);
        var exact = Assert.Single(bag, i => i.Attrs?.GetValueOrDefault(30) == 777);
        Assert.Equal("original", exact.StringAttrs![510]);
        Assert.True(exact.IsLocked);
        Assert.Equal(4_000_001_000L, roles.LoadProgress(role).Currency[1]);
        await alice.SendGameAsync(MailService.ClaimMail, new ClaimMailReq { MailUid = { auctionId, marketId } });
        Assert.Equal(ErrorCodes.MailAlreadyClaimed, (await Next(alice, MailService.ClaimMail, ClaimMailRsp.Parser)).Result);
        Assert.Equal(4_000_001_000L, roles.LoadProgress(role).Currency[1]);
        var persisted = JsonDocument.Parse(roles.LoadMailbox(role)!);
        var letters = persisted.RootElement.GetProperty("Letters").EnumerateArray().ToArray();
        Assert.Equal("auction-settlement-42", letters[0].GetProperty("DeliveryKey").GetString());
        Assert.Equal(JsonValueKind.String, letters[0].GetProperty("ContentParams").ValueKind);
        Assert.Equal(JsonValueKind.String, letters[1].GetProperty("ContentParams").ValueKind);
    }

    [Fact]
    public async Task BoundAttachmentCannotUseFreeSpaceInUnboundStackWhenBagIsFull()
    {
        var (alice, role) = await InWorld("BoundCapacity");
        await using var _ = alice;
        var data = _app.Services.GetRequiredService<GameData>();
        int capacity = (int)GameData.Int(data.Row("BackpackConfig", 1)!.Value, "_iTabCapacity");
        var loop = _app.Services.GetRequiredService<GameLoop>();
        var sessions = _app.Services.GetRequiredService<SessionRegistry>();
        var market = _app.Services.GetRequiredService<MarketStore>();
        Assert.Equal(0, await loop.InvokeAsync(async () =>
        {
            var owner = sessions.All().Single(s => s.Role?.Id == role);
            return await market.RunAsync(owner, tx =>
            {
                MarketFixtureEconomy.Edit(tx, tx.Role(role), participant =>
                {
                    var bag = participant.Items;
                    int existing = bag.Count(i => i.Grid == 0 && i.Pack == 1);
                    for (int n = existing; n < capacity; n++)
                        bag.Add(new InvItem { Entry = 1000000, Stack = 1, Pack = 1, Sort = n + 1 });
                });
                return 0;
            });
        }));
        string mailId = await _app.Services.GetRequiredService<MailService>().SendRawAsync(role,
            "Bound item", "Body", [new(1000000, 1, 1)]);
        await alice.SendGameAsync(MailService.ClaimMail, new ClaimMailReq { MailUid = { mailId } });
        Assert.Equal(ErrorCodes.BagFull, (await Next(alice, MailService.ClaimMail, ClaimMailRsp.Parser)).Result);
        Assert.Equal(false, Assert.IsType<Dictionary<object, object?>>(Row(await List(alice, role))["Params"])["giftReceived"]);
        Assert.DoesNotContain(_app.Services.GetRequiredService<ItemStore>().Load(role),
            i => i.Entry == 1000000 && i.BindType == 1);
    }

    [Fact]
    public async Task CombinedBoundAttachmentAndLongCurrencyOverflowLeavesMailUnclaimed()
    {
        var (alice, role) = await InWorld("MixedOverflow");
        await using var _ = alice;
        var session = _app.Services.GetRequiredService<SessionRegistry>().All().Single(s => s.Role?.Id == role);
        session.Progress!.Currency[1] = long.MaxValue - 1;
        const string id = "1790000000000000201";
        var box = new MailService.Mailbox
        {
            Letters =
        [
            new MailService.Letter
            {
                Id = id, CreatedTime = 1_780_000_000L,
                Attachments = [new(1000000, 1, 1), new(1, 1)],
                Currency = new() { [1] = 1 },
            },
        ]
        };
        var roles = _app.Services.GetRequiredService<RoleStore>();
        roles.SaveMailbox(role, JsonSerializer.SerializeToUtf8Bytes(box));
        await alice.SendGameAsync(MailService.ClaimMail, new ClaimMailReq { MailUid = { id } });
        var refused = await Next(alice, MailService.ClaimMail, ClaimMailRsp.Parser);
        Assert.Equal(ErrorCodes.Parameter, refused.Result);
        Assert.Empty(refused.MailUid);
        Assert.Equal(false, Assert.IsType<Dictionary<object, object?>>(Row(await List(alice, role))["Params"])["giftReceived"]);
        Assert.Equal(long.MaxValue - 1, session.Progress.Currency[1]);
        Assert.DoesNotContain(_app.Services.GetRequiredService<ItemStore>().Load(role),
            i => i.Entry == 1000000 && i.BindType == 1);
    }

    [Fact]
    public async Task ConcurrentRawTemplateAndMarketWritesDoNotOverwriteClaimOrRead()
    {
        var (alice, role) = await InWorld("Concurrent");
        await using var _ = alice;
        var mail = _app.Services.GetRequiredService<MailService>();
        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var raw = Task.Run(async () => { await start.Task; return await mail.SendRawAsync(role, "Raw", "Body", [new(1000000, 1)]); });
        var template = Task.Run(async () => { await start.Task; return await mail.SendTemplateAsync(role, 1024, [new(2, 3)]); });
        var market = Task.Run(async () =>
        {
            await start.Task; return await Deliver(role, 1064,
            [new InvItem { Entry = 2010121, Stack = 1, Attrs = new() { [30] = 91 } }]);
        });
        start.SetResult();
        await Task.WhenAll(raw, template, market);
        var initial = await List(alice, role);
        Assert.Equal(3, Assert.IsType<object?[]>(initial["MailList"]).Length);
        var read = Task.Run(async () => await Positional(alice, "setMailHaveRead", role.ToString(), await raw));
        var delivery = Task.Run(() => mail.SendRawAsync(role, "While claiming", "Body"));
        await alice.SendGameAsync(MailService.ClaimMail, new ClaimMailReq { MailUid = { await market } });
        var claimed = await Next(alice, MailService.ClaimMail, ClaimMailRsp.Parser);
        await Task.WhenAll(read, delivery);
        Assert.Equal(0, claimed.Result);
        Assert.Equal(await market, Assert.Single(claimed.MailUid));
        Assert.True(Ok(await read));
        var final = await List(alice, role);
        Assert.Equal(4, Assert.IsType<object?[]>(final["MailList"]).Length);
        Assert.Equal(true, Assert.IsType<Dictionary<object, object?>>(RowById(final, await raw)["Params"])["haveRead"]);
        Assert.Equal(true, Assert.IsType<Dictionary<object, object?>>(RowById(final, await market)["Params"])["giftReceived"]);
        Assert.NotNull(RowById(final, await template));
        Assert.NotNull(RowById(final, await delivery));
    }

    [Fact]
    public async Task LegacyRowsMigrateAtomicallyAndRemainIdempotentAcrossRestart()
    {
        var (alice, role) = await InWorld("Legacy");
        string raw = await _app.Services.GetRequiredService<MailService>().SendRawAsync(role, "Raw", "Body");
        const string pending = "1790000000000000001", claimed = "1790000000000000002";
        var fixtureAccounts = _app.Services.GetRequiredService<AccountStore>();
        foreach (var (id, received) in new[] { (pending, false), (claimed, true) })
        {
            FixtureSql.Execute(fixtureAccounts, "INSERT INTO market_record(kind,id,data) VALUES('mail',$id,$data)",
                ("$id", id), ("$data", JsonSerializer.Serialize(new
                {
                    Id = id, RoleId = role, TemplateId = 1064, CreatedTime = 1_780_000_000L,
                    RemoveTime = long.MaxValue, Read = received, Claimed = received,
                    Items = new[] { new InvItem { Entry = 2010121, Stack = 1, Attrs = new() { [30] = received ? 12 : 13 },
                        StringAttrs = new() { [510] = "legacy" }, IsLocked = true } },
                    Currency = new Dictionary<int, long> { [1] = 4_000_000_000L },
                    ContentParams = new[] { "Legacy item" },
                })));
        }
        FixtureSql.Execute(fixtureAccounts,
            $"CREATE TRIGGER fail_legacy_mailbox BEFORE UPDATE OF mailbox ON role WHEN NEW.id={role} BEGIN SELECT RAISE(ABORT, 'migration rollback'); END");
        Assert.Throws<Microsoft.Data.Sqlite.SqliteException>(() =>
            _app.Services.GetRequiredService<MarketStore>().MigrateLegacyMail());
        using (var db = _app.Services.GetRequiredService<AccountStore>().Open())
        {
            using var count = db.CreateCommand();
            count.CommandText = "SELECT COUNT(*) FROM market_record WHERE kind='mail'";
            Assert.Equal(2L, (long)count.ExecuteScalar()!);
        }
        FixtureSql.Execute(fixtureAccounts, "DROP TRIGGER fail_legacy_mailbox");
        Assert.Equal(raw, Row(await List(alice, role))["MailId"]);
        await alice.DisposeAsync();
        await RestartAsync();
        var (again, sameRole) = await InWorld("Legacy", false);
        Assert.Equal(role, sameRole);
        var list = await List(again, role);
        Assert.Equal(3, Assert.IsType<object?[]>(list["MailList"]).Length);
        Assert.Equal(false, Assert.IsType<Dictionary<object, object?>>(RowById(list, pending)["Params"])["giftReceived"]);
        Assert.Equal(true, Assert.IsType<Dictionary<object, object?>>(RowById(list, claimed)["Params"])["giftReceived"]);
        Assert.Equal(raw, RowById(list, raw)["MailId"]);
        using (var db = _app.Services.GetRequiredService<AccountStore>().Open())
        using (var count = db.CreateCommand())
        {
            count.CommandText = "SELECT COUNT(*) FROM market_record WHERE kind='mail'";
            Assert.Equal(0L, (long)count.ExecuteScalar()!);
        }
        await again.SendGameAsync(MailService.ClaimMail, new ClaimMailReq { MailUid = { claimed } });
        Assert.Equal(ErrorCodes.MailAlreadyClaimed, (await Next(again, MailService.ClaimMail, ClaimMailRsp.Parser)).Result);
        await again.SendGameAsync(MailService.ClaimMail, new ClaimMailReq { MailUid = { pending } });
        Assert.Equal(0, (await Next(again, MailService.ClaimMail, ClaimMailRsp.Parser)).Result);
        Assert.Equal(4_000_001_000L, _app.Services.GetRequiredService<RoleStore>().LoadProgress(role).Currency[1]);
        Assert.Single(_app.Services.GetRequiredService<ItemStore>().Load(role), i => i.Attrs?.GetValueOrDefault(30) == 13);
        await again.DisposeAsync();
        await RestartAsync();
        var (final, _) = await InWorld("Legacy", false);
        await using (final)
        {
            Assert.Equal(3, Assert.IsType<object?[]>((await List(final, role))["MailList"]).Length);
            Assert.Equal(4_000_001_000L, _app.Services.GetRequiredService<RoleStore>().LoadProgress(role).Currency[1]);
        }
    }
}

using Microsoft.AspNetCore.Builder;
using Romsg;
using Ro3Emu.Bot;
using Ro3Emu.Server;
using Ro3Emu.Server.Game;

namespace Ro3Emu.Tests;

/// <summary>
/// The chat typing state 83718 (named GUILD_VOICE_TYPEDOWN): the sender gets an ack, whoever sees the sender gets unit
/// custom data TextTypingState 5000013 [state, channel] (LC_CustomDataHandler_Chat.ChangeTypingState), once per change.
/// </summary>
public sealed class ChatTypingTests : IAsyncLifetime
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "ro3emu-chattyping-" + Guid.NewGuid().ToString("N"));
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(8);
    private const int TextTypingState = 5000013;
    private WebApplication _app = null!;
    private string _http = "";

    public async Task InitializeAsync()
    {
        var (web, micro, game) = TestPorts.FreePorts();
        _app = ServerHost.Create([], o =>
        {
            o.DataDir = _dir; o.GameDataDir = Path.Combine(RepoRoot(), "data");
            o.HttpPort = web; o.MicroGatePort = micro; o.GameGatePort = game;
        });
        await _app.StartAsync();
        _http = $"http://127.0.0.1:{web}";
    }

    public async Task DisposeAsync()
    {
        await _app.StopAsync(); await _app.DisposeAsync();
        using var db = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={Path.Combine(_dir, "ro3emu.db")}");
        Microsoft.Data.Sqlite.SqliteConnection.ClearPool(db);
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }

    private static string RepoRoot()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d != null && !File.Exists(Path.Combine(d.FullName, "Ro3Emu.slnx"))) d = d.Parent;
        return d?.FullName ?? throw new InvalidOperationException("repo root not found");
    }

    private async Task<(BotClient Bot, long Id)> InWorld(string name)
    {
        var bot = new BotClient(_http, File.ReadAllText(Path.Combine(_dir, "gate_rsa_public.pem")));
        await bot.LoginAsync(name.ToLowerInvariant(), "pw"); await bot.EnterGateAsync();
        long id = (await bot.CreateRoleAsync(name)).RoleBaseInfo.Id;
        await bot.EnterWorldAsync(id);
        return (bot, id);
    }

    private static async Task<int> TypeDown(BotClient bot, int state, int channel)
    {
        using var reply = bot.Inbox.Expect(e => e.IsGame(WorldCoreService.TypeDown));
        await bot.SendGameAsync(WorldCoreService.TypeDown, new GuildVoiceTypeDownMessageReq { Type = state, ChannelId = channel });
        return GuildVoiceTypeDownMessageResp.Parser.ParseFrom((await reply.WaitAsync(Wait)).Body).ErrorCode;
    }

    /// <summary>The next TextTypingState [state, channel] of <paramref name="owner"/> that <paramref name="viewer"/> receives.</summary>
    private static async Task<(long State, long Channel)> Seen(BotClient viewer, long owner, TimeSpan? wait = null)
    {
        var e = await viewer.Inbox.WaitAsync(e => e.IsGame(WorldCoreService.CustomChange)
            && CustomDataChange.Parser.ParseFrom(e.Body) is var c && c.EntityId == owner && c.CustomDatas.Any(d => d.Id == TextTypingState),
            wait ?? Wait);
        var value = CustomDataChange.Parser.ParseFrom(e.Body).CustomDatas.Single(d => d.Id == TextTypingState).CustomParam.ListLongParam;
        Assert.Equal(2, value.Count);
        return (value[0], value[1]);
    }

    [ClientDataFact]
    public async Task ViewersSeeTypingStartAndEndOncePerChange()
    {
        var (alice, aliceId) = await InWorld("Alice");
        var (bob, _) = await InWorld("Bob");
        await using var a = alice;
        await using var b = bob;
        await bob.Inbox.WaitAsync(e => e.IsGame(31037), Wait); // bob sees alice (same born point)

        Assert.Equal(0, await TypeDown(alice, 0, 1));
        Assert.Equal((0L, 1L), await Seen(bob, aliceId));
        // The same state again changes nothing.
        Assert.Equal(0, await TypeDown(alice, 0, 1));
        await Assert.ThrowsAsync<TimeoutException>(() => Seen(bob, aliceId, TimeSpan.FromSeconds(1)));

        Assert.Equal(0, await TypeDown(alice, 1, 1));
        Assert.Equal((1L, 1L), await Seen(bob, aliceId));
        Assert.Equal(0, await TypeDown(alice, 1, 1));
        await Assert.ThrowsAsync<TimeoutException>(() => Seen(bob, aliceId, TimeSpan.FromSeconds(1)));

        // Only the two states and the chat settings' channels (one per ChatChannelConfig row).
        Assert.Equal(ErrorCodes.Parameter, await TypeDown(alice, 2, 1));
        Assert.Equal(ErrorCodes.Parameter, await TypeDown(alice, 0, 0));
        Assert.Equal(ErrorCodes.Parameter, await TypeDown(alice, 0, 12));
    }

    [ClientDataFact]
    public async Task TypingStartsAreLimitedButTheEndAlwaysGoesOut()
    {
        var (alice, aliceId) = await InWorld("Alice");
        var (bob, _) = await InWorld("Bob");
        await using var a = alice;
        await using var b = bob;
        await bob.Inbox.WaitAsync(e => e.IsGame(31037), Wait);

        // Ten starts in the window reach the viewer (switching channels makes each one a change), the eleventh does not.
        for (int i = 0; i < 10; i++)
        {
            Assert.Equal(0, await TypeDown(alice, 0, 1 + i % 2));
            Assert.Equal((0L, (long)(1 + i % 2)), await Seen(bob, aliceId));
        }
        Assert.Equal(0, await TypeDown(alice, 0, 1));
        await Assert.ThrowsAsync<TimeoutException>(() => Seen(bob, aliceId, TimeSpan.FromSeconds(1)));
        Assert.Equal(0, await TypeDown(alice, 1, 2));
        Assert.Equal((1L, 2L), await Seen(bob, aliceId));
    }
}

using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Ro3Emu.Bot;
using Ro3Emu.Server;

namespace Ro3Emu.Tests;

/// <summary>POST /ro3emu/shutdown (StopApplication) must stop the host in bounded time while a client is in the world.</summary>
public sealed class ShutdownWithClientTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task StopApplicationStopsTheHostWhileAClientIsInTheWorld(bool busy, bool halfRequest)
    {
        string dataDir = Path.Combine(Path.GetTempPath(), "ro3emu-shutdown-" + Guid.NewGuid().ToString("N"));
        var (httpPort, microPort, gamePort) = TestPorts.FreePorts();
        var app = ServerHost.Create([], o =>
        {
            o.DataDir = dataDir;
            o.GameDataDir = SyntheticData.Dir;
            o.HttpPort = httpPort;
            o.MicroGatePort = microPort;
            o.GameGatePort = gamePort;
        });
        using var cts = new CancellationTokenSource();
        try
        {
            await app.StartAsync();
            await using var player = new BotClient($"http://127.0.0.1:{httpPort}", File.ReadAllText(Path.Combine(dataDir, "gate_rsa_public.pem")));
            await player.LoginAsync("player", "pw");
            await player.EnterGateAsync();
            await player.EnterWorldAsync((await player.CreateRoleAsync("Player")).RoleBaseInfo.Id);
            using var half = new System.Net.Sockets.TcpClient();
            if (halfRequest)
            {
                await half.ConnectAsync("127.0.0.1", httpPort);
                await half.GetStream().WriteAsync("POST /ro3emu/status HTTP/1.1\r\nHost: x\r\nContent-Length: 100\r\n\r\nab"u8.ToArray());
            }
            var chatter = Task.Run(async () =>
            {
                try { while (busy && !cts.IsCancellationRequested) { await player.HeartbeatAsync(); await player.SendChatAsync("", 1, "hello"); } }
                catch { }
            });
            await Task.Delay(300);

            var stopped = app.WaitForShutdownAsync();
            var watch = System.Diagnostics.Stopwatch.StartNew();
            app.Services.GetRequiredService<IHostApplicationLifetime>().StopApplication();
            var done = await Task.WhenAny(stopped, Task.Delay(TimeSpan.FromSeconds(15)));
            cts.Cancel();
            await chatter;
            Assert.True(done == stopped, $"host still stopping after {watch.Elapsed.TotalSeconds:F1} s with a client connected");
            await stopped; // propagate shutdown failures instead of treating any completed task as success
        }
        finally
        {
            cts.Cancel();
            await app.DisposeAsync();
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            try { Directory.Delete(dataDir, true); } catch (IOException) { }
        }
    }
}

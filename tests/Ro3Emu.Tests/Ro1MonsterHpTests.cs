using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Ro3Emu.Server;
using Ro3Emu.Server.Game;

namespace Ro3Emu.Tests;

/// <summary>The RO1 HP reference (Reconstructed/ro1-monster-hp.json, switch Ro3Emu:Ro1MonsterHp): the Pupa.</summary>
public sealed class Ro1MonsterHpTests
{
    private const int Pupa = 5000035, PecoPecoEgg = 5000033, AntEgg = 5000085, Fabre = 5000003;

    [Fact]
    public void ThePupaIsListedWithItsRo1Hp()
    {
        var pupa = MonsterService.LoadRo1MonsterHp()[Pupa];
        Assert.Equal((1008, 427, 427), (pupa.Ro1Id, pupa.Ro1Hp, pupa.Hp)); // rAthena pre-re mob_db: Pupa, Hp 427, used as it is
    }

    [Fact]
    public void TheEggsThatDieInOneHitAreListedWithTheirRo1Hp()
    {
        var all = MonsterService.LoadRo1MonsterHp();
        Assert.Equal((1047, 420, 420), (all[PecoPecoEgg].Ro1Id, all[PecoPecoEgg].Ro1Hp, all[PecoPecoEgg].Hp)); // mob_db: Peco Peco Egg
        Assert.Equal((1097, 420, 420), (all[AntEgg].Ro1Id, all[AntEgg].Ro1Hp, all[AntEgg].Hp)); // mob_db: Ant Egg
        Assert.All(all.Values, e => Assert.Equal(e.Ro1Hp, e.Hp));
    }

    private static async Task<(long Pupa, long PecoPecoEgg, long AntEgg, long Fabre)> HpsAsync(bool ro1Hp)
    {
        string dataDir = Path.Combine(Path.GetTempPath(), "ro3emu-ro1hp-" + Guid.NewGuid().ToString("N"));
        var (httpPort, microPort, gamePort) = TestPorts.FreePorts();
        var app = ServerHost.Create([], o =>
        {
            o.DataDir = dataDir;
            o.GameDataDir = Path.Combine(RepoRoot(), "data");
            o.HttpPort = httpPort; o.MicroGatePort = microPort; o.GameGatePort = gamePort;
            o.Ro1MonsterHp = ro1Hp;
        });
        await app.StartAsync();
        try
        {
            var monsters = app.Services.GetRequiredService<MonsterService>();
            return (monsters.Template(Pupa)!.MaxHp, monsters.Template(PecoPecoEgg)!.MaxHp, monsters.Template(AntEgg)!.MaxHp, monsters.Template(Fabre)!.MaxHp);
        }
        finally
        {
            await app.StopAsync();
            await app.DisposeAsync();
            SqliteConnection.ClearAllPools();
            try { Directory.Delete(dataDir, true); } catch (IOException) { }
        }
    }

    [ClientDataFact]
    public async Task WithTheSwitchOnThePupaAndTheEggsHaveTheirRo1HpAndOtherMonstersKeepTheClients()
    {
        var on = await HpsAsync(true);
        Assert.Equal((427, 420, 420), (on.Pupa, on.PecoPecoEgg, on.AntEgg));
        Assert.Equal((await HpsAsync(false)).Fabre, on.Fabre); // a monster that is not listed
    }

    [ClientDataFact]
    public async Task WithTheSwitchOffTheClientsHpStays() => Assert.Equal((4L, 45L, 92L), await HpsAsync(false) is var off ? (off.Pupa, off.PecoPecoEgg, off.AntEgg) : default);

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "Ro3Emu.slnx"))) dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("repo root not found");
    }
}

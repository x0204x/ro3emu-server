using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Ro3Emu.Server.Game;

namespace Ro3Emu.Tests;

/// <summary>合成資料：普通增益必須實際改變施法排程所讀取的詠唱時間，並遵守移除及到期。</summary>
public sealed class ChantBuffTests
{
    private const int Buff = 99080;
    private static readonly JsonElement Chant = JsonSerializer.SerializeToElement(new { _iChantFix = 500, _iChantVar = 3000 });

    [Theory]
    [InlineData(AttributeId.VarChant, -600, 2900)]
    [InlineData(AttributeId.PerVarChant, -2000, 2900)]
    [InlineData(AttributeId.FixChant, -200, 3300)]
    [InlineData(AttributeId.PerFixChant, -4000, 3300)]
    [InlineData(AttributeId.CastSpeed, 500, 2500)]
    public async Task OrdinaryBuffChangesChantAndRemovalRestoresIt(int attribute, int value, int expected)
    {
        await using var f = new Fixture(attribute, value);
        await f.Start();
        await using var bot = await f.Host.InWorldAsync("Chanter");
        await f.Loop.InvokeAsync(async () =>
        {
            Assert.Equal(3500, f.ChantMs());
            await f.Buffs.AddAsync(f.Session, Buff, 5000, f.Session.Role!.Id);
            Assert.Equal(expected, f.ChantMs());
            await f.Buffs.RemoveAsync(f.Session, Assert.Single(f.Session.Buffs, b => b.ConfigId == Buff));
            Assert.Equal(3500, f.ChantMs());
        });
    }

    [Fact]
    public async Task ExpiredBuffIsIgnoredBeforeCleanupButSourceOwnedPassiveRemains()
    {
        await using var f = new Fixture(AttributeId.PerVarChant, -2500);
        await f.Start();
        await using var bot = await f.Host.InWorldAsync("Chanter");
        await f.Loop.InvokeAsync(async () =>
        {
            await f.Buffs.AddAsync(f.Session, Buff, 5000, f.Session.Role!.Id);
            var timed = Assert.Single(f.Session.Buffs, b => b.ConfigId == Buff);
            Assert.Equal(2750, f.ChantMs());
            f.Session.Buffs.Remove(timed);
            var expired = timed with { ExpireMs = MonsterService.NowMs - 1 };
            f.Session.Buffs.Add(expired);
            Assert.Equal(3500, f.ChantMs());
            Assert.Contains(expired, f.Session.Buffs);
            f.Session.Buffs.Remove(expired);
            await f.Buffs.AddPassiveAsync(f.Session, Buff, 987654, totemOwned: false);
            Assert.Equal(2750, f.ChantMs());
            var passive = Assert.Single(f.Session.Buffs, b => b.ConfigId == Buff);
            Assert.True(passive.IsPassive);
            Assert.Equal(long.MaxValue, passive.ExpireMs);
            await f.Buffs.RemoveAsync(f.Session, passive);
            Assert.Equal(3500, f.ChantMs());
        });
    }

    private sealed class Fixture(int attribute, int value) : IAsyncDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "ro3emu-chant-" + Guid.NewGuid().ToString("N"));
        public SyntheticHost Host { get; private set; } = null!;
        public GameSession Session => Host.Service<SessionRegistry>().All().Single(s => s.Role != null);
        public GameLoop Loop => Host.Service<GameLoop>();
        public BuffService Buffs => Host.Service<BuffService>();
        public int ChantMs() => Host.Service<CombatService>().ChantMs(Session, Chant);

        public async Task Start()
        {
            foreach (string file in Directory.EnumerateFiles(SyntheticData.Dir, "*", SearchOption.AllDirectories))
            {
                string dest = Path.Combine(_root, Path.GetRelativePath(SyntheticData.Dir, file));
                Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                File.Copy(file, dest);
            }
            var parameters = JsonNode.Parse(File.ReadAllText(Path.Combine(_root, "config", "PublicParamConfig.json")))?.AsObject()
                ?? throw new InvalidOperationException("Missing synthetic parameters.");
            parameters["VariableChantPerParamA"] = 1;
            parameters["VariableChantPerParamB"] = 1000;
            parameters["VariableChantPerParamC"] = 1;
            File.WriteAllText(Path.Combine(_root, "config", "PublicParamConfig.json"), parameters.ToJsonString());
            var buffs = new Dictionary<string, object>
            {
                [Buff.ToString(CultureInfo.InvariantCulture)] = new
                {
                    _iID = Buff, _iBuffID = 9908, _iType = 1, _iTime = 5000,
                    _kChangeAttri = new[] { new[] { attribute, value } },
                },
            };
            File.WriteAllText(Path.Combine(_root, "config", "BuffConfig.json"), JsonSerializer.Serialize(buffs));
            Host = await SyntheticHost.StartAsync(o => o.GameDataDir = _root);
        }

        public async ValueTask DisposeAsync()
        {
            if (Host != null) await Host.DisposeAsync();
            if (Directory.Exists(_root)) Directory.Delete(_root, true);
        }
    }
}

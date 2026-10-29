using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Romsg;
using Ro3Emu.Bot;
using Ro3Emu.Server.Game;
using Ro3Emu.Server.Maps;

namespace Ro3Emu.Tests;

/// <summary>
/// AIConfig _iIsSurround / _iSurroundDistance (MonsterService.Surround) on invented fixture rows: the attackers of a
/// player stand on slots around it instead of one spot, a slot the nav grid blocks falls back to the straight approach,
/// and a row without surround keeps the straight approach.
/// </summary>
public sealed class MonsterSurroundTests
{
    private const int Surrounding = 16329001, Straight = 16329002;
    private const float Reach = 10f, Radius = Reach * 0.8f, Spacing = 4f, Away = 14f;
    private const int SpacingCm = (int)(Spacing * 100), LeashCm = 5000;
    private const float PlayerX = 150f, PlayerZ = 186f;
    private const int MaxHpAttribute = 101, HpAttribute = 2000;
    private const long LastingHp = 1_000_000;

    /// <summary>12 slots: the ring of 8 m holds floor(2π·8 / 4) of them, 30° apart.</summary>
    private const int Slots = 12;

    [Fact]
    public async Task AttackersOfOnePlayerSpreadOverItsRingWithinReachAndFollowIt()
    {
        await using var f = await Fixture.StartAsync();
        var monsters = new List<MonsterUnit>();
        foreach (float bearing in new[] { 5f, 17f, 25f })
            monsters.Add(await f.SpawnAsync(Surrounding, bearing));
        await f.EngageAsync(monsters);
        await f.SettleAsync(monsters, m => AtAnySlot(f, m));
        var placed = await f.Read(() => monsters.Select(m => (m.X, m.Z)).ToArray());
        Assert.Equal(3, placed.Select(p => SlotOf(p.X - PlayerX, p.Z - PlayerZ)).Distinct().Count());
        for (int i = 0; i < placed.Length; i++)
        {
            Assert.InRange(Distance(placed[i].X - PlayerX, placed[i].Z - PlayerZ), Radius - 0.3f, Reach + 0.5f);
            for (int j = i + 1; j < placed.Length; j++)
            {
                float apart = Distance(placed[i].X - placed[j].X, placed[i].Z - placed[j].Z);
                Assert.True(apart >= Spacing - 0.2f, $"{placed[i]} {placed[j]}");
            }
        }
        foreach (var m in monsters) await f.HitAsync(m);

        // The slots move with the player: each attacker walks to the same slot around the new position.
        var slots = placed.Select(p => SlotOf(p.X - PlayerX, p.Z - PlayerZ)).ToArray();
        await f.Loop.InvokeAsync(() => { f.Session.Position.X += 3; return Task.CompletedTask; });
        await f.SettleAsync(monsters, m => Near(m, SlotPoint(PlayerX + 3, PlayerZ, slots[monsters.IndexOf(m)])));
    }

    [Fact]
    public async Task EachTakesTheFreeSlotNearestItsBearingAndAFullRingIsShared()
    {
        // A spacing of 20 m leaves two slots on the 8 m ring: 0° and 180°.
        await using var f = await Fixture.StartAsync();
        var first = await f.SpawnAsync(Surrounding, 10, spacing: 20);
        var second = await f.SpawnAsync(Surrounding, 20, spacing: 20);
        var third = await f.SpawnAsync(Surrounding, 30, spacing: 20);
        foreach (var m in new[] { first, second, third })
        {
            await f.EngageAsync([m]);
            await f.Until(() => m.Moving);
        }
        await f.SettleAsync([first, third], m => Near(m, SlotPoint(PlayerX, PlayerZ, 0, 2)));
        await f.SettleAsync([second], m => Near(m, SlotPoint(PlayerX, PlayerZ, 1, 2)));
        await f.HitAsync(second);
    }

    [Theory]
    [InlineData(156, 160)] // the slot's own cell
    [InlineData(154, 155)] // a wall between the player and the slot; the slot itself and the way to it are open
    public async Task ASlotTheNavGridBlocksFallsBackToTheStraightApproach(int holeFromX, int holeToX)
    {
        // Bearing 14°: slot 0 (0°, at x 158) is nearest; the straight approach stays north of the hole (z 184-186).
        await using var f = await Fixture.StartAsync(blocked: (holeFromX, holeToX, 184, 186));
        var m = await f.SpawnAsync(Surrounding, 14);
        await f.EngageAsync([m]);
        await f.SettleAsync([m], u => OnStraightLine(u, 14));
        await f.HitAsync(m);
    }

    [Fact]
    public async Task ARowWithoutSurroundKeepsTheStraightApproach()
    {
        await using var f = await Fixture.StartAsync();
        var m = await f.SpawnAsync(Straight, 14);
        await f.EngageAsync([m]);
        await f.SettleAsync([m], u => OnStraightLine(u, 14));
        await f.HitAsync(m);
    }

    [Theory]
    [InlineData(2f, 200, 1.5f, 3f, 5f)]
    [InlineData(3f, 400, 1.5f, 3f, 5f)]
    [InlineData(10f, 300, 2.5f, 5f, 14f)]
    public async Task ASurroundingMonsterSwingsAsOftenAsAStraightOneAtAPlayerStrafingInReach(float reach, int speedCm,
        float strafe, float pace, float away)
    {
        // 1 m spacing; the player strafes ±strafe m at pace m/s for 8 s (swing interval 2 s).
        await using var f = await Fixture.StartAsync();
        MonsterTemplate Tuned(MonsterTemplate t) => t with { AttackRange = reach, SpeedCm = speedCm };
        var surrounding = await f.SpawnAsync(Surrounding, 90, spacing: 1, away: away, tune: Tuned);
        var straight = await f.SpawnAsync(Straight, 90, away: away, tune: Tuned);
        var swings = new Dictionary<MonsterUnit, int> { [surrounding] = 0, [straight] = 0 };
        await f.Loop.InvokeAsync(() =>
        {
            var monsters = f.Monsters;
            var attack = monsters.Attack!;
            monsters.Attack = (m, p) =>
            {
                if (swings.TryGetValue(m, out int n)) swings[m] = n + 1;
                return attack(m, p);
            };
            return Task.CompletedTask;
        });
        await f.EngageAsync([surrounding, straight]);
        float phase = 0;
        for (var end = DateTime.UtcNow.AddSeconds(8); DateTime.UtcNow < end;)
        {
            await Task.Delay(50);
            phase += pace * 0.05f;
            float x = PlayerX + strafe * MathF.Sin(phase / strafe);
            await f.Loop.InvokeAsync(() => { f.Session.Position.X = x; return Task.CompletedTask; });
        }
        var (around, line) = await f.Read(() => (swings[surrounding], swings[straight]));
        Assert.True(line >= 3, $"straight {line}");
        Assert.True(Math.Abs(around - line) <= 1, $"surrounding {around}, straight {line}");
    }

    /// <summary>
    /// Today's approach: on the line toward the monster's start, stopped at its first tick in reach (attack range
    /// + 0.5 m) or at the end of its walk, 0.8 of the attack range from the player.
    /// </summary>
    private static bool OnStraightLine(MonsterUnit m, float bearing)
    {
        float dx = m.X - PlayerX, dz = m.Z - PlayerZ;
        float off = MathF.Abs(MathF.Atan2(dz, dx) * 180 / MathF.PI - bearing);
        return !m.Moving && off < 1 && Distance(dx, dz) is >= Radius - 0.1f and <= Reach + 0.5f;
    }

    private static (float X, float Z) SlotPoint(float x, float z, int slot, int slots = Slots)
    {
        float a = 2 * MathF.PI * slot / slots;
        return (x + Radius * MathF.Cos(a), z + Radius * MathF.Sin(a));
    }

    private static int SlotOf(float dx, float dz)
    {
        float turns = MathF.Atan2(dz, dx) / (2 * MathF.PI) * Slots;
        return ((int)MathF.Round(turns) % Slots + Slots) % Slots;
    }

    private static bool AtAnySlot(Fixture f, MonsterUnit m)
    {
        var p = f.Session.Position;
        return Near(m, SlotPoint(p.X, p.Z, SlotOf(m.X - p.X, m.Z - p.Z)));
    }

    /// <summary>
    /// Standing at <paramref name="at"/> as the server counts it: a monster that is MinChaseStep or less from its slot
    /// does not walk again (a swing can stop a walk there), plus a little float slack.
    /// </summary>
    private static bool Near(MonsterUnit m, (float X, float Z) at, float within = MonsterService.MinChaseStep + 0.05f) =>
        !m.Moving && Distance(m.X - at.X, m.Z - at.Z) <= within;

    private static float Distance(float dx, float dz) => MathF.Sqrt(dx * dx + dz * dz);

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly string _root =
            Path.Combine(Path.GetTempPath(), "ro3emu-surround-" + Guid.NewGuid().ToString("N"));
        private SyntheticHost _host = null!;
        private BotClient _bot = null!;

        public GameSession Session => _host.Service<SessionRegistry>().All().Single(s => s.Role != null);
        public GameLoop Loop => _host.Service<GameLoop>();
        public MonsterService Monsters => _host.Service<MonsterService>();

        public static async Task<Fixture> StartAsync((int X0, int X1, int Z0, int Z1)? blocked = null)
        {
            var f = new Fixture();
            f.CopyAndWriteData(blocked);
            f._host = await SyntheticHost.StartAsync(o => o.GameDataDir = f._root);
            f._bot = await f._host.InWorldAsync("Surrounded");
            await f.Loop.InvokeAsync(async () =>
            {
                f.Session.Position.X = PlayerX;
                f.Session.Position.Y = 0;
                f.Session.Position.Z = PlayerZ;
                // The fixture role has no HP (no attribute rows) and would fall at the first hit; the fight must last.
                var character = f._host.Service<CharacterService>();
                character.DynamicProperties.Add(_ => [(MaxHpAttribute, LastingHp), (HpAttribute, LastingHp)]);
                await character.RefreshStatsAsync(f.Session);
            });
            return f;
        }

        /// <summary>Two invented monsters (AI rows with and without surround), and a nav grid with a hole when
        /// asked.</summary>
        private void CopyAndWriteData((int X0, int X1, int Z0, int Z1)? blocked)
        {
            foreach (string file in Directory.EnumerateFiles(SyntheticData.Dir, "*", SearchOption.AllDirectories))
            {
                string dest = Path.Combine(_root, Path.GetRelativePath(SyntheticData.Dir, file));
                Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                File.Copy(file, dest);
            }
            var npcs = JsonNode.Parse(File.ReadAllText(Path.Combine(_root, "config", "NPCConfig.json")))!.AsObject();
            foreach (var (id, ai) in new[] { (Surrounding, 1990001), (Straight, 1990002) })
                npcs[Key(id)] = JsonSerializer.SerializeToNode(new
                {
                    _iID = id, _iNPCType = 1, _iLevel = 50, _iAttribute = 900001, _iCamp = 2, _iSpeed = 800, _iAI = ai,
                });
            File.WriteAllText(Path.Combine(_root, "config", "NPCConfig.json"), npcs.ToJsonString());
            Write("AIConfig", new Dictionary<string, object>
            {
                ["1990001"] = new
                {
                    _iID = 1990001, _iIsSurround = 1, _iSurroundDistance = SpacingCm, _iChaseDistance = LeashCm,
                },
                ["1990002"] = new
                {
                    _iID = 1990002, _iIsSurround = 0, _iSurroundDistance = SpacingCm, _iChaseDistance = LeashCm,
                },
            });
            if (blocked is not { } hole) return;
            Write("MapConfig", new Dictionary<string, object>
            {
                [Key(SyntheticData.Town)] = new
                {
                    _iID = SyntheticData.Town, _kResSceneEditorName = "synthetic_town", _iMapResourceID = 1990001,
                },
            });
            Write("MapResource", new Dictionary<string, object>
            {
                ["1990001"] = new { _iID = 1990001, _kNavDataName = "surround_dynamicmapdata.bytes" },
            });
            Directory.CreateDirectory(Path.Combine(_root, "nav"));
            File.WriteAllBytes(Path.Combine(_root, "nav", "surround_dynamicmapdata.bytes"), NavGrid(hole));
        }

        /// <summary>A flat 300 m square at height 0 but for one hole, in NavGrid's layout (30-cell blocks, no extra
        /// layers).</summary>
        private static byte[] NavGrid((int X0, int X1, int Z0, int Z1) hole)
        {
            const int Size = 300, Block = 30, Blocks = Size / Block, Header = 48, Table = Blocks * Blocks * 16;
            var data = new byte[Header + Table + Size * Size * 4];
            BitConverter.GetBytes(Size).CopyTo(data, 20);
            BitConverter.GetBytes(Size).CopyTo(data, 24);
            BitConverter.GetBytes(Block).CopyTo(data, 28);
            for (int b = 0; b < Blocks * Blocks; b++)
                BitConverter.GetBytes(Header + Table + b * Block * Block * 4).CopyTo(data, Header + b * 16);
            for (int x = 0; x < Size; x++)
                for (int z = 0; z < Size; z++)
                {
                    int block = z / Block * Blocks + x / Block;
                    int cell = Header + Table + block * Block * Block * 4 + (z % Block * Block + x % Block) * 4;
                    bool open = x < hole.X0 || x > hole.X1 || z < hole.Z0 || z > hole.Z1;
                    data[cell] = open ? (byte)0x80 : (byte)0;
                }
            return data;
        }

        private void Write(string table, object rows) =>
            File.WriteAllText(Path.Combine(_root, "config", table + ".json"), JsonSerializer.Serialize(rows));

        private static string Key(long id) => id.ToString(CultureInfo.InvariantCulture);

        /// <summary>A monster <paramref name="away"/> m (default <see cref="Away"/>) from the player at
        /// <paramref name="bearing"/>°, reaching <see cref="Reach"/> unless <paramref name="tune"/> says otherwise.</summary>
        public Task<MonsterUnit> SpawnAsync(int npcId, float bearing, float? spacing = null, float away = Away,
            Func<MonsterTemplate, MonsterTemplate>? tune = null) =>
            Loop.InvokeAsync(async () =>
            {
                var template = Monsters.Template(npcId) ?? throw new InvalidOperationException("No fixture monster.");
                float a = bearing * MathF.PI / 180;
                var at = new MapPoint(PlayerX + away * MathF.Cos(a), 0, PlayerZ + away * MathF.Sin(a), 0);
                var tuned = template with
                {
                    AttackRange = Reach,
                    SurroundDistance = spacing ?? template.SurroundDistance,
                };
                tuned = tune?.Invoke(tuned) ?? tuned;
                return await Monsters.SpawnSingleAsync(Session.SceneEntry, npcId, at, Session.InstanceId,
                        customTemplate: tuned)
                    ?? throw new InvalidOperationException("Fixture monster could not spawn.");
            });

        public Task EngageAsync(IEnumerable<MonsterUnit> monsters) => Loop.InvokeAsync(() =>
        {
            foreach (var m in monsters)
            {
                Monsters.Engage(m, Session);
                Assert.Same(Session, m.Target);
            }
            return Task.CompletedTask;
        });

        public Task<T> Read<T>(Func<T> read) => Loop.InvokeAsync(() => Task.FromResult(read()));

        public async Task Until(Func<bool> done, int seconds = 10)
        {
            var end = DateTime.UtcNow.AddSeconds(seconds);
            while (!await Read(done))
            {
                if (DateTime.UtcNow > end) return;
                await Task.Delay(50);
            }
        }

        /// <summary>Waits until every monster stands where <paramref name="placed"/> wants, then asserts so.</summary>
        public async Task SettleAsync(IReadOnlyList<MonsterUnit> monsters, Func<MonsterUnit, bool> placed)
        {
            await Until(() => monsters.All(placed));
            var where = await Read(() => string.Join(", ", monsters.Select(m =>
                $"({m.X:0.00}, {m.Z:0.00}{(m.Moving ? " moving" : "")}{(m.Target == null ? " idle" : "")})")));
            Assert.True(await Read(() => monsters.All(placed)), where);
        }

        /// <summary>The monster's normal attack lands on the player (31101 from it).</summary>
        public Task<BotEvent> HitAsync(MonsterUnit m) => _bot.Inbox.WaitAsync(e => e.IsGame(CombatService.SkillHit)
            && SkillHitInfo.Parser.ParseFrom(e.Body).SrcID == m.Uid, TimeSpan.FromSeconds(10));

        public async ValueTask DisposeAsync()
        {
            await _bot.DisposeAsync();
            await _host.DisposeAsync();
            Directory.Delete(_root, true);
        }
    }
}

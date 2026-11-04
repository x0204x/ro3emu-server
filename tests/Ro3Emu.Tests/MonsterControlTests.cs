using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Romsg;
using Ro3Emu.Bot;
using Ro3Emu.Server.Game;
using Ro3Emu.Server.Maps;

namespace Ro3Emu.Tests;

/// <summary>
/// Control states from any source on invented fixture rows: a player's Stun or Frozen on a monster carries its
/// SpecialStatusConfig state, the monster's AI obeys the forbidden actions until the buff ends, a Frozen breaks on a hit
/// into its follow-up, and the same groups on a player resolve to the same states.
/// </summary>
public sealed class MonsterControlTests
{
    private const int Npc = 16329101, Ai = 1990101;
    private const int Stun = 901601, Frozen = 902001, FrozenAfter = 902601, Petrify = 902101, Root = 902401,
        KnockUp = 901801, GenericFreeze = 1200301;
    private const int StunState = 18, FrozenState = 11, PetrifyState = 12, RootState = 21, KnockUpState = 3;
    private const int ControlTag = 37, NegativeType = 2;
    private const float Reach = 3f, Away = 12f;
    private const float PlayerX = 150f, PlayerZ = 186f;
    private const int MaxHpAttribute = 101, HpAttribute = 2000;
    private const long LastingHp = 1_000_000;

    // Invented rows shaped like the client's: Stun/Frozen/Petrify/Knock-Up forbid walking and the normal attack but
    // not casting, Root forbids walking only.
    private static readonly int[] HeldForbids = [ForbiddenState.Walk, ForbiddenState.NormalAttack, ForbiddenState.UseItem];
    private static readonly int[] RootForbids = [ForbiddenState.Walk];

    [Theory]
    [InlineData(Stun, StunState)]
    [InlineData(Frozen, FrozenState)]
    [InlineData(Petrify, PetrifyState)]
    [InlineData(Root, RootState)]
    [InlineData(KnockUp, KnockUpState)]
    public async Task APlayersControlOnAMonsterCarriesItsState(int buff, int state)
    {
        await using var f = await Fixture.StartAsync();
        var monster = await f.SpawnAsync(Away);
        await f.Loop.InvokeAsync(async () =>
        {
            await f.Buffs.AddSupportAsync(monster, buff, 5000, f.Session.Role!.Id);
            Assert.Equal(1UL << (state - 1), f.Buffs.UnitStatus(monster));
            Assert.True(f.Buffs.UnitForbidden(monster, ForbiddenState.Walk));
            Assert.Equal(state != RootState, f.Buffs.UnitForbidden(monster, ForbiddenState.NormalAttack));
            Assert.False(f.Buffs.UnitForbidden(monster, ForbiddenState.CastSkill)); // the rows do not forbid it
        });
    }

    [Theory]
    [InlineData(Frozen, FrozenState)]
    [InlineData(Petrify, PetrifyState)]
    [InlineData(Root, RootState)]
    [InlineData(KnockUp, KnockUpState)]
    public async Task AMonstersControlOnAPlayerCarriesItsState(int buff, int state)
    {
        await using var f = await Fixture.StartAsync();
        var monster = await f.SpawnAsync(Away);
        await f.Loop.InvokeAsync(async () =>
        {
            await f.Buffs.AddAsync(f.Session, buff, 5000, monster.Uid);
            Assert.Equal(1L << (state - 1), f.Session.StatusBits & (1L << (state - 1)));
            Assert.True(f.Buffs.HasForbidden(f.Session, ForbiddenState.Walk));
            await f.Buffs.RemoveAsync(f.Session, f.Session.Buffs.Single(b => b.ConfigId == buff));
            Assert.False(f.Buffs.HasForbidden(f.Session, ForbiddenState.Walk));
        });
    }

    [Fact]
    public async Task TheUnusedGenericFreezeGroupCarriesNoState()
    {
        await using var f = await Fixture.StartAsync();
        var monster = await f.SpawnAsync(Away);
        await f.Loop.InvokeAsync(async () =>
        {
            await f.Buffs.AddAsync(f.Session, GenericFreeze, 5000, monster.Uid);
            Assert.False(f.Buffs.HasForbidden(f.Session, ForbiddenState.Walk));
            await f.Buffs.AddSupportAsync(monster, GenericFreeze, 5000, f.Session.Role!.Id);
            Assert.Equal(0UL, f.Buffs.UnitStatus(monster));
        });
    }

    [Fact]
    public async Task AStunnedMonsterNeitherChasesNorAttacksAndResumesWhenTheStunEnds()
    {
        await using var f = await Fixture.StartAsync();
        var monster = await f.SpawnAsync(Away);
        var start = await f.Read(() => (monster.X, monster.Z));
        await f.Loop.InvokeAsync(async () =>
        {
            await f.Buffs.AddSupportAsync(monster, Stun, 1500, f.Session.Role!.Id);
            f.Monsters.Engage(monster, f.Session);
        });
        await Task.Delay(800);
        Assert.Equal(start, await f.Read(() => (monster.X, monster.Z)));
        Assert.False(await f.Read(() => monster.Moving));
        Assert.Same(f.Session, await f.Read(() => monster.Target));

        // The stun expires: the monster walks in and swings.
        await f.HitAsync(monster);
        Assert.False(await f.Read(() => f.Buffs.UnitForbidden(monster, ForbiddenState.Walk)));
    }

    [Fact]
    public async Task AStunStopsAMonsterAlreadyWalking()
    {
        await using var f = await Fixture.StartAsync();
        var monster = await f.SpawnAsync(Away);
        await f.Loop.InvokeAsync(() => { f.Monsters.Engage(monster, f.Session); return Task.CompletedTask; });
        await f.Until(() => monster.Moving);
        await f.Loop.InvokeAsync(() => f.Buffs.AddSupportAsync(monster, Stun, 5000, f.Session.Role!.Id));
        await Task.Delay(300); // the next AI tick stops the walk
        var held = await f.Read(() => (monster.X, monster.Z));
        Assert.False(await f.Read(() => monster.Moving));
        await Task.Delay(800);
        Assert.Equal(held, await f.Read(() => (monster.X, monster.Z)));
        Assert.False(f.SwungBy(monster));
    }

    [Fact]
    public async Task AStunnedMonsterInReachDoesNotSwing()
    {
        await using var f = await Fixture.StartAsync();
        var monster = await f.SpawnAsync(Reach - 1);
        await f.Loop.InvokeAsync(async () =>
        {
            await f.Buffs.AddSupportAsync(monster, Stun, 5000, f.Session.Role!.Id);
            f.Monsters.Engage(monster, f.Session);
        });
        await Task.Delay(1200);
        Assert.False(f.SwungBy(monster));
    }

    [Fact]
    public async Task ARemovedControlFreesTheMonster()
    {
        await using var f = await Fixture.StartAsync();
        var monster = await f.SpawnAsync(Away);
        await f.Loop.InvokeAsync(async () =>
        {
            await f.Buffs.AddSupportAsync(monster, Stun, 60000, f.Session.Role!.Id);
            Assert.True(f.Buffs.UnitForbidden(monster, ForbiddenState.Walk));
            await f.Buffs.RemoveUnitConfigAsync(monster, Stun);
            Assert.False(f.Buffs.UnitForbidden(monster, ForbiddenState.Walk));
            Assert.Equal(0UL, f.Buffs.UnitStatus(monster));
        });
    }

    [Fact]
    public async Task APlayersFrozenOnAMonsterBreaksOnAHitIntoItsFollowUp()
    {
        await using var f = await Fixture.StartAsync();
        var monster = await f.SpawnAsync(Away);
        await f.Loop.InvokeAsync(async () =>
        {
            await f.Buffs.AddSupportAsync(monster, Frozen, 5000, f.Session.Role!.Id);
            await f.Buffs.BreakHitControlsAsync(monster);
            Assert.False(f.Buffs.UnitHasGroup(monster, BuffService.FreezeGroup));
            Assert.False(f.Buffs.UnitForbidden(monster, ForbiddenState.Walk));
            Assert.True(f.Buffs.UnitHasGroup(monster, FrozenAfter / 100));
            Assert.False(f.Buffs.HasUnitCombatBuffs(monster)); // an ordinary follow-up, as its cause
        });
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "ro3emu-control-" + Guid.NewGuid().ToString("N"));
        private SyntheticHost _host = null!;
        private BotClient _bot = null!;

        public GameSession Session => _host.Service<SessionRegistry>().All().Single(s => s.Role != null);
        public GameLoop Loop => _host.Service<GameLoop>();
        public MonsterService Monsters => _host.Service<MonsterService>();
        public BuffService Buffs => _host.Service<BuffService>();

        public static async Task<Fixture> StartAsync()
        {
            var f = new Fixture();
            f.CopyAndWriteData();
            f._host = await SyntheticHost.StartAsync(o => o.GameDataDir = f._root);
            f._bot = await f._host.InWorldAsync("Controller");
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

        private void CopyAndWriteData()
        {
            foreach (string file in Directory.EnumerateFiles(SyntheticData.Dir, "*", SearchOption.AllDirectories))
            {
                string dest = Path.Combine(_root, Path.GetRelativePath(SyntheticData.Dir, file));
                Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                File.Copy(file, dest);
            }
            var npcs = JsonNode.Parse(File.ReadAllText(Path.Combine(_root, "config", "NPCConfig.json")))!.AsObject();
            npcs[Key(Npc)] = JsonSerializer.SerializeToNode(new
            {
                _iID = Npc, _iNPCType = 1, _iLevel = 50, _iAttribute = 900001, _iCamp = 2, _iSpeed = 800, _iAI = Ai,
            });
            File.WriteAllText(Path.Combine(_root, "config", "NPCConfig.json"), npcs.ToJsonString());
            Write("AIConfig", new Dictionary<string, object> { [Key(Ai)] = new { _iID = Ai, _iChaseDistance = 5000 } });
            Write("BuffConfig", new Dictionary<string, object>
            {
                [Key(Stun)] = Control(Stun, BuffService.StunGroup),
                [Key(Frozen)] = new
                {
                    _iID = Frozen, _iBuffID = BuffService.FreezeGroup, _iType = NegativeType, _iTime = 5000,
                    _kBuffCategory = new[] { ControlTag }, _kLogicData = new[] { FrozenAfter, 2000 },
                },
                [Key(FrozenAfter)] = new { _iID = FrozenAfter, _iBuffID = FrozenAfter / 100, _iType = NegativeType, _iTime = 2000 },
                [Key(Petrify)] = Control(Petrify, BuffService.PetrifyGroup),
                [Key(Root)] = Control(Root, BuffService.RootGroup),
                [Key(KnockUp)] = Control(KnockUp, BuffService.KnockUpGroup),
                [Key(GenericFreeze)] = new { _iID = GenericFreeze, _iBuffID = GenericFreeze / 100, _iType = NegativeType, _iTime = 5000 },
            });
            Write("SpecialStatusConfig", new Dictionary<string, object>
            {
                [Key(StunState)] = new { _iID = StunState, _kForbidden = HeldForbids },
                [Key(FrozenState)] = new { _iID = FrozenState, _kForbidden = HeldForbids },
                [Key(PetrifyState)] = new { _iID = PetrifyState, _kForbidden = HeldForbids },
                [Key(KnockUpState)] = new { _iID = KnockUpState, _kForbidden = HeldForbids },
                [Key(RootState)] = new { _iID = RootState, _kForbidden = RootForbids },
            });
        }

        private static object Control(int id, int group) => new
        {
            _iID = id, _iBuffID = group, _iType = NegativeType, _iTime = 5000, _kBuffCategory = new[] { ControlTag },
        };

        private void Write(string table, object rows) =>
            File.WriteAllText(Path.Combine(_root, "config", table + ".json"), JsonSerializer.Serialize(rows));

        private static string Key(long id) => id.ToString(CultureInfo.InvariantCulture);

        /// <summary>The fixture monster <paramref name="away"/> m east of the player, reaching <see cref="Reach"/>.</summary>
        public Task<MonsterUnit> SpawnAsync(float away) => Loop.InvokeAsync(async () =>
        {
            var template = Monsters.Template(Npc) ?? throw new InvalidOperationException("No fixture monster.");
            var at = new MapPoint(PlayerX + away, 0, PlayerZ, 0);
            return await Monsters.SpawnSingleAsync(Session.SceneEntry, Npc, at, Session.InstanceId,
                    customTemplate: template with { AttackRange = Reach, RoamDistance = 0 })
                ?? throw new InvalidOperationException("Fixture monster could not spawn.");
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

        /// <summary>The monster's normal attack lands on the player (31101 from it).</summary>
        public Task<BotEvent> HitAsync(MonsterUnit m) => _bot.Inbox.WaitAsync(e => e.IsGame(CombatService.SkillHit)
            && SkillHitInfo.Parser.ParseFrom(e.Body).SrcID == m.Uid, TimeSpan.FromSeconds(10));

        public bool SwungBy(MonsterUnit m) => _bot.Inbox.Snapshot().Any(e => e.IsGame(CombatService.SkillHit)
            && SkillHitInfo.Parser.ParseFrom(e.Body).SrcID == m.Uid);

        public async ValueTask DisposeAsync()
        {
            await _bot.DisposeAsync();
            await _host.DisposeAsync();
            Directory.Delete(_root, true);
        }
    }
}

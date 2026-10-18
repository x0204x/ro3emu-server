using System.Text.Json;
using Romsg;
using Ro3Emu.Server.Data;

namespace Ro3Emu.Server.Game;

/// <summary>
/// Hidden Realm (TenGameplayConfig 50199, _iIsRandomDungeon 1, personal unlock 35100006 = base level 90): entering it
/// draws the boss the party fights. 82591 RandomDungeonNotify {origin, random, enterTime} opens UI_Sys_DungeonUnknownPanel
/// (LC_DAL_MultiPlayerDungeon._processRandomDungeonNotify), which turns a card to the drawn boss
/// (TenGameplayConfig.GetBossId(random)) and counts down to enterTime (Unix seconds, TimerManager.GetSystemTime); the
/// countdown is PublicParamConfig FiveGamePlay_RandomDuration (10, read by no Lua: a server parameter).
/// <para>(inferred) The draw is uniform over the bosses of the _iRandom 1 rows (50104-50106, the Hard five-player
/// bosses) that every member has unlocked; with none it is the Hidden Realm's own boss. The run reports the drawn boss
/// in its init data with the Hidden Realm as MultiDungeonInitData.origin, and counts as a Hidden Realm clear: its
/// reward (boss row 4300099 _iPersonAward/_iGuildAward), its allowance (TenGameplayRefreshTimeConfig
/// _iAddRandomRewardNum) and its record.</para>
/// </summary>
public sealed partial class DungeonService
{
    public const uint RandomDungeon = 82591;
    private readonly HashSet<GameSession> rolling = new();

    private static bool IsRandom(DungeonCatalog.Plan plan) => plan.Origin == null && plan.Multi is { } multi
        && GameData.Int(multi, "_iIsRandomDungeon") == 1;

    private int RandomSeconds() => data.Table("PublicParamConfig").TryGetValue("FiveGamePlay_RandomDuration", out var value)
        ? (int)Math.Clamp(GameData.Number(value) ?? 10, 0, 600) : 10;

    /// <summary>Every boss a party could be drawn: resolvable, in this playbook, unlocked for all members.</summary>
    internal List<DungeonCatalog.Plan> RandomPool(DungeonCatalog.Plan origin, IReadOnlyCollection<GameSession> members)
    {
        var pool = new List<DungeonCatalog.Plan>();
        foreach (var multi in catalog.MultiRows.Where(r => GameData.Int(r, "_iRandom") == 1).OrderBy(r => GameData.Int(r, "_iID")))
            for (int mark = 1; mark <= DungeonCatalog.Marks(multi); mark++)
            {
                if (catalog.Boss(multi, mark) is not { } boss || members.Any(s => !Meets(s, multi) || !Meets(s, boss))) continue;
                var plan = catalog.Resolve(new EnterDungeonReq { SceneEntry = GameData.Int(boss, "_iDungeon"),
                    InitData = new DungeonInitData { MultiDungeonInitData = new MultiDungeonInitData
                    { MultiDungeonId = (int)GameData.Int(multi, "_iID"), MarkId = mark } } });
                if (plan != null) pool.Add(plan with { Origin = new DungeonCatalog.RandomOrigin(origin.Multi!.Value, origin.Boss!.Value, origin.Mark) });
            }
        return pool;
    }

    /// <summary>The draw, its 82591 and, after the countdown, the ordinary run of the drawn boss.</summary>
    private async Task RollAsync(DungeonCatalog.Plan origin, GameSession[] members, long leader, long team, bool matched)
    {
        var pool = RandomPool(origin, members);
        var drawn = pool.Count > 0 ? pool[Random.Shared.Next(pool.Count)]
            : origin with { Origin = new DungeonCatalog.RandomOrigin(origin.Multi!.Value, origin.Boss!.Value, origin.Mark) };
        long enterTime = clock.GetUtcNow().ToUnixTimeSeconds() + RandomSeconds();
        var notify = new RandomDungeonNotify { EnterTime = enterTime,
            Origin = new MultiDungeonInfo { MultiDungeonId = origin.MultiId, MarkId = origin.Mark },
            Random = new MultiDungeonInfo { MultiDungeonId = drawn.MultiId, MarkId = drawn.Mark } };
        foreach (var member in members) rolling.Add(member);
        await Task.WhenAll(members.Select(member => SendQuietAsync(member, RandomDungeon, notify)));
        loop.ScheduleLegacy(TimeSpan.FromSeconds(RandomSeconds()), async () =>
        {
            foreach (var member in members) rolling.Remove(member);
            // A member who went offline, died or entered elsewhere during the card's countdown stays behind (inferred).
            var going = members.Where(s => sessions.All().Contains(s) && Admission(s, drawn, continuation: true) == 0).ToArray();
            if (going.Length == 0) return;
            await BeginAsync(drawn, going, going.Any(s => s.Role!.Id == leader) ? leader : going[0].Role!.Id, team, matched);
        });
    }
}

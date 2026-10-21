using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Romsg;
using Ro3Emu.Bot;
using Ro3Emu.Server;
using Ro3Emu.Server.Accounts;
using Ro3Emu.Server.Data;
using Ro3Emu.Server.Game;
using Ro3Emu.Server.Net;

namespace Ro3Emu.Tests;

public sealed class TotemEffectsTests : IAsyncLifetime
{
    private readonly string _dataDir = Path.Combine(Path.GetTempPath(), "ro3emu-totem-effects-" + Guid.NewGuid().ToString("N"));
    private WebApplication _app = null!;
    private string _http = "";

    public async Task InitializeAsync()
    {
        var (httpPort, microPort, gamePort) = TestPorts.FreePorts();
        _app = ServerHost.Create([], o =>
        {
            o.DataDir = _dataDir; o.GameDataDir = Path.Combine(RepoRoot(), "data");
            o.HttpPort = httpPort; o.MicroGatePort = microPort; o.GameGatePort = gamePort;
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

    [ClientDataFact]
    public async Task EffectiveAttributesStacksPassiveOwnershipAndHpConditionFollowCurrentPlan()
    {
        await using var bot = new BotClient(_http, File.ReadAllText(Path.Combine(_dataDir, "gate_rsa_public.pem")));
        await bot.LoginAsync("effect", "pw");
        await bot.EnterGateAsync();
        long role = (await bot.CreateRoleAsync("Effect")).RoleBaseInfo.Id;
        await bot.EnterWorldAsync(role);

        var registry = _app.Services.GetRequiredService<SessionRegistry>();
        var session = registry.All().Single(s => s.Role?.Id == role);
        var effects = _app.Services.GetRequiredService<TotemEffects>();
        var combat = _app.Services.GetRequiredService<CombatService>();
        var data = _app.Services.GetRequiredService<GameData>();
        var magic = data.Table("SkillConfig").Values.First(r => GameData.Int(r, "_iDmgType") == 2);
        Assert.Equal(1d, effects.OutgoingMultiplier(session, magic, DamageHitResult.Normal));
        Assert.Equal(1d, effects.IncomingMultiplier(session, DamageHitResult.Normal));
        Assert.Equal(1d, effects.HealingMultiplier(session));
        var item = new InvItem
        {
            Guid = 990001, Entry = 17300001, Stack = 1, Pack = 4,
            Attrs = new Dictionary<int, long>
            {
                [3] = 1, [240] = 10051001, [241] = 10053001,
                [242] = 10053005, [243] = 10052017, [244] = 10053010, [245] = 10053012
            },
        };
        session.Items.Add(item);
        session.Totems!.TotemPlans[0].SetUpTotem[1] = item.Guid;
        await effects.RefreshAsync(session);
        Assert.Contains(session.Buffs, b => b.IsPassive && b.SourceId == item.Guid && b.ConfigId == 2500201 && b.ExpireMs == long.MaxValue);
        Assert.Equal(1.01, effects.OutgoingMultiplier(session, magic, DamageHitResult.Normal), 3);
        Assert.Equal(0.99, effects.IncomingMultiplier(session, DamageHitResult.Normal), 3);
        Assert.Equal(1.01, effects.HealingMultiplier(session), 3);

        // Client preview opcode 10 uses inclusive >=80% and BaseLv-scaled component 1043.
        session.CurrentHp = null;
        Assert.Contains(effects.Attributes(session, item, 10052017), a => a.Id == 1043 && a.Value == 2);
        session.CurrentHp = 1;
        Assert.DoesNotContain(effects.Attributes(session, item, 10052017), a => a.Id == 1043);
        session.CurrentHp = null;

        // The registered op-2 passive grants a timed active floor when a normal attack or skill lands.
        var normal = data.Row("SkillConfig", 1000001)!.Value;
        foreach (var handler in combat.CastLanded) await handler(session, normal);
        var stack = Assert.Single(session.Buffs, b => b.ConfigId == 2500401 && b.SourceId == item.Guid);
        Assert.Equal(1, stack.PileCount);
        Assert.Equal(1, session.Buffs.Count(b => b.ConfigId == 2500401));
        for (int n = 0; n < 3; n++) foreach (var handler in combat.CastLanded) await handler(session, normal);
        stack = Assert.Single(session.Buffs, b => b.ConfigId == 2500401 && b.SourceId == item.Guid);
        Assert.Equal(3, stack.PileCount); // op18 raises this instance's category cap 2 -> 3.
        Assert.Equal(250, effects.Attributes(session, item, 10053005).Where(a => a.Id == 213).Sum(a => a.Value));
        Assert.Equal(1.025, effects.OutgoingMultiplier(session, magic, DamageHitResult.Normal), 3);

        // An ordinary buff with the same config must not replace the source-owned totem stack.
        var buffs = _app.Services.GetRequiredService<BuffService>();
        await buffs.AddAsync(session, 2500401, 30000, role);
        Assert.Equal(2, session.Buffs.Count(b => b.ConfigId == 2500401));
        Assert.Contains(session.Buffs, b => b.ConfigId == 2500401 && !b.IsTotemOwned && b.SourceId == role);
        Assert.Contains(session.Buffs, b => b.ConfigId == 2500401 && b.IsTotemOwned && b.SourceId == item.Guid && b.PileCount == 3);

        // Switching away removes the exact source's passive registration and floor, leaving the ordinary buff.
        session.Totems.CurrentPlan = 1;
        await effects.RefreshAsync(session);
        Assert.DoesNotContain(session.Buffs, b => b.SourceId == item.Guid);
        Assert.Contains(session.Buffs, b => b.ConfigId == 2500401 && !b.IsTotemOwned && b.SourceId == role);
        Assert.Equal(1d, effects.OutgoingMultiplier(session, magic, DamageHitResult.Normal));
        Assert.Equal(1d, effects.IncomingMultiplier(session, DamageHitResult.Normal));
        Assert.Equal(1d, effects.HealingMultiplier(session));
    }

    [ClientDataFact]
    public async Task RoleSwitchRemovesOldTotemStacksWithoutPreWorldPropertyPackets()
    {
        await using var bot = new BotClient(_http, File.ReadAllText(Path.Combine(_dataDir, "gate_rsa_public.pem")));
        await bot.LoginAsync("effectswitch", "pw");
        await bot.EnterGateAsync();
        long first = (await bot.CreateRoleAsync("EffectA")).RoleBaseInfo.Id;
        long second = (await bot.CreateRoleAsync("EffectB")).RoleBaseInfo.Id;
        var firstSelf = await bot.EnterWorldAsync(first);
        long attack = firstSelf.Property.Single(p => p.Idx == 103).Data;
        var registry = _app.Services.GetRequiredService<SessionRegistry>();
        var session = registry.All().Single(s => s.Role?.Id == first);
        var buffs = _app.Services.GetRequiredService<BuffService>();
        await buffs.AddStackAsync(session, 2500301, 60000, 990002, 2);
        await bot.Inbox.WaitAsync(e => e.IsGame(CharacterService.OnePropertyChange), TimeSpan.FromSeconds(10));
        Assert.Contains(session.Buffs, b => b.IsTotemOwned && b.ConfigId == 2500301);

        // Expiry/removal during an ordinary map load must still update the later ENTITYSELF snapshot.
        session.SceneLoading = true;
        await buffs.RemoveAsync(session, session.Buffs.Single(b => b.IsTotemOwned && b.ConfigId == 2500301));
        Assert.Equal(attack, session.EntityProperties.Single(p => p.Id == 103).Value);
        session.SceneLoading = false;
        await buffs.AddStackAsync(session, 2500301, 60000, 990002, 2);
        await bot.Inbox.WaitAsync(e => e.IsGame(CharacterService.OnePropertyChange), TimeSpan.FromSeconds(10));

        await bot.SwitchRoleAsync();
        int start = bot.Inbox.Snapshot().Count;
        var nextSelf = await bot.EnterWorldAsync(second);
        Assert.Same(session, registry.All().Single(s => s.Role?.Id == second));
        var events = bot.Inbox.Snapshot().Skip(start).ToArray();
        Assert.Contains(events, e => e.IsGame(MsgId.EntitySelf));
        Assert.DoesNotContain(events.TakeWhile(e => !e.IsGame(MsgId.EntitySelf)),
            e => e.IsGame(CharacterService.OnePropertyChange) || e.IsGame(BuffService.BuffRemove));
        Assert.DoesNotContain(session.Buffs, b => b.IsTotemOwned);
        Assert.Equal(attack, nextSelf.Property.Single(p => p.Idx == 103).Data);
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !Directory.Exists(Path.Combine(dir.FullName, "data", "config"))) dir = dir.Parent;
        return dir?.FullName ?? throw new DirectoryNotFoundException("repository data/config");
    }
}

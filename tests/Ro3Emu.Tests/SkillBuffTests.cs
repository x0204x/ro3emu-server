using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Romsg;
using Ro3Emu.Bot;
using Ro3Emu.Server;
using Ro3Emu.Server.Game;
using Ro3Emu.Server.Data;

namespace Ro3Emu.Tests;

/// <summary>Buff lifetime, control union, hand removal, immune-death choice and configured energy over the game wire.</summary>
public sealed class SkillBuffTests : IAsyncLifetime
{
    private readonly string _dataDir = Path.Combine(Path.GetTempPath(), "ro3emu-buff-" + Guid.NewGuid().ToString("N"));
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(5);
    private WebApplication _app = null!;
    private string _http = "";

    public async Task InitializeAsync()
    {
        var (httpPort, microPort, gamePort) = TestPorts.FreePorts();
        _app = ServerHost.Create([], o =>
        {
            o.DataDir = _dataDir;
            o.GameDataDir = Path.Combine(RepoRoot(), "data");
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

    private async Task<BotClient> InWorld(string name)
    {
        var bot = new BotClient(_http, File.ReadAllText(Path.Combine(_dataDir, "gate_rsa_public.pem")));
        await bot.LoginAsync(name.ToLowerInvariant(), "pw");
        await bot.EnterGateAsync();
        var role = await bot.CreateRoleAsync(name);
        await bot.EnterWorldAsync(role.RoleBaseInfo.Id);
        return bot;
    }

    private static async Task<T> Next<T>(BotClient bot, uint msgId, Google.Protobuf.MessageParser<T> parser, Func<T, bool>? match = null)
        where T : Google.Protobuf.IMessage<T>
    {
        var e = await bot.Inbox.WaitAsync(e => e.IsGame(msgId) && (match == null || match(parser.ParseFrom(e.Body))), Wait);
        return parser.ParseFrom(e.Body);
    }

    private async Task Gm(string account, string line)
    {
        using var http = new HttpClient();
        (await http.PostAsync($"{_http}/ro3emu/gm?account={account}", Operator.Command(line))).EnsureSuccessStatusCode();
    }

    private GameSession Session(string account) => _app.Services.GetRequiredService<SessionRegistry>().All()
        .Single(s => s.Account == account);

    private BuffService Buffs => _app.Services.GetRequiredService<BuffService>();
    private Task Run(Func<Task> work) => _app.Services.GetRequiredService<GameLoop>().InvokeAsync(work);
    private Task<T> Run<T>(Func<Task<T>> work) => _app.Services.GetRequiredService<GameLoop>().InvokeAsync(work);

    [ClientDataFact]
    public async Task CastGrantsNonemptyBuffAndHandRemovalRejectsProtectedAndForeignGuids()
    {
        await using var alice = await InWorld("Alice");
        await using var bob = await InWorld("Bob");
        await Gm("alice", "!job 1100");
        var learn = new SkillGrowthSkillPlanUpdateReq();
        learn.LevelUpList[11004] = new SkillData { SkillId = 11004, SkillLevel = 5 };
        learn.LevelUpList[11005] = new SkillData { SkillId = 11005, SkillLevel = 1 };
        await alice.SendGameAsync(SkillService.PlanUpdate, learn);
        Assert.Equal(0, (await Next(alice, SkillService.PlanUpdate, SkillGrowthSkillPlanUpdateResp.Parser)).Result);
        await alice.SendGameAsync(CombatService.UseSkill, new CastSkillReq { SkillId = 1100501 });
        Assert.Equal(0, (await Next(alice, CombatService.UseSkill, CastSkillRsp.Parser)).Result);
        var added = await Next(alice, BuffService.BuffUpdate, BuffUpdateNotify.Parser);
        Assert.Single(added.Buffs);
        Assert.Equal(1100501, added.Buffs[0].ConfigId);
        Assert.True(added.Buffs[0].BuffGuid > 0);
        var aoi = await Next(alice, CombatService.AoiAttrChange, AOIAttrChange.Parser,
            x => x.BuffInfo?.AddBuffs.Any(b => b.BuffGuid == added.Buffs[0].BuffGuid) == true);
        Assert.Single(aoi.BuffInfo.AddBuffs);

        await bob.SendGameAsync(BuffService.ClientRemoveBuff, new ClientRemoveBuffReq { BuffGuid = added.Buffs[0].BuffGuid });
        Assert.NotEqual(0, (await Next(bob, BuffService.ClientRemoveBuff, ClientRemoveBuffRsp.Parser)).Result);
        await alice.SendGameAsync(BuffService.ClientRemoveBuff, new ClientRemoveBuffReq { BuffGuid = added.Buffs[0].BuffGuid });
        Assert.NotEqual(0, (await Next(alice, BuffService.ClientRemoveBuff, ClientRemoveBuffRsp.Parser)).Result);
        Assert.Contains(Session("alice").Buffs, b => b.Guid == added.Buffs[0].BuffGuid);
    }

    [ClientDataFact]
    public async Task HandRemovableBuffRequestRemovesTheOwnedInstance()
    {
        await using var alice = await InWorld("Alice");
        var session = Session("alice");
        await Run(() => Buffs.AddAsync(session, 1120801, 15000, session.Role!.Id));
        var added = await Next(alice, BuffService.BuffUpdate, BuffUpdateNotify.Parser, x => x.Buffs.Any(b => b.ConfigId == 1120801));
        long guid = added.Buffs.Single().BuffGuid;
        await alice.SendGameAsync(BuffService.ClientRemoveBuff, new ClientRemoveBuffReq { BuffGuid = guid });
        Assert.Equal(0, (await Next(alice, BuffService.ClientRemoveBuff, ClientRemoveBuffRsp.Parser)).Result);
        Assert.Contains(guid, (await Next(alice, BuffService.BuffRemove, BuffRemoveNotify.Parser)).BuffGuids);
        Assert.DoesNotContain(session.Buffs, b => b.Guid == guid);
    }

    [ClientDataFact]
    public async Task OverlappingControlsRecompute2702AndKeepTheRemainingSource()
    {
        await using var alice = await InWorld("Alice");
        var session = Session("alice");
        await Run(() => Buffs.AddAsync(session, 901601, 15000, 1)); // Stun
        var stun = await Next(alice, CombatService.AoiAttrChange, AOIAttrChange.Parser,
            x => x.ForbidInfo != null && x.StateInfo != null);
        Assert.True(stun.ForbidInfo.Data.Values[0] != 0);
        Assert.True(Buffs.HasForbidden(session, 3)); // walk
        await Run(() => Buffs.AddAsync(session, 902201, 15000, 2)); // Sleep
        var sleep = await Next(alice, CombatService.AoiAttrChange, AOIAttrChange.Parser,
            x => x.ForbidInfo != null && x.StateInfo != null && x.StateInfo.Data.Values[0] != stun.StateInfo.Data.Values[0]);
        Assert.True(sleep.ForbidInfo.Data.Values[0] != 0);
        await Run(() => Buffs.RemoveAsync(session, session.Buffs.Single(b => b.ConfigId == 901601)));
        var after = await Next(alice, CombatService.AoiAttrChange, AOIAttrChange.Parser,
            x => x.ForbidInfo != null && x.StateInfo != null && x.StateInfo.Data.Values[0] != sleep.StateInfo.Data.Values[0]);
        Assert.True(after.ForbidInfo.Data.Values[0] != 0);
        Assert.True(Buffs.HasForbidden(session, 3));
        await Run(() => Buffs.RemoveAsync(session, session.Buffs.Single(b => b.ConfigId == 902201)));
        var clear = await Next(alice, CombatService.AoiAttrChange, AOIAttrChange.Parser,
            x => x.ForbidInfo != null && x.StateInfo != null && x.ForbidInfo.Data.Values[0] == 0);
        Assert.Single(clear.ForbidInfo.Data.Values); // Lua reads values[1] unconditionally.
    }

    [ClientDataFact]
    public async Task StunForbidsItemUseWithoutConsumingThePotion()
    {
        await using var alice = await InWorld("Alice");
        var session = Session("alice");
        var potion = session.Items.Single(i => i.Entry == 1000000 && i.Grid == 0);
        int before = potion.Stack;
        await Run(() => Buffs.AddAsync(session, 901601, 15000, 1)); // Stun: SpecialStatusConfig 18 forbids UseItem(12).
        Assert.True(Buffs.HasForbidden(session, 12));
        await alice.SendGameAsync(CharacterService.ItemUse, new ItemUseReq { ItemGuid = potion.Guid, ItemCount = 1 });
        Assert.Equal(ErrorCodes.ItemUseForbidden,
            (await Next(alice, CharacterService.ItemUse, ItemUseResp.Parser)).Result);
        Assert.Equal(before, session.Items.Single(i => i.Guid == potion.Guid).Stack);

        await Run(() => Buffs.RemoveAsync(session, session.Buffs.Single(b => b.ConfigId == 901601)));
        Assert.False(Buffs.HasForbidden(session, 12));
        await alice.SendGameAsync(CharacterService.ItemUse, new ItemUseReq { ItemGuid = potion.Guid, ItemCount = 1 });
        Assert.Equal(0, (await Next(alice, CharacterService.ItemUse, ItemUseResp.Parser)).Result);
        Assert.Equal(before - 1, session.Items.Single(i => i.Guid == potion.Guid).Stack);
    }

    [ClientDataFact]
    public async Task OsirisLethalOfferRequiresConfirmationAndSendsEffectCooldown()
    {
        await using var alice = await InWorld("Alice");
        var session = Session("alice");
        await Run(() => Buffs.AddAsync(session, 2052101, 15000, session.Role!.Id));
        var buff = await Next(alice, BuffService.BuffUpdate, BuffUpdateNotify.Parser, x => x.Buffs.Any(b => b.ConfigId == 2052101));
        Assert.True(await Run(() => Buffs.TryPreventLethalAsync(session)));
        var cd = await Next(alice, BuffService.BuffEffectCd, BuffEffectCdNotify.Parser);
        Assert.Equal(buff.Buffs[0].BuffGuid, cd.BuffGuid);
        var offer = await Next(alice, BuffService.ImmuneDeathNotify, PlayerOnEnterImmuneDeadNotify.Parser);
        Assert.Equal(2052101, offer.BuffConfigId);
        Assert.True(offer.ExpireTimestampMs > DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        await alice.SendGameAsync(BuffService.ImmuneDeathConfirm, new PlayerConfirmImmuneDeadReq { IsConfirm = true });
        Assert.Equal(0, (await Next(alice, BuffService.ImmuneDeathConfirm, PlayerConfirmImmuneDeadRsp.Parser)).Result);
        Assert.Null(session.ImmuneDeathOffer);
        Assert.False(session.Dead);
    }

    [ClientDataFact]
    public async Task SceneTravelPreservesPendingLethalDecisionUntilDeclined()
    {
        await using var alice = await InWorld("Alice");
        var session = Session("alice");
        await Run(() => Buffs.AddAsync(session, 2052101, 60000, session.Role!.Id));
        Assert.True(await Run(() => Buffs.TryPreventLethalAsync(session)));
        await Next(alice, BuffService.ImmuneDeathNotify, PlayerOnEnterImmuneDeadNotify.Parser);
        await Run(() =>
        {
            session.ImmuneDeathOffer = session.ImmuneDeathOffer! with { ExpiresMs = MonsterService.NowMs + 30000 };
            return Task.CompletedTask;
        });
        var pending = session.ImmuneDeathOffer;
        await alice.SendGameAsync(SceneService.SceneTeleport, new SceneTeleportReq { TargetMapId = 10100 });
        Assert.Equal(0, (await Next(alice, SceneService.SceneTeleport, SceneTeleportRsp.Parser)).Result);
        await Next(alice, MsgId.SwitchSceneLine, SwitchSceneLineResp.Parser, m => m.SceneEntry == 10100);
        await alice.SendGameAsync(MsgId.SceneLoaded, new SceneLoadedReq { SceneId = 10100 });
        await Next(alice, MsgId.EntitySelf, EntitySelf.Parser);
        Assert.Same(pending, session.ImmuneDeathOffer);
        await alice.SendGameAsync(BuffService.ImmuneDeathConfirm, new PlayerConfirmImmuneDeadReq { IsConfirm = false });
        Assert.Equal(0, (await Next(alice, BuffService.ImmuneDeathConfirm, PlayerConfirmImmuneDeadRsp.Parser)).Result);
        await Next(alice, CombatService.ReviveInfo, SyncClientPlayerReviveInfoNtf.Parser, m => m.BDead);
        Assert.True(session.Dead);
        Assert.Null(session.ImmuneDeathOffer);
    }

    [ClientDataFact]
    public async Task ConfiguredEnergyInitializesAndChargesTheExactMeter()
    {
        await using var alice = await InWorld("Alice");
        await Gm("alice", "!job 5400");
        var session = Session("alice");
        var initial = await Next(alice, BuffService.EnergyNotify, PlayerEnergyNotify.Parser, x => x.EnergyConfigId == 540001);
        Assert.Equal(2, initial.EnergyValue);
        var skill = _app.Services.GetRequiredService<GameData>().Row("SkillConfig", 1530101)!.Value;
        Assert.True(Buffs.CanSpendEnergy(session, skill));
        await Run(() => Buffs.SpendEnergyAsync(session, skill));
        var charged = await Next(alice, BuffService.EnergyNotify, PlayerEnergyNotify.Parser,
            x => x.EnergyConfigId == 540001 && x.EnergyValue == 1);
        Assert.Equal(1, charged.EnergyValue);
        await Run(() => Buffs.ReconcileEnergyAsync(session)); // ordinary same-job progress callback cannot refill
        Assert.Equal(1, session.Energy[540001].Value);
        await Gm("alice", "!job 4400");
        var newJob = await Next(alice, BuffService.EnergyNotify, PlayerEnergyNotify.Parser, x => x.EnergyConfigId == 440001);
        Assert.Equal(2, newJob.EnergyValue);
        Assert.False(session.Energy.ContainsKey(540001));
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !Directory.Exists(Path.Combine(dir.FullName, "data", "config"))) dir = dir.Parent;
        return dir?.FullName ?? throw new DirectoryNotFoundException("Repository data/config");
    }
}

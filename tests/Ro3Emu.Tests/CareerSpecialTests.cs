using System.Globalization;
using Google.Protobuf;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Romsg;
using Ro3Emu.Bot;
using Ro3Emu.Server;
using Ro3Emu.Server.Accounts;
using Ro3Emu.Server.Data;
using Ro3Emu.Server.Game;
using Ro3Emu.Server.Maps;

namespace Ro3Emu.Tests;

/// <summary>Special Class Items over the wire: 5002 DBCareerSpecial, 32721 lease, 32722 cancel, pendants, battle mount.</summary>
public sealed class CareerSpecialTests : IAsyncLifetime
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "ro3emu-career-" + Guid.NewGuid().ToString("N"));
    private WebApplication _app = null!;
    private string _http = "";
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(10);
    private const int Cart = 1500000, BirchCart = 1500001, Falcon = 1500100, GrandPeco = 1500203, KnightPeco = 1500201;

    public async Task InitializeAsync()
    {
        var (h, m, g) = TestPorts.FreePorts();
        _app = ServerHost.Create([], o => { o.DataDir = _dir; o.GameDataDir = Path.Combine(RepoRoot(), "data"); o.HttpPort = h; o.MicroGatePort = m; o.GameGatePort = g; });
        await _app.StartAsync(); _http = $"http://127.0.0.1:{h}";
    }

    public async Task DisposeAsync()
    {
        await _app.StopAsync(); await _app.DisposeAsync(); Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }

    private async Task<BotClient> InWorld(string name, bool create = true)
    {
        var b = new BotClient(_http, File.ReadAllText(Path.Combine(_dir, "gate_rsa_public.pem")));
        await b.LoginAsync(name.ToLowerInvariant(), "pw"); await b.EnterGateAsync();
        long id = create ? (await b.CreateRoleAsync(name)).RoleBaseInfo.Id : b.Roles.Single().Id;
        await b.EnterWorldAsync(id); return b;
    }

    private static async Task<T> Next<T>(BotClient b, uint id, MessageParser<T> p, Func<T, bool>? match = null) where T : IMessage<T>
    { var e = await b.Inbox.WaitAsync(e => e.IsGame(id) && (match == null || match(p.ParseFrom(e.Body))), Wait); return p.ParseFrom(e.Body); }

    private async Task Gm(string account, string command)
    { using var h = new HttpClient(); (await h.PostAsync($"{_http}/ro3emu/gm?account={account}", Operator.Command(command))).EnsureSuccessStatusCode(); }

    private GameSession Session(string account) => _app.Services.GetRequiredService<SessionRegistry>().All().Single(s => s.Account == account && s.Role != null);

    /// <summary>A career, base level and learned skill groups, persisted like a real learn (the skill tree is not driven here).</summary>
    private async Task Become(string account, int career, int level, params int[] skillGroups)
    {
        await Gm(account, $"!job {career}");
        await Gm(account, $"!lvl {level}");
        var loop = _app.Services.GetRequiredService<GameLoop>();
        for (var until = DateTime.UtcNow + Wait; ; await Task.Delay(20)) // the GM commits finish on the loop
        {
            bool applied = false;
            await loop.InvokeAsync(() => { applied = Session(account).Progress is { } p && p.Career == career && p.Level == level; return Task.CompletedTask; });
            if (applied) break;
            Assert.True(DateTime.UtcNow < until, "job/level not applied");
        }
        // Level-up observers may still commit progress right after the GM reply: retry on their newer revision.
        for (int attempt = 0; ; attempt++)
        {
            try
            {
                await loop.InvokeAsync(() =>
                {
                    var s = Session(account);
                    foreach (int group in skillGroups) s.Progress!.Skills[group] = 1;
                    _app.Services.GetRequiredService<RoleStore>().SaveProgress(s.Role!.Id, s.Progress!);
                    return Task.CompletedTask;
                });
                return;
            }
            catch (Ro3Emu.Server.Accounts.Persistence.OwnerRevisionConflictException) when (attempt < 50) { await Task.Delay(50); }
        }
    }

    private static async Task<int> LeaseAsync(BotClient b, int id)
    {
        await b.SendGameAsync(CareerSpecialService.Lease, new LeaseCareerSpecialItemReq { CartRemouldCfgId = id });
        return (await Next(b, CareerSpecialService.Lease, LeaseCareerSpecialItemRsp.Parser)).ErrCode;
    }

    private static async Task<int> CancelAsync(BotClient b, int id)
    {
        await b.SendGameAsync(CareerSpecialService.CancelLease, new CancelLeaseCareerSpecialItemReq { CartRemouldCfgId = id });
        return (await Next(b, CareerSpecialService.CancelLease, CancelLeaseCareerSpecialItemRsp.Parser)).ErrCode;
    }

    [ClientDataFact]
    public async Task LoginSendsBothEmptySlotsAndRefusalsFollowCanWearProfessionItem()
    {
        await using var a = await InWorld("Alice");
        var initial = await Next(a, CareerSpecialService.Sync, DBCareerSpecialIncrement.Parser);
        Assert.True(initial.SyncAllMark);
        Assert.NotNull(initial.PendantSlot?.All); Assert.Equal(0, initial.PendantSlot!.All.CartRemouldCfg);
        Assert.NotNull(initial.MountsSlot?.All); Assert.Equal(0, initial.MountsSlot!.All.CartRemouldCfg);

        Assert.Equal(CareerSpecialService.ErrMissing, await LeaseAsync(a, 42));
        Assert.Equal(ErrorCodes.FunctionLocked, await LeaseAsync(a, Cart)); // function 16 opens at base level 10
        await Gm("alice", "!lvl 10");
        Assert.Equal(CareerSpecialService.ErrJob, await LeaseAsync(a, Cart)); // a Novice is not in _kProfessionID
        await Gm("alice", "!job 6100");
        Assert.Equal(CareerSpecialService.ErrSkill, await LeaseAsync(a, Cart)); // Cart Use (group 16003) not learned
        await Gm("alice", "!job 3200");
        Assert.Equal(CareerSpecialService.ErrLevel, await LeaseAsync(a, Falcon)); // _iUnlockBaseLv 30
        Assert.Equal(CareerSpecialService.ErrNotLeased, await CancelAsync(a, Falcon));
        Assert.Equal(0, Session("alice").CareerSpecial!.PendantSlot.CartRemouldCfg);
        var stored = _app.Services.GetRequiredService<RoleStore>().LoadCareerSpecial(Session("alice").Role!.Id);
        Assert.True(stored == null || DBCareerSpecial.Parser.ParseFrom(stored).PendantSlot?.CartRemouldCfg is null or 0);
    }

    [ClientDataFact]
    public async Task CartLeaseShowsThePendantToViewersPersistsAndCannotBeCancelledWhileTheCartHoldsItems()
    {
        await using (var a = await InWorld("Alice"))
        await using (var b = await InWorld("Bob"))
        {
            await Next(a, CareerSpecialService.Sync, DBCareerSpecialIncrement.Parser);
            await Become("alice", 6100, 10, 16003, 16004); // Cart Use; Cart Remodeling unlocks the other carts
            Assert.Equal(0, await LeaseAsync(a, Cart));
            var lease = await Next(a, CareerSpecialService.Sync, DBCareerSpecialIncrement.Parser, p => !p.SyncAllMark);
            Assert.Equal(Cart, lease.PendantSlot.Inc.CartRemouldCfg);
            foreach (var bot in new[] { a, b })
            {
                var shown = await Next(bot, WorldEffectsService.Pendant, PendantUpdate.Parser, p => p.EntityID == (long)a.Self!.Uuid);
                Assert.Equal(51001, shown.PendantInfo[51001].PendantId);
                Assert.False(shown.PendantInfo[51001].IsHide);
            }
            Assert.True(CareerSpecialService.HoldsSubType(Session("alice"), _app.Services.GetRequiredService<GameData>(), 1));

            // Another cart replaces the first one in the same slot; the old pendant goes hidden.
            Assert.Equal(0, await LeaseAsync(a, BirchCart));
            var swap = await Next(b, WorldEffectsService.Pendant, PendantUpdate.Parser, p => p.PendantInfo.ContainsKey(51002));
            Assert.True(swap.PendantInfo[51001].IsHide);
            Assert.False(swap.PendantInfo[51002].IsHide);

            // Pack 100 holds an item: the client's 23034 rule, server code 1111.
            var items = Session("alice").Items;
            long potion = items.Single(i => i.Entry == 1000000 && i.Pack != 100).Guid;
            var move = new CareerSpecialTransferItemsReq { TransType = CareerSpecialTransferItemsType.ToCart };
            move.ItemGuidCountMap[potion] = 2;
            await a.SendGameAsync(CharacterService.CareerSpecialTransfer, move);
            Assert.Equal(0, (await Next(a, CharacterService.CareerSpecialTransfer, CareerSpecialTransferItemsRsp.Parser)).Result);
            Assert.Equal(CareerSpecialService.ErrCartNotEmpty, await CancelAsync(a, BirchCart));
            Assert.Equal(BirchCart, Session("alice").CareerSpecial!.PendantSlot.CartRemouldCfg);
            var back = new CareerSpecialTransferItemsReq { TransType = CareerSpecialTransferItemsType.ToNormal };
            back.ItemGuidCountMap[Session("alice").Items.Single(i => i.Pack == 100).Guid] = 2;
            await a.SendGameAsync(CharacterService.CareerSpecialTransfer, back);
            Assert.Equal(0, (await Next(a, CharacterService.CareerSpecialTransfer, CareerSpecialTransferItemsRsp.Parser)).Result);
            Assert.Equal(0, await CancelAsync(a, BirchCart));
            var cancel = await Next(a, CareerSpecialService.Sync, DBCareerSpecialIncrement.Parser, p => p.PendantSlot?.Inc != null && p.PendantSlot.Inc.HasCartRemouldCfg && p.PendantSlot.Inc.CartRemouldCfg == 0);
            Assert.NotNull(cancel);
            var hidden = await Next(b, WorldEffectsService.Pendant, PendantUpdate.Parser, p => p.PendantInfo.TryGetValue(51002, out var d) && d.IsHide);
            Assert.Single(hidden.PendantInfo);
            Assert.Equal(0, await LeaseAsync(a, Cart));
        }
        await using var again = await InWorld("Alice", create: false);
        var restored = await Next(again, CareerSpecialService.Sync, DBCareerSpecialIncrement.Parser);
        Assert.Equal(Cart, restored.PendantSlot.All.CartRemouldCfg);
        Assert.Equal(51001, again.Self!.PendantInfo[51001].PendantId);
    }

    [ClientDataFact]
    public async Task CrusaderPecoLeaseSelectsTheBattleSlotRidesAndCancelDismounts()
    {
        await using var a = await InWorld("Alice");
        await using var b = await InWorld("Bob");
        await Become("alice", 1210, 30, 11407); // Crusader, Riding
        Assert.Equal(CareerSpecialService.ErrJob, await LeaseAsync(a, KnightPeco)); // Peco Peco is the Knight line's
        Assert.Equal(0, await LeaseAsync(a, GrandPeco));
        await Next(a, CareerSpecialService.Sync, DBCareerSpecialIncrement.Parser, p => p.MountsSlot?.Inc?.CartRemouldCfg == GrandPeco);
        var slot = await Next(a, MountService.Sync, DBMountsInfoIncrement.Parser, p => p.MountIds.ContainsKey(2));
        Assert.Equal(GrandPeco, Session("alice").Mounts!.MountIds[2].MountId);
        Assert.False(Session("alice").Mounts!.MountIds[2].IsRiding);
        Assert.NotNull(slot);
        Assert.Empty(Session("alice").CareerPendants); // a Peco has no pendant; it shows only when ridden

        await a.SendGameAsync(MountService.Ride, new PlayerRiderMountReq { Tp = (eRiderType)3 }); // on-battle
        Assert.Equal(0, (await Next(a, MountService.Ride, PlayerRiderMountResp.Parser)).Result);
        Assert.Equal(GrandPeco, MountService.Rider(Session("alice")));
        foreach (var bot in new[] { a, b })
            await Next(bot, CharacterService.EntityBaseChange, EntityBase.Parser,
                p => p.Uuid == a.Self!.Uuid && p.NotifyType == EntityBase.Types.NotifyType.Rider && p.CurRider == GrandPeco);

        Assert.Equal(0, await CancelAsync(a, GrandPeco));
        Assert.Equal(0, MountService.Rider(Session("alice")));
        Assert.Equal(0, Session("alice").Mounts!.MountIds[2].MountId);
        await Next(b, CharacterService.EntityBaseChange, EntityBase.Parser,
            p => p.Uuid == a.Self!.Uuid && p.NotifyType == EntityBase.Types.NotifyType.Rider && p.CurRider == 0);
        await a.SendGameAsync(MountService.Ride, new PlayerRiderMountReq { Tp = (eRiderType)3 });
        Assert.NotEqual(0, (await Next(a, MountService.Ride, PlayerRiderMountResp.Parser)).Result);
    }

    /// <summary>
    /// A Crusader on a Grand Peco who gets on the travel mount (the client does it by itself when auto-pathing) is back on
    /// the Peco once off it again, as before a cast.
    /// </summary>
    [ClientDataFact]
    public async Task TheBattleMountOutlastsATravelRide()
    {
        await using var a = await InWorld("Alice");
        await Become("alice", 1210, 30, 11407); // Crusader, Riding
        Assert.Equal(0, await LeaseAsync(a, GrandPeco));
        await Gm("alice", "!item 1501013 1");
        long guid = Session("alice").Items.Single(i => i.Entry == 1501013).Guid;
        await a.SendGameAsync(31323, new ItemUseReq { ItemGuid = guid, ItemCount = 1 });
        Assert.Equal(0, (await Next(a, 31323, ItemUseResp.Parser)).Result);
        await a.SendGameAsync(MountService.SetConfig, new PlayerMountsSetCfgReq { MountId = 60030010 });
        Assert.Equal(0, (await Next(a, MountService.SetConfig, PlayerMountsSetCfgResp.Parser)).Result);

        foreach (var tp in new[] { eRiderType.OnBattle, eRiderType.OnSystem })
        {
            await a.SendGameAsync(MountService.Ride, new PlayerRiderMountReq { Tp = tp });
            Assert.Equal(0, (await Next(a, MountService.Ride, PlayerRiderMountResp.Parser)).Result);
        }
        Assert.Equal(60030010, MountService.Rider(Session("alice")));
        Assert.True(Session("alice").Mounts!.MountIds[2].IsRiding);
        await a.SendGameAsync(MountService.Ride, new PlayerRiderMountReq { Tp = eRiderType.OffSystem });
        Assert.Equal(0, (await Next(a, MountService.Ride, PlayerRiderMountResp.Parser)).Result);
        Assert.Equal(GrandPeco, MountService.Rider(Session("alice")));

        // The battle mount button on a rider of the travel mount alone swaps them onto the Peco.
        foreach (var tp in new[] { eRiderType.OffBattle, eRiderType.OnSystem, eRiderType.OnBattle })
        {
            await a.SendGameAsync(MountService.Ride, new PlayerRiderMountReq { Tp = tp });
            Assert.Equal(0, (await Next(a, MountService.Ride, PlayerRiderMountResp.Parser)).Result);
        }
        Assert.Equal(GrandPeco, MountService.Rider(Session("alice")));
        Assert.False(Session("alice").Mounts!.MountIds[1].IsRiding);
    }

    // RidingEffects: a ridden mount's _kBindBuff rows, the riding passives' type 17 Movement Speed and their own rows.
    private const int Wolf = 1500400, Dragon = 1500205, PerSpeed = 1131;

    private async Task<long> RideAsync(BotClient a, int mount)
    {
        Assert.Equal(0, await LeaseAsync(a, mount));
        await a.SendGameAsync(MountService.Ride, new PlayerRiderMountReq { Tp = (eRiderType)3 }); // on-battle
        Assert.Equal(0, (await Next(a, MountService.Ride, PlayerRiderMountResp.Parser)).Result);
        return await SpeedAsync();
    }

    private async Task<long> SpeedAsync()
    {
        long speed = 0;
        await _app.Services.GetRequiredService<GameLoop>().InvokeAsync(() =>
        { speed = _app.Services.GetRequiredService<CharacterService>().Stats(Session("alice"))[PerSpeed]; return Task.CompletedTask; });
        return speed;
    }

    private async Task<int[]> BuffsAsync()
    {
        int[] ids = [];
        await _app.Services.GetRequiredService<GameLoop>().InvokeAsync(() =>
        { ids = [.. Session("alice").Buffs.Select(b => b.ConfigId)]; return Task.CompletedTask; });
        return ids;
    }

    // Wolf Taming: "While mounted on the Demonic Wolf, Movement Speed increases by 10% and Normal Attack becomes [Warg
    // Strike]" is the wolf's bind buff 1331001; Wolf's Fang level 1 adds its 13312 row 1331201 (Physical ATK +1%).
    [ClientDataFact]
    public async Task RidingTheWolfGrantsItsMountBuffAndWolfsFangUntilDismounted()
    {
        await using var a = await InWorld("Alice");
        await Become("alice", 3400, 99, 13310, 13311); // Ranger, Wolf Taming, Wolf's Fang
        long walking = await SpeedAsync();
        Assert.Equal(walking + 1000, await RideAsync(a, Wolf));
        Assert.Contains(1331001, await BuffsAsync());
        Assert.Contains(1331201, await BuffsAsync());

        Assert.Equal(0, await CancelAsync(a, Wolf));
        Assert.DoesNotContain(1331001, await BuffsAsync());
        Assert.DoesNotContain(1331201, await BuffsAsync());
        Assert.Equal(walking, await SpeedAsync());
    }

    // JobAttackConfig 3400 on 1500400: "Normal Attack becomes [Warg Strike]". The client reads its normal attack from the
    // wheel (the bar's slot and atkSkillId), and only the skill window looks the mount up by itself.
    [ClientDataFact]
    public async Task RidingTheWolfMakesWargStrikeTheNormalAttackUntilDismounted()
    {
        const int ArrowAttack = 1300001, WargStrike = 1003201;
        await using var a = await InWorld("Alice");
        await Become("alice", 3400, 99, 13310);
        Assert.Equal(0, await LeaseAsync(a, Wolf));
        await a.SendGameAsync(GmCommands.GeneralProcGm, new GeneralGMProcReq { GmType = (GmOpEnum)173, ParamString = "2110120" }); // a bow: key 110
        await Next(a, GmCommands.GeneralProcGm, GeneralGMProcRsp.Parser);
        Assert.Contains(Session("alice").Items, item => item.Entry == 2110120 && item.Grid == 1);

        await a.SendGameAsync(MountService.Ride, new PlayerRiderMountReq { Tp = eRiderType.OnBattle });
        Assert.Equal(0, (await Next(a, MountService.Ride, PlayerRiderMountResp.Parser)).Result);
        var riding = await Next(a, SkillService.SkillDataSync, DBSkillDataIncrement.Parser, m => m.CurrentAtkSkillId == WargStrike);
        Assert.Equal(WargStrike, riding.SkillWheel.Inc.AtkSkillId);
        Assert.Contains(riding.SkillWheel.Inc.WhellSkills.Values, slot => slot.Inc?.SkillConfigId == WargStrike);
        Assert.Null(riding.OriginSkillWheel); // unchanged: the client's base map keeps the career's own attack

        // It is sent, not kept in the plans.
        Assert.NotEqual(WargStrike, Session("alice").SkillPlans!.CurrentAtkSkillId);

        await a.SendGameAsync(MountService.Ride, new PlayerRiderMountReq { Tp = eRiderType.OffBattle });
        Assert.Equal(0, (await Next(a, MountService.Ride, PlayerRiderMountResp.Parser)).Result);
        var walking = await Next(a, SkillService.SkillDataSync, DBSkillDataIncrement.Parser, m => m.CurrentAtkSkillId == ArrowAttack);
        Assert.Equal(ArrowAttack, walking.SkillWheel.Inc.AtkSkillId);
        Assert.Contains(walking.SkillWheel.Inc.WhellSkills.Values, slot => slot.Inc?.SkillConfigId == ArrowAttack);

        // Accepted while riding, and a relogged rider starts with it.
        await a.SendGameAsync(MountService.Ride, new PlayerRiderMountReq { Tp = eRiderType.OnBattle });
        Assert.Equal(0, (await Next(a, MountService.Ride, PlayerRiderMountResp.Parser)).Result);
        await Next(a, SkillService.SkillDataSync, DBSkillDataIncrement.Parser, m => m.CurrentAtkSkillId == WargStrike);
        var target = await _app.Services.GetRequiredService<GameLoop>().InvokeAsync(() =>
        {
            var alice = Session("alice");
            return _app.Services.GetRequiredService<MonsterService>().SpawnSingleAsync(alice.SceneEntry, 5000001,
                new MapPoint(alice.Position.X, alice.Position.Y, alice.Position.Z, 0), alice.InstanceId);
        });
        await a.SendGameAsync(CombatService.UseSkill, new CastSkillReq { SkillId = WargStrike, TargetId = target!.Uid });
        var cast = await Next(a, CombatService.UseSkill, CastSkillRsp.Parser);
        Assert.Equal(0, cast.Result);
        var hit = await Next(a, CombatService.SkillHit, SkillHitInfo.Parser, p => p.SessionId == cast.SessionId);
        Assert.True(Assert.Single(hit.Dsts).Damage > 0);
        await a.DisposeAsync();
        await using var again = await InWorld("Alice", create: false);
        var relogged = await Next(again, SkillService.SkillDataSync, DBSkillDataIncrement.Parser, m => m.SyncAllMark);
        Assert.Equal(WargStrike, relogged.SkillWheel.All.AtkSkillId);
        Assert.Contains(relogged.SkillWheel.All.WhellSkills.Values, slot => slot.SkillConfigId == WargStrike);
        Assert.Contains(relogged.OriginSkillWheel.All.WhellSkills.Values, slot => slot.SkillConfigId == ArrowAttack);
    }

    /// <summary>A stationary monster that stays out of the fight, some metres from Alice along walkable ground.</summary>
    private async Task<MonsterUnit> WolfTargetAsync(float metres)
    {
        var alice = Session("alice");
        var start = alice.Position.Clone();
        var nav = _app.Services.GetRequiredService<WorldData>().Map(alice.SceneEntry)!.Nav;
        var (ux, uz) = new[] { (1f, 0f), (-1f, 0f), (0f, 1f), (0f, -1f) }.First(o => nav == null
            || nav.CanWalk(start.X, start.Y, start.Z, start.X + o.Item1 * metres, start.Z + o.Item2 * metres));
        var monsters = _app.Services.GetRequiredService<MonsterService>();
        return await _app.Services.GetRequiredService<GameLoop>().InvokeAsync(async () =>
        {
            var template = monsters.Template(5000002)! with { RoamDistance = 0, SurroundDistance = 0 };
            var at = new MapPoint(start.X + ux * metres, start.Y, start.Z + uz * metres, 0);
            var target = await monsters.SpawnSingleAsync(alice.SceneEntry, 5000002, at, alice.InstanceId, customTemplate: template);
            target!.StaysOutOfBattle = true;
            target.Hp = 10_000_000;
            return target;
        });
    }

    private static async Task<(CastSkillRsp Cast, UseInSkillInfo Timeline, SkillHitInfo Hit)> CastOnAsync(BotClient a, int skill, MonsterUnit target)
    {
        await a.SendGameAsync(CombatService.UseSkill, new CastSkillReq { SkillId = skill, TargetId = target.Uid, TargetPos = target.Pos });
        var cast = await Next(a, CombatService.UseSkill, CastSkillRsp.Parser);
        Assert.Equal(0, cast.Result);
        var timeline = await Next(a, CombatService.UseInSkill, UseInSkillInfo.Parser, m => m.SessionId == cast.SessionId);
        return (cast, timeline, await Next(a, CombatService.SkillHit, SkillHitInfo.Parser, m => m.SessionId == cast.SessionId));
    }

    private async Task AssertChargedAsync(BotClient a, MonsterUnit target, int skill, int effect, int ms)
    {
        var alice = Session("alice");
        var moved = await Next(a, CombatService.SkillMove, SkillStartMoveNotify.Parser, m => m.EntityID == (ulong)alice.Role!.Id);
        Assert.Equal(skill, moved.SkillConfigId);
        Assert.Equal(effect, moved.EffectId);
        Assert.Equal(1, moved.MoveType); // enum_SkillMoveType.Move
        Assert.Equal(ms, moved.MoveTimeMs);
        Assert.InRange(MonsterService.Distance(target.X, target.Z, moved.EndPos.X, moved.EndPos.Z), 0.9f, 1.1f); // "in front of it" (inferred 1 m)
        await _app.Services.GetRequiredService<GameLoop>().InvokeAsync(() =>
        {
            Assert.Equal(moved.EndPos, alice.Position);
            return Task.CompletedTask;
        });
    }

    // Warg Strike's mode row on the wolf (show 13312): "Commands the Demonic Wolf to Charge the designated ST target". Its
    // displacement effect 1331203 is in no m_kShows entry, so only a 31112 moves the rider.
    [ClientDataFact]
    public async Task WargStrikeChargesTheRiderAtItsTarget()
    {
        await using var a = await InWorld("Alice");
        await Become("alice", 3400, 99, 13310, 13311, 13312);
        await RideAsync(a, Wolf);
        var target = await WolfTargetAsync(10);
        var (_, timeline, hit) = await CastOnAsync(a, 1331201, target);
        Assert.Equal(1331211, timeline.SkillID);
        Assert.Equal(1331211, hit.SkillId);
        await AssertChargedAsync(a, target, 1331211, 1331203, 94);

        // Already in front of the target it stays where it is.
        var start = await _app.Services.GetRequiredService<GameLoop>().InvokeAsync(() =>
        {
            var alice = Session("alice");
            alice.SkillCds.Clear();
            alice.SkillModeCds.Clear();
            alice.SkillCast = null;
            return Task.FromResult(alice.Position.Clone());
        });
        await CastOnAsync(a, 1331201, target);
        await Task.Delay(100);
        Assert.Single(a.Inbox.Snapshot(), e => e.IsGame(CombatService.SkillMove));
        Assert.Equal(start, Session("alice").Position);
    }

    // Warg Hunt (_kScriptLogic WolvesShadow) shows only Skill27: the three wolves are fx_skill_wughunt_01/02 in the shows of
    // the _kLogicData rows [near, far], and the dash "if you are more than ${5}m from the target" is the far row's 1333014.
    [ClientDataTheory]
    [InlineData(3f, 1332901, 0)]        // within 5 m: Warg Hunt (Melee), no dash
    [InlineData(10f, 1333001, 1333014)] // Warg Hunt (Ranged), 80 ms dash
    public async Task WargHuntCastsItsNearOrFarRow(float metres, int child, int effect)
    {
        await using var a = await InWorld("Alice");
        await Become("alice", 3400, 99, 13310, 13311, 13323);
        await RideAsync(a, Wolf);
        var target = await WolfTargetAsync(metres);
        var before = Session("alice").Position.Clone();
        var (cast, timeline, hit) = await CastOnAsync(a, 1332301, target);
        Assert.Equal(child, timeline.SkillID);
        Assert.Equal(child, hit.SkillId);
        Assert.Equal(3, hit.Split); // _kShowDmg "3 hits"
        Assert.True(Assert.Single(hit.Dsts).Damage > 0);
        if (effect != 0) await AssertChargedAsync(a, target, child, effect, 80);
        else
        {
            await Task.Delay(100);
            Assert.DoesNotContain(a.Inbox.Snapshot(), e => e.IsGame(CombatService.SkillMove));
            Assert.Equal(before, Session("alice").Position);
        }
        // One cast and one hit, the wheel's cooldown on the wheel's group.
        await Task.Delay(100);
        Assert.Single(a.Inbox.Snapshot(), e => e.IsGame(CombatService.UseInSkill));
        Assert.Single(a.Inbox.Snapshot(), e => e.IsGame(CombatService.SkillHit) && SkillHitInfo.Parser.ParseFrom(e.Body).SessionId == cast.SessionId);
        var cooldown = await Next(a, CombatService.SkillCdUpdate, SkillCdNotify.Parser, m => m.Infos.Any(i => i.SkillId == 13323));
        Assert.Equal(13323, Assert.Single(cooldown.Infos).SkillId);
    }

    // Warg King's BuffConfig 1332601 selects mode 1 of 13323, 13329 and 13330: the five-wolf rows.
    [ClientDataFact]
    public async Task WargHuntFollowsTheModeOfItsNearAndFarGroups()
    {
        await using var a = await InWorld("Alice");
        await Become("alice", 3400, 99, 13310, 13311, 13323);
        await RideAsync(a, Wolf);
        await _app.Services.GetRequiredService<GameLoop>().InvokeAsync(() =>
            _app.Services.GetRequiredService<BuffService>().AddAsync(Session("alice"), 1332601, 60000, Session("alice").Role!.Id));
        var target = await WolfTargetAsync(10);
        var (_, timeline, hit) = await CastOnAsync(a, 1332301, target);
        Assert.Equal(1333011, timeline.SkillID);
        Assert.Equal(5, hit.Split);
        await AssertChargedAsync(a, target, 1333011, 13330113, 80);
    }

    // The wheel a rider is sent differs from the saved one, and the client names the slot by the row it shows.
    [ClientDataFact]
    public async Task TheBarCanBeEditedWhileTheWheelShowsTheMountsAttack()
    {
        const int WargStrike = 1003201;
        await using var a = await InWorld("Alice");
        await Become("alice", 3400, 99, 13310);
        Assert.Equal(0, await LeaseAsync(a, Wolf));
        await a.SendGameAsync(GmCommands.GeneralProcGm, new GeneralGMProcReq { GmType = (GmOpEnum)173, ParamString = "2110120" }); // a bow: key 110
        await Next(a, GmCommands.GeneralProcGm, GeneralGMProcRsp.Parser);
        await a.SendGameAsync(MountService.Ride, new PlayerRiderMountReq { Tp = eRiderType.OnBattle });
        Assert.Equal(0, (await Next(a, MountService.Ride, PlayerRiderMountResp.Parser)).Result);
        var riding = await Next(a, SkillService.SkillDataSync, DBSkillDataIncrement.Parser, m => m.CurrentAtkSkillId == WargStrike);
        int slot = riding.SkillWheel.Inc.WhellSkills.First(kv => kv.Value.Inc?.SkillConfigId == WargStrike).Key;
        var shown = Ro3Emu.Server.Data.Config.SkillConfig.Get(_app.Services.GetRequiredService<GameData>(), WargStrike)!.Value;

        await a.SendGameAsync(SkillService.SkillLoad, new SkillLoadReq
        {
            OpType = SkillLoadOperation.SkillLoadOpUnload, IsManual = true, SocketNum = slot - 1,
            Info = new SkillData { SkillId = (int)shown.SkillId, SkillLevel = (int)shown.Level }
        });
        Assert.Equal(0, (await Next(a, SkillService.SkillLoad, SkillLoadResp.Parser)).Result);
        Assert.False(_app.Services.GetRequiredService<SkillService>().Build(Session("alice")).SkillWheel.WhellSkills.ContainsKey(slot));
    }

    // Lua_CFG_JobAttackConfig._getData: the Ranger's rows name no weapon but a bow and bare hands, and a weapon without a row has no attack to read.
    [ClientDataFact]
    public async Task AWeaponTheTableDoesNotNameKeepsTheCareersAttack()
    {
        await using var a = await InWorld("Alice");
        await Become("alice", 1100, 99);
        var sword = _app.Services.GetRequiredService<GameData>().Table("ItemConfig").First(p => p.Value.TryGetProperty("_iSubType", out var sub)
            && sub.GetInt32() == 2 && p.Value.GetProperty("_iType").GetInt32() == 5).Key;
        await a.SendGameAsync(GmCommands.GeneralProcGm, new GeneralGMProcReq { GmType = (GmOpEnum)173, ParamString = sword });
        await Next(a, GmCommands.GeneralProcGm, GeneralGMProcRsp.Parser);
        await Become("alice", 3400, 99, 13310);
        Assert.Contains(Session("alice").Items, item => item.Entry == long.Parse(sword, CultureInfo.InvariantCulture) && item.Grid == 1); // kept through the job change
        Assert.Equal(0, await LeaseAsync(a, Wolf));
        await a.SendGameAsync(MountService.Ride, new PlayerRiderMountReq { Tp = eRiderType.OnBattle });
        Assert.Equal(0, (await Next(a, MountService.Ride, PlayerRiderMountResp.Parser)).Result);
        await Assert.ThrowsAsync<TimeoutException>(() => a.Inbox.WaitAsync(e => e.IsGame(SkillService.SkillDataSync)
            && DBSkillDataIncrement.Parser.ParseFrom(e.Body).CurrentAtkSkillId == 1003201, TimeSpan.FromSeconds(1)));
    }

    // The table's other rows work the same way: a Crusader's Peco has no weapon condition (_kWeaponType empty).
    [ClientDataFact]
    public async Task APecoRiderAttacksWithTheMountsAttack()
    {
        await using var a = await InWorld("Alice");
        await Become("alice", 1210, 30, 11407);
        Assert.Equal(0, await LeaseAsync(a, GrandPeco));
        await a.SendGameAsync(MountService.Ride, new PlayerRiderMountReq { Tp = eRiderType.OnBattle });
        Assert.Equal(0, (await Next(a, MountService.Ride, PlayerRiderMountResp.Parser)).Result);
        var riding = await Next(a, SkillService.SkillDataSync, DBSkillDataIncrement.Parser, m => m.CurrentAtkSkillId == 1001201);
        Assert.Equal(1001201, riding.SkillWheel.Inc.AtkSkillId);
    }

    // Wolf Taming: "When the Demonic Wolf is not mounted and follows the Ranger, it howls every 30 sec, increasing the
    // Ranger's Physical ATK by 5% for 10 sec" (BuffConfig 1332501 Warg Howl); a ridden wolf does not howl.
    [ClientDataFact]
    public async Task TheLeasedWolfHowlsWhileItFollowsUnmounted()
    {
        await using var a = await InWorld("Alice");
        await Become("alice", 3400, 99, 13310);
        Assert.Equal(0, await LeaseAsync(a, Wolf));
        var howl = await Next(a, BuffService.BuffUpdate, BuffUpdateNotify.Parser, p => p.Buffs.Any(b => b.ConfigId == 1332501));
        Assert.Equal(10_000, howl.Buffs.Single(b => b.ConfigId == 1332501).Duration);
    }

    // The client hides pendants by itself only on a system mount (LC_System_Pendant._onDoPendantMountState,
    // IsRideSystemMount), so a ridden wolf must be hidden here, or it follows its own rider.
    [ClientDataFact]
    public async Task TheRiddenWolfDoesNotAlsoFollowItsRider()
    {
        const int WolfPendant = 54001;
        await using (var a = await InWorld("Alice"))
        await using (var b = await InWorld("Bob"))
        {
            await Become("alice", 3400, 99, 13310);
            Assert.Equal(0, await LeaseAsync(a, Wolf));
            Assert.False(Pendant().IsHide);

            await a.SendGameAsync(MountService.Ride, new PlayerRiderMountReq { Tp = eRiderType.OnBattle });
            Assert.Equal(0, (await Next(a, MountService.Ride, PlayerRiderMountResp.Parser)).Result);
            foreach (var bot in new[] { a, b })
                await Next(bot, WorldEffectsService.Pendant, PendantUpdate.Parser,
                    p => p.EntityID == (long)a.Self!.Uuid && p.PendantInfo.TryGetValue(WolfPendant, out var d) && d.IsHide);
            Assert.True(Pendant().IsHide);

            await a.SendGameAsync(MountService.Ride, new PlayerRiderMountReq { Tp = eRiderType.OffBattle });
            Assert.Equal(0, (await Next(a, MountService.Ride, PlayerRiderMountResp.Parser)).Result);
            foreach (var bot in new[] { a, b })
                await Next(bot, WorldEffectsService.Pendant, PendantUpdate.Parser,
                    p => p.EntityID == (long)a.Self!.Uuid && p.PendantInfo.TryGetValue(WolfPendant, out var d) && !d.IsHide);
            Assert.False(Pendant().IsHide);

            await a.SendGameAsync(MountService.Ride, new PlayerRiderMountReq { Tp = eRiderType.OnBattle });
            Assert.Equal(0, (await Next(a, MountService.Ride, PlayerRiderMountResp.Parser)).Result);
        }
        await using var again = await InWorld("Alice", create: false);
        Assert.True(Pendant().IsHide); // the entity a viewer and a relogged rider get

        PendantData Pendant()
        {
            var entity = new EntityBase();
            CareerSpecialService.PopulateEntity(Session("alice"), entity);
            return entity.PendantInfo[WolfPendant];
        }
    }
    // Riding (Crusader): SpecialEffectConfig 16120051 [17, 2, 1131, 500], "increase Movement Speed by 5% while mounted".
    [ClientDataFact]
    public async Task RidingAddsItsMovementSpeedOnlyWhileAPecoIsRidden()
    {
        await using var a = await InWorld("Alice");
        await Become("alice", 1210, 30, 11407);
        long walking = await SpeedAsync();
        Assert.Equal(walking + 500, await RideAsync(a, GrandPeco)); // the leased Peco has no _kAttr of its own
        Assert.Contains(1131601, await BuffsAsync()); // Mounted State
        Assert.Equal(0, await CancelAsync(a, GrandPeco));
        Assert.Equal(walking, await SpeedAsync());
    }

    // Dragon Training level 1: [17, 6, 1131, 300] on the dragon and its BuffConfig 11318 row 1131801, whose tuples raise
    // Dragon Breath (Fire) 11304 and (Water) 11305 by 6%.
    [ClientDataFact]
    public async Task DragonTrainingStandsOnlyOnTheDragon()
    {
        await using var a = await InWorld("Alice");
        await Become("alice", 1400, 99, 11307);
        long walking = await SpeedAsync();
        Assert.Equal(walking + 300, await RideAsync(a, Dragon));
        var buffs = await BuffsAsync();
        Assert.Contains(1131801, buffs);
        Assert.Contains(1131901, buffs); // Mounted State-Dragon
        Assert.Equal(0, await CancelAsync(a, Dragon));
        Assert.DoesNotContain(1131801, await BuffsAsync());
    }

    [ClientDataFact]
    public async Task JobChangeRepairsALeaseTheNewCareerCannotHoldAndReleasesTheBattleSlot()
    {
        await using (var a = await InWorld("Alice"))
        {
            await Become("alice", 1210, 30, 11407);
            Assert.Equal(0, await LeaseAsync(a, GrandPeco));
            await Gm("alice", "!job 1310"); // Paladin: still in _kProfessionID
            Assert.Equal(GrandPeco, Session("alice").CareerSpecial!.MountsSlot.CartRemouldCfg);
            await Gm("alice", "!job 6100"); // Merchant
            await Next(a, CareerSpecialService.Sync, DBCareerSpecialIncrement.Parser,
                p => p.MountsSlot?.Inc != null && p.MountsSlot.Inc.HasCartRemouldCfg && p.MountsSlot.Inc.CartRemouldCfg == 0);
            Assert.Equal(0, Session("alice").CareerSpecial!.MountsSlot.CartRemouldCfg);
            Assert.Equal(0, Session("alice").Mounts!.MountIds[2].MountId);
        }
        await using var again = await InWorld("Alice", create: false);
        var restored = await Next(again, CareerSpecialService.Sync, DBCareerSpecialIncrement.Parser);
        Assert.Equal(0, restored.MountsSlot.All.CartRemouldCfg);
    }

    private static string RepoRoot()
    { for (var d = new DirectoryInfo(AppContext.BaseDirectory); d != null; d = d.Parent) if (File.Exists(Path.Combine(d.FullName, "Ro3Emu.slnx"))) return d.FullName; throw new DirectoryNotFoundException(); }
}

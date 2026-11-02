using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Romsg;
using Ro3Emu.Bot;
using Ro3Emu.Server.Data;
using Ro3Emu.Server.Data.Config;
using Ro3Emu.Server.Game;
using Ro3Emu.Server.Maps;

namespace Ro3Emu.Tests;

/// <summary>Channelled skills: hits along a rectangle ahead of the caster at each of the show's later attack points.</summary>
public sealed partial class SkillCombatTests
{
    private const int MyriadStars = 13320, MyriadStarsLv1 = 1332001, MyriadStarsLv5 = 1332005, MyriadStarsTalentLv5 = 1332015,
        MyriadStarsBuffLv1 = 1332301, AerialBombardmentLv3 = 1631803, ArrowStormLv5 = 1330205, FireWall = 1201001;

    private JsonElement SkillRow(int id) => SkillConfig.Get(_app.Services.GetRequiredService<GameData>(), id)!.Value.Row;

    /// <summary>Alice as a Ranger facing +X (face 90) with her own monsters only, placed at (ahead, beside) metres.</summary>
    private async Task<(BotClient Alice, MonsterUnit[] Monsters)> Ranger(params (float Ahead, float Beside)[] places)
    {
        var alice = await InWorld("Alice");
        await Gm("alice", "!job 3400"); await Gm("alice", "!lvl 99"); await Teach("alice", MyriadStars);
        var session = Session("alice");
        var world = _app.Services.GetRequiredService<World>();
        var monsters = new MonsterUnit[places.Length];
        for (int i = 0; i < places.Length; i++) monsters[i] = await Target("alice", stationary: true, npcId: 5000002);
        await Loop.InvokeAsync(() =>
        {
            foreach (var other in world.Map(session.WorldScene).Units.OfType<MonsterUnit>().Where(m => !monsters.Contains(m)))
            { other.X += 200; other.Z += 200; }
            for (int i = 0; i < places.Length; i++)
            {
                monsters[i].StaysOutOfBattle = true;
                monsters[i].X = session.Position.X + places[i].Ahead; monsters[i].Z = session.Position.Z - places[i].Beside;
                monsters[i].Hp = 100_000_000;
            }
            return Task.CompletedTask;
        });
        return (alice, monsters);
    }

    private static List<SkillHitInfo> Hits(BotClient bot, long session) =>
    [
        .. bot.Inbox.Snapshot().Where(e => e.IsGame(CombatService.SkillHit)).Select(e => SkillHitInfo.Parser.ParseFrom(e.Body))
            .Where(hit => hit.SessionId == session),
    ];

    private static async Task<long> CastMyriadStars(BotClient alice)
    {
        await alice.SendGameAsync(CombatService.UseSkill, new CastSkillReq { SkillId = MyriadStarsLv1, Face = 90 });
        var accepted = await Next(alice, CombatService.UseSkill, CastSkillRsp.Parser);
        Assert.Equal(0, accepted.Result);
        return accepted.SessionId;
    }

    // In-game report: "The Skill Myriad Stars Chase the Moon doesn't work, only the animation does."
    [ClientDataFact]
    public async Task MyriadStarsHitsAlongItsAimOncePerLaterAttackPointAndBuffsOnce()
    {
        var (alice, monsters) = await Ranger((10, 0), (17.5f, 2.5f), (8, 8), (-3, 0), (5, 3.5f), (19, 0));
        await using var _ = alice;
        var session = Session("alice");
        long cast = MonsterService.NowMs;
        long id = await CastMyriadStars(alice);
        await Next(alice, CombatService.SkillHit, SkillHitInfo.Parser, hit => hit.SessionId == id);
        var buff = await Loop.InvokeAsync(() => Task.FromResult(session.Buffs.Single(b => b.ConfigId == MyriadStarsBuffLv1)));
        Assert.InRange(buff.ExpireMs - cast, 1000, 2600); // 2 s from the cast's start

        var deadline = DateTime.UtcNow + Wait;
        while (Hits(alice, id).Count < 7 && DateTime.UtcNow < deadline) await Task.Delay(20);
        await Task.Delay(400);
        var hits = Hits(alice, id);

        Assert.Equal(7, hits.Count); // _kDamageParam1 [16000], "PATK * 1120%": seven hits of 160%
        foreach (var hit in hits)
        {
            Assert.Equal(1332007, hit.ShowPoint);
            Assert.Equal(2, hit.Split);
            // Inside the 18 m by 6 m rectangle ahead, not beside it (the old reading's 18 m width), behind or past it.
            Assert.Equal(new[] { monsters[0].Uid, monsters[1].Uid }.Order(), hit.Dsts.Select(d => d.DstID).Order());
        }
        // Not refreshed by the later hits: the one at 1.7 s would have left it running until 3.7 s.
        await Loop.InvokeAsync(() =>
        {
            Assert.DoesNotContain(session.Buffs, b => b.ConfigId == MyriadStarsBuffLv1 && b.ExpireMs != buff.ExpireMs);
            return Task.CompletedTask;
        });
    }

    [ClientDataFact]
    public async Task AnInterruptedMyriadStarsDealsNoMoreHits()
    {
        var (alice, _) = await Ranger((10, 0));
        await using var _a = alice;
        long id = await CastMyriadStars(alice);
        await Next(alice, CombatService.SkillHit, SkillHitInfo.Parser, hit => hit.SessionId == id);
        await alice.SendGameAsync(CombatService.SkillInterrupt, new SkillInterruptReq());
        await Next(alice, CombatService.SkillInterrupt, SkillInterruptResp.Parser);
        int atInterrupt = Hits(alice, id).Count;
        await Task.Delay(1600);
        Assert.InRange(Hits(alice, id).Count, atInterrupt, atInterrupt + 1); // one may have been on the wire
    }

    [ClientDataFact]
    public async Task MyriadStarsAreaFollowsItsWalkingCaster()
    {
        var (alice, _) = await Ranger((10, 0));
        await using var _a = alice;
        var session = Session("alice");
        long id = await CastMyriadStars(alice);
        await Next(alice, CombatService.SkillHit, SkillHitInfo.Parser, hit => hit.SessionId == id);
        // Past the monster, along the aim: the cast is not cancelled (_bMovement 1) but its area left the monster behind.
        await Loop.InvokeAsync(() => { session.Position.X += 15; Combat.PlayerMoved(session); return Task.CompletedTask; });
        int moved = Hits(alice, id).Count;
        await Task.Delay(1600);
        Assert.InRange(Hits(alice, id).Count, moved, moved + 1);
        Assert.True(Hits(alice, id).Count < 7);
    }

    [ClientDataFact]
    public void ChannelHitsAreTheShowsLaterPointsOfTheDescribedChannels()
    {
        var data = _app.Services.GetRequiredService<GameData>();
        (double, long)[] Hit(int id)
        {
            var row = SkillRow(id);
            return [.. CombatService.ChannelHits(row, data.SkillTemplate(new SkillConfig(row).ScriptShow))
                .Select(h => (h.Point, new SkillConfig(h.Row).DamageParam1[0]))];
        }
        void Total(int id, long hits, long described)
        {
            var row = new SkillConfig(SkillRow(id));
            var dealt = Hit(id);
            Assert.Equal(hits, dealt.Length);
            Assert.Equal(described, dealt.Sum(h => h.Item2) / 100);
            Assert.All(CombatService.ChannelHits(row.Row, data.SkillTemplate(row.ScriptShow)), h => Assert.Empty(new SkillConfig(h.Row).Buff1));
        }
        Total(MyriadStarsLv5, 7, 1400);        // "PATK * 1400%"
        Total(AerialBombardmentLv3, 7, 2100);  // "totaling PATK * 2100%"
        Total(MyriadStarsTalentLv5, 11, 2000 + 150); // its "2000" and "one instance of 150%" when the skill ends
        Assert.Equal(15000, Hit(MyriadStarsTalentLv5)[^1].Item2);
        Assert.Equal(0.5, Hit(MyriadStarsLv5)[0].Item1);
        Assert.Empty(Hit(ArrowStormLv5));
    }

    /// <summary>The offsets are (ahead, beside) metres from the caster, who faces +X.</summary>
    [ClientDataTheory]
    [InlineData(MyriadStarsLv5, 17.5f, 2.5f, 0, true)]   // Rect360 [1800,600]: from the caster along the aim
    [InlineData(MyriadStarsLv5, 0.2f, 0, 0, true)]
    [InlineData(MyriadStarsLv5, 18.5f, 0, 0, false)]
    [InlineData(MyriadStarsLv5, 5, 3.5f, 0, false)]
    [InlineData(MyriadStarsLv5, -2, 0, 0, false)]
    [InlineData(MyriadStarsLv5, 0, 8, 0, false)]
    [InlineData(FireWall, 4.4f, 1.4f, 4, true)]          // DragRect [100,300]: 1 m deep, 3 m across, on the aimed point
    [InlineData(FireWall, 5, 0, 4, false)]
    [InlineData(FireWall, 4, 2, 4, false)]
    public void RectanglesAreLengthAlongTheAimByWidth(int id, float ahead, float beside, float aim, bool inside)
    {
        var origin = new Vec3();
        var point = new Vec3 { X = ahead, Z = -beside };
        Assert.Equal(inside, CombatService.SkillAreaContains(SkillRow(id), origin, new Vec3 { X = aim }, 90, point));
    }

    [ClientDataFact]
    public void FixRectIsCentredOnItsForwardOffset()
    {
        // Guardian skill [1800,600,0,0,900], "18" by "6": centred 9 m ahead, so from the caster to 18 m.
        var row = _app.Services.GetRequiredService<GameData>().Table("SkillConfig").Values
            .First(r => new SkillConfig(r) is { ScriptLogic: "PetSkill_1_QiWangNvShen", Release: SkillRelease.FixRect });
        Assert.True(CombatService.SkillAreaContains(row, new Vec3(), new Vec3(), 90, new Vec3 { X = 17.5f, Z = 2.5f }));
        Assert.True(CombatService.SkillAreaContains(row, new Vec3(), new Vec3(), 90, new Vec3 { X = .5f }));
        Assert.False(CombatService.SkillAreaContains(row, new Vec3(), new Vec3(), 90, new Vec3 { X = -1 }));
        Assert.False(CombatService.SkillAreaContains(row, new Vec3(), new Vec3(), 90, new Vec3 { X = 9, Z = 4 }));
    }
}

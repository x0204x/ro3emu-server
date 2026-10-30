using System.Globalization;
using System.Text.Json;
using Romsg;
using Ro3Emu.Bot;
using Ro3Emu.Server.Game;
using Ro3Emu.Server.Maps;

namespace Ro3Emu.Tests;

/// <summary>以自造技能／彈道及下騎等待provider驗證共用施放生命週期，不讀取client資料。</summary>
public sealed class MountedProjectileTimingTests
{
    private const int Skill = 1997301, Group = 19973, Effect = 1997301;
    private const int ActionMs = 500, FlightMs = 900, DismountLeadMs = 600;
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(8);

    [Theory]
    [InlineData(0)]
    [InlineData(DismountLeadMs)]
    public async Task LeadAndCooldownRepeatKeepOneProjectileAliveUntilItsHit(int leadMs)
    {
        await using var f = await Fixture.StartAsync(leadMs);
        long originalHp = await f.Read(() => f.Target.Hp);
        await f.Bot.SendGameAsync(CombatService.UseSkill, f.Request());
        var accepted = await Next(f.Bot, CombatService.UseSkill, CastSkillRsp.Parser);
        Assert.Equal(0, accepted.Result);
        var original = await f.Read(() => f.Player.SkillCast);
        Assert.NotNull(original);
        await f.Bot.SendGameAsync(CombatService.UseSkill, f.Request());
        var refused = await Next(f.Bot, CombatService.UseSkill, CastSkillRsp.Parser);
        Assert.Equal(ErrorCodes.SkillCooldown, refused.Result);
        Assert.Equal(Skill, Assert.Single(refused.Params));
        Assert.Same(original, await f.Read(() => f.Player.SkillCast));
        Assert.False(original.Cancelled);

        if (leadMs > 0)
        {
            // 等待期間不能先播時間軸、先發彈道或先扣血；冷卻拒絕也不能送出中斷原施放的31103。
            await Task.Delay(leadMs / 3);
            Assert.DoesNotContain(f.Bot.Inbox.Snapshot(), e => e.IsGame(CombatService.UseInSkill)
                && UseInSkillInfo.Parser.ParseFrom(e.Body).SessionId == accepted.SessionId
                || e.IsGame(CombatService.SkillBullet) && SkillFixTimeBulletNotify.Parser.ParseFrom(e.Body).SkillConfigId == Skill
                || e.IsGame(CombatService.SkillHit) && SkillHitInfo.Parser.ParseFrom(e.Body).SessionId == accepted.SessionId);
            Assert.Equal(originalHp, await f.Read(() => f.Target.Hp));
        }
        Assert.DoesNotContain(f.Bot.Inbox.Snapshot(), e => e.IsGame(CombatService.SkillEnd)
            && SkillEndNotify.Parser.ParseFrom(e.Body).Result == ErrorCodes.SkillCooldown);
        var timeline = await Next(f.Bot, CombatService.UseInSkill, UseInSkillInfo.Parser, n => n.SessionId == accepted.SessionId);
        Assert.True(timeline.StartTime >= f.BeforeCastAt + leadMs, "時間軸早於下騎等待結束。");
        var bullet = await Next(f.Bot, CombatService.SkillBullet, SkillFixTimeBulletNotify.Parser, n => n.SkillConfigId == Skill);
        Assert.Equal(Effect, bullet.EffectId);
        Assert.Equal(FlightMs, bullet.FixedTime);
        Assert.True(bullet.StartTimestampMs >= timeline.StartTime, "彈道早於施放時間軸。");
        Assert.True(MonsterService.NowMs >= f.BeforeCastAt + leadMs, "等待結束前已發彈道。");

        // 此自造彈道飛行比動作長；動作尾端後、落地前仍須保留cast，不能先清除導致命中被Live拒絕。
        await Task.Delay(ActionMs * 2 / 3);
        Assert.True(MonsterService.NowMs < bullet.StartTimestampMs + FlightMs, "測試未取得落地前的觀察窗口。");
        Assert.Same(original, await f.Read(() => f.Player.SkillCast));
        Assert.Equal(originalHp, await f.Read(() => f.Target.Hp));
        var hit = await Next(f.Bot, CombatService.SkillHit, SkillHitInfo.Parser, n => n.SessionId == accepted.SessionId);
        Assert.Contains(hit.Dsts, n => n.DstID == f.Target.Uid && n.Damage > 0);
        Assert.True(MonsterService.NowMs >= bullet.StartTimestampMs + FlightMs, "彈道落地前已命中。");
        await f.Until(() => f.Player.SkillCast == null);
        Assert.True(await f.Read(() => f.Target.Hp < originalHp));
        Assert.Single(f.Bot.Inbox.Snapshot(), e => e.IsGame(CombatService.UseInSkill)
            && UseInSkillInfo.Parser.ParseFrom(e.Body).SessionId == accepted.SessionId);
        Assert.Single(f.Bot.Inbox.Snapshot(), e => e.IsGame(CombatService.SkillBullet)
            && SkillFixTimeBulletNotify.Parser.ParseFrom(e.Body).SkillConfigId == Skill);
        Assert.Single(f.Bot.Inbox.Snapshot(), e => e.IsGame(CombatService.SkillHit)
            && SkillHitInfo.Parser.ParseFrom(e.Body).SessionId == accepted.SessionId);
    }

    [Fact]
    public async Task InterruptDuringDismountWaitSuppressesDelayedTimelineProjectileAndHit()
    {
        await using var f = await Fixture.StartAsync(DismountLeadMs);
        long hp = await f.Read(() => f.Target.Hp);
        await f.Bot.SendGameAsync(CombatService.UseSkill, f.Request());
        var accepted = await Next(f.Bot, CombatService.UseSkill, CastSkillRsp.Parser);
        Assert.Equal(0, accepted.Result);
        await f.Bot.SendGameAsync(CombatService.SkillInterrupt, new SkillInterruptReq { InterruptSkillId = Skill });
        Assert.True((await Next(f.Bot, CombatService.SkillInterrupt, SkillInterruptResp.Parser)).IsInterruptSucc);
        var end = await Next(f.Bot, CombatService.SkillEnd, SkillEndNotify.Parser, n => n.SessionId == accepted.SessionId);
        Assert.True(end.ClearEffect);
        await Task.Delay(DismountLeadMs + ActionMs + FlightMs + 200);
        Assert.Null(await f.Read(() => f.Player.SkillCast));
        Assert.Equal(hp, await f.Read(() => f.Target.Hp));
        Assert.DoesNotContain(f.Bot.Inbox.Snapshot(), e => e.IsGame(CombatService.UseInSkill)
            && UseInSkillInfo.Parser.ParseFrom(e.Body).SessionId == accepted.SessionId
            || e.IsGame(CombatService.SkillBullet) && SkillFixTimeBulletNotify.Parser.ParseFrom(e.Body).SkillConfigId == Skill
            || e.IsGame(CombatService.SkillHit) && SkillHitInfo.Parser.ParseFrom(e.Body).SessionId == accepted.SessionId);
    }

    private static async Task<T> Next<T>(BotClient bot, uint id, Google.Protobuf.MessageParser<T> parser, Func<T, bool>? match = null)
        where T : Google.Protobuf.IMessage<T>
    {
        var packet = await bot.Inbox.WaitAsync(e => e.IsGame(id) && (match == null || match(parser.ParseFrom(e.Body))), Wait);
        return parser.ParseFrom(packet.Body);
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "ro3emu-mounted-projectile-" + Guid.NewGuid().ToString("N"));
        private SyntheticHost _host = null!;
        public BotClient Bot { get; private set; } = null!;
        public GameSession Player => _host.Service<SessionRegistry>().All().Single(s => s.Role != null);
        public MonsterUnit Target { get; private set; } = null!;
        public long BeforeCastAt { get; private set; }

        public static async Task<Fixture> StartAsync(int leadMs)
        {
            var f = new Fixture();
            f.WriteData();
            f._host = await SyntheticHost.StartAsync(o => o.GameDataDir = f._root);
            SureHitRolls.Install(f._host.App);
            f.Bot = await f._host.InWorldAsync("Projectile");
            await f._host.Service<GameLoop>().InvokeAsync(async () =>
            {
                f.Player.Progress!.Skills[Group] = 1;
                var character = f._host.Service<CharacterService>();
                character.DynamicProperties.Add(_ => [(101, 100000), (2000, 100000)]);
                await character.RefreshStatsAsync(f.Player);
                var monsters = f._host.Service<MonsterService>();
                var template = Assert.IsType<MonsterTemplate>(monsters.Template(16320001)) with
                { RoamDistance = 0, SurroundDistance = 0, SpeedCm = 0 };
                f.Target = Assert.IsType<MonsterUnit>(await monsters.SpawnSingleAsync(f.Player.SceneEntry, 16320001,
                    new MapPoint(f.Player.Position.X, f.Player.Position.Y, f.Player.Position.Z, 0),
                    f.Player.InstanceId, customTemplate: template));
                f._host.Service<CombatService>().BeforeCast = _ =>
                {
                    f.BeforeCastAt = MonsterService.NowMs;
                    return Task.FromResult(leadMs);
                };
            });
            return f;
        }

        public CastSkillReq Request() => new() { SkillId = Skill, TargetId = Target.Uid };
        public Task<T> Read<T>(Func<T> read) => _host.Service<GameLoop>().InvokeAsync(() => Task.FromResult(read()));
        public async Task Until(Func<bool> ready)
        {
            var deadline = DateTime.UtcNow + Wait;
            while (!await Read(ready))
            {
                Assert.True(DateTime.UtcNow < deadline, "彈道命中後未清除施放。");
                await Task.Delay(20);
            }
        }

        private void WriteData()
        {
            foreach (string file in Directory.EnumerateFiles(SyntheticData.Dir, "*", SearchOption.AllDirectories))
            {
                string target = Path.Combine(_root, Path.GetRelativePath(SyntheticData.Dir, file));
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.Copy(file, target);
            }
            Write("config/SkillConfig.json", new Dictionary<string, object>
            {
                [Skill.ToString(CultureInfo.InvariantCulture)] = new
                {
                    _iID = Skill, _iSkillID = Group, _iLevel = 1, _iBasicType = 1, _iRelease = 1, _iDmgType = 1,
                    _iDistanceMax = 2000, _iActionTime = ActionMs, _iCD = 10000, _bCancelCast = true,
                    _iScriptShow = Group, _kDamageParam2 = new[] { 50 },
                },
            });
            Write("skills/skill_19973.json", new Dictionary<string, object>
            {
                ["fixture"] = new { m_kAtkPoints = new[] { 0.2 }, m_kState = new { Time = new[] { 0d, 0d, 0.5 } } },
            });
            Write("skills/Lua_Config_EffectData.json", new Dictionary<string, object>
            {
                [Effect.ToString(CultureInfo.InvariantCulture)] = new
                {
                    m_kDamage = new
                    {
                        m_kDamageType = 2,
                        m_kBulletData = new { m_kBulletName = "invented_projectile", m_iBulletControlType = 0, m_fBulletLifeTime = FlightMs },
                    },
                },
            });
        }

        private void Write(string name, object value)
        {
            string path = Path.Combine(_root, name);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, JsonSerializer.Serialize(value));
        }

        public async ValueTask DisposeAsync()
        {
            await Bot.DisposeAsync();
            await _host.DisposeAsync();
            Directory.Delete(_root, true);
        }
    }
}

using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;
using Romsg;
using Ro3Emu.Bot;
using Ro3Emu.Server.Accounts;
using Ro3Emu.Server.Game;
using Ro3Emu.Server.Maps;

namespace Ro3Emu.Tests;

/// <summary>
/// 自造資料驗證目前伺服器野外組隊 EXP 管線與重送冪等性；4／4／3 是本地推定政策，
/// 並非原服分配公式或遠距資格的驗收證據。本案直接呼叫正式 RewardService，
/// 未模擬玩家施放技能或命中封包。
/// </summary>
public sealed partial class PartyExperiencePipelineTests
{
    private const int Npc = 990301, Multiverse = 990302, NpcDrop = 990303, Pool = 990304, Curve = 3007;
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(15);

    [Fact]
    public async Task SyntheticDropChainCommitsThreeMemberBaseAndJobSharesOnce()
    {
        string fixture = CreateFixture();
        try
        {
            await using var host = await SyntheticHost.StartAsync(options => options.GameDataDir = fixture);
            await using var alice = await host.InWorldAsync("PipelineAlice");
            await using var bob = await host.InWorldAsync("PipelineBob");
            await using var carol = await host.InWorldAsync("PipelineCarol");
            await CreateParty(alice, bob, carol);

            var bots = new[] { alice, bob, carol };
            var roleIds = bots.Select(bot => (long)bot.Self!.Uuid).ToArray();
            var sessions = roleIds.Select(id => Session(host, id)).ToArray();
            var before = roleIds.Select(id => Progress(host, id)).ToArray();
            var syncBefore = bots.Select(bot => SyncCount(bot)).ToArray();
            Assert.All(sessions, session => Assert.Equal(SyntheticData.Town, session.SceneEntry));
            Assert.Equal(3, host.Service<TeamService>().ExperienceRecipients(sessions[0]).Count);

            var monster = new MonsterUnit
            {
                Uid = 990305,
                SceneEntry = sessions[0].SceneEntry,
                RuntimeMap = sessions[0].RuntimeMap,
                Template = new MonsterTemplate(Npc, 1, 10, 0, 1, 1, 0, 0, 1, 0, 0, 100, 1, 1, 1, 1,
                    false, 0, 1000, 5, 10, 0, 1000, .1f, 1000, 1, 500),
                Group = new SpawnGroup(-1, false, 1, 0, [], []),
                Home = new SpawnPoint(-1, 0, 0, 0, 0, Npc),
                Hp = 0,
            };
            var reward = host.Service<RewardService>();
            await host.Service<GameLoop>().InvokeAsync(async () =>
            {
                await reward.PrepareKilledAsync(sessions[0], monster);
                await reward.KilledAsync(sessions[0], monster);
            });

            string source = Assert.IsType<RewardSourceId>(monster.RewardSource).Key;
            Assert.True(await host.Service<CharacterService>().RewardSourceCommittedAsync(source));
            Assert.Equal(new long[] { 4, 4, 3 }, roleIds.Select((id, index) => Progress(host, id).Base - before[index].Base));
            Assert.Equal(new long[] { 4, 4, 3 }, roleIds.Select((id, index) => Progress(host, id).Job - before[index].Job));
            Assert.Equal(new long[] { 4, 4, 3 }, sessions.Select((session, index) => session.Progress!.Exp - before[index].Base));
            Assert.Equal(new long[] { 4, 4, 3 }, sessions.Select((session, index) => session.Progress!.JobExp - before[index].Job));
            for (int i = 0; i < bots.Length; i++)
            {
                await bots[i].HeartbeatAsync();
                var expected = Progress(host, roleIds[i]);
                var updates = bots[i].Inbox.Snapshot()
                    .Where(packet => packet.IsGame(CharacterService.RoleBaseInfoSync))
                    .Skip(syncBefore[i])
                    .Select(packet => RoleBaseInfoIncrement.Parser.ParseFrom(packet.Body));
                Assert.Contains(updates, update => update.HasExp && update.HasJobExp
                    && update.Exp == expected.Base && update.JobExp == expected.Job);
            }

            var obligations = Obligations(host, source);
            Assert.Equal(2, obligations.Count);
            Assert.Equal(new long[] { roleIds[1], roleIds[2] }.OrderBy(id => id),
                obligations.Select(row => row.Role));
            Assert.Equal(new long[] { 4, 3 }, obligations.Select(row => row.Base));
            Assert.Equal(new long[] { 4, 3 }, obligations.Select(row => row.Job));
            Assert.All(obligations, row => Assert.True(row.Delivered));

            // 同一 MonsterUnit／來源再次進入預備與結算，不可新增義務或重新發獎。
            var settled = roleIds.Select(id => Progress(host, id)).ToArray();
            await host.Service<GameLoop>().InvokeAsync(async () =>
            {
                await reward.PrepareKilledAsync(sessions[0], monster);
                await reward.KilledAsync(sessions[0], monster);
            });
            Assert.Equal(settled, roleIds.Select(id => Progress(host, id)).ToArray());
            Assert.Equal(obligations.ToArray(), Obligations(host, source).ToArray());
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(fixture, true);
        }
    }

    private static (long Base, long Job) Progress(SyntheticHost host, long role)
    {
        var progress = host.Service<RoleStore>().LoadProgress(role);
        // 測試的每人份額皆低於自造首級門檻，因此仍在第 1 級。
        Assert.Equal(1, progress.Level);
        Assert.Equal(1, progress.JobLevel);
        return (progress.Exp, progress.JobExp);
    }

    private static int SyncCount(BotClient bot) => bot.Inbox.Snapshot()
        .Count(packet => packet.IsGame(CharacterService.RoleBaseInfoSync));

    private static GameSession Session(SyntheticHost host, long role) => host.Service<SessionRegistry>().All()
        .Single(session => session.Role?.Id == role);

    private static List<(long Role, long Base, long Job, bool Delivered)> Obligations(SyntheticHost host, string source)
    {
        using var db = host.Service<AccountStore>().Open();
        using var command = db.CreateCommand();
        command.CommandText = """
            SELECT role_id, base_exp, job_exp, delivered
            FROM party_experience_obligation
            WHERE source_key LIKE $source
            ORDER BY role_id
            """;
        command.Parameters.AddWithValue("$source", source + "/party-exp/%");
        using var rows = command.ExecuteReader();
        var result = new List<(long Role, long Base, long Job, bool Delivered)>();
        while (rows.Read()) result.Add((rows.GetInt64(0), rows.GetInt64(1), rows.GetInt64(2), rows.GetInt64(3) == 1));
        return result;
    }

    private static async Task CreateParty(BotClient leader, params BotClient[] others)
    {
        await leader.SendGameAsync(TeamService.TeamCreate, new TeamCreateReq { TeamType = TeamType.Five, TargetId = 1001 });
        var created = await Next(leader, TeamService.TeamCreate, TeamCreateResp.Parser);
        Assert.Equal(0, created.Result);
        foreach (var member in others)
        {
            await leader.SendGameAsync(TeamService.TeamInvite, new InviteIntoTeamReq { Guid = (long)member.Self!.Uuid });
            Assert.Equal(0, (await Next(leader, TeamService.TeamInvite, InviteIntoTeamResp.Parser)).Result);
            Assert.Equal((long)leader.Self!.Uuid,
                (await Next(member, TeamService.TeamNotifyInvitee, NotifyInvitee.Parser)).InviteGuid);
            await member.SendGameAsync(TeamService.TeamInviteeResp, new InviteeResp
            {
                TeamId = created.TeamId, IsAgree = true, InviteGuid = (long)leader.Self.Uuid
            });
            Assert.Equal(0, (await Next(member, TeamService.TeamInviteeResp, InviteeRespResp.Parser)).Result);
        }
    }

    private static async Task<T> Next<T>(BotClient bot, uint opcode, Google.Protobuf.MessageParser<T> parser)
        where T : Google.Protobuf.IMessage<T> => parser.ParseFrom(
            (await bot.Inbox.WaitAsync(packet => packet.IsGame(opcode), Wait)).Body);

    private static string CreateFixture()
    {
        string root = Path.Combine(Path.GetTempPath(), "ro3emu-party-pipeline-" + Guid.NewGuid().ToString("N"));
        foreach (string source in Directory.EnumerateFiles(SyntheticData.Dir, "*", SearchOption.AllDirectories))
        {
            string target = Path.Combine(root, Path.GetRelativePath(SyntheticData.Dir, source));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(source, target);
        }

        // 全部數值為本測試自造；只有欄位結構沿用服務端配置讀取契約。
        string config = Path.Combine(root, "config");
        var map = JsonNode.Parse(File.ReadAllText(Path.Combine(config, "MapConfig.json")))!.AsObject();
        map[SyntheticData.Town.ToString(CultureInfo.InvariantCulture)]!["_iSceneType"] = 1;
        File.WriteAllText(Path.Combine(config, "MapConfig.json"), map.ToJsonString());
        var publicParams = JsonNode.Parse(File.ReadAllText(Path.Combine(config, "PublicParamConfig.json")))!.AsObject();
        publicParams["TeamExpBuffNum"] = JsonSerializer.SerializeToNode(new[] { 10000, 12000, 17000, 19000, 21000 });
        File.WriteAllText(Path.Combine(config, "PublicParamConfig.json"), publicParams.ToJsonString());
        Append(config, "NPCConfig", Npc, new { _iID = Npc, _iNPCType = 1, _iLevel = 1, _iDrop = Multiverse });
        Append(config, "MultiverseGroupConfig", Multiverse,
            new { _iID = Multiverse, _itype = 0, _iValue_default = NpcDrop });
        Append(config, "NPCDropConfig", NpcDrop, new Dictionary<string, object>
        {
            ["0"] = new { _iDropPool1 = Pool }
        });
        Append(config, "DropPoolConfig", Pool,
            new { _iID = Pool, _iDropGroup_1 = new long[] { Curve, 1_000_000, 1, 1 } });
        Append(config, "DropGroupConfig", Curve, new[]
        {
            new { _iWeights = 1, _iMinLevelLimit = 1, _iMaxLevelLimit = 5,
                _iMinNumber = 7, _iMaxNumber = 7 }
        });
        Append(config, "LvExpCorrectionConfig", 1,
            new { _iID = 1, _iBaseLvGap = 0, _iLvDifferenceCorrection = 10000 });
        Append(config, "JobLvConfig", 101,
            new { _iID = 101, _iMaxJobLv = 10, _iJobeExp = 100 });
        return root;
    }

    private static void Append(string config, string table, int key, object row)
    {
        string path = Path.Combine(config, table + ".json");
        var content = File.Exists(path) ? JsonNode.Parse(File.ReadAllText(path))!.AsObject() : new JsonObject();
        content.Add(key.ToString(CultureInfo.InvariantCulture), JsonSerializer.SerializeToNode(row));
        File.WriteAllText(path, content.ToJsonString());
    }
}

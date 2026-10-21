using System.Collections.Concurrent;
using System.Text.Json;
using Google.Protobuf;
using Microsoft.AspNetCore.Builder;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Romsg;
using Ro3Emu.Bot;
using Ro3Emu.Server;
using Ro3Emu.Server.Accounts;
using Ro3Emu.Server.Game;
using Ro3Emu.Server.Game.Ownership;

namespace Ro3Emu.Tests;

/// <summary>
/// Pending payout obligations of every activity on the shared component, as a database made before it holds them
/// (Mock Battle, MVP, timed battle rows written in their old shape while the role is offline): the role select
/// reserves each one, the login pays each exactly once, and a second restart pays nothing again.
/// </summary>
public sealed class RoleObligationsLoginTests : IAsyncLifetime
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(15);
    private readonly string _dataDir = Path.Combine(Path.GetTempPath(), "ro3emu-obligations-" + Guid.NewGuid().ToString("N"));
    private WebApplication _app = null!;
    private string _http = "";

    public Task InitializeAsync() => StartAsync();

    private async Task StartAsync()
    {
        var (httpPort, microPort, gamePort) = TestPorts.FreePorts();
        _app = ServerHost.Create([], o =>
        {
            o.DataDir = _dataDir;
            o.GameDataDir = Path.Combine(ClientData.RepoRoot, "data");
            o.HttpPort = httpPort; o.MicroGatePort = microPort; o.GameGatePort = gamePort;
        });
        await _app.StartAsync();
        _http = $"http://127.0.0.1:{httpPort}";
    }

    private async Task StopAsync()
    {
        await _app.StopAsync();
        await _app.DisposeAsync();
        SqliteConnection.ClearAllPools();
    }

    public async Task DisposeAsync()
    {
        await StopAsync();
        try { Directory.Delete(_dataDir, true); } catch (IOException) { }
    }

    private BotClient Bot() => new(_http, File.ReadAllText(Path.Combine(_dataDir, "gate_rsa_public.pem")));

    private SqliteConnection Db()
    {
        var db = new SqliteConnection($"Data Source={Path.Combine(_dataDir, "ro3emu.db")};Pooling=False");
        db.Open();
        return db;
    }

    private long Scalar(string sql)
    {
        using var db = Db();
        using var cmd = db.CreateCommand();
        cmd.CommandText = sql;
        return (long)cmd.ExecuteScalar()!;
    }

    private void Exec(SqliteConnection db, string sql, params (string Name, object Value)[] args)
    {
        using var cmd = db.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (name, value) in args) cmd.Parameters.AddWithValue(name, value);
        Assert.Equal(1, cmd.ExecuteNonQuery());
    }

    [ClientDataFact]
    public async Task EveryActivitysPendingRowFromAnOldDatabaseIsReservedAndPaidOnceAtLogin()
    {
        long role;
        await using (var bot = Bot())
        {
            await bot.LoginAsync("relog", "pw");
            await bot.EnterGateAsync();
            role = (await bot.CreateRoleAsync("Relog")).RoleBaseInfo.Id;
            await bot.EnterWorldAsync(role);
        }
        await StopAsync();

        // Offline, rows in the shape the per-activity plans wrote (the MVP and timed-battle ones with the revision row
        // their create plans bumped).
        const long match = 4242;
        using (var db = Db())
        {
            var payout = new CasualBattleService.Payout
            {
                MatchId = match, RoleId = role, BattleTime = 1, ResultId = "4242", Won = true, Outcome = 1,
                StartMmr = 1000, MmrDelta = 5, Stats = new TimedBattleService.CasualStats { PersonalScore = 40 },
            };
            Exec(db, "INSERT INTO casual_battle_obligation(source_key,match_id,role_id,payout) VALUES($s,$m,$r,$p)",
                ("$s", CasualBattleService.SourceKey(match, role)), ("$m", match), ("$r", role),
                ("$p", JsonSerializer.SerializeToUtf8Bytes(payout)));
            string mvp = $"mvp/kill/1/1000/{role}";
            Exec(db, """
                INSERT INTO mvp_award_obligation(source_key,cfg_id,death_ms,player_id,awards,result,delivered)
                VALUES($s,1,1000,$r,$a,$n,0)
                """, ("$s", mvp), ("$r", role), ("$a", "[]"u8.ToArray()),
                ("$n", new MVPResultNtf { CfgId = 1 }.ToByteArray()));
            Exec(db, "INSERT INTO owner_revision(kind,id,part,revision) VALUES('mvp_obligation',$s,'state',1)", ("$s", mvp));
            string timed = $"timed_battle/{match}/{role}";
            Exec(db, """
                INSERT INTO timed_battle_obligation(source_key,match_id,entry,role_id,grants,costs,next_state,result)
                VALUES($s,$m,2,$r,$g,$c,$n,$x)
                """, ("$s", timed), ("$m", match), ("$r", role), ("$g", "[]"u8.ToArray()), ("$c", "[]"u8.ToArray()),
                ("$n", "{}"u8.ToArray()), ("$x", new ChickenResultNotify().ToByteArray()));
            Exec(db, "INSERT INTO owner_revision(kind,id,part,revision) VALUES('timed_battle_obligation',$s,'state',1)", ("$s", timed));
        }

        for (int restart = 0; restart < 2; restart++)
        {
            await StartAsync();
            var audit = new ConcurrentQueue<string>();
            _app.Services.GetRequiredService<OwnerRegistry>().UncoveredForTest = audit.Enqueue;
            await using (var bot = Bot())
            {
                await bot.LoginAsync("relog", "pw");
                await bot.EnterGateAsync();
                await bot.EnterWorldAsync(role);
                if (restart == 0)
                {
                    await bot.Inbox.WaitAsync(e => e.IsGame(MvpService.ResultNtf), Wait);
                    await bot.Inbox.WaitAsync(e => e.IsGame(TimedBattleService.ChickenResult), Wait);
                }
                else
                {
                    await Task.Delay(500);
                    Assert.DoesNotContain(bot.Inbox.Snapshot(), e => e.IsGame(MvpService.ResultNtf) || e.IsGame(TimedBattleService.ChickenResult));
                }
            }
            _app.Services.GetRequiredService<OwnerRegistry>().UncoveredForTest = null;
            // The role select reserved every pending row its login paid.
            Assert.Empty(audit);
            Assert.Equal(0, Scalar("SELECT count(*) FROM casual_battle_obligation WHERE delivered=0")
                + Scalar("SELECT count(*) FROM mvp_award_obligation WHERE delivered=0")
                + Scalar("SELECT count(*) FROM timed_battle_obligation WHERE delivered=0"));
            var state = JsonSerializer.Deserialize<TimedBattleService.State>(
                _app.Services.GetRequiredService<RoleStore>().LoadTimedBattle(role)!)!;
            Assert.Contains(match, state.CasualSettled);
            Assert.Single(state.CasualRecords);
            Assert.Equal(1005, state.CasualMmr);
            Assert.Equal(40, state.CasualTotal.PersonalScore);
            await StopAsync();
        }
        await StartAsync(); // for DisposeAsync
    }
}

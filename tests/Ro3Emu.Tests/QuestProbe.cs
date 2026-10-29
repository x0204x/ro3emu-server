using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Romsg;
using Ro3Emu.Server.Accounts;
using Ro3Emu.Server.Game;

namespace Ro3Emu.Tests;

/// <summary>Reads a role's quest state on the game loop, for the quest target tests of the feature fixtures.</summary>
internal static class QuestProbe
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(20);

    private static GameSession? Online(WebApplication app, string account) => app.Services.GetRequiredService<SessionRegistry>()
        .All().FirstOrDefault(s => s.Account == account && s.Role != null);

    /// <summary>The quest's row (a copy), null when the role is offline or has no such quest.</summary>
    public static Task<TaskInfo?> ReadAsync(WebApplication app, string account, long task) =>
        app.Services.GetRequiredService<GameLoop>().InvokeAsync(() =>
            Task.FromResult(Online(app, account)?.Tasks?.Infos.GetValueOrDefault(task)?.Clone()));

    public static async Task<TaskInfo> WaitAsync(WebApplication app, string account, long task, Func<TaskInfo, bool> match, string what)
    {
        var deadline = DateTime.UtcNow + Wait;
        while (true)
        {
            var info = await ReadAsync(app, account, task);
            if (info != null && match(info)) return info;
            if (DateTime.UtcNow > deadline) throw new TimeoutException($"{account} quest {task} {what}: {info}");
            await Task.Delay(25);
        }
    }

    /// <summary>Running (or finished) with its first target at <paramref name="progress"/>.</summary>
    public static Task<TaskInfo> AtAsync(WebApplication app, string account, long task, long progress) =>
        WaitAsync(app, account, task, i => i.Status is TaskState.Running or TaskState.Finish && i.CompleteNum[0] == progress,
            $"at {progress}");

    /// <summary>Every target met: finished (a claimed quest) or already delivered (an automatic one).</summary>
    public static Task<TaskInfo> DoneAsync(WebApplication app, string account, long task) =>
        WaitAsync(app, account, task, i => i.Status is TaskState.Finish or TaskState.Delivery, "done");

    /// <summary>等待已提交的完成次數；可重複任務可能已重新指派為 Running，不能只等 Finish／Delivery。</summary>
    public static async Task<TaskInfo> DeliveredAsync(WebApplication app, string account, long task, int count = 1)
    {
        var deadline = DateTime.UtcNow + Wait;
        while (true)
        {
            var value = await app.Services.GetRequiredService<GameLoop>().InvokeAsync(() =>
            {
                var session = Online(app, account);
                return Task.FromResult((Count: session?.Tasks?.CompleteTaskNum.GetValueOrDefault(task) ?? 0,
                    Info: session?.Tasks?.Infos.GetValueOrDefault(task)?.Clone()));
            });
            if (value.Count > count) throw new InvalidOperationException($"{account} quest {task}: expected {count} committed deliveries, got {value.Count}");
            if (value.Count == count && value.Info != null) return value.Info;
            if (DateTime.UtcNow > deadline) throw new TimeoutException($"{account} quest {task}: no committed delivery {count}");
            await Task.Delay(25);
        }
    }

    /// <summary>只計算該角色／任務實際提交的獎勵來源，不把重新指派的 Running 列當成未交付。</summary>
    public static long AwardSourceCount(WebApplication app, long role, long task)
    {
        using var db = app.Services.GetRequiredService<AccountStore>().Open();
        using var command = db.CreateCommand();
        command.CommandText = """
            SELECT COUNT(*) FROM operation_source s JOIN operation_receipt r ON r.operation_id=s.operation_id
            WHERE s.source_key LIKE $prefix AND r.command_kind='cd13e/quest-award'
            """;
        command.Parameters.AddWithValue("$prefix", $"task/{role}/{task}/%");
        return (long)command.ExecuteScalar()!;
    }

    public static async Task GmAsync(string http, string account, string command)
    {
        using var client = new HttpClient();
        (await client.PostAsync($"{http}/ro3emu/gm?account={account}", new StringContent(command))).EnsureSuccessStatusCode();
    }

    /// <summary>GM accept, then wait until the quest is in the role's state.</summary>
    public static async Task AcceptAsync(WebApplication app, string http, string account, long task)
    {
        await GmAsync(http, account, $"!quest accept {task}");
        await WaitAsync(app, account, task, _ => true, "accepted");
    }

    /// <summary>Wait until the account's role has left the world (before a relogin).</summary>
    public static async Task OfflineAsync(WebApplication app, string account)
    {
        var deadline = DateTime.UtcNow + Wait;
        while (await app.Services.GetRequiredService<GameLoop>().InvokeAsync(() => Task.FromResult(Online(app, account) != null)))
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException($"{account} is still online");
            await Task.Delay(25);
        }
        await Task.Delay(200);
    }
}

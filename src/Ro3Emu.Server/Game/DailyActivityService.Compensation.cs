using System.Text.Json;
using Google.Protobuf;
using Ro3Emu.Server.Data;
using Ro3Emu.Server.Data.Config;

namespace Ro3Emu.Server.Game;

/// <summary>
/// The Daily Quest Compensation Mail: PublicParamConfig.DailyTaskCompensationMail names MailConfig 1058, "Daily Quest
/// Compensation Mail" / "You didn't Claim the rewards from your Daily Quests Yesterday, so we've reissued them by mail.
/// Please check your inbox!" (text 10140000052 / 10140100063). The client only shows the letter; the server decides it.
/// (inferred) The rewards are the To-Do boxes (GenericStageRewardsConfig type 7) whose liveness threshold the ending day
/// reached and that were not claimed: the reset that ends the day rolls what each claim would have paid, at the level the
/// role had, and keeps it in <see cref="DailyState.Compensation"/> under the ended day's epoch; the letter then carries
/// it, once per day (delivery key daily-compensation/{role}/{epoch}), from the login or the running reset that follows.
/// A letter that cannot be delivered stays owed and is retried at the next login or reset.
/// </summary>
public sealed partial class DailyActivityService
{
    /// <summary>One owed good of a compensation letter (item, count, bind as the claim would have granted it).</summary>
    public sealed record CompensationGood(int Entry, int Count, int Bind);

    /// <summary>PublicParamConfig.DailyTaskCompensationMail (1058); 0 without it: nothing is owed then.</summary>
    private int CompensationMail => _data.Table("PublicParamConfig").TryGetValue("DailyTaskCompensationMail", out var value)
        && GameData.Number(value) is { } id and > 0 ? (int)id : 0;

    /// <summary>Before the ending day's To-Do claims are cleared: what its reached, unclaimed boxes owe.</summary>
    private void OweCompensation(GameSession session, DailyState state, long endedEpoch)
    {
        long liveness = session.Progress!.Currency.GetValueOrDefault(Liveness);
        if (liveness <= 0 || CompensationMail == 0 || state.Compensation.ContainsKey(endedEpoch)) return;
        List<(int Entry, int Count, int Bind)> goods = [];
        foreach (var stage in GenericStageRewardsConfig.All(_data))
            if (stage.Type == TodoStage && stage.Item == Liveness && stage.Score <= liveness
                && !state.Claims.Contains((int)stage.Id) && StageGrant(session, (int)stage.Id) is { } grant)
                goods.AddRange(grant);
        if (goods.Count == 0) return;
        state.Compensation[endedEpoch] = [.. goods.GroupBy(g => (g.Entry, g.Bind))
            .Select(g => new CompensationGood(g.Key.Entry, g.Sum(x => x.Count), g.Key.Bind))];
    }

    /// <summary>Delivers every owed compensation letter; each one removes its day from the saved state in its own commit.</summary>
    private async Task SendCompensationAsync(GameSession session)
    {
        if (session.Daily is not { Compensation.Count: > 0 } state || session.Role is not { } role || CompensationMail == 0) return;
        foreach (var (epoch, goods) in state.Compensation.OrderBy(kv => kv.Key).ToList())
        {
            var next = JsonSerializer.Deserialize<DailyState>(JsonSerializer.SerializeToUtf8Bytes(state))!;
            next.Compensation.Remove(epoch);
            next.Rows = Rows(session).ToByteArray();
            byte[] saved = JsonSerializer.SerializeToUtf8Bytes(next);
            try
            {
                await _mail.SendTemplateOnceAsync(role.Id, CompensationMail,
                    [.. goods.Select(g => new MailService.Attachment(g.Entry, g.Count, g.Bind))],
                    $"daily-compensation/{role.Id}/{epoch}", tx => tx.RoleBlob(role.Id, "daily", saved), session.Owners?.Current);
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                LogCompensationHeld(_log, e, role.Id, epoch);
                return;
            }
            // A letter delivered before (same key) skips its commit: the removal is saved here too.
            state.Compensation.Remove(epoch);
            Save(session);
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Daily compensation mail for role {Role}, day {Epoch}, stays owed")]
    private static partial void LogCompensationHeld(ILogger logger, Exception exception, long role, long epoch);
}

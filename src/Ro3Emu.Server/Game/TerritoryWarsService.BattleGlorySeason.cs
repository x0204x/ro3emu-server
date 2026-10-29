using Ro3Emu.Server.Accounts.Persistence;

namespace Ro3Emu.Server.Game;

/// <summary>
/// Battle Merit season settlement: when the Territory War season rolls over, the past season's standings on
/// leaderboard 2102 (earned points, ties by role id, inferred) are paid by MailConfig 1090 "Season Settlement: Battle
/// Merit Rewards" ("You finished ${1} on this Season's Territory War Merit Rankings"), its ${1} the place. The goods are
/// the SiegeSettlementHonorAward row whose _kRank holds the place: UI_Sys_TerritoryWars_BattleFeatsSettlementPanelCtrl
/// shows that table's award list for m_iBattleGloryRank (GetConfigIdByRank / GetAwardListById), so places 1-50 are
/// paid and the rest get no letter. Sending the letters at the rollover, once per role and season (delivery key), is
/// inferred: the client only shows the result. WorldState.GlorySettle keeps the seasons still to pay, so a failed or
/// interrupted settlement is retried by the reconciliation.
/// </summary>
public sealed partial class TerritoryWarsService
{
    public const int GlorySeasonMail = 1090;
    private bool _glorySettling;
    private long _glorySettleRetryAt;

    /// <summary>Adds a past season to the settlement list (the reconciliation mails it).</summary>
    internal async Task QueueGlorySeasonAsync(string epoch)
    {
        using var reservation = await EnterGateAsync("territory/battle merit season");
        using var stagedTerritoryState = StageState();
        try
        {
            if (epoch.Length == 0 || state.GlorySettle.Contains(epoch)) return;
            state.GlorySettle.Add(epoch);
            await SaveAsync(state);
        }
        finally { stagedTerritoryState.Dispose(); _gate.Release(); reservation?.Dispose(); }
    }

    /// <summary>Outside the territory gate: mails each pending season, then drops it from the pending list.</summary>
    internal async Task SettleGlorySeasonsAsync()
    {
        if (_glorySettling || state.GlorySettle.Count == 0 || _clock.Now < _glorySettleRetryAt) return;
        _glorySettling = true;
        try
        {
            foreach (string epoch in state.GlorySettle.ToArray())
            {
                if (!await MailGlorySeasonAsync(epoch))
                {
                    _glorySettleRetryAt = _clock.Now + 60; // retried later; delivered letters are skipped by their keys
                    return;
                }
                using var reservation = await EnterGateAsync("territory/battle merit season");
                using var stagedTerritoryState = StageState();
                try { if (state.GlorySettle.Remove(epoch)) await SaveAsync(state); }
                finally { stagedTerritoryState.Dispose(); _gate.Release(); reservation?.Dispose(); }
            }
        }
        finally { _glorySettling = false; }
    }

    /// <summary>A past season's standings from the saved roles (online ones save every award): place, role.</summary>
    internal async Task<IReadOnlyList<(int Place, long Role, long Points)>> GloryStandingsAsync(string epoch)
    {
        var rows = await _accounts.ReadAsync<List<(long Role, byte[] State)>>(new GloryStandingsRead());
        return rows.Select(r => (r.Role, Points: GloryPointsOf(r.State, epoch))).Where(r => r.Points > 0)
            .OrderByDescending(r => r.Points).ThenBy(r => r.Role)
            .Select((r, i) => (i + 1, r.Role, r.Points)).ToList();
    }

    private async Task<bool> MailGlorySeasonAsync(string epoch)
    {
        foreach (var (place, role, _) in await GloryStandingsAsync(epoch))
        {
            var recipient = _sessions.All().FirstOrDefault(s => s.Role?.Id == role)
                ?? new GameSession(null!, null!) { Role = _roles.Get(role), Progress = _roles.LoadProgress(role) };
            if (recipient.Role == null) continue;
            var goods = new List<Grant>();
            AddRankAward(goods, "SiegeSettlementHonorAward", place, recipient);
            if (goods.Count == 0) continue;
            var attachments = goods.GroupBy(g => (g.Entry, g.Bind))
                .Select(g => new MailService.Attachment(g.Key.Entry, checked(g.Sum(x => x.Count)), g.Key.Bind)).ToArray();
            try
            {
                using var scope = _loop.Owners is { } owners
                    ? await owners.EnterAsync("territory/battle merit season mail", Ownership.ReservationKey.Role(role)) : null;
                await _mail.SendTemplateOnceAsync(role, GlorySeasonMail, attachments, $"battle-merit-season:{epoch}:{role}",
                    _ => { }, scope?.Context, contentParams: [place]);
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                LogGlorySeasonMailFailed(_log, e, epoch, role);
                return false;
            }
        }
        return true;
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "Battle Merit season {Epoch} mail to role {Role} failed")]
    private static partial void LogGlorySeasonMailFailed(ILogger logger, Exception error, string epoch, long role);
}

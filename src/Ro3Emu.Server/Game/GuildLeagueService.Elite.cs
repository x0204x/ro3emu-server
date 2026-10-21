using Ro3Emu.Server.Accounts;

namespace Ro3Emu.Server.Game;

/// <summary>
/// The league's side of the elite roster (GuildService.LeagueElite): the reminder mail before the first match and the
/// auto-fill when a round opens.
/// </summary>
public sealed partial class GuildLeagueService
{
    private (int Index, int Round) _filledRound;
    private int _mailedCup;

    private bool ElitesDue(long now) => FillDue(now) != 0 || MailDue(now);

    /// <summary>The round that just opened for the registered tournament and is not filled yet (0 otherwise).</summary>
    private int FillDue(long now)
    {
        if (_state.Cup is not { Settled: false } cup || _calendar.Current(now) is not { } t || t.Index != cup.Index) return 0;
        int round = GuildLeagueCalendar.OpenRound(t, now);
        return round > 0 && _filledRound != (cup.Index, round) ? round : 0;
    }

    /// <summary>GuildSettingConfig GuildLeagueEliteSettingMailTime (7200 s) before the preliminaries start.</summary>
    private bool MailDue(long now) => _state.Cup is { Settled: false } cup && _mailedCup != cup.Index && cup.Windows.Length > 0
        && now >= cup.Windows[0] - Setting("GuildLeagueEliteSettingMailTime", 7200) && now < cup.Windows[0];

    private IEnumerable<long> CupGuilds(Cup cup) => cup.Groups.SelectMany(g => g.Pairs).SelectMany(p => p).Where(id => id > 0).Distinct();

    private async Task ElitesAsync(long now)
    {
        if (MailDue(now) && _state.Cup is { } mailCup)
        {
            await EliteMailAsync(mailCup);
            _mailedCup = mailCup.Index;
        }
        if (FillDue(now) is > 0 and var round && _state.Cup is { } cup)
        {
            foreach (long guild in CupGuilds(cup)) await FillElitesAsync(guild);
            _filledRound = (cup.Index, round);
        }
    }

    /// <summary>
    /// (inferred) Who the auto-fill may pick: the Main Battlefield's positions (GuildLeagueMainSceneNeedPosition) and no
    /// temporary member (517), so that every filled elite can enter.
    /// </summary>
    private Task<int> FillElitesAsync(long guild)
    {
        long[] positions = SettingArray("GuildLeagueMainSceneNeedPosition");
        return _guildService.FillLeagueElitesAsync(guild, m => !Temporary(m) && (positions.Length == 0 || positions.Contains(m.Official)));
    }

    /// <summary>
    /// Mail GuildLeagueEliteSettingMailID (1125, "Guild League Elite Settings Reminder", text 10140100124) to the holders of
    /// EliteSetting (GuildPermissionConfig 20) of every placed guild. (inferred) Only guilds whose roster is short of
    /// GuildLeagueEliteNum are reminded; the delivery key makes it once per tournament and holder.
    /// </summary>
    private async Task EliteMailAsync(Cup cup)
    {
        int template = (int)Setting("GuildLeagueEliteSettingMailID", 1125);
        long wanted = Setting("GuildLeagueEliteNum", 40);
        var recipients = new List<long>();
        foreach (long id in CupGuilds(cup))
            if (_guilds.Get(id) is { } g && GuildService.LeagueElites(g).Count < wanted)
                recipients.AddRange(g.Members.Keys.Where(role => _guildService.HasRight(g, role, 20) && _roles.Get(role) != null));
        if (recipients.Count == 0) return;
        using (await _mail.LockRecipientsAsync(recipients))
        {
            var tx = _roles.NewTransaction();
            foreach (long role in recipients)
                _mail.QueueTemplateLocked(role, $"guild-league:{cup.Index}:elite-setting:{role}", template, [], tx);
            await _roles.CommitAsync(tx, "guild-league/elite-mail");
        }
        foreach (long role in recipients)
            try { await _mail.NotifyRecipientAsync(role); }
            catch (Exception e) { _log.LogWarning(e, "Guild League elite mail notice deferred for {Role}", role); }
    }

    /// <summary>GM aid: fill the caller's guild roster now, as the next round's opening would.</summary>
    private async Task<string> FillNowAsync(GameSession s)
    {
        if (s.Role == null || _guilds.ForRole(s.Role.Id) is not { } g) return "You are not in a guild.";
        int added = await FillElitesAsync(g.Id);
        return $"Elite roster of {g.Name}: {added} added, {GuildService.LeagueElites(_guilds.Get(g.Id)!).Count} in total.";
    }
}

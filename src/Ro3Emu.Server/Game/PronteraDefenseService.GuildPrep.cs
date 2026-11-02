using System.Text.Json;
using Romsg;
using Ro3Emu.Server.Accounts.Persistence;
using Ro3Emu.Server.Data;
using Ro3Emu.Server.Data.Config;
using Ro3Emu.Server.Economy;

namespace Ro3Emu.Server.Game;

/// <summary>
/// Guild preparation (PronteraDefenseGuildPrepConfig, DBPronteraDefenseGuildInfo guildPrepLv/guildPrepExp). The defence
/// widget (UI_Sys_Season_DefenceWarWidgetCtrl._refreshGuildInfo) shows the level and a bar of guildPrepExp over
/// GetExp(level), "max level" from the largest id; donating needs a guild and a supply (_checkCanDonate: HaveGuild and
/// PronteraDefenseGuildSupplyItemId 600). Inferred where the client is silent:
/// <list type="bullet">
/// <item>Each donated supply is one preparation exp for the donor's guild, as it is one contribution point for the
/// donor. The exp is kept per opening of TimeLimit 1012, like the stations.</item>
/// <item>A guild starts at the first level (level 0 would show "0/0", a bar that cannot fill) and _iExp is what fills
/// the bar of that level: the remainder carries into the next level.</item>
/// <item>The level's _iBuffId (8000101-8000105 "Guild Battle Preparations", one buff group, cleared on a scene change)
/// is on the guild's members while they are on a Battle for Prontera map (PronteraDefenseMapConfig) and the activity is
/// open, until the opening ends.</item>
/// </list>
/// </summary>
public sealed partial class PronteraDefenseService
{
    /// <summary>Each guild's preparation exp in the current opening.</summary>
    private readonly Dictionary<long, long> _prepExp = [];
    private readonly BuffService _buffs;

    /// <summary>The level and the exp in its bar for a guild's total preparation exp.</summary>
    internal static (long Level, long Exp) PrepLevel(GameData data, long total)
    {
        var levels = PronteraDefenseGuildPrepConfig.All(data).OrderBy(r => r.Id).ToArray();
        if (levels.Length == 0) return (0, 0);
        long exp = Math.Max(0, total);
        int at = 0;
        while (at + 1 < levels.Length && levels[at].Exp > 0 && exp >= levels[at].Exp) exp -= levels[at++].Exp;
        return (levels[at].Id, exp);
    }

    private DBPronteraDefenseGuildInfo GuildInfo(long guild)
    {
        long total = _prepExp.GetValueOrDefault(guild);
        var (level, exp) = PrepLevel(_data, total);
        return new DBPronteraDefenseGuildInfo { GuildId = guild, GuildPrepLv = level, GuildPrepExp = exp, GuildContribution = total };
    }

    /// <summary>The stationed guilds and those that donated (5013 globalInfo.guildInfo).</summary>
    private void AddGuildInfo(DBPronteraDefenseInfo global)
    {
        foreach (long guild in _station.Keys.Concat(_prepExp.Keys).Distinct())
            if (_guilds.Get(guild) != null) global.GuildInfo[guild] = GuildInfo(guild);
    }

    /// <summary>The viewer's own guild, which the widget reads even before it donated or took a station.</summary>
    private void AddOwnGuildInfo(GameSession session, DBPronteraDefenseInfo global)
    {
        if (session.Role != null && _guilds.ForRole(session.Role.Id) is { } guild && !global.GuildInfo.ContainsKey(guild.Id))
            global.GuildInfo[guild.Id] = GuildInfo(guild.Id);
    }

    /// <summary>
    /// 82976 PronteraDefenseDonate: supplies 600 for the donor's contribution points and its guild's preparation exp,
    /// both one per supply (inferred); a role without a guild is refused, as the widget never sends it.
    /// </summary>
    private async Task DonateAsync(GameSession session, PronteraDefenseDonateReq donation, CommonActivityRes response, long now)
    {
        response.PronteraDefenseDonate = new PronteraDefenseDonateResp { Req = donation.Clone() };
        long count = donation.ItemCount;
        var guild = _guilds.ForRole(session.Role!.Id);
        if (count <= 0 || count > int.MaxValue) { response.Result = ErrorCodes.Parameter; return; }
        if (guild == null) { response.Result = ErrorCodes.NotInGuild; return; }
        if (_character.CountOf(session, SupplyItem) < count) { response.Result = ErrorCodes.ItemsShort; return; }
        var next = CopyState(session.Defense!);
        next.ContributionPoint = checked(next.ContributionPoint + count); // one point per supply: inferred.
        int round = RoundAt(now);
        if (round > 0) next.RoundContribution[round] = next.RoundContribution.GetValueOrDefault(round) + count;
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(next);
        long open = Opening();
        var committed = await _character.ExchangeAsync(session, [(SupplyItem, count)], [], popup: false,
            persist: tx =>
            {
                tx.RoleBlob(session.Role.Id, "timed_activity", bytes);
                tx.Add(new PronteraDefenseGuildPrepPlan(open, guild.Id, count));
                tx.WithEventEconomy(EventEconomy.ForRole(_data, session, "prontera-donate",
                    $"prontera/donate/{open}/{session.Role.Id}/{Guid.NewGuid():N}",
                    [(SupplyItem, count)], [], new { Count = count, next.ContributionPoint, Guild = guild.Id }));
            },
            committed: () => session.Defense = next);
        response.Result = committed == null ? ErrorCodes.Unknown : 0;
        if (committed == null) return;
        await NotifyAsync(session);
        await AddPrepAsync(guild.Id, count);
    }

    private void LoadPrep(long open)
    {
        foreach (var (guild, exp) in _accounts.ReadSync<(long Guild, long Exp)[]>(new PronteraDefenseGuildPrepReadPlan(open)))
            _prepExp[guild] = exp;
    }

    /// <summary>The committed donation's exp for the guild; refreshes the buff of its members when the level rose.</summary>
    private async Task AddPrepAsync(long guild, long count)
    {
        long before = PrepLevel(_data, _prepExp.GetValueOrDefault(guild)).Level;
        _prepExp[guild] = checked(_prepExp.GetValueOrDefault(guild) + count);
        if (PrepLevel(_data, _prepExp[guild]).Level != before)
            await RoleFanout.EachAsync(_sessions.All().Where(s => s.Role != null && GuildOf(s) == guild), ApplyPrepBuffAsync);
        await RoleFanout.EachAsync(_sessions.All().Where(s => s.Role != null && s.ActivitiesReady && GuildOf(s) == guild), NotifyAsync);
    }

    private long GuildOf(GameSession s) => s.Role is { } role && _guilds.ForRole(role.Id) is { } guild ? guild.Id : 0;

    /// <summary>SceneService.Loaded, and a level change: the guild's level buff on a defence map while the activity is open.</summary>
    private async Task ApplyPrepBuffAsync(GameSession s)
    {
        long guild = GuildOf(s);
        if (s.Role == null || s.Dead || guild == 0
            || !PronteraDefenseMapConfig.All(_data).Any(c => c.MapId == s.SceneEntry)
            || !_clock.TryWindow(TimeLimitId, out _, out var end) || !_clock.IsOpen(TimeLimitId, _clock.UtcNow)) return;
        long level = PrepLevel(_data, _prepExp.GetValueOrDefault(guild)).Level;
        if (PronteraDefenseGuildPrepConfig.Get(_data, level) is not { BuffId: > 0 } row) return;
        long ms = (end - _clock.UtcNow).Ticks / TimeSpan.TicksPerMillisecond;
        await _buffs.AddAsync(s, (int)row.BuffId, (int)Math.Clamp(ms, 1000, int.MaxValue), s.Role.Id);
    }
}

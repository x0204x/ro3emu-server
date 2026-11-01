using System.Globalization;
using Ro3Emu.Server.Accounts;
using Ro3Emu.Server.Data.Config;
using Ro3Emu.Server.Economy;
using Ro3Emu.Server.Game.Ownership;

namespace Ro3Emu.Server.Game;

/// <summary>
/// The guild's share of the territory world's Faction Quest (TaskService.FactionQuest.cs): PublicParam
/// TerritoryWarzoneTaskGuildAward 1021 names no award row in this client, and quest 1101000's _iBeforeDescribe says only
/// "Obtain a wealth of resources and provide Guild Coins to the guild". (inferred) A delivered 1101000 pays the
/// member's guild what a member's daily caravan departure pays it: the Guild Funds (entry 104) of the first award of
/// the smallest GuildTransportTaskConfig row (_iNum 5, group 2651: 1000 funds), credited like the caravan's funds
/// (GuildCaravanRewardPlan: treasury and the member's fund ranking) in one event receipt per role and game day.
/// </summary>
public sealed partial class GuildCaravanService
{
    private const int GuildFunds = 104;

    /// <summary>The guild funds of the caravan's daily departure award (the smallest row's first group).</summary>
    internal long FactionGuildFunds(GameSession s)
    {
        long group = GuildTransportTaskConfig.All(_data).OrderBy(r => r.Num).Select(r => r.Award.FirstOrDefault()).FirstOrDefault();
        return group == 0 ? 0 : _awards.Resolve(s, group).Grants.Where(g => g.Entry == GuildFunds).Sum(g => (long)g.Count);
    }

    /// <summary>TaskService.FactionGuildAward (inside the quest lock): the payment is posted with the guild reserved.</summary>
    public void PayFactionAward(GameSession s, long day)
    {
        if (Guild(s) is not { } guild) return;
        ReservationKey[] keys = [ReservationKey.Entity("guild", guild.Id.ToString(CultureInfo.InvariantCulture))];
        _loop.PostOwned(s, keys, () => PayFactionAwardAsync(s, day, guild.Id, keys), "guild faction award");
    }

    /// <summary>The funds go to the role's guild, if it is still that guild's member.</summary>
    private async Task PayFactionAwardAsync(GameSession s, long day, long guildId, ReservationKey[] keys)
    {
        if (s.Role is not { } role || Guild(s) is not { } guild || guild.Id != guildId) return;
        long funds = FactionGuildFunds(s);
        if (funds <= 0) return;
        string source = string.Create(CultureInfo.InvariantCulture, $"territory-faction-guild:{role.Id}:{day}");
        var economy = new EconomyEnvelope(Guid.NewGuid().ToString("N"), role.Account, role.Id,
            "territory-faction-guild-award", source, source, NowMs,
            [new(new(EconomyAssetKind.GuildResource, EconomyDenominations.GuildMoney), GuildOwner(guild.Id),
                GuildMoneyCompartment(guild.Id), funds, EconomyLegKind.Source, EconomyReason.GuildResourceGrant)], []);
        List<InvItem>? committed;
        using (await CharacterService.Lock(s, keys))
        {
            if (!ReferenceEquals(s.Role, role) || Guild(s)?.Id != guild.Id) return;
            committed = await _character.ExchangeLockedAsync(s, [], [], popup: false, persist: tx =>
            {
                _guilds.EnlistCaravanReward(guild.Id, role.Id, funds, 0, tx);
                tx.WithEconomy(economy);
            }, asyncCommit: true);
        }
        if (committed == null) return;
        await _guildService.RefreshCaravanResourcesAsync(guild.Id);
    }
}

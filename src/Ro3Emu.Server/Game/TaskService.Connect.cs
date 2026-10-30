using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Ro3Emu.Server.Data.Config;

namespace Ro3Emu.Server.Game;

/// <summary>
/// Connect quests (TaskConfig type 19, 1901000-1905110, "Connect Quest - ..."): one party activity each, all 53
/// hidden from the player. enum_TaskType has no 19 and LC_TaskHelper's main-task set leaves it out, so the client never
/// lists, tracks or accepts one; every row is Auto accept and Auto delivery with no pre-task, condition, award or EXP,
/// so the server starts them all (AcceptRootTasks) and their only effect is the delivery action 116 (inferred).
/// <para>Targets (no LC_ConditionDefine entry, so the client has no evaluator; R is p2 by the default formatter, the
/// meaning is ProgressConfig's text):</para>
/// <para>- 106012 [n, npc] "Required MVP not defeated together": one per committed MVP kill settlement (MvpService) of
///   MVPConfig._iNPCID npc in which another member of the role's party also took part (inferred: "together" = a
///   fellow party member among the participants). Its npcs 9300201 (1901180/1901190, an OnHookDungeonConfig boss)
///   have no server here.</para>
/// <para>- 106013 [n] "Not enough joint Battle Royale participation": one per settled Kafra Survivor match
///   (TimedBattleService) that another member of the role's party played too (inferred).</para>
/// <para>- 106014 [n] "Have not idled together for ${1} minutes" (1902000 "Auto-battle in a party for @{1} minutes",
///   SocialRelationsConfig 2000 names function 18 "AFK in the Field"): whole minutes of Field Bonus Time consumed
///   while another party member on the same map runs it too (WildAfkService). The seconds are kept per attachment,
///   so a part minute is lost on a relog (inferred).</para>
/// <para>- 107005 [n, boss] "Instance not successfully cleared": one per committed successful clear of the TenGameplay
///   boss (TenGameplayBossConfig._iBoss: Phantom Realm, Realm of the Gods) with another player in the run
///   (DungeonService.MultiBossCleared; inferred, as 106602's guild rule).</para>
/// <para>Delivery runs <see cref="UpdateSocialityScore"/> [SocialRelationsConfig id]. No client Lua reads that table or
/// a sociality score; its only filled column is _iFriendValue ["107", amount, 1], ActionConfig 107 GainIntimacy, the
/// friend intimacy the client shows (onRelationDataChange, FriendlinessConfig levels). So the role gains the amount
/// with each friend in its party, once the delivery commits (inferred: the party is who did it together; p3 has no
/// recovered meaning; a friend who delivers the same quest adds the amount again). _iFunction (17 MVP, 18 AFK,
/// 61/62 the raids, 65 Survivor) only names the activity.</para>
/// </summary>
public sealed partial class TaskService
{
    private const int ConnectTaskType = 19;

    internal const int TConnectMvp = 106012, TConnectSurvivor = 106013, TConnectAfk = 106014, TConnectBoss = 107005;

    /// <summary>ActionConfig 116 UpdateSocialityScore and 107 GainIntimacy (_iEXECHost 1).</summary>
    internal const int UpdateSocialityScore = 116, GainIntimacyAction = 107;

    /// <summary>The roles of a role's party, itself included (TeamService.MembersOf).</summary>
    public Func<long, IReadOnlyList<long>>? PartyMembers { get; set; }

    /// <summary>116 after the delivery committed: the role, its party-mates and the intimacy to gain with the friends among
    /// them (FriendExtraService.GainIntimacy).</summary>
    public Action<GameSession, IReadOnlyList<long>, int>? GainIntimacy { get; set; }

    private readonly ConditionalWeakTable<Ownership.AttachmentLease, StrongBox<long>> _partyAfkSeconds = [];

    /// <summary>The intimacy a 116 [id] action gives, -1 when its row is missing or fills a relation this server
    /// does not apply (the brotherhood, love and master columns are empty in the whole export).</summary>
    private long SocialityIntimacy(string[] action)
    {
        if (action.Length != 2 || !long.TryParse(action[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out long id)
            || SocialRelationsConfig.Get(_data, id) is not { } row) return -1;
        if (Filled(row.BrotherhoodValue) || Filled(row.LoveValue) || Filled(row.MasterValue)) return -1;
        long amount = 0;
        foreach (var value in row.FriendValue)
        {
            if (value is [] or [0]) continue;
            if (value is not [GainIntimacyAction, > 0 and <= int.MaxValue, ..]) return -1;
            amount += value[1];
        }
        return amount;

        // A filled column holds any action row other than the empty sentinel [""].
        static bool Filled(JsonElement? rows) => rows is { ValueKind: JsonValueKind.Array } array
            && array.EnumerateArray().Any(r => r.ValueKind != JsonValueKind.Array
                || r.EnumerateArray().Any(v => v.ValueKind != JsonValueKind.String || v.GetString() != ""));
    }

    /// <summary>QuestDelivered: a delivered Connect quest's 116 raises the intimacy with the friends in the party.</summary>
    public void OnConnectDelivered(GameSession session, long id, AwardPlan _)
    {
        if (session.Role is not { } role || !Defs().TryGetValue(id, out var def) || GainIntimacy is not { } gain) return;
        foreach (var action in def.ExecActions.Where(a => ServerOpcode(a) == UpdateSocialityScore))
        {
            long amount = SocialityIntimacy(action);
            var mates = Mates(role.Id);
            if (amount > 0 && mates.Count > 0) gain(session, mates, (int)amount);
        }
    }

    private List<long> Mates(long role) => PartyMembers is { } party ? [.. party(role).Where(id => id != role).Distinct()] : [];

    /// <summary>107005: a committed successful clear of the TenGameplay boss <paramref name="boss"/> by <paramref name="party"/>.</summary>
    public Task OnConnectBossClearedAsync(GameSession s, long boss, IReadOnlyCollection<GameSession> party) =>
        party.Any(o => !ReferenceEquals(o, s) && o.Role != null)
            ? ProgressAsync(s, TConnectBoss, t => t is [TConnectBoss, _, var cleared] && cleared == boss ? 1 : 0)
            : Task.CompletedTask;

    /// <summary>106012 inside the MVP settlement: the killed MVP's npc and whether a party-mate took part too.</summary>
    private static bool ConnectMvp(long[] target, long npc, bool withParty) =>
        withParty && target is [TConnectMvp, _, var mvp] && mvp == npc;

    /// <summary>106013 inside the Survivor settlement: another member of the player's party played the same match.</summary>
    internal bool PartyMateIn(GameSession player, IEnumerable<GameSession> match) =>
        player.Role is { } role && Mates(role.Id) is { Count: > 0 } mates && match.Any(p => p.Role is { } o && mates.Contains(o.Id));

    /// <summary>
    /// 106014: <paramref name="seconds"/> of Field Bonus Time just consumed (WildAfkService). They count while another
    /// party member on the same map has its Bonus Time running; each whole minute is one.
    /// </summary>
    public Task OnBonusTimeConsumedAsync(GameSession s, long seconds)
    {
        if (seconds <= 0 || s.Role is not { } role || s.Attachment is not { } attachment || s.Tasks == null) return Task.CompletedTask;
        var mates = Mates(role.Id);
        bool together = _sessions.All().Any(o => o.Role is { } other && mates.Contains(other.Id) && o.WorldScene == s.WorldScene
            && o.WildAfk is { Info: { IsHighMagnification: true, IsSuspended: false } });
        if (!together) return Task.CompletedTask;
        var total = _partyAfkSeconds.GetValue(attachment, _ => new StrongBox<long>());
        total.Value += seconds;
        long minutes = total.Value / 60;
        total.Value %= 60;
        return minutes > 0 ? ProgressAsync(s, TConnectAfk, t => t.Length == 2 ? minutes : 0) : Task.CompletedTask;
    }
}

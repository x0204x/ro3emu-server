using System.Globalization;
using System.Text.Json;
using Google.Protobuf;
using Romsg;
using Ro3Emu.Server.Accounts;
using Ro3Emu.Server.Accounts.Persistence;
using Ro3Emu.Server.Data;
using Ro3Emu.Server.Data.Config;

namespace Ro3Emu.Server.Game;

/// <summary>
/// The Starshatter Dragon summoning ritual (client FlyingDragon, function 3220): 82968-82974, one ritual per server
/// (DBFlyingDragonProduceInfo is DBActivityParam oneof 5, beside Siege and Territory Wars), in server_state.
/// <list type="bullet">
/// <item>Start (82968, NPC 107 "Starshatter Dragon Ritual Administrator" in Prontera, dialogue 10701): the owner pays the
/// Starshatter Dragon's _kMaterial (Lord's Mane x1, WardRobeConfig 60060010) and, for extraRewardIndex 1-3, a red packet
/// of DragonTaskResortRank[i] x DragonTaskHelpTimes of DragonTaskResortItem (Zeny), as the investment panel shows
/// (UI_Sys_Shop_InvestmentWidgetCtrl). Refused while a ritual runs (1517), away from the NPC (1518), short (1520),
/// within DragonTaskWaitTime of the role's last start (1526) or for an owner of the dragon.</item>
/// <item>Progress (0-10000) grows by DragonTaskPerSec each second, as UI_Sys_Shop_UnblockingPanelCtrl predicts it, plus
/// DragonTaskHelpProg per assist; it ends at ProcessEndTimeStamp (start + DragonTaskLimited).</item>
/// <item>Assist (82969): one per role, never the owner (1521/1519), at most DragonTaskHelpTimes; pays award
/// DragonTaskHelpReward (1 Minted Coin) and, with a red packet, its share in DragonTaskReturnItem (text 200049).</item>
/// <item>Self accelerate (82971, owner): (10000 - progress) x DragonTaskSpUPEffi of DragonTaskSpUPItem finishes it.</item>
/// <item>Complete (82972, owner at 10000, dialogue "Begin the ritual"): the Ceremony; marquee 8002; thumbs-up (82973)
/// within DragonCeremonyLimited seconds near the virtual NPC 108, once per role, never the owner.</item>
/// </list>
/// Inferred (the client does not say): progress follows the client's own prediction (so a ritual fills in
/// 10000 / DragonTaskPerSec seconds and the time limit only matters for an owner who never begins the ceremony: then mail
/// 1114 returns the Lord's Mane and the unpaid red packet); the dragon comes by mail as item 1501004 when the ceremony
/// ends; the red packet's unclaimed part returns by mail 1116; a thumbs-up pays nothing (DragonLikeReward and
/// DragonLikeTrigger have no reader); the start distance is 15 m and the thumbs-up one DragonCeremonyAround.
/// </summary>
public sealed class FlyingDragonService : IGameTicker
{
    public const uint Start = 82968, Assist = 82969, Query = 82970, Accelerate = 82971, Complete = 82972, ThumbsUp = 82973,
        Notify = 82974;
    private const string StateKey = "flying_dragon";
    private const int MountId = 60060010, MountItem = 1501004, ProgressFull = 10000, StartReach = 15;
    private const uint SystemBroadcast = 1022;

    private readonly GameData _data;
    private readonly CharacterService _character;
    private readonly MailService _mail;
    private readonly AccountStore _accounts;
    private readonly SessionRegistry _sessions;
    private readonly GameLoop _loop;
    private readonly Maps.WorldData _world;
    private DBFlyingDragonProduceInfo? _state;
    private TimeSpan _lastCheck;

    public FlyingDragonService(GameData data, CharacterService character, MailService mail, AccountStore accounts,
        SessionRegistry sessions, GameLoop loop, Maps.WorldData world, MvpService mvps, GmCommands gm,
        Microsoft.Extensions.Options.IOptions<ServerOptions> options)
    {
        this._data = data; this._character = character; this._mail = mail; this._accounts = accounts;
        this._sessions = sessions; this._loop = loop; this._world = world;
        mvps.LordsManePercent = Math.Clamp(options.Value.LordsManePercent, 0, 100);
        loop.Add(this);
        gm.Register("dragon", "info | start [packet 0-3] | progress <0-10000> | complete | end | timeout (Starshatter ritual test aid)", GmAsync);
    }

    private long Param(string key, long fallback = 0) =>
        _data.Table("PublicParamConfig").TryGetValue(key, out var v) && GameData.Number(v) is { } n ? (long)n : fallback;

    private long[] Params(string key) =>
        _data.Table("PublicParamConfig").TryGetValue(key, out var v) && v.ValueKind == JsonValueKind.Array
            ? [.. v.EnumerateArray().Select(x => (long)(GameData.Number(x) ?? 0))] : [];

    private static long Now => DateTimeOffset.UtcNow.ToUnixTimeSeconds();

    private DBFlyingDragonProduceInfo State => _state ??=
        _accounts.ReadSync<string?>(new SeasonStateReadPlan(StateKey)) is { Length: > 0 } saved
            ? DBFlyingDragonProduceInfo.Parser.ParseFrom(Convert.FromBase64String(saved)) : new DBFlyingDragonProduceInfo();

    private Task<int> SaveAsync() =>
        _accounts.WriteLegacyAsync<int>(new SeasonStateWritePlan(StateKey, Convert.ToBase64String(State.ToByteArray())));

    private FlyingDragonProductStatus Status => State.ProductInfo != null ? FlyingDragonProductStatus.Product
        : State.CeremonyInfo != null ? FlyingDragonProductStatus.Ceremony : FlyingDragonProductStatus.Idle;

    private long StartTime => State.ProductInfo is { } p ? p.ProcessEndTimeStamp - Param("DragonTaskLimited", 3600) : 0;

    /// <summary>The ritual's progress now: DragonTaskPerSec per second since the start plus the assists (or full).</summary>
    private int Progress()
    {
        if (State.ProductInfo is not { } p) return 0;
        if (p.HasSelfAccelerate) return ProgressFull;
        long value = Math.Max(0, Now - StartTime) * Param("DragonTaskPerSec", 50) + p.AssistantRoles.Count * Param("DragonTaskHelpProg", 500);
        return (int)Math.Min(ProgressFull, value);
    }

    /// <summary>The viewer's 82970/82974 view.</summary>
    private FlyingDragonProduceInfo View(GameSession viewer)
    {
        long me = viewer.Role?.Id ?? 0;
        var info = new FlyingDragonProduceInfo { Status = Status, ProcessUniqueId = State.ProcessUniqueId, OwnerRoleId = State.OwnerRoleId };
        if (State.ProductInfo is { } p)
            info.ProductInfo = new ProductInfo
            {
                ReleaseProgress = Progress(), ProcessEndTimeStamp = p.ProcessEndTimeStamp,
                HasAssisted = p.AssistantRoles.ContainsKey(me), ExtraRewardIndex = p.ExtraRewardIndex,
            };
        else if (State.CeremonyInfo is { } c)
            info.CeremonyInfo = new CeremonyInfo
            {
                CanThumbsUp = me != State.OwnerRoleId && !c.ThumbsUpRoles.ContainsKey(me) && Now < c.ThumbsUpEndTimeStamp,
                ThumbsUpEndTimeStamp = c.ThumbsUpEndTimeStamp, IsAlreadyThumbsUp = c.ThumbsUpRoles.ContainsKey(me),
                CeremonyStartTime = c.CeremonyStartTime,
            };
        return info;
    }

    /// <summary>Every online player sees the ritual's top button (MainMenuWidgetBaseNewCtrl), so every change reaches all.</summary>
    private Task PushAllAsync() => RoleFanout.EachAsync(_sessions.All().Where(p => p.Role != null),
        p => p.SendGameAsync(Notify, new FlyingDragonProductNotify { Info = View(p) }));

    public Task LoginAsync(GameSession s) => Status == FlyingDragonProductStatus.Idle ? Task.CompletedTask
        : s.SendGameAsync(Notify, new FlyingDragonProductNotify { Info = View(s) });

    public Task QueryAsync(GameSession s) => s.SendGameAsync(Query, new FlyingDragonQueryProduceRes { Info = View(s) });

    private Task MarqueeAsync(int chatId, params string[] args)
    {
        var ntf = new SystemBoardCastMsgNtf { Source = MsgSource.System, ChatId = chatId };
        for (int i = 0; i < args.Length; i++) ntf.ParamList[i + 1] = new ChatParameter { Params = { args[i] }, Type = ChatParamType.ChatParamString };
        return RoleFanout.EachAsync(_sessions.All().Where(p => p.Role != null), p => p.SendGameAsync(SystemBroadcast, ntf));
    }

    private bool Near(GameSession s, long[] npc, long reach)
    {
        if (npc.Length < 2 || s.SceneEntry != npc[0]) return false;
        var map = _world.Map(npc[0]);
        var at = map?.Npcs.FirstOrDefault(n => n.NpcId == npc[1]);
        return at != null && MonsterService.Distance(s.Position.X, s.Position.Z, at.X, at.Z) <= reach;
    }

    private static bool OwnsDragon(GameSession s) =>
        s.Mounts?.MountIds.ContainsKey(MountId) == true || s.Wardrobe?.AllLightUpAppearances.ContainsKey(MountId) == true;

    /// <summary>The dragon's unseal material (WardRobeConfig 60060010 _kMaterial: Lord's Mane x1).</summary>
    private List<(int Entry, long Count)> Material() =>
        _data.Row("WardRobeConfig", MountId) is { } row && new WardRobeConfig(row).Material is { } pairs
            ? [.. pairs.Where(p => p.Length >= 2).Select(p => ((int)p[0], p[1]))] : [];

    /// <summary>The red packet of extraRewardIndex 1-3: DragonTaskResortRank[i - 1] per helper, times the helpers.</summary>
    private long PacketPrice(int index) => index >= 1 && Params("DragonTaskResortRank") is { } ranks && index <= ranks.Length ? ranks[index - 1] : 0;

    // --- requests ---------------------------------------------------------------------------------------------------

    public async Task StartAsync(GameSession s, FlyingDragonStartProduceReq req)
    {
        int result;
        using (await CharacterService.Lock(s)) result = await StartLockedAsync(s, req.ExtraRewardIndex, anywhere: false);
        await s.SendGameAsync(Start, new FlyingDragonStartProduceRes { Result = result });
        if (result == 0)
        {
            await MarqueeAsync((int)Param("DragonChatTask", 8001), s.Role!.Name);
            await PushAllAsync();
        }
    }

    private async Task<int> StartLockedAsync(GameSession s, int index, bool anywhere)
    {
        long role = s.Role!.Id, wait = Param("DragonTaskWaitTime", 86400);
        if (index < 0 || index > Params("DragonTaskResortRank").Length) return ErrorCodes.DragonConfig;
        if (Status != FlyingDragonProductStatus.Idle) return ErrorCodes.DragonBusy;
        if (State.StartRoles.TryGetValue(role, out long last) && Now - last < wait) return ErrorCodes.DragonCooldown;
        if (OwnsDragon(s)) return ErrorCodes.DragonDone;
        if (!anywhere && !Near(s, Params("DragonTaskNPC"), StartReach)) return ErrorCodes.DragonPlace;
        var cost = Material();
        long packet = PacketPrice(index) * Param("DragonTaskHelpTimes", 10);
        if (packet > 0) cost.Add(((int)Param("DragonTaskResortItem", 1), packet));
        if (cost.Count == 0) return ErrorCodes.DragonConfig;
        var paid = await _character.ExchangeLockedAsync(s, cost, [], popup: false,
            persist: tx => tx.WithEconomy(_character.FamilyEconomy(s, "cd13a/character-dragon-start", cost, [],
                new { Index = index, Packet = packet })));
        if (paid == null) return ErrorCodes.DragonItems;
        var next = State.Clone();
        next.ProductInfo = new DBProductInfo
        {
            ProcessEndTimeStamp = Now + Param("DragonTaskLimited", 3600), ExtraRewardIndex = index,
            OriItems = { cost.ToDictionary(c => c.Entry, c => (int)c.Count) },
        };
        next.CeremonyInfo = null;
        next.ProcessUniqueId = Math.Max(State.ProcessUniqueId + 1, Now);
        next.OwnerRoleId = role;
        next.StartRoles[role] = Now;
        _state = next;
        await SaveAsync();
        return 0;
    }

    public async Task AssistAsync(GameSession s)
    {
        int result;
        using (await CharacterService.Lock(s)) result = await AssistLockedAsync(s);
        await s.SendGameAsync(Assist, new FlyingDragonAssistProduceRes { Result = result });
        if (result == 0) await PushAllAsync();
    }

    private async Task<int> AssistLockedAsync(GameSession s)
    {
        long role = s.Role!.Id;
        if (State.ProductInfo is not { } p) return ErrorCodes.DragonDone;
        if (role == State.OwnerRoleId) return ErrorCodes.DragonSelf;
        if (p.AssistantRoles.ContainsKey(role)) return ErrorCodes.DragonAssisted;
        if (Progress() >= ProgressFull || p.AssistantRoles.Count >= Param("DragonTaskHelpTimes", 10)) return ErrorCodes.DragonDone;
        var goods = HelpReward();
        long share = PacketPrice(p.ExtraRewardIndex);
        if (share > 0) goods.Add(((int)Param("DragonTaskReturnItem", 2), (int)share));
        var paid = await _character.ExchangeLockedAsync(s, [], goods, popup: true,
            persist: tx => tx.WithEconomy(_character.FamilyEconomy(s, "cd13a/character-dragon-assist", [], goods,
                new { Ritual = State.ProcessUniqueId })));
        if (paid == null) return ErrorCodes.BagFull;
        var next = State.Clone();
        next.ProductInfo.AssistantRoles[role] = true;
        _state = next;
        await SaveAsync();
        return 0;
    }

    /// <summary>DragonTaskHelpReward: AwardConfig _kAward [entry, count, bind] triples (1001: one Minted Coin).</summary>
    private List<(int Entry, int Count)> HelpReward() =>
        _data.Row("AwardConfig", Param("DragonTaskHelpReward", 1001)) is { ValueKind: JsonValueKind.Array } rows && rows.GetArrayLength() > 0
            && new AwardConfig(rows[0]).Award is { } award
            ? [.. award.Where(a => a.Length >= 2).Select(a => ((int)a[0], (int)a[1]))] : [];

    public async Task AccelerateAsync(GameSession s)
    {
        int result;
        using (await CharacterService.Lock(s)) result = await AccelerateLockedAsync(s);
        await s.SendGameAsync(Accelerate, new FlyingDragonSelfAccelerateRes { Result = result });
        if (result == 0) await PushAllAsync();
    }

    private async Task<int> AccelerateLockedAsync(GameSession s)
    {
        if (State.ProductInfo is not { } p || p.HasSelfAccelerate) return ErrorCodes.DragonDone;
        if (s.Role!.Id != State.OwnerRoleId) return ErrorCodes.DragonSelf;
        int left = ProgressFull - Progress();
        if (left <= 0) return ErrorCodes.DragonDone;
        var cost = new List<(int Entry, long Count)> { ((int)Param("DragonTaskSpUPItem", 2), left * Param("DragonTaskSpUPEffi", 10)) };
        var paid = await _character.ExchangeLockedAsync(s, cost, [], popup: false,
            persist: tx => tx.WithEconomy(_character.FamilyEconomy(s, "cd13a/character-dragon-accelerate", cost, [],
                new { Ritual = State.ProcessUniqueId, Left = left })));
        if (paid == null) return ErrorCodes.DragonItems;
        var next = State.Clone();
        next.ProductInfo.HasSelfAccelerate = true;
        _state = next;
        await SaveAsync();
        return 0;
    }

    public async Task CompleteAsync(GameSession s)
    {
        int result = State.ProductInfo == null ? ErrorCodes.DragonDone
            : s.Role!.Id != State.OwnerRoleId ? ErrorCodes.DragonSelf
            : Progress() < ProgressFull ? ErrorCodes.DragonDone : 0;
        if (result == 0)
        {
            long limit = Param("DragonCeremonyLimited", 30);
            var ended = State.Clone();
            var next = State.Clone();
            next.ProductInfo = null;
            next.CeremonyInfo = new DBCeremonyInfo { CeremonyStartTime = Now, ThumbsUpEndTimeStamp = Now + limit, CanThumbsUp = true };
            _state = next;
            await SaveAsync();
            // The packet's unclaimed shares go back to the owner (DragonTaskPacketMail 1116 "the unclaimed portions").
            await RefundPacketAsync(s.Role!.Id, ended);
        }
        await s.SendGameAsync(Complete, new FlyingDragonCompleteProduceRes { Result = result });
        if (result == 0)
        {
            await MarqueeAsync((int)Param("DragonChatCeremony", 8002), s.Role!.Name, Param("DragonCeremonyLimited", 30).ToString(CultureInfo.InvariantCulture));
            await PushAllAsync();
        }
    }

    public async Task ThumbsUpAsync(GameSession s)
    {
        long role = s.Role!.Id;
        int result = State.CeremonyInfo is not { } c ? ErrorCodes.DragonNoLike
            : role == State.OwnerRoleId ? ErrorCodes.DragonSelf
            : c.ThumbsUpRoles.ContainsKey(role) ? ErrorCodes.DragonLiked
            : Now >= c.ThumbsUpEndTimeStamp ? ErrorCodes.DragonNoLike
            : !Near(s, Params("DragonTaskVirtualNPC"), Param("DragonCeremonyAround", 40)) ? ErrorCodes.DragonPlace : 0;
        if (result == 0)
        {
            var next = State.Clone();
            next.CeremonyInfo.ThumbsUpRoles[role] = true;
            _state = next;
            await SaveAsync();
        }
        await s.SendGameAsync(ThumbsUp, new FlyingDragonCeremonyThumbsUpRes { Result = result });
        if (result == 0) await s.SendGameAsync(Notify, new FlyingDragonProductNotify { Info = View(s) });
    }

    // --- time -------------------------------------------------------------------------------------------------------

    public void Tick(GameTime time)
    {
        if (time.Now - _lastCheck < TimeSpan.FromSeconds(1)) return;
        _lastCheck = time.Now;
        if (State.CeremonyInfo is { } c && Now >= c.ThumbsUpEndTimeStamp) _loop.PostLegacy(FinishCeremonyAsync, "flying dragon ceremony");
        else if (State.ProductInfo is { } p && Now >= p.ProcessEndTimeStamp) _loop.PostLegacy(TimeOutAsync, "flying dragon timeout");
    }

    /// <summary>The ceremony ends: the owner's dragon (item 1501004, use effect 201 permanent) comes by mail; back to Idle.</summary>
    private async Task FinishCeremonyAsync()
    {
        if (State.CeremonyInfo is not { } c || Now < c.ThumbsUpEndTimeStamp) return;
        long owner = State.OwnerRoleId;
        var next = State.Clone();
        next.CeremonyInfo = null;
        next.OwnerRoleId = 0;
        _state = next;
        await SaveAsync();
        await _mail.SendRawAsync(owner, ItemName(MountItem), ItemName(MountItem), [new MailService.Attachment(MountItem, 1)]);
        await PushAllAsync();
    }

    /// <summary>The time ran out before the ceremony: mail 1114 returns the materials and the unpaid packet.</summary>
    private async Task TimeOutAsync()
    {
        if (State.ProductInfo is not { } p || Now < p.ProcessEndTimeStamp) return;
        long owner = State.OwnerRoleId;
        var ended = State.Clone();
        var next = State.Clone();
        next.ProductInfo = null;
        next.OwnerRoleId = 0;
        _state = next;
        await SaveAsync();
        var material = Material().Select(m => new MailService.Attachment(m.Entry, (int)m.Count)).ToList();
        await _mail.SendTemplateAsync(owner, (int)Param("DragonTaskMail", 1114), material);
        await RefundPacketAsync(owner, ended);
        await PushAllAsync();
    }

    private async Task RefundPacketAsync(long owner, DBFlyingDragonProduceInfo ritual)
    {
        if (ritual.ProductInfo is not { } p) return;
        long share = PacketPrice(p.ExtraRewardIndex), helpers = Param("DragonTaskHelpTimes", 10);
        long left = (helpers - p.AssistantRoles.Count) * share;
        if (left <= 0) return;
        await _mail.SendTemplateAsync(owner, (int)Param("DragonTaskPacketMail", 1116),
            [new MailService.Attachment((int)Param("DragonTaskResortItem", 1), (int)left)]);
    }

    private string ItemName(int entry) => _data.Row("ItemConfig", entry) is { } row && new ItemConfig(row).Name is [var text, ..]
        ? _data.Text(text) : entry.ToString(CultureInfo.InvariantCulture);

    // --- test aid ---------------------------------------------------------------------------------------------------

    private async Task GmAsync(GameSession s, string[] a)
    {
        if (a.Length >= 1 && a[0] == "start")
        {
            // The NPC talk without walking there: everything else is checked as 82968 does.
            int index = a.Length >= 2 && int.TryParse(a[1], out int i) ? i : 0, result;
            using (await CharacterService.Lock(s)) result = await StartLockedAsync(s, index, anywhere: true);
            if (result == 0)
            {
                await MarqueeAsync((int)Param("DragonChatTask", 8001), s.Role!.Name);
                await PushAllAsync();
            }
            await GmCommands.ReplyAsync(s, "start " + result);
            return;
        }
        if (a.Length >= 1 && a[0] == "complete")
        {
            await CompleteAsync(s); // the NPC's "Begin the ritual" without walking there
            return;
        }
        if (a.Length >= 2 && a[0] == "progress" && int.TryParse(a[1], out int value) && State.ProductInfo != null)
        {
            var next = State.Clone();
            // Move the start back so the per-second growth alone gives the asked progress.
            long perSec = Math.Max(1, Param("DragonTaskPerSec", 50));
            next.ProductInfo.ProcessEndTimeStamp = Now - value / perSec + Param("DragonTaskLimited", 3600);
            _state = next;
            await SaveAsync();
            await PushAllAsync();
        }
        else if (a.Length >= 1 && a[0] == "end" && State.CeremonyInfo != null)
        {
            var next = State.Clone();
            next.CeremonyInfo.ThumbsUpEndTimeStamp = Now;
            _state = next;
            await FinishCeremonyAsync();
        }
        else if (a.Length >= 1 && a[0] == "timeout" && State.ProductInfo != null)
        {
            var next = State.Clone();
            next.ProductInfo.ProcessEndTimeStamp = Now;
            _state = next;
            await TimeOutAsync();
        }
        await GmCommands.ReplyAsync(s, $"Ritual {Status}: owner {State.OwnerRoleId}, progress {Progress()}, "
            + $"assists {State.ProductInfo?.AssistantRoles.Count ?? 0}, likes {State.CeremonyInfo?.ThumbsUpRoles.Count ?? 0}");
    }
}

using System.Collections.Frozen;
using Romsg;
using Ro3Emu.Server.Accounts.Persistence;
using Ro3Emu.Server.Data;

namespace Ro3Emu.Server.Game;

/// <summary>
/// A system mount and its WardRobeConfig type-6 row are one collectible seen through two datasets.
/// The wardrobe Mount tab lights an entry only from 5034 allLightUpAppearances
/// (UI_Sys_WardrobePanelCtrl.lua:1848-1859) and rides it by sending 31183 then 32743 with that ID (:1398-1425,
/// :1070-1126). Ownership conditions, the luxury serial and the panel's AllMountData binding instead resolve the
/// row's _iSystemID in 5012 (LC_InteractCondition.lua:4448-4515, UI_Sys_WardrobePanelCtrl.lua:4880-4905, :162,
/// :2314-2346). Every mount ownership change therefore carries the matching appearance in the same commit.
/// Both overdueTimeMs fields are absolute Unix milliseconds with zero meaning permanent
/// (LC_FashionShowFunc.lua:256-286, LC_FashionHelper.lua:512-519/680-696, LC_DAL_Mount.lua:186-220).
/// </summary>
public sealed partial class WardrobeService
{
    private FrozenDictionary<long, int>? _mountAppearances;

    /// <summary>MountsConfig ID to its listed (not disabled) type-6 appearance, by WardRobeConfig._iSystemID.</summary>
    internal int MountAppearance(long mountId)
    {
        _mountAppearances ??= _data.Table("WardRobeConfig").Values
            .Where(r => GameData.Int(r, "_iType") == MountSlot && GameData.Int(r, "_iDisable") <= 0
                && GameData.Int(r, "_iSystemID") > 0 && GameData.Int(r, "_iID") > 0)
            .GroupBy(r => GameData.Int(r, "_iSystemID"))
            .ToFrozenDictionary(g => g.Key, g => (int)g.Min(r => GameData.Int(r, "_iID")));
        return _mountAppearances.GetValueOrDefault(mountId);
    }

    /// <summary>The MountsConfig ID behind a listed (not disabled) type-6 look, by its _iSystemID; zero otherwise.</summary>
    internal int SystemMount(int appearanceId) =>
        Row(appearanceId) is { } row && GameData.Int(row, "_iType") == MountSlot && GameData.Int(row, "_iDisable") <= 0
            && GameData.Int(row, "_iSystemID") is var id and > 0 and <= int.MaxValue ? (int)id : 0;

    /// <summary>
    /// Ownership of the system mounts behind bought type-6 looks, planned by MountService and joined to a
    /// wardrobe-owned transaction: Persist writes "mounts", Committed adopts it (and re-arms the mount expiry),
    /// PublishAsync sends 5012. StateHash fingerprints the persisted mounts blob for the receipt.
    /// </summary>
    internal sealed record LinkedMountGrant(IReadOnlyList<int> Mounts, string StateHash,
        Action<RoleItemPlanBuilder> Persist, Action Committed, Func<Task> PublishAsync);

    /// <summary>
    /// Registered by MountService, which already depends on this service (a constructor dependency back would be
    /// circular). Given the mount IDs, returns an ErrorCodes result and the grant, or a null grant when every mount is
    /// already owned permanently.
    /// </summary>
    internal Func<GameSession, IReadOnlyList<int>, (int Error, LinkedMountGrant? Grant)>? MountGrant { get; set; }

    /// <summary>The appearance lives as long as the longer-lived source; zero (permanent) dominates.</summary>
    private static long Lifetime(long a, long b) => a == 0 || b == 0 ? 0 : Math.Max(a, b);

    /// <summary>Light (or extend) every owned mount's appearance in <paramref name="info"/>; true if it changed.</summary>
    private bool LightMounts(DBWardRobeInfo info, IEnumerable<DBMount> mounts)
    {
        long now = _clock.GetUtcNow().ToUnixTimeMilliseconds();
        bool changed = false;
        foreach (var mount in mounts)
        {
            if (mount.OverdueTimeMs > 0 && mount.OverdueTimeMs <= now) continue;
            int id = MountAppearance(mount.MountId);
            if (id == 0) continue;
            long due = info.AllLightUpAppearances.TryGetValue(id, out var lit)
                && (lit.OverdueTimeMs == 0 || lit.OverdueTimeMs > now)
                ? Lifetime(lit.OverdueTimeMs, mount.OverdueTimeMs) : mount.OverdueTimeMs;
            if (lit != null && lit.AppearanceId == id && lit.OverdueTimeMs == due) continue;
            info.AllLightUpAppearances[id] = new DBAppearanceInfo { AppearanceId = id, OverdueTimeMs = due };
            changed = true;
        }
        return changed;
    }

    /// <summary>The wardrobe that must commit together with a granted mount, or null when 5034 is already consistent.</summary>
    internal DBWardRobeInfo? WithMount(GameSession session, DBMount mount)
    {
        if (session.Wardrobe == null) return null;
        var next = session.Wardrobe.Clone();
        return LightMounts(next, [mount]) ? next : null;
    }

    /// <summary>The wardrobe with expired ownership removed (as RefreshExpiredAsync), for a mount expiry commit.</summary>
    internal DBWardRobeInfo? WithoutExpired(GameSession session)
    {
        if (session.Wardrobe == null) return null;
        var next = session.Wardrobe.Clone();
        if (!RemoveExpired(next)) return null;
        EnsureDefaults(session, next);
        return next;
    }

    /// <summary>Adopt a wardrobe committed by another owner's transaction (call from its committed callback).</summary>
    internal void AdoptCommitted(GameSession session, DBWardRobeInfo next)
    {
        session.Wardrobe = next;
        ArmExpiry(session);
    }

    /// <summary>Publish an adopted wardrobe: 5034, and the AOI look only when worn slots changed.</summary>
    internal async Task PublishCommittedAsync(GameSession session, DBWardRobeInfo before)
    {
        await CharacterService.SyncAsync(session, WardrobeSync, session.Wardrobe!.Clone());
        if (!before.CurUseAppearances.Equals(session.Wardrobe.CurUseAppearances)) await PublishAppearanceAsync(session);
    }
}

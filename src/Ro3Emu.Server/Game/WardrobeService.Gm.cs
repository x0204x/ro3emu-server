using System.Security.Cryptography;
using Google.Protobuf;
using Romsg;
using Ro3Emu.Server.Data.Config;

namespace Ro3Emu.Server.Game;

public sealed partial class WardrobeService
{
    /// <summary>
    /// GMWarRobe 251 "lightupallappearance" (preset 86): every WardRobeConfig look the role may own lights for good, a
    /// mount look with its system mount (as a Fashion-shop purchase grants it, so no lit look lacks its mount). (inferred)
    /// The same per-look rules as an unlock (listed, sex, mount job); one 5034 and no unlock tips; nothing is worn.
    /// </summary>
    internal async Task<string> GmLightUpAllAsync(GameSession session)
    {
        using var _ = await CharacterService.Lock(session);
        if (session.Wardrobe == null) return "The wardrobe is not loaded for this role.";
        await RefreshExpiredAsync(session);
        var current = session.Wardrobe!;
        var looks = WardRobeConfig.All(_data).Select(r => (int)r.Id)
            .Where(id => id > 0 && !Owns(current, id) && Validate(session, current, id, requireOwned: false) == 0).ToList();
        if (looks.Count == 0) return "Every appearance is already unlocked.";
        var mounts = looks.Select(SystemMount).Where(id => id > 0).Distinct().ToList();
        LinkedMountGrant? ride = null;
        if (mounts.Count > 0 && MountGrant != null)
        {
            (int error, ride) = MountGrant(session, mounts);
            // Without its mount a mount look stays dark (inferred, keeps the invariant above).
            if (error != 0) looks.RemoveAll(id => SystemMount(id) > 0);
        }
        else looks.RemoveAll(id => SystemMount(id) > 0);
        var next = current.Clone();
        foreach (int id in looks) next.AllLightUpAppearances[id] = new DBAppearanceInfo { AppearanceId = id };
        var tx = _roles.NewTransaction();
        tx.ItemDelta(session.Role!.Id, session.Items, session.Items);
        tx.RoleBlob(session.Role.Id, "wardrobe", next.ToByteArray());
        ride?.Persist(tx);
        // The mount family may write both blobs (a look and its system mount).
        tx.WithEconomy(_character.FamilyEconomy(session, "cd13b/mount-gm-appearances", [], [],
            new
            {
                Appearances = looks.Count, Mounts = ride?.Mounts ?? [], MountStateHash = ride?.StateHash,
                StateHash = Convert.ToHexString(SHA256.HashData(next.ToByteArray()))
            }));
        await _roles.CommitAsync(tx, "wardrobe/gm-light-up-all", _ =>
        {
            session.Wardrobe = next;
            ArmExpiry(session);
            ride?.Committed();
        });
        await CharacterService.SyncAsync(session, WardrobeSync, next.Clone());
        if (ride != null) await ride.PublishAsync();
        await _character.RefreshStatsAsync(session); // a lit look may carry wardrobe attributes
        return $"{looks.Count} appearances unlocked" + (ride != null ? $", {ride.Mounts.Count} mounts with them." : ".");
    }
}

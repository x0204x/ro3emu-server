using Ro3Emu.Server.Data.Config;

namespace Ro3Emu.Server.Game;

public sealed partial class CareerSpecialService
{
    /// <summary>
    /// GMEquipPendant 176 / GMEquipMount 175 (the class presets name carts, the falcon, Peco Pecos, the dragon, the wolf
    /// and the Madogear this way): lease the Special Class Item as 32721 does, without its reply. (inferred) The lease
    /// rules (career, level, skill) still hold, since a lease the role may not hold is repaired away at its next login;
    /// <paramref name="ride"/> (the presets' "1" after a mount) also rides it, and MountService's own checks dismount
    /// where riding is not allowed.
    /// </summary>
    internal async Task<string> GmLeaseAsync(GameSession s, long id, bool mount, bool ride)
    {
        using var gate = await CharacterService.Lock(s);
        int type = mount ? MountType : PendantType;
        if (s.CareerSpecial == null) return "Special Class Items are not loaded for this role.";
        if (CartRemouldTable.Get(_data, id) is not { } row || row.Type != type)
            return $"{id} is not a Special Class Item {(mount ? "mount" : "pendant")} (CartRemouldTable _iType {type}).";
        int error = Entitlement(s, Row(id));
        if (error != 0)
            return $"{id} cannot be leased: " + error switch
            {
                ErrJob => $"not for job {s.Progress!.Career}",
                ErrLevel => $"needs base level {row.UnlockBaseLv}",
                ErrQuest => $"needs quest {row.TaskId}",
                ErrSkill => $"needs skill {row.UnlockSkillId}",
                _ => $"error {error}",
            } + ".";
        long previous = mount ? s.CareerSpecial.MountsSlot.CartRemouldCfg : s.CareerSpecial.PendantSlot.CartRemouldCfg;
        if (previous == id && !(mount && ride)) return $"{id} is already leased.";
        if (previous != id && IsCart(previous) && !IsCart(id) && CartHasItems(s)) return $"{id} cannot replace the cart: the cart is not empty.";
        var next = s.CareerSpecial.Clone();
        if (mount) next.MountsSlot.CartRemouldCfg = id;
        else next.PendantSlot.CartRemouldCfg = id;
        var mounts = mount ? BattleSlot(s, id, previous) : null;
        if (ride && mounts != null)
            foreach (var (key, selected) in mounts.MountIds) selected.IsRiding = key == BattleSlotKey && selected.MountId == id;
        var shown = s.CareerPendants;
        await CommitAsync(s, next, "gm-lease", mounts);
        await PublishAsync(s, mount, shown);
        return $"Leased {(mount ? "mount" : "pendant")} {id}" + (ride && mounts != null ? ", riding." : ".");
    }
}

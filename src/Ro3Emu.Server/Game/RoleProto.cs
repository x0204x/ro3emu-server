using Romsg;
using Ro3Emu.Server.Accounts;

namespace Ro3Emu.Server.Game;

/// <summary>Builds the romsg views of a stored role.</summary>
public static class RoleProto
{
    /// <summary>enum_WardrobeShowType.Hair — the appearance slot the create screen fills.</summary>
    public const int HairSlot = 7;

    /// <param name="account">The account as the client spelled it at login (AccountStore.ClientAccount); default the stored name.</param>
    public static RoleBaseInfo BaseInfo(Role role, string? account = null) => new()
    {
        Id = role.Id,
        Name = role.Name,
        Sex = role.Sex,
        Career = role.Career,
        Level = role.Level,
        JobLevel = 1,
        CreateTime = role.CreateTime,
        LastLoginTime = role.LastLoginTime,
        LastLoginSceneLineNum = 1,
        Account = account ?? role.Account,
        BornArea = role.Area,
    };

    public static Dictionary<int, int> Appearances(Role role) =>
        role.Hair != 0 ? new() { [HairSlot] = role.Hair } : new();

    /// <summary>Offline/model cache projection of the same persisted wardrobe used by the world entity.</summary>
    public static Dictionary<int, int> Appearances(Role role, byte[]? saved)
    {
        if (saved == null) return Appearances(role);
        var wardrobe = DBWardRobeInfo.Parser.ParseFrom(saved);
        long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var result = wardrobe.CurUseAppearances.Where(p => wardrobe.AllLightUpAppearances.TryGetValue(p.Value, out var owned)
            && (owned.OverdueTimeMs == 0 || owned.OverdueTimeMs > now)).ToDictionary(p => p.Key, p => p.Value);
        if (!result.ContainsKey(HairSlot) && role.Hair != 0) result[HairSlot] = role.Hair;
        return result;
    }

    /// <summary>
    /// 5034 DBWardRobeInfo after role select: the worn appearances (the hair chosen at
    /// creation), each owned permanently, and plan 0 "Default" holding them. WardrobeService extends this migration
    /// seed with configured defaults and persisted collection, looks and favorites.
    /// </summary>
    public static DBWardRobeInfo Wardrobe(Role role)
    {
        var info = new DBWardRobeInfo { IsInit = true };
        var worn = Appearances(role);
        var plan = new DBAppearancePlan { Index = 0, IsUnlock = true, Name = "Default" };
        foreach (var (slot, id) in worn)
        {
            info.CurUseAppearances[slot] = id;
            info.AllLightUpAppearances[id] = new DBAppearanceInfo { AppearanceId = id, OverdueTimeMs = 0 };
            plan.Appearances[slot] = id;
        }
        info.AppearancePlans[0] = plan;
        return info;
    }

    /// <summary>romsg.DBWardRobeInfo bytes for getRoleInfo (only curUseAppearances is read by the role list).</summary>
    public static byte[] WardrobeBytes(Role role, byte[]? saved = null)
    {
        var wardrobe = new DBWardRobeInfo();
        wardrobe.CurUseAppearances.Add(Appearances(role, saved));
        return Google.Protobuf.MessageExtensions.ToByteArray(wardrobe);
    }
}

using Romsg;
using Ro3Emu.Server.Data;
using Ro3Emu.Server.Maps;

namespace Ro3Emu.Server.Game;

public sealed partial class BuffService
{
    private const int DarkShowState = 2704, DarkRadius = 2705, DarkSelectRadius = 2710;
    // (inferred) The server-only blind radius was not recovered. Use 5 m consistently for render and selection;
    // this is an explicit emulator policy, not a client-derived numeric value or a hit-chance penalty.
    // (inferred scope) Only a guardian's blind limits sight: the same group from ordinary skills keeps its previous,
    // display-only behaviour until the radius policy is established for every source.
    internal const int BlindRadiusCm = 500;
    internal bool Blinded(GameSession player) => player.Buffs.Any(b => b.IsGuardianOwned && b.ExpireMs > MonsterService.NowMs
        && _data.Row("BuffConfig", b.ConfigId) is { } row && GameData.Int(row, "_iBuffID") == BlindGroup);
    internal bool CanSeePosition(GameSession player, Vec3 position) => !Blinded(player)
        || MonsterService.Distance(player.Position.X, player.Position.Z, position.X, position.Z) <= BlindRadiusCm / 100f;

    // (inferred) Apply the same selection radius to a blinded monster's acquisition and locked target.
    internal bool CanSeePosition(WorldUnit unit, Vec3 position) => !_unitBuffs.TryGetValue(unit.Uid, out var buffs)
        || !buffs.Any(b => b.IsGuardianOwned && b.ExpireMs > MonsterService.NowMs && b.ConfigId / 100 == BlindGroup)
        || MonsterService.Distance(unit.X, unit.Z, position.X, position.Z) <= BlindRadiusCm / 100f;

    private IEnumerable<(int Id, long Value)> BlindProperties(GameSession player)
    {
        // Lua_GameDefine.enum_unitAttrMap; LC_UnitBase.UpdateDarkRadius converts cm to metres.
        yield return (DarkShowState, Blinded(player) ? 1 : 0);
        yield return (DarkRadius, BlindRadiusCm);
        yield return (DarkSelectRadius, BlindRadiusCm);
    }

    private Task PublishBlindAsync(GameSession player) => player.Role == null ? Task.CompletedTask
        : ToPlayerAndViewersAsync(player, AoiAttrChange, new AOIAttrChange
        {
            EntityUuid = (ulong)player.Role.Id,
            Property = { BlindProperties(player).Select(p => new AoIProperty { Idx = p.Id, Data = p.Value }) },
        });
}

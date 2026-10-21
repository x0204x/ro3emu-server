namespace Ro3Emu.Server.Game;

/// <summary>
/// Actions a control state forbids, as SpecialStatusConfig._kForbidden numbers them: the 1-based position in
/// Lua_GameDefine.enum_ForbiddenState (Select = 1, EnemySelect = 2, Walk = 4, CastSkill = 8, NormalAttack = 16...).
/// <see cref="BuffService"/> converts an ordinal to the wire's bit flag.
/// </summary>
internal static class ForbiddenState
{
    public const int Walk = 3, CastSkill = 4, NormalAttack = 5, Damage = 9, UseItem = 12, Revive = 16;
}

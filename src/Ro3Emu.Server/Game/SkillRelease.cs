namespace Ro3Emu.Server.Game;

/// <summary>SkillConfig._iRelease: 0 for none, otherwise Lua_GameDefine.enum_SkillIndicatorNew + 1 (Lock 0 ..
/// DragCircle 7), as <see cref="BossSkillSets"/> reads it. <see cref="Area"/> is the _iRangeType of an area skill.</summary>
internal static class SkillRelease
{
    internal const int Lock = 1, FixCircle = 2, FixRect = 3, Fan = 4, Arrow = 5, Rect360 = 6, DragRect = 7, DragCircle = 8;
    internal const int SingleTarget = 1, Area = 2; // _iRangeType
}

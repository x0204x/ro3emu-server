using System.Text.Json;

namespace Ro3Emu.Server.Data.Config;

/// <summary>Lua_Config_EffectData.m_kPoint.m_kWarningRange；時間為秒，半徑為公尺。</summary>
internal readonly record struct StoryWarningData(JsonElement Row)
{
    internal static StoryWarningData? Find(GameData data, int effectId) =>
        SkillEffectData.Find(data.SkillTemplateTable("Lua_Config_EffectData"), effectId) is { } effect
        && ConfigValue.Raw(effect.Row, "m_kPoint") is { } point
        && ConfigValue.Raw(point, "m_kWarningRange") is { } warning ? new(warning) : null;

    internal int DurationMs => ConfigValue.Number(Row, "m_fDuration") is var duration
        && double.IsFinite(duration) && duration > 0 ? (int)Math.Min(int.MaxValue, duration * 1000) : 0;
    internal float Radius => (float)ConfigValue.Number(Row, "m_fRadius");
    internal bool ClosesOnInterrupt => ConfigValue.Bool(Row, "m_bCloseWhileSkillInterrupt");
    internal bool ClosesOnRemoval => ConfigValue.Bool(Row, "m_bCloseWhileMasterDestroy");
}

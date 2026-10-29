using System.Text.Json;

namespace Ro3Emu.Server.Data.Config;

/// <summary>Typed views over the nested Lua skill-template structures in data/skills.</summary>
public readonly record struct SkillEffectDamage(JsonElement Row)
{
    public long DamageType => ConfigValue.Integral(Row, "m_kDamageType");
    public bool HasActionDamage => Row.TryGetProperty("m_kActionDamge", out _);
    public SkillBulletData? Bullet => ConfigValue.Raw(Row, "m_kBulletData") is { } value
        ? new SkillBulletData(value) : null;
}

public readonly record struct SkillBulletData(JsonElement Row)
{
    public string Name => ConfigValue.Text(Row, "m_kBulletName");
    public long ControlType => ConfigValue.Integral(Row, "m_iBulletControlType");
    public double LifeTime => ConfigValue.Number(Row, "m_fBulletLifeTime");
    public double Speed => ConfigValue.Number(Row, "m_fBulletSpeed");
}

public readonly record struct SkillTimelineData(JsonElement Row)
{
    public bool HasClientPoints => ConfigValue.Raw(Row, "m_kClientPoints") is { } points && points.ValueKind switch
    {
        JsonValueKind.Array => points.GetArrayLength() > 0,
        JsonValueKind.Object => points.EnumerateObject().Any(),
        _ => false,
    };
}

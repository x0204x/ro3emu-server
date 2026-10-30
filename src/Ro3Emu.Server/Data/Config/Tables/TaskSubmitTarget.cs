namespace Ro3Emu.Server.Data.Config;

/// <summary>
/// TaskConfig._kProgressTarget 的 CheckItemSubmit tuple。LC_InteractCondition.CheckItemSubmit
/// 讀 p2 物品與 p3 數量，不讀 p4；p4 的原服用途仍未知。接受已出現的 0／1，沿用交付扣料規則為 (inferred)。
/// </summary>
public readonly record struct TaskSubmitTarget(int Item, int Count, long TrailingParameter)
{
    public const int Condition = 103051;

    public static bool TryRead(ReadOnlySpan<long> values, out TaskSubmitTarget target)
    {
        target = default;
        if (values is not [Condition, > 0 and <= int.MaxValue, > 0 and <= int.MaxValue, 0 or 1]) return false;
        target = new((int)values[1], (int)values[2], values[3]);
        return true;
    }
}

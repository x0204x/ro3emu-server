using System.Reflection;
using System.Text;
using Ro3Emu.Server.Economy;

namespace Ro3Emu.Tests;

/// <summary>自造每日結算 child／revision／hash 組合；舊 AFK 與新增酒館皆維持嚴格形狀。</summary>
public sealed class EconomyDailyResetReaderTests
{
    private static readonly MethodInfo Validate = typeof(EconomyLedgerReader).GetMethod(
        "ValidateDailyResetChildren", BindingFlags.NonPublic | BindingFlags.Static)
        ?? throw new InvalidOperationException("每日結算讀取器驗證入口遺失。");

    private static Dictionary<string, long> Parts(bool afk, bool tavern)
    {
        var parts = new Dictionary<string, long> { ["progress"] = 1, ["daily"] = 1, ["tasks"] = 1 };
        if (afk) parts["wild_afk"] = 1;
        if (tavern) parts["tavern"] = 1;
        return parts;
    }

    private static object?[] Children(int count) => [true, .. Enumerable.Repeat<object?>(1, count - 1)];
    private static void Check(bool afk, bool tavern, object?[] children, string outcome) =>
        Validate.Invoke(null, [Parts(afk, tavern), children, Encoding.UTF8.GetBytes(outcome)]);

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void ExactOptionalControlChildrenRemainCompatible(bool afk, bool tavern)
    {
        string outcome = tavern ? "{\"TavernHash\":\"" + new string('A', 64) + "\"}" : "{}";
        Check(afk, tavern, Children(1 + (afk ? 1 : 0) + (tavern ? 1 : 0)), outcome);
        if (!tavern) Check(afk, false, Children(afk ? 2 : 1), "{\"TavernHash\":null}");
    }

    [Theory]
    [InlineData(false, false, 2)]
    [InlineData(true, false, 1)]
    [InlineData(false, true, 1)]
    [InlineData(true, true, 2)]
    public void OptionalRevisionCannotLoseOrInventAChild(bool afk, bool tavern, int count)
    {
        string outcome = tavern ? "{\"TavernHash\":\"" + new string('A', 64) + "\"}" : "{}";
        var error = Assert.Throws<TargetInvocationException>(() => Check(afk, tavern, Children(count), outcome));
        Assert.IsType<InvalidDataException>(error.InnerException);
    }

    [Theory]
    [InlineData(true, "{}")]
    [InlineData(true, "{\"TavernHash\":null}")]
    [InlineData(true, "{\"TavernHash\":123}")]
    [InlineData(true, "{\"TavernHash\":\"A\"}")]
    [InlineData(false, "{\"TavernHash\":\"A\"}")]
    public void TavernHashMustAgreeWithItsRevision(bool tavern, string outcome)
    {
        var error = Assert.Throws<TargetInvocationException>(() => Check(false, tavern, Children(tavern ? 2 : 1), outcome));
        Assert.IsType<InvalidDataException>(error.InnerException);
    }

    [Fact]
    public void SuccessfulChildValuesAreStillRequired()
    {
        var error = Assert.Throws<TargetInvocationException>(() => Check(false, false, [false], "{}"));
        Assert.IsType<InvalidDataException>(error.InnerException);
    }
}

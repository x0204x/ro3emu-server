using Ro3Emu.ClientHooks;

namespace Ro3Emu.Tests;

public sealed class TitleReplyBindingTests
{
    [Fact]
    public void RegisteringFailureHandlerNeverUnlocksOrRunsSuccess()
    {
        int successes = 0, failures = 0;
        Action success = () => successes++, error = () => failures++;
        var handlers = new Dictionary<string, (Action? Success, Action? Error, bool Show)>();
        Assert.True(TitleReplyBinding.Ensure<Action?>(success, null, error, false,
            (name, onSuccess, onError, show) => handlers.Add(name, (onSuccess, onError, show))));
        Assert.Equal(0, successes); Assert.Equal(0, failures);
        var bound = Assert.Single(handlers);
        Assert.Equal("TitleLevel", bound.Key); Assert.False(bound.Value.Show);
        Assert.Same(success, bound.Value.Success); Assert.Same(error, bound.Value.Error);
        Assert.IsType<Action>(bound.Value.Error)(); // 模擬已確定失敗的原生分派，不以開關視窗清鎖。
        Assert.Equal(1, failures); Assert.Equal(0, successes);
    }

    [Fact]
    public void ExistingErrorHandlerIsPreservedAndMissingNativeHandlerIsRefused()
    {
        Action original = () => { };
        int registrations = 0;
        void Register(string dataset, Action? success, Action? error, bool show) => registrations++;
        Assert.True(TitleReplyBinding.Ensure<Action?>(null, original, original, true, Register));
        Assert.False(TitleReplyBinding.Ensure<Action?>(null, null, null, true, Register));
        Assert.Equal(0, registrations);
    }

    [Fact]
    public void CustomLabelsFollowTheMapperCurrentLanguageWithoutCachingTheTranslatedResult()
    {
        bool traditional = true;
        var labels = new ClientLabelText(source => traditional ? source == "Title" ? "頭銜" : "關閉" : source);
        Assert.Equal("頭銜", labels.Map("Title")); Assert.Equal("關閉", labels.Map("Close"));
        traditional = false;
        Assert.Equal("Title", labels.Map("Title")); Assert.Equal("Close", labels.Map("Close"));
    }

    [Fact]
    public void MissingOrFailingMapperPreservesTheSourceInsteadOfForcingChinese()
    {
        Assert.Equal("Title", new ClientLabelText(null).Map("Title"));
        Assert.Equal("Close", new ClientLabelText(_ => throw new InvalidOperationException()).Map("Close"));
    }
}

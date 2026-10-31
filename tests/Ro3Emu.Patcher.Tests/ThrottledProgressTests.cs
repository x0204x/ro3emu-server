using Ro3Emu.Launcher;

namespace Ro3Emu.Patcher.Tests;

public sealed class ThrottledProgressTests
{
    [Fact]
    public void CompletingAQuickVerificationAlwaysPublishesTheLastFileCount()
    {
        var shown = new List<UpdateProgress>();
        var progress = new ThrottledProgress(shown.Add);
        progress.Report(new(UpdateStage.Verifying, 0, 30, FilesTotal: 2));
        progress.Report(new(UpdateStage.Verifying, 30, 30, "last.bin", 1, 2));
        var final = new UpdateProgress(UpdateStage.Verifying, 30, 30, "last.bin", 2, 2);
        progress.Report(final);
        Assert.Equal(final, shown[^1]);
    }

    [Fact]
    public void FileOnlyInspectionAndTheNextStepAreNotLostToThrottling()
    {
        var shown = new List<UpdateProgress>();
        var progress = new ThrottledProgress(shown.Add);
        progress.Report(new(UpdateStage.Inspecting, 0, 0, FilesTotal: 5));
        var final = new UpdateProgress(UpdateStage.Inspecting, 0, 0, FilesDone: 5, FilesTotal: 5);
        progress.Report(final);
        var next = new UpdateProgress(UpdateStage.Checking, 0, 0);
        progress.Report(next);
        Assert.Contains(final, shown);
        Assert.Equal(next, shown[^1]);
    }
}

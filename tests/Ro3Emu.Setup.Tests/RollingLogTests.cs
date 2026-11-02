namespace Ro3Emu.Setup.Tests;

public sealed class RollingLogTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "ro3emu-rollinglog-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }

    [Fact]
    public void AFullFileRollsOverAndOnlyTheNewestFilesStay()
    {
        Directory.CreateDirectory(_dir);
        for (int i = 0; i < 4; i++)
        {
            string old = Path.Combine(_dir, $"server-2020010{i + 1}-000000.log");
            File.WriteAllText(old, "old run");
            File.SetLastWriteTimeUtc(old, new DateTime(2020, 1, i + 1, 0, 0, 0, DateTimeKind.Utc));
        }
        using (var log = new RollingLog(_dir, maxBytes: 1000, keep: 3))
        {
            string first = log.Path;
            for (int i = 0; i < 100; i++) log.WriteLine(new string('x', 50));
            Assert.NotEqual(first, log.Path);
            Assert.EndsWith(".log", log.Path, StringComparison.Ordinal);
        }
        var left = Directory.GetFiles(_dir, "server-*.log");
        Assert.Equal(3, left.Length);
        Assert.DoesNotContain(left, f => Path.GetFileName(f).StartsWith("server-2020", StringComparison.Ordinal));
        Assert.All(left, f => Assert.True(new FileInfo(f).Length <= 1000 + 60, f));
    }
}

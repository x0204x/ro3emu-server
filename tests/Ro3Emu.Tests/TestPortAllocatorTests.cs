namespace Ro3Emu.Tests;

/// <summary>The test port allocator: fresh ports first, a rested port again, never a port another host may still be
/// starting on, and never a port that cannot be bound.</summary>
public sealed class TestPortAllocatorTests
{
    private long _now;
    private readonly HashSet<int> _bound = [];

    private TestPortAllocator Allocator(int first, int end) =>
        new(first, end, () => _now, port => !_bound.Contains(port), _ => 0);

    [Fact]
    public void FreshPortsComeFirstAndARecentPortIsNeverRepeated()
    {
        var ports = Allocator(41000, 41003);
        int[] handed = [ports.Next(), ports.Next(), ports.Next()];
        Assert.Equal([41000, 41001, 41002], handed.Order());
        _now += TestPortAllocator.ReuseAfterMs - 1;
        Assert.Throws<InvalidOperationException>(() => ports.Next());
    }

    [Fact]
    public void ARestedPortIsHandedOutAgainUnlessAHostStillListensOnIt()
    {
        var ports = Allocator(41000, 41002);
        Assert.Equal(41000, ports.Next());
        Assert.Equal(41001, ports.Next());
        _bound.Add(41000); // its host still runs
        _now += TestPortAllocator.ReuseAfterMs;
        Assert.Equal(41001, ports.Next());
        Assert.Throws<InvalidOperationException>(() => ports.Next()); // 41001 was just handed out again, 41000 is bound
        _bound.Remove(41000);
        Assert.Equal(41000, ports.Next());
    }

    [Fact]
    public void ThePortsOfTheApplicationAreNeverHandedOut()
    {
        var ports = Allocator(19999, 20001);
        Assert.Equal(19999, ports.Next());
        Assert.Throws<InvalidOperationException>(() => ports.Next());
    }
}

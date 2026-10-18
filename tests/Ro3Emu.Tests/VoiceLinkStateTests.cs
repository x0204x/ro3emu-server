using Ro3Emu.ClientHooks;

namespace Ro3Emu.Tests;

/// <summary>
/// The in-client voice link's end-of-life rule (src/Ro3Emu.ClientHooks/VoiceLinkState.cs, compiled into this project):
/// a failure on any thread reaches the main thread exactly once, a close the game asked for never does.
/// </summary>
public sealed class VoiceLinkStateTests
{
    /// <summary>What VoiceClient.Link does on each path: Close() on a game request, Fail(why) from the writer, the
    /// reader's end and an overflowing queue; every Fail tries to take the notice and posts OnClosed if it got it.</summary>
    private sealed class FakeLink
    {
        public readonly VoiceLinkState State = new();
        public int TransportClosed;
        public readonly List<string> Notices = new();

        public void Close()
        {
            if (State.RequestClose()) Interlocked.Increment(ref TransportClosed);
        }

        public void Fail(string why)
        {
            if (State.Fail(why)) Interlocked.Increment(ref TransportClosed);
            if (State.TakeFailureNotice()) lock (Notices) Notices.Add(State.Reason);
        }
    }

    [Fact]
    public void AWriteFailureIsReportedOnceEvenThoughTheReaderEndsAfterIt()
    {
        // The bug: the writer's error closed the link like a local Close, so the reader, ending next, reported nothing.
        var link = new FakeLink();
        link.Fail("send failed: IOException");
        link.Fail("IOException: read on a closed socket"); // the reader, unblocked by the closed socket
        Assert.Equal(["send failed: IOException"], link.Notices);
        Assert.Equal(1, link.TransportClosed);
        Assert.True(link.State.Closed);
        Assert.True(link.State.HasFailed);
    }

    [Fact]
    public void ACloseTheGameAskedForIsNotReported()
    {
        var link = new FakeLink();
        link.Close();
        link.Fail("ObjectDisposedException"); // the reader and writer see their socket go away
        link.Fail("send failed: ObjectDisposedException");
        Assert.Empty(link.Notices);
        Assert.Equal(1, link.TransportClosed);
        Assert.False(link.State.HasFailed);
        Assert.Equal("", link.State.Reason);
    }

    [Fact]
    public void TheGameClosingAfterAFailureChangesNothing()
    {
        var link = new FakeLink();
        link.Fail("kicked (code 4)");
        link.Close(); // OnClosed's own Drop
        Assert.Equal(["kicked (code 4)"], link.Notices);
        Assert.Equal(1, link.TransportClosed);
        Assert.True(link.State.HasFailed);
    }

    [Fact]
    public void RacingFailuresOnManyThreadsReportExactlyOnce()
    {
        for (int round = 0; round < 200; round++)
        {
            var link = new FakeLink();
            using var start = new Barrier(4);
            var threads = Enumerable.Range(0, 4).Select(i => new Thread(() =>
            {
                start.SignalAndWait();
                if (i == 3) link.Close(); else link.Fail("thread " + i);
            })).ToList();
            threads.ForEach(t => t.Start());
            threads.ForEach(t => t.Join());
            Assert.Equal(1, link.TransportClosed);
            // Either a failure won (one notice, with that thread's reason) or the game's close did (none).
            if (link.State.HasFailed) Assert.Equal([link.State.Reason], link.Notices);
            else Assert.Empty(link.Notices);
        }
    }
}

namespace Ro3Emu.Server.Game.Ownership;

/// <summary>Frozen presentation work belonging to one checked operation. Helpers finish their
/// authoritative effects without awaiting transport; callers release owners before flushing.</summary>
internal sealed class OwnerNotices : IDisposable
{
    private readonly OwnerContext _context;
    private readonly List<Func<Task>> _notices = [];
    internal OwnerNotices(OwnerContext context)
    {
        context.Registry.AssertCurrent(context);
        if (context.Notices != null) throw new InvalidOperationException("Owner notices already captured.");
        this._context = context;
        context.Notices = this;
    }
    internal void Add(GameSession session, Func<Task> send)
    {
        _context.Registry.AssertCurrent(_context);
        var role = session.Role;
        var lease = session.Attachment;
        _notices.Add(() => !ReferenceEquals(session.Role, role) || !ReferenceEquals(session.Attachment, lease)
            || lease?.Cancelled == true ? Task.CompletedTask : send());
    }
    public void Dispose()
    {
        _context.Registry.AssertCurrent(_context);
        if (ReferenceEquals(_context.Notices, this)) _context.Notices = null;
    }
    internal async Task FlushAsync(Func<Func<Task>, Task>? dispatch = null)
    {
        if (ReferenceEquals(_context.Notices, this)) throw new InvalidOperationException("Release notice capture before transport.");
        // Submit this committed batch in one loop turn so another operation cannot enqueue a
        // newer delta between two frozen packets merely because the first socket is backpressured.
        var pending = _notices.Select(notice => dispatch == null ? notice() : dispatch(notice)).ToArray();
        await Task.WhenAll(pending);
        _notices.Clear();
    }
}

namespace Ro3Emu.Server.Accounts.Persistence;

/// <summary>
/// A database operation refused for a persistence reason the caller can act on, kept apart from
/// <see cref="InvalidOperationException"/> (a program error) so a broad catch can tell overload from a bug:
/// <see cref="DbQueueFullException"/> (the plan was never admitted) and <see cref="RolePlanConflictException"/> (a
/// captured revision changed). In both cases nothing of the plan was written.
/// </summary>
public abstract class PersistenceException : Exception
{
    protected PersistenceException() { }
    protected PersistenceException(string message) : base(message) { }
    protected PersistenceException(string message, Exception innerException) : base(message, innerException) { }
}

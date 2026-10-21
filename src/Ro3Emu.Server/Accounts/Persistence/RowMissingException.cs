namespace Ro3Emu.Server.Accounts.Persistence;

/// <summary>
/// A plan's update found no row to change: the aggregate was deleted after the caller captured it. Callers that can
/// lose such a race catch it by table and id instead of by message text.
/// </summary>
internal sealed class RowMissingException(string table, long id, string message) : InvalidOperationException(message)
{
    public string Table { get; } = table;
    public long Id { get; } = id;

    public bool Is(string table, long id) => Table == table && Id == id;
}

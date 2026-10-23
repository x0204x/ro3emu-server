namespace Ro3Emu.Server.Accounts.Persistence;

/// <summary>A store's cache over database rows that a writer job can make stale.</summary>
public enum WrittenCache { Guild, AdventureGroup }

/// <summary>
/// The generation of each <see cref="WrittenCache"/> of one database. A plan that writes the cached rows names the cache
/// (<see cref="DbWriter.AfterJob"/>); that database's writer bumps the generation when the job ends, and the store drops
/// what it cached under an older generation. Each <see cref="AccountStore"/> has its own, so a write to one host's
/// database never invalidates another host's cache in the same process.
/// </summary>
public sealed class CacheGenerations
{
    private readonly long[] _generations = new long[Enum.GetValues<WrittenCache>().Length];

    public long Current(WrittenCache cache) => Volatile.Read(ref _generations[(int)cache]);

    internal void Bump(WrittenCache cache) => Interlocked.Increment(ref _generations[(int)cache]);
}

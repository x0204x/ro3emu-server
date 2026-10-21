using Microsoft.Data.Sqlite;
using Ro3Emu.Server.Accounts.Persistence;

namespace Ro3Emu.Server.Accounts;

/// <summary>Friend links (both directions stored), pending applications and blacklists, by role id.</summary>
public sealed partial class FriendStore(AccountStore accounts)
{
    private readonly AccountStore _accounts = accounts;

    private T Read<T>(FriendReadKind kind, long a, long b = 0, int max = 100, long cutoff = 0)
        => _accounts.ReadSync<T>(new FriendReadPlan(kind, a, b, max, cutoff));
    private Task<T> ReadAsync<T>(FriendReadKind kind, long a, long b = 0, int max = 100, long cutoff = 0)
        => _accounts.ReadAsync<T>(new FriendReadPlan(kind, a, b, max, cutoff));
    private void Write(FriendWritePlan plan) => _accounts.WriteLegacySync<bool>(plan);
    private Task<bool> WriteAsync(FriendWritePlan plan) => _accounts.WriteLegacyAsync<bool>(plan);

    public List<long> Friends(long role) => Read<List<long>>(FriendReadKind.Friends, role);
    public Task<List<long>> FriendsAsync(long role) => ReadAsync<List<long>>(FriendReadKind.Friends, role);

    public List<long> Applicants(long role) => Read<List<long>>(FriendReadKind.Applicants, role);
    public Task<List<long>> ApplicantsAsync(long role) => ReadAsync<List<long>>(FriendReadKind.Applicants, role);

    public List<long> Blacklist(long role) => Read<List<long>>(FriendReadKind.Blacklist, role);
    public Task<List<long>> BlacklistAsync(long role) => ReadAsync<List<long>>(FriendReadKind.Blacklist, role);

    public List<long> BlacklistedBy(long role) => Read<List<long>>(FriendReadKind.BlacklistedBy, role);
    public Task<List<long>> BlacklistedByAsync(long role) => ReadAsync<List<long>>(FriendReadKind.BlacklistedBy, role);

    public bool AreFriends(long a, long b) => Read<bool>(FriendReadKind.AreFriends, a, b);
    public Task<bool> AreFriendsAsync(long a, long b) => ReadAsync<bool>(FriendReadKind.AreFriends, a, b);

    public bool HasApplied(long target, long applicant) => Read<bool>(FriendReadKind.HasApplied, target, applicant);
    public Task<bool> HasAppliedAsync(long target, long applicant) => ReadAsync<bool>(FriendReadKind.HasApplied, target, applicant);

    public bool Blocks(long owner, long other) => Read<bool>(FriendReadKind.Blocks, owner, other);
    public Task<bool> BlocksAsync(long owner, long other) => ReadAsync<bool>(FriendReadKind.Blocks, owner, other);

    public void Apply(long target, long applicant) => Write(new(FriendWriteKind.Apply, target, applicant, DateTimeOffset.UtcNow.ToUnixTimeSeconds()));
    public Task ApplyAsync(long target, long applicant) => WriteAsync(new(FriendWriteKind.Apply, target, applicant, DateTimeOffset.UtcNow.ToUnixTimeSeconds()));

    public void RemoveApplication(long target, long applicant) => Write(new(FriendWriteKind.RemoveApplication, target, applicant));
    public Task RemoveApplicationAsync(long target, long applicant) => WriteAsync(new(FriendWriteKind.RemoveApplication, target, applicant));

    public void Link(long a, long b) => Write(new(FriendWriteKind.Link, a, b, DateTimeOffset.UtcNow.ToUnixTimeSeconds()));
    public Task LinkAsync(long a, long b) => WriteAsync(new(FriendWriteKind.Link, a, b, DateTimeOffset.UtcNow.ToUnixTimeSeconds()));

    public void Unlink(long a, long b) => Write(new(FriendWriteKind.Unlink, a, b));
    public Task UnlinkAsync(long a, long b) => WriteAsync(new(FriendWriteKind.Unlink, a, b));

    public void Block(long owner, long other) => Write(new(FriendWriteKind.Block, owner, other));
    public Task BlockAsync(long owner, long other) => WriteAsync(new(FriendWriteKind.Block, owner, other));

    public void Unblock(long owner, long other) => Write(new(FriendWriteKind.Unblock, owner, other));
    public Task UnblockAsync(long owner, long other) => WriteAsync(new(FriendWriteKind.Unblock, owner, other));

}

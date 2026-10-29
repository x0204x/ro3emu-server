using Ro3Emu.Server.Accounts.Persistence;

namespace Ro3Emu.Server.Accounts;

public sealed partial class AccountStore
{
    /// <summary>
    /// The gift code <paramref name="code"/> (matched case-insensitively) with its uses so far, in all and by
    /// <paramref name="account"/>, or null. Read from the database on every call: the admin panel writes gift_code
    /// from another process, so nothing about a code is cached.
    /// </summary>
    public Task<GiftCodeState?> ReadGiftCodeAsync(string code, string account) =>
        ReadAsync<GiftCodeState?>(new GiftCodeRead(code, account));
}

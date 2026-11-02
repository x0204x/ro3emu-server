namespace Ro3Emu.Panel;

/// <summary>
/// The rule every new account name meets, wherever the account is created: the game's first login (SdkEndpoints) and
/// the panel's sign-up, which adds its own stricter rule on top (PanelSite.NameProblem). (inferred) The client has no
/// rule of its own we know; this one only keeps names short and printable, so it never refuses a name a player could
/// type in the login box. Invisible format characters count as unprintable: a zero-width space or a right-to-left mark
/// would make a second account that reads like "admin". Logins of existing accounts never go through it.
/// </summary>
public static class AccountNames
{
    public const int MaxLength = 64;

    /// <summary>Why <paramref name="name"/> (already trimmed) cannot be a new account's name, or null when it can.</summary>
    public static string? Problem(string name) =>
        name.Length == 0 ? "Choose an account name."
        : name.Length > MaxLength ? $"An account name has at most {MaxLength} characters."
        : name.Any(c => char.IsControl(c) || char.IsWhiteSpace(c) || char.IsSurrogate(c)
            || char.GetUnicodeCategory(c) == System.Globalization.UnicodeCategory.Format) ? "An account name has no spaces or control characters."
        : null;
}

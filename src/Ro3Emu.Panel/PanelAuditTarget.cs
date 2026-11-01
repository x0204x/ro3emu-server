using System.Text.RegularExpressions;

namespace Ro3Emu.Panel;

/// <summary>
/// The thing an audit row is about, read from its action and target column: a character (its id), an account, a
/// character known only by its name (GM console, mail), a report, the news, the gift codes or the server. The page
/// links each kind to its own page. Rows written before a target format changed still read: a character action's
/// "Name (id)" and the old "role:id" of a rename or restore (its name then comes from the detail).
/// </summary>
/// <param name="Kind">character, account, character-name, report, news, gift-code, server or none.</param>
/// <param name="Key">The character id, account name, character name or report id; empty for the others.</param>
/// <param name="Label">The text the audit log shows.</param>
public sealed partial record PanelAuditTarget(string Kind, string Key, string Label)
{
    private static readonly string[] AccountActions = ["ban", "unban", "role", "2fa-reset", "panel-logout"];

    public static PanelAuditTarget Of(string action, string target, string detail)
    {
        if (action.StartsWith("char-", StringComparison.Ordinal)) return Character(action, target, detail);
        if (AccountActions.Contains(action)) return new("account", target, target);
        if (action == "report" && target.StartsWith("report:", StringComparison.Ordinal))
            return new("report", target[7..], "Report #" + target[7..]);
        if (action.StartsWith("news-", StringComparison.Ordinal)) return new("news", "", "News");
        if (action.StartsWith("gift-code-", StringComparison.Ordinal)) return new("gift-code", "", target);
        if (action == "gm" && target.Length > 0) return new("character-name", target, target);
        if (action == "mail") return Mail(target);
        if (target == "server") return new("server", "", "Server");
        return new("none", "", target);
    }

    /// <summary>"Name (id)", or the old "role:id" of a rename ("old => new") or a restore ("name of account").</summary>
    private static PanelAuditTarget Character(string action, string target, string detail)
    {
        if (NameAndId().Match(target) is { Success: true } named) return new("character", named.Groups[2].Value, named.Groups[1].Value);
        if (!target.StartsWith("role:", StringComparison.Ordinal)) return new("none", "", target);
        string id = target[5..];
        string? name = action switch
        {
            "char-rename" when detail.LastIndexOf(" => ", StringComparison.Ordinal) is var at and >= 0 => detail[(at + 4)..],
            "char-restore" when detail.LastIndexOf(" of ", StringComparison.Ordinal) is var at and > 0 => detail[..at],
            _ => null,
        };
        return new("character", id, string.IsNullOrEmpty(name) ? "Character " + id : name);
    }

    /// <summary>Mail's "character:Name", "account:Name" or "everyone ..." (each maybe with " (online)").</summary>
    private static PanelAuditTarget Mail(string target)
    {
        string to = target.EndsWith(" (online)", StringComparison.Ordinal) ? target[..^9] : target;
        if (to.StartsWith("character:", StringComparison.Ordinal)) return new("character-name", to[10..], to[10..]);
        if (to.StartsWith("account:", StringComparison.Ordinal)) return new("account", to[8..], to[8..]);
        return new("none", "", target);
    }

    [GeneratedRegex(@"^(.*) \((\d+)\)$")]
    private static partial Regex NameAndId();
}

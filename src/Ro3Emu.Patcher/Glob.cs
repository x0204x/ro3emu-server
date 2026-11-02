using System.Text;
using System.Text.RegularExpressions;

namespace Ro3Emu.Patcher;

/// <summary>
/// Exclude patterns of a release: <c>*</c> stays inside one path segment, <c>**</c> crosses segments, a trailing
/// <c>/</c> excludes a directory wherever it appears, a pattern without <c>/</c> also matches a bare file name.
/// </summary>
public sealed class GlobSet
{
    readonly List<Regex> _patterns = [];

    public GlobSet(IEnumerable<string> patterns)
    {
        foreach (string raw in patterns)
        {
            string pattern = raw.Replace('\\', '/').Trim();
            if (pattern.Length == 0)
            {
                continue;
            }
            if (pattern.EndsWith('/'))
            {
                _patterns.Add(new Regex("(^|/)" + Translate(pattern.TrimEnd('/')) + "/", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant));
            }
            else if (!pattern.Contains('/'))
            {
                _patterns.Add(new Regex("(^|/)" + Translate(pattern) + "$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant));
            }
            else
            {
                _patterns.Add(new Regex("^" + Translate(pattern.TrimStart('/')) + "$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant));
            }
        }
    }

    public bool IsMatch(string relativePath) => _patterns.Any(p => p.IsMatch(relativePath));

    static string Translate(string glob)
    {
        var sb = new StringBuilder();
        for (int i = 0; i < glob.Length; i++)
        {
            char c = glob[i];
            if (c == '*' && i + 1 < glob.Length && glob[i + 1] == '*')
            {
                sb.Append(".*");
                i++;
            }
            else if (c == '*')
            {
                sb.Append("[^/]*");
            }
            else if (c == '?')
            {
                sb.Append("[^/]");
            }
            else
            {
                sb.Append(Regex.Escape(c.ToString()));
            }
        }
        return sb.ToString();
    }
}

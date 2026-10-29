using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;

namespace Ro3Emu.Loc
{
    /// <summary>
    /// Chinese -> English at the point text is drawn. Cecil puts a call to <see cref="Map"/>
    /// in front of TMP_Text.text / SetText, TextMeshPro(UGUI).Awake and HUDTextHelper.SetWordCell, so baked prefab text,
    /// Lua literals, raw config strings and C# strings all pass through here.
    /// <list type="bullet">
    /// <item>fast path: a string without a CJK character is returned as is;</item>
    /// <item>exact map, then a normalized retry (trim, zero-width spaces, \r\n);</item>
    /// <item>rich text and multi-line text: each tag-free / line segment is mapped on its own;</item>
    /// <item>templates: keys with %d / %s / {0} / ${1} / @{1} match runtime-built strings;</item>
    /// <item>misses are appended once to &lt;client&gt;/ro3emu/loc_misses.tsv (collect mode).</item>
    /// </list>
    /// Data: the embedded loc_en.tsv, then &lt;client&gt;/ro3emu/loc_en.tsv. Lines are "source\ttarget" with
    /// \t \n \\ escaped; the last matching row wins. Source-controlled Localization-ID corrections are separate.
    /// </summary>
    public static class Ro3Loc
    {
        private static readonly object Gate = new object();
        private static Dictionary<string, string> _map;
        private static List<Template> _templates;
        private static Dictionary<long, string> _sids;
        private static readonly Dictionary<string, string> Derived = new Dictionary<string, string>(StringComparer.Ordinal);
        private static readonly HashSet<string> Misses = new HashSet<string>(StringComparer.Ordinal);
        private static string _dir;
        private static bool _failed;
        private const int MaxCached = 50000;

        /// <summary>Hook 0 = TMP setter, 1 = TMP Awake, 2 = HUD; only used to tag misses.</summary>
        public static string Map(string text) => MapFrom(text, 0);

        public static string MapAwake(string text) => MapFrom(text, 1);

        public static string MapHud(string text) => MapFrom(text, 2);

        /// <summary>
        /// LanguageMain.Get(sid) result: source-controlled SID corrections win first. A sid that is in no Localization
        /// table comes back "" / "None" / "Not Find Key", so the label would otherwise be empty. Those missing sids
        /// map to the Chinese baked into the prefab (loc_sids.tsv), then through the ordinary text map.
        /// </summary>
        public static string Sid(string value, long sid)
        {
            if (SidOverrides.Count != 0 && SidOverrides.TryGetValue(sid, out var corrected)) return corrected;
            if (!string.IsNullOrEmpty(value) && value != "None" && !value.StartsWith("Not Find Key", StringComparison.Ordinal)) return value;
            try
            {
                string zh = null;
                lock (Gate)
                {
                    if (_map == null) Load();
                    if (_sids != null) _sids.TryGetValue(sid, out zh);
                }
                if (zh != null) return MapFrom(zh, 0);
            }
            catch
            {
                // keep the value the client resolved
            }
            return value;
        }

        /// <summary>English the client ships that we replace (en_overrides.tsv): exact strings, checked before the CJK fast path.</summary>
        private static readonly Dictionary<string, string> EnglishOverrides = LoadEnglishOverrides();

        /// <summary>Exact Localization IDs whose visible condition text needs more detail than the shipped row.</summary>
        private static readonly Dictionary<long, string> SidOverrides = LoadSidOverrides();

        private static Dictionary<string, string> LoadEnglishOverrides()
        {
            var map = new Dictionary<string, string>(StringComparer.Ordinal);
            try
            {
                using (var stream = typeof(Ro3Loc).Assembly.GetManifestResourceStream("en_overrides.tsv"))
                using (var reader = stream == null ? null : new StreamReader(stream, Encoding.UTF8))
                {
                    string line;
                    while (reader != null && (line = reader.ReadLine()) != null)
                    {
                        int tab = line.IndexOf('\t');
                        if (line.Length == 0 || line[0] == '#' || tab <= 0) continue;
                        map[Unescape(line.Substring(0, tab)).TrimStart('﻿')] = Unescape(line.Substring(tab + 1));
                    }
                }
            }
            catch
            {
                // no overrides
            }
            return map;
        }

        private static Dictionary<long, string> LoadSidOverrides()
        {
            var map = new Dictionary<long, string>();
            try
            {
                using (var stream = typeof(Ro3Loc).Assembly.GetManifestResourceStream("loc_sid_overrides.tsv"))
                using (var reader = stream == null ? null : new StreamReader(stream, Encoding.UTF8))
                {
                    string line;
                    while (reader != null && (line = reader.ReadLine()) != null)
                    {
                        int tab = line.IndexOf('\t');
                        if (line.Length == 0 || line[0] == '#' || tab <= 0 || !long.TryParse(line.Remove(tab), out long sid)) continue;
                        string text = Unescape(line.Remove(0, tab + 1));
                        if (text.Length != 0) map[sid] = text;
                    }
                }
            }
            catch
            {
                // no SID overrides
            }
            return map;
        }

        private static string MapFrom(string text, int hook)
        {
            if (text == null || text.Length == 0) return text;
            if (EnglishOverrides.Count != 0 && EnglishOverrides.TryGetValue(text, out var replaced)) return replaced;
            if (!HasCjk(text)) return text;
            try
            {
                lock (Gate)
                {
                    if (_map == null) Load();
                    if (_failed) return text;
                    if (_map.TryGetValue(text, out var en)) return en;
                    if (Derived.TryGetValue(text, out en)) return en ?? text;
                    if (Misses.Contains(text)) return text;
                    en = Resolve(text);
                    if (en != null)
                    {
                        if (Derived.Count < MaxCached) Derived[text] = en;
                        return en;
                    }
                    if (Misses.Count < MaxCached && Misses.Add(text)) LogMiss(text, hook);
                    return text;
                }
            }
            catch
            {
                return text;
            }
        }

        private static bool HasCjk(string s)
        {
            for (int i = 0; i < s.Length; i++)
                if (IsCjk(s[i])) return true;
            return false;
        }

        // Han and kana only: full-width punctuation alone ("World\uFF1A World") needs no translation.
        private static bool IsCjk(char c) =>
            (c >= '\u3040' && c <= '\u9FFF') || (c >= '\u2E80' && c <= '\u2FDF') || (c >= '\uF900' && c <= '\uFAFF');

        /// <summary>Normalized form, segments, then templates, then CJK runs; null when nothing covers every CJK part.</summary>
        private static string Resolve(string text)
        {
            string norm = Normalize(text);
            if (!ReferenceEquals(norm, text) && _map.TryGetValue(norm, out var en)) return en;
            if (text.IndexOf('<') >= 0 || text.IndexOf('\n') >= 0) return Segments(text);
            return MatchTemplate(text) ?? CjkRuns(text);
        }

        /// <summary>
        /// Text the client composed from an English format and a Chinese name ("利刃重击 is on cooldown.", a skill name
        /// from a table the client reads in Chinese): each maximal CJK run (a middle dot may join two) is mapped on its own.
        /// All or nothing: one unknown run leaves the whole string as it was.
        /// </summary>
        private static string CjkRuns(string text)
        {
            StringBuilder sb = null;
            int i = 0, copied = 0;
            while (i < text.Length)
            {
                if (!IsCjk(text[i])) { i++; continue; }
                int end = i + 1;
                while (end < text.Length && (IsCjk(text[end]) || (text[end] == '·' || text[end] == '・') && end + 1 < text.Length && IsCjk(text[end + 1]))) end++;
                if (i == 0 && end == text.Length) return null; // the whole string: the exact lookup already missed it
                if (!_map.TryGetValue(text.Substring(i, end - i), out var en) || HasCjk(en)) return null;
                if (sb == null) sb = new StringBuilder(text.Length * 2);
                sb.Append(text, copied, i - copied).Append(en);
                copied = i = end;
            }
            if (sb == null) return null;
            return sb.Append(text, copied, text.Length - copied).ToString();
        }

        private static string Normalize(string s)
        {
            string t = s.Trim().Replace("\u200B", "").Replace("\r\n", "\n");
            return t == s ? s : t;
        }

        /// <summary>Maps each text run between rich-text tags and newlines; the tags and separators stay.</summary>
        private static string Segments(string text)
        {
            var sb = new StringBuilder(text.Length * 2);
            int i = 0;
            bool any = false;
            while (i < text.Length)
            {
                char c = text[i];
                if (c == '<')
                {
                    int close = text.IndexOf('>', i);
                    if (close < 0) { sb.Append(text, i, text.Length - i); break; }
                    sb.Append(text, i, close - i + 1);
                    i = close + 1;
                    continue;
                }
                if (c == '\n' || c == '\r') { sb.Append(c); i++; continue; }
                int end = i;
                while (end < text.Length && text[end] != '<' && text[end] != '\n' && text[end] != '\r') end++;
                string run = text.Substring(i, end - i);
                if (HasCjk(run))
                {
                    string core = run.Trim();
                    string en = null;
                    if (!_map.TryGetValue(core, out en)) en = MatchTemplate(core) ?? CjkRuns(core);
                    if (en == null) return null;
                    int lead = run.IndexOf(core, StringComparison.Ordinal);
                    sb.Append(run, 0, lead).Append(en).Append(run, lead + core.Length, run.Length - lead - core.Length);
                    any = true;
                }
                else sb.Append(run);
                i = end;
            }
            return any ? sb.ToString() : null;
        }

        private static string MatchTemplate(string text)
        {
            foreach (var t in _templates)
            {
                var m = t.Pattern.Match(text);
                if (!m.Success) continue;
                string en = t.Apply(m);
                if (en != text && !HasCjk(en)) return en;
            }
            return null;
        }

        private static void Load()
        {
            _map = new Dictionary<string, string>(StringComparer.Ordinal);
            _templates = new List<Template>();
            try
            {
                _dir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory ?? ".", "ro3emu");
                using (var stream = typeof(Ro3Loc).Assembly.GetManifestResourceStream("loc_en.tsv"))
                    if (stream != null) Read(new StreamReader(stream, Encoding.UTF8));
                _sids = new Dictionary<long, string>();
                using (var stream = typeof(Ro3Loc).Assembly.GetManifestResourceStream("loc_sids.tsv"))
                    if (stream != null) ReadSids(new StreamReader(stream, Encoding.UTF8));
                string overrides = Path.Combine(_dir, "loc_en.tsv");
                if (File.Exists(overrides)) Read(new StreamReader(overrides, Encoding.UTF8));
            }
            catch
            {
                _failed = _map.Count == 0;
            }
        }

        private static void Read(TextReader reader)
        {
            using (reader)
            {
                string line;
                while ((line = reader.ReadLine()) != null)
                {
                    if (line.Length == 0 || line[0] == '#') continue;
                    int tab = line.IndexOf('\t');
                    if (tab <= 0) continue;
                    string zh = Unescape(line.Substring(0, tab)).TrimStart('\uFEFF');
                    string en = Unescape(line.Substring(tab + 1));
                    if (en.Length == 0) continue;
                    _map[zh] = en;
                    var template = Template.TryCreate(zh, en);
                    if (template != null) _templates.Add(template);
                }
            }
        }

        private static void ReadSids(TextReader reader)
        {
            using (reader)
            {
                string line;
                while ((line = reader.ReadLine()) != null)
                {
                    int tab = line.IndexOf('\t');
                    if (tab <= 0 || line[0] == '#' || !long.TryParse(line.Substring(0, tab), out long sid)) continue;
                    _sids[sid] = Unescape(line.Substring(tab + 1));
                }
            }
        }

        private static string Unescape(string s)
        {
            if (s.IndexOf('\\') < 0) return s;
            var sb = new StringBuilder(s.Length);
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                if (c == '\\' && i + 1 < s.Length)
                {
                    char n = s[++i];
                    sb.Append(n == 't' ? '\t' : n == 'n' ? '\n' : n == 'r' ? '\r' : n);
                }
                else sb.Append(c);
            }
            return sb.ToString();
        }

        private static void LogMiss(string text, int hook)
        {
            try
            {
                Directory.CreateDirectory(_dir);
                string escaped = text.Replace("\\", "\\\\").Replace("\t", "\\t").Replace("\r", "\\r").Replace("\n", "\\n");
                File.AppendAllText(Path.Combine(_dir, "loc_misses.tsv"), escaped + "\t" + (hook == 0 ? "set" : hook == 1 ? "awake" : "hud") + "\n", Encoding.UTF8);
            }
            catch
            {
                // collect mode is best effort
            }
        }

        /// <summary>A key with placeholders, compiled to an anchored regex; the English side reuses the captures.</summary>
        private sealed class Template
        {
            private static readonly Regex Token = new Regex(@"%(?:\d+\$)?[-+ 0#]*\d*(?:\.\d+)?[sdfiuxX]|\{\d+\}|[$@]\{\d+\}", RegexOptions.CultureInvariant);
            public Regex Pattern;
            private string[] _zhTokens;
            private string _en;

            public static Template TryCreate(string zh, string en)
            {
                var tokens = Token.Matches(zh);
                // Only keys with Chinese around the tokens: "{0}" alone would match every string.
                if (tokens.Count == 0 || !HasCjk(Token.Replace(zh, ""))) return null;
                var pattern = new StringBuilder("^");
                var names = new string[tokens.Count];
                int last = 0;
                for (int i = 0; i < tokens.Count; i++)
                {
                    pattern.Append(Regex.Escape(zh.Substring(last, tokens[i].Index - last)));
                    pattern.Append("(.+?)");
                    names[i] = tokens[i].Value;
                    last = tokens[i].Index + tokens[i].Length;
                }
                pattern.Append(Regex.Escape(zh.Substring(last))).Append('$');
                return new Template { Pattern = new Regex(pattern.ToString(), RegexOptions.Singleline | RegexOptions.CultureInvariant), _zhTokens = names, _en = en };
            }

            public string Apply(Match m)
            {
                int nextPercent = 0;
                return Token.Replace(_en, t =>
                {
                    int index = -1;
                    if (t.Value[0] == '%')
                    {
                        // printf-style: in order among the % tokens of the key
                        int seen = 0;
                        for (int i = 0; i < _zhTokens.Length; i++)
                            if (_zhTokens[i][0] == '%' && seen++ == nextPercent) { index = i; break; }
                        nextPercent++;
                    }
                    else index = Array.IndexOf(_zhTokens, t.Value);
                    return index >= 0 && index + 1 < m.Groups.Count ? m.Groups[index + 1].Value : t.Value;
                });
            }
        }
    }
}

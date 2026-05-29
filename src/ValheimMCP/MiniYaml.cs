using System.Collections.Generic;
using System.Globalization;

namespace ValheimMCP
{
    /// <summary>
    ///     Minimal YAML reader for a flat 2-level config: top-level sections, nested
    ///     <c>key: value</c> scalars, and block lists (<c>- item</c>). Inline
    ///     <c>[]</c> / <c>[a, b]</c> flow lists are also accepted. Full-line comments
    ///     (<c>#</c>) and blanks are ignored, as are trailing <c> #</c> comments.
    ///     NOT a general YAML parser — just enough for this mod's config, with zero
    ///     dependencies. Keys are addressed by dotted path, e.g. <c>server.host</c>.
    /// </summary>
    internal sealed class MiniYaml
    {
        private readonly Dictionary<string, string> _scalars = new();
        private readonly Dictionary<string, List<string>> _lists = new();

        public static MiniYaml Parse(string text)
        {
            var y = new MiniYaml();
            string section = null;
            string listKey = null;

            foreach (var rawLine in text.Replace("\r\n", "\n").Split('\n'))
            {
                var line = StripComment(rawLine);
                var trimmed = line.Trim();
                if (trimmed.Length == 0) continue;

                var indent = line.Length - line.TrimStart(' ').Length;

                if (trimmed[0] == '-')
                {
                    if (listKey != null)
                    {
                        var item = Unquote(trimmed.Substring(1).Trim());
                        if (item.Length > 0) y._lists[listKey].Add(item);
                    }

                    continue;
                }

                var colon = trimmed.IndexOf(':');
                if (colon < 0) continue;
                var key = trimmed.Substring(0, colon).Trim();
                var val = trimmed.Substring(colon + 1).Trim();

                if (indent == 0)
                {
                    listKey = null;
                    if (val.Length == 0)
                    {
                        section = key; // a section begins
                    }
                    else
                    {
                        y._scalars[key] = Unquote(val); // top-level scalar
                        section = null;
                    }

                    continue;
                }

                var path = section != null ? section + "." + key : key;
                if (val.Length == 0)
                {
                    // nested map / block list begins
                    listKey = path;
                    if (!y._lists.ContainsKey(path)) y._lists[path] = new List<string>();
                }
                else if (val.StartsWith("[") && val.EndsWith("]"))
                {
                    var inner = val.Substring(1, val.Length - 2);
                    var list = new List<string>();
                    foreach (var part in inner.Split(','))
                    {
                        var p = Unquote(part.Trim());
                        if (p.Length > 0) list.Add(p);
                    }

                    y._lists[path] = list;
                    listKey = null;
                }
                else
                {
                    y._scalars[path] = Unquote(val);
                    listKey = null;
                }
            }

            return y;
        }

        public string Get(string path, string dflt)
        {
            return _scalars.TryGetValue(path, out var v) ? v : dflt;
        }

        public int GetInt(string path, int dflt)
        {
            return _scalars.TryGetValue(path, out var v) &&
                   int.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out var i)
                ? i
                : dflt;
        }

        public List<string> GetList(string path)
        {
            return _lists.TryGetValue(path, out var l) ? l : new List<string>();
        }

        private static string StripComment(string line)
        {
            // Full-line comment.
            var t = line.TrimStart();
            if (t.Length > 0 && t[0] == '#') return "";
            // Trailing " #..." comment (our config values never contain '#').
            var h = line.IndexOf(" #", System.StringComparison.Ordinal);
            return h >= 0 ? line.Substring(0, h) : line;
        }

        private static string Unquote(string s)
        {
            if (s.Length >= 2 &&
                ((s[0] == '"' && s[s.Length - 1] == '"') || (s[0] == '\'' && s[s.Length - 1] == '\'')))
                return s.Substring(1, s.Length - 2);
            return s;
        }
    }
}
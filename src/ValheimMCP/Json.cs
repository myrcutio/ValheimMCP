using System.Collections.Generic;
using System.Text;

namespace ValheimMCP
{
    /// <summary>
    ///     Minimal hand-rolled JSON writer. Avoids any NuGet/Unity serialization
    ///     dependency (and the "JsonUtility only on main thread" hazard, since
    ///     responses are built on the listener's background thread).
    /// </summary>
    internal static class Json
    {
        public static string Str(string s)
        {
            if (s == null) return "null";
            var sb = new StringBuilder(s.Length + 2);
            sb.Append('"');
            foreach (var c in s)
            {
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default:
                        if (c < 0x20) sb.Append("\\u").Append(((int)c).ToString("x4"));
                        else sb.Append(c);
                        break;
                }
            }
            sb.Append('"');
            return sb.ToString();
        }

        public static string Array(IEnumerable<string> items)
        {
            var sb = new StringBuilder();
            sb.Append('[');
            var first = true;
            foreach (var item in items)
            {
                if (!first) sb.Append(',');
                first = false;
                sb.Append(Str(item));
            }
            sb.Append(']');
            return sb.ToString();
        }

        public static string Error(string message)
        {
            return "{\"ok\":false,\"error\":" + Str(message) + "}";
        }

        public static string Health(bool inGame)
        {
            return "{\"ok\":true,\"inGame\":" + (inGame ? "true" : "false") + "}";
        }

        public static string Commands(IReadOnlyList<KeyValuePair<string, string>> commands)
        {
            var sb = new StringBuilder();
            sb.Append("{\"ok\":true,\"commands\":[");
            for (var i = 0; i < commands.Count; i++)
            {
                if (i > 0) sb.Append(',');
                sb.Append("{\"name\":").Append(Str(commands[i].Key))
                    .Append(",\"description\":").Append(Str(commands[i].Value)).Append('}');
            }
            sb.Append("]}");
            return sb.ToString();
        }

        public static string LogResult(long cursor, int matching, int dropped, IEnumerable<string> lines)
        {
            return "{\"ok\":true,\"cursor\":" + cursor +
                   ",\"matching\":" + matching +
                   ",\"dropped\":" + dropped +
                   ",\"lines\":" + Array(lines) + "}";
        }

        public static string CommandResult(string ran, CommandResult result)
        {
            var sb = new StringBuilder();
            sb.Append("{\"ok\":").Append(result.Ok ? "true" : "false")
                .Append(",\"ran\":").Append(Str(ran))
                .Append(",\"output\":").Append(Array(result.Output));
            if (!result.Ok)
                sb.Append(",\"error\":").Append(Str(result.Error));
            sb.Append('}');
            return sb.ToString();
        }
    }
}

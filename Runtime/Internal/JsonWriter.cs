using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace Reflect.Internal
{
    /// <summary>
    /// Tiny allocation-light JSON writer. Good enough for outbound events —
    /// no reflection, no dependencies, handles primitives + nested maps/lists.
    /// </summary>
    internal static class JsonWriter
    {
        public static void Kv(StringBuilder sb, string key, string value)
        {
            WriteString(sb, key);
            sb.Append(':');
            if (value == null) sb.Append("null");
            else WriteString(sb, value);
        }

        public static void KvNum(StringBuilder sb, string key, long value)
        {
            WriteString(sb, key);
            sb.Append(':');
            sb.Append(value.ToString(CultureInfo.InvariantCulture));
        }

        public static void KvNum(StringBuilder sb, string key, double value)
        {
            WriteString(sb, key);
            sb.Append(':');
            sb.Append(value.ToString("R", CultureInfo.InvariantCulture));
        }

        public static void KvBool(StringBuilder sb, string key, bool value)
        {
            WriteString(sb, key);
            sb.Append(':');
            sb.Append(value ? "true" : "false");
        }

        public static void WriteString(StringBuilder sb, string s)
        {
            if (s == null) { sb.Append("null"); return; }
            sb.Append('"');
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                switch (c)
                {
                    case '"':  sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\b': sb.Append("\\b");  break;
                    case '\f': sb.Append("\\f");  break;
                    case '\n': sb.Append("\\n");  break;
                    case '\r': sb.Append("\\r");  break;
                    case '\t': sb.Append("\\t");  break;
                    default:
                        if (c < 0x20) sb.AppendFormat(CultureInfo.InvariantCulture, "\\u{0:x4}", (int)c);
                        else sb.Append(c);
                        break;
                }
            }
            sb.Append('"');
        }

        public static void WriteValue(StringBuilder sb, object v)
        {
            if (v == null)            { sb.Append("null"); return; }
            if (v is string s)        { WriteString(sb, s); return; }
            if (v is bool b)          { sb.Append(b ? "true" : "false"); return; }
            if (v is int i)           { sb.Append(i.ToString(CultureInfo.InvariantCulture)); return; }
            if (v is long l)          { sb.Append(l.ToString(CultureInfo.InvariantCulture)); return; }
            if (v is float f)         { sb.Append(f.ToString("R", CultureInfo.InvariantCulture)); return; }
            if (v is double d)        { sb.Append(d.ToString("R", CultureInfo.InvariantCulture)); return; }
            if (v is decimal dec)     { sb.Append(dec.ToString(CultureInfo.InvariantCulture)); return; }

            if (v is IDictionary<string, object> map)
            {
                sb.Append('{');
                bool first = true;
                foreach (var kv in map)
                {
                    if (!first) sb.Append(',');
                    first = false;
                    WriteString(sb, kv.Key);
                    sb.Append(':');
                    WriteValue(sb, kv.Value);
                }
                sb.Append('}');
                return;
            }

            if (v is IEnumerable seq && !(v is string))
            {
                sb.Append('[');
                bool first = true;
                foreach (var item in seq)
                {
                    if (!first) sb.Append(',');
                    first = false;
                    WriteValue(sb, item);
                }
                sb.Append(']');
                return;
            }

            // Fallback: treat as string
            WriteString(sb, v.ToString());
        }
    }
}

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
            sb.Append(FormatDouble(value));
        }

        /// <summary>
        /// Format a double as a valid JSON number. NaN / ±Infinity have no JSON
        /// representation — emitting the bare tokens <c>NaN</c>/<c>Infinity</c> (which
        /// <c>ToString("R")</c> produces) makes the entire event body unparseable and
        /// can poison a whole batch and the on-disk queue. A bad value (e.g. a NaN
        /// revenue from a divide-by-zero price) degrades to <c>null</c> instead.
        /// </summary>
        internal static string FormatDouble(double value)
        {
            if (double.IsNaN(value) || double.IsInfinity(value)) return "null";
            return ForceDecimal(value.ToString("R", CultureInfo.InvariantCulture));
        }

        internal static string FormatFloat(float value)
        {
            if (float.IsNaN(value) || float.IsInfinity(value)) return "null";
            return ForceDecimal(value.ToString("R", CultureInfo.InvariantCulture));
        }

        /// <summary>
        /// Force a decimal point onto a whole-number double/float (e.g. <c>"2"</c> →
        /// <c>"2.0"</c>). The native cores read price/revenue/amount via a typed
        /// double cast (<c>as? Double</c>); a JSON integer token deserializes to Int
        /// and the cast drops it — silent revenue/price loss across the JNI/P-Invoke
        /// JSON boundary. Integers (long/int) are NOT routed through here, so they
        /// stay integers.
        /// </summary>
        private static string ForceDecimal(string s)
        {
            if (s.IndexOf('.') < 0 && s.IndexOf('e') < 0 && s.IndexOf('E') < 0) return s + ".0";
            return s;
        }

        /// <summary>Serialize any supported value (map/list/scalar) to a JSON string —
        /// the boundary encoder for native-core args.</summary>
        public static string Serialize(object v)
        {
            var sb = new StringBuilder(256);
            WriteValue(sb, v);
            return sb.ToString();
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
                        if (c < 0x20)
                            sb.AppendFormat(CultureInfo.InvariantCulture, "\\u{0:x4}", (int)c);
                        else if (char.IsHighSurrogate(c) && (i + 1 >= s.Length || !char.IsLowSurrogate(s[i + 1])))
                            // Lone high surrogate — escape it so the body stays valid JSON
                            // and round-trips, instead of being replaced with U+FFFD by the
                            // UTF-8 encoder (which silently corrupts the value on the wire).
                            sb.AppendFormat(CultureInfo.InvariantCulture, "\\u{0:x4}", (int)c);
                        else if (char.IsLowSurrogate(c) && (i == 0 || !char.IsHighSurrogate(s[i - 1])))
                            // Lone low surrogate (not preceded by a high surrogate) — escape it.
                            sb.AppendFormat(CultureInfo.InvariantCulture, "\\u{0:x4}", (int)c);
                        else
                            sb.Append(c);   // normal char, or a valid surrogate-pair member
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
            if (v is float f)         { sb.Append(FormatFloat(f)); return; }
            if (v is double d)        { sb.Append(FormatDouble(d)); return; }
            if (v is decimal dec)     { sb.Append(dec.ToString(CultureInfo.InvariantCulture)); return; }
            if (v is uint || v is ushort || v is short || v is byte || v is sbyte)
                                      { sb.Append(System.Convert.ToInt64(v).ToString(CultureInfo.InvariantCulture)); return; }
            if (v is ulong ul)        { sb.Append(ul.ToString(CultureInfo.InvariantCulture)); return; }

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

            // Fallback: treat as string, invariant (never locale-sensitive ToString()).
            WriteString(sb, System.Convert.ToString(v, CultureInfo.InvariantCulture));
        }
    }
}

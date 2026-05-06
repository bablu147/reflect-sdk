using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace Reflect.Internal
{
    /// <summary>
    /// Minimal JSON parser — returns <see cref="IDictionary{String,Object}"/>,
    /// <see cref="IList{Object}"/>, <see cref="string"/>, <see cref="double"/>,
    /// <see cref="long"/>, <see cref="bool"/>, or null.
    ///
    /// Adapted from public-domain MiniJSON (Calvin Rien) with small tweaks for Reflect.
    /// Used only for parsing small payloads coming back from native code.
    /// </summary>
    internal static class MiniJson
    {
        public static object Deserialize(string json)
        {
            if (string.IsNullOrEmpty(json)) return null;
            using (var r = new StringReader(json))
                return new Parser(r).ParseValue();
        }

        private sealed class Parser
        {
            private const string WordBreak = "{}[],:\"";
            private readonly StringReader _r;

            public Parser(StringReader r) { _r = r; }

            public object ParseValue()
            {
                SkipWhite();
                int pc = _r.Peek();
                if (pc == -1) return null;
                char c = (char)pc;
                switch (c)
                {
                    case '{': return ParseObject();
                    case '[': return ParseArray();
                    case '"': return ParseString();
                    case '-':
                    case '0': case '1': case '2': case '3': case '4':
                    case '5': case '6': case '7': case '8': case '9':
                        return ParseNumber();
                    default:  return ParseWord();
                }
            }

            private IDictionary<string, object> ParseObject()
            {
                var map = new Dictionary<string, object>();
                _r.Read(); // {
                while (true)
                {
                    SkipWhite();
                    int pc = _r.Peek();
                    if (pc == -1) return null;
                    char c = (char)pc;
                    if (c == '}') { _r.Read(); return map; }
                    if (c == ',') { _r.Read(); continue; }
                    var key = ParseString();
                    if (key == null) return null;
                    SkipWhite();
                    if ((char)_r.Read() != ':') return null;
                    map[key] = ParseValue();
                }
            }

            private IList<object> ParseArray()
            {
                var list = new List<object>();
                _r.Read(); // [
                while (true)
                {
                    SkipWhite();
                    int pc = _r.Peek();
                    if (pc == -1) return null;
                    char c = (char)pc;
                    if (c == ']') { _r.Read(); return list; }
                    if (c == ',') { _r.Read(); continue; }
                    list.Add(ParseValue());
                }
            }

            private string ParseString()
            {
                SkipWhite();
                var sb = new StringBuilder();
                _r.Read(); // "
                while (true)
                {
                    int pc = _r.Read();
                    if (pc == -1) return null;
                    char c = (char)pc;
                    if (c == '"') return sb.ToString();
                    if (c == '\\')
                    {
                        int esc = _r.Read();
                        if (esc == -1) return null;
                        switch ((char)esc)
                        {
                            case '"':  sb.Append('"'); break;
                            case '\\': sb.Append('\\'); break;
                            case '/':  sb.Append('/'); break;
                            case 'b':  sb.Append('\b'); break;
                            case 'f':  sb.Append('\f'); break;
                            case 'n':  sb.Append('\n'); break;
                            case 'r':  sb.Append('\r'); break;
                            case 't':  sb.Append('\t'); break;
                            case 'u':
                                var hex = new char[4];
                                for (int i = 0; i < 4; i++)
                                {
                                    int hc = _r.Read();
                                    if (hc == -1) return null;
                                    hex[i] = (char)hc;
                                }
                                try { sb.Append((char)Convert.ToInt32(new string(hex), 16)); }
                                catch (FormatException) { sb.Append('\uFFFD'); }
                                break;
                        }
                    }
                    else sb.Append(c);
                }
            }

            private object ParseNumber()
            {
                var sb = new StringBuilder();
                while (true)
                {
                    int pc = _r.Peek();
                    if (pc == -1) break;
                    char c = (char)pc;
                    if (WordBreak.IndexOf(c) >= 0 || char.IsWhiteSpace(c)) break;
                    sb.Append(c);
                    _r.Read();
                }
                var s = sb.ToString();
                if (s.IndexOf('.') < 0 && s.IndexOf('e') < 0 && s.IndexOf('E') < 0)
                {
                    if (long.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var l)) return l;
                }
                if (double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var d)) return d;
                return null;
            }

            private object ParseWord()
            {
                var sb = new StringBuilder();
                while (true)
                {
                    int pc = _r.Peek();
                    if (pc == -1) break;
                    char c = (char)pc;
                    if (WordBreak.IndexOf(c) >= 0 || char.IsWhiteSpace(c)) break;
                    sb.Append(c);
                    _r.Read();
                }
                var s = sb.ToString();
                if (s == "true") return true;
                if (s == "false") return false;
                if (s == "null") return null;
                return s;
            }

            private void SkipWhite()
            {
                while (true)
                {
                    int pc = _r.Peek();
                    if (pc == -1 || !char.IsWhiteSpace((char)pc)) return;
                    _r.Read();
                }
            }
        }
    }
}

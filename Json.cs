using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace SigmaMod
{
    // Minimal JSON reader: objects -> Dictionary<string, object>, arrays -> List<object>,
    // numbers -> double, plus string, bool and null. Enough for glTF and its extras.
    internal static class Json
    {
        public static object Parse(string s)
        {
            int i = 0;
            var v = Value(s, ref i);
            Ws(s, ref i);
            if (i != s.Length) throw new FormatException("JSON: trailing data at " + i);
            return v;
        }

        public static string Write(object v)
        {
            var sb = new StringBuilder();
            Write(sb, v, -1);
            return sb.ToString();
        }

        // indented, short lists of plain values kept on one line
        public static string Pretty(object v)
        {
            var sb = new StringBuilder();
            Write(sb, v, 0);
            return sb.ToString();
        }

        private static void Break(StringBuilder sb, int indent)
        {
            if (indent < 0) return;
            sb.Append('\n').Append(' ', indent * 2);
        }

        private static bool Flat(System.Collections.IEnumerable l)
        {
            int n = 0;
            foreach (var e in l)
            {
                if (e is System.Collections.IDictionary || e is System.Collections.IEnumerable && !(e is string) || ++n > 8) return false;
            }
            return true;
        }

        private static void Write(StringBuilder sb, object v, int indent)
        {
            int inner = indent < 0 ? -1 : indent + 1;
            switch (v)
            {
                case null: sb.Append("null"); break;
                case string s:
                    sb.Append('"');
                    foreach (char c in s)
                    {
                        if (c == '"' || c == '\\') sb.Append('\\').Append(c);
                        else if (c < 0x20) sb.Append("\\u").Append(((int)c).ToString("x4"));
                        else sb.Append(c);
                    }
                    sb.Append('"');
                    break;
                case bool b: sb.Append(b ? "true" : "false"); break;
                case float f: sb.Append(float.IsNaN(f) || float.IsInfinity(f) ? "0" : f.ToString("R", CultureInfo.InvariantCulture)); break;
                case double d: sb.Append(Num(d)); break;
                case int n: sb.Append(n.ToString(CultureInfo.InvariantCulture)); break;
                case long n: sb.Append(n.ToString(CultureInfo.InvariantCulture)); break;
                case System.Collections.IDictionary d:
                {
                    sb.Append('{');
                    bool first = true;
                    foreach (System.Collections.DictionaryEntry e in d)
                    {
                        if (!first) sb.Append(',');
                        first = false;
                        Break(sb, inner);
                        Write(sb, e.Key.ToString(), -1);
                        sb.Append(indent < 0 ? ":" : ": ");
                        Write(sb, e.Value, inner);
                    }
                    if (!first) Break(sb, indent);
                    sb.Append('}');
                    break;
                }
                case System.Collections.IEnumerable l:
                {
                    sb.Append('[');
                    bool first = true, flat = indent < 0 || Flat(l);
                    foreach (var e in l)
                    {
                        if (!first) sb.Append(flat && indent >= 0 ? ", " : ",");
                        first = false;
                        if (!flat) Break(sb, inner);
                        Write(sb, e, flat ? -1 : inner);
                    }
                    if (!first && !flat) Break(sb, indent);
                    sb.Append(']');
                    break;
                }
                default: Write(sb, v.ToString(), -1); break;
            }
        }

        private static string Num(double d) =>
            double.IsNaN(d) || double.IsInfinity(d) ? "0" : d.ToString("R", CultureInfo.InvariantCulture);

        private static object Value(string s, ref int i)
        {
            Ws(s, ref i);
            if (i >= s.Length) throw new FormatException("JSON: unexpected end");
            char c = s[i];
            switch (c)
            {
                case '{':
                {
                    var d = new Dictionary<string, object>();
                    i++; Ws(s, ref i);
                    if (s[i] == '}') { i++; return d; }
                    while (true)
                    {
                        Ws(s, ref i);
                        string k = Str(s, ref i);
                        Ws(s, ref i); Expect(s, ref i, ':');
                        d[k] = Value(s, ref i);
                        Ws(s, ref i);
                        if (s[i] == ',') { i++; continue; }
                        Expect(s, ref i, '}');
                        return d;
                    }
                }
                case '[':
                {
                    var l = new List<object>();
                    i++; Ws(s, ref i);
                    if (s[i] == ']') { i++; return l; }
                    while (true)
                    {
                        l.Add(Value(s, ref i));
                        Ws(s, ref i);
                        if (s[i] == ',') { i++; continue; }
                        Expect(s, ref i, ']');
                        return l;
                    }
                }
                case '"': return Str(s, ref i);
                case 't': Word(s, ref i, "true"); return true;
                case 'f': Word(s, ref i, "false"); return false;
                case 'n': Word(s, ref i, "null"); return null;
                default:
                {
                    int st = i;
                    while (i < s.Length && "+-0123456789.eE".IndexOf(s[i]) >= 0) i++;
                    if (st == i) throw new FormatException("JSON: unexpected '" + c + "' at " + i);
                    return double.Parse(s.Substring(st, i - st), NumberStyles.Float, CultureInfo.InvariantCulture);
                }
            }
        }

        private static string Str(string s, ref int i)
        {
            Expect(s, ref i, '"');
            var sb = new StringBuilder();
            while (s[i] != '"')
            {
                char c = s[i++];
                if (c != '\\') { sb.Append(c); continue; }
                char e = s[i++];
                switch (e)
                {
                    case 'n': sb.Append('\n'); break;
                    case 't': sb.Append('\t'); break;
                    case 'r': sb.Append('\r'); break;
                    case 'b': sb.Append('\b'); break;
                    case 'f': sb.Append('\f'); break;
                    case 'u': sb.Append((char)int.Parse(s.Substring(i, 4), NumberStyles.HexNumber)); i += 4; break;
                    default: sb.Append(e); break;
                }
            }
            i++;
            return sb.ToString();
        }

        private static void Ws(string s, ref int i)
        {
            while (i < s.Length && char.IsWhiteSpace(s[i])) i++;
        }

        private static void Expect(string s, ref int i, char c)
        {
            if (i >= s.Length || s[i] != c) throw new FormatException("JSON: expected '" + c + "' at " + i);
            i++;
        }

        private static void Word(string s, ref int i, string w)
        {
            if (string.CompareOrdinal(s, i, w, 0, w.Length) != 0) throw new FormatException("JSON: bad literal at " + i);
            i += w.Length;
        }
    }
}

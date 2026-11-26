using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

/// <summary>
/// Minimal, dependency-free JSON reader/writer for the Claude bridge.
///
/// Parse() returns plain CLR values:
///   object  -> Dictionary&lt;string, object&gt;
///   array   -> List&lt;object&gt;
///   string  -> string
///   number  -> double
///   boolean -> bool
///   null    -> null
///
/// This replaces the earlier "find the first occurrence of the key" approach,
/// which broke whenever a string value happened to contain a key name — for
/// example a C# script passed to write_script that mentions "content".
/// </summary>
public static class ClaudeJson
{
    // ── Parsing ───────────────────────────────────────────────────────────────

    public static object Parse(string json)
    {
        if (string.IsNullOrEmpty(json)) return null;
        int i = 0;
        var value = ParseValue(json, ref i);
        return value;
    }

    /// <summary>Parse and return a dictionary, or an empty one on any failure.</summary>
    public static Dictionary<string, object> ParseObject(string json)
    {
        try { return Parse(json) as Dictionary<string, object> ?? new Dictionary<string, object>(); }
        catch { return new Dictionary<string, object>(); }
    }

    static object ParseValue(string s, ref int i)
    {
        SkipWhitespace(s, ref i);
        if (i >= s.Length) throw new FormatException("Unexpected end of JSON");

        switch (s[i])
        {
            case '{': return ParseDict(s, ref i);
            case '[': return ParseList(s, ref i);
            case '"': return ParseString(s, ref i);
            case 't': Literal(s, ref i, "true");  return true;
            case 'f': Literal(s, ref i, "false"); return false;
            case 'n': Literal(s, ref i, "null");  return null;
            default:  return ParseNumber(s, ref i);
        }
    }

    static Dictionary<string, object> ParseDict(string s, ref int i)
    {
        var dict = new Dictionary<string, object>();
        i++; // '{'
        SkipWhitespace(s, ref i);
        if (i < s.Length && s[i] == '}') { i++; return dict; }

        while (i < s.Length)
        {
            SkipWhitespace(s, ref i);
            if (i >= s.Length || s[i] != '"') throw new FormatException("Expected a key at " + i);
            var key = ParseString(s, ref i);

            SkipWhitespace(s, ref i);
            if (i >= s.Length || s[i] != ':') throw new FormatException("Expected ':' at " + i);
            i++;

            dict[key] = ParseValue(s, ref i);

            SkipWhitespace(s, ref i);
            if (i >= s.Length) break;
            if (s[i] == ',') { i++; continue; }
            if (s[i] == '}') { i++; return dict; }
            throw new FormatException("Expected ',' or '}' at " + i);
        }
        throw new FormatException("Unterminated object");
    }

    static List<object> ParseList(string s, ref int i)
    {
        var list = new List<object>();
        i++; // '['
        SkipWhitespace(s, ref i);
        if (i < s.Length && s[i] == ']') { i++; return list; }

        while (i < s.Length)
        {
            list.Add(ParseValue(s, ref i));
            SkipWhitespace(s, ref i);
            if (i >= s.Length) break;
            if (s[i] == ',') { i++; continue; }
            if (s[i] == ']') { i++; return list; }
            throw new FormatException("Expected ',' or ']' at " + i);
        }
        throw new FormatException("Unterminated array");
    }

    static string ParseString(string s, ref int i)
    {
        var sb = new StringBuilder();
        i++; // opening quote
        while (i < s.Length)
        {
            char c = s[i];
            if (c == '"') { i++; return sb.ToString(); }
            if (c == '\\')
            {
                i++;
                if (i >= s.Length) break;
                char e = s[i];
                if (e == 'u')
                {
                    if (i + 4 >= s.Length) throw new FormatException("Bad \\u escape");
                    var hex = s.Substring(i + 1, 4);
                    sb.Append((char)Convert.ToInt32(hex, 16));
                    i += 5;
                    continue;
                }
                switch (e)
                {
                    case 'n': sb.Append('\n'); break;
                    case 'r': sb.Append('\r'); break;
                    case 't': sb.Append('\t'); break;
                    case 'b': sb.Append('\b'); break;
                    case 'f': sb.Append('\f'); break;
                    case '/': sb.Append('/');  break;
                    case '"': sb.Append('"');  break;
                    case '\\': sb.Append('\\'); break;
                    default:  sb.Append(e);    break;
                }
                i++;
                continue;
            }
            sb.Append(c);
            i++;
        }
        throw new FormatException("Unterminated string");
    }

    static object ParseNumber(string s, ref int i)
    {
        int start = i;
        while (i < s.Length)
        {
            char c = s[i];
            if (c == '-' || c == '+' || c == '.' || c == 'e' || c == 'E' || (c >= '0' && c <= '9')) i++;
            else break;
        }
        var text = s.Substring(start, i - start);
        double d;
        if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out d))
            throw new FormatException("Bad number: " + text);
        return d;
    }

    static void Literal(string s, ref int i, string word)
    {
        if (i + word.Length > s.Length || string.CompareOrdinal(s, i, word, 0, word.Length) != 0)
            throw new FormatException("Expected '" + word + "' at " + i);
        i += word.Length;
    }

    static void SkipWhitespace(string s, ref int i)
    {
        while (i < s.Length && (s[i] == ' ' || s[i] == '\t' || s[i] == '\n' || s[i] == '\r')) i++;
    }

    // ── Typed accessors ───────────────────────────────────────────────────────

    public static object Get(object container, string key)
    {
        var dict = container as Dictionary<string, object>;
        if (dict == null) return null;
        object value;
        return dict.TryGetValue(key, out value) ? value : null;
    }

    public static bool Has(object container, string key)
    {
        var dict = container as Dictionary<string, object>;
        return dict != null && dict.ContainsKey(key);
    }

    /// <summary>String value of a key, or null. Numbers and bools are stringified.</summary>
    public static string Str(object container, string key, string fallback = null)
    {
        var v = Get(container, key);
        if (v == null) return fallback;
        var s = v as string;
        if (s != null) return s;
        if (v is bool) return ((bool)v) ? "true" : "false";
        if (v is double) return ((double)v).ToString(CultureInfo.InvariantCulture);
        return v.ToString();
    }

    public static float? Float(object container, string key)
    {
        var v = Get(container, key);
        if (v is double) return (float)(double)v;
        var s = v as string;
        float f;
        if (s != null && float.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out f)) return f;
        return null;
    }

    public static int? Int(object container, string key)
    {
        var f = Float(container, key);
        return f.HasValue ? (int)Math.Round(f.Value) : (int?)null;
    }

    public static bool Bool(object container, string key, bool fallback = false)
    {
        var v = Get(container, key);
        if (v is bool) return (bool)v;
        if (v is double) return Math.Abs((double)v) > double.Epsilon;
        var s = v as string;
        if (s != null) return s == "true" || s == "1";
        return fallback;
    }

    public static List<object> List(object container, string key)
    {
        return Get(container, key) as List<object>;
    }

    /// <summary>Numeric array, e.g. [x, y, z]. Returns null when absent or malformed.</summary>
    public static float[] Floats(object container, string key)
    {
        return ToFloats(Get(container, key));
    }

    public static float[] ToFloats(object value)
    {
        var list = value as List<object>;
        if (list == null) return null;
        var result = new float[list.Count];
        for (int i = 0; i < list.Count; i++)
        {
            if (list[i] is double) { result[i] = (float)(double)list[i]; continue; }
            float f;
            var s = list[i] as string;
            if (s != null && float.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out f)) result[i] = f;
            else return null;
        }
        return result;
    }

    // ── Writing ───────────────────────────────────────────────────────────────

    public static string Serialize(object value)
    {
        if (value == null) return "null";
        if (value is bool) return ((bool)value) ? "true" : "false";
        if (value is string) return Quote((string)value);

        if (value is float)  return Number((float)value);
        if (value is double) return Number((double)value);
        if (value is int)    return ((int)value).ToString(CultureInfo.InvariantCulture);
        if (value is long)   return ((long)value).ToString(CultureInfo.InvariantCulture);
        if (value is uint || value is short || value is ushort || value is byte || value is sbyte)
            return Convert.ToInt64(value).ToString(CultureInfo.InvariantCulture);
        if (value is decimal) return ((decimal)value).ToString(CultureInfo.InvariantCulture);

        var dict = value as IDictionary;
        if (dict != null)
        {
            var sb = new StringBuilder("{");
            bool first = true;
            foreach (DictionaryEntry entry in dict)
            {
                if (!first) sb.Append(',');
                sb.Append(Quote(entry.Key == null ? "null" : entry.Key.ToString()))
                  .Append(':')
                  .Append(Serialize(entry.Value));
                first = false;
            }
            return sb.Append('}').ToString();
        }

        var enumerable = value as IEnumerable;
        if (enumerable != null)
        {
            var sb = new StringBuilder("[");
            bool first = true;
            foreach (var item in enumerable)
            {
                if (!first) sb.Append(',');
                sb.Append(Serialize(item));
                first = false;
            }
            return sb.Append(']').ToString();
        }

        if (value is Enum) return Quote(value.ToString());
        return Quote(value.ToString());
    }

    /// <summary>JSON has no NaN or Infinity: those become null rather than invalid output.</summary>
    static string Number(double d)
    {
        if (double.IsNaN(d) || double.IsInfinity(d)) return "null";
        return d.ToString("R", CultureInfo.InvariantCulture);
    }

    public static string Quote(string s)
    {
        if (s == null) return "null";
        var sb = new StringBuilder(s.Length + 2);
        sb.Append('"');
        foreach (char c in s)
        {
            switch (c)
            {
                case '"':  sb.Append("\\\""); break;
                case '\\': sb.Append("\\\\"); break;
                case '\n': sb.Append("\\n");  break;
                case '\r': sb.Append("\\r");  break;
                case '\t': sb.Append("\\t");  break;
                case '\b': sb.Append("\\b");  break;
                case '\f': sb.Append("\\f");  break;
                default:
                    if (c < ' ') sb.Append("\\u").Append(((int)c).ToString("x4"));
                    else sb.Append(c);
                    break;
            }
        }
        return sb.Append('"').ToString();
    }
}

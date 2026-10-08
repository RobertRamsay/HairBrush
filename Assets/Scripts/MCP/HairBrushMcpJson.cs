using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using UnityEngine;

// A small, self-contained JSON reader/writer for the MCP command channel.
//
// JsonUtility cannot read a message whose shape is only known at runtime (every command carries
// different arguments), and Newtonsoft is not in this project. Adding a package for one loopback
// protocol was judged worse than these two hundred lines, which only ever see messages the local
// bridge has already validated against each tool's schema.
//
// Parsed shapes: objects -> Dictionary<string, object>, arrays -> List<object>, numbers -> double,
// plus string, bool and null. Writing additionally understands Vector3, Color, IDictionary,
// IEnumerable, enums and every numeric type.
public static class HairBrushMcpJson
{
    public static object Parse(string text)
    {
        if (text == null) throw new FormatException("No JSON");
        int i = 0;
        object value = ReadValue(text, ref i);
        SkipSpace(text, ref i);
        if (i != text.Length) throw new FormatException("Trailing characters after JSON value");
        return value;
    }

    public static string Write(object value)
    {
        StringBuilder sb = new StringBuilder(256);
        WriteValue(sb, value);
        return sb.ToString();
    }

    // ---------------------------------------------------------------------------------
    // Reading
    // ---------------------------------------------------------------------------------

    static void SkipSpace(string s, ref int i)
    {
        while (i < s.Length && char.IsWhiteSpace(s[i])) i++;
    }

    static object ReadValue(string s, ref int i)
    {
        SkipSpace(s, ref i);
        if (i >= s.Length) throw new FormatException("Unexpected end of JSON");
        char c = s[i];
        if (c == '{') return ReadObject(s, ref i);
        if (c == '[') return ReadArray(s, ref i);
        if (c == '"') return ReadString(s, ref i);
        if (c == 't' && string.CompareOrdinal(s, i, "true", 0, 4) == 0) { i += 4; return true; }
        if (c == 'f' && string.CompareOrdinal(s, i, "false", 0, 5) == 0) { i += 5; return false; }
        if (c == 'n' && string.CompareOrdinal(s, i, "null", 0, 4) == 0) { i += 4; return null; }
        return ReadNumber(s, ref i);
    }

    static Dictionary<string, object> ReadObject(string s, ref int i)
    {
        Dictionary<string, object> result = new Dictionary<string, object>();
        i++;
        SkipSpace(s, ref i);
        if (i < s.Length && s[i] == '}') { i++; return result; }
        while (true)
        {
            SkipSpace(s, ref i);
            if (i >= s.Length || s[i] != '"') throw new FormatException("Expected property name");
            string key = ReadString(s, ref i);
            SkipSpace(s, ref i);
            if (i >= s.Length || s[i] != ':') throw new FormatException("Expected ':'");
            i++;
            result[key] = ReadValue(s, ref i);
            SkipSpace(s, ref i);
            if (i >= s.Length) throw new FormatException("Unterminated object");
            if (s[i] == ',') { i++; continue; }
            if (s[i] == '}') { i++; return result; }
            throw new FormatException("Expected ',' or '}'");
        }
    }

    static List<object> ReadArray(string s, ref int i)
    {
        List<object> result = new List<object>();
        i++;
        SkipSpace(s, ref i);
        if (i < s.Length && s[i] == ']') { i++; return result; }
        while (true)
        {
            result.Add(ReadValue(s, ref i));
            SkipSpace(s, ref i);
            if (i >= s.Length) throw new FormatException("Unterminated array");
            if (s[i] == ',') { i++; continue; }
            if (s[i] == ']') { i++; return result; }
            throw new FormatException("Expected ',' or ']'");
        }
    }

    static string ReadString(string s, ref int i)
    {
        StringBuilder sb = new StringBuilder();
        i++;
        while (i < s.Length)
        {
            char c = s[i++];
            if (c == '"') return sb.ToString();
            if (c != '\\') { sb.Append(c); continue; }
            if (i >= s.Length) break;
            char e = s[i++];
            switch (e)
            {
                case '"': sb.Append('"'); break;
                case '\\': sb.Append('\\'); break;
                case '/': sb.Append('/'); break;
                case 'b': sb.Append('\b'); break;
                case 'f': sb.Append('\f'); break;
                case 'n': sb.Append('\n'); break;
                case 'r': sb.Append('\r'); break;
                case 't': sb.Append('\t'); break;
                case 'u':
                    if (i + 4 > s.Length) throw new FormatException("Bad unicode escape");
                    sb.Append((char)Convert.ToInt32(s.Substring(i, 4), 16));
                    i += 4;
                    break;
                default: throw new FormatException("Bad escape");
            }
        }
        throw new FormatException("Unterminated string");
    }

    static double ReadNumber(string s, ref int i)
    {
        int start = i;
        while (i < s.Length && "+-0123456789.eE".IndexOf(s[i]) >= 0) i++;
        if (start == i) throw new FormatException("Unexpected character '" + s[i] + "'");
        return double.Parse(s.Substring(start, i - start), NumberStyles.Float, CultureInfo.InvariantCulture);
    }

    // ---------------------------------------------------------------------------------
    // Writing
    // ---------------------------------------------------------------------------------

    static void WriteValue(StringBuilder sb, object value)
    {
        switch (value)
        {
            case null: sb.Append("null"); return;
            case string str: WriteString(sb, str); return;
            case bool b: sb.Append(b ? "true" : "false"); return;
            case float f: WriteNumber(sb, f); return;
            case double d: WriteNumber(sb, d); return;
            case int n: sb.Append(n.ToString(CultureInfo.InvariantCulture)); return;
            case long l: sb.Append(l.ToString(CultureInfo.InvariantCulture)); return;
            case Enum en: WriteString(sb, en.ToString()); return;
            case Vector3 v:
                sb.Append('[');
                WriteNumber(sb, v.x); sb.Append(',');
                WriteNumber(sb, v.y); sb.Append(',');
                WriteNumber(sb, v.z);
                sb.Append(']');
                return;
            case IDictionary dict:
            {
                sb.Append('{');
                bool first = true;
                foreach (DictionaryEntry entry in dict)
                {
                    if (!first) sb.Append(',');
                    first = false;
                    WriteString(sb, Convert.ToString(entry.Key, CultureInfo.InvariantCulture));
                    sb.Append(':');
                    WriteValue(sb, entry.Value);
                }
                sb.Append('}');
                return;
            }
            case IEnumerable list:
            {
                sb.Append('[');
                bool first = true;
                foreach (object item in list)
                {
                    if (!first) sb.Append(',');
                    first = false;
                    WriteValue(sb, item);
                }
                sb.Append(']');
                return;
            }
        }
        if (value is IConvertible) { WriteNumber(sb, Convert.ToDouble(value, CultureInfo.InvariantCulture)); return; }
        WriteString(sb, value.ToString());
    }

    // NaN and infinity are not JSON. A card with a broken parameter should still report, so
    // they become null rather than an unparseable reply.
    static void WriteNumber(StringBuilder sb, double d)
    {
        if (double.IsNaN(d) || double.IsInfinity(d)) { sb.Append("null"); return; }
        sb.Append(Math.Round(d, 6).ToString("R", CultureInfo.InvariantCulture));
    }

    static void WriteString(StringBuilder sb, string s)
    {
        sb.Append('"');
        foreach (char c in s)
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
    }
}

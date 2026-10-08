using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace ParelVR.SDK.Core.Http
{
    /// <summary>
    /// Minimal recursive-descent JSON parser producing a plain object tree
    /// (Dictionary&lt;string, object&gt; / List&lt;object&gt; / string / double / bool / null).
    /// Used for dynamically-keyed JSON payloads that JsonUtility cannot deserialize.
    /// </summary>
    public static class MiniJson
    {
        public static object Parse(string json)
        {
            if (string.IsNullOrEmpty(json)) return null;
            int index = 0;
            object result = ParseValue(json, ref index);
            return result;
        }

        private static object ParseValue(string s, ref int i)
        {
            SkipWhitespace(s, ref i);
            if (i >= s.Length) return null;
            char c = s[i];
            switch (c)
            {
                case '{': return ParseObject(s, ref i);
                case '[': return ParseArray(s, ref i);
                case '"': return ParseString(s, ref i);
                case 't':
                    i += 4;
                    return true;
                case 'f':
                    i += 5;
                    return false;
                case 'n':
                    i += 4;
                    return null;
                default:
                    return ParseNumber(s, ref i);
            }
        }

        private static Dictionary<string, object> ParseObject(string s, ref int i)
        {
            var dict = new Dictionary<string, object>();
            i++;
            SkipWhitespace(s, ref i);
            if (i < s.Length && s[i] == '}') { i++; return dict; }

            while (i < s.Length)
            {
                SkipWhitespace(s, ref i);
                string key = ParseString(s, ref i);
                SkipWhitespace(s, ref i);
                i++;
                object value = ParseValue(s, ref i);
                dict[key] = value;
                SkipWhitespace(s, ref i);
                if (i < s.Length && s[i] == ',') { i++; continue; }
                if (i < s.Length && s[i] == '}') { i++; break; }
                break;
            }
            return dict;
        }

        private static List<object> ParseArray(string s, ref int i)
        {
            var list = new List<object>();
            i++;
            SkipWhitespace(s, ref i);
            if (i < s.Length && s[i] == ']') { i++; return list; }

            while (i < s.Length)
            {
                object value = ParseValue(s, ref i);
                list.Add(value);
                SkipWhitespace(s, ref i);
                if (i < s.Length && s[i] == ',') { i++; continue; }
                if (i < s.Length && s[i] == ']') { i++; break; }
                break;
            }
            return list;
        }

        private static string ParseString(string s, ref int i)
        {
            var sb = new StringBuilder();
            i++;
            while (i < s.Length && s[i] != '"')
            {
                char ch = s[i];
                if (ch == '\\' && i + 1 < s.Length)
                {
                    i++;
                    char esc = s[i];
                    switch (esc)
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
                            string hex = s.Substring(i + 1, 4);
                            sb.Append((char)ushort.Parse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture));
                            i += 4;
                            break;
                        default: sb.Append(esc); break;
                    }
                    i++;
                }
                else
                {
                    sb.Append(ch);
                    i++;
                }
            }
            i++;
            return sb.ToString();
        }

        private static double ParseNumber(string s, ref int i)
        {
            int start = i;
            while (i < s.Length && (char.IsDigit(s[i]) || s[i] == '-' || s[i] == '+' || s[i] == '.' || s[i] == 'e' || s[i] == 'E'))
                i++;
            return double.Parse(s.Substring(start, i - start), CultureInfo.InvariantCulture);
        }

        private static void SkipWhitespace(string s, ref int i)
        {
            while (i < s.Length && char.IsWhiteSpace(s[i])) i++;
        }

        // Convenience accessors
        public static Dictionary<string, object> AsDict(object o) => o as Dictionary<string, object>;
        public static List<object> AsList(object o) => o as List<object>;
        public static string AsString(object o) => o?.ToString();
        public static bool AsBool(object o) => o is bool b && b;
        public static double AsDouble(object o) => o is double d ? d : 0;
        public static int AsInt(object o) => (int)AsDouble(o);
        public static long AsLong(object o) => (long)AsDouble(o);

        public static Dictionary<string, object> Get(this Dictionary<string, object> dict, string key) =>
            dict != null && dict.TryGetValue(key, out var v) ? v as Dictionary<string, object> : null;

        public static List<object> GetList(this Dictionary<string, object> dict, string key) =>
            dict != null && dict.TryGetValue(key, out var v) ? v as List<object> : null;

        public static string GetString(this Dictionary<string, object> dict, string key, string fallback = "") =>
            dict != null && dict.TryGetValue(key, out var v) && v != null ? v.ToString() : fallback;

        public static bool GetBool(this Dictionary<string, object> dict, string key, bool fallback = false) =>
            dict != null && dict.TryGetValue(key, out var v) && v is bool b ? b : fallback;

        public static double GetDouble(this Dictionary<string, object> dict, string key, double fallback = 0) =>
            dict != null && dict.TryGetValue(key, out var v) && v is double d ? d : fallback;

        public static int GetInt(this Dictionary<string, object> dict, string key, int fallback = 0) =>
            (int)GetDouble(dict, key, fallback);
    }
}

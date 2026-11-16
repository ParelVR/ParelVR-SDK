using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace ParelVR.SDK.Port
{
    /// <summary>One object of a Unity scene / prefab / asset file.</summary>
    internal sealed class PortYamlDoc
    {
        public int ClassId;
        public long FileId;
        public bool Stripped;
        public string TypeName;
        public int Start, End;            // body lines [Start, End)
        public long GameObject;           // MonoBehaviour: the object it is on
        public string ScriptGuid;         // MonoBehaviour: its script
        public long ScriptFileId;
        public PortValue Body;            // parsed on first use
    }

    /// <summary>
    /// Reads the text form of a Unity scene, prefab or asset: enough to get at the settings of a component whose
    /// script is missing (the file keeps them; Unity cannot show them without the script).
    /// </summary>
    internal sealed class PortYamlFile
    {
        private static readonly Regex Header = new Regex(@"^--- !u!(\d+) &(-?\d+)( stripped)?", RegexOptions.Compiled);
        private static readonly Regex RefPattern = new Regex(@"fileID:\s*(-?\d+)(?:,\s*guid:\s*([0-9a-fA-F]{32}))?", RegexOptions.Compiled);

        public readonly string Path;
        private readonly string[] _lines;
        private readonly Dictionary<long, PortYamlDoc> _docs = new Dictionary<long, PortYamlDoc>();
        private readonly Dictionary<long, List<PortYamlDoc>> _behavioursOn = new Dictionary<long, List<PortYamlDoc>>();

        public bool IsText { get; }

        private PortYamlFile(string path, string[] lines)
        {
            Path = path;
            _lines = lines;
            IsText = lines.Length > 0 && lines[0].StartsWith("%YAML", StringComparison.Ordinal);
            if (!IsText) return;
            PortYamlDoc doc = null;
            for (int i = 0; i < lines.Length; i++)
            {
                if (!lines[i].StartsWith("--- ", StringComparison.Ordinal)) continue;
                Match m = Header.Match(lines[i]);
                if (!m.Success) continue;
                if (doc != null) doc.End = i;
                doc = new PortYamlDoc
                {
                    ClassId = int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture),
                    FileId = long.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture),
                    Stripped = m.Groups[3].Success,
                    Start = i + 2
                };
                if (i + 1 < lines.Length) doc.TypeName = lines[i + 1].TrimEnd(':', ' ');
                _docs[doc.FileId] = doc;
            }
            if (doc != null) doc.End = lines.Length;

            // Where each MonoBehaviour is and which script it wants, from its first lines (no full parse).
            foreach (PortYamlDoc d in _docs.Values)
            {
                if (d.ClassId != 114) continue;
                for (int i = d.Start; i < Math.Min(d.End, d.Start + 12); i++)
                {
                    string line = lines[i];
                    if (line.StartsWith("  m_GameObject:", StringComparison.Ordinal))
                    {
                        Match r = RefPattern.Match(line);
                        if (r.Success) d.GameObject = long.Parse(r.Groups[1].Value, CultureInfo.InvariantCulture);
                    }
                    else if (line.StartsWith("  m_Script:", StringComparison.Ordinal))
                    {
                        Match r = RefPattern.Match(line);
                        if (r.Success)
                        {
                            d.ScriptFileId = long.Parse(r.Groups[1].Value, CultureInfo.InvariantCulture);
                            d.ScriptGuid = r.Groups[2].Success ? r.Groups[2].Value.ToLowerInvariant() : null;
                        }
                    }
                }
                if (d.GameObject == 0) continue;
                if (!_behavioursOn.TryGetValue(d.GameObject, out List<PortYamlDoc> list)) _behavioursOn[d.GameObject] = list = new List<PortYamlDoc>();
                list.Add(d);
            }
        }

        public static PortYamlFile Load(string path)
        {
            try
            {
                if (string.IsNullOrEmpty(path) || !File.Exists(path)) return null;
                var file = new PortYamlFile(path, File.ReadAllLines(path));
                return file.IsText ? file : null;
            }
            catch (Exception) { return null; }
        }

        public PortYamlDoc Doc(long fileId) => _docs.TryGetValue(fileId, out PortYamlDoc d) ? d : null;

        public IEnumerable<PortYamlDoc> Docs => _docs.Values;

        public IReadOnlyList<PortYamlDoc> BehavioursOn(long gameObjectFileId) =>
            _behavioursOn.TryGetValue(gameObjectFileId, out List<PortYamlDoc> list) ? list : (IReadOnlyList<PortYamlDoc>)Array.Empty<PortYamlDoc>();

        public PortValue Body(PortYamlDoc doc)
        {
            if (doc == null) return PortValue.Null;
            if (doc.Body != null) return doc.Body;
            var lines = new List<Line>();
            for (int i = doc.Start; i < doc.End; i++)
            {
                string raw = _lines[i];
                if (raw.Length == 0) continue;
                int indent = 0;
                while (indent < raw.Length && raw[indent] == ' ') indent++;
                lines.Add(new Line { Indent = indent, Text = raw.Substring(indent) });
            }
            int at = 0;
            try { doc.Body = lines.Count > 0 ? Block(lines, ref at, lines[0].Indent) : PortValue.NewMap(); }
            catch (Exception) { doc.Body = PortValue.NewMap(); }
            if (doc.Body.K != PortValue.Kind.Map) doc.Body = PortValue.NewMap();
            return doc.Body;
        }

        // ── the subset of YAML Unity writes ──────────────────────────────────

        private struct Line
        {
            public int Indent;
            public string Text;
        }

        private static bool IsItem(Line l) => l.Text.StartsWith("- ", StringComparison.Ordinal) || l.Text == "-";

        private static PortValue Block(List<Line> lines, ref int i, int indent)
        {
            if (i >= lines.Count) return PortValue.Null;
            return IsItem(lines[i]) ? ListBlock(lines, ref i, indent) : MapBlock(lines, ref i, indent);
        }

        private static PortValue ListBlock(List<Line> lines, ref int i, int indent)
        {
            PortValue list = PortValue.NewList();
            while (i < lines.Count && lines[i].Indent == indent && IsItem(lines[i]))
            {
                string content = lines[i].Text.Length > 1 ? lines[i].Text.Substring(2) : string.Empty;
                if (SplitKey(content, out _, out _))
                {
                    // "- key: value" starts a map whose other keys sit two further in.
                    lines[i] = new Line { Indent = indent + 2, Text = content };
                    list.List.Add(MapBlock(lines, ref i, indent + 2));
                }
                else
                {
                    i++;
                    list.List.Add(Inline(Continue(lines, ref i, content, indent)));
                }
            }
            return list;
        }

        private static PortValue MapBlock(List<Line> lines, ref int i, int indent)
        {
            PortValue map = PortValue.NewMap();
            while (i < lines.Count && lines[i].Indent == indent && !IsItem(lines[i]))
            {
                if (!SplitKey(lines[i].Text, out string key, out string rest)) { i++; continue; }
                i++;
                if (rest.Length > 0)
                {
                    map.Set(key, Inline(Continue(lines, ref i, rest, indent)));
                    continue;
                }
                if (i < lines.Count && (lines[i].Indent > indent || (lines[i].Indent == indent && IsItem(lines[i]))))
                    map.Set(key, Block(lines, ref i, lines[i].Indent));
                else
                    map.Set(key, PortValue.Null);
            }
            return map;
        }

        /// <summary>A long quoted string or {…} / […] runs on over the next, deeper lines.</summary>
        private static string Continue(List<Line> lines, ref int i, string text, int indent)
        {
            char q = text.Length > 0 ? text[0] : '\0';
            bool quoted = q == '\'' || q == '"';
            bool flow = q == '{' || q == '[';
            if (!quoted && !flow) return text;
            var sb = new StringBuilder(text);
            while (quoted ? !Closed(sb.ToString(), q) : !Balanced(sb.ToString()))
            {
                if (i >= lines.Count || lines[i].Indent <= indent) break;
                sb.Append(' ').Append(lines[i].Text);
                i++;
            }
            return sb.ToString();
        }

        private static bool Closed(string text, char q)
        {
            if (text.Length < 2) return false;
            int i = 1;
            while (i < text.Length)
            {
                if (q == '\'' && text[i] == '\'' && i + 1 < text.Length && text[i + 1] == '\'') { i += 2; continue; }
                if (q == '"' && text[i] == '\\') { i += 2; continue; }
                if (text[i] == q) return true;
                i++;
            }
            return false;
        }

        private static bool Balanced(string text)
        {
            int depth = 0;
            bool inQuote = false;
            foreach (char c in text)
            {
                if (c == '\'' || c == '"') inQuote = !inQuote;
                if (inQuote) continue;
                if (c == '{' || c == '[') depth++;
                else if (c == '}' || c == ']') depth--;
            }
            return depth <= 0;
        }

        private static bool SplitKey(string text, out string key, out string rest)
        {
            key = rest = null;
            if (text.Length == 0) return false;
            char c0 = text[0];
            if (c0 == '{' || c0 == '[' || c0 == '\'' || c0 == '"' || c0 == '-') return false;
            int colon = text.IndexOf(':');
            while (colon >= 0)
            {
                if (colon == text.Length - 1 || text[colon + 1] == ' ')
                {
                    key = text.Substring(0, colon).Trim();
                    rest = colon == text.Length - 1 ? string.Empty : text.Substring(colon + 2).Trim();
                    return key.Length > 0 && key.IndexOf(' ') < 0;
                }
                colon = text.IndexOf(':', colon + 1);
            }
            return false;
        }

        private static PortValue Inline(string text)
        {
            text = text.Trim();
            if (text.Length == 0 || text == "~" || text == "null") return PortValue.Null;
            int at = 0;
            return Flow(text, ref at);
        }

        private static PortValue Flow(string s, ref int at)
        {
            Skip(s, ref at);
            if (at >= s.Length) return PortValue.Null;
            char c = s[at];
            if (c == '{')
            {
                at++;
                PortValue map = PortValue.NewMap();
                while (true)
                {
                    Skip(s, ref at);
                    if (at >= s.Length) break;
                    if (s[at] == '}') { at++; break; }
                    int colon = s.IndexOf(':', at);
                    if (colon < 0) break;
                    string key = s.Substring(at, colon - at).Trim();
                    at = colon + 1;
                    map.Set(key, Flow(s, ref at));
                    Skip(s, ref at);
                    if (at < s.Length && s[at] == ',') at++;
                }
                return AsReference(map);
            }
            if (c == '[')
            {
                at++;
                PortValue list = PortValue.NewList();
                while (true)
                {
                    Skip(s, ref at);
                    if (at >= s.Length) break;
                    if (s[at] == ']') { at++; break; }
                    list.List.Add(Flow(s, ref at));
                    Skip(s, ref at);
                    if (at < s.Length && s[at] == ',') at++;
                }
                return list;
            }
            if (c == '\'' || c == '"') return PortValue.Str(Quoted(s, ref at, c));
            int start = at;
            int depth = 0;
            while (at < s.Length)
            {
                char d = s[at];
                if (depth == 0 && (d == ',' || d == '}' || d == ']')) break;
                at++;
            }
            string scalar = s.Substring(start, at - start).Trim();
            return scalar.Length == 0 ? PortValue.Null : PortValue.Str(scalar);
        }

        /// <summary>{fileID: n[, guid: g, type: t]} is a reference.</summary>
        private static PortValue AsReference(PortValue map)
        {
            PortValue file = map.Get("fileID");
            if (file.K != PortValue.Kind.String || map.Map.Count > 3) return map;
            if (!long.TryParse(file.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out long id)) return map;
            PortValue guid = map.Get("guid");
            return new PortValue
            {
                K = PortValue.Kind.Object,
                FileId = id,
                Guid = guid.K == PortValue.Kind.String && id != 0 ? guid.Text.ToLowerInvariant() : null
            };
        }

        private static string Quoted(string s, ref int at, char q)
        {
            var sb = new StringBuilder();
            at++;
            while (at < s.Length)
            {
                char c = s[at];
                if (q == '\'' && c == '\'')
                {
                    if (at + 1 < s.Length && s[at + 1] == '\'') { sb.Append('\''); at += 2; continue; }
                    at++;
                    break;
                }
                if (q == '"' && c == '\\' && at + 1 < s.Length)
                {
                    char e = s[at + 1];
                    at += 2;
                    switch (e)
                    {
                        case 'n': sb.Append('\n'); break;
                        case 't': sb.Append('\t'); break;
                        case 'u':
                            if (at + 4 <= s.Length && int.TryParse(s.Substring(at, 4), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int code))
                            {
                                sb.Append((char)code);
                                at += 4;
                            }
                            break;
                        default: sb.Append(e); break;
                    }
                    continue;
                }
                if (q == '"' && c == '"') { at++; break; }
                sb.Append(c);
                at++;
            }
            return sb.ToString();
        }

        private static void Skip(string s, ref int at)
        {
            while (at < s.Length && s[at] == ' ') at++;
        }
    }
}

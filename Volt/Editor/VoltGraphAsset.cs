using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using UnityEngine;

namespace ParelVR.Volt.EditorTools
{
    /// <summary>
    /// A visual world script: event nodes start chains of action nodes. "Generate" in the Volt Graph window turns it
    /// into an ordinary VoltCS script next to the asset, which compiles, uploads and debugs like a hand-written one.
    /// </summary>
    [CreateAssetMenu(menuName = "ParelVR/Volt Graph", fileName = "NewVoltGraph", order = 81)]
    public sealed class VoltGraphAsset : ScriptableObject
    {
        public string className = "NewVoltGraph";
        public List<VoltGraphNode> nodes = new List<VoltGraphNode>();
        public List<VoltGraphLink> links = new List<VoltGraphLink>();
    }

    [Serializable]
    public sealed class VoltGraphNode
    {
        public string id;
        public string kind;
        public Vector2 position;
        public List<string> values = new List<string>();
    }

    /// <summary>The flow goes from output <see cref="port"/> of one node into another.</summary>
    [Serializable]
    public sealed class VoltGraphLink
    {
        public string from;
        public int port;
        public string to;
    }

    internal enum VoltGraphParam { Text, Number, Choice, GameObject, AudioSource, Animator, Transform }

    internal sealed class VoltGraphParamDef
    {
        public string Name;
        public VoltGraphParam Kind;
        public string[] Choices;
        public string Default = string.Empty;

        public bool IsObject => Kind >= VoltGraphParam.GameObject;
        public string FieldType => Kind == VoltGraphParam.GameObject ? "GameObject" : Kind.ToString();
    }

    internal sealed class VoltGraphNodeDef
    {
        public string Kind, Title, Category;
        public bool IsEvent;
        public string[] Outputs = { "" };
        public VoltGraphParamDef[] Params = Array.Empty<VoltGraphParamDef>();
        /// <summary>Events: the method declaration; {0} is the first value.</summary>
        public string Method;
        /// <summary>Actions: the statement; {0}, {1}… are the values, object values as the field names.</summary>
        public string Statement;
    }

    /// <summary>The nodes the graph knows and how a graph becomes C#.</summary>
    internal static class VoltGraphLibrary
    {
        private static VoltGraphParamDef P(string name, VoltGraphParam kind, string value = "", params string[] choices) =>
            new VoltGraphParamDef { Name = name, Kind = kind, Default = value, Choices = choices };

        private static VoltGraphNodeDef Event(string kind, string title, string method, params VoltGraphParamDef[] ps) =>
            new VoltGraphNodeDef { Kind = kind, Title = title, Category = "Events", IsEvent = true, Method = method, Params = ps };

        private static VoltGraphNodeDef Action(string kind, string title, string statement, params VoltGraphParamDef[] ps) =>
            new VoltGraphNodeDef { Kind = kind, Title = title, Category = "Actions", Statement = statement, Params = ps };

        public static readonly VoltGraphNodeDef[] All =
        {
            Event("start", "On Start", "public override void OnStart()"),
            Event("interact", "On Interact", "public override void OnInteract()"),
            Event("pickup", "On Pickup", "public override void OnPickup()"),
            Event("drop", "On Drop", "public override void OnDrop()"),
            Event("use", "On Pickup Use", "public override void OnPickupUseDown()"),
            Event("hoverEnter", "On Hover Enter", "public override void OnHoverEnter()"),
            Event("hoverExit", "On Hover Exit", "public override void OnHoverExit()"),
            Event("joined", "On Player Joined", "public override void OnPlayerJoined(VoltPlayer player)"),
            Event("left", "On Player Left", "public override void OnPlayerLeft(VoltPlayer player)"),
            Event("enter", "On Player Enter Trigger", "public override void OnPlayerEnterTrigger(VoltPlayer player)"),
            Event("exit", "On Player Exit Trigger", "public override void OnPlayerExitTrigger(VoltPlayer player)"),
            Event("custom", "Custom Event", "public void {0}()", P("Name", VoltGraphParam.Text, "MyEvent")),
            Event("network", "Network Event", "[VoltNetworkEvent] public void {0}()", P("Name", VoltGraphParam.Text, "MyNetworkEvent")),

            Action("active", "Set Active", "if ({0} != null) {0}.SetActive({1});", P("Object", VoltGraphParam.GameObject), P("To", VoltGraphParam.Choice, "On", "On", "Off", "Toggle")),
            Action("audio", "Play Audio", "if ({0} != null) {0}.Play();", P("Source", VoltGraphParam.AudioSource)),
            Action("anim", "Play Animation", "if ({0} != null) {0}.Play({1}, 0);", P("Animator", VoltGraphParam.Animator), P("State", VoltGraphParam.Text, "Idle")),
            Action("send", "Send Custom Event", "SendCustomEvent({0});", P("Name", VoltGraphParam.Text, "MyEvent")),
            Action("sendNet", "Send Network Event", "SendNetworkEvent(VoltNetworkTarget.{0}, {1});", P("To", VoltGraphParam.Choice, "All", "All", "Others", "Owner", "Master"), P("Name", VoltGraphParam.Text, "MyNetworkEvent")),
            Action("broadcast", "Broadcast Event", "VoltEvents.Broadcast({0});", P("Name", VoltGraphParam.Text, "MyEvent")),
            Action("teleport", "Teleport Local Player", "if ({0} != null) VoltNetworking.LocalPlayer.Teleport({0}.position, {0}.rotation);", P("To", VoltGraphParam.Transform)),
            Action("respawn", "Respawn Local Player", "VoltNetworking.LocalPlayer.Respawn();"),
            Action("notify", "Show Notification", "VoltDialogs.ShowNotification({0}, {1});", P("Title", VoltGraphParam.Text, "Hello"), P("Text", VoltGraphParam.Text)),
            Action("log", "Log", "VoltDebug.Log({0});", P("Message", VoltGraphParam.Text, "Hello")),
            new VoltGraphNodeDef { Kind = "delay", Title = "Wait Seconds", Category = "Flow", Params = new[] { P("Seconds", VoltGraphParam.Number, "1") } },
            new VoltGraphNodeDef { Kind = "ifMaster", Title = "If Master", Category = "Flow", Outputs = new[] { "Master", "Others" } },
        };

        public static VoltGraphNodeDef Find(string kind) => Array.Find(All, d => d.Kind == kind);

        public static string FieldName(VoltGraphNode node, VoltGraphParamDef param) =>
            "g" + (node.id ?? "0").Replace("-", "").Substring(0, Math.Min(8, (node.id ?? "0").Replace("-", "").Length)) + "_" + Identifier(param.Name);

        public static string Identifier(string text)
        {
            var sb = new StringBuilder();
            foreach (char c in text ?? string.Empty)
                if (char.IsLetterOrDigit(c) || c == '_') sb.Append(c);
            if (sb.Length == 0 || char.IsDigit(sb[0])) sb.Insert(0, '_');
            return sb.ToString();
        }

        private static string Literal(string text)
        {
            var sb = new StringBuilder("\"");
            foreach (char c in text ?? string.Empty)
            {
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': break;
                    default: sb.Append(c); break;
                }
            }
            return sb.Append('"').ToString();
        }

        private static string Value(VoltGraphNode node, VoltGraphParamDef[] ps, int i)
        {
            VoltGraphParamDef p = ps[i];
            string v = i < node.values.Count ? node.values[i] : p.Default;
            switch (p.Kind)
            {
                case VoltGraphParam.Number:
                    return float.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out float f) ? f.ToString("R", CultureInfo.InvariantCulture) + "f" : "0f";
                case VoltGraphParam.Choice:
                    if (Array.IndexOf(p.Choices, v) < 0) v = p.Default;
                    if (node.kind == "active") return v == "On" ? "true" : v == "Off" ? "false" : "!" + FieldName(node, ps[0]) + ".activeSelf";
                    return v;
                case VoltGraphParam.Text:
                    return node.kind == "custom" || node.kind == "network" ? Identifier(v) : Literal(v);
                default:
                    return FieldName(node, p);
            }
        }

        /// <summary>The C# of a graph. Problems that stop it from compiling are put in <paramref name="problems"/>.</summary>
        public static string Generate(VoltGraphAsset graph, List<string> problems)
        {
            var byId = new Dictionary<string, VoltGraphNode>(StringComparer.Ordinal);
            foreach (VoltGraphNode n in graph.nodes) if (n != null && !string.IsNullOrEmpty(n.id)) byId[n.id] = n;

            var fields = new StringBuilder();
            var methods = new StringBuilder();
            var extra = new StringBuilder();
            var declared = new HashSet<string>(StringComparer.Ordinal);

            foreach (VoltGraphNode node in graph.nodes)
            {
                VoltGraphNodeDef def = Find(node.kind);
                if (def == null) { problems.Add("Unknown node '" + node.kind + "'."); continue; }
                foreach (VoltGraphParamDef p in def.Params)
                    if (p.IsObject) fields.Append("    public ").Append(p.FieldType).Append(' ').Append(FieldName(node, p)).Append(";\n");
            }

            foreach (VoltGraphNode node in graph.nodes)
            {
                VoltGraphNodeDef def = Find(node.kind);
                if (def == null || !def.IsEvent) continue;
                string head = def.Params.Length > 0 ? string.Format(def.Method, Value(node, def.Params, 0)) : def.Method;
                if (!declared.Add(head)) { problems.Add("Two '" + def.Title + "' nodes make the same method; their chains run from the first one only."); continue; }
                methods.Append("\n    ").Append(head).Append("\n    {\n");
                Chain(graph, byId, node.id, 0, 2, methods, extra, new HashSet<string>(), problems);
                methods.Append("    }\n");
            }

            string name = Identifier(string.IsNullOrEmpty(graph.className) ? graph.name : graph.className);
            return "// Generated from the Volt graph '" + graph.name + "'. Edit the graph, not this file: Generate writes it again.\n" +
                   "using ParelVR.Volt;\nusing ParelVR.Volt.Networking;\nusing ParelVR.Volt.Players;\nusing ParelVR.Volt.UI;\nusing UnityEngine;\n\n" +
                   "public class " + name + " : VoltBehaviour\n{\n" + fields + methods + extra + "}\n";
        }

        private static void Chain(VoltGraphAsset graph, Dictionary<string, VoltGraphNode> byId, string from, int port, int depth,
            StringBuilder code, StringBuilder extra, HashSet<string> path, List<string> problems)
        {
            string pad = new string(' ', depth * 4);
            foreach (VoltGraphLink link in graph.links)
            {
                if (link.from != from || link.port != port || !byId.TryGetValue(link.to, out VoltGraphNode node)) continue;
                VoltGraphNodeDef def = Find(node.kind);
                if (def == null || def.IsEvent) continue;
                if (!path.Add(node.id) || depth > 32) { problems.Add("A chain loops back into '" + def.Title + "'; the loop was cut."); continue; }

                if (node.kind == "delay")
                {
                    // The rest of the chain becomes a method of its own that a delayed custom event calls.
                    string next = "__Wait_" + FieldName(node, def.Params[0]);
                    code.Append(pad).Append("SendCustomEventDelayedSeconds(\"").Append(next).Append("\", ").Append(Value(node, def.Params, 0)).Append(");\n");
                    if (extra.ToString().IndexOf("public void " + next + "()", StringComparison.Ordinal) < 0) // reached from two chains: made once
                    {
                        var body = new StringBuilder();
                        Chain(graph, byId, node.id, 0, 2, body, extra, new HashSet<string>(path), problems);
                        extra.Append("\n    public void ").Append(next).Append("()\n    {\n").Append(body).Append("    }\n");
                    }
                }
                else if (node.kind == "ifMaster")
                {
                    code.Append(pad).Append("if (VoltNetworking.IsMaster)\n").Append(pad).Append("{\n");
                    Chain(graph, byId, node.id, 0, depth + 1, code, extra, new HashSet<string>(path), problems);
                    code.Append(pad).Append("}\n").Append(pad).Append("else\n").Append(pad).Append("{\n");
                    Chain(graph, byId, node.id, 1, depth + 1, code, extra, new HashSet<string>(path), problems);
                    code.Append(pad).Append("}\n");
                }
                else
                {
                    var args = new object[def.Params.Length];
                    for (int i = 0; i < args.Length; i++) args[i] = Value(node, def.Params, i);
                    code.Append(pad).Append(string.Format(def.Statement, args)).Append('\n');
                    Chain(graph, byId, node.id, 0, depth, code, extra, path, problems);
                }
                path.Remove(node.id);
            }
        }
    }
}

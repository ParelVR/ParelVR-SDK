using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.Callbacks;
using UnityEditor.Experimental.GraphView;
using UnityEditor.UIElements;
using UnityEngine;
using UnityEngine.UIElements;

namespace ParelVR.Volt.EditorTools
{
    /// <summary>Edits a <see cref="VoltGraphAsset"/>: right-click to add nodes, drag from an output to an input to chain them.</summary>
    public sealed class VoltGraphWindow : EditorWindow
    {
        private VoltGraphAsset _asset;
        private VoltGraphView _view;
        private TextField _className;
        private Label _status;

        [MenuItem("ParelVR SDK/Volt/Graph Editor", priority = 30)]
        private static void OpenEmpty() => GetWindow<VoltGraphWindow>("Volt Graph");

        [OnOpenAsset]
        private static bool OpenAsset(int instanceId, int line)
        {
            if (!(EditorUtility.InstanceIDToObject(instanceId) is VoltGraphAsset graph)) return false;
            GetWindow<VoltGraphWindow>("Volt Graph").Load(graph);
            return true;
        }

        private void CreateGUI()
        {
            var bar = new Toolbar();
            var picker = new ObjectField { objectType = typeof(VoltGraphAsset), allowSceneObjects = false, value = _asset };
            picker.style.width = 220;
            picker.RegisterValueChangedCallback(e => Load(e.newValue as VoltGraphAsset));
            bar.Add(picker);
            _className = new TextField("Class") { value = _asset != null ? _asset.className : string.Empty };
            _className.style.width = 260;
            _className.RegisterValueChangedCallback(e => { if (_asset != null) { Undo.RecordObject(_asset, "Rename Volt graph class"); _asset.className = e.newValue; } });
            bar.Add(_className);
            bar.Add(new ToolbarButton(Save) { text = "Save" });
            bar.Add(new ToolbarButton(Generate) { text = "Generate Script" });
            _status = new Label();
            _status.style.unityTextAlign = TextAnchor.MiddleLeft;
            _status.style.marginLeft = 8;
            bar.Add(_status);
            rootVisualElement.Add(bar);

            _view = new VoltGraphView();
            _view.StretchToParentSize();
            _view.style.top = 22;
            rootVisualElement.Add(_view);
            if (_asset != null) _view.Show(_asset);
            else _status.text = "Pick a Volt Graph asset (Create > ParelVR > Volt Graph).";
        }

        private void OnDisable() => Save();

        private void Load(VoltGraphAsset asset)
        {
            Save();
            _asset = asset;
            if (_className != null) _className.SetValueWithoutNotify(asset != null ? asset.className : string.Empty);
            if (_view != null) _view.Show(asset);
            if (_status != null) _status.text = asset != null ? string.Empty : "Pick a Volt Graph asset.";
        }

        private void Save()
        {
            if (_asset == null || _view == null) return;
            _view.WriteTo(_asset);
            EditorUtility.SetDirty(_asset);
            AssetDatabase.SaveAssetIfDirty(_asset);
        }

        private void Generate()
        {
            if (_asset == null) return;
            Save();
            var problems = new List<string>();
            string code = VoltGraphLibrary.Generate(_asset, problems);
            string folder = Path.GetDirectoryName(AssetDatabase.GetAssetPath(_asset));
            string name = VoltGraphLibrary.Identifier(string.IsNullOrEmpty(_asset.className) ? _asset.name : _asset.className);
            string path = Path.Combine(folder ?? "Assets", name + ".cs").Replace('\\', '/');
            File.WriteAllText(path, code);
            AssetDatabase.ImportAsset(path);
            foreach (string problem in problems) Debug.LogWarning("[Volt Graph] " + problem);
            _status.text = "Wrote " + path + (problems.Count > 0 ? " (" + problems.Count + " warnings in the Console)" : "") + ". Add it to an object, then assign its fields.";
        }
    }

    internal sealed class VoltGraphView : GraphView
    {
        public VoltGraphView()
        {
            SetupZoom(ContentZoomer.DefaultMinScale, ContentZoomer.DefaultMaxScale);
            this.AddManipulator(new ContentDragger());
            this.AddManipulator(new SelectionDragger());
            this.AddManipulator(new RectangleSelector());
            var grid = new GridBackground();
            Insert(0, grid);
            grid.StretchToParentSize();
        }

        public void Show(VoltGraphAsset asset)
        {
            DeleteElements(graphElements.ToList());
            if (asset == null) return;
            var views = new Dictionary<string, VoltGraphNodeView>(StringComparer.Ordinal);
            foreach (VoltGraphNode node in asset.nodes)
            {
                VoltGraphNodeDef def = VoltGraphLibrary.Find(node.kind);
                if (def == null || string.IsNullOrEmpty(node.id)) continue;
                var view = new VoltGraphNodeView(def, node);
                views[node.id] = view;
                AddElement(view);
            }
            foreach (VoltGraphLink link in asset.links)
            {
                if (!views.TryGetValue(link.from, out VoltGraphNodeView a) || !views.TryGetValue(link.to, out VoltGraphNodeView b)) continue;
                if (link.port < 0 || link.port >= a.Outputs.Count || b.Input == null) continue;
                AddElement(a.Outputs[link.port].ConnectTo(b.Input));
            }
        }

        public void WriteTo(VoltGraphAsset asset)
        {
            Undo.RecordObject(asset, "Edit Volt graph");
            asset.nodes.Clear();
            asset.links.Clear();
            foreach (VoltGraphNodeView view in nodes.OfType<VoltGraphNodeView>())
            {
                view.Data.position = view.GetPosition().position;
                asset.nodes.Add(view.Data);
            }
            foreach (Edge edge in edges)
            {
                if (!(edge.output?.node is VoltGraphNodeView a) || !(edge.input?.node is VoltGraphNodeView b)) continue;
                asset.links.Add(new VoltGraphLink { from = a.Data.id, port = a.Outputs.IndexOf(edge.output), to = b.Data.id });
            }
        }

        public override void BuildContextualMenu(ContextualMenuPopulateEvent evt)
        {
            Vector2 at = contentViewContainer.WorldToLocal(evt.mousePosition);
            foreach (VoltGraphNodeDef def in VoltGraphLibrary.All)
            {
                VoltGraphNodeDef chosen = def;
                evt.menu.AppendAction("Add/" + def.Category + "/" + def.Title, _ =>
                {
                    var data = new VoltGraphNode { id = Guid.NewGuid().ToString("N"), kind = chosen.Kind, position = at };
                    foreach (VoltGraphParamDef p in chosen.Params) data.values.Add(p.Default);
                    AddElement(new VoltGraphNodeView(chosen, data));
                });
            }
            evt.menu.AppendSeparator();
            base.BuildContextualMenu(evt);
        }

        public override List<Port> GetCompatiblePorts(Port startPort, NodeAdapter nodeAdapter) =>
            ports.Where(p => p.direction != startPort.direction && p.node != startPort.node).ToList();
    }

    internal sealed class VoltGraphNodeView : Node
    {
        public readonly VoltGraphNode Data;
        public readonly Port Input;
        public readonly List<Port> Outputs = new List<Port>();

        public VoltGraphNodeView(VoltGraphNodeDef def, VoltGraphNode data)
        {
            Data = data;
            title = def.Title;
            SetPosition(new Rect(data.position, new Vector2(220, 120)));
            titleContainer.style.backgroundColor = def.IsEvent ? new Color(0.22f, 0.32f, 0.5f) : def.Category == "Flow" ? new Color(0.42f, 0.33f, 0.18f) : new Color(0.24f, 0.24f, 0.24f);

            if (!def.IsEvent)
            {
                Input = InstantiatePort(Orientation.Horizontal, Direction.Input, Port.Capacity.Multi, typeof(bool));
                Input.portName = string.Empty;
                inputContainer.Add(Input);
            }
            foreach (string output in def.Outputs)
            {
                Port port = InstantiatePort(Orientation.Horizontal, Direction.Output, Port.Capacity.Multi, typeof(bool));
                port.portName = output;
                outputContainer.Add(port);
                Outputs.Add(port);
            }

            while (data.values.Count < def.Params.Length) data.values.Add(def.Params[data.values.Count].Default);
            for (int i = 0; i < def.Params.Length; i++)
            {
                int index = i;
                VoltGraphParamDef p = def.Params[i];
                switch (p.Kind)
                {
                    case VoltGraphParam.Choice:
                    {
                        var choices = p.Choices.ToList();
                        string value = choices.Contains(data.values[i]) ? data.values[i] : p.Default;
                        var field = new PopupField<string>(p.Name, choices, value);
                        field.RegisterValueChangedCallback(e => Data.values[index] = e.newValue);
                        extensionContainer.Add(field);
                        break;
                    }
                    case VoltGraphParam.Text:
                    case VoltGraphParam.Number:
                    {
                        var field = new TextField(p.Name) { value = data.values[i] };
                        field.RegisterValueChangedCallback(e => Data.values[index] = e.newValue);
                        extensionContainer.Add(field);
                        break;
                    }
                    default:
                        extensionContainer.Add(new Label(p.Name + ": " + p.FieldType + " (set on the component)"));
                        break;
                }
            }
            RefreshExpandedState();
            RefreshPorts();
        }
    }
}

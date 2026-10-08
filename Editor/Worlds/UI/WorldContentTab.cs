using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading.Tasks;
using ParelVR.SDK.Backend;
using ParelVR.SDK.Core.ControlPanel;
using ParelVR.SDK.Core.Settings;
using ParelVR.SDK.Core.Util;
using ParelVR.SDK.Worlds.Components;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.UIElements;

namespace ParelVR.SDK.Worlds.UI
{
    /// <summary>
    /// The Content Manager for a world project: every world you have uploaded, with its picture, its id to
    /// copy, a way to point the open scene at it, and Delete.
    /// </summary>
    public class WorldContentTab : IParelTab
    {
        public string TabName => "Content Manager";
        public int TabOrder => 20;
        public ParelProjectType RequiredMode => ParelProjectType.World;

        private TextField _search;
        private Label _status;
        private VisualElement _list;
        private List<WorldRecord> _worlds;
        private int _request;

        public void BuildUI(VisualElement container)
        {
            var toolbar = new VisualElement();
            toolbar.AddToClassList("bk-card");
            var title = new Label("Worlds");
            title.AddToClassList("bk-card-title");
            toolbar.Add(title);

            var row = new VisualElement();
            row.AddToClassList("bk-prop");
            var label = new Label("Search");
            label.AddToClassList("bk-prop-label");
            row.Add(label);
            var value = new VisualElement();
            value.AddToClassList("bk-prop-value");
            _search = new TextField();
            _search.AddToClassList("bk-input");
            _search.RegisterValueChangedCallback(_ => Render());
            value.Add(_search);
            value.Add(ActionButton("Refresh", () => _ = RefreshAsync()));
            row.Add(value);
            toolbar.Add(row);

            _status = new Label();
            _status.AddToClassList("bk-hint");
            _status.style.marginTop = 0;
            toolbar.Add(_status);
            container.Add(toolbar);

            _list = new VisualElement();
            container.Add(_list);
        }

        public void OnShown() => _ = RefreshAsync();

        private async Task RefreshAsync()
        {
            int request = ++_request;
            _status.text = "Loading your worlds...";
            try
            {
                List<WorldRecord> worlds = await WorldApiClient.GetMyWorldsAsync();
                if (request != _request) return;
                _worlds = worlds ?? new List<WorldRecord>();
                Render();
            }
            catch (Exception ex)
            {
                if (request != _request) return;
                _status.text = "Couldn't load your worlds: " + ex.Message;
            }
        }

        private void Render()
        {
            if (_list == null) return;
            _list.Clear();
            if (_worlds == null) return;

            string filter = (_search.value ?? string.Empty).Trim();
            var shown = _worlds.FindAll(w => w != null && (filter.Length == 0
                || (w.name ?? string.Empty).IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0
                || (w.id ?? string.Empty).IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0));

            _status.text = _worlds.Count == 0
                ? "You haven't uploaded any worlds yet. Build & Publish one from the Builder."
                : "Showing " + shown.Count + " of " + _worlds.Count + " world(s).";

            ParelWorldDescriptor descriptor = UnityEngine.Object.FindFirstObjectByType<ParelWorldDescriptor>();
            foreach (WorldRecord world in shown) _list.Add(Row(world, descriptor));
        }

        private VisualElement Row(WorldRecord world, ParelWorldDescriptor descriptor)
        {
            var card = new VisualElement();
            card.AddToClassList("bk-card");
            card.AddToClassList("bk-content-row");

            var frame = new VisualElement();
            frame.AddToClassList("bk-thumb-frame");
            frame.style.width = 128;
            frame.style.height = 96;
            frame.style.flexShrink = 0;
            var image = new Image { scaleMode = ScaleMode.ScaleAndCrop };
            image.style.flexGrow = 1;
            frame.Add(image);
            if (!string.IsNullOrEmpty(world.thumbnailUrl))
            {
                Action<Texture2D> show = texture => { if (texture != null) image.image = texture; };
                show(ParelTextureCache.GetOrFetch(world.thumbnailUrl, show));
            }
            card.Add(frame);

            var info = new VisualElement();
            info.style.flexGrow = 1;
            info.style.flexShrink = 1;
            info.style.marginLeft = 10;
            var name = new Label(string.IsNullOrEmpty(world.name) ? "(unnamed)" : world.name);
            name.AddToClassList("bk-content-name");
            info.Add(name);
            var id = new Label(world.id);
            id.AddToClassList("bk-hint");
            id.style.marginTop = 0;
            id.selection.isSelectable = true;
            info.Add(id);

            var pills = new VisualElement();
            pills.AddToClassList("bk-row");
            pills.style.marginTop = 6;
            bool isPublic = string.Equals(world.releaseStatus, "public", StringComparison.OrdinalIgnoreCase);
            pills.Add(Pill(string.IsNullOrEmpty(world.releaseStatus) ? "Private" : CultureInfo.InvariantCulture.TextInfo.ToTitleCase(world.releaseStatus), isPublic ? "bk-pill-good" : "bk-pill-neutral"));
            bool inScene = descriptor != null && descriptor.blueprintId == world.id;
            if (inScene) pills.Add(Pill("In this scene", "bk-pill-accent"));
            info.Add(pills);

            string updated = Date(string.IsNullOrEmpty(world.updatedAt) ? world.createdAt : world.updatedAt);
            if (updated != null)
            {
                var when = new Label("Updated " + updated);
                when.AddToClassList("bk-hint");
                info.Add(when);
            }
            card.Add(info);

            var actions = new VisualElement();
            actions.style.width = 132;
            actions.style.flexShrink = 0;
            actions.style.marginLeft = 8;
            actions.Add(ActionButton("Copy ID", () => EditorGUIUtility.systemCopyBuffer = world.id));
            if (!inScene && descriptor != null) actions.Add(ActionButton("Use in This Scene", () => Attach(world, descriptor)));
            Button delete = ActionButton("Delete", () => _ = DeleteAsync(world));
            delete.AddToClassList("bk-btn-danger");
            actions.Add(delete);
            card.Add(actions);
            return card;
        }

        /// <summary>Points the open scene at an uploaded world, so the next publish updates it.</summary>
        private void Attach(WorldRecord world, ParelWorldDescriptor descriptor)
        {
            if (!string.IsNullOrEmpty(descriptor.blueprintId) &&
                !EditorUtility.DisplayDialog("Use in This Scene",
                    "This scene already publishes to another world (" + descriptor.blueprintId + "). Point it at \"" + world.name + "\" instead?", "Use This World", "Cancel")) return;
            Undo.RecordObject(descriptor, "Set World Blueprint ID");
            descriptor.blueprintId = world.id;
            EditorUtility.SetDirty(descriptor);
            EditorSceneManager.MarkSceneDirty(descriptor.gameObject.scene);
            Render();
        }

        private async Task DeleteAsync(WorldRecord world)
        {
            if (!EditorUtility.DisplayDialog("Delete World",
                    "Delete \"" + world.name + "\" from ParelVR? Players will no longer be able to visit it. This cannot be undone.", "Delete", "Cancel")) return;
            try
            {
                await WorldApiClient.DeleteWorldAsync(world.id);
                _worlds?.Remove(world);
                Render();
            }
            catch (Exception ex)
            {
                EditorUtility.DisplayDialog("Couldn't Delete World", ex.Message, "OK");
            }
        }

        private static Button ActionButton(string text, Action onClick)
        {
            var button = new Button(onClick) { text = text };
            button.AddToClassList("bk-btn");
            button.style.marginBottom = 4;
            return button;
        }

        private static Label Pill(string text, string modifier)
        {
            var pill = new Label(text);
            pill.AddToClassList("bk-pill");
            pill.AddToClassList(modifier);
            pill.style.marginRight = 4;
            return pill;
        }

        private static string Date(string iso)
        {
            return DateTime.TryParse(iso, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out DateTime when)
                ? when.ToLocalTime().ToString("d MMM yyyy", CultureInfo.CurrentCulture)
                : null;
        }
    }
}

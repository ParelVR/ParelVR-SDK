using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using ParelVR.SDK.Backend;
using ParelVR.SDK.Core.Http;
using UnityEditor;
using UnityEngine;

namespace ParelVR.SDK.Worlds.UI
{
    /// <summary>
    /// Who helps run an uploaded world and what each role may do from the world's Settings page on the
    /// ParelVR website (edit settings, ban players, ban groups from hosting, shut down instances, send
    /// announcements). The owner can always do everything. Owners, co-owners and moderators are protected:
    /// an instance master can't kick, mute or warn them.
    /// </summary>
    public sealed class ParelWorldRolesWindow : EditorWindow
    {
        [Serializable] private class Perms { public bool settings; public bool bans; public bool groupBans; public bool instances; public bool announce; }
        [Serializable] private class RolePerms { public Perms coOwner = new Perms { settings = true, bans = true, groupBans = true, instances = true, announce = true }; public Perms moderator = new Perms { bans = true, instances = true, announce = true }; }
        [Serializable] private class Moderator { public string playFabId; public string displayName; }
        [Serializable] private class HostGroup { public string groupId; public string displayName; }
        [Serializable] private class Roles { public List<string> coOwners = new List<string>(); public List<Moderator> moderators = new List<Moderator>(); public List<HostGroup> hostGroups = new List<HostGroup>(); }
        [Serializable] private class Manage { public bool success; public string role; public Roles roles; public RolePerms rolePermissions; }
        [Serializable] private class SaveBody { public List<string> coOwners; public List<Moderator> moderators; public List<HostGroup> hostGroups; public RolePerms rolePermissions; }

        private List<WorldRecord> _worlds = new List<WorldRecord>();
        private int _selected = -1;
        private Roles _roles;
        private RolePerms _perms;
        private string _status = "";
        private bool _busy;
        private string _newCoOwner = "", _newModerator = "";
        private Vector2 _scroll;

        [MenuItem("ParelVR SDK/World Roles and Permissions", priority = 20)]
        public static void Open()
        {
            var w = GetWindow<ParelWorldRolesWindow>("World Roles");
            w.minSize = new Vector2(440, 520);
            w.LoadWorlds();
        }

        private async void LoadWorlds()
        {
            _busy = true; _status = "Loading your worlds…"; Repaint();
            try { _worlds = await WorldApiClient.GetMyWorldsAsync() ?? new List<WorldRecord>(); _status = _worlds.Count == 0 ? "Upload a world first, then set its roles here." : ""; }
            catch (Exception e) { _status = "Couldn't load your worlds: " + e.Message; }
            _busy = false; Repaint();
        }

        private async Task LoadWorld(string id)
        {
            _busy = true; _status = "Loading roles…"; Repaint();
            try
            {
                string raw = await ParelApiClient.GetRawAsync($"/api/parelvr/worlds/{id}/manage");
                // JsonUtility can't read the "co-owner" key, so it is renamed before parsing.
                var m = JsonUtility.FromJson<Manage>((raw ?? "{}").Replace("\"co-owner\"", "\"coOwner\""));
                _roles = m?.roles ?? new Roles();
                _roles.coOwners ??= new List<string>(); _roles.moderators ??= new List<Moderator>(); _roles.hostGroups ??= new List<HostGroup>();
                _perms = m?.rolePermissions ?? new RolePerms();
                _perms.coOwner ??= new RolePerms().coOwner; _perms.moderator ??= new RolePerms().moderator;
                _status = "";
            }
            catch (Exception e) { _roles = null; _status = "Couldn't load this world's roles: " + e.Message; }
            _busy = false; Repaint();
        }

        private async void Save(string id)
        {
            _busy = true; _status = "Saving…"; Repaint();
            try
            {
                await ParelApiClient.PutJsonAsync<WorldResponse>($"/api/parelvr/worlds/{id}/roles", new SaveBody { coOwners = _roles.coOwners, moderators = _roles.moderators, hostGroups = _roles.hostGroups, rolePermissions = _perms });
                _status = "Saved. It applies on the website and in the game right away.";
                await LoadWorld(id);
            }
            catch (Exception e) { _status = "Couldn't save: " + e.Message; }
            _busy = false; Repaint();
        }

        private void OnGUI()
        {
            EditorGUILayout.Space(6);
            EditorGUILayout.LabelField("World Roles and Permissions", EditorStyles.boldLabel);
            EditorGUILayout.HelpBox("Choose who helps run your world and what each role can do on the world's Settings page on the ParelVR website. You (the uploader) can always do everything. Owners, co-owners and moderators can't be kicked, muted or warned by an instance master.", MessageType.Info);

            using (new EditorGUI.DisabledScope(_busy))
            {
                string[] names = _worlds.ConvertAll(w => string.IsNullOrEmpty(w.name) ? w.id : w.name).ToArray();
                int pick = EditorGUILayout.Popup("World", _selected, names);
                if (pick != _selected && pick >= 0) { _selected = pick; _ = LoadWorld(_worlds[pick].id); }
                if (GUILayout.Button("Refresh worlds", GUILayout.Width(120))) LoadWorlds();
            }

            if (!string.IsNullOrEmpty(_status)) EditorGUILayout.HelpBox(_status, MessageType.None);
            if (_roles == null || _selected < 0) return;

            _scroll = EditorGUILayout.BeginScrollView(_scroll);
            using (new EditorGUI.DisabledScope(_busy))
            {
                EditorGUILayout.Space(8);
                EditorGUILayout.LabelField("Co-owners", EditorStyles.boldLabel);
                for (int i = 0; i < _roles.coOwners.Count; i++)
                {
                    using (new EditorGUILayout.HorizontalScope())
                    {
                        EditorGUILayout.LabelField(_roles.coOwners[i]);
                        if (GUILayout.Button("Remove", GUILayout.Width(70))) { _roles.coOwners.RemoveAt(i); break; }
                    }
                }
                using (new EditorGUILayout.HorizontalScope())
                {
                    _newCoOwner = EditorGUILayout.TextField(_newCoOwner);
                    if (GUILayout.Button("Add", GUILayout.Width(70)) && !string.IsNullOrWhiteSpace(_newCoOwner)) { _roles.coOwners.Add(_newCoOwner.Trim()); _newCoOwner = ""; }
                }
                PermsGUI("Co-owners can", _perms.coOwner);

                EditorGUILayout.Space(10);
                EditorGUILayout.LabelField("Moderators", EditorStyles.boldLabel);
                for (int i = 0; i < _roles.moderators.Count; i++)
                {
                    using (new EditorGUILayout.HorizontalScope())
                    {
                        var m = _roles.moderators[i];
                        EditorGUILayout.LabelField(string.IsNullOrEmpty(m.displayName) ? m.playFabId : $"{m.displayName} ({m.playFabId})");
                        if (GUILayout.Button("Remove", GUILayout.Width(70))) { _roles.moderators.RemoveAt(i); break; }
                    }
                }
                using (new EditorGUILayout.HorizontalScope())
                {
                    _newModerator = EditorGUILayout.TextField(_newModerator);
                    if (GUILayout.Button("Add", GUILayout.Width(70)) && !string.IsNullOrWhiteSpace(_newModerator)) { _roles.moderators.Add(new Moderator { playFabId = _newModerator.Trim() }); _newModerator = ""; }
                }
                EditorGUILayout.LabelField("Add people by username or user ID.", EditorStyles.miniLabel);
                PermsGUI("Moderators can", _perms.moderator);

                EditorGUILayout.Space(12);
                if (GUILayout.Button("Save roles and permissions", GUILayout.Height(30))) Save(_worlds[_selected].id);
            }
            EditorGUILayout.EndScrollView();
        }

        private static void PermsGUI(string title, Perms p)
        {
            EditorGUILayout.LabelField(title, EditorStyles.miniBoldLabel);
            EditorGUI.indentLevel++;
            p.settings = EditorGUILayout.ToggleLeft("Edit world settings (description, tags, content warnings, NSFW avatars)", p.settings);
            p.bans = EditorGUILayout.ToggleLeft("Ban and unban players from the world", p.bans);
            p.groupBans = EditorGUILayout.ToggleLeft("Ban groups from hosting instances", p.groupBans);
            p.instances = EditorGUILayout.ToggleLeft("Shut down instances", p.instances);
            p.announce = EditorGUILayout.ToggleLeft("Send world announcements", p.announce);
            EditorGUI.indentLevel--;
        }
    }
}

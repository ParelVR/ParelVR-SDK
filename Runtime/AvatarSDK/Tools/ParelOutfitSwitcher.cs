using System.Collections.Generic;
using UnityEngine;

namespace ParelVR.AvatarSDK
{
    /// <summary>
    /// Switch between whole outfits from one menu: wearing one outfit hides every other. The SDK
    /// builds one Int parameter, a sub menu with a button per outfit, and the FX layer that shows
    /// the right objects.
    /// </summary>
    [AddComponentMenu("ParelVR/Avatar Tools/Outfit Switcher")]
    public sealed class ParelOutfitSwitcher : MonoBehaviour, IParelEditorOnly
    {
        [Tooltip("Name of the sub menu holding the outfit buttons.")]
        public string menuName = "Outfits";

        [Tooltip("Where that sub menu goes, e.g. \"Clothing\" (empty = the top of the menu).")]
        public string menuFolder = string.Empty;

        public Texture2D icon;

        [Tooltip("The Expression Parameter used (empty = made from the menu name).")]
        public string parameterName = string.Empty;

        [Tooltip("Remember the last outfit between sessions.")]
        public bool saved = true;

        [Tooltip("Everyone else sees the outfit change too.")]
        public bool synced = true;

        [Tooltip("The outfit worn when the avatar is first put on (0 = the first one).")]
        [Min(0)] public int defaultOutfit;

        public List<ParelOutfit> outfits = new List<ParelOutfit>();

        public string MenuLabel => string.IsNullOrWhiteSpace(menuName) ? gameObject.name : menuName.Trim();
    }
}

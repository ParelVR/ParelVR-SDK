using System.Collections.Generic;
using UnityEngine;

namespace ParelVR.AvatarSDK
{
    /// <summary>
    /// One-component on/off toggle. Put it on the thing you want to toggle (or anywhere, and list the
    /// objects) and the SDK builds everything you'd otherwise wire by hand: the Expression Parameter,
    /// the Expressions Menu button and the FX layer with its On / Off animations.
    /// </summary>
    [AddComponentMenu("ParelVR/Avatar Tools/Object Toggle")]
    public sealed class ParelObjectToggle : MonoBehaviour, IParelEditorOnly
    {
        [Tooltip("The button's name in the Expressions Menu (empty = this object's name).")]
        public string menuName = string.Empty;

        [Tooltip("Sub menu path for the button, e.g. \"Clothing/Hats\" (empty = the top of the menu).")]
        public string menuFolder = string.Empty;

        public Texture2D icon;

        [Tooltip("On when the avatar is first worn.")]
        public bool defaultOn = true;

        [Tooltip("Remember the last setting between sessions.")]
        public bool saved = true;

        [Tooltip("Everyone else sees it too. Off = only you see the change.")]
        public bool synced = true;

        [Tooltip("The Expression Parameter used (empty = made from the menu name). Give toggles the same name to link them.")]
        public string parameterName = string.Empty;

        [Tooltip("Objects to turn on and off. Empty = this object.")]
        public List<ParelToggledObject> objects = new List<ParelToggledObject>();

        public List<ParelBlendShapeChange> blendShapes = new List<ParelBlendShapeChange>();
        public List<ParelMaterialSwap> materialSwaps = new List<ParelMaterialSwap>();

        public string MenuLabel => string.IsNullOrWhiteSpace(menuName) ? gameObject.name : menuName.Trim();

        /// <summary>The objects this toggle drives (this object when nothing else is listed).</summary>
        public List<ParelToggledObject> Targets()
        {
            var result = new List<ParelToggledObject>();
            if (objects != null)
            {
                foreach (ParelToggledObject entry in objects)
                {
                    if (entry != null && entry.target != null) result.Add(entry);
                }
            }
            bool changesSomethingElse = (blendShapes != null && blendShapes.Count > 0) || (materialSwaps != null && materialSwaps.Count > 0);
            if (result.Count == 0 && !changesSomethingElse) result.Add(new ParelToggledObject { target = gameObject });
            return result;
        }
    }
}

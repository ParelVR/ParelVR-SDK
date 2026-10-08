using System;
using UnityEngine;

namespace ParelVR.AvatarSDK
{
    /// <summary>
    /// Marks a component that only exists while you work in Unity. When an avatar is built, the SDK
    /// applies what these components describe (toggles, outfits, merged clothing...) to the build copy
    /// and then removes them, so they never ship inside the avatar.
    /// </summary>
    public interface IParelEditorOnly
    {
    }

    /// <summary>A GameObject an Object Toggle turns on and off.</summary>
    [Serializable]
    public sealed class ParelToggledObject
    {
        public GameObject target;
        [Tooltip("Turn this object OFF when the toggle is on (and on when it's off).")]
        public bool invert;
    }

    /// <summary>A blend shape set to one value while a toggle / outfit is on and another while it's off.</summary>
    [Serializable]
    public sealed class ParelBlendShapeChange
    {
        public SkinnedMeshRenderer renderer;
        public string blendShape = string.Empty;
        [Range(0f, 100f)] public float onValue = 100f;
        [Range(0f, 100f)] public float offValue;
    }

    /// <summary>A material slot swapped while a toggle is on (off = the renderer's own material).</summary>
    [Serializable]
    public sealed class ParelMaterialSwap
    {
        public Renderer renderer;
        [Min(0)] public int slot;
        public Material onMaterial;
    }

    /// <summary>One outfit of an Outfit Switcher.</summary>
    [Serializable]
    public sealed class ParelOutfit
    {
        public string name = "Outfit";
        public Texture2D icon;
        [Tooltip("Everything that belongs to this outfit. Shown while it's worn, hidden while another outfit is.")]
        public GameObject[] objects = Array.Empty<GameObject>();
        public ParelBlendShapeChange[] blendShapes = Array.Empty<ParelBlendShapeChange>();
    }

    /// <summary>One source -> target blend shape pair for Blend Shape Sync.</summary>
    [Serializable]
    public sealed class ParelBlendShapeLink
    {
        public string source = string.Empty;
        [Tooltip("Empty = the same name as the source.")]
        public string target = string.Empty;
    }
}

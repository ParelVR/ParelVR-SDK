using UnityEngine;

namespace ParelVR.WorldSDK
{
    /// <summary>
    /// 3D Audio Raycast Material: what a wall, floor or prop is made of, for 3D Audio Raycast sound. It sets how much
    /// sound gets through it (brick and concrete let almost nothing through, wood and glass more, curtains and bushes
    /// most of it, always more of the lows than the highs) and how much it soaks up (a carpeted room echoes less than a
    /// tiled one). It works for the colliders on this object and all its children. Without one, 3D Audio Raycast
    /// guesses from the collider's physics material, the object's material and its name (glass, wood, metal, brick,
    /// concrete, stone, carpet, curtain, fabric, bush, leaves, water), and uses Generic for the rest.
    /// </summary>
    [AddComponentMenu("ParelVR/World SDK/3D Audio Raycast Material")]
    [DisallowMultipleComponent]
    public sealed class ParelRaycastAudioMaterial : MonoBehaviour
    {
        public enum Kind
        {
            Generic,
            Concrete,
            Brick,
            Stone,
            Wood,
            Glass,
            Metal,
            Plaster,
            Tile,
            Carpet,
            Fabric,
            Foliage,
            Water,
            Grate,
            Custom
        }

        [Tooltip("What it is made of. Custom = set the numbers yourself.")]
        public Kind material = Kind.Generic;

        [Header("Custom")]
        [Tooltip("How much sound gets through, for low, middle and high pitches (0 = nothing, 1 = all of it).")]
        public Vector3 letsThrough = new Vector3(0.32f, 0.1f, 0.02f);
        [Tooltip("How much sound it soaks up instead of bouncing back, for low, middle and high pitches (0 = it all " +
                 "bounces, 1 = none does).")]
        public Vector3 soaksUp = new Vector3(0.1f, 0.2f, 0.3f);

        internal ParelAcoustic Values => material == Kind.Custom
            ? new ParelAcoustic(letsThrough.x, letsThrough.y, letsThrough.z, soaksUp.x, soaksUp.y, soaksUp.z)
            : ParelAcoustic.Of(material);

        private void OnValidate() => ParelRaycastAudioEngine.ForgetMaterials();
        private void OnEnable() => ParelRaycastAudioEngine.ForgetMaterials();
    }

    /// <summary>
    /// What a surface does to sound in three bands (low below ~350 Hz, middle, high above ~2.2 kHz): t = the share of
    /// the sound's amplitude that gets through, a = the share of its energy it soaks up.
    /// </summary>
    internal readonly struct ParelAcoustic
    {
        public readonly float tL, tM, tH, aL, aM, aH;

        public ParelAcoustic(float tl, float tm, float th, float al, float am, float ah)
        {
            tL = Mathf.Clamp01(tl); tM = Mathf.Clamp01(tm); tH = Mathf.Clamp01(th);
            aL = Mathf.Clamp(al, 0.01f, 1f); aM = Mathf.Clamp(am, 0.01f, 1f); aH = Mathf.Clamp(ah, 0.01f, 1f);
        }

        public static readonly ParelAcoustic Generic = new ParelAcoustic(0.32f, 0.1f, 0.02f, 0.1f, 0.2f, 0.3f);

        public static ParelAcoustic Of(ParelRaycastAudioMaterial.Kind kind)
        {
            switch (kind)
            {
                case ParelRaycastAudioMaterial.Kind.Concrete: return new ParelAcoustic(0.1f, 0.02f, 0.005f, 0.02f, 0.03f, 0.05f);
                case ParelRaycastAudioMaterial.Kind.Brick: return new ParelAcoustic(0.12f, 0.025f, 0.006f, 0.03f, 0.04f, 0.07f);
                case ParelRaycastAudioMaterial.Kind.Stone: return new ParelAcoustic(0.08f, 0.015f, 0.004f, 0.02f, 0.03f, 0.04f);
                case ParelRaycastAudioMaterial.Kind.Wood: return new ParelAcoustic(0.45f, 0.18f, 0.05f, 0.15f, 0.1f, 0.08f);
                case ParelRaycastAudioMaterial.Kind.Glass: return new ParelAcoustic(0.35f, 0.22f, 0.08f, 0.18f, 0.06f, 0.03f);
                case ParelRaycastAudioMaterial.Kind.Metal: return new ParelAcoustic(0.3f, 0.08f, 0.03f, 0.05f, 0.04f, 0.05f);
                case ParelRaycastAudioMaterial.Kind.Plaster: return new ParelAcoustic(0.25f, 0.06f, 0.015f, 0.12f, 0.08f, 0.05f);
                case ParelRaycastAudioMaterial.Kind.Tile: return new ParelAcoustic(0.15f, 0.03f, 0.008f, 0.01f, 0.02f, 0.03f);
                case ParelRaycastAudioMaterial.Kind.Carpet: return new ParelAcoustic(0.5f, 0.25f, 0.1f, 0.06f, 0.3f, 0.6f);
                case ParelRaycastAudioMaterial.Kind.Fabric: return new ParelAcoustic(0.85f, 0.6f, 0.35f, 0.1f, 0.45f, 0.65f);
                case ParelRaycastAudioMaterial.Kind.Foliage: return new ParelAcoustic(0.9f, 0.7f, 0.45f, 0.1f, 0.25f, 0.4f);
                case ParelRaycastAudioMaterial.Kind.Water: return new ParelAcoustic(0.4f, 0.15f, 0.05f, 0.01f, 0.01f, 0.02f);
                case ParelRaycastAudioMaterial.Kind.Grate: return new ParelAcoustic(0.95f, 0.85f, 0.7f, 0.05f, 0.1f, 0.15f);
                default: return Generic;
            }
        }

        /// <summary>A guess from a name: "Glass_Window", "WoodFloor", "Bush03"...</summary>
        public static bool Guess(string name, out ParelAcoustic acoustic)
        {
            acoustic = Generic;
            if (string.IsNullOrEmpty(name)) return false;
            string n = name.ToLowerInvariant();
            ParelRaycastAudioMaterial.Kind kind;
            if (n.Contains("glass") || n.Contains("window")) kind = ParelRaycastAudioMaterial.Kind.Glass;
            else if (n.Contains("concrete") || n.Contains("cement")) kind = ParelRaycastAudioMaterial.Kind.Concrete;
            else if (n.Contains("brick")) kind = ParelRaycastAudioMaterial.Kind.Brick;
            else if (n.Contains("stone") || n.Contains("rock") || n.Contains("marble") || n.Contains("cliff")) kind = ParelRaycastAudioMaterial.Kind.Stone;
            else if (n.Contains("wood") || n.Contains("plank") || n.Contains("timber")) kind = ParelRaycastAudioMaterial.Kind.Wood;
            else if (n.Contains("metal") || n.Contains("steel") || n.Contains("iron")) kind = ParelRaycastAudioMaterial.Kind.Metal;
            else if (n.Contains("tile") || n.Contains("ceramic")) kind = ParelRaycastAudioMaterial.Kind.Tile;
            else if (n.Contains("plaster") || n.Contains("drywall")) kind = ParelRaycastAudioMaterial.Kind.Plaster;
            else if (n.Contains("carpet") || n.Contains("rug")) kind = ParelRaycastAudioMaterial.Kind.Carpet;
            else if (n.Contains("curtain") || n.Contains("fabric") || n.Contains("cloth")) kind = ParelRaycastAudioMaterial.Kind.Fabric;
            else if (n.Contains("bush") || n.Contains("leaf") || n.Contains("leaves") || n.Contains("foliage") || n.Contains("hedge")) kind = ParelRaycastAudioMaterial.Kind.Foliage;
            else if (n.Contains("water")) kind = ParelRaycastAudioMaterial.Kind.Water;
            else if (n.Contains("fence") || n.Contains("grate") || n.Contains("railing") || n.Contains("mesh_fence")) kind = ParelRaycastAudioMaterial.Kind.Grate;
            else return false;
            acoustic = Of(kind);
            return true;
        }
    }
}

using System;
using UnityEditor;
using UnityEngine;

namespace ParelVR.Rendering.Editor
{
    /// <summary>
    /// Reads an HDRP material's Diffusion Profile at upload: its scattering colour becomes ParelVR/Subsurface's
    /// scattering colour and its transmission tint the transmission tint. HDRP stores the profile asset's GUID packed in
    /// the 16 bytes of a Vector4 (_DiffusionProfileAsset); the profile is read through reflection so the SDK needs no HDRP.
    /// </summary>
    public static class ParelDiffusionProfiles
    {
        public static bool Resolve(Material source, MaterialDescription d)
        {
            if (source == null || d == null || !d.Subsurface) return false;
            Vector4 packed = Packed(source, d);
            if (packed == Vector4.zero) return false;

            var bytes = new byte[16];
            Buffer.BlockCopy(BitConverter.GetBytes(packed.x), 0, bytes, 0, 4);
            Buffer.BlockCopy(BitConverter.GetBytes(packed.y), 0, bytes, 4, 4);
            Buffer.BlockCopy(BitConverter.GetBytes(packed.z), 0, bytes, 8, 4);
            Buffer.BlockCopy(BitConverter.GetBytes(packed.w), 0, bytes, 12, 4);
            var guid = new System.Text.StringBuilder(32);
            foreach (byte b in bytes) guid.Append(b.ToString("x2"));
            string path = AssetDatabase.GUIDToAssetPath(guid.ToString());
            if (string.IsNullOrEmpty(path)) return false;
            UnityEngine.Object asset = AssetDatabase.LoadMainAssetAtPath(path);
            object profile = SrpSceneReader.Get(asset, "profile");
            if (profile == null) return false;

            if (SrpSceneReader.Get(profile, "scatteringDistance") is Color scatter)
            {
                // The hue of the scattering distance: the channel that scatters furthest shows most.
                float max = Mathf.Max(scatter.r, Mathf.Max(scatter.g, scatter.b));
                if (max > 1e-4f) d.SubsurfaceColor = new ColorValue(scatter.r / max, scatter.g / max, scatter.b / max, 1f);
            }
            if (SrpSceneReader.Get(profile, "transmissionTint") is Color tint)
                d.TransmissionTint = new ColorValue(tint.r, tint.g, tint.b, 1f);
            return true;
        }

        // The material's own value when its shader is present (exact floats), else what the reader saw.
        private static Vector4 Packed(Material m, MaterialDescription d)
        {
            foreach (string name in new[] { "_DiffusionProfileAsset", "_DiffusionProfileAsset0" })
                if (m.shader != null && m.HasProperty(name))
                {
                    Vector4 v = m.GetVector(name);
                    if (v != Vector4.zero) return v;
                }
            ColorValue c = d.DiffusionProfileRef;
            return new Vector4(c.R, c.G, c.B, c.A);
        }
    }
}

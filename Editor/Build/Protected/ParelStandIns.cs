using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;
using UnityEngine.Rendering;

namespace ParelVR.SDK.Build.Protected
{
    /// <summary>
    /// Stand-ins for protected meshes, textures and sounds: good enough to set an avatar up (same bones, weights,
    /// blendshape names and material slots, the rough shape and colors) and useless as a copy.
    /// </summary>
    public static class ParelStandIns
    {
        /// <summary>Cells along a mesh's longest side; every vertex in a cell becomes one.</summary>
        public const int MeshCells = 32;
        /// <summary>Longest side of a stand-in texture.</summary>
        public const int TextureSize = 32;
        private const float MaxAudioSeconds = 30f;
        private const int AudioRate = 8000;

        /// <summary>
        /// A blocky copy of <paramref name="source"/> by vertex clustering: bind poses, bone weights, blendshape names and
        /// frames and the sub-mesh count stay, so renderers, visemes and material slots keep working.
        /// </summary>
        public static Mesh Mesh(Mesh source)
        {
            Mesh readable = source.isReadable ? source : UnityEngine.Object.Instantiate(source);
            try
            {
                Vector3[] vertices = readable.vertices;
                Vector2[] uvs = readable.uv;
                BoneWeight[] weights = readable.boneWeights;
                Bounds bounds = readable.bounds;
                float cell = Mathf.Max(bounds.size.x, Mathf.Max(bounds.size.y, bounds.size.z)) / MeshCells;
                if (cell <= 1e-6f) cell = 0.01f;

                var cluster = new int[vertices.Length];
                var lookup = new Dictionary<long, int>();
                var sums = new List<Vector3>();
                var counts = new List<int>();
                var firsts = new List<int>();
                for (int i = 0; i < vertices.Length; i++)
                {
                    Vector3 p = (vertices[i] - bounds.min) / cell;
                    long key = (long)Mathf.Clamp(Mathf.FloorToInt(p.x), 0, 0xFFFF)
                               | ((long)Mathf.Clamp(Mathf.FloorToInt(p.y), 0, 0xFFFF) << 16)
                               | ((long)Mathf.Clamp(Mathf.FloorToInt(p.z), 0, 0xFFFF) << 32);
                    if (!lookup.TryGetValue(key, out int c))
                    {
                        c = sums.Count;
                        lookup[key] = c;
                        sums.Add(Vector3.zero);
                        counts.Add(0);
                        firsts.Add(i);
                    }
                    cluster[i] = c;
                    sums[c] += vertices[i];
                    counts[c]++;
                }

                int n = sums.Count;
                var outVertices = new Vector3[n];
                for (int c = 0; c < n; c++) outVertices[c] = sums[c] / counts[c];

                var mesh = new Mesh { name = source.name, indexFormat = n > 65535 ? IndexFormat.UInt32 : IndexFormat.UInt16 };
                mesh.vertices = outVertices;
                if (uvs != null && uvs.Length == vertices.Length)
                {
                    var outUv = new Vector2[n];
                    for (int c = 0; c < n; c++) outUv[c] = uvs[firsts[c]];
                    mesh.uv = outUv;
                }
                if (weights != null && weights.Length == vertices.Length)
                {
                    var outWeights = new BoneWeight[n];
                    for (int c = 0; c < n; c++) outWeights[c] = weights[firsts[c]];
                    mesh.boneWeights = outWeights;
                    mesh.bindposes = readable.bindposes;
                }

                mesh.subMeshCount = readable.subMeshCount;
                for (int s = 0; s < readable.subMeshCount; s++)
                {
                    var triangles = new List<int>();
                    if (readable.GetTopology(s) == MeshTopology.Triangles)
                    {
                        int[] source3 = readable.GetTriangles(s);
                        var seen = new HashSet<long>();
                        for (int t = 0; t + 2 < source3.Length; t += 3)
                        {
                            int a = cluster[source3[t]], b = cluster[source3[t + 1]], c = cluster[source3[t + 2]];
                            if (a == b || b == c || a == c) continue;
                            int lo = Math.Min(a, Math.Min(b, c)), hi = Math.Max(a, Math.Max(b, c)), mid = a + b + c - lo - hi;
                            if (!seen.Add(((long)lo * n + mid) * n + hi)) continue;
                            triangles.Add(a);
                            triangles.Add(b);
                            triangles.Add(c);
                        }
                    }
                    mesh.SetTriangles(triangles, s, false);
                }

                int shapes = readable.blendShapeCount;
                if (shapes > 0)
                {
                    var delta = new Vector3[vertices.Length];
                    var deltaNormals = new Vector3[vertices.Length];
                    var deltaTangents = new Vector3[vertices.Length];
                    var outDelta = new Vector3[n];
                    for (int shape = 0; shape < shapes; shape++)
                    {
                        string name = readable.GetBlendShapeName(shape);
                        int frames = readable.GetBlendShapeFrameCount(shape);
                        for (int frame = 0; frame < frames; frame++)
                        {
                            readable.GetBlendShapeFrameVertices(shape, frame, delta, deltaNormals, deltaTangents);
                            Array.Clear(outDelta, 0, n);
                            for (int i = 0; i < vertices.Length; i++) outDelta[cluster[i]] += delta[i];
                            for (int c = 0; c < n; c++) outDelta[c] /= counts[c];
                            mesh.AddBlendShapeFrame(name, readable.GetBlendShapeFrameWeight(shape, frame), outDelta, null, null);
                        }
                    }
                }

                mesh.RecalculateNormals();
                mesh.RecalculateTangents();
                mesh.RecalculateBounds();
                return mesh;
            }
            finally
            {
                if (readable != source) UnityEngine.Object.DestroyImmediate(readable);
            }
        }

        /// <summary>A <see cref="TextureSize"/> px PNG of <paramref name="source"/>; a flat one for normal maps.</summary>
        public static byte[] TexturePng(Texture source, bool normalMap, bool linear)
        {
            if (normalMap || source == null)
            {
                var flat = new Texture2D(4, 4, TextureFormat.RGBA32, false, true);
                var pixels = new Color32[16];
                Color32 color = normalMap ? new Color32(128, 128, 255, 255) : new Color32(128, 128, 128, 255);
                for (int i = 0; i < pixels.Length; i++) pixels[i] = color;
                flat.SetPixels32(pixels);
                flat.Apply();
                try { return flat.EncodeToPNG(); }
                finally { UnityEngine.Object.DestroyImmediate(flat); }
            }

            int w = TextureSize, h = TextureSize;
            if (source.width >= source.height) h = Mathf.Max(1, TextureSize * source.height / Mathf.Max(1, source.width));
            else w = Mathf.Max(1, TextureSize * source.width / Mathf.Max(1, source.height));

            RenderTexture rt = RenderTexture.GetTemporary(w, h, 0, RenderTextureFormat.ARGB32, linear ? RenderTextureReadWrite.Linear : RenderTextureReadWrite.sRGB);
            RenderTexture previous = RenderTexture.active;
            var small = new Texture2D(w, h, TextureFormat.RGBA32, false, linear);
            try
            {
                Graphics.Blit(source, rt);
                RenderTexture.active = rt;
                small.ReadPixels(new Rect(0, 0, w, h), 0, 0);
                small.Apply();
                return small.EncodeToPNG();
            }
            finally
            {
                RenderTexture.active = previous;
                RenderTexture.ReleaseTemporary(rt);
                UnityEngine.Object.DestroyImmediate(small);
            }
        }

        /// <summary>A plain gray cubemap.</summary>
        public static Cubemap Cubemap(string name)
        {
            var cube = new Cubemap(4, TextureFormat.RGBA32, false) { name = name };
            var pixels = new Color[16];
            for (int i = 0; i < pixels.Length; i++) pixels[i] = new Color(0.5f, 0.5f, 0.5f, 1f);
            foreach (CubemapFace face in new[] { CubemapFace.PositiveX, CubemapFace.NegativeX, CubemapFace.PositiveY, CubemapFace.NegativeY, CubemapFace.PositiveZ, CubemapFace.NegativeZ })
                cube.SetPixels(pixels, face);
            cube.Apply();
            return cube;
        }

        /// <summary>A silent 16-bit WAV as long as <paramref name="source"/> (up to 30 s), with its channel count.</summary>
        public static byte[] SilentWav(AudioClip source)
        {
            int channels = Mathf.Clamp(source != null ? source.channels : 1, 1, 2);
            float seconds = Mathf.Clamp(source != null ? source.length : 0.1f, 0.05f, MaxAudioSeconds);
            int samples = Mathf.CeilToInt(seconds * AudioRate) * channels;
            int dataBytes = samples * 2;
            using (var stream = new MemoryStream(44 + dataBytes))
            using (var writer = new BinaryWriter(stream))
            {
                writer.Write(new[] { (byte)'R', (byte)'I', (byte)'F', (byte)'F' });
                writer.Write(36 + dataBytes);
                writer.Write(new[] { (byte)'W', (byte)'A', (byte)'V', (byte)'E', (byte)'f', (byte)'m', (byte)'t', (byte)' ' });
                writer.Write(16);
                writer.Write((short)1);
                writer.Write((short)channels);
                writer.Write(AudioRate);
                writer.Write(AudioRate * channels * 2);
                writer.Write((short)(channels * 2));
                writer.Write((short)16);
                writer.Write(new[] { (byte)'d', (byte)'a', (byte)'t', (byte)'a' });
                writer.Write(dataBytes);
                writer.Write(new byte[dataBytes]);
                writer.Flush();
                return stream.ToArray();
            }
        }
    }
}

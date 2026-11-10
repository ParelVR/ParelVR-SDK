using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;

namespace ParelVR.Rendering.Editor
{
    /// <summary>
    /// Bakes HDRP / URP Decal Projectors into geometry at upload. For each projector, the opaque surfaces inside its box
    /// are clipped to the box and turned into a decal mesh with the projector's UVs, drawn with a ParelVR transparent
    /// material made from the decal material, a few millimetres off the surface. Baked decals cost one draw call each and
    /// no projection at runtime (Meta / Standalone VR too). They stick to what was in the box at upload: objects that
    /// move later don't get them. Runs on the build copy only; the projector component is removed from that copy.
    /// </summary>
    public static class ParelDecalBaker
    {
        /// <summary>Surface offset against z-fighting, in metres.</summary>
        public const float SurfaceOffset = 0.002f;
        /// <summary>Triangles per decal mesh beyond which the rest is left out (a decal over a whole terrain-sized mesh).</summary>
        public const int MaxTriangles = 60000;

        public static int BakeAll(Scene scene, string generatedFolder, CompatibilityReport report)
        {
            var projectors = new List<Component>();
            foreach (GameObject root in scene.GetRootGameObjects())
                foreach (Component c in root.GetComponentsInChildren<Component>(true))
                    if (c != null && c.GetType().Name == "DecalProjector") projectors.Add(c);
            if (projectors.Count == 0) return 0;

            var receivers = new List<MeshRenderer>();
            foreach (GameObject root in scene.GetRootGameObjects())
                foreach (MeshRenderer r in root.GetComponentsInChildren<MeshRenderer>(true))
                    if (r.enabled && r.gameObject.activeInHierarchy && r.TryGetComponent(out MeshFilter f) && f.sharedMesh != null) receivers.Add(r);

            int baked = 0;
            var materials = new Dictionary<Material, Material>();
            foreach (Component p in projectors)
            {
                string where = ParelSceneConverter.Path(p.transform);
                bool active = !(p is Behaviour b) || (b.enabled && b.gameObject.activeInHierarchy);
                Material source = SrpSceneReader.Get(p, "material") as Material;
                if (active && source != null)
                {
                    if (!materials.TryGetValue(source, out Material decalMaterial))
                        materials[source] = decalMaterial = DecalMaterial(source, generatedFolder, report, where);
                    if (decalMaterial != null)
                    {
                        int tris = Bake(p, decalMaterial, receivers, generatedFolder, out bool capped);
                        if (tris > 0)
                        {
                            baked++;
                            report.Note(capped ? DiagnosticSeverity.Warning : DiagnosticSeverity.Info, where,
                                "Decal baked into the surfaces under it (" + tris + " triangles)" + (capped ? "; it covered too much geometry and was cut at " + MaxTriangles + " triangles" : "") + ".");
                        }
                        else report.Note(DiagnosticSeverity.Notice, where, "Decal projector touches no opaque surface: nothing to bake.");
                    }
                }
                UnityEngine.Object.DestroyImmediate(p); // HDRP/URP component: not available in ParelVR
            }
            return baked;
        }

        // ---- material -------------------------------------------------------------------------------------------

        private static Texture Tex(Material m, params string[] names)
        {
            foreach (string n in names)
                if (m.HasProperty(n) && m.GetTexture(n) != null) return m.GetTexture(n);
            return null;
        }

        private static float Float(Material m, string name, float fallback) => m.HasProperty(name) ? m.GetFloat(name) : fallback;

        private static Material DecalMaterial(Material source, string folder, CompatibilityReport report, string where)
        {
            bool affectsAlbedo = Float(source, "_AffectAlbedo", 1f) > 0.5f;
            Texture baseMap = Tex(source, "_BaseColorMap", "Base_Map", "_BaseMap", "_MainTex");
            Texture normalMap = Tex(source, "_NormalMap", "Normal_Map", "_BumpMap");
            if (!affectsAlbedo)
            {
                report.Note(DiagnosticSeverity.Notice, where, "Decal that only changes normals or smoothness (no colour): left out, it would show as a flat patch.");
                return null;
            }

            Color color = source.HasProperty("_BaseColor") ? source.GetColor("_BaseColor") : source.HasProperty("_Color") ? source.GetColor("_Color") : Color.white;
            color.a *= Float(source, "_DecalBlend", 1f);
            Shader shader = (ParelShaderLibrary.Active != null ? ParelShaderLibrary.Active.Get(ShaderRole.Transparent, TargetPlatform.Pc) : null) ?? Shader.Find(ParelShaderNames.Transparent);
            if (shader == null) return null;
            var d = new MaterialDescription
            {
                Surface = SurfaceType.Fade,
                BaseColor = new ColorValue(color.r, color.g, color.b, color.a),
                BaseMap = new TextureSlot(baseMap),
                NormalMap = normalMap != null ? new TextureSlot(normalMap) : null,
                NormalScale = Float(source, "_NormalScale", 1f),
                Metallic = 0f,
                Smoothness = Float(source, "_Smoothness", Float(source, "_DecalMaskMapBlueScale", 0.4f)),
                // Just before other transparent surfaces; the decal's draw order sorts overlapping decals.
                RenderQueue = 2950 + Mathf.Clamp((int)Float(source, "_DrawOrder", 0f), -40, 40),
                ReceiveShadows = true,
            };
            var m = new Material(shader) { name = source.name + " (ParelVR decal)", enableInstancing = true };
            new ParelMaterialWriter().Write(d, ShaderRole.Transparent, TargetPlatform.Pc, new UnityMaterialSink(m));
            m.SetFloat("_ZWrite", 0f);
            if (!AssetDatabase.IsValidFolder(folder)) ParelSceneConverter.CreateFolder(folder);
            AssetDatabase.CreateAsset(m, AssetDatabase.GenerateUniqueAssetPath(folder + "/" + Safe(m.name) + ".mat"));
            return m;
        }

        // ---- geometry -------------------------------------------------------------------------------------------

        private static int Bake(Component projector, Material material, List<MeshRenderer> receivers, string folder, out bool capped)
        {
            capped = false;
            Transform t = projector.transform;
            Vector3 size = SrpSceneReader.Get(projector, "size") is Vector3 s ? s : Vector3.one;
            Vector3 pivot = SrpSceneReader.Get(projector, "pivot") is Vector3 pv ? pv : Vector3.zero;
            Vector2 uvScale = SrpSceneReader.Get(projector, "uvScale") is Vector2 us ? us : Vector2.one;
            Vector2 uvBias = SrpSceneReader.Get(projector, "uvBias") is Vector2 ub ? ub : Vector2.zero;
            float fade = SrpSceneReader.Get(projector, "fadeFactor") is float ff ? ff : 1f;
            float startAngle = SrpSceneReader.Get(projector, "startAngleFade") is float sa ? sa : 180f;
            float endAngle = SrpSceneReader.Get(projector, "endAngleFade") is float ea ? ea : 180f;
            if ((SrpSceneReader.Get(projector, "scaleMode")?.ToString() ?? "") == "InheritFromHierarchy") size = Vector3.Scale(size, t.lossyScale);
            if (fade <= 0.001f || size.x <= 0f || size.y <= 0f || size.z <= 0f) return 0;

            // Decal space: the projector's position and rotation, metres; the box spans pivot ± size / 2, projecting along +Z.
            Matrix4x4 decalToWorld = Matrix4x4.TRS(t.position, t.rotation, Vector3.one);
            Matrix4x4 worldToDecal = decalToWorld.inverse;
            Vector3 min = pivot - size * 0.5f, max = pivot + size * 0.5f;
            Bounds worldBox = WorldBounds(decalToWorld, min, max);
            float keepAngle = endAngle >= 179.9f ? 89.9f : Mathf.Lerp(startAngle, endAngle, 0.5f); // fade to half: kept

            var verts = new List<Vector3>();
            var normals = new List<Vector3>();
            var uvs = new List<Vector2>();
            var indices = new List<int>();
            var poly = new List<Vector3>(9);
            var scratch = new List<Vector3>(9);

            foreach (MeshRenderer r in receivers)
            {
                if (!r.bounds.Intersects(worldBox) || r.transform.IsChildOf(t)) continue;
                Mesh mesh = r.GetComponent<MeshFilter>().sharedMesh;
                Material[] mats = r.sharedMaterials;
                Vector3[] mv = mesh.vertices;
                Matrix4x4 toDecal = worldToDecal * r.localToWorldMatrix;
                var dv = new Vector3[mv.Length];
                for (int i = 0; i < mv.Length; i++) dv[i] = toDecal.MultiplyPoint3x4(mv[i]);

                for (int sub = 0; sub < mesh.subMeshCount; sub++)
                {
                    Material receiverMaterial = sub < mats.Length ? mats[sub] : null;
                    if (receiverMaterial != null && receiverMaterial.renderQueue >= 2500) continue; // decals only land on opaque surfaces
                    if (mesh.GetTopology(sub) != MeshTopology.Triangles) continue;
                    int[] tris = mesh.GetTriangles(sub);
                    for (int i = 0; i + 2 < tris.Length; i += 3)
                    {
                        Vector3 a = dv[tris[i]], b = dv[tris[i + 1]], c = dv[tris[i + 2]];
                        if (Mathf.Max(a.x, b.x, c.x) < min.x || Mathf.Min(a.x, b.x, c.x) > max.x ||
                            Mathf.Max(a.y, b.y, c.y) < min.y || Mathf.Min(a.y, b.y, c.y) > max.y ||
                            Mathf.Max(a.z, b.z, c.z) < min.z || Mathf.Min(a.z, b.z, c.z) > max.z) continue;
                        Vector3 n = Vector3.Cross(b - a, c - a);
                        if (n.sqrMagnitude < 1e-12f) continue;
                        n.Normalize();
                        // Surfaces facing the projector (normal against +Z) within the fade angle.
                        float angle = Vector3.Angle(n, Vector3.back);
                        if (angle > keepAngle) continue;

                        poly.Clear();
                        poly.Add(a); poly.Add(b); poly.Add(c);
                        for (int axis = 0; axis < 3 && poly.Count >= 3; axis++)
                        {
                            Clip(poly, scratch, axis, min[axis], true);
                            Clip(poly, scratch, axis, max[axis], false);
                        }
                        if (poly.Count < 3) continue;
                        if (indices.Count / 3 + poly.Count - 2 > MaxTriangles)
                        {
                            capped = true;
                            break;
                        }
                        int start = verts.Count;
                        foreach (Vector3 p in poly)
                        {
                            verts.Add(p + n * SurfaceOffset);
                            normals.Add(n);
                            uvs.Add(new Vector2((p.x - min.x) / size.x * uvScale.x + uvBias.x, (p.y - min.y) / size.y * uvScale.y + uvBias.y));
                        }
                        for (int k = 1; k + 1 < poly.Count; k++)
                        {
                            indices.Add(start);
                            indices.Add(start + k);
                            indices.Add(start + k + 1);
                        }
                    }
                    if (capped) break;
                }
                if (capped) break;
            }
            if (indices.Count == 0) return 0;

            var decalMesh = new Mesh { name = projector.name + " decal" };
            if (verts.Count > 65000) decalMesh.indexFormat = IndexFormat.UInt32;
            decalMesh.SetVertices(verts);
            decalMesh.SetNormals(normals);
            decalMesh.SetUVs(0, uvs);
            decalMesh.SetTriangles(indices, 0);
            decalMesh.RecalculateTangents();
            decalMesh.RecalculateBounds();
            if (!AssetDatabase.IsValidFolder(folder)) ParelSceneConverter.CreateFolder(folder);
            AssetDatabase.CreateAsset(decalMesh, AssetDatabase.GenerateUniqueAssetPath(folder + "/" + Safe(decalMesh.name) + ".asset"));

            var go = new GameObject(projector.name + " (baked decal)");
            SceneManager.MoveGameObjectToScene(go, projector.gameObject.scene);
            go.transform.SetParent(t, false);
            Vector3 ls = t.lossyScale;
            go.transform.localScale = new Vector3(Inv(ls.x), Inv(ls.y), Inv(ls.z)); // decal space is unscaled
            go.layer = projector.gameObject.layer;
            go.isStatic = projector.gameObject.isStatic;
            go.AddComponent<MeshFilter>().sharedMesh = decalMesh;
            var renderer = go.AddComponent<MeshRenderer>();
            renderer.sharedMaterial = material;
            renderer.shadowCastingMode = ShadowCastingMode.Off;
            renderer.receiveShadows = true;
            renderer.lightProbeUsage = LightProbeUsage.BlendProbes;
            return indices.Count / 3;
        }

        private static float Inv(float v) => Mathf.Abs(v) > 1e-6f ? 1f / v : 1f;

        /// <summary>Sutherland–Hodgman against one axis-aligned plane: keeps the side ≥ value (lower) or ≤ value (upper).</summary>
        private static void Clip(List<Vector3> poly, List<Vector3> scratch, int axis, float value, bool lower)
        {
            scratch.Clear();
            for (int i = 0; i < poly.Count; i++)
            {
                Vector3 cur = poly[i], next = poly[(i + 1) % poly.Count];
                float dc = lower ? cur[axis] - value : value - cur[axis];
                float dn = lower ? next[axis] - value : value - next[axis];
                if (dc >= 0f) scratch.Add(cur);
                if ((dc >= 0f) != (dn >= 0f)) scratch.Add(Vector3.Lerp(cur, next, dc / (dc - dn)));
            }
            poly.Clear();
            poly.AddRange(scratch);
        }

        private static Bounds WorldBounds(Matrix4x4 m, Vector3 min, Vector3 max)
        {
            var b = new Bounds(m.MultiplyPoint3x4(min), Vector3.zero);
            for (int i = 1; i < 8; i++)
                b.Encapsulate(m.MultiplyPoint3x4(new Vector3((i & 1) != 0 ? max.x : min.x, (i & 2) != 0 ? max.y : min.y, (i & 4) != 0 ? max.z : min.z)));
            return b;
        }

        private static string Safe(string name)
        {
            foreach (char c in System.IO.Path.GetInvalidFileNameChars()) name = name.Replace(c, '_');
            return string.IsNullOrEmpty(name) ? "Decal" : name;
        }
    }
}

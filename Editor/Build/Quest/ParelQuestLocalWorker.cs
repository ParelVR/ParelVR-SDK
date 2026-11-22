using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using ParelVR.SDK.Build;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;

namespace ParelVR.SDK.Build.Quest
{
    /// <summary>
    /// Runs inside the background Unity that <see cref="ParelQuestLocalBuild"/> starts with -buildTarget Android, on the
    /// background project's copy of the content (never the creator's files): moves materials to headset shaders, compresses
    /// textures and sound for Android, makes lights and effects cheaper, and builds the Android bundle, lowering texture
    /// sizes until it fits the platform's limit. Every warning and error Unity prints meanwhile goes into the result.
    /// </summary>
    public static class ParelQuestLocalWorker
    {
        private const string GeneratedFolder = "Assets/ParelVR_Quest_Generated";
        private const int MaxLogs = 400;

        private struct Squeeze
        {
            public int cap;
            public TextureImporterFormat format;
            public bool squeezeMeshesAndSound;

            public Squeeze(int cap, TextureImporterFormat format, bool squeezeMeshesAndSound)
            {
                this.cap = cap;
                this.format = format;
                this.squeezeMeshesAndSound = squeezeMeshesAndSound;
            }
        }

        // The rounds after the first, each smaller than the last: meshes and sound first (barely visible), then texture size, then coarser ASTC blocks.
        private static readonly Squeeze[] Steps =
        {
            new Squeeze(2048, TextureImporterFormat.Automatic, true),
            new Squeeze(1024, TextureImporterFormat.Automatic, true),
            new Squeeze(1024, TextureImporterFormat.ASTC_8x8, true),
            new Squeeze(512, TextureImporterFormat.ASTC_8x8, true),
            new Squeeze(256, TextureImporterFormat.ASTC_8x8, true),
            new Squeeze(256, TextureImporterFormat.ASTC_12x12, true),
            new Squeeze(128, TextureImporterFormat.ASTC_12x12, true),
        };

        private static readonly List<ParelQuestLogEntry> Logs = new List<ParelQuestLogEntry>();
        private static readonly HashSet<string> Seen = new HashSet<string>(StringComparer.Ordinal);

        private static void Step(string text) => Debug.Log(ParelQuestLocalBuild.StepPrefix + text);

        public static void Run()
        {
            var result = new ParelQuestLocalResult();
            string resultPath = null;
            int exitCode = 1;
            var timer = System.Diagnostics.Stopwatch.StartNew();
            Application.SetStackTraceLogType(LogType.Log, StackTraceLogType.None);
            Application.logMessageReceivedThreaded += Capture;
            try
            {
                string jobPath = Argument("-parelJob") ?? throw new Exception("No -parelJob given.");
                ParelQuestLocalJob job = JsonUtility.FromJson<ParelQuestLocalJob>(File.ReadAllText(jobPath));
                resultPath = job.resultPath;
                ParelQuestHints hints = job.hints ?? throw new Exception("The job has no content.");
                bool avatar = job.kind == ParelQuestSource.AvatarKind;
                if (EditorUserBuildSettings.activeBuildTarget != BuildTarget.Android) throw new Exception("The background build didn't start for Android.");

                Step("Setting the project up like ParelVR's Quest build");
                ApplyAndroidSettings();
                AssetDatabase.Refresh(ImportAssetOptions.ForceSynchronousImport);
                UseHeadsetPipeline();
                if (string.IsNullOrEmpty(hints.mainAsset) || AssetDatabase.LoadMainAssetAtPath(hints.mainAsset) == null)
                    throw new Exception("The copy for Android doesn't contain " + (avatar ? "the avatar" : "the scene") + " (" + hints.mainAsset + ").");
                List<string> scenes = avatar ? new List<string>() : (hints.scenes != null && hints.scenes.Count > 0 ? hints.scenes : new List<string> { hints.mainAsset });

                var report = new ParelQuestReport();
                var use = new ContentUse();
                string[] assets = AssetDatabase.FindAssets(string.Empty, new[] { "Assets" }).Select(AssetDatabase.GUIDToAssetPath).Distinct().ToArray();
                Step("Moving materials to headset shaders");
                ParelQuestConverter.ConvertMaterials(assets.Where(p => p.EndsWith(".mat", StringComparison.OrdinalIgnoreCase)), avatar, hints, report);
                ParelQuestConverter.ConvertAudio(assets.Where(p => AssetImporter.GetAtPath(p) is AudioImporter), report);
                ParelQuestConverter.ConvertRenderTextures(assets.Where(p => p.EndsWith(".renderTexture", StringComparison.OrdinalIgnoreCase)), report);

                if (avatar)
                {
                    Step("Making the avatar headset-ready");
                    GameObject contents = PrefabUtility.LoadPrefabContents(hints.mainAsset);
                    try
                    {
                        ParelQuestConverter.ConvertAvatar(contents, report);
                        PrefabUtility.SaveAsPrefabAsset(contents, hints.mainAsset);
                    }
                    finally
                    {
                        PrefabUtility.UnloadPrefabContents(contents);
                    }
                }
                else
                {
                    Step("Making the world headset-ready (lights, probes, effects, terrain)");
                    foreach (string scenePath in scenes)
                    {
                        Scene scene = EditorSceneManager.OpenScene(scenePath, OpenSceneMode.Single);
                        ParelQuestConverter.ConvertWorldScene(scene, GeneratedFolder, report);
                        use.Note(scene);
                        EditorSceneManager.SaveScene(scene);
                    }
                    EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
                }
                AssetDatabase.SaveAssets();
                KeepOnlyUsedVariants(avatar, use, report);
                ParelMipStreamingOff.TurnOffFor(avatar ? new[] { hints.mainAsset } : scenes.ToArray());

                string[] textures = AssetDatabase.FindAssets("t:Texture", new[] { "Assets" }).Select(AssetDatabase.GUIDToAssetPath).Distinct().ToArray();
                string[] sounds = assets.Where(p => AssetImporter.GetAtPath(p) is AudioImporter).ToArray();
                string[] models = assets.Where(p => AssetImporter.GetAtPath(p) is ModelImporter).ToArray();
                int startCap = avatar ? ParelQuestConverter.AvatarTextureCap : ParelQuestConverter.WorldTextureCap;
                Squeeze[] steps = new[] { new Squeeze(startCap, TextureImporterFormat.Automatic, false) }.Concat(Steps.Where(s => s.cap <= startCap)).ToArray();
                for (int i = 0; ; i++)
                {
                    Squeeze step = steps[i];
                    report.texturesCompressed = 0;
                    report.texturesDownsized = 0;
                    Step(i == 0 ? "Compressing textures for Android" : $"Over the {job.bundleLimitBytes / (1024 * 1024)} MB limit: textures down to {step.cap} px");
                    if (step.format == TextureImporterFormat.Automatic) ParelQuestConverter.ConvertTextures(textures, step.cap, report);
                    else ShrinkTextures(textures, step.cap, step.format, report);
                    if (step.squeezeMeshesAndSound && !steps[i - 1].squeezeMeshesAndSound) SqueezeMeshesAndSound(models, sounds, report);
                    Step("Building the Android bundle (shaders compile for Vulkan)");
                    string bundle = Build(avatar, avatar ? new[] { hints.mainAsset } : scenes.ToArray(), job.outputDir);
                    long size = new FileInfo(bundle).Length;
                    Debug.Log($"[ParelVR SDK] Android bundle: {size} bytes (textures up to {step.cap} px, {step.format}).");
                    if (job.bundleLimitBytes <= 0 || size <= job.bundleLimitBytes)
                    {
                        result.bundlePath = bundle;
                        result.bundleBytes = size;
                        if (i > 0) report.Note($"compressed to fit {job.bundleLimitBytes / (1024 * 1024)} MB: textures up to {step.cap} px");
                        break;
                    }
                    if (i == steps.Length - 1)
                        throw new Exception($"The Meta / Standalone VR version is {size / (1024f * 1024f):0.0} MB even fully compressed (textures at {step.cap} px, mono sound, " +
                                            $"compressed meshes); the limit is {job.bundleLimitBytes / (1024 * 1024)} MB. Use fewer or smaller meshes and sounds. The PC version is not affected.");
                }

                result.triangleCount = (int)Math.Min(int.MaxValue, report.triangles);
                result.materialCount = report.materialSlots;
                result.skinnedMeshCount = report.skinnedMeshes;
                result.report = report;
                result.summary = report.Summary();
                result.success = true;
                exitCode = 0;
            }
            catch (Exception ex)
            {
                result.error = ex.Message;
                Debug.LogException(ex);
            }
            finally
            {
                Application.logMessageReceivedThreaded -= Capture;
            }

            lock (Logs) result.logs = Logs.ToList();
            result.seconds = timer.Elapsed.TotalSeconds;
            try
            {
                if (!string.IsNullOrEmpty(resultPath)) File.WriteAllText(resultPath, JsonUtility.ToJson(result, true));
            }
            catch (Exception ex)
            {
                Debug.LogException(ex);
                exitCode = 1;
            }
            EditorApplication.Exit(exitCode);
        }

        /// <summary>Every warning and error, as the Console of a project switched to Android would show it.</summary>
        private static void Capture(string message, string stackTrace, LogType type)
        {
            if (type == LogType.Log || string.IsNullOrEmpty(message)) return;
            // The copy has no scripts of the project: bundles carry no code, the SDK's checks list those components.
            if (message.IndexOf("referenced script", StringComparison.OrdinalIgnoreCase) >= 0 && message.IndexOf("missing", StringComparison.OrdinalIgnoreCase) >= 0) return;
            ParelQuestLogEntry parsed = ParelQuestLocalBuild.ParseLogLine(message.Split('\n')[0]);
            var entry = new ParelQuestLogEntry
            {
                type = type == LogType.Warning ? "warning" : "error",
                message = message.Length > 1200 ? message.Substring(0, 1200) + "..." : message,
                source = parsed?.source ?? (message.IndexOf("import", StringComparison.OrdinalIgnoreCase) >= 0 ? "asset" : "build"),
                shader = parsed?.shader,
            };
            lock (Logs)
            {
                if (Logs.Count >= MaxLogs || !Seen.Add(entry.type + "|" + entry.message)) return;
                Logs.Add(entry);
            }
        }

        private static string Build(bool avatar, string[] assetNames, string outputDir)
        {
            string workDir = Path.Combine(ProjectRoot, "Temp", "ParelQuestBuild");
            if (Directory.Exists(workDir)) Directory.Delete(workDir, true);
            Directory.CreateDirectory(workDir);

            // Same names and options as the SDK's PC builds (ParelAvatarBuilder / WorldBuilder).
            string bundleName = avatar ? "avatar_" + Guid.NewGuid().ToString("N") + ".bundle" : "worldbundle";
            BuildAssetBundleOptions options = avatar
                ? BuildAssetBundleOptions.ChunkBasedCompression | BuildAssetBundleOptions.ForceRebuildAssetBundle | BuildAssetBundleOptions.StrictMode
                : BuildAssetBundleOptions.ForceRebuildAssetBundle;
            AssetBundleManifest manifest = BuildPipeline.BuildAssetBundles(workDir,
                new[] { new AssetBundleBuild { assetBundleName = bundleName, assetNames = assetNames } },
                options, BuildTarget.Android);
            string built = Path.Combine(workDir, bundleName);
            if (manifest == null || !File.Exists(built)) throw new Exception("Unity couldn't build the Android bundle. The errors above say why.");

            Directory.CreateDirectory(outputDir);
            string output = Path.Combine(outputDir, avatar ? "android-avatar.bundle" : "android-worldbundle");
            if (File.Exists(output)) File.Delete(output);
            File.Copy(built, output);
            return output;
        }

        /// <summary>Like <see cref="ParelQuestConverter.ConvertTextures"/> with a fixed, coarser ASTC block. Lightmaps keep Automatic so their HDR format survives.</summary>
        private static void ShrinkTextures(IEnumerable<string> texturePaths, int cap, TextureImporterFormat format, ParelQuestReport report)
        {
            report.textureCap = cap;
            AssetDatabase.StartAssetEditing();
            try
            {
                foreach (string path in texturePaths)
                {
                    if (!(AssetImporter.GetAtPath(path) is TextureImporter importer)) continue;
                    TextureImporterPlatformSettings android = importer.GetPlatformTextureSettings("Android");
                    int before = android.overridden ? android.maxTextureSize : importer.maxTextureSize;
                    int size = Math.Min(before, importer.textureShape == TextureImporterShape.TextureCube ? Math.Min(cap, 1024) : cap);
                    bool lightmap = importer.textureType == TextureImporterType.Lightmap || importer.textureType == TextureImporterType.DirectionalLightmap
                                    || importer.textureType == TextureImporterType.Shadowmask;
                    android.overridden = true;
                    android.maxTextureSize = size;
                    android.format = lightmap ? TextureImporterFormat.Automatic : format;
                    android.textureCompression = TextureImporterCompression.Compressed;
                    android.crunchedCompression = false;
                    importer.SetPlatformTextureSettings(android);
                    importer.SaveAndReimport();
                    report.texturesCompressed++;
                    if (size < before) report.texturesDownsized++;
                }
            }
            finally
            {
                AssetDatabase.StopAssetEditing();
            }
        }

        /// <summary>Mono 22 kHz sound at lower Vorbis quality, and high mesh compression on imported models.</summary>
        private static void SqueezeMeshesAndSound(IEnumerable<string> models, IEnumerable<string> sounds, ParelQuestReport report)
        {
            AssetDatabase.StartAssetEditing();
            try
            {
                foreach (string path in sounds)
                {
                    if (!(AssetImporter.GetAtPath(path) is AudioImporter importer)) continue;
                    AudioImporterSampleSettings android = importer.GetOverrideSampleSettings("Android");
                    android.compressionFormat = AudioCompressionFormat.Vorbis;
                    android.quality = Mathf.Min(android.quality, 0.25f);
                    android.sampleRateSetting = AudioSampleRateSetting.OverrideSampleRate;
                    android.sampleRateOverride = 22050;
                    importer.forceToMono = true;
                    importer.SetOverrideSampleSettings("Android", android);
                    importer.SaveAndReimport();
                }
                foreach (string path in models)
                {
                    if (!(AssetImporter.GetAtPath(path) is ModelImporter importer) || importer.meshCompression == ModelImporterMeshCompression.High) continue;
                    importer.meshCompression = ModelImporterMeshCompression.High;
                    importer.SaveAndReimport();
                }
            }
            finally
            {
                AssetDatabase.StopAssetEditing();
            }
            report.Note("meshes and sound squeezed to fit the size limit");
        }

        /// <summary>The game's Android settings, so what is built here renders the same as the game: Linear, Vulkan, ASTC, DXT5nm normals.</summary>
        private static void ApplyAndroidSettings()
        {
            if (PlayerSettings.colorSpace != ColorSpace.Linear) PlayerSettings.colorSpace = ColorSpace.Linear;
            PlayerSettings.SetUseDefaultGraphicsAPIs(BuildTarget.Android, false);
            // Vulkan only: every Quest (2, 3, 3S, Pro) runs the game on Vulkan, and compiling each shader a second time for
            // OpenGL ES doubled the longest step and kept the CPU at 100% (11-21).
            PlayerSettings.SetGraphicsAPIs(BuildTarget.Android, new[] { GraphicsDeviceType.Vulkan });
            PlayerSettings.SetNormalMapEncoding(NamedBuildTarget.Android, NormalMapEncoding.DXT5nm);
            EditorUserBuildSettings.androidBuildSubtarget = MobileTextureSubtarget.ASTC;
            // Normally set on the command line already (ParelQuestLocalBuild.RunAsync); kept here so they hold whatever
            // started this Unity. They live in this background project's Library only.
            if (EditorUserBuildSettings.overrideMaxTextureSize != ParelQuestConverter.ImportSizeCap)
                EditorUserBuildSettings.overrideMaxTextureSize = ParelQuestConverter.ImportSizeCap;
            if (EditorUserBuildSettings.overrideTextureCompression != OverrideTextureCompression.ForceFastCompressor)
                EditorUserBuildSettings.overrideTextureCompression = OverrideTextureCompression.ForceFastCompressor;

            var player = new SerializedObject(Unsupported.GetSerializedAssetInterfaceSingleton("PlayerSettings"));
            SetGroupValue(player.FindProperty("m_BuildTargetGroupLightmapEncodingQuality"), "Android", "m_EncodingQuality", 1);
            player.ApplyModifiedPropertiesWithoutUndo();

            // Keep every lightmap, fog and instancing variant: a bundle's shaders can't know which of them a world needs.
            var graphics = new SerializedObject(GraphicsSettings.GetGraphicsSettings());
            foreach (string name in new[] { "m_LightmapStripping", "m_FogStripping" }) SetInt(graphics, name, 1);
            SetInt(graphics, "m_InstancingStripping", 2);
            foreach (string name in new[] { "m_LightmapKeepPlain", "m_LightmapKeepDirCombined", "m_LightmapKeepDynamicPlain",
                         "m_LightmapKeepDynamicDirCombined", "m_LightmapKeepShadowMask", "m_LightmapKeepSubtractive",
                         "m_FogKeepLinear", "m_FogKeepExp", "m_FogKeepExp2" })
                SetInt(graphics, name, 1);
            graphics.ApplyModifiedPropertiesWithoutUndo();
            AssetDatabase.SaveAssets();
        }

        /// <summary>
        /// The game's own headset URP asset, copied in by ParelQuestLocalBuild for URP projects, becomes the only pipeline
        /// asset here (default and every quality level): a headset always draws with it, so its features are the only URP
        /// variants the bundle needs.
        /// </summary>
        private static void UseHeadsetPipeline()
        {
            var asset = AssetDatabase.LoadAssetAtPath<RenderPipelineAsset>(ParelQuestLocalBuild.HeadsetPipelineAsset);
            if (asset == null) return;
            GraphicsSettings.defaultRenderPipeline = asset;
            UnityEngine.Object[] quality = AssetDatabase.LoadAllAssetsAtPath("ProjectSettings/QualitySettings.asset");
            if (quality != null && quality.Length > 0 && quality[0] != null)
            {
                var settings = new SerializedObject(quality[0]);
                SerializedProperty levels = settings.FindProperty("m_QualitySettings");
                if (levels != null && levels.isArray)
                    for (int i = 0; i < levels.arraySize; i++)
                    {
                        SerializedProperty pipeline = levels.GetArrayElementAtIndex(i).FindPropertyRelative("customRenderPipeline");
                        if (pipeline != null) pipeline.objectReferenceValue = asset;
                    }
                settings.ApplyModifiedPropertiesWithoutUndo();
            }
            AssetDatabase.SaveAssets();
            Debug.Log("[ParelVR SDK] Shaders compile for the game's headset URP asset only.");
        }

        /// <summary>What the bundle's scenes use of baked light, fog and GPU instancing.</summary>
        private sealed class ContentUse
        {
            public bool plainLightmaps, directionalLightmaps, realtimeGI, shadowmask, subtractive, instancing;
            public readonly HashSet<FogMode> fog = new HashSet<FogMode>();

            public void Note(Scene scene)
            {
                LightmapData[] maps = LightmapSettings.lightmaps;
                if (maps != null && maps.Length > 0)
                {
                    if (LightmapSettings.lightmapsMode == LightmapsMode.CombinedDirectional) directionalLightmaps = true;
                    else plainLightmaps = true;
                }
                else if (Lightmapping.lightingDataAsset != null)
                {
                    // Baked lighting data but no lightmaps read back: keep both kinds rather than draw it unlit.
                    directionalLightmaps = plainLightmaps = true;
                }
                if (Lightmapping.TryGetLightingSettings(out LightingSettings lighting) && lighting != null)
                {
                    if (lighting.realtimeGI) realtimeGI = true;
                    if (lighting.bakedGI && lighting.mixedBakeMode == MixedLightingMode.Shadowmask) shadowmask = true;
                    if (lighting.bakedGI && lighting.mixedBakeMode == MixedLightingMode.Subtractive) subtractive = true;
                }
                else
                {
                    shadowmask = subtractive = true; // can't tell: keep both
                }
                if (RenderSettings.fog) fog.Add(RenderSettings.fogMode);
                foreach (GameObject root in scene.GetRootGameObjects())
                {
                    foreach (Terrain terrain in root.GetComponentsInChildren<Terrain>(true))
                        if (terrain.drawInstanced) instancing = true;
                    foreach (ParticleSystemRenderer particles in root.GetComponentsInChildren<ParticleSystemRenderer>(true))
                        if (particles.renderMode == ParticleSystemRenderMode.Mesh && particles.enableGPUInstancing) instancing = true;
                }
            }
        }

        /// <summary>
        /// Shader variants for what the content uses, as Unity's own automatic stripping does for a game's scenes (it can't
        /// see a bundle's): every lightmap, fog and instancing variant was kept, multiplying what URP compiled (11-21).
        /// Worlds keep the lightmap modes their scenes were baked with, their fog mode and exponential-squared fog (the
        /// SDK's Weather and Skybox Cycle turn that on), and instancing variants only for instanced materials, or for
        /// everything when a terrain or mesh particles draw instanced. Avatars are never lightmapped: they keep the
        /// shadowmask / subtractive variants (lit in any world), every fog mode, and instanced materials.
        /// </summary>
        private static void KeepOnlyUsedVariants(bool avatar, ContentUse use, ParelQuestReport report)
        {
            int On(bool keep) => keep ? 1 : 0;
            bool realtime = !avatar && use.realtimeGI;
            var graphics = new SerializedObject(GraphicsSettings.GetGraphicsSettings());
            SetInt(graphics, "m_LightmapStripping", 1);
            SetInt(graphics, "m_FogStripping", 1);
            SetInt(graphics, "m_LightmapKeepPlain", On(!avatar && use.plainLightmaps));
            SetInt(graphics, "m_LightmapKeepDirCombined", On(!avatar && use.directionalLightmaps));
            SetInt(graphics, "m_LightmapKeepDynamicPlain", On(realtime));
            SetInt(graphics, "m_LightmapKeepDynamicDirCombined", On(realtime));
            SetInt(graphics, "m_LightmapKeepShadowMask", On(avatar || use.shadowmask));
            SetInt(graphics, "m_LightmapKeepSubtractive", On(avatar || use.subtractive));
            SetInt(graphics, "m_FogKeepLinear", On(avatar || use.fog.Contains(FogMode.Linear)));
            SetInt(graphics, "m_FogKeepExp", On(avatar || use.fog.Contains(FogMode.Exponential)));
            SetInt(graphics, "m_FogKeepExp2", 1);
            // 0 = only for materials with GPU instancing on, 2 = all.
            SetInt(graphics, "m_InstancingStripping", !avatar && use.instancing ? 2 : 0);
            graphics.ApplyModifiedPropertiesWithoutUndo();
            AssetDatabase.SaveAssets();
            if (!avatar)
                Debug.Log("[ParelVR SDK] Shader variants kept: lightmaps " +
                          (use.directionalLightmaps ? "directional " : "") + (use.plainLightmaps ? "plain " : "") + (use.realtimeGI ? "realtime " : "") +
                          (use.shadowmask ? "shadowmask " : "") + (use.subtractive ? "subtractive " : "") +
                          "| fog " + string.Join(" ", use.fog.Select(f => f.ToString()).Concat(new[] { "ExponentialSquared" }).Distinct()) +
                          "| instancing " + (use.instancing ? "all" : "instanced materials only") + ".");
        }

        private static void SetInt(SerializedObject target, string name, int value)
        {
            SerializedProperty property = target.FindProperty(name);
            if (property != null) property.intValue = value;
        }

        private static void SetGroupValue(SerializedProperty list, string group, string field, int value)
        {
            if (list == null || !list.isArray) return;
            for (int i = 0; i < list.arraySize; i++)
            {
                SerializedProperty item = list.GetArrayElementAtIndex(i);
                if (item.FindPropertyRelative("m_BuildTarget")?.stringValue != group) continue;
                SerializedProperty valueProperty = item.FindPropertyRelative(field);
                if (valueProperty != null) valueProperty.intValue = value;
                return;
            }
        }

        private static string ProjectRoot => Path.GetFullPath(Path.Combine(Application.dataPath, ".."));

        private static string Argument(string name)
        {
            string[] args = Environment.GetCommandLineArgs();
            for (int i = 0; i < args.Length - 1; i++)
                if (args[i] == name) return args[i + 1];
            return null;
        }
    }
}

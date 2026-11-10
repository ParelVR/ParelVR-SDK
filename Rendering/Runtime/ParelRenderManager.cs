using UnityEngine;

namespace ParelVR.Rendering
{
    /// <summary>
    /// Entry point to PVRP (ParelVR Rendering). It holds the shared classifier and mapper registry and binds PVRP's log
    /// to Unity's console. Nothing here runs on its own: game code, the SDK and the Editor tools call in, so adding
    /// the framework changes no existing behaviour.
    /// </summary>
    public static class ParelRenderManager
    {
        public static ShaderClassifier Classifier { get; } = new ShaderClassifier();
        public static MaterialMapperRegistry Mappers { get; } = MaterialConversionPlanner.CreateDefaultRegistry();

        public static bool IsInitialized { get; private set; }

        /// <summary>
        /// The target this build renders for. Android builds (and the Editor with Android selected) count as Quest, so
        /// Quest behaviour can be checked in the Editor.
        /// </summary>
        public static TargetPlatform CurrentTarget
        {
            get
            {
#if UNITY_ANDROID
                return TargetPlatform.Quest;
#else
                return TargetPlatform.Pc;
#endif
            }
        }

        /// <summary>Safe to call more than once.</summary>
        public static void Initialize()
        {
            if (IsInitialized) return;
            IsInitialized = true;
            if (RenderingLog.Sink == null) RenderingLog.Sink = LogToUnity;
        }

        public static ShaderClassification Classify(Shader shader) => Classify(shader, CurrentTarget);

        public static ShaderClassification Classify(Shader shader, TargetPlatform target) =>
            Classifier.Classify(UnityShaderInspector.Inspect(shader), target);

        public static ShaderClassification Classify(Material material) => Classify(material, CurrentTarget);

        public static ShaderClassification Classify(Material material, TargetPlatform target) =>
            Classifier.Classify(UnityShaderInspector.Inspect(material), target);

        /// <summary>Call when content is unloaded (a world closes) so cached shader data doesn't outlive the shaders.</summary>
        public static void ClearCaches()
        {
            UnityShaderInspector.ClearCache();
        }

        private static void LogToUnity(DiagnosticSeverity severity, string message)
        {
            switch (severity)
            {
                case DiagnosticSeverity.Error: Debug.LogError(message); break;
                case DiagnosticSeverity.Warning: Debug.LogWarning(message); break;
                default: Debug.Log(message); break;
            }
        }
    }
}

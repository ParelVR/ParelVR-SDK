using UnityEditor;
using UnityEngine;

namespace ParelVR.SDK.Build
{
    internal static class TestBuilderMenu
    {
        [MenuItem("ParelVR SDK/Test Build World (Fase 4)", priority = 50)]
        private static void TestBuildWorld()
        {
            try
            {
                string path = WorldBuilder.BuildWorldBundle();
                Debug.Log($"[Fase 4 Test] World built successfully at: {path}");
                EditorUtility.RevealInFinder(path);
            }
            catch (System.Exception ex)
            {
                Debug.LogError($"[Fase 4 Test] Error building world: {ex.Message}");
            }
        }
    }
}

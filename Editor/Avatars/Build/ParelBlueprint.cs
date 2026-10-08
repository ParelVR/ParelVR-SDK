using ParelVR.AvatarSDK;
using UnityEditor;
using UnityEditor.SceneManagement;

namespace ParelVR.SDK.Avatars.Build
{
    /// <summary>
    /// Reads and writes an avatar's Blueprint ID on its Pipeline Manager (adding one when missing),
    /// with Undo, prefab-override recording and scene dirtying handled in one place.
    /// </summary>
    public static class ParelBlueprint
    {
        /// <summary>The descriptor's Pipeline Manager, added (and given any legacy Blueprint ID) if it doesn't have one.</summary>
        public static ParelPipelineManager EnsurePipelineManager(ParelAvatarDescriptor descriptor)
        {
            if (descriptor == null) return null;
            ParelPipelineManager pipeline = descriptor.GetComponent<ParelPipelineManager>();
            if (pipeline != null) return pipeline;

            string legacyId = descriptor.BlueprintId;
            pipeline = Undo.AddComponent<ParelPipelineManager>(descriptor.gameObject);
            pipeline.contentType = ParelPipelineManager.ContentType.Avatar;
            pipeline.blueprintId = legacyId ?? string.Empty;
            EditorUtility.SetDirty(pipeline);
            return pipeline;
        }

        public static void Assign(ParelAvatarDescriptor descriptor, string blueprintId, string undoName)
        {
            if (descriptor == null) return;
            ParelPipelineManager pipeline = EnsurePipelineManager(descriptor);
            Undo.RecordObject(descriptor, undoName);
            if (pipeline != null) Undo.RecordObject(pipeline, undoName);

            descriptor.BlueprintId = blueprintId ?? string.Empty;
            if (pipeline != null)
            {
                pipeline.completedSDKPipeline = !string.IsNullOrEmpty(blueprintId);
                EditorUtility.SetDirty(pipeline);
                PrefabUtility.RecordPrefabInstancePropertyModifications(pipeline);
            }
            EditorUtility.SetDirty(descriptor);
            PrefabUtility.RecordPrefabInstancePropertyModifications(descriptor);
            if (descriptor.gameObject.scene.IsValid()) EditorSceneManager.MarkSceneDirty(descriptor.gameObject.scene);
        }

        public static void Detach(ParelAvatarDescriptor descriptor) => Assign(descriptor, string.Empty, "Detach Blueprint ID");
    }
}

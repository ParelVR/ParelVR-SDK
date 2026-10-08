using UnityEngine;

namespace ParelVR.AvatarSDK
{
    /// <summary>
    /// Holds the content's Blueprint ID -- the counterpart of VRChat's Pipeline Manager. Added next to
    /// the Avatar Descriptor automatically. The first Build &amp; Publish assigns the ID; every publish
    /// after that updates the same avatar. Detach clears it so the next publish creates a new one.
    /// </summary>
    [DisallowMultipleComponent]
    [AddComponentMenu("ParelVR/Pipeline Manager")]
    public sealed class ParelPipelineManager : MonoBehaviour
    {
        public enum ContentType
        {
            Avatar = 0,
            World = 1,
        }

        [Tooltip("Assigned the first time this content is published. Publishing again with the same ID updates it.")]
        public string blueprintId = string.Empty;

        public ContentType contentType = ContentType.Avatar;

        /// <summary>True once a publish through the SDK finished with this ID.</summary>
        public bool completedSDKPipeline;

        public bool HasBlueprint => !string.IsNullOrEmpty(blueprintId);

        /// <summary>Forgets the Blueprint ID: the next publish creates new content instead of updating this one.</summary>
        public void Detach()
        {
            blueprintId = string.Empty;
            completedSDKPipeline = false;
        }
    }
}

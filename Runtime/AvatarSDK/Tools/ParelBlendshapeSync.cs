using System.Collections.Generic;
using UnityEngine;

namespace ParelVR.AvatarSDK
{
    /// <summary>
    /// Keeps this mesh's blend shapes matching another's -- e.g. a shirt following the body's chest
    /// size. Copies the current values, and every animation of the source shapes in the FX layer.
    /// </summary>
    [AddComponentMenu("ParelVR/Avatar Tools/Blend Shape Sync")]
    public sealed class ParelBlendshapeSync : MonoBehaviour, IParelEditorOnly
    {
        [Tooltip("The mesh to follow (usually the body).")]
        public SkinnedMeshRenderer source;

        [Tooltip("The mesh that follows. Empty = the Skinned Mesh Renderer on this object.")]
        public SkinnedMeshRenderer target;

        [Tooltip("Follow every blend shape both meshes have with the same name.")]
        public bool matchAllByName = true;

        [Tooltip("Extra pairs, for shapes named differently on the two meshes.")]
        public List<ParelBlendShapeLink> links = new List<ParelBlendShapeLink>();

        public SkinnedMeshRenderer Target => target != null ? target : GetComponent<SkinnedMeshRenderer>();
    }
}

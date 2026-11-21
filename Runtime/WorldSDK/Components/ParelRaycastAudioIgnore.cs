using UnityEngine;

namespace ParelVR.WorldSDK
{
    /// <summary>
    /// 3D Audio Raycast Ignore: the sounds on this object and its children stay exactly as you made them, even inside a
    /// 3D Audio Raycast Zone (background music, a 2D announcer, a sound you process yourself).
    /// </summary>
    [AddComponentMenu("ParelVR/World SDK/3D Audio Raycast Ignore")]
    [DisallowMultipleComponent]
    public sealed class ParelRaycastAudioIgnore : MonoBehaviour
    {
    }
}

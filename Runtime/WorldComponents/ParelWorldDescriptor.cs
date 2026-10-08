using System.Collections.Generic;
using UnityEngine;

namespace ParelVR.SDK.Worlds.Components
{
    /// <summary>
    /// Place this component on a GameObject in your scene to mark it as a ParelVR World.
    /// The SDK will detect it automatically and allow you to publish the scene.
    /// The blueprintId is assigned on first publish and saved with the scene so that
    /// future builds update the same world instead of creating a new one.
    /// </summary>
    [DisallowMultipleComponent]
    [AddComponentMenu("ParelVR/World Descriptor")]
    public class ParelWorldDescriptor : MonoBehaviour
    {
        [HideInInspector]
        public string blueprintId;

        // Edited in the ParelVR SDK window (Credits and Monetization tabs), sent when the world is published.
        [HideInInspector]
        public List<ParelWorldCreditRole> credits = new List<ParelWorldCreditRole>();

        [HideInInspector]
        public ParelWorldShopTheme shopTheme = new ParelWorldShopTheme();

        [HideInInspector]
        public ParelWorldMonetization monetization = new ParelWorldMonetization();
    }
}

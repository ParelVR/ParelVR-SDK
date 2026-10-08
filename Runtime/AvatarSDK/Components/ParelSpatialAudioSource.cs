using UnityEngine;

namespace ParelVR.AvatarSDK
{
    /// <summary>
    /// VRChat's Spatial Audio Source: how an AudioSource on the avatar sounds to other people --
    /// gain, falloff distances and whether it's positional. ParelVR caps these so nobody can blast a
    /// whole instance.
    /// </summary>
    [RequireComponent(typeof(AudioSource))]
    [AddComponentMenu("ParelVR/Avatar SDK/Spatial Audio Source")]
    public sealed class ParelSpatialAudioSource : MonoBehaviour
    {
        public const float MaxGainDb = 10f;
        public const float MaxFar = 40f;

        [Range(-24f, MaxGainDb), Tooltip("Volume boost in decibels.")]
        public float gain = 0f;
        [Tooltip("Where the sound becomes inaudible (meters).")]
        public float far = 40f;
        [Tooltip("Where the sound starts getting quieter (meters).")]
        public float near;
        [Tooltip("Size of the sound source (meters); sounds come from a volume rather than a point.")]
        public float volumetricRadius;
        [Tooltip("Positional (3D) audio. Off = everyone hears it at the same volume.")]
        public bool enableSpatialization = true;
        [Tooltip("Use the AudioSource's own volume curve instead of ParelVR's.")]
        public bool useAudioSourceVolumeCurve;

        /// <summary>Applies these settings (clamped to ParelVR's limits) to the AudioSource.</summary>
        public void Apply()
        {
            AudioSource source = GetComponent<AudioSource>();
            if (source == null) return;

            float clampedFar = Mathf.Clamp(far, 0.1f, MaxFar);
            float clampedNear = Mathf.Clamp(near, 0f, clampedFar);
            source.spatialBlend = enableSpatialization ? 1f : 0f;
            source.minDistance = Mathf.Max(0.01f, Mathf.Max(clampedNear, volumetricRadius));
            source.maxDistance = clampedFar;
            if (!useAudioSourceVolumeCurve) source.rolloffMode = AudioRolloffMode.Logarithmic;
            float linear = Mathf.Pow(10f, Mathf.Clamp(gain, -24f, MaxGainDb) / 20f);
            source.volume = Mathf.Clamp01(source.volume * Mathf.Min(linear, 1f));
            source.spread = volumetricRadius > 0f ? Mathf.Clamp(volumetricRadius * 45f, 0f, 180f) : source.spread;
        }
    }
}

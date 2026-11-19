using UnityEngine;

namespace ParelVR.WorldSDK
{
    /// <summary>
    /// 3D Audio Raycast, Single Object: a sound that comes from one object (a radio, a fire, a speaker, a machine).
    /// Add it next to the object's AudioSource. The sound plays through the headset's 3D audio, walls, floors and props
    /// between the listener and the object muffle it and turn it down (rays go to the middle of the object and to four
    /// points Sound Size out from it, so it clears bit by bit as you step round a corner), it loses its highs far away,
    /// and it echoes as much as the room it is in does. The object's own colliders never block it.
    /// </summary>
    [RequireComponent(typeof(AudioSource))]
    [AddComponentMenu("ParelVR/World SDK/3D Audio Raycast Single Object")]
    [DisallowMultipleComponent]
    public sealed class ParelRaycastAudioObject : ParelRaycastAudio
    {
        [Header("Object")]
        [Tooltip("How big the sound is (meters). The muffling rays go to points this far round the object's middle, so a big " +
                 "sound peeking out past a wall is only half muffled.")]
        [Min(0f)] public float soundSize = 0.25f;

        private AudioSource _own;

        public override AudioSource Sound => _own != null ? _own : _own = GetComponent<AudioSource>();

        protected override int RayTargets(Vector3 ear, Vector3[] targets)
        {
            Vector3 centre = transform.position;
            targets[0] = centre;
            if (soundSize <= 0.001f) return 1;
            Around(ear, centre, soundSize, targets, 1);
            return 5;
        }

#if UNITY_EDITOR
        private void OnDrawGizmosSelected()
        {
            Gizmos.color = new Color(0.35f, 0.8f, 1f, 0.9f);
            Gizmos.DrawWireSphere(transform.position, Mathf.Max(0.05f, soundSize));
            AudioSource source = Sound;
            if (source != null)
            {
                Gizmos.color = new Color(0.35f, 0.8f, 1f, 0.25f);
                Gizmos.DrawWireSphere(transform.position, source.maxDistance);
            }

            DrawHearing();
        }
#endif
    }
}

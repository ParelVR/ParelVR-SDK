using UnityEngine;

namespace ParelVR.WorldSDK
{
    /// <summary>
    /// 3D Audio Raycast, Single Object: a sound that comes from one object (a radio, a fire, a speaker, a machine).
    /// Add it next to the object's AudioSource. Walls and props between you and it let only some of it through by what
    /// they are made of, it finds its way round corners and through doorways and is heard from there, it reflects off
    /// the walls, floor and ceiling round you, it fills the room with that room's echo, and it loses its highs far
    /// away. The object's own colliders never block it. A Single Object inside a 3D Audio Raycast Zone uses its own
    /// settings instead of the zone's.
    /// </summary>
    [RequireComponent(typeof(AudioSource))]
    [AddComponentMenu("ParelVR/World SDK/3D Audio Raycast Single Object")]
    [DisallowMultipleComponent]
    public sealed class ParelRaycastAudioObject : ParelRaycastAudio
    {
        [Header("Object")]
        [Tooltip("How big the sound is (meters). The muffling rays spread over this size, so a big sound peeking out " +
                 "past a wall is only partly muffled, and up close it sounds as big as this.")]
        [Min(0f)] public float soundSize = 0.25f;

        private AudioSource _own;
        private ParelRaycastAudioVoice _voice;

        /// <summary>The AudioSource this drives.</summary>
        public AudioSource Sound => _own != null ? _own : _own = GetComponent<AudioSource>();

        public override float Occlusion => _voice != null ? _voice.Occlusion : 0f;

        internal override void Begin()
        {
            _voice = ParelRaycastAudioEngine.Attach(this, Sound, soundSize, false);
        }

        internal override void Prepare(Vector3 ear, float dt)
        {
            if (_voice == null) Begin();
            else _voice.Radius = soundSize;
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

            ParelRaycastAudioZone.DrawHearing(_voice);
        }
#endif
    }
}

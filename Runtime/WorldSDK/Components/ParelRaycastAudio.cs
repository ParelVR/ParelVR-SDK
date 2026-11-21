using System;
using UnityEngine;

namespace ParelVR.WorldSDK
{
    /// <summary>
    /// What the two 3D Audio Raycast components share. Every sound they drive is worked out from the world's real
    /// geometry, again and again as you move:
    /// <list type="bullet">
    /// <item>It plays through the headset's 3D audio (the same as players' voices).</item>
    /// <item>A spread of rays from your ears to the sound (it has a size, so it clears bit by bit as you step round a
    /// corner) finds every wall, floor and prop in between. Each one lets some of the sound through, more of the lows
    /// than the highs, by what it is made of (see 3D Audio Raycast Material): behind brick you hear a dull thump,
    /// behind a curtain nearly all of it.</item>
    /// <item>When the sound is blocked, rays look for the way round: through a doorway, an open window, over a wall,
    /// round two corners. That path is heard from its direction, later by the extra distance, and duller the sharper
    /// it bends, so a radio in the next room comes out of the doorway and clears as you walk up to it.</item>
    /// <item>Rays round you find the walls, floor and ceiling. Each one sends you a real reflection of the sound (from
    /// the right direction, at the right delay, softer off soft materials) and together they set the room's echo
    /// tail: a small tiled room rings short and bright, a big hall long, a carpeted room is dead, outdoors is dry.</item>
    /// <item>Far sounds lose their highs the way they do in the air.</item>
    /// </list>
    /// The AudioSource's own volume, Min/Max Distance and rolloff still decide how loud it is and how it fades, and
    /// World Menu sliders and Volt scripts can change them as usual: all of this works on the sound itself, never on
    /// those settings.
    /// </summary>
    public abstract class ParelRaycastAudio : MonoBehaviour
    {
        // Ignore Raycast (2), UI (5), the local and remote players' avatars (6, 7), the handheld camera (8), the
        // menus (9) and avatar pedestals (10).
        public const int DefaultOccluderMask = ~((1 << 2) | (1 << 5) | (1 << 6) | (1 << 7) | (1 << 8) | (1 << 9) | (1 << 10));

        /// <summary>The game leaves these sounds alone in every zone (players' voices, portals). Null = none.</summary>
        public static Func<AudioSource, bool> SkipSound;

        [Header("3D audio")]
        [Tooltip("Play through the headset's 3D audio, the same as players' voices. Off = Unity's plain left/right panning.")]
        public bool headset3DAudio = true;

        [Header("Walls")]
        [Tooltip("Walls, floors and props between you and the sound let only some of it through, more of the lows than the highs.")]
        public bool muffleThroughWalls = true;
        [Tooltip("What blocks the sound. Players, UI and Ignore Raycast are left out. Trigger colliders never block it.")]
        public LayerMask occluders = DefaultOccluderMask;
        [Tooltip("How much walls block: 1 = like real life, 0.5 = half as much, 2 = like walls twice as thick.")]
        [Range(0f, 2f)] public float wallStrength = 1f;
        [Tooltip("When the sound is blocked, it finds its way round: through doorways and open windows, over walls and " +
                 "round corners, and is heard from there.")]
        public bool aroundCorners = true;
        [Tooltip("Seconds it takes to change when a wall comes between or goes away. Short = snappy, long = soft.")]
        [Range(0.05f, 2f)] public float smoothing = 0.2f;

        [Header("Distance")]
        [Tooltip("Far sounds lose their highs, the way they do in the air.")]
        public bool airAbsorption = true;

        [Header("Room")]
        [Tooltip("Real reflections off the walls, floor and ceiling round you, and the room's echo tail, both measured with rays.")]
        public bool roomEcho = true;
        [Tooltip("How strong the reflections and the echo tail are. 1 = like real life.")]
        [Range(0f, 2f)] public float echoAmount = 1f;

        /// <summary>0 = nothing between the listener and the sound, 1 = fully behind walls with no way round.</summary>
        public abstract float Occlusion { get; }

        protected virtual void OnEnable() => ParelRaycastAudioEngine.Register(this);
        protected virtual void OnDisable() => ParelRaycastAudioEngine.Unregister(this);

        /// <summary>Called by the engine once this component is registered: put voices on its own sounds.</summary>
        internal virtual void Begin() { }

        /// <summary>Called by the engine every frame before the sounds are worked out (a zone moves its area sound).</summary>
        internal virtual void Prepare(Vector3 ear, float dt) { }

        /// <summary>
        /// Fills the points the muffling rays go to for one of this component's sounds; the first is the middle and
        /// counts double. Returns how many (0 = nothing can stand between, e.g. you are inside the area sound).
        /// </summary>
        internal virtual int RayTargets(ParelRaycastAudioVoice voice, Vector3 ear, Vector3[] targets)
        {
            Vector3 centre = voice.Source.transform.position;
            targets[0] = centre;
            float r = voice.Radius;
            if (r <= 0.01f) return 1;
            Ring(ear, centre, r, targets, 1, 6);
            return 7;
        }

        /// <summary><paramref name="count"/> points in a ring round <paramref name="centre"/>, square to the line from the ear.</summary>
        internal static void Ring(Vector3 ear, Vector3 centre, float size, Vector3[] targets, int start, int count)
        {
            Vector3 look = centre - ear;
            if (look.sqrMagnitude < 0.0001f) look = Vector3.forward;
            Vector3 side = Vector3.Cross(look, Vector3.up);
            if (side.sqrMagnitude < 0.0001f) side = Vector3.Cross(look, Vector3.right);
            side = side.normalized;
            Vector3 up = Vector3.Cross(side, look).normalized;
            for (int i = 0; i < count; i++)
            {
                float a = i * (Mathf.PI * 2f / count);
                targets[start + i] = centre + (side * Mathf.Cos(a) + up * Mathf.Sin(a)) * size;
            }
        }
    }
}

using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Events;

namespace ParelVR.AvatarSDK
{
    /// <summary>
    /// A Contact Receiver: reacts when a Contact Sender with a matching tag touches it. One receiver can drive several
    /// parameters at once (touching, taps, proximity, speed, the direction it was touched from, who touched it), so a
    /// headpat or a boop is one component instead of three. Pulses (Entered / Exited) are held long enough that no
    /// animator misses them, and the controller of the hand that touches can buzz. In a world, a receiver calls its
    /// On Enter / On Exit events instead (wire them to a Volt script's method like a UI button).
    /// </summary>
    [AddComponentMenu("ParelVR/Avatar SDK/Contact Receiver")]
    public sealed class ParelContactReceiver : ParelContactBase
    {
        public enum ReceiverType
        {
            /// <summary>Value while touched, 0 otherwise.</summary>
            Constant = 0,
            /// <summary>Value for a moment as contact starts (see Hold).</summary>
            OnEnter = 1,
            /// <summary>0 at the edge to 1 at the centre.</summary>
            Proximity = 2,
        }

        /// <summary>What an extra output reports.</summary>
        public enum OutputKind
        {
            /// <summary>Bool: something touches it.</summary>
            Touching = 0,
            /// <summary>Bool: a touch started (held for Hold seconds).</summary>
            Entered = 1,
            /// <summary>Bool: the last touch ended (held for Hold seconds).</summary>
            Exited = 2,
            /// <summary>Float 0-1: how deep the nearest touch is.</summary>
            Proximity = 3,
            /// <summary>Int: how many senders touch it right now.</summary>
            Count = 4,
            /// <summary>Int: goes up by one every touch (wraps at 255): taps, boops, high fives.</summary>
            Taps = 5,
            /// <summary>Float 0-1: how fast the touch moves (1 = 4 m/s or more).</summary>
            Speed = 6,
            /// <summary>Float -1 to 1: where the touch is, along the receiver's own X (left / right).</summary>
            DirectionX = 7,
            /// <summary>Float -1 to 1: along the receiver's Y (down / up).</summary>
            DirectionY = 8,
            /// <summary>Float -1 to 1: along the receiver's Z (back / front).</summary>
            DirectionZ = 9,
            /// <summary>Bool: the wearer's own avatar touches it.</summary>
            FromSelf = 10,
            /// <summary>Bool: someone else (or the world) touches it.</summary>
            FromOthers = 11,
        }

        [Serializable]
        public sealed class Output
        {
            public OutputKind kind = OutputKind.Touching;
            public string parameter = string.Empty;
        }

        /// <summary>Where the senders may come from.</summary>
        [Flags]
        public enum Sources
        {
            Avatars = 1,
            World = 2,
        }

        public ReceiverType receiverType = ReceiverType.Constant;
        [Tooltip("The avatar parameter this receiver sets (Bool / Int / Float).")]
        public string parameter = string.Empty;
        [Tooltip("What Constant and On Enter set the parameter to while active.")]
        public float value = 1f;
        [Tooltip("More parameters from the same contact: taps, proximity, speed, direction, who touched it.")]
        public List<Output> outputs = new List<Output>();
        [Tooltip("React to senders on this same avatar.")]
        public bool allowSelf = true;
        [Tooltip("React to senders on other avatars and in the world.")]
        public bool allowOthers = true;
        [Tooltip("Which senders count: other avatars, the world's own senders, or both.")]
        public Sources sources = Sources.Avatars | Sources.World;
        [Tooltip("On Enter and Taps: slowest sender speed (m/s) that counts as a touch.")]
        public float minVelocity = 0.05f;
        [Tooltip("Seconds that On Enter, Entered and Exited stay on, so animators and other players never miss them.")]
        public float hold = 0.1f;
        [Tooltip("Seconds over which Proximity, Speed and Direction glide to new values (0 = instantly).")]
        public float smoothing;
        [Tooltip("Box receivers: Proximity measures from the front (+Z) face instead of the centre.")]
        public bool proximityFromFront;
        [Tooltip("The controller of the hand that touches this vibrates, 0 to 1 (the wearer's own hands, in VR).")]
        [Range(0f, 1f)] public float haptics;

        [Tooltip("Worlds only: called when a touch starts. Ignored on avatars.")]
        public UnityEvent onContactEnter = new UnityEvent();
        [Tooltip("Worlds only: called when the last touch ends. Ignored on avatars.")]
        public UnityEvent onContactExit = new UnityEvent();

        [NonSerialized] internal bool WasTouching;
        [NonSerialized] internal float LastValue = -1f;
        [NonSerialized] internal float PulseUntil;
        [NonSerialized] internal float EnteredUntil;
        [NonSerialized] internal float ExitedUntil;
        [NonSerialized] internal int Taps;
        [NonSerialized] internal float SmoothProximity, SmoothSpeed;
        [NonSerialized] internal Vector3 SmoothDirection;
        [NonSerialized] internal float[] LastOutputs;
        [NonSerialized] internal readonly HashSet<ParelContactSender> Touching = new HashSet<ParelContactSender>();
        [NonSerialized] internal readonly HashSet<ParelContactSender> TouchingBefore = new HashSet<ParelContactSender>();

        /// <summary>The value the main parameter has now (for UI and debugging).</summary>
        public float CurrentValue => LastValue < 0f ? 0f : LastValue;
        /// <summary>How many senders touch it now.</summary>
        public int TouchCount => Touching.Count;
        /// <summary>Touches so far (wraps at 255).</summary>
        public int TapCount => Taps;

        private void OnEnable() => ParelContactSystem.Register(this);

        private void OnDisable()
        {
            ParelContactSystem.Unregister(this);
            WasTouching = false;
            LastValue = -1f;
            LastOutputs = null;
            Touching.Clear();
            TouchingBefore.Clear();
        }
    }
}

using UnityEngine;

namespace ParelVR.AvatarSDK
{
    /// <summary>
    /// VRChat's Station: a seat other players (or the wearer) can use -- a chair, a shoulder to ride,
    /// a vehicle. Needs a Collider on the same object so players can point at it and Use it.
    /// </summary>
    [DisallowMultipleComponent]
    [AddComponentMenu("ParelVR/Avatar SDK/Station")]
    public sealed class ParelStation : MonoBehaviour
    {
        public enum Mobility
        {
            /// <summary>The seated player can still walk around (the station follows them).</summary>
            Mobile = 0,
            /// <summary>The seated player can't move or turn.</summary>
            Immobilize = 1,
            /// <summary>Like Immobilize, but movement input still reaches the station (vehicles).</summary>
            ImmobilizeForVehicle = 2,
        }

        [Tooltip("What the seated player can still do.")]
        public Mobility playerMobility = Mobility.Immobilize;

        [Tooltip("Players already sitting in a station may switch straight to this one.")]
        public bool canUseStationFromStation = true;

        [Tooltip("Replaces the seated player's Base / Sitting layer while seated (empty = the default sitting pose).")]
        public RuntimeAnimatorController animatorController;

        [Tooltip("Players can't leave by moving or jumping; only the station (or an animation) lets them out.")]
        public bool disableStationExit;

        [Tooltip("Seated players use the sitting pose. Off = they stand at the enter location.")]
        public bool seated = true;

        [Tooltip("Where the seated player is placed (empty = this object).")]
        public Transform stationEnterPlayerLocation;

        [Tooltip("Where the player is put when they get up (empty = where they were before sitting).")]
        public Transform stationExitPlayerLocation;

        public Transform EnterLocation => stationEnterPlayerLocation != null ? stationEnterPlayerLocation : transform;

        private void OnDrawGizmosSelected()
        {
            Transform enter = EnterLocation;
            Gizmos.color = new Color(0.25f, 0.82f, 0.71f, 0.9f);
            Gizmos.DrawWireCube(enter.position + enter.up * 0.45f, new Vector3(0.45f, 0.9f, 0.45f));
            Gizmos.DrawLine(enter.position, enter.position + enter.forward * 0.4f);
            if (stationExitPlayerLocation != null)
            {
                Gizmos.color = new Color(1f, 0.6f, 0.2f, 0.9f);
                Gizmos.DrawWireSphere(stationExitPlayerLocation.position, 0.15f);
            }
        }
    }
}

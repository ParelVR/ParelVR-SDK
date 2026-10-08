using System;
using System.Collections.Generic;
using UnityEngine;

namespace ParelVR.AvatarSDK
{
    /// <summary>VRChat's playable layers, plus its special Sitting / T-Pose / IK-Pose layers.</summary>
    public enum ParelAvatarLayer
    {
        Base = 0,
        Additive = 1,
        Gesture = 2,
        Action = 3,
        FX = 4,
        Sitting = 5,
        TPose = 6,
        IKPose = 7,
    }

    /// <summary>Layers whose weight Playable Layer Control / Animator Layer Control can blend.</summary>
    public enum ParelBlendableLayer
    {
        Action = 0,
        FX = 1,
        Gesture = 2,
        Additive = 3,
    }

    /// <summary>The avatar's built-in contact colliders (VRChat's descriptor colliders).</summary>
    public enum ParelAvatarColliderSlot
    {
        Head = 0,
        Torso = 1,
        HandL = 2,
        HandR = 3,
        FootL = 4,
        FootR = 5,
        FingerIndexL = 6,
        FingerIndexR = 7,
        FingerMiddleL = 8,
        FingerMiddleR = 9,
        FingerRingL = 10,
        FingerRingR = 11,
        FingerLittleL = 12,
        FingerLittleR = 13,
    }

    /// <summary>Who may collide with / grab / pose a PhysBone (VRChat's True / False / Other).</summary>
    public enum ParelDynamicsPermission
    {
        Nobody = 0,
        Everyone = 1,
        SelfOnly = 2,
        OthersOnly = 3,
    }

    /// <summary>Where a parameter change came from (the avatar runtime may treat them differently).</summary>
    public enum ParelParameterSource
    {
        Driver = 0,
        Contact = 1,
        PhysBone = 2,
        Rsp = 3,
        Other = 4,
    }

    /// <summary>The type of an avatar parameter as the runtime knows it.</summary>
    public enum ParelParameterKind
    {
        Unknown = 0,
        Bool = 1,
        Int = 2,
        Float = 3,
    }

    /// <summary>
    /// Implemented by the game's avatar runtime on the avatar root. Avatar Dynamics, state behaviours
    /// and Play Audio read and write avatar parameters through it, so the SDK never references game code.
    /// </summary>
    public interface IParelAvatarParameterSink
    {
        /// <summary>True for the wearer's own avatar on this client.</summary>
        bool IsLocalAvatar { get; }

        /// <summary>Expression parameters, built-ins and animator-only parameters all resolve here.</summary>
        bool TryGetParameter(string name, out float value);

        ParelParameterKind GetParameterKind(string name);

        /// <summary>Built-ins are read-only; writes to them are ignored.</summary>
        void SetParameter(string name, float value, ParelParameterSource source);
    }

    /// <summary>
    /// Implemented by the game: what the state behaviours (Parameter Driver, Tracking Control, ...)
    /// actually do. Unset in a creator project, where the behaviours are inert.
    /// </summary>
    public interface IParelAvatarBehaviourHandler
    {
        void OnTrackingControl(Animator animator, ParelAnimatorTrackingControl control);
        void OnLocomotionControl(Animator animator, bool disableLocomotion);
        void OnPlayableLayerControl(Animator animator, ParelPlayableLayerControl control);
        void OnAnimatorLayerControl(Animator animator, ParelAnimatorLayerControl control);
        void OnTemporaryPoseSpace(Animator animator, ParelAnimatorTemporaryPoseSpace poseSpace);
    }

    public static class ParelAvatarBehaviourHooks
    {
        public static IParelAvatarBehaviourHandler Handler;
    }

    /// <summary>Finds the parameter sink that owns a transform (the avatar runtime on its root).</summary>
    public static class ParelAvatarSinks
    {
        public static IParelAvatarParameterSink Find(Component component)
        {
            if (component == null) return null;
            return component.GetComponentInParent<IParelAvatarParameterSink>(true);
        }
    }

    /// <summary>
    /// The built-in parameters ParelVR feeds to every playable layer, under VRChat's names. Declare
    /// one in a controller (same name and type) to use it; don't add them to Expression Parameters.
    /// </summary>
    public static class ParelAvatarBuiltinParameters
    {
        public enum Kind
        {
            Bool,
            Int,
            Float,
        }

        public struct Info
        {
            public string Name;
            public Kind Type;
            public string Description;
        }

        public static readonly Info[] All =
        {
            new Info { Name = "IsLocal", Type = Kind.Bool, Description = "True on the wearer's own client." },
            new Info { Name = "PreviewMode", Type = Kind.Int, Description = "1 while shown on a menu pedestal / preview, else 0." },
            new Info { Name = "Viseme", Type = Kind.Int, Description = "Current viseme 0-14 (sil, PP, FF, TH, DD, kk, CH, SS, nn, RR, aa, E, ih, oh, ou)." },
            new Info { Name = "Voice", Type = Kind.Float, Description = "Voice volume 0-1." },
            new Info { Name = "GestureLeft", Type = Kind.Int, Description = "Left hand gesture 0-7 (Neutral, Fist, Open, Point, Victory, RockNRoll, Gun, ThumbsUp)." },
            new Info { Name = "GestureRight", Type = Kind.Int, Description = "Right hand gesture 0-7." },
            new Info { Name = "GestureLeftWeight", Type = Kind.Float, Description = "Left trigger 0-1." },
            new Info { Name = "GestureRightWeight", Type = Kind.Float, Description = "Right trigger 0-1." },
            new Info { Name = "AngularY", Type = Kind.Float, Description = "Turning speed in degrees per second." },
            new Info { Name = "VelocityX", Type = Kind.Float, Description = "Sideways speed (m/s)." },
            new Info { Name = "VelocityY", Type = Kind.Float, Description = "Vertical speed (m/s)." },
            new Info { Name = "VelocityZ", Type = Kind.Float, Description = "Forward speed (m/s)." },
            new Info { Name = "VelocityMagnitude", Type = Kind.Float, Description = "Total speed (m/s)." },
            new Info { Name = "Upright", Type = Kind.Float, Description = "How upright you are, 0 (lying) to 1 (standing)." },
            new Info { Name = "Grounded", Type = Kind.Bool, Description = "True when standing on the ground." },
            new Info { Name = "Seated", Type = Kind.Bool, Description = "True when sitting in a seat." },
            new Info { Name = "AFK", Type = Kind.Bool, Description = "True when away (headset off / idle)." },
            new Info { Name = "TrackingType", Type = Kind.Int, Description = "0 uninitialized, 1 generic, 2 hands-only, 3 head+hands, 4 4-point, 5 5-point, 6 full body." },
            new Info { Name = "VRMode", Type = Kind.Int, Description = "1 in VR, 0 on desktop." },
            new Info { Name = "MuteSelf", Type = Kind.Bool, Description = "True when the wearer's microphone is muted." },
            new Info { Name = "InStation", Type = Kind.Bool, Description = "True when in a seat / station." },
            new Info { Name = "Earmuffs", Type = Kind.Bool, Description = "True when earmuffs (voice dampening) are on." },
            new Info { Name = "IsOnFriendsList", Type = Kind.Bool, Description = "True when the wearer is on the viewer's friends list (always false locally)." },
            new Info { Name = "AvatarVersion", Type = Kind.Int, Description = "Always 3." },
            new Info { Name = "IsAnimatorEnabled", Type = Kind.Bool, Description = "True while the avatar's animators are running." },
            new Info { Name = "ScaleModified", Type = Kind.Bool, Description = "True when the wearer has resized the avatar." },
            new Info { Name = "ScaleFactor", Type = Kind.Float, Description = "Current size / uploaded size." },
            new Info { Name = "ScaleFactorInverse", Type = Kind.Float, Description = "1 / ScaleFactor." },
            new Info { Name = "EyeHeightAsMeters", Type = Kind.Float, Description = "Eye height in meters." },
            new Info { Name = "EyeHeightAsPercent", Type = Kind.Float, Description = "Eye height from 0.2 m (0) to 5 m (1)." },
        };

        private static HashSet<string> _names;

        public static bool IsBuiltin(string name)
        {
            if (string.IsNullOrEmpty(name)) return false;
            if (_names == null)
            {
                _names = new HashSet<string>(StringComparer.Ordinal);
                foreach (Info info in All) _names.Add(info.Name);
            }
            return _names.Contains(name);
        }
    }
}

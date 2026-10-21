using System;
using System.Collections.Generic;
using UnityEngine;
using Object = UnityEngine.Object;

namespace ParelVR.AvatarSDK
{
    /// <summary>Evaluates every Contact Receiver against the Contact Senders that share a tag with it, once per frame.</summary>
    public static class ParelContactSystem
    {
        private const float FullSpeed = 4f;
        private const float HapticSeconds = 0.08f;

        private static readonly List<ParelContactSender> Senders = new List<ParelContactSender>();
        private static readonly List<ParelContactReceiver> Receivers = new List<ParelContactReceiver>();
        private static readonly Dictionary<string, List<ParelContactSender>> ByTag = new Dictionary<string, List<ParelContactSender>>(StringComparer.Ordinal);
        private static readonly Dictionary<Transform, IParelAvatarParameterSink> SinkCache = new Dictionary<Transform, IParelAvatarParameterSink>();
        private static readonly HashSet<ParelContactSender> Candidates = new HashSet<ParelContactSender>();
        private static readonly List<ParelContactSender> Entering = new List<ParelContactSender>();

        public static int SenderCount => Senders.Count;
        public static int ReceiverCount => Receivers.Count;

        /// <summary>A touch started on a receiver. Platforms use it for world scripts and effects.</summary>
        public static event Action<ParelContactReceiver, ParelContactSender> ContactEntered;
        /// <summary>The last touch on a receiver ended.</summary>
        public static event Action<ParelContactReceiver> ContactExited;

        /// <summary>Set by the platform: vibrates a controller (left hand?, strength 0-1, seconds).</summary>
        public static Action<bool, float, float> HapticHandler;

        public static void Register(ParelContactSender sender)
        {
            if (sender != null && !Senders.Contains(sender)) Senders.Add(sender);
            ParelAvatarDynamicsRunner.EnsureRunning();
        }

        public static void Unregister(ParelContactSender sender) => Senders.Remove(sender);

        public static void Register(ParelContactReceiver receiver)
        {
            if (receiver != null && !Receivers.Contains(receiver)) Receivers.Add(receiver);
            ParelAvatarDynamicsRunner.EnsureRunning();
        }

        public static void Unregister(ParelContactReceiver receiver) => Receivers.Remove(receiver);

        /// <summary>Forget cached avatar sinks (call when an avatar is replaced).</summary>
        public static void ClearSinkCache() => SinkCache.Clear();

        internal static void Evaluate(float deltaTime)
        {
            if (Receivers.Count == 0) return;

            foreach (List<ParelContactSender> list in ByTag.Values) list.Clear();
            for (int i = Senders.Count - 1; i >= 0; i--)
            {
                ParelContactSender sender = Senders[i];
                if (sender == null)
                {
                    Senders.RemoveAt(i);
                    continue;
                }
                if (!sender.isActiveAndEnabled || sender.collisionTags == null) continue;
                Transform owner = sender.OwnerRoot;
                if (owner != null)
                {
                    IParelAvatarParameterSink sink = SinkForOwner(owner);
                    if (sender.localOnly && (sink == null || !sink.IsLocalAvatar)) continue;
                    if (!IsActive(sender, sink)) continue;
                }
                sender.UpdateWorld(deltaTime);
                for (int t = 0; t < sender.collisionTags.Count && t < ParelContactBase.MaxTags; t++)
                {
                    string tag = sender.collisionTags[t];
                    if (string.IsNullOrEmpty(tag)) continue;
                    if (!ByTag.TryGetValue(tag, out List<ParelContactSender> list))
                    {
                        list = new List<ParelContactSender>();
                        ByTag[tag] = list;
                    }
                    list.Add(sender);
                }
            }

            for (int i = Receivers.Count - 1; i >= 0; i--)
            {
                ParelContactReceiver receiver = Receivers[i];
                if (receiver == null)
                {
                    Receivers.RemoveAt(i);
                    continue;
                }
                if (!receiver.isActiveAndEnabled) continue;

                IParelAvatarParameterSink sink = null;
                if (!receiver.IsWorldContact)
                {
                    sink = SinkFor(receiver);
                    if (sink == null) continue; // the avatar's runtime is not there yet
                    if (receiver.localOnly && !sink.IsLocalAvatar) continue;
                }
                receiver.UpdateWorld(deltaTime);
                Evaluate(receiver, sink, deltaTime, receiver.IsWorldContact || IsActive(receiver, sink));
            }
        }

        /// <summary>Contacts with an "Active When" parameter exist only while it is on.</summary>
        private static bool IsActive(ParelContactBase contact, IParelAvatarParameterSink sink)
        {
            if (string.IsNullOrEmpty(contact.activeWhen)) return true;
            return sink != null && sink.TryGetParameter(contact.activeWhen, out float value) && Mathf.Abs(value) > 0.0001f;
        }

        private static void Evaluate(ParelContactReceiver receiver, IParelAvatarParameterSink sink, float deltaTime, bool active)
        {
            Candidates.Clear();
            if (active && receiver.collisionTags != null)
            {
                for (int t = 0; t < receiver.collisionTags.Count && t < ParelContactBase.MaxTags; t++)
                {
                    string tag = receiver.collisionTags[t];
                    if (!string.IsNullOrEmpty(tag) && ByTag.TryGetValue(tag, out List<ParelContactSender> list))
                        foreach (ParelContactSender sender in list) Candidates.Add(sender);
                }
            }

            receiver.TouchingBefore.Clear();
            foreach (ParelContactSender sender in receiver.Touching) receiver.TouchingBefore.Add(sender);
            receiver.Touching.Clear();

            Transform owner = receiver.OwnerRoot;
            bool fromSelf = false, fromOthers = false;
            float proximity = 0f, speed = 0f;
            Vector3 direction = Vector3.zero;

            foreach (ParelContactSender sender in Candidates)
            {
                if (sender == null || ReferenceEquals(sender, receiver)) continue;
                Transform senderOwner = sender.OwnerRoot;
                bool self = owner != null && senderOwner == owner;
                if (self ? !receiver.allowSelf : !receiver.allowOthers) continue;
                ParelContactReceiver.Sources source = senderOwner == null ? ParelContactReceiver.Sources.World : ParelContactReceiver.Sources.Avatars;
                if (!self && (receiver.sources & source) == 0) continue;

                if (ParelContactBase.Distance(receiver, sender, out _, out _) > 0.0001f) continue;
                receiver.Touching.Add(sender);
                if (self) fromSelf = true;
                else fromOthers = true;

                float closeness = Proximity(receiver, sender);
                if (closeness < proximity && receiver.Touching.Count > 1) continue;
                proximity = closeness;
                speed = (sender.Velocity - receiver.Velocity).magnitude;
                Vector3 at = sender.ClosestPoint(receiver.WorldCenter);
                if ((at - receiver.WorldCenter).sqrMagnitude < 1e-8f) at = sender.WorldCenter;
                Vector3 local = Quaternion.Inverse(receiver.WorldRotation) * (at - receiver.WorldCenter);
                direction = local.sqrMagnitude > 1e-8f ? local.normalized : Vector3.zero;
            }

            float now = Time.time;
            bool touching = receiver.Touching.Count > 0;
            Entering.Clear();
            foreach (ParelContactSender sender in receiver.Touching)
                if (!receiver.TouchingBefore.Contains(sender) && sender.Velocity.magnitude >= receiver.minVelocity) Entering.Add(sender);
            bool entered = Entering.Count > 0;
            bool exited = receiver.WasTouching && !touching;
            bool firstTouch = touching && !receiver.WasTouching;
            receiver.WasTouching = touching;

            if (entered)
            {
                receiver.Taps = (receiver.Taps + Entering.Count) % 256;
                receiver.EnteredUntil = now + Mathf.Max(0f, receiver.hold);
                receiver.PulseUntil = now + Mathf.Max(0f, receiver.hold);
                Haptics(receiver);
            }
            if (exited) receiver.ExitedUntil = now + Mathf.Max(0f, receiver.hold);

            float blend = receiver.smoothing > 0.0001f ? 1f - Mathf.Exp(-deltaTime / receiver.smoothing) : 1f;
            receiver.SmoothProximity = Mathf.Lerp(receiver.SmoothProximity, touching ? proximity : 0f, blend);
            receiver.SmoothSpeed = Mathf.Lerp(receiver.SmoothSpeed, touching ? speed : 0f, blend);
            receiver.SmoothDirection = Vector3.Lerp(receiver.SmoothDirection, touching ? direction : Vector3.zero, blend);

            float result;
            switch (receiver.receiverType)
            {
                case ParelContactReceiver.ReceiverType.OnEnter:
                    result = entered || now < receiver.PulseUntil ? receiver.value : 0f;
                    break;
                case ParelContactReceiver.ReceiverType.Proximity:
                    result = receiver.SmoothProximity;
                    break;
                default:
                    result = touching ? receiver.value : 0f;
                    break;
            }

            if (sink != null && !string.IsNullOrEmpty(receiver.parameter) && Mathf.Abs(result - receiver.LastValue) > 0.0005f)
                sink.SetParameter(receiver.parameter, result, ParelParameterSource.Contact);
            receiver.LastValue = result;

            if (sink != null && receiver.outputs != null && receiver.outputs.Count > 0)
            {
                if (receiver.LastOutputs == null || receiver.LastOutputs.Length != receiver.outputs.Count)
                {
                    receiver.LastOutputs = new float[receiver.outputs.Count];
                    for (int i = 0; i < receiver.LastOutputs.Length; i++) receiver.LastOutputs[i] = float.NaN;
                }
                for (int i = 0; i < receiver.outputs.Count; i++)
                {
                    ParelContactReceiver.Output output = receiver.outputs[i];
                    if (output == null || string.IsNullOrEmpty(output.parameter)) continue;
                    float value;
                    switch (output.kind)
                    {
                        case ParelContactReceiver.OutputKind.Touching: value = touching ? 1f : 0f; break;
                        case ParelContactReceiver.OutputKind.Entered: value = entered || now < receiver.EnteredUntil ? 1f : 0f; break;
                        case ParelContactReceiver.OutputKind.Exited: value = exited || now < receiver.ExitedUntil ? 1f : 0f; break;
                        case ParelContactReceiver.OutputKind.Proximity: value = receiver.SmoothProximity; break;
                        case ParelContactReceiver.OutputKind.Count: value = Mathf.Min(receiver.Touching.Count, 255); break;
                        case ParelContactReceiver.OutputKind.Taps: value = receiver.Taps; break;
                        case ParelContactReceiver.OutputKind.Speed: value = Mathf.Clamp01(receiver.SmoothSpeed / FullSpeed); break;
                        case ParelContactReceiver.OutputKind.DirectionX: value = receiver.SmoothDirection.x; break;
                        case ParelContactReceiver.OutputKind.DirectionY: value = receiver.SmoothDirection.y; break;
                        case ParelContactReceiver.OutputKind.DirectionZ: value = receiver.SmoothDirection.z; break;
                        case ParelContactReceiver.OutputKind.FromSelf: value = fromSelf ? 1f : 0f; break;
                        default: value = fromOthers ? 1f : 0f; break;
                    }
                    if (!float.IsNaN(receiver.LastOutputs[i]) && Mathf.Abs(value - receiver.LastOutputs[i]) <= 0.0005f) continue;
                    receiver.LastOutputs[i] = value;
                    sink.SetParameter(output.parameter, value, ParelParameterSource.Contact);
                }
            }

            if (firstTouch && Entering.Count > 0) Raise(receiver, Entering[0]);
            else if (firstTouch) Raise(receiver, null);
            if (exited)
            {
                // Avatars cannot call anything: their events are never run.
                if (receiver.IsWorldContact) Safe(receiver.onContactExit);
                try { ContactExited?.Invoke(receiver); }
                catch (Exception e) { Debug.LogException(e); }
            }
        }

        private static void Raise(ParelContactReceiver receiver, ParelContactSender sender)
        {
            if (receiver.IsWorldContact) Safe(receiver.onContactEnter);
            try { ContactEntered?.Invoke(receiver, sender); }
            catch (Exception e) { Debug.LogException(e); }
        }

        private static void Safe(UnityEngine.Events.UnityEvent e)
        {
            try { e?.Invoke(); }
            catch (Exception exception) { Debug.LogException(exception); }
        }

        /// <summary>The wearer's own hands buzz when they touch a receiver that asks for it.</summary>
        private static void Haptics(ParelContactReceiver receiver)
        {
            if (receiver.haptics <= 0f || HapticHandler == null) return;
            foreach (ParelContactSender sender in Entering)
            {
                if (sender.HapticHand == 0) continue;
                try { HapticHandler(sender.HapticHand == 1, Mathf.Clamp01(receiver.haptics), HapticSeconds); }
                catch (Exception e) { Debug.LogException(e); }
            }
        }

        /// <summary>0 where the sender just touches the edge, 1 at the centre (or the back, for a front-measured box).</summary>
        private static float Proximity(ParelContactReceiver receiver, ParelContactSender sender)
        {
            if (receiver.shapeType == ParelContactBase.ShapeType.Box)
            {
                Vector3 inside = sender.ClosestPoint(receiver.WorldCenter);
                Vector3 local = Quaternion.Inverse(receiver.WorldRotation) * (inside - receiver.WorldCenter);
                Vector3 half = receiver.WorldHalfSize;
                if (receiver.proximityFromFront)
                    return half.z > 1e-6f ? Mathf.Clamp01((half.z - local.z) / (2f * half.z)) : 1f;
                float x = half.x > 1e-6f ? Mathf.Abs(local.x) / half.x : 0f;
                float y = half.y > 1e-6f ? Mathf.Abs(local.y) / half.y : 0f;
                float z = half.z > 1e-6f ? Mathf.Abs(local.z) / half.z : 0f;
                return 1f - Mathf.Clamp01(Mathf.Max(x, Mathf.Max(y, z)));
            }
            if (sender.shapeType == ParelContactBase.ShapeType.Box)
            {
                Vector3 near = sender.ClosestPoint(receiver.WorldCenter);
                float distance = Vector3.Distance(ParelDynamicsMath.ClosestPointOnSegment(receiver.WorldA, receiver.WorldB, near), near);
                return receiver.WorldRadius > 1e-6f ? 1f - Mathf.Clamp01(distance / receiver.WorldRadius) : 1f;
            }
            float reach = receiver.WorldRadius + sender.WorldRadius;
            float centreDistance = Vector3.Distance(receiver.WorldCenter, ParelDynamicsMath.ClosestPointOnSegment(sender.WorldA, sender.WorldB, receiver.WorldCenter));
            return reach > 1e-6f ? 1f - Mathf.Clamp01(centreDistance / reach) : 1f;
        }

        private static IParelAvatarParameterSink SinkFor(ParelContactReceiver receiver)
        {
            Transform owner = receiver.OwnerRoot;
            if (owner == null) return null;
            IParelAvatarParameterSink sink = SinkForOwner(owner);
            return sink ?? ParelAvatarSinks.Find(receiver);
        }

        private static IParelAvatarParameterSink SinkForOwner(Transform owner)
        {
            if (owner == null) return null;
            if (!SinkCache.TryGetValue(owner, out IParelAvatarParameterSink sink) || sink == null || (sink is Object unityObject && unityObject == null))
            {
                sink = owner.GetComponentInChildren<IParelAvatarParameterSink>(true);
                SinkCache[owner] = sink;
            }
            return sink;
        }
    }

    /// <summary>
    /// The contacts every avatar carries from its descriptor's collider settings: senders on the head, torso, hands, feet
    /// and fingers with the standard tags, and Motion Bone colliders on the hands and fingers. Used by the game and by
    /// the SDK's avatar simulator, so both behave the same.
    /// </summary>
    public static class ParelStandardContacts
    {
        /// <summary>Builds them under the avatar's bones; returns what was made so it can be destroyed with the avatar.</summary>
        public static List<GameObject> Create(ParelAvatarDescriptor descriptor, Animator animator, Transform owner, bool wearerIsLocal)
        {
            var created = new List<GameObject>();
            if (descriptor == null || animator == null || !animator.isHuman) return created;
            foreach (ParelAvatarColliderSlot slot in Enum.GetValues(typeof(ParelAvatarColliderSlot)))
            {
                GameObject go = CreateCollider(descriptor, animator, slot, owner, wearerIsLocal);
                if (go != null) created.Add(go);
            }
            ParelContactSystem.ClearSinkCache();
            return created;
        }

        private static GameObject CreateCollider(ParelAvatarDescriptor descriptor, Animator animator, ParelAvatarColliderSlot slot, Transform owner, bool wearerIsLocal)
        {
            ParelAvatarDescriptor.ColliderConfig config = descriptor.GetCollider(slot);
            if (config == null || config.state == ParelAvatarDescriptor.ColliderState.Disabled) return null;

            Transform bone = config.state == ParelAvatarDescriptor.ColliderState.Custom && config.transform != null
                ? config.transform
                : animator.GetBoneTransform(BoneFor(slot));
            if (bone == null) return null;

            var go = new GameObject("ParelVR Collider " + slot);
            go.transform.SetParent(bone, false);

            float boneScale = Mathf.Max(0.0001f, bone.lossyScale.y);
            float avatarScale = Mathf.Max(0.0001f, owner.lossyScale.y);
            float radius = config.radius * avatarScale / boneScale;
            float height = config.height * avatarScale / boneScale;
            Vector3 position = config.state == ParelAvatarDescriptor.ColliderState.Custom ? config.position : Vector3.zero;
            Quaternion rotation = config.state == ParelAvatarDescriptor.ColliderState.Custom ? config.rotation : Quaternion.identity;

            bool capsule = slot != ParelAvatarColliderSlot.Head;
            var sender = go.AddComponent<ParelContactSender>();
            sender.shapeType = capsule ? ParelContactBase.ShapeType.Capsule : ParelContactBase.ShapeType.Sphere;
            sender.radius = radius;
            sender.height = height;
            sender.position = position;
            sender.rotation = rotation;
            sender.collisionTags = TagsFor(slot);
            sender.OwnerRootOverride = owner;
            if (wearerIsLocal && IsHandOrFinger(slot)) sender.HapticHand = IsLeft(slot) ? 1 : 2;

            if (IsHandOrFinger(slot))
            {
                var collider = go.AddComponent<ParelMotionBoneCollider>();
                collider.shapeType = ParelMotionBoneCollider.ShapeType.Capsule;
                collider.radius = radius;
                collider.height = height;
                collider.position = position;
                collider.rotation = rotation;
                collider.OwnerRootOverride = owner;
                ParelMotionBoneSystem.RegisterGlobalCollider(collider);
            }
            return go;
        }

        public static bool IsHandOrFinger(ParelAvatarColliderSlot slot) =>
            slot != ParelAvatarColliderSlot.Head && slot != ParelAvatarColliderSlot.Torso &&
            slot != ParelAvatarColliderSlot.FootL && slot != ParelAvatarColliderSlot.FootR;

        private static bool IsLeft(ParelAvatarColliderSlot slot) => slot.ToString().EndsWith("L", StringComparison.Ordinal);

        public static HumanBodyBones BoneFor(ParelAvatarColliderSlot slot)
        {
            switch (slot)
            {
                case ParelAvatarColliderSlot.Head: return HumanBodyBones.Head;
                case ParelAvatarColliderSlot.Torso: return HumanBodyBones.Chest;
                case ParelAvatarColliderSlot.HandL: return HumanBodyBones.LeftHand;
                case ParelAvatarColliderSlot.HandR: return HumanBodyBones.RightHand;
                case ParelAvatarColliderSlot.FootL: return HumanBodyBones.LeftFoot;
                case ParelAvatarColliderSlot.FootR: return HumanBodyBones.RightFoot;
                case ParelAvatarColliderSlot.FingerIndexL: return HumanBodyBones.LeftIndexDistal;
                case ParelAvatarColliderSlot.FingerIndexR: return HumanBodyBones.RightIndexDistal;
                case ParelAvatarColliderSlot.FingerMiddleL: return HumanBodyBones.LeftMiddleDistal;
                case ParelAvatarColliderSlot.FingerMiddleR: return HumanBodyBones.RightMiddleDistal;
                case ParelAvatarColliderSlot.FingerRingL: return HumanBodyBones.LeftRingDistal;
                case ParelAvatarColliderSlot.FingerRingR: return HumanBodyBones.RightRingDistal;
                case ParelAvatarColliderSlot.FingerLittleL: return HumanBodyBones.LeftLittleDistal;
                default: return HumanBodyBones.RightLittleDistal;
            }
        }

        /// <summary>The standard tags of a body part, from general to specific: Hand, HandL; Finger, FingerL, FingerIndex, FingerIndexL.</summary>
        public static List<string> TagsFor(ParelAvatarColliderSlot slot)
        {
            switch (slot)
            {
                case ParelAvatarColliderSlot.Head: return new List<string> { "Head" };
                case ParelAvatarColliderSlot.Torso: return new List<string> { "Torso" };
                case ParelAvatarColliderSlot.HandL: return new List<string> { "Hand", "HandL" };
                case ParelAvatarColliderSlot.HandR: return new List<string> { "Hand", "HandR" };
                case ParelAvatarColliderSlot.FootL: return new List<string> { "Foot", "FootL" };
                case ParelAvatarColliderSlot.FootR: return new List<string> { "Foot", "FootR" };
            }
            string name = slot.ToString();
            string side = name.EndsWith("L", StringComparison.Ordinal) ? "L" : "R";
            string finger = name.Substring(0, name.Length - 1);
            return new List<string> { "Finger", "Finger" + side, finger, finger + side };
        }
    }
}

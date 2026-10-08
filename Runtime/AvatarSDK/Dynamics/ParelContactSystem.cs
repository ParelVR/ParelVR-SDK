using System.Collections.Generic;
using UnityEngine;

namespace ParelVR.AvatarSDK
{
    /// <summary>Evaluates every Contact Receiver against every Contact Sender once per frame.</summary>
    public static class ParelContactSystem
    {
        private static readonly List<ParelContactSender> Senders = new List<ParelContactSender>();
        private static readonly List<ParelContactReceiver> Receivers = new List<ParelContactReceiver>();
        private static readonly Dictionary<string, List<ParelContactSender>> ByTag = new Dictionary<string, List<ParelContactSender>>(System.StringComparer.Ordinal);
        private static readonly Dictionary<Transform, IParelAvatarParameterSink> SinkCache = new Dictionary<Transform, IParelAvatarParameterSink>();
        private static readonly HashSet<ParelContactSender> Candidates = new HashSet<ParelContactSender>();

        public static int SenderCount => Senders.Count;
        public static int ReceiverCount => Receivers.Count;

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
                if (!sender.isActiveAndEnabled) continue;
                if (sender.localOnly && !IsLocal(sender.OwnerRoot)) continue;
                sender.UpdateWorld(deltaTime);
                if (sender.collisionTags == null) continue;
                foreach (string tag in sender.collisionTags)
                {
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
                if (!receiver.isActiveAndEnabled || string.IsNullOrEmpty(receiver.parameter)) continue;

                IParelAvatarParameterSink sink = SinkFor(receiver);
                if (sink == null) continue;
                if (receiver.localOnly && !sink.IsLocalAvatar) continue;

                receiver.UpdateWorld(deltaTime);
                Evaluate(receiver, sink);
            }
        }

        private static void Evaluate(ParelContactReceiver receiver, IParelAvatarParameterSink sink)
        {
            Candidates.Clear();
            if (receiver.collisionTags != null)
            {
                foreach (string tag in receiver.collisionTags)
                {
                    if (!string.IsNullOrEmpty(tag) && ByTag.TryGetValue(tag, out List<ParelContactSender> list))
                    {
                        foreach (ParelContactSender sender in list) Candidates.Add(sender);
                    }
                }
            }

            Transform owner = receiver.OwnerRoot;
            bool touching = false;
            bool fastEnough = false;
            float proximity = 0f;

            foreach (ParelContactSender sender in Candidates)
            {
                if (sender == null || sender == (ParelContactBase)receiver) continue;
                bool self = owner != null && sender.OwnerRoot == owner;
                if (self ? !receiver.allowSelf : !receiver.allowOthers) continue;

                ParelDynamicsMath.ClosestPointsBetweenSegments(receiver.WorldA, receiver.WorldB, sender.WorldA, sender.WorldB, out Vector3 onReceiver, out Vector3 onSender);
                float distance = Vector3.Distance(onReceiver, onSender);
                float reach = receiver.WorldRadius + sender.WorldRadius;
                if (distance > reach) continue;

                touching = true;
                if (sender.Velocity.magnitude >= receiver.minVelocity) fastEnough = true;
                if (receiver.receiverType == ParelContactReceiver.ReceiverType.Proximity)
                {
                    // 1 at the receiver's center, 0 where the sender just touches its edge.
                    float centerDistance = Vector3.Distance(receiver.WorldCenter, ParelDynamicsMath.ClosestPointOnSegment(sender.WorldA, sender.WorldB, receiver.WorldCenter));
                    float value = reach > 1e-6f ? 1f - Mathf.Clamp01(centerDistance / reach) : 1f;
                    if (value > proximity) proximity = value;
                }
            }

            float result;
            switch (receiver.receiverType)
            {
                case ParelContactReceiver.ReceiverType.OnEnter:
                    if (receiver.PendingReset)
                    {
                        receiver.PendingReset = false;
                        result = 0f;
                    }
                    else if (touching && !receiver.WasTouching && fastEnough)
                    {
                        result = 1f;
                        receiver.PendingReset = true;
                    }
                    else
                    {
                        result = 0f;
                    }
                    break;
                case ParelContactReceiver.ReceiverType.Proximity:
                    result = proximity;
                    break;
                default:
                    result = touching ? 1f : 0f;
                    break;
            }

            receiver.WasTouching = touching;
            if (Mathf.Abs(result - receiver.LastValue) > 0.0005f)
            {
                receiver.LastValue = result;
                sink.SetParameter(receiver.parameter, result, ParelParameterSource.Contact);
            }
        }

        private static IParelAvatarParameterSink SinkFor(ParelContactReceiver receiver)
        {
            Transform owner = receiver.OwnerRoot;
            if (owner == null) return null;
            if (!SinkCache.TryGetValue(owner, out IParelAvatarParameterSink sink) || sink == null || (sink is Object unityObject && unityObject == null))
            {
                sink = owner.GetComponentInChildren<IParelAvatarParameterSink>(true) ?? ParelAvatarSinks.Find(receiver);
                SinkCache[owner] = sink;
            }
            return sink;
        }

        private static bool IsLocal(Transform owner)
        {
            if (owner == null) return false;
            IParelAvatarParameterSink sink = owner.GetComponentInChildren<IParelAvatarParameterSink>(true);
            return sink != null && sink.IsLocalAvatar;
        }
    }
}

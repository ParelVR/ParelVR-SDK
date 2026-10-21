using System;
using System.Collections.Generic;
using UnityEngine;

namespace ParelVR.WorldSDK
{
    /// <summary>
    /// Where a world's RSP components meet the game. The game hands this every RSP message whose address
    /// starts with /world/ and sends out whatever the world's components send, so apps outside the game
    /// (stream decks, overlays, lighting rigs) can drive a world's objects and hear back from them.
    /// </summary>
    public static class ParelRspWorld
    {
        public const string Prefix = "/world/";

        private static readonly Dictionary<string, List<ParelRspReceiver>> Receivers = new Dictionary<string, List<ParelRspReceiver>>(StringComparer.Ordinal);
        private static readonly List<ParelRspReceiver> Delivering = new List<ParelRspReceiver>();

        /// <summary>Raised for every message a world component sends; the game puts it on the wire.</summary>
        public static event Action<string, object[]> Outgoing;

        public static int ReceiverCount { get; private set; }
        public static int AddressCount => Receivers.Count;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetState()
        {
            Receivers.Clear();
            ReceiverCount = 0;
            Outgoing = null;
        }

        /// <summary>"lights", "/lights" and "/world/lights" all become "/world/lights". Null for an empty address.</summary>
        public static string Normalize(string address)
        {
            if (string.IsNullOrWhiteSpace(address)) return null;
            address = address.Trim();
            if (address.StartsWith(Prefix, StringComparison.Ordinal)) return address.Length > Prefix.Length ? address : null;
            address = address.TrimStart('/');
            return address.Length > 0 ? Prefix + address : null;
        }

        internal static void Register(string address, ParelRspReceiver receiver)
        {
            if (!Receivers.TryGetValue(address, out List<ParelRspReceiver> list))
            {
                list = new List<ParelRspReceiver>(1);
                Receivers.Add(address, list);
            }
            if (list.Contains(receiver)) return;
            list.Add(receiver);
            ReceiverCount++;
        }

        internal static void Unregister(string address, ParelRspReceiver receiver)
        {
            if (address == null || !Receivers.TryGetValue(address, out List<ParelRspReceiver> list) || !list.Remove(receiver)) return;
            ReceiverCount--;
            if (list.Count == 0) Receivers.Remove(address);
        }

        /// <summary>Called by the game on the main thread. False when nothing in the world listens on the address.</summary>
        public static bool Deliver(string address, object[] args)
        {
            if (address == null || !Receivers.TryGetValue(address, out List<ParelRspReceiver> list) || list.Count == 0) return false;
            // A receiver can switch objects off (and so unregister others) while it runs.
            Delivering.Clear();
            Delivering.AddRange(list);
            for (int i = 0; i < Delivering.Count; i++)
            {
                ParelRspReceiver receiver = Delivering[i];
                if (receiver == null) continue;
                try { receiver.Receive(args ?? Array.Empty<object>()); }
                catch (Exception e) { Debug.LogException(e, receiver); }
            }
            Delivering.Clear();
            return true;
        }

        /// <summary>Sends a message out to RSP apps. The address goes under /world/.</summary>
        public static void Send(string address, params object[] args)
        {
            address = Normalize(address);
            if (address == null) return;
            Outgoing?.Invoke(address, args ?? Array.Empty<object>());
        }
    }
}

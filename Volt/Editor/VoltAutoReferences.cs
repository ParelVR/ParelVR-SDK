using System;
using System.Collections.Generic;
using System.Reflection;
using ParelVR.SDK.Core.Settings;
using ParelVR.Volt.Interaction;
using ParelVR.Volt.Networking;
using ParelVR.Volt.Players;
using UnityEditor;
using UnityEngine;

namespace ParelVR.Volt.EditorTools
{
    /// <summary>
    /// "Automatically Add Referenced Scripts": when a component is added to an object and it needs other components
    /// to work, those are added with it. Only ever adds; nothing already on the object is changed. Switched on and
    /// off in ParelVR SDK > Settings > Preferences.
    /// </summary>
    [InitializeOnLoad]
    public static class VoltAutoReferences
    {
        private static bool _adding;

        static VoltAutoReferences()
        {
            ObjectFactory.componentWasAdded -= OnComponentAdded;
            ObjectFactory.componentWasAdded += OnComponentAdded;
        }

        private static void OnComponentAdded(Component component)
        {
            if (_adding || component == null || !ParelPreferences.AutoAddReferencedScripts) return;
            if (EditorApplication.isPlayingOrWillChangePlaymode) return;

            _adding = true;
            try
            {
                var added = new List<string>();
                Require(component, added);
                if (added.Count > 0)
                    Debug.Log("[ParelVR SDK] " + ObjectNames.NicifyVariableName(component.GetType().Name) + " on '" + component.gameObject.name +
                              "' needs " + string.Join(", ", added) + ": added automatically. (Settings > Preferences > Automatically Add Referenced Scripts)",
                        component.gameObject);
            }
            catch (Exception e)
            {
                Debug.LogException(e);
            }
            finally
            {
                _adding = false;
            }
        }

        /// <summary>What each kind of component cannot work without.</summary>
        private static void Require(Component component, List<string> added)
        {
            GameObject go = component.gameObject;
            switch (component)
            {
                case VoltPickup _:
                    // Carried and thrown by physics, pointed at through a collider, and seen by everyone through the sync.
                    Add<Rigidbody>(go, added);
                    AddCollider(go, added);
                    Add<VoltObjectSync>(go, added);
                    break;
                case VoltInteractable _:
                case VoltStation _:
                    AddCollider(go, added);
                    break;
                case VoltBehaviour behaviour:
                    RequireForEvents(behaviour, go, added);
                    break;
            }
        }

        /// <summary>A script that handles an event needs the component that raises it.</summary>
        private static void RequireForEvents(VoltBehaviour behaviour, GameObject go, List<string> added)
        {
            Type type = behaviour.GetType();
            bool pickup = Handles(type, "OnPickup") || Handles(type, "OnDrop") || Handles(type, "OnPickupUseDown") || Handles(type, "OnPickupUseUp");
            bool station = Handles(type, "OnStationEntered", typeof(VoltPlayer)) || Handles(type, "OnStationExited", typeof(VoltPlayer));

            if (pickup && go.GetComponent<VoltPickup>() == null)
            {
                VoltPickup made = Add<VoltPickup>(go, added);
                if (made != null) Require(made, added);
            }
            if (station && go.GetComponent<VoltStation>() == null)
            {
                VoltStation made = Add<VoltStation>(go, added);
                if (made != null) Require(made, added);
            }
            // OnInteract works with a VoltInteractable or with a bare collider; a collider is the least that is needed.
            if (Handles(type, "OnInteract")) AddCollider(go, added);
        }

        private static bool Handles(Type type, string method, params Type[] parameters)
        {
            MethodInfo found = type.GetMethod(method, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null, parameters, null);
            return found != null && found.DeclaringType != typeof(VoltBehaviour) && found.GetBaseDefinition().DeclaringType == typeof(VoltBehaviour);
        }

        private static T Add<T>(GameObject go, List<string> added) where T : Component
        {
            if (go.GetComponent<T>() != null) return null;
            T made = Undo.AddComponent<T>(go);
            if (made != null) added.Add(ObjectNames.NicifyVariableName(typeof(T).Name));
            return made;
        }

        /// <summary>Any collider on the object or under it will do; without one a box is fitted to the mesh.</summary>
        private static void AddCollider(GameObject go, List<string> added)
        {
            if (go.GetComponentInChildren<Collider>(true) != null) return;
            if (Undo.AddComponent<BoxCollider>(go) != null) added.Add("Box Collider");
        }
    }
}

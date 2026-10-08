using System;
using System.Linq;
using System.Collections.Generic;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.SceneManagement;
using ParelVR.SDK.Core.Validation;
using ParelVR.SDK.Worlds.Components;
using UnityEditor;

namespace ParelVR.SDK.Worlds.Validation
{
    public class WorldValidator : IValidator
    {
        public string Name => "World Components";
        public global::ParelVR.SDK.Core.Settings.ParelProjectType RequiredMode => global::ParelVR.SDK.Core.Settings.ParelProjectType.World;

        public Task<ValidationReport> ValidateAsync()
        {
            var report = new ValidationReport();

            var allBehaviours = new List<MonoBehaviour>();
            var cameras = new List<Camera>();
            var lights = new List<Light>();

            for (int i = 0; i < SceneManager.sceneCount; i++)
            {
                var scene = SceneManager.GetSceneAt(i);
                if (!scene.isLoaded) continue;

                foreach (var root in scene.GetRootGameObjects())
                {
                    allBehaviours.AddRange(root.GetComponentsInChildren<MonoBehaviour>(true));
                    cameras.AddRange(root.GetComponentsInChildren<Camera>(true));
                    lights.AddRange(root.GetComponentsInChildren<Light>(true));
                }
            }

            // The SDK's ParelVR Spawn Point (and the game's own SpawnPoint, in projects that have it).
            var actualSpawnPoints = allBehaviours.FindAll(s => s != null && (s is global::ParelVR.WorldSDK.ParelVRSpawnPoint || s.GetType().Name == "SpawnPoint"));
            var descriptors = allBehaviours.FindAll(s => s != null && s is ParelWorldDescriptor);

            var descriptor = descriptors.FirstOrDefault() as ParelWorldDescriptor;
            var spawnPoint = actualSpawnPoints.FirstOrDefault();

            if (descriptor == null && spawnPoint == null)
            {
                report.AddError("Your scene is missing a World Descriptor and a Spawn Point.", () =>
                {
                    var go = new GameObject("[ParelVR World]");
                    go.AddComponent<ParelWorldDescriptor>();
                    var spawn = new GameObject("Spawn Point");
                    spawn.transform.SetParent(go.transform, false);
                    spawn.AddComponent<global::ParelVR.WorldSDK.ParelVRSpawnPoint>();
                    Undo.RegisterCreatedObjectUndo(go, "Create ParelVR World");
                });
            }
            else if (descriptor == null && spawnPoint != null)
            {
                report.AddError("Your scene is missing a World Descriptor.", () =>
                {
                    Undo.AddComponent<ParelWorldDescriptor>(spawnPoint.gameObject);
                });
            }
            else if (descriptor != null && spawnPoint == null)
            {
                report.AddError("Your world needs at least one ParelVR Spawn Point (Add Component > ParelVR > World SDK > ParelVR Spawn Point).", () =>
                {
                    var spawn = new GameObject("Spawn Point");
                    spawn.transform.SetParent(descriptor.transform, false);
                    spawn.AddComponent<global::ParelVR.WorldSDK.ParelVRSpawnPoint>();
                    Undo.RegisterCreatedObjectUndo(spawn, "Add Spawn Point");
                });
            }
            
            if (actualSpawnPoints.Count > 1)
            {
                report.AddInfo("You have several Spawn Points. Players arrive at the enabled one with the highest Priority.");
            }
            
            if (descriptors.Count > 1)
            {
                report.AddWarning("You have more than one World Descriptor. Only one is needed.");
            }

            foreach (var cam in cameras)
            {
                var listener = cam.GetComponent<AudioListener>();
                if (listener != null)
                {
                    report.AddError($"Camera '{cam.name}' has an AudioListener. ParelVR client adds its own.", () =>
                    {
                        Undo.DestroyObjectImmediate(listener);
                    });
                }
            }

            if (lights.FindAll(l => l.type == LightType.Directional).Count == 0)
            {
                report.AddWarning("Your world has no Directional Light. It might be completely dark.");
            }

            return Task.FromResult(report);
        }
    }
}

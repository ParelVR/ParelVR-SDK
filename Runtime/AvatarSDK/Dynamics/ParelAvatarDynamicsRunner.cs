using UnityEngine;

namespace ParelVR.AvatarSDK
{
    /// <summary>
    /// Hidden driver for Avatar Dynamics. Runs after everything else in LateUpdate (animation, IK,
    /// network poses, finger tracking have all been applied by then): Motion Bones first, then Contacts.
    /// Created automatically the first time a Motion Bone, contact or grabber appears in Play Mode.
    /// </summary>
    [DefaultExecutionOrder(32000)]
    [AddComponentMenu("")]
    public sealed class ParelAvatarDynamicsRunner : MonoBehaviour
    {
        private static ParelAvatarDynamicsRunner _instance;

        /// <summary>Raised after Motion Bones and Contacts have run for the frame.</summary>
        public static event System.Action AfterDynamics;

        public static void EnsureRunning()
        {
            if (_instance != null || !Application.isPlaying) return;
            var go = new GameObject("ParelVR Avatar Dynamics") { hideFlags = HideFlags.HideInHierarchy | HideFlags.DontSave };
            DontDestroyOnLoad(go);
            _instance = go.AddComponent<ParelAvatarDynamicsRunner>();
        }

        // Last of the Updates, before the Animator: Motion Bones drop last frame's simulated pose.
        private void Update()
        {
            try
            {
                ParelMotionBoneSystem.BeforeAnimation();
            }
            catch (System.Exception e)
            {
                Debug.LogException(e);
            }
        }

        private void LateUpdate()
        {
            float dt = Time.deltaTime;
            try
            {
                ParelMotionBoneSystem.Simulate(dt);
            }
            catch (System.Exception e)
            {
                Debug.LogException(e);
            }

            try
            {
                ParelContactSystem.Evaluate(dt);
            }
            catch (System.Exception e)
            {
                Debug.LogException(e);
            }

            AfterDynamics?.Invoke();
        }

        private void OnDestroy()
        {
            if (_instance == this) _instance = null;
        }
    }
}

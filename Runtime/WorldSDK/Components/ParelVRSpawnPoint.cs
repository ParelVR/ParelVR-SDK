using UnityEngine;

namespace ParelVR.WorldSDK
{
    public enum ParelVRSpawnFacingMode
    {
        UsePointForward = 0,
        FaceWorldForward = 1,
        FaceWorldCenter = 2
    }

    [DisallowMultipleComponent]
    [AddComponentMenu("ParelVR/World SDK/ParelVR Spawn Point")]
    public sealed class ParelVRSpawnPoint : MonoBehaviour
    {
        [SerializeField] private string spawnLabel = "Spawn";
        [SerializeField, Min(0)] private int priority;
        [SerializeField, Min(0f)] private float radiusOverride;
        [SerializeField] private bool oneShot;
        [SerializeField] private bool enabledForRuntime = true;
        [SerializeField] private ParelVRSpawnFacingMode facingMode = ParelVRSpawnFacingMode.UsePointForward;
        [SerializeField] private Color gizmoColor = new Color(0.25f, 0.82f, 0.71f, 0.92f);

        public string SpawnLabel => spawnLabel;
        public int Priority => priority;
        public float RadiusOverride => radiusOverride;
        public bool OneShot => oneShot;
        public bool EnabledForRuntime => enabledForRuntime;
        public ParelVRSpawnFacingMode FacingMode => facingMode;
        public Color GizmoColor => gizmoColor;

        private void OnDrawGizmos()
        {
            Gizmos.color = gizmoColor;
            Gizmos.DrawWireSphere(transform.position, Mathf.Max(0.2f, radiusOverride > 0f ? radiusOverride : 0.4f));
            Gizmos.DrawLine(transform.position, transform.position + (transform.forward * 1.2f));
        }
    }
}

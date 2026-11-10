using System;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace ParelVR.Rendering
{
    /// <summary>
    /// ParelVR Adaptive Rendering (PVAR): keeps the frame rate at the display's refresh rate by lowering quality where
    /// the frame is slow (GPU or CPU) and raising it again once there is room, never above the player's own settings.
    /// It does nothing until <see cref="Enable"/> is called; <see cref="Disable"/> puts the player's settings back.
    /// The game tells it the player's settings with <see cref="SetUserLimits"/> whenever it applies them (also after
    /// anything resets QualitySettings), and holds it during loads with <see cref="Hold"/>.
    /// </summary>
    [DefaultExecutionOrder(-9000)]
    public sealed class ParelAdaptiveRendering : MonoBehaviour
    {
        public static ParelAdaptiveRendering Instance { get; private set; }
        public static bool IsEnabled => Instance != null;

        /// <summary>Raised after a new profile was applied: (profile, reason). The game applies what PVRP can't (Quest foveation, post-processing).</summary>
        public static event Action<QualityProfile, string> ProfileChanged;

        /// <summary>Optional: true while the device throttles for heat (the game wires it to the platform's API).</summary>
        public static Func<bool> ThermalProvider;

        public HardwareInfo Hardware { get; private set; }
        public AdaptiveController Controller { get; private set; }
        public ParelQualityApplier Applier { get; private set; }
        public ParelFrameTimingSampler Sampler { get; } = new ParelFrameTimingSampler();
        public QualityProfile Current { get; private set; }
        public float TargetMs { get; private set; }

        private float _nextTargetCheck;
        private float _nextStatus;

        /// <summary>
        /// Starts PVAR (or returns the running one). <paramref name="project"/> is the project's top quality setup and
        /// <paramref name="user"/> the player's settings as currently applied; both default to what Unity uses now.
        /// </summary>
        public static ParelAdaptiveRendering Enable(QualityLimits? project = null, QualityLimits? user = null, AdaptiveSettings settings = null)
        {
            if (Instance != null) return Instance;
            ParelRenderManager.Initialize();
            var go = new GameObject("ParelVR Adaptive Rendering");
            DontDestroyOnLoad(go);
            ParelAdaptiveRendering pvar = go.AddComponent<ParelAdaptiveRendering>();
            pvar.Begin(project ?? QualityLimits.Capture(), user ?? QualityLimits.Capture(), settings);
            return pvar;
        }

        /// <summary>Stops PVAR and puts the player's own settings back.</summary>
        public static void Disable()
        {
            if (Instance == null) return;
            Instance.Applier.Restore();
            RenderingLog.Notice("PVAR off: the player's settings are back (" + Instance.Applier.User + ").");
            Destroy(Instance.gameObject);
            Instance = null;
        }

        /// <summary>The player's settings changed or were applied again: they are the new ceiling, and the current profile is applied under them.</summary>
        public static void SetUserLimits(QualityLimits user)
        {
            if (Instance == null) return;
            Instance.Applier.User = user;
            Instance.Apply(Instance.Controller.Current, "player settings applied");
        }

        /// <summary>Ignores frame times for <paramref name="seconds"/> (a world loading, an avatar swap).</summary>
        public static void Hold(float seconds)
        {
            if (Instance == null) return;
            Instance.Controller.Hold(Time.unscaledTimeAsDouble, seconds);
        }

        /// <summary>Sets the best and worst rung PVAR may use (0 = the platform's top profile; -1 worst = its last).</summary>
        public static void SetRange(int best, int worst)
        {
            if (Instance == null) return;
            Instance.Controller.Settings.BestLevel = best;
            Instance.Controller.Settings.WorstLevel = worst;
            if (Instance.Controller.ApplyLimits()) Instance.Apply(Instance.Controller.Current, "range changed");
        }

        private void Begin(QualityLimits project, QualityLimits user, AdaptiveSettings settings)
        {
            Instance = this;
            Hardware = ParelHardwareDetector.Detect();
            int start = HardwareClassifier.StartingLevel(Hardware);
            Controller = new AdaptiveController(QualityProfiles.For(Hardware.Platform), settings, start);
            Applier = new ParelQualityApplier(project, user);
            TargetMs = ParelHardwareDetector.TargetFrameMs();
            RenderingLog.Notice("PVAR on. Device: " + Hardware + ". Budget " + TargetMs.ToString("0.0") + " ms" +
                              (ParelHardwareDetector.FramesArePaced ? " (paced)" : string.Empty) + ". Start: " + Controller.Ladder[Controller.GpuLevel].Name + ".");
            Apply(Controller.Current, "start (hardware estimate)");
            Controller.Hold(Time.unscaledTimeAsDouble, 3f);
            _nextStatus = Time.unscaledTime + 10f; // first status once timings have come in
            SceneManager.sceneLoaded += OnSceneLoaded;
        }

        private void OnDestroy()
        {
            SceneManager.sceneLoaded -= OnSceneLoaded;
            if (Instance == this) Instance = null;
        }

        private void OnSceneLoaded(Scene scene, LoadSceneMode mode) => Controller.Hold(Time.unscaledTimeAsDouble, 3f);

        private void Update()
        {
            if (Time.unscaledTime >= _nextTargetCheck)
            {
                // The headset or the V-Sync setting can change while running.
                _nextTargetCheck = Time.unscaledTime + 2f;
                TargetMs = ParelHardwareDetector.TargetFrameMs();
            }
            FrameSample s = Sampler.Sample(TargetMs, ParelHardwareDetector.FramesArePaced);
            s.Thermal = ThermalProvider != null && SafeThermal();
            if (Controller.Update(s)) Apply(Controller.Current, Controller.LastReason);

            if (Time.unscaledTime >= _nextStatus)
            {
                _nextStatus = Time.unscaledTime + 60f;
                RenderingLog.Notice($"PVAR: {Current?.Name}, GPU {Controller.GpuAverageMs:0.0} ms / CPU {Controller.CpuAverageMs:0.0} ms of {TargetMs:0.0} ms" +
                                  (Sampler.SplitAvailable ? string.Empty : " (total frame time; turn on Frame Timing Stats for CPU/GPU)"));
            }
        }

        private bool SafeThermal()
        {
            try { return ThermalProvider(); }
            catch (Exception) { return false; }
        }

        private void Apply(QualityProfile p, string reason)
        {
            Current = p;
            Applier.Apply(p);
            RenderingLog.Notice("PVAR → " + p.Name + " (" + reason + "): " + Applier.Applied + ", render scale " + Applier.AppliedRenderScale.ToString("0.00"));
            try { ProfileChanged?.Invoke(p, reason); }
            catch (Exception e) { Debug.LogException(e); }
        }
    }
}

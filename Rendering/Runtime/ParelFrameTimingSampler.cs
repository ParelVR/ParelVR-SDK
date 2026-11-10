using UnityEngine;

namespace ParelVR.Rendering
{
    /// <summary>
    /// Measures each frame for PVAR. With <b>Frame Timing Stats</b> on (Player Settings) Unity reports CPU and GPU time
    /// separately and PVAR lowers only the slow side. Without it only the total frame time is known: PVAR then counts
    /// missed frames and lowers GPU settings first.
    /// </summary>
    public sealed class ParelFrameTimingSampler
    {
        private readonly FrameTiming[] _timings = new FrameTiming[1];
        private int _framesWithoutSplit;

        /// <summary>True once CPU/GPU timings have come through (Frame Timing Stats on and supported by the graphics API).</summary>
        public bool SplitAvailable { get; private set; }

        /// <summary>Call once per frame (Update). Uses the previous frame's timings, the latest Unity has.</summary>
        public FrameSample Sample(float targetMs, bool paced)
        {
            double now = Time.unscaledTimeAsDouble;
            float totalMs = Time.unscaledDeltaTime * 1000f;

            FrameTimingManager.CaptureFrameTimings();
            if (FrameTimingManager.GetLatestTimings(1, _timings) > 0)
            {
                FrameTiming t = _timings[0];
                // Work, not waiting: the main thread minus its wait for the display, or the render thread if that took longer.
                double cpu = System.Math.Max(t.cpuMainThreadFrameTime - t.cpuMainThreadPresentWaitTime, t.cpuRenderThreadFrameTime);
                if (t.gpuFrameTime > 0 && cpu > 0)
                {
                    SplitAvailable = true;
                    _framesWithoutSplit = 0;
                    return new FrameSample { Time = now, CpuMs = (float)cpu, GpuMs = (float)t.gpuFrameTime, TargetMs = targetMs, HasSplit = true, Paced = paced };
                }
            }
            // Some frames (a load, a device change) come without GPU time; only give up on the split after a run of them.
            if (++_framesWithoutSplit > 120) SplitAvailable = false;
            FrameSample total = FrameSample.Total(now, totalMs, targetMs);
            total.Paced = paced;
            return total;
        }
    }
}

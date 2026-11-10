using System;
using System.Collections.Generic;

namespace ParelVR.Rendering
{
    /// <summary>One frame's timing, in milliseconds. <see cref="HasSplit"/> is false when only the total frame time is known.</summary>
    public struct FrameSample
    {
        /// <summary>Seconds since start-up (unscaled).</summary>
        public double Time;
        public float CpuMs;
        public float GpuMs;
        /// <summary>The frame budget: 1000 / the display's refresh rate.</summary>
        public float TargetMs;
        /// <summary>True when the CPU and GPU times were measured separately (FrameTimingManager); false when only the total is known.</summary>
        public bool HasSplit;
        /// <summary>The device reports it is throttling for heat (Quest). Quality is not raised while it does.</summary>
        public bool Thermal;
        /// <summary>
        /// Frames wait for the display (V-Sync or a headset compositor). Then a total frame time sits at the budget even
        /// when there is time to spare, so without a split only missed frames (an average above the budget) count.
        /// </summary>
        public bool Paced;

        public static FrameSample Total(double time, float frameMs, float targetMs) =>
            new FrameSample { Time = time, CpuMs = frameMs, GpuMs = frameMs, TargetMs = targetMs, HasSplit = false };
    }

    /// <summary>Tuning for <see cref="AdaptiveController"/>. Times in seconds; thresholds are fractions of the frame budget.</summary>
    public sealed class AdaptiveSettings
    {
        /// <summary>A side drops a step when its average frame time is above this share of the budget.</summary>
        public float DownThreshold = 0.92f;
        /// <summary>A side may come back up a step when its average is below this share of the budget.</summary>
        public float UpThreshold = 0.72f;
        /// <summary>Time constant of the rolling average.</summary>
        public float AverageWindow = 1.0f;
        /// <summary>Shortest time between two steps (either side).</summary>
        public float MinStepInterval = 2.0f;
        /// <summary>How long a side has to stay fast after its last drop before it may come back up.</summary>
        public float RecoverDelay = 8.0f;
        /// <summary>Upper bound of the recover delay after repeated bouncing between two steps.</summary>
        public float MaxRecoverDelay = 64.0f;
        /// <summary>Paced frames, total time only: drop when the average is this far over the budget (about one frame in seven missed).</summary>
        public float PacedDownThreshold = 1.15f;
        /// <summary>Paced frames, total time only: recovery needs the average this close to the budget (almost no missed frames).</summary>
        public float PacedUpThreshold = 1.02f;
        /// <summary>A single frame counts at most this many budgets, so one hitch (a load, a GC) can't drag the average down a whole step.</summary>
        public float SampleClamp = 2.0f;
        /// <summary>Samples are ignored for this long after a step or a <see cref="AdaptiveController.Hold"/> (the new settings settle).</summary>
        public float SettleTime = 1.0f;
        /// <summary>Best allowed rung (0 = the ladder's top). The user's ceiling.</summary>
        public int BestLevel;
        /// <summary>Worst allowed rung. The user's floor; -1 = the ladder's last rung.</summary>
        public int WorstLevel = -1;
    }

    /// <summary>
    /// ParelVR Adaptive Rendering (PVAR) decision logic. It keeps a rolling average of CPU and GPU frame time against
    /// the frame budget and moves a GPU rung and a CPU rung on a quality ladder separately, so only the slow side gives
    /// up quality. Drops are quick; recovery waits (<see cref="AdaptiveSettings.RecoverDelay"/>), and the wait doubles
    /// every time a recovery has to be taken back, so the quality doesn't flicker between two steps. Pure logic: the
    /// caller feeds samples and applies <see cref="Current"/> when <see cref="Update"/> returns true.
    /// </summary>
    public sealed class AdaptiveController
    {
        private sealed class Side
        {
            public int Level;
            public double Average = -1;
            public double LastDown = double.NegativeInfinity;
            public double LastUp = double.NegativeInfinity;
            public float RecoverDelay;
        }

        private readonly Side _gpu = new Side();
        private readonly Side _cpu = new Side();
        private double _lastStep = double.NegativeInfinity;
        private double _settleUntil = double.NegativeInfinity;
        private double _lastTime = double.NaN;
        private double _collected;

        public IReadOnlyList<QualityProfile> Ladder { get; }
        public AdaptiveSettings Settings { get; }

        public int GpuLevel => _gpu.Level;
        public int CpuLevel => _cpu.Level;
        /// <summary>Rolling averages (ms); -1 until the first sample counts.</summary>
        public double GpuAverageMs => _gpu.Average;
        public double CpuAverageMs => _cpu.Average;
        /// <summary>Why the last step was taken, for logs and the debug overlay.</summary>
        public string LastReason { get; private set; } = string.Empty;

        /// <summary>The settings in effect: GPU-side values from the GPU rung, CPU-side ones from the CPU rung.</summary>
        public QualityProfile Current => QualityProfiles.Compose(Ladder[_gpu.Level], Ladder[_cpu.Level]);

        public AdaptiveController(IReadOnlyList<QualityProfile> ladder, AdaptiveSettings settings = null, int startLevel = 0)
        {
            if (ladder == null || ladder.Count == 0) throw new ArgumentException("The quality ladder is empty.", nameof(ladder));
            Ladder = ladder;
            Settings = settings ?? new AdaptiveSettings();
            _gpu.RecoverDelay = _cpu.RecoverDelay = Settings.RecoverDelay;
            _gpu.Level = _cpu.Level = Clamp(startLevel);
        }

        private int Worst => Settings.WorstLevel < 0 ? Ladder.Count - 1 : Math.Min(Settings.WorstLevel, Ladder.Count - 1);
        private int Best => Math.Max(0, Math.Min(Settings.BestLevel, Worst));
        private int Clamp(int level) => Math.Max(Best, Math.Min(Worst, level));

        /// <summary>
        /// Ignores samples for <paramref name="seconds"/> from <paramref name="now"/>, e.g. while a world loads, and
        /// starts the averages over afterwards.
        /// </summary>
        public void Hold(double now, float seconds)
        {
            _settleUntil = Math.Max(_settleUntil, now + seconds);
            _gpu.Average = _cpu.Average = -1;
            _collected = 0;
        }

        /// <summary>Moves both rungs to <paramref name="level"/> (clamped to the allowed range), e.g. after the user changes the limits.</summary>
        public void SetLevel(int level, double now)
        {
            _gpu.Level = _cpu.Level = Clamp(level);
            Hold(now, Settings.SettleTime);
        }

        /// <summary>Re-clamps both rungs after <see cref="AdaptiveSettings.BestLevel"/> / <see cref="AdaptiveSettings.WorstLevel"/> changed. Returns true if a rung moved.</summary>
        public bool ApplyLimits()
        {
            int g = Clamp(_gpu.Level), c = Clamp(_cpu.Level);
            bool moved = g != _gpu.Level || c != _cpu.Level;
            _gpu.Level = g;
            _cpu.Level = c;
            return moved;
        }

        /// <summary>Feeds one frame. Returns true when a rung moved; then apply <see cref="Current"/>.</summary>
        public bool Update(FrameSample s)
        {
            if (s.TargetMs <= 0f) return false;
            double dt = double.IsNaN(_lastTime) ? 0 : Math.Max(0, s.Time - _lastTime);
            _lastTime = s.Time;
            if (s.Time < _settleUntil) return false;

            float cap = s.TargetMs * Settings.SampleClamp;
            // A long frame also counts for at most the clamp's length of time, or a single hitch would weigh as much
            // as the whole half second it took.
            dt = Math.Min(dt, cap / 1000.0);
            double alpha = dt <= 0 ? 1 : 1 - Math.Exp(-dt / Math.Max(0.05, Settings.AverageWindow));
            if (s.HasSplit)
            {
                Feed(_gpu, Math.Min(s.GpuMs, cap), alpha);
                Feed(_cpu, Math.Min(s.CpuMs, cap), alpha);
            }
            else
            {
                // Only the total is known: count it against the GPU, which is what limits most VR frames. The CPU rung
                // takes over once the GPU rung has nothing left to give.
                Feed(_gpu, Math.Min(s.GpuMs, cap), alpha);
                _cpu.Average = _gpu.Average;
            }

            // Decide only on a full window of frames, never on the first few.
            _collected += dt;
            if (_collected < Settings.AverageWindow) return false;
            if (s.Time - _lastStep < Settings.MinStepInterval) return false;
            double down = s.TargetMs * Settings.DownThreshold, up = s.TargetMs * Settings.UpThreshold;
            if (!s.HasSplit && s.Paced)
            {
                down = s.TargetMs * Settings.PacedDownThreshold;
                up = s.TargetMs * Settings.PacedUpThreshold;
            }

            // Drop the slower side first. Without a split, the GPU rung goes first, then the CPU rung.
            Side slow = !s.HasSplit ? (_gpu.Level < Worst ? _gpu : _cpu) : (_gpu.Average >= _cpu.Average ? _gpu : _cpu);
            if (slow.Average > down && slow.Level < Worst) return Step(slow, +1, s, slow == _gpu ? "GPU" : "CPU");
            Side other = slow == _gpu ? _cpu : _gpu;
            if (s.HasSplit && other.Average > down && other.Level < Worst) return Step(other, +1, s, other == _gpu ? "GPU" : "CPU");

            if (s.Thermal) return false;
            // Recover the side with the most headroom first. Without a split, the CPU rung comes all the way back before
            // the GPU rung moves (the reverse of the order they went down in).
            Side[] order = !s.HasSplit ? new[] { _cpu.Level > Best ? _cpu : _gpu }
                : (_gpu.Average <= _cpu.Average ? new[] { _gpu, _cpu } : new[] { _cpu, _gpu });
            foreach (Side side in order)
            {
                double avg = s.HasSplit ? side.Average : _gpu.Average;
                // Up one step per recover delay: counted from this side's last step either way.
                if (side.Level > Best && avg >= 0 && avg < up && s.Time - Math.Max(side.LastDown, side.LastUp) >= side.RecoverDelay)
                    return Step(side, -1, s, side == _gpu ? "GPU" : "CPU");
            }
            return false;
        }

        private static void Feed(Side side, double ms, double alpha) =>
            side.Average = side.Average < 0 ? ms : side.Average + alpha * (ms - side.Average);

        private bool Step(Side side, int direction, FrameSample s, string label)
        {
            double avg = side.Average;
            if (direction > 0)
            {
                // Dropping again soon after a recovery means the recovery was one step too far: wait twice as long next time.
                if (s.Time - side.LastUp < side.RecoverDelay)
                    side.RecoverDelay = Math.Min(Settings.MaxRecoverDelay, side.RecoverDelay * 2f);
                side.LastDown = s.Time;
            }
            else
            {
                // A long stable stretch earns back the normal delay.
                if (s.Time - side.LastDown > Settings.MaxRecoverDelay) side.RecoverDelay = Settings.RecoverDelay;
                side.LastUp = s.Time;
            }
            side.Level += direction;
            _lastStep = s.Time;
            _settleUntil = s.Time + Settings.SettleTime;
            _gpu.Average = _cpu.Average = -1;
            _collected = 0;
            LastReason = $"{label} {(direction > 0 ? "down" : "up")} to {Ladder[side.Level].Name}: {avg:0.0} ms average, budget {s.TargetMs:0.0} ms" +
                         (s.HasSplit ? string.Empty : " (total frame time)");
            return true;
        }
    }
}

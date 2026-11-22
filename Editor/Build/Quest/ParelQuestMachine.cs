using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using UnityEditor;
using UnityEngine;

namespace ParelVR.SDK.Build.Quest
{
    /// <summary>How hard the background Meta / Standalone VR build may use this PC.</summary>
    public enum ParelQuestBuildSpeed
    {
        /// <summary>Up to a fifth of the CPU, lowest priority: for laptops, or working while it builds.</summary>
        Light = 0,
        /// <summary>Up to about a third of the CPU (the default).</summary>
        Balanced = 1,
        /// <summary>Up to most of the CPU: quickest, while you aren't using the PC.</summary>
        Fast = 2,
    }

    /// <summary>
    /// Looks at this PC (processors, memory, battery) and at what the rest of the PC is doing, and keeps the background
    /// Android build under a CPU limit that follows it.
    ///
    /// Lower priority and half the processors (1.26.1 - 1.26.3) still let the build and its shader compilers hold a
    /// 6-core PC at 70% (a creator, 11-21): Unity starts one compiler per processor and they all ran flat out. Here the
    /// background Unity is put in a Windows job object with a hard CPU cap. Every process it starts (each shader
    /// compiler, import worker and the Android tools) is in the job too and shares that one cap. Every 2 seconds the
    /// cap is set again from how busy the rest of the PC is: when the creator's editor, a game or anything else needs
    /// the CPU, the build gets less; on battery it is held at the Light limit.
    /// </summary>
    public static class ParelQuestMachine
    {
        private const string SpeedKey = "ParelVR.SDK.QuestBuildSpeed";

        public static ParelQuestBuildSpeed Speed
        {
            get => (ParelQuestBuildSpeed)Mathf.Clamp(EditorPrefs.GetInt(SpeedKey, (int)ParelQuestBuildSpeed.Balanced), 0, 2);
            set => EditorPrefs.SetInt(SpeedKey, (int)value);
        }

        public static readonly string[] SpeedLabels =
        {
            "Light: up to 20% of the CPU (laptops, or working while it builds)",
            "Balanced: up to 35% of the CPU",
            "Fast: up to 70% of the CPU (quickest, while you aren't using the PC)",
        };

        private static int SpeedPercent(ParelQuestBuildSpeed speed) => speed == ParelQuestBuildSpeed.Light ? 20 : speed == ParelQuestBuildSpeed.Fast ? 70 : 35;

        public static int Processors => Environment.ProcessorCount;

        /// <summary>True on a laptop running from its battery.</summary>
        public static bool OnBattery
        {
            get
            {
                if (Application.platform != RuntimePlatform.WindowsEditor) return SystemInfo.batteryStatus == BatteryStatus.Discharging;
                try
                {
                    return GetSystemPowerStatus(out SystemPowerStatus power) && power.ACLineStatus == 0 && power.BatteryFlag != 128;
                }
                catch (Exception) { return false; }
            }
        }

        /// <summary>Free memory in MB, or -1 when it can't be read.</summary>
        public static long FreeMemoryMb
        {
            get
            {
                if (Application.platform != RuntimePlatform.WindowsEditor) return -1;
                try
                {
                    var status = new MemoryStatusEx { dwLength = (uint)Marshal.SizeOf(typeof(MemoryStatusEx)) };
                    return GlobalMemoryStatusEx(ref status) ? (long)(status.ullAvailPhys / (1024 * 1024)) : -1;
                }
                catch (Exception) { return -1; }
            }
        }

        public static long TotalMemoryMb => SystemInfo.systemMemorySize;

        /// <summary>The highest share of the CPU the build may take now, in percent, before the rest of the PC is counted.</summary>
        public static int MaxPercent
        {
            get
            {
                int percent = SpeedPercent(Speed);
                if (OnBattery) percent = Math.Min(percent, SpeedPercent(ParelQuestBuildSpeed.Light));
                // Few processors: the creator's own editor needs a bigger share of them.
                if (Processors <= 4) percent = Math.Min(percent, 30);
                return Math.Max(10, percent);
            }
        }

        /// <summary>Unity's own worker threads: fewer on small or busy PCs (the compilers are capped by the job either way).</summary>
        public static int WorkerThreads => Math.Max(1, Math.Min(Processors / 2, (int)Math.Ceiling(Processors * MaxPercent / 100.0)));

        /// <summary>One line on this PC for the Console at the start of a build.</summary>
        public static string Describe()
        {
            long free = FreeMemoryMb;
            string memory = TotalMemoryMb > 0 ? $"{TotalMemoryMb / 1024.0:0.#} GB memory" + (free >= 0 ? $" ({free / 1024.0:0.#} GB free)" : string.Empty) : "memory unknown";
            string line = $"This PC: {SystemInfo.processorType.Trim()}, {Processors} processors, {memory}" + (OnBattery ? ", on battery" : string.Empty) +
                          $". The background build may use up to {MaxPercent}% of the CPU ({Speed}), less while the rest of the PC is busy.";
            if (free >= 0 && free < 3072) line += " Little memory is free: closing other programs makes the build quicker.";
            return line;
        }

        // ---- The running build ------------------------------------------------------------------------------------

        private static IntPtr _job = IntPtr.Zero;
        private static int _appliedPercent;
        private static long _lastIdle, _lastBusy, _lastJob;
        private static double _othersSmoothed = -1;

        /// <summary>The CPU line for the status ("CPU: build 31% of 35%, rest of the PC 12%"), null when nothing is measured yet.</summary>
        public static string Usage { get; private set; }

        /// <summary>Puts the build's process in a capped job. False when this isn't Windows or Windows refused.</summary>
        public static bool Limit(Process process)
        {
            Release();
            Usage = null;
            _othersSmoothed = -1;
            if (Application.platform != RuntimePlatform.WindowsEditor || process == null) return false;
            try
            {
                _job = CreateJobObject(IntPtr.Zero, null);
                if (_job == IntPtr.Zero) return false;
                if (!AssignProcessToJobObject(_job, process.Handle))
                {
                    Release();
                    return false;
                }
                _appliedPercent = 0;
                SetCap(MaxPercent);
                SampleTimes(out _lastIdle, out _lastBusy, out _lastJob);
                return true;
            }
            catch (Exception)
            {
                Release();
                return false;
            }
        }

        /// <summary>Called about every 2 seconds while the build runs: measures, then sets the cap again.</summary>
        public static void Update()
        {
            if (_job == IntPtr.Zero) return;
            try
            {
                if (!SampleTimes(out long idle, out long busy, out long job)) return;
                long all = (busy - _lastBusy) + (idle - _lastIdle);
                if (all <= 0) return;
                double total = 1.0 - (double)(idle - _lastIdle) / all;
                double ours = Math.Max(0, (double)(job - _lastJob) / all);
                double others = Math.Max(0, total - ours);
                _lastIdle = idle;
                _lastBusy = busy;
                _lastJob = job;
                _othersSmoothed = _othersSmoothed < 0 ? others : _othersSmoothed * 0.6 + others * 0.4;

                // Leave the rest of the PC what it is using plus a fifth of the CPU spare, never below a tenth for the build.
                int max = MaxPercent;
                int cap = (int)Math.Round(Math.Min(max, Math.Max(10, (1.0 - _othersSmoothed - 0.2) * 100)));
                if (Math.Abs(cap - _appliedPercent) >= 3) SetCap(cap);
                Usage = $"CPU: build {ours * 100:0}% (limit {_appliedPercent}%), rest of the PC {others * 100:0}%";
            }
            catch (Exception) { }
        }

        public static void Release()
        {
            if (_job != IntPtr.Zero)
            {
                try { CloseHandle(_job); }
                catch (Exception) { }
            }
            _job = IntPtr.Zero;
        }

        private static void SetCap(int percent)
        {
            percent = Mathf.Clamp(percent, 5, 100);
            var info = new CpuRateControl { ControlFlags = CpuRateEnable | CpuRateHardCap, CpuRate = (uint)(percent * 100) };
            if (SetInformationJobObject(_job, JobObjectCpuRateControlInformation, ref info, Marshal.SizeOf(typeof(CpuRateControl))))
                _appliedPercent = percent;
        }

        private static bool SampleTimes(out long idle, out long busy, out long job)
        {
            idle = busy = job = 0;
            if (!GetSystemTimes(out long sysIdle, out long kernel, out long user)) return false;
            idle = sysIdle;
            busy = kernel - sysIdle + user; // kernel time includes idle time
            if (QueryInformationJobObject(_job, JobObjectBasicAccountingInformation, out BasicAccounting accounting, Marshal.SizeOf(typeof(BasicAccounting)), IntPtr.Zero))
                job = accounting.TotalUserTime + accounting.TotalKernelTime;
            return true;
        }

        // ---- Windows ----------------------------------------------------------------------------------------------

        private const int JobObjectBasicAccountingInformation = 1;
        private const int JobObjectCpuRateControlInformation = 15;
        private const uint CpuRateEnable = 0x1;
        private const uint CpuRateHardCap = 0x4;

        [StructLayout(LayoutKind.Sequential)]
        private struct CpuRateControl
        {
            public uint ControlFlags;
            public uint CpuRate; // cycles per 10,000
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct BasicAccounting
        {
            public long TotalUserTime;
            public long TotalKernelTime;
            public long ThisPeriodTotalUserTime;
            public long ThisPeriodTotalKernelTime;
            public uint TotalPageFaultCount;
            public uint TotalProcesses;
            public uint ActiveProcesses;
            public uint TotalTerminatedProcesses;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct SystemPowerStatus
        {
            public byte ACLineStatus;
            public byte BatteryFlag;
            public byte BatteryLifePercent;
            public byte SystemStatusFlag;
            public int BatteryLifeTime;
            public int BatteryFullLifeTime;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct MemoryStatusEx
        {
            public uint dwLength;
            public uint dwMemoryLoad;
            public ulong ullTotalPhys;
            public ulong ullAvailPhys;
            public ulong ullTotalPageFile;
            public ulong ullAvailPageFile;
            public ulong ullTotalVirtual;
            public ulong ullAvailVirtual;
            public ulong ullAvailExtendedVirtual;
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr CreateJobObject(IntPtr attributes, string name);
        [DllImport("kernel32.dll")] private static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);
        [DllImport("kernel32.dll")] private static extern bool SetInformationJobObject(IntPtr job, int infoClass, ref CpuRateControl info, int length);
        [DllImport("kernel32.dll")] private static extern bool QueryInformationJobObject(IntPtr job, int infoClass, out BasicAccounting info, int length, IntPtr returnLength);
        [DllImport("kernel32.dll")] private static extern bool GetSystemTimes(out long idle, out long kernel, out long user);
        [DllImport("kernel32.dll")] private static extern bool GetSystemPowerStatus(out SystemPowerStatus status);
        [DllImport("kernel32.dll")] private static extern bool GlobalMemoryStatusEx(ref MemoryStatusEx status);
        [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr handle);
    }
}

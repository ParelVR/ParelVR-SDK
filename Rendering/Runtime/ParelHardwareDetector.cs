using System.Collections.Generic;
using UnityEngine;
#if PVRP_XR
using UnityEngine.XR;
#endif

namespace ParelVR.Rendering
{
    /// <summary>What this device is and how fast it has to draw. Read-only: it only asks Unity and the XR runtime.</summary>
    public static class ParelHardwareDetector
    {
        public static HardwareInfo Detect()
        {
            var hw = new HardwareInfo
            {
                Platform = ParelRenderManager.CurrentTarget,
                GpuName = SystemInfo.graphicsDeviceName ?? string.Empty,
                GpuVendor = SystemInfo.graphicsDeviceVendor ?? string.Empty,
                GraphicsApi = SystemInfo.graphicsDeviceType.ToString(),
                GpuMemoryMb = SystemInfo.graphicsMemorySize,
                CpuName = SystemInfo.processorType ?? string.Empty,
                CpuCores = SystemInfo.processorCount,
                CpuFrequencyMhz = SystemInfo.processorFrequency,
                SystemMemoryMb = SystemInfo.systemMemorySize,
                DeviceModel = SystemInfo.deviceModel ?? string.Empty,
                SupportsCompute = SystemInfo.supportsComputeShaders,
                MaxTextureSize = SystemInfo.maxTextureSize,
                RenderWidth = Screen.width,
                RenderHeight = Screen.height,
                RefreshRate = DisplayRefreshRate(),
            };
#if PVRP_XR
            if (XrActive)
            {
                hw.XrDevice = XRSettings.loadedDeviceName ?? string.Empty;
                hw.RenderWidth = XRSettings.eyeTextureWidth;
                hw.RenderHeight = XRSettings.eyeTextureHeight;
                if (TryXrRefreshRate(out float hz)) hw.RefreshRate = hz;
            }
#endif
            return hw;
        }

        /// <summary>True while a headset is rendering.</summary>
        public static bool XrActive
        {
            get
            {
#if PVRP_XR
                return XRSettings.enabled && XRSettings.isDeviceActive;
#else
                return false;
#endif
            }
        }

        /// <summary>
        /// The time one frame may take, in ms: the headset's refresh interval, else the frame-rate cap or V-Sync
        /// interval, else the monitor's refresh interval (an uncapped desktop aims for at least that).
        /// </summary>
        public static float TargetFrameMs()
        {
#if PVRP_XR
            if (XrActive && TryXrRefreshRate(out float xrHz) && xrHz > 1f) return 1000f / xrHz;
#endif
            float hz = DisplayRefreshRate();
            if (QualitySettings.vSyncCount > 0) return 1000f * QualitySettings.vSyncCount / hz;
            if (Application.targetFrameRate > 0) return 1000f / Mathf.Min(Application.targetFrameRate, Mathf.Max(hz, Application.targetFrameRate));
            return 1000f / hz;
        }

        /// <summary>True when frames wait for the display (a headset compositor or V-Sync), so a frame never takes less than the budget.</summary>
        public static bool FramesArePaced => XrActive || QualitySettings.vSyncCount > 0;

        private static float DisplayRefreshRate()
        {
            double hz = Screen.currentResolution.refreshRateRatio.value;
            return hz > 1 ? (float)hz : 60f;
        }

#if PVRP_XR
        private static readonly List<XRDisplaySubsystem> Displays = new List<XRDisplaySubsystem>();

        private static bool TryXrRefreshRate(out float hz)
        {
            hz = 0f;
            Displays.Clear();
            SubsystemManager.GetSubsystems(Displays);
            foreach (XRDisplaySubsystem d in Displays)
                if (d != null && d.running && d.TryGetDisplayRefreshRate(out hz) && hz > 1f) return true;
            return false;
        }
#endif
    }
}

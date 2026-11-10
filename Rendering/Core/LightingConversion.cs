using System;

namespace ParelVR.Rendering
{
    /// <summary>
    /// Physical (HDRP) light values to Built-in ones. HDRP lights are in lux, candela and nits and the camera's exposure
    /// brings them to screen brightness; Built-in values are already screen brightness. Each conversion applies the
    /// exposure the creator's scene used (<see cref="SceneEv100"/>), so the result looks as it did in HDRP.
    /// The formulas follow HDRP's own: exposure multiplier 1 / (1.2 × 2^EV100), illuminance E = 2.5 × 2^EV100 for a
    /// light-meter reading, luminance 2^(EV100 − 3).
    /// </summary>
    public static class LightingConversion
    {
        /// <summary>A typical interior: what is assumed when the scene's exposure is unknown.</summary>
        public const float DefaultEv100 = 9f;

        /// <summary>The exposure of the scene being converted. Upload-time conversion sets it from the scene before converting its materials.</summary>
        public static float SceneEv100 = DefaultEv100;

        public static float ExposureScale(float ev100) => (float)(1.0 / (1.2 * Math.Pow(2.0, ev100)));

        /// <summary>The exposure a light meter would choose for a scene lit by <paramref name="lux"/> (e.g. its sun).</summary>
        public static float Ev100FromIlluminance(float lux) => lux > 0f ? (float)(Math.Log(lux / 2.5, 2.0)) : DefaultEv100;

        public static float NitsFromEv100(float ev100) => (float)Math.Pow(2.0, ev100 - 3.0);

        /// <summary>
        /// The factor HDRP emission ends up multiplied by on screen. <paramref name="exposureWeight"/> is HDRP's "Exposure
        /// Weight": 1 lets the camera exposure apply fully, 0 shows the value as it is.
        /// </summary>
        public static float EmissionScale(float exposureWeight, float ev100)
        {
            float w = exposureWeight < 0f ? 0f : exposureWeight > 1f ? 1f : exposureWeight;
            return 1f + (ExposureScale(ev100) - 1f) * w;
        }

        /// <summary>A directional light: HDRP lux to a Built-in intensity (Built-in's diffuse term has no 1/π).</summary>
        public static float DirectionalIntensity(float lux, float ev100) => (float)(lux / Math.PI) * ExposureScale(ev100);

        /// <summary>
        /// A point or spot light: HDRP candela to a Built-in intensity that gives the same brightness a quarter of the way
        /// out to its range. Built-in falls off as 1 / (1 + 25 (d/r)²) instead of 1/d², so nearer and farther points differ.
        /// </summary>
        public static float PunctualIntensity(float candela, float range, float ev100)
        {
            double d = Math.Max(range * 0.25, 0.01);
            double radiance = candela / (d * d) / Math.PI * ExposureScale(ev100);
            const double builtInFalloffAtQuarter = 1.0 / (1.0 + 25.0 / 16.0);
            return (float)(radiance / builtInFalloffAtQuarter);
        }

        /// <summary>HDRI sky: HDRP multiplier and exposure (EV) to the ParelVR/Skybox exposure value (linear).</summary>
        public static float SkyExposure(float multiplier, float skyEv, float sceneEv100) =>
            (float)(multiplier * Math.Pow(2.0, skyEv)) * ExposureScale(sceneEv100);

        /// <summary>A scale for reflection probes and light probes baked in physical units.</summary>
        public static float BakedScale(float sceneEv100) => ExposureScale(sceneEv100);

        /// <summary>HDRP fog's mean free path (metres) as Built-in exponential fog density: both fall off as e^(−d/path).</summary>
        public static float FogDensity(float meanFreePath) => 1f / Math.Max(meanFreePath, 0.01f);

        /// <summary>Encodes a linear value for a [Gamma] float property (Unity decodes those like colours in a linear project).</summary>
        public static float LinearToGamma(float v) => new ColorValue(v, v, v).LinearToGamma().R;
    }
}

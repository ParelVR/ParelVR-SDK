using System;

namespace ParelVR.Rendering
{
    /// <summary>
    /// The 8 Gerstner waves ParelVR/Water draws for a wind sea (HDRP Ocean / River simulation). Wave heights follow the
    /// Pierson-Moskowitz spectrum: the energy sits around the peak wavelength the wind builds (about 43 m at 26 km/h),
    /// not in the longest wave the surface could hold, so the sea reads as choppy water instead of a flat swell.
    /// </summary>
    public static class WaterSpectrum
    {
        public const int Count = 8;
        private const double G = 9.81;

        /// <summary>Significant wave height (m) of a fully developed sea for a wind of <paramref name="windMs"/> m/s.</summary>
        public static double SignificantHeight(double windMs) => 0.0214 * windMs * windMs;

        /// <summary>The wind (m/s) whose fully developed sea has <paramref name="height"/> m significant wave height.</summary>
        public static double WindForHeight(double height) => Math.Sqrt(Math.Max(height, 0.0) / 0.0214);

        /// <summary>Wavelength (m) the spectrum peaks at for a wind of <paramref name="windMs"/> m/s.</summary>
        public static double PeakWavelength(double windMs)
        {
            double wp = 0.877 * G / Math.Max(windMs, 0.1);
            return 2.0 * Math.PI * G / (wp * wp);
        }

        /// <summary>
        /// Wavelengths and amplitudes (m) for a wind sea. <paramref name="maxLength"/>: the longest wave the surface
        /// allows (from its repetition size). <paramref name="bandLong"/> / <paramref name="bandShort"/>: HDRP's band
        /// multipliers for the longer and shorter half. <paramref name="height"/>: significant wave height (m).
        /// </summary>
        public static void WindSea(double windMs, double height, double maxLength, double bandLong, double bandShort,
                                   double[] lengths, double[] amplitudes)
        {
            double peak = PeakWavelength(windMs);
            double longest = Math.Max(0.5, Math.Min(maxLength, peak * 1.6));
            double sumSq = 0;
            for (int i = 0; i < Count; i++)
            {
                double l = longest * Math.Pow(0.66, i);
                double r = l / peak;
                // PM energy per log-frequency band, as a component amplitude: a ∝ λ · e^(-0.625 (λ/λp)²).
                double a = l * Math.Exp(-0.625 * r * r) * (i < Count / 2 ? bandLong : bandShort);
                lengths[i] = l;
                amplitudes[i] = a;
                sumSq += a * a;
            }
            // Random-phase waves: Hs = 4 σ, σ² = Σ a² / 2.
            double scale = sumSq > 0 ? height / (4.0 * Math.Sqrt(sumSq * 0.5)) : 0;
            for (int i = 0; i < Count; i++) amplitudes[i] *= scale;
        }
    }
}

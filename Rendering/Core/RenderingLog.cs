using System;

namespace ParelVR.Rendering
{
    /// <summary>
    /// Logging for PVRP code that can't see UnityEngine. The runtime assembly points <see cref="Sink"/> at Debug.Log;
    /// tests can point it at a list. Every line is prefixed with [PVRP] so it can be filtered in Player.log.
    /// </summary>
    public static class RenderingLog
    {
        public const string Prefix = "[PVRP] ";

        /// <summary>Where messages go. Null drops them.</summary>
        public static Action<DiagnosticSeverity, string> Sink;

        /// <summary>Messages below this are dropped.</summary>
        public static DiagnosticSeverity MinimumSeverity = DiagnosticSeverity.Notice;

        public static void Write(DiagnosticSeverity severity, string message)
        {
            if (severity < MinimumSeverity) return;
            Sink?.Invoke(severity, Prefix + message);
        }

        public static void Info(string message) => Write(DiagnosticSeverity.Info, message);
        public static void Notice(string message) => Write(DiagnosticSeverity.Notice, message);
        public static void Warning(string message) => Write(DiagnosticSeverity.Warning, message);
        public static void Error(string message) => Write(DiagnosticSeverity.Error, message);
    }
}

using System;
using System.Collections.Generic;
using System.Text;

namespace ParelVR.Rendering
{
    /// <summary>One line of a compatibility report: what PVRP found for one material (or one scene-level setting).</summary>
    public sealed class CompatibilityEntry
    {
        /// <summary>Where it was found, e.g. a renderer path or an asset path.</summary>
        public string Subject = string.Empty;
        public string MaterialName = string.Empty;
        public string ShaderName = string.Empty;
        public ShaderFamily Family;
        public RenderPipelineKind Pipeline;
        public CompatibilityLevel Level;
        public ConversionAction Action;
        public ShaderRole? TargetRole;
        public DiagnosticSeverity Severity;
        public string Message = string.Empty;
        public readonly List<string> Unmapped = new List<string>();
        public readonly List<string> MissingTextures = new List<string>();
    }

    /// <summary>
    /// What PVRP did with a world, avatar or scene, for one target. Written for creators: entries that were handled fine
    /// are counted, not listed, unless <see cref="ToText"/> is asked for the full detail.
    /// </summary>
    public sealed class CompatibilityReport
    {
        public string Title = string.Empty;
        public TargetPlatform Target;
        public RenderPipelineKind DetectedPipeline = RenderPipelineKind.Unknown;
        public readonly List<CompatibilityEntry> Entries = new List<CompatibilityEntry>();
        /// <summary>Scene-level notes (lighting, fog, post-processing...) that aren't about one material.</summary>
        public readonly List<CompatibilityEntry> Notes = new List<CompatibilityEntry>();

        public CompatibilityEntry Add(CompatibilityEntry entry)
        {
            if (entry == null) throw new ArgumentNullException(nameof(entry));
            Entries.Add(entry);
            return entry;
        }

        public CompatibilityEntry Note(DiagnosticSeverity severity, string subject, string message)
        {
            var entry = new CompatibilityEntry { Subject = subject ?? string.Empty, Severity = severity, Message = message ?? string.Empty, Action = ConversionAction.Skipped };
            Notes.Add(entry);
            return entry;
        }

        public int Inspected => Entries.Count;
        public int Count(ConversionAction action) { int n = 0; foreach (var e in Entries) if (e.Action == action) n++; return n; }
        public int Count(DiagnosticSeverity severity) { int n = 0; foreach (var e in Entries) if (e.Severity == severity) n++; foreach (var e in Notes) if (e.Severity == severity) n++; return n; }
        public int MissingTextureCount { get { int n = 0; foreach (var e in Entries) n += e.MissingTextures.Count; return n; } }
        public bool HasErrors => Count(DiagnosticSeverity.Error) > 0;

        /// <summary>The pipeline most materials were made for (Built-in when they all run directly).</summary>
        public RenderPipelineKind MajorityPipeline()
        {
            var counts = new Dictionary<RenderPipelineKind, int>();
            foreach (var e in Entries)
            {
                if (e.Pipeline == RenderPipelineKind.Unknown || e.Pipeline == RenderPipelineKind.Any) continue;
                counts.TryGetValue(e.Pipeline, out int n);
                counts[e.Pipeline] = n + 1;
            }
            RenderPipelineKind best = RenderPipelineKind.Unknown;
            int bestCount = 0;
            foreach (var pair in counts)
                if (pair.Value > bestCount) { best = pair.Key; bestCount = pair.Value; }
            return best;
        }

        /// <summary>Plain-text report. Without <paramref name="detail"/>, materials that were preserved or converted cleanly are only counted.</summary>
        public string ToText(bool detail = false)
        {
            var sb = new StringBuilder();
            sb.Append(string.IsNullOrEmpty(Title) ? "ParelVR rendering report" : Title).Append(" (").Append(Target == TargetPlatform.Pc ? "PC" : "Quest").AppendLine(")");
            sb.Append("Made for: ").AppendLine(PipelineLabel(DetectedPipeline != RenderPipelineKind.Unknown ? DetectedPipeline : MajorityPipeline()));
            sb.Append("Materials checked: ").Append(Inspected)
              .Append(" | kept: ").Append(Count(ConversionAction.Preserved))
              .Append(" | converted: ").Append(Count(ConversionAction.Converted))
              .Append(" | stand-in: ").Append(Count(ConversionAction.Fallback))
              .Append(" | not supported: ").Append(Count(ConversionAction.Unsupported))
              .Append(" | missing textures: ").Append(MissingTextureCount).AppendLine();

            bool any = false;
            foreach (var e in Entries)
            {
                bool clean = e.Severity <= DiagnosticSeverity.Info && e.Unmapped.Count == 0 && e.MissingTextures.Count == 0;
                if (clean && !detail) continue;
                if (!any) { sb.AppendLine(); sb.AppendLine(detail ? "Materials:" : "Needs a look:"); any = true; }
                sb.Append("- [").Append(SeverityLabel(e.Severity)).Append("] ").Append(string.IsNullOrEmpty(e.MaterialName) ? "(unnamed)" : e.MaterialName)
                  .Append(" — ").Append(string.IsNullOrEmpty(e.ShaderName) ? "(no shader)" : e.ShaderName)
                  .Append(" → ").Append(ActionLabel(e));
                if (!string.IsNullOrEmpty(e.Message)) sb.Append(". ").Append(e.Message);
                sb.AppendLine();
                if (e.Unmapped.Count > 0) sb.Append("    not carried over: ").AppendLine(string.Join(", ", e.Unmapped));
                if (e.MissingTextures.Count > 0) sb.Append("    missing textures: ").AppendLine(string.Join(", ", e.MissingTextures));
                if (!string.IsNullOrEmpty(e.Subject)) sb.Append("    at ").AppendLine(e.Subject);
            }

            if (Notes.Count > 0)
            {
                sb.AppendLine().AppendLine("Scene:");
                foreach (var n in Notes)
                    sb.Append("- [").Append(SeverityLabel(n.Severity)).Append("] ").Append(n.Subject).Append(": ").AppendLine(n.Message);
            }
            return sb.ToString();
        }

        public static string PipelineLabel(RenderPipelineKind kind)
        {
            switch (kind)
            {
                case RenderPipelineKind.BuiltIn: return "Built-in Render Pipeline";
                case RenderPipelineKind.Universal: return "Universal Render Pipeline (URP)";
                case RenderPipelineKind.HighDefinition: return "High Definition Render Pipeline (HDRP)";
                case RenderPipelineKind.Any: return "any pipeline";
                default: return "unknown";
            }
        }

        private static string SeverityLabel(DiagnosticSeverity s)
        {
            switch (s)
            {
                case DiagnosticSeverity.Error: return "error";
                case DiagnosticSeverity.Warning: return "warning";
                case DiagnosticSeverity.Notice: return "note";
                default: return "ok";
            }
        }

        private static string ActionLabel(CompatibilityEntry e)
        {
            switch (e.Action)
            {
                case ConversionAction.Preserved: return "kept as is";
                case ConversionAction.Converted: return "converted to ParelVR " + (e.TargetRole?.ToString() ?? "shader");
                case ConversionAction.Fallback: return "stand-in ParelVR " + (e.TargetRole?.ToString() ?? "shader");
                case ConversionAction.Unsupported: return "not supported";
                default: return "skipped";
            }
        }
    }
}

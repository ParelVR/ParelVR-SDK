namespace ParelVR.Rendering
{
    /// <summary>How much PVRP changes content as it loads.</summary>
    public enum RepairPolicy
    {
        /// <summary>Only materials that can't render (missing shader, error shader, unsupported on this GPU).</summary>
        BrokenOnly,
        /// <summary>Broken materials, plus URP and HDRP materials, which the Built-in renderer can't draw. The default.</summary>
        ForeignPipelines,
        /// <summary>Also every convertible Built-in material (e.g. Standard → ParelVR Lit on standalone headsets).</summary>
        ConvertAll,
    }

    /// <summary>What happens to one material when content loads.</summary>
    public enum RepairAction
    {
        Keep,
        /// <summary>Replaced by a ParelVR material: an exact conversion or a stand-in, as the plan decides.</summary>
        Convert,
        /// <summary>Nothing can draw it and no ParelVR shader fits (e.g. an HDRP decal): the renderer is switched off and reported.</summary>
        Hide,
    }

    /// <summary>The load-time decision for one material, kept free of engine types so it can be tested on its own.</summary>
    public static class RuntimeRepair
    {
        /// <param name="broken">The shader is missing, is Unity's error shader, or can't run on this GPU.</param>
        public static RepairAction Decide(ShaderClassification c, bool broken, RepairPolicy policy)
        {
            if (c == null) return RepairAction.Keep;
            broken |= c.Family == ShaderFamily.ErrorShader;
            if (broken) return c.SuggestedRole.HasValue ? RepairAction.Convert : RepairAction.Hide;
            if (c.Level == CompatibilityLevel.Direct || !c.SuggestedRole.HasValue) return RepairAction.Keep;

            bool foreign = c.Pipeline == RenderPipelineKind.Universal || c.Pipeline == RenderPipelineKind.HighDefinition;
            switch (policy)
            {
                case RepairPolicy.ForeignPipelines:
                    return foreign ? RepairAction.Convert : RepairAction.Keep;
                case RepairPolicy.ConvertAll:
                    // A working custom shader stays: its cost is unknown, but replacing it would change the look.
                    return foreign || c.Level == CompatibilityLevel.Convertible ? RepairAction.Convert : RepairAction.Keep;
                default:
                    return RepairAction.Keep;
            }
        }

        /// <summary>The policy PVRP uses when the game doesn't choose one.</summary>
        public static RepairPolicy DefaultPolicy(TargetPlatform target) => RepairPolicy.ForeignPipelines;
    }
}

namespace ParelVR.Rendering
{
    /// <summary>What should happen to one material, decided without touching any engine object.</summary>
    public sealed class ConversionPlan
    {
        public ShaderClassification Classification;
        public ConversionAction Action;
        /// <summary>The ParelVR role to build. Null when the material is kept or nothing fits.</summary>
        public ShaderRole? Role;
        public MaterialDescription Description;
        public CompatibilityEntry Entry;
        public bool NeedsNewMaterial => Role.HasValue && (Action == ConversionAction.Converted || Action == ConversionAction.Fallback);
    }

    /// <summary>
    /// Turns a classification and a material's values into a <see cref="ConversionPlan"/>: keep it, convert it with
    /// a family reader, or give it a stand-in built by <see cref="GenericReader"/>. Also writes the report entry.
    /// </summary>
    public static class MaterialConversionPlanner
    {
        public static ConversionPlan Plan(ShaderClassification c, IMaterialPropertySource source, MaterialMapperRegistry mappers)
        {
            var entry = new CompatibilityEntry
            {
                MaterialName = source.MaterialName ?? string.Empty,
                ShaderName = string.IsNullOrEmpty(source.ShaderName) ? "(missing shader)" : source.ShaderName,
                Family = c.Family,
                Pipeline = c.Pipeline,
                Level = c.Level,
                Message = c.Reason,
            };
            var plan = new ConversionPlan { Classification = c, Entry = entry };

            if (c.Level == CompatibilityLevel.Direct)
            {
                plan.Action = entry.Action = ConversionAction.Preserved;
                entry.Severity = c.NeedsCreatorAction ? DiagnosticSeverity.Warning : DiagnosticSeverity.Info;
                return plan;
            }

            if (!c.SuggestedRole.HasValue)
            {
                plan.Action = entry.Action = ConversionAction.Unsupported;
                entry.Severity = DiagnosticSeverity.Warning;
                return plan;
            }

            IMaterialReader reader = mappers.ReaderFor(c.Family);
            bool exact = c.Level == CompatibilityLevel.Convertible && reader != null && !(reader is GenericReader);
            if (reader == null) reader = new GenericReader();

            MaterialDescription d = reader.Read(source, c.Family);
            d.SourceFamily = c.Family;
            if (string.IsNullOrEmpty(d.SourceShader)) d.SourceShader = source.ShaderName ?? string.Empty;
            if (string.IsNullOrEmpty(d.Name)) d.Name = source.MaterialName ?? string.Empty;

            ShaderRole role = RoleSelector.Select(d, c.SuggestedRole.Value);
            plan.Description = d;
            plan.Role = entry.TargetRole = role;
            plan.Action = entry.Action = exact ? ConversionAction.Converted : ConversionAction.Fallback;
            entry.Unmapped.AddRange(d.UnmappedFeatures);
            if (!exact && c.Level == CompatibilityLevel.Convertible)
                entry.Message = "There is no exact converter for this shader yet, so a stand-in keeps its colour and textures where it can.";
            entry.Severity = !exact ? DiagnosticSeverity.Warning
                : d.UnmappedFeatures.Count > 0 ? DiagnosticSeverity.Notice
                : DiagnosticSeverity.Info;
            return plan;
        }

        /// <summary>The readers and writer PVRP ships with. Specific readers win over the generic one.</summary>
        public static MaterialMapperRegistry CreateDefaultRegistry()
        {
            var registry = new MaterialMapperRegistry();
            registry.Register(new GenericReader());
            registry.Register(new BuiltInLegacyReader());
            registry.Register(new BuiltInUnlitReader());
            registry.Register(new BuiltInParticlesReader());
            registry.Register(new BuiltInStandardReader());
            registry.Register(new SkyboxReader());
            registry.Register(new TerrainReader());
            registry.Register(new UrpLitReader());
            registry.Register(new UrpSimpleLitReader());
            registry.Register(new UrpUnlitReader());
            registry.Register(new UrpParticlesReader());
            registry.Register(new HdrpLitReader());
            registry.Register(new HdrpLayeredLitReader());
            registry.Register(new HdrpUnlitReader());
            registry.Register(new ParelMaterialWriter());
            registry.Register(new SkyboxWriter());
            registry.Register(new TerrainWriter());
            return registry;
        }
    }
}

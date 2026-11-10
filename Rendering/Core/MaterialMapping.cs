using System;
using System.Collections.Generic;

namespace ParelVR.Rendering
{
    /// <summary>Read access to one material's values, by shader property name. Implemented over UnityEngine.Material at runtime and over saved properties in the Editor.</summary>
    public interface IMaterialPropertySource
    {
        string MaterialName { get; }
        string ShaderName { get; }
        /// <summary>The material's own queue (its override, or the shader's default).</summary>
        int RenderQueue { get; }
        bool HasProperty(string name);
        float GetFloat(string name);
        ColorValue GetColor(string name);
        /// <summary>Null when the property doesn't exist; a slot with no texture when it exists but is empty.</summary>
        TextureSlot GetTexture(string name);
        bool IsKeywordEnabled(string keyword);
        /// <summary>A material override tag such as "RenderType"; empty when not set.</summary>
        string GetTag(string tag);
    }

    /// <summary>Write access to the new material. Setting a property the target shader doesn't have is a no-op.</summary>
    public interface IMaterialPropertySink
    {
        bool HasProperty(string name);
        void SetFloat(string name, float value);
        void SetColor(string name, ColorValue value);
        void SetTexture(string name, TextureSlot slot);
        void SetKeyword(string keyword, bool enabled);
        void SetRenderQueue(int queue);
        void SetOverrideTag(string tag, string value);
    }

    /// <summary>Turns materials of some shader families into a <see cref="MaterialDescription"/>.</summary>
    public interface IMaterialReader
    {
        bool CanRead(ShaderFamily family);
        MaterialDescription Read(IMaterialPropertySource source, ShaderFamily family);
    }

    /// <summary>Turns a <see cref="MaterialDescription"/> into the properties of a ParelVR shader for one role.</summary>
    public interface IMaterialWriter
    {
        bool CanWrite(ShaderRole role);
        void Write(MaterialDescription description, ShaderRole role, TargetPlatform target, IMaterialPropertySink sink);
    }

    /// <summary>The readers and writers PVRP uses. New families are added by registering a reader, not by editing the converter.</summary>
    public sealed class MaterialMapperRegistry
    {
        private readonly List<IMaterialReader> _readers = new List<IMaterialReader>();
        private readonly List<IMaterialWriter> _writers = new List<IMaterialWriter>();

        public IReadOnlyList<IMaterialReader> Readers => _readers;
        public IReadOnlyList<IMaterialWriter> Writers => _writers;

        /// <summary>Later registrations win, so a project can override a built-in reader.</summary>
        public void Register(IMaterialReader reader)
        {
            if (reader == null) throw new ArgumentNullException(nameof(reader));
            _readers.Insert(0, reader);
        }

        public void Register(IMaterialWriter writer)
        {
            if (writer == null) throw new ArgumentNullException(nameof(writer));
            _writers.Insert(0, writer);
        }

        public IMaterialReader ReaderFor(ShaderFamily family)
        {
            foreach (IMaterialReader reader in _readers)
                if (reader.CanRead(family)) return reader;
            return null;
        }

        public IMaterialWriter WriterFor(ShaderRole role)
        {
            foreach (IMaterialWriter writer in _writers)
                if (writer.CanWrite(role)) return writer;
            return null;
        }

        public bool TryRead(IMaterialPropertySource source, ShaderFamily family, out MaterialDescription description)
        {
            IMaterialReader reader = ReaderFor(family);
            description = reader?.Read(source, family);
            if (description != null)
            {
                description.SourceFamily = family;
                if (string.IsNullOrEmpty(description.SourceShader)) description.SourceShader = source.ShaderName ?? string.Empty;
                if (string.IsNullOrEmpty(description.Name)) description.Name = source.MaterialName ?? string.Empty;
            }
            return description != null;
        }
    }
}

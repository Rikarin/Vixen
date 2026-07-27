// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core;
using Vixen.Core.Yaml.Meta;

namespace Vixen.Core.Yaml.Tests;

public enum ColorSpace {
    Linear,
    Srgb
}

public enum TextureUsage {
    Albedo,
    Normal,
    Mask,
    Hdr,
    Ui
}

public enum TextureCompression {
    None,
    Bc7,
    Astc6X6,
    Etc2Rgba
}

public enum WrapMode {
    Repeat,
    Clamp,
    Mirror
}

[DataContract]
public sealed record WrapModes {
    public WrapMode U { get; init; } = WrapMode.Repeat;
    public WrapMode V { get; init; } = WrapMode.Repeat;
}

/// <summary>A sparse per-target patch. Only the keys present are applied over the base settings.</summary>
[DataContract]
public sealed record TargetOverride {
    public string Target { get; init; } = string.Empty;
    public TextureCompression? Compression { get; init; }
    public int? MaxSize { get; init; }
}

[DataContract("TextureImporter")]
public sealed record TextureImportSettings : IImportSettings {
    public int Version { get; init; } = 3;
    public string SourceHash { get; init; } = string.Empty;
    public ColorSpace ColorSpace { get; init; } = ColorSpace.Srgb;
    public TextureUsage Usage { get; init; } = TextureUsage.Albedo;
    public bool GenerateMips { get; init; } = true;
    public WrapModes Wrap { get; init; } = new();
    public int Anisotropy { get; init; } = 8;
    public int MaxSize { get; init; } = 2048;
    public TextureCompression Compression { get; init; } = TextureCompression.Bc7;
    public float Quality { get; init; } = 0.85f;
    public bool Streaming { get; init; } = true;

    /// <summary>
    ///     A <c>T[]</c> rather than doc 08's <c>ImmutableArray&lt;T&gt;</c> — see the note in
    ///     <c>CollectionFactory</c>. In an init-only record it is just as immutable, and it is the
    ///     one an AOT build can construct.
    /// </summary>
    public TargetOverride[] Overrides { get; init; } = [];
}

[DataContract("ModelImporter")]
public sealed record ModelImportSettings : IImportSettings {
    public int Version { get; init; } = 5;
    public float Scale { get; init; } = 1f;
    public bool ImportAnimations { get; init; } = true;
    public Dictionary<string, string> MaterialMapping { get; init; } = [];
    public List<string> Lods { get; init; } = [];
}

/// <summary>The envelope, so that polymorphism is tested where doc 08 actually uses it.</summary>
[DataContract]
public sealed record AssetMetaFixture {
    public Guid Guid { get; init; }
    public int MetaVersion { get; init; } = 1;
    public IImportSettings? Importer { get; init; }
    public string? Address { get; init; }
    public string[] Labels { get; init; } = [];
}

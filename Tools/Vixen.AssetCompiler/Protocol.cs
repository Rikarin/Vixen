// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Buffers.Binary;
using Vixen.Core;
using Vixen.Core.Serialization;

namespace Vixen.AssetCompiler;

/// <summary>One import, on the wire.</summary>
/// <remarks>
///     A flat mirror of <c>ImportJob</c> rather than the type itself. That one holds a
///     <c>VirtualPath</c> and an <c>AssetId</c>, which are engine identity structs and could be
///     serialised; the mirror exists so the wire format is a thing this file defines completely and
///     a change to a domain type is not silently a protocol break.
/// </remarks>
[DataContract("VixenImportRequest")]
public sealed record ImportRequestMessage {
    /// <summary>Which asset, as 32 hex digits.</summary>
    public string Guid { get; set; } = string.Empty;

    /// <summary>Which importer, by name.</summary>
    public string Importer { get; set; } = string.Empty;

    /// <summary>Where its source is, as a virtual path.</summary>
    public string Source { get; set; } = string.Empty;

    /// <summary>Its settings, as YAML with the per-target overrides already resolved.</summary>
    public string Settings { get; set; } = string.Empty;

    /// <summary>Which build target.</summary>
    public string Target { get; set; } = string.Empty;

    /// <summary>Whether an undeclared read fails the import.</summary>
    public bool EnforceDeclaredReads { get; set; } = true;
}

/// <summary>One chunk an import produced, on the wire.</summary>
[DataContract("VixenImportArtifact")]
public sealed record ArtifactMessage {
    /// <summary>Which sub-asset it is, as eight hex digits, or empty for the main object.</summary>
    public string SubAsset { get; set; } = string.Empty;

    /// <summary>What kind of thing it is.</summary>
    public string Type { get; set; } = string.Empty;

    /// <summary>Its bytes.</summary>
    public byte[] Content { get; set; } = [];
}

/// <summary>One sub-asset an import declared, on the wire.</summary>
[DataContract("VixenImportSubAsset")]
public sealed record SubAssetMessage {
    /// <summary>Its id, as eight hex digits.</summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>What it is called.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>What kind of thing it is.</summary>
    public string Type { get; set; } = string.Empty;
}

/// <summary>One thing an importer said, on the wire.</summary>
[DataContract("VixenImportDiagnostic")]
public sealed record DiagnosticMessage {
    /// <summary>How much attention it needs, as the enum's value.</summary>
    public int Severity { get; set; }

    /// <summary>What it says.</summary>
    public string Message { get; set; } = string.Empty;
}

/// <summary>What one import produced, on the wire.</summary>
[DataContract("VixenImportResponse")]
public sealed record ImportResponseMessage {
    /// <summary>Whether it came out with no errors.</summary>
    public bool Succeeded { get; set; }

    /// <summary>The chunks.</summary>
    public ArtifactMessage[] Artifacts { get; set; } = [];

    /// <summary>What the asset now declares it contains.</summary>
    public SubAssetMessage[] SubAssets { get; set; } = [];

    /// <summary>Everything the importer said.</summary>
    public DiagnosticMessage[] Diagnostics { get; set; } = [];

    /// <summary>Every file it declared, including its own source.</summary>
    public string[] FileDependencies { get; set; } = [];

    /// <summary>Every other asset it declared, as 32 hex digits each.</summary>
    public string[] AssetDependencies { get; set; } = [];
}

// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

namespace Vixen.Core.Yaml.Meta;

/// <summary>Settings for one importer, selected by the <c>.meta</c> file's type tag.</summary>
/// <remarks>
///     A <c>[DataContract]</c> name on the implementing record is the tag: one attribute defines the
///     <c>.meta</c> tag, the settings type and the serializer, with no registration table to keep in
///     sync.
/// </remarks>
public interface IImportSettings {
    /// <summary>
    ///     The importer's own version. Bumping it invalidates every artefact that importer produced.
    /// </summary>
    int Version { get; }
}

/// <summary>Where an asset appears in a shipped build, if it does.</summary>
/// <remarks>
///     <para>
///         The per-asset facts live here, in the <c>.meta</c>; the group *policy* — compression,
///         packing, local or remote — lives in a <c>.vxgroup</c> asset. Unity splits this the other
///         way and gets a second identity system, a second source of merge conflicts, and the "asset
///         is in two groups" state for its trouble.
///     </para>
///     <para>
///         Properties rather than a positional record, here and throughout the model. A positional
///         record has only its positional constructor, and a binder reading a file has no arguments
///         to pass it — the shape a deserializer needs is the one doc 08 writes anyway.
///     </para>
/// </remarks>
[DataContract]
public sealed record AddressableInfo {
    /// <summary>What the game asks for it by. <see langword="null" /> means it is not shipped.</summary>
    public string? Address { get; init; }

    /// <summary>Which bundle group it belongs to, or <see langword="null" /> to inherit the folder's.</summary>
    public string? Group { get; init; }

    /// <summary>Labels for bulk loading.</summary>
    public string[] Labels { get; init; } = [];
}

/// <summary>One object inside an imported asset.</summary>
[DataContract]
public sealed record SubAssetEntry {
    /// <summary>Its stable id, derived from what it is rather than from where it landed.</summary>
    public SubAssetId Id { get; init; }

    /// <summary>What it is called.</summary>
    public string Name { get; init; } = string.Empty;

    /// <summary>What kind of thing it is — <c>Mesh</c>, <c>AnimationClip</c>, and so on.</summary>
    public string Type { get; init; } = string.Empty;
}

/// <summary>The whole of a <c>.meta</c> sidecar.</summary>
/// <remarks>
///     <para>
///         The pattern is Unity's, unchanged and adopted deliberately (ADR-005): one sidecar per
///         imported file, one per folder, committed next to the asset, and <b>the GUID is the
///         identity</b> — generated on first import, never regenerated, path-independent, so moving
///         or renaming a file breaks no reference anywhere. The schema inside is Vixen's.
///     </para>
///     <para>
///         Key order here is the order it is written in, and the envelope — <see cref="Guid" />,
///         <see cref="MetaVersion" />, <see cref="Importer" /> — comes first on purpose. That is what
///         lets the index rebuild read three lines of a file and stop; see <see cref="MetaScanner" />.
///     </para>
/// </remarks>
[DataContract]
public sealed record AssetMeta {
    /// <summary>The identity. Assigned once, never rewritten.</summary>
    public AssetId Guid { get; init; }

    /// <summary>Which version of this envelope schema the file was written against.</summary>
    public int MetaVersion { get; init; } = MetaMigrationChain.CurrentVersion;

    /// <summary>Which importer made it, and how it was configured.</summary>
    public IImportSettings? Importer { get; init; }

    /// <summary>Where it appears in a shipped build, if it does.</summary>
    public AddressableInfo? Addressable { get; init; }

    /// <summary>What is inside it.</summary>
    public SubAssetEntry[] SubAssets { get; init; } = [];

    /// <summary>
    ///     User and plugin metadata, keyed by name — the typed replacement for Unity's untyped
    ///     <c>userData</c> string.
    /// </summary>
    public Dictionary<string, string> Extensions { get; init; } = [];
}

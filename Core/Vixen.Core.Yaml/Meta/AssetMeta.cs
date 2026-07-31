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

    /// <summary>
    ///     What the author renamed a sub-asset to, keyed by the name the <i>source file</i> gives it.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>An importer setting rather than a sidecar edit, and it has to be.</b> Every import
    ///         rewrites the <c>subAssets:</c> block from what the importer declared, so a name typed
    ///         there would be thrown away by the next re-import. This is the author's input; that
    ///         block is the import's output.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Keyed by the source's name, not by the one the last import settled on.</b> A
    ///         <c>.glb</c> with two meshes called <c>Cube</c> imports them as <c>Cube</c> and
    ///         <c>Cube_1</c>; keying by that would mean a re-export that added a third shuffled which
    ///         rename landed where. Renaming both of two identically-named sub-assets is therefore
    ///         not expressible, which is the honest answer — nothing in the file tells them apart.
    ///     </para>
    ///     <para>
    ///         Empty for every importer that does not offer renaming, which is a default rather than a
    ///         requirement: an importer opts in by declaring the property, and the ones whose
    ///         sub-assets are named by something the author already controls — a sprite in the
    ///         slicer, a video's single track — have nothing to gain from it.
    ///     </para>
    /// </remarks>
    IReadOnlyList<SubAssetRename> SubAssetNames => [];
}

/// <summary>What the author wants one of an asset's parts called.</summary>
/// <remarks>
///     <para>
///         ⚠ <b>A list of pairs rather than a map, and the reason is that somebody has to be able to
///         edit it.</b> The inspector has a drawer for a list and none for a dictionary — deliberately,
///         because a dictionary is two columns rather than a column of values — so a map would be a
///         setting only a text editor could change, which is the state this exists to get out of.
///         It also diffs and merges the way the rest of a <c>.meta</c> does.
///     </para>
///     <para>
///         ⚠ <b>Keyed by what the <i>source file</i> calls a thing.</b> A <c>.glb</c> with two meshes
///         called <c>Cube</c> imports them as <c>Cube</c> and <c>Cube_1</c>; keying by the second of
///         those would mean a re-export that added a third shuffled which rename landed where. The
///         cost is that two parts the file cannot tell apart cannot be renamed apart either, which is
///         the honest answer rather than a limitation.
///     </para>
/// </remarks>
[DataContract]
public sealed record SubAssetRename {
    /// <summary>What the source file calls it.</summary>
    public string Source { get; set; } = string.Empty;

    /// <summary>What it should be addressed as instead.</summary>
    public string Name { get; set; } = string.Empty;
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
    /// <summary>
    ///     What the game asks for it by, or <see langword="null" /> for its project-relative path.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>Null means "the path", not "not shipped".</b> An asset's location is already
    ///         unique, already meaningful and already what the author sees in the Project panel, so
    ///         <c>Assets/Textures/Crate.png</c> is an address every asset can have without anybody
    ///         typing one — and a project where every address has to be set by hand is a project
    ///         where most of them never are, which shows up as a load that fails on a device.
    ///     </para>
    ///     <para>
    ///         Setting one overrides the path. That is worth doing where the address is a contract —
    ///         a level a save game names, a DLC pack a URL is built from — because it survives the
    ///         file being moved, which the default by definition does not.
    ///     </para>
    ///     <para>
    ///         <see cref="Excluded" /> is how something is kept out of a build. It has to be a
    ///         separate flag now that an absent address means the default rather than an absence.
    ///     </para>
    /// </remarks>
    public string? Address { get; init; }

    /// <summary>Which bundle group it belongs to, or <see langword="null" /> to inherit the folder's.</summary>
    public string? Group { get; init; }

    /// <summary>Labels for bulk loading.</summary>
    public string[] Labels { get; init; } = [];

    /// <summary>Whether to keep it out of the build entirely.</summary>
    /// <remarks>
    ///     ⚠ <b>Not the same as having no address, and not the same as being unused.</b> An excluded
    ///     asset is in no bundle and reachable by nothing — so anything addressable that depends on it
    ///     fails the build, by the same rule that catches a dependency with no address. What this is
    ///     for is the source file nothing loads at run time: a layered PSD a texture was flattened
    ///     from, a reference FBX kept beside the one that ships.
    /// </remarks>
    public bool Excluded { get; init; }
}

/// <summary>One object inside an imported asset.</summary>
[DataContract]
public sealed record SubAssetEntry {
    /// <summary>Its stable id, derived from what it is rather than from where it landed.</summary>
    public SubAssetId Id { get; init; }

    /// <summary>What it is called, after any rename and any suffix a collision forced.</summary>
    public string Name { get; init; } = string.Empty;

    /// <summary>What kind of thing it is — <c>Mesh</c>, <c>AnimationClip</c>, and so on.</summary>
    public string Type { get; init; } = string.Empty;

    /// <summary>What the source file called it, when that is not <see cref="Name" />.</summary>
    /// <remarks>
    ///     ⚠ <b>Recorded so that renaming has something to key on.</b>
    ///     <see cref="IImportSettings.SubAssetNames" /> maps a source name to a chosen one, and an
    ///     inspector offering that rename has to be able to show the author which source name a row
    ///     stands for — otherwise the only way to rename <c>Cube_1</c> is to know that the file calls
    ///     it <c>Cube</c> and that it was the second one.
    ///     <para>
    ///         Empty when the two agree, which is almost always, so it costs nothing in the sidecar
    ///         of an asset whose names were already distinct.
    ///     </para>
    /// </remarks>
    public string Source { get; init; } = string.Empty;
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

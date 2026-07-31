// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

namespace Vixen.Core;

/// <summary>
///     Says what kind of asset an <see cref="AssetId" /> or <see cref="AssetReference" /> member is
///     allowed to name, so that a picker offers only those and a drag carrying anything else is
///     refused.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>Here rather than in the editor, and that is the whole point of it.</b> The editor has
///         had an asset-picker attribute since the inspector was written, but a component in
///         <c>Vixen.Audio</c> or in somebody's game cannot carry it — annotating a runtime type for
///         the editor would be a runtime assembly referencing an editor one, which is the coupling
///         doc 11's layering exists to prevent. So a runtime member had no way to say what it holds,
///         and the answer the inspector fell back on was "any asset in the project", which for a clip
///         member is a list with every texture and every scene in it.
///     </para>
///     <para>
///         ⚠ <b>A CLR type rather than a string kind.</b> <c>[AssetType("audio")]</c> would need this
///         assembly to define a vocabulary of asset kinds, and the set of kinds is the set of
///         <i>importers</i> — which live in the editor and which a plugin adds to. A type is a name
///         both ends already have: the member says <c>AudioClip</c> because that is what it resolves
///         to at run time, and joining that to whichever importer produces one is the editor's job
///         and stays there.
///     </para>
///     <para>
///         Advisory, exactly as <see cref="RangeAttribute" /> is: nothing at run time refuses an id
///         of the wrong kind, because an id is sixteen bytes and knows nothing about what wrote it.
///         This is what a person editing the value is offered.
///     </para>
/// </remarks>
/// <param name="assetType">What the member resolves to — <c>typeof(AudioClip)</c>, <c>typeof(MeshData)</c>.</param>
[AttributeUsage(AttributeTargets.Field | AttributeTargets.Property)]
public sealed class AssetTypeAttribute(Type assetType) : Attribute {
    /// <summary>What the member resolves to.</summary>
    public Type AssetType { get; } = assetType ?? throw new ArgumentNullException(nameof(assetType));

    /// <summary>Whether the member may hold nothing at all.</summary>
    /// <remarks>
    ///     On by default, because most references are optional — a mesh with no material draws with
    ///     the renderer's default, which <c>MeshRenderable.Material</c>'s own remarks call a usable
    ///     value rather than a mistake. Off is what removes the picker's "None" button, and a field
    ///     that cannot be null offering one is a button that either fails silently or writes
    ///     something the type forbids.
    /// </remarks>
    public bool AllowNull { get; set; } = true;
}

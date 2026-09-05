// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core;
using Vixen.Editor.Assets.Textures;

namespace Vixen.Editor.Assets.MeshMaps;

/// <summary>One baked mesh map, as the file it is about to become.</summary>
/// <remarks>
///     <para>
///         <b>The step § D12 leaves to the caller, done once.</b> <c>MapBaker</c> returns arrays of
///         <c>float</c> and <c>Vector3</c> and its remarks are explicit that writing them is
///         somebody else's job, because <c>Core/</c> is under the virtual-path rule. This is what
///         that job produces: the bytes, what to call them, and what the sidecar beside them has to
///         say so that the file means the same thing when it is read back.
///     </para>
///     <para>
///         ⚠ <b>Bytes rather than a path.</b> Encoding and writing are separated so that the whole
///         of the encoding — the row flip, the signed remaps, the settings each usage needs — is
///         testable without a project, a database or a disk, which is most of what can be wrong here.
///     </para>
/// </remarks>
public sealed record MeshMapImage {
    /// <summary>What this map measures.</summary>
    public required MeshMapUsage Usage { get; init; }

    /// <summary>What to call it, with its extension and no directory.</summary>
    public required string FileName { get; init; }

    /// <summary>The PNG.</summary>
    public required byte[] Png { get; init; }

    /// <summary>How the texture importer must read it.</summary>
    public required TextureImportSettings Settings { get; init; }

    /// <summary>What an encoded value is multiplied by, or zero where the map is not quantized.</summary>
    /// <remarks>
    ///     Only <see cref="MeshMapUsage.Displacement" /> and <see cref="MeshMapUsage.Curvature" />
    ///     carry one. See <see cref="MeshMapNaming.ScaleKey" /> for what a reader does with it.
    /// </remarks>
    public float Scale { get; init; }
}

/// <summary>Every map one bake produced, and what each one became in the project.</summary>
/// <param name="Mesh">The mesh's name, which is the stem of every file in the set.</param>
/// <param name="Maps">What each usage became, by usage.</param>
/// <param name="Files">Where each one was written, as full paths, in the order they were written.</param>
/// <param name="Warnings">What the bake could not do, carried straight through from <c>BakedMaps</c>.</param>
/// <remarks>
///     ⚠ <b>A reference per usage, because binding is by usage.</b> A caller that wanted a path
///     would be a caller that breaks the day somebody moves the folder; <see cref="Files" /> is for
///     a status line and a test, and <see cref="Maps" /> is what a generator resolves through.
/// </remarks>
public sealed record MeshMapSet(
    string Mesh,
    IReadOnlyDictionary<MeshMapUsage, AssetReference> Maps,
    IReadOnlyList<string> Files,
    IReadOnlyList<string> Warnings
);

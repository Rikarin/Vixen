// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Diagnostics.CodeAnalysis;

namespace Vixen.Editor.Assets.MeshMaps;

/// <summary>What one baked mesh map measures, which is how a generator finds it.</summary>
/// <remarks>
///     <para>
///         <b>docs/plan/48 § 4.8: a Mesh Map Input binds <i>by usage</i>.</b> That is what makes one
///         generator compound work on every mesh — a Curvature Edge Wear graph asks for
///         <see cref="Curvature" /> and gets whichever file this project's bake produced, without
///         naming it. So the usage is the identity of a mesh map and the file name is a convenience,
///         which is the same bargain the asset database makes between a GUID and a path.
///     </para>
///     <para>
///         One member per row of § D12's table, plus the two the bake has always returned. It is
///         deliberately <i>not</i> <c>Vixen.Geometry.Remeshing.MeshMaps</c>: that is a flags enum
///         saying which measurements to spend rays on, and a usage is a single value naming one
///         file. Folding them together would make "bake AO and thickness" and "this file is the AO
///         map" the same type, and the second one has to be one value or a look-up by it is
///         meaningless.
///     </para>
/// </remarks>
public enum MeshMapUsage {
    /// <summary>The tangent- or object-space normal map. Always baked.</summary>
    Normal,

    /// <summary>The signed displacement along the target's normal. Always baked.</summary>
    Displacement,

    /// <summary>The unoccluded fraction of the hemisphere.</summary>
    AmbientOcclusion,

    /// <summary>The average unoccluded direction.</summary>
    BentNormal,

    /// <summary>Mean curvature: positive on an edge, negative in a crease.</summary>
    Curvature,

    /// <summary>How enclosed the inside is.</summary>
    Thickness,

    /// <summary>The surface point, normalised into the source's bounding box.</summary>
    Position,

    /// <summary>The source's own normal, unrotated.</summary>
    WorldNormal,

    /// <summary>The source's face group as a distinct colour.</summary>
    Id
}

/// <summary>What a baked mesh map is called, and how something later finds it again.</summary>
/// <remarks>
///     <para>
///         <b>Two answers to "which map is this", and they are not the same answer.</b> The file
///         name is what an artist reads in the browser — § D12 is explicit that these are ordinary
///         assets an artist opens when a generator misbehaves, not a hidden cache — and
///         <see cref="UsageKey" /> in the sidecar is what a generator resolves. The sidecar wins: a
///         file somebody renamed still knows what it measures, and doc 08's whole argument is that a
///         path is a fact about today.
///     </para>
///     <para>
///         ⚠ <b>The suffixes are the vocabulary M8's generators are written against</b>, so they are
///         fixed here rather than composed at each call site. Renaming one silently unbinds every
///         shipped generator that asks for it, which is why they are short, lower-case and boring:
///         <c>ao</c> rather than <c>ambientOcclusion</c>, <c>height</c> rather than
///         <c>displacement</c>, matching what an artist arriving from Painter or InstaMAT already
///         types.
///     </para>
/// </remarks>
public static class MeshMapNaming {
    /// <summary>Which folder under <c>Assets/</c> a bake writes into by default.</summary>
    public const string DefaultFolder = "MeshMaps";

    /// <summary>The extension a baked map is written with.</summary>
    /// <remarks>
    ///     PNG, because <c>Vixen.Core.Imaging</c>'s encoder sits under everything in this repository
    ///     that writes a picture and every file browser on every platform opens one. ⚠ It is eight
    ///     bits a channel, so <see cref="ScaleKey" /> is how the two signed maps get their range back
    ///     — a sixteen-bit container is owed and the sidecar's scale is what makes adding one a
    ///     change to this file rather than to every reader.
    /// </remarks>
    public const string Extension = ".png";

    /// <summary>The sidecar extension key naming what a map measures.</summary>
    /// <remarks>The authoritative binding. Its value is the usage's <see cref="Suffix" />.</remarks>
    public const string UsageKey = "meshMap.usage";

    /// <summary>The sidecar extension key naming the mesh the set was baked from.</summary>
    public const string MeshKey = "meshMap.mesh";

    /// <summary>The sidecar extension key holding what an encoded value is multiplied by.</summary>
    /// <remarks>
    ///     ⚠ <b>Present only on the two signed maps</b>, <see cref="MeshMapUsage.Displacement" /> and
    ///     <see cref="MeshMapUsage.Curvature" />. Both are stored as <c>0.5 + 0.5·v/range</c>, so a
    ///     reader recovers <c>v</c> as <c>(sample·2 − 1)·scale</c>. Written beside the pixels because
    ///     a quantized measurement with its scale somewhere else is half of the ways this goes wrong
    ///     — <c>BakedMaps.DisplacementRange</c>'s remarks say so on the other side of the seam.
    /// </remarks>
    public const string ScaleKey = "meshMap.scale";

    /// <summary>The suffix a usage's file name ends in.</summary>
    /// <param name="usage">The usage.</param>
    /// <returns>The suffix, with no separator and no dot.</returns>
    /// <exception cref="ArgumentOutOfRangeException">It is not a usage.</exception>
    public static string Suffix(MeshMapUsage usage) => usage switch {
        MeshMapUsage.Normal => "normal",
        MeshMapUsage.Displacement => "height",
        MeshMapUsage.AmbientOcclusion => "ao",
        MeshMapUsage.BentNormal => "bent",
        MeshMapUsage.Curvature => "curvature",
        MeshMapUsage.Thickness => "thickness",
        MeshMapUsage.Position => "position",
        MeshMapUsage.WorldNormal => "world",
        MeshMapUsage.Id => "id",
        _ => throw new ArgumentOutOfRangeException(nameof(usage), usage, "There is no such mesh map.")
    };

    /// <summary>The usage a suffix names.</summary>
    /// <param name="suffix">The suffix, without a separator.</param>
    /// <param name="usage">What it names.</param>
    /// <returns>Whether it names one.</returns>
    /// <remarks>Ordinal and case-sensitive: these are written by a bake, not typed by a person.</remarks>
    public static bool TryParseSuffix(string? suffix, out MeshMapUsage usage) {
        foreach (var candidate in Every) {
            if (string.Equals(Suffix(candidate), suffix, StringComparison.Ordinal)) {
                usage = candidate;
                return true;
            }
        }

        usage = default;
        return false;
    }

    /// <summary>Every usage, in the order a bake writes them.</summary>
    public static IReadOnlyList<MeshMapUsage> Every { get; } = Enum.GetValues<MeshMapUsage>();

    /// <summary>What one map of a mesh's set is called.</summary>
    /// <param name="mesh">The mesh's name, already safe for a file name.</param>
    /// <param name="usage">What the map measures.</param>
    /// <returns>The file name, with its extension.</returns>
    /// <exception cref="ArgumentException">The mesh name is null or empty.</exception>
    public static string FileName(string mesh, MeshMapUsage usage) {
        ArgumentException.ThrowIfNullOrEmpty(mesh);
        return mesh + "_" + Suffix(usage) + Extension;
    }

    /// <summary>Reads a file name back into the mesh and the usage it names.</summary>
    /// <param name="fileName">The file name, with or without a directory in front of it.</param>
    /// <param name="mesh">The mesh's name.</param>
    /// <param name="usage">What the map measures.</param>
    /// <returns>Whether it is one of ours.</returns>
    /// <remarks>
    ///     ⚠ <b>The last underscore, not the first.</b> <c>Old_Barrel_ao.png</c> is a map of
    ///     <c>Old_Barrel</c>, and splitting at the first separator would call it a map of <c>Old</c>
    ///     with an unknown usage — which is a set that silently loses half its members the day
    ///     somebody names a mesh with two words.
    /// </remarks>
    public static bool TryParseFileName(
        string? fileName,
        [NotNullWhen(true)] out string? mesh,
        out MeshMapUsage usage
    ) {
        mesh = null;
        usage = default;

        if (string.IsNullOrEmpty(fileName)) {
            return false;
        }

        var name = Path.GetFileName(fileName);

        if (!name.EndsWith(Extension, StringComparison.OrdinalIgnoreCase)) {
            return false;
        }

        var stem = name[..^Extension.Length];
        var split = stem.LastIndexOf('_');

        if (split <= 0 || !TryParseSuffix(stem[(split + 1)..], out usage)) {
            return false;
        }

        mesh = stem[..split];
        return true;
    }
}

// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Diagnostics.CodeAnalysis;
using Vixen.Core;

namespace Vixen.Editor.Assets.MeshMaps;

/// <summary>What a texture graph writes when it asks for a baked map by what it measures.</summary>
/// <remarks>
///     <para>
///         <b>Doc 48 § 4.8's binding, as a string, because that is the only thing that crosses.</b>
///         A compilation may not open an asset database — it runs on every edit and a preview is one
///         — so what a <c>Mesh Map</c> node puts in <c>TextureGraphExternal.Asset</c> is a reference
///         and a host resolves it. For a <c>Source/Bitmap</c> that reference is a project-relative
///         path; for a mesh map there is no path to write, because the whole point is that the graph
///         does not know which mesh it is about to be baked for.
///     </para>
///     <para>
///         ⚠ <b>So the grammar is a scheme — <c>meshmap:curvature</c> — and it is deliberately not a
///         path.</b> A path would have to be a lie about some particular mesh, and a host reading one
///         could not tell the lie from a real reference to a file an artist happened to wire in by
///         hand. The scheme makes the two kinds of external distinguishable by looking, which is what
///         lets a bake with no mesh refuse a generator with a sentence rather than fail to open
///         <c>Assets/MeshMaps/curvature.png</c>.
///     </para>
/// </remarks>
public static class MeshMapReference {
    /// <summary>The scheme a mesh-map reference is written under.</summary>
    public const string Scheme = "meshmap:";

    /// <summary>What a graph writes to ask for one map.</summary>
    /// <param name="usage">What the map has to measure.</param>
    /// <returns>The reference.</returns>
    /// <exception cref="ArgumentOutOfRangeException">It is not a usage.</exception>
    public static string For(MeshMapUsage usage) => Scheme + MeshMapNaming.Suffix(usage);

    /// <summary>Whether a reference is one of these at all.</summary>
    /// <param name="reference">What the graph wrote.</param>
    /// <returns>Whether it names a mesh map rather than a file.</returns>
    /// <remarks>
    ///     ⚠ <b>The scheme and not the whole grammar</b>, so that a host walking a compilation's
    ///     externals can separate the two kinds before it decides what it is able to resolve — and so
    ///     that <c>meshmap:curvatur</c> is reported as a mesh map with a bad usage rather than
    ///     silently handed to a file opener as a path.
    /// </remarks>
    public static bool IsMeshMap(string? reference) =>
        reference is not null && reference.StartsWith(Scheme, StringComparison.Ordinal);

    /// <summary>Reads a reference back into the usage it names.</summary>
    /// <param name="reference">What the graph wrote.</param>
    /// <param name="usage">What it asks for.</param>
    /// <returns>Whether it is a mesh-map reference naming a usage this build bakes.</returns>
    public static bool TryParse(string? reference, out MeshMapUsage usage) {
        usage = default;

        return IsMeshMap(reference) && MeshMapNaming.TryParseSuffix(reference![Scheme.Length..], out usage);
    }
}

/// <summary>
///     One texture set's mesh maps, as the thing a graph's <c>meshmap:</c> references resolve
///     against.
/// </summary>
/// <remarks>
///     <para>
///         <b>This is the half of <a href="https://github.com/Rikarin/Vixen/issues/702">#702</a> that
///         a graph actually calls, and <see cref="MeshMapLibrary" /> is the half under it.</b> The
///         library is every map a project holds; a binding is the one set a bake is for, which is
///         where the mesh finally enters. Doc 48 § 4.8 puts it here on purpose: the graph names the
///         measurement, the bake names the mesh, and neither knows the other's half.
///     </para>
///     <para>
///         ⚠ <b>It is not a cache and holds no pixels.</b> What comes back is a
///         <see cref="MeshMapAsset" /> — an identity and a path — and reading the picture is the
///         caller's, because the caller is the one with a device to upload it to. Doing the read here
///         would put an image decoder behind a resolver that a preview calls on every edit.
///     </para>
///     <para>
///         ⚠ <b>An unresolved reference is a sentence and not an empty map.</b> Three different
///         things go wrong here and they need three different messages: the graph asked for a usage
///         this build does not bake, the set exists and was baked without that map (which
///         <see cref="MeshMapLibrary.TryResolve(string,MeshMapUsage,out MeshMapAsset)" />'s remarks
///         say is legitimate — <c>MeshMapBake.Always</c> guarantees only two of the nine), or there is
///         no such set. A black stand-in for any of them is a generator that produces a plausible
///         flat mask, which is the one failure mode doc 48 § A.9 says makes the tool not worth
///         opening.
///     </para>
/// </remarks>
public sealed class MeshMapBinding {
    readonly MeshMapLibrary library;

    /// <summary>Binds a graph's mesh-map references to one set.</summary>
    /// <param name="library">The project's maps, already indexed.</param>
    /// <param name="set">The set the bake is for — the stem every file in it is named from.</param>
    /// <exception cref="ArgumentNullException"><paramref name="library" /> is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="set" /> is null or empty.</exception>
    public MeshMapBinding(MeshMapLibrary library, string set) {
        ArgumentNullException.ThrowIfNull(library);
        ArgumentException.ThrowIfNullOrEmpty(set);

        this.library = library;
        Set = set;
    }

    /// <summary>The set every reference resolves against.</summary>
    public string Set { get; }

    /// <summary>Binds to the one set a model has baked.</summary>
    /// <param name="library">The project's maps, already indexed.</param>
    /// <param name="model">The model asset.</param>
    /// <param name="binding">The binding.</param>
    /// <param name="problem">Why there is none, or an empty string.</param>
    /// <returns>Whether that model has exactly one set.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="library" /> is null.</exception>
    /// <remarks>
    ///     ⚠ <b>A model is not a set, so this refuses rather than picks.</b> A model with three
    ///     meshes has three sets and every one of them has a curvature map — see
    ///     <see cref="MeshMapLibrary.TryResolve(AssetId,MeshMapUsage,out MeshMapAsset)" />, which
    ///     makes the same call for the same reason. The message names the sets, because the caller's
    ///     next move is to pick one.
    /// </remarks>
    public static bool TryFor(
        MeshMapLibrary library,
        AssetId model,
        [NotNullWhen(true)] out MeshMapBinding? binding,
        out string problem
    ) {
        ArgumentNullException.ThrowIfNull(library);

        binding = null;
        problem = "";

        if (model.IsEmpty) {
            problem = "No model was named, and an empty asset id is what a sidecar naming no model reads back as — "
                + "so it matches every un-keyed set in the project rather than one.";

            return false;
        }

        var sets = library.SetsOf(model);

        switch (sets.Count) {
            case 0:
                problem = "That model has no baked mesh maps. Bake them from Assets ▸ Bake Mesh Maps…, or the "
                    + "generators reading them have nothing to read.";

                return false;
            case 1:
                binding = new(library, sets[0]);

                return true;
            default:
                problem = $"That model has {sets.Count} texture sets ({string.Join(", ", sets)}), so \"the "
                    + "curvature map of this model\" has that many answers. Name the set to bake.";

                return false;
        }
    }

    /// <summary>Resolves one of a graph's external references, when it is a mesh map.</summary>
    /// <param name="reference">What the graph wrote — <c>TextureGraphExternal.Asset</c>.</param>
    /// <param name="map">The map it binds to.</param>
    /// <param name="problem">Why it does not, or an empty string.</param>
    /// <returns>Whether it resolved.</returns>
    /// <remarks>
    ///     ⚠ <b><see langword="false" /> with an empty <paramref name="problem" /> means "not mine",
    ///     and a caller has to tell that apart from a failure.</b> A compilation's external list mixes
    ///     imported bitmaps with mesh maps; a host walks it once and each entry is one or the other,
    ///     so a resolver that reported a bitmap's path as an unresolvable mesh map would make every
    ///     graph containing a <c>Source/Bitmap</c> look broken. <see cref="MeshMapReference.IsMeshMap" />
    ///     is the same question asked before the call, for a caller that would rather branch.
    /// </remarks>
    public bool TryResolve(string? reference, out MeshMapAsset map, out string problem) {
        map = default;
        problem = "";

        if (!MeshMapReference.IsMeshMap(reference)) {
            return false;
        }

        if (!MeshMapReference.TryParse(reference, out var usage)) {
            problem = $"'{reference}' asks for a mesh map this build does not bake. The ones it does are "
                + $"{string.Join(", ", MeshMapNaming.Every.Select(MeshMapNaming.Suffix))}.";

            return false;
        }

        if (!library.TryResolve(Set, usage, out map)) {
            problem = $"'{Set}' has no {MeshMapNaming.Suffix(usage)} map. A bake writes the normal and the "
                + "displacement always and the other seven only when it was asked for them, so this set was "
                + "baked without it — re-bake it with that map turned on.";

            return false;
        }

        return true;
    }
}

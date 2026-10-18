// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core;
using Vixen.Rendering;
using Vixen.Rendering.Ecs;

namespace Vixen.Engine.Renderer;

/// <summary>A catalog's materials, with a fallback for the ones it has refused for good.</summary>
/// <remarks>
///     <para>
///         ⚠ <b>Without it a mesh whose material the catalog refused does not draw at all, and that is
///         not obvious from either side.</b> <see cref="IMaterialSource.TryGet" /> is two-valued and
///         its false means "not yet" — <see cref="MeshExtractionSystem" /> reads it that way and
///         leaves the entity unsettled, to be asked again next frame. <see cref="AssetMaterialSource" />
///         also returns false for a reference it has given up on — one the build did not ship, a
///         handle that failed, a document the compiler rejected — so the entity waits for the life
///         of the process and only <see cref="MeshExtractionSystem.Waiting" /> says so.
///         <see cref="AssetMaterialSource.Refused" /> is what separates the two, and this is what asks.
///     </para>
///     <para>
///         ⚠ <b>Both renderers use this one type</b>
///         (<a href="https://github.com/Rikarin/Vixen/issues/509">#509</a>).
///         <see cref="WorldRenderer" /> hands it to the extraction <see cref="WorldRenderer.Register" />
///         builds, falling back to <see cref="MeshExtractionSystem.Material" /> — what a drawable that
///         names no material is drawn in — so a game after a partial content update draws a crate in
///         the neutral material rather than no crate. The editor's extraction is hand-assembled and
///         <c>Register</c> never runs there, so <c>EditorWorldRenderer.Mount</c> builds its own over
///         its grey fallback. A fix to one renderer only was the shape this issue had for a year.
///     </para>
///     <para>
///         The fallback is read on each refusal rather than captured, because a host sets
///         <see cref="MeshExtractionSystem.Material" /> after <see cref="WorldRenderer.Register" />
///         has made the extraction. With no fallback the refusal stays a refusal: a null material
///         reaching the virtualized path is a dereference, and "draws nothing" is what such a host
///         already sees for every drawable.
///     </para>
///     <para>
///         ⚠ <b>The fallback is not silent.</b> <see cref="FellBack" /> counts the references that took
///         it, so a host can say "this many materials are not in the catalog" rather than leaving
///         somebody to wonder why one crate is grey.
///     </para>
/// </remarks>
public sealed class FallbackMaterialSource : IMaterialSource {
    readonly AssetMaterialSource painter;
    readonly Func<Material?> fallback;
    readonly HashSet<AssetReference> fell = [];

    /// <summary>Wraps a mounted source.</summary>
    /// <param name="painter">The mounted source.</param>
    /// <param name="fallback">
    ///     What a refused reference is painted with, asked on each refusal; null from it leaves the
    ///     refusal standing, which is exactly the source this wraps.
    /// </param>
    /// <exception cref="ArgumentNullException">Either argument is null.</exception>
    public FallbackMaterialSource(AssetMaterialSource painter, Func<Material?> fallback) {
        ArgumentNullException.ThrowIfNull(painter);
        ArgumentNullException.ThrowIfNull(fallback);

        this.painter = painter;
        this.fallback = fallback;
    }

    /// <summary>The source this defers to, for whoever needs the real one.</summary>
    public AssetMaterialSource Painter => painter;

    /// <summary>How many distinct references the catalog refused and the fallback was drawn for.</summary>
    /// <remarks>
    ///     A set rather than a counter because a refused reference is asked once per entity and answered
    ///     the same for ever after — a per-ask count would report the number of entities rather than the
    ///     number of materials, and would keep climbing as a scene grew.
    /// </remarks>
    public int FellBack => fell.Count;

    /// <inheritdoc />
    public bool TryGet(AssetReference reference, out Material material) {
        if (painter.TryGet(reference, out material)) {
            return true;
        }

        // ⚠ Asked *after* the miss and not before it. `Refused` is false for a reference nothing has
        // asked about yet, so a check in front of the call would let every first ask through as
        // "still coming" — which is the right answer, arrived at the wrong way round, and would stop
        // being right the moment the predicate learned to answer without an entry.
        if (!painter.Refused(reference) || fallback() is not { } substitute) {
            return false;
        }

        fell.Add(reference);
        material = substitute;

        return true;
    }
}

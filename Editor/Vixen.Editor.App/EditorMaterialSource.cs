// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core;
using Vixen.Engine.Renderer;
using Vixen.Rendering;
using Vixen.Rendering.Ecs;

namespace Vixen.Editor.App;

/// <summary>The project's materials, with the editor's answer for the ones the catalog has not got.</summary>
/// <remarks>
///     <para>
///         <b>The material half of the argument <see cref="EditorContent" /> makes about geometry.</b>
///         The editor draws what is <em>in the project</em> and a catalog carries what <em>ships</em>,
///         and the two are deliberately different sets: <c>BuildPlanner.AddressOf</c> gives an excluded
///         asset no address, so a material somebody marked as reference-only has no entry.
///         <c>ProjectMeshSource</c> answers that for geometry by reading the import cache instead. This
///         answers it for materials by substituting the fallback, because there is no import-cache
///         material source and a viewport is better off drawing a grey crate than no crate.
///     </para>
///     <para>
///         ⚠ <b>Without it the mesh does not draw at all, and that is not obvious from either side.</b>
///         <see cref="IMaterialSource.TryGet" /> is two-valued and its false means "not yet" —
///         <c>MeshExtractionSystem.Painted</c> reads it that way and leaves the entity unsettled, to be
///         asked again next frame. <see cref="AssetMaterialSource" /> also returns false for a
///         reference it has refused for good, so the entity waits for the life of the process and the
///         author sees geometry that is simply absent. <see cref="AssetMaterialSource.Refused" /> is
///         what separates the two, and this is the only thing that asks.
///     </para>
///     <para>
///         ⚠ <b>The fallback is not silent.</b> <see cref="FellBack" /> counts the references that took
///         it, so <c>EditorWorldRenderer.Degraded</c> can say "this many materials in this scene are not
///         in the project's catalog" rather than leaving an author to wonder why one crate is grey.
///     </para>
/// </remarks>
sealed class EditorMaterialSource : IMaterialSource {
    readonly AssetMaterialSource painter;
    readonly Material? fallback;
    readonly HashSet<AssetReference> fell = [];

    /// <summary>Wraps the source a mount built.</summary>
    /// <param name="painter">The mounted source.</param>
    /// <param name="fallback">
    ///     What a refused reference is painted with, or null for a host that would compile none — in
    ///     which case this behaves exactly as the source it wraps.
    /// </param>
    /// <exception cref="ArgumentNullException"><paramref name="painter" /> is null.</exception>
    public EditorMaterialSource(AssetMaterialSource painter, Material? fallback) {
        ArgumentNullException.ThrowIfNull(painter);

        this.painter = painter;
        this.fallback = fallback;
    }

    /// <summary>The source this defers to, for whoever needs the real one.</summary>
    public AssetMaterialSource Painter => painter;

    /// <summary>How many distinct references the catalog could not supply.</summary>
    /// <remarks>
    ///     A set rather than a counter because a refused reference is asked once and answered for
    ///     ever after — a per-ask count would report the number of entities rather than the number of
    ///     materials, and would keep climbing as a scene grew.
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
        if (!painter.Refused(reference) || fallback is null) {
            return false;
        }

        fell.Add(reference);
        material = fallback;

        return true;
    }
}

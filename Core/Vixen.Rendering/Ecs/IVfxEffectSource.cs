// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core;
using Vixen.Vfx;

namespace Vixen.Rendering.Ecs;

/// <summary>Where the effect a <see cref="VfxEmitter" /> names comes from.</summary>
/// <remarks>
///     <para>
///         <b><see cref="IMaterialSource" />'s shape, one asset kind along, and for the same reason:
///         <c>Vixen.Rendering</c> links no asset manager.</b> An extraction system asks for a graph by
///         reference and is told whether it is ready; what turns a reference into an address and an
///         address into bytes is the content stack's business, which sits above this one.
///     </para>
///     <para>
///         <b>A compiled graph rather than the content record</b>, because the conversion is not free:
///         <c>VfxEffectContent.ToGraph</c> runs the validation that refuses an updater reading what
///         nothing writes, and doing it per emitter per frame would be doing it a thousand times for
///         an answer that cannot change. An implementation caches; the interface says so by handing
///         back the compiled form.
///     </para>
///     <para>
///         ⚠ <b>The graph is shared between every emitter that names it.</b> A
///         <see cref="VfxCompiledGraph" /> is immutable data — that is the property the whole dual
///         backend rests on — so one asset is one graph however many <see cref="VfxSystem" />s run it,
///         and an emitter's own state lives in the system rather than the graph.
///     </para>
/// </remarks>
public interface IVfxEffectSource {
    /// <summary>The effect a reference names, if it is ready yet.</summary>
    /// <param name="reference">Which effect.</param>
    /// <param name="graph">The compiled graph, when this returns true.</param>
    /// <returns>Whether it is ready.</returns>
    /// <remarks>
    ///     <b>Asking is what starts the load</b>, exactly as it is for a mesh and for a material: an
    ///     implementation begins one on the first miss and answers false until it lands, so a caller
    ///     never has to say "and also fetch this".
    /// </remarks>
    bool TryGet(AssetReference reference, out VfxCompiledGraph graph);
}

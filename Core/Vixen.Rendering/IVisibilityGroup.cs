// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Threading;

namespace Vixen.Rendering;

/// <summary>
///     Which objects each view can see, however that answer was arrived at.
/// </summary>
/// <remarks>
///     <para>
///         Two implementations, one question. <see cref="VisibilityGroup" /> tests every object on
///         the job system; <see cref="GpuVisibilityGroup" /> uploads the same records and dispatches
///         a compute shader. [docs/plan/06 § Frame structure] calls for both — GPU culling "where
///         capabilities allow", with "the CPU path remain[ing] for GL/WebGL" — and an interface is
///         what lets a compositor choose without anything downstream knowing which it got.
///     </para>
///     <para>
///         <strong>The result is a bitset either way, and that is the load-bearing part of the
///         contract.</strong> Sorting walks <see cref="Words" /> a word at a time; a feature narrows
///         the set through <see cref="Hide" />. Neither would work against an implementation that
///         answered with a list, so the shape of the answer belongs here rather than to whichever
///         thing computed it.
///     </para>
///     <para>
///         <strong>After <see cref="Cull" /> returns, the bits are this frame's.</strong> That is
///         the whole obligation an implementation takes on, and it is what makes the two
///         interchangeable — a group whose answer was a frame old would produce geometry that pops
///         at the edges of the screen with nothing anywhere to say why.
///     </para>
/// </remarks>
public interface IVisibilityGroup : IDisposable {
    /// <summary>How many views have results.</summary>
    int ViewCount { get; }

    /// <summary>Whether an object is visible in a view.</summary>
    /// <param name="viewIndex">Which view.</param>
    /// <param name="id">Which object.</param>
    /// <returns>False for a view or an object that has no answer, rather than throwing.</returns>
    bool IsVisible(int viewIndex, RenderObjectId id);

    /// <summary>The raw visibility words for a view, for a consumer that walks them itself.</summary>
    /// <param name="viewIndex">Which view.</param>
    /// <returns>One bit per object, least significant first; empty for a view with no answer.</returns>
    ReadOnlySpan<ulong> Words(int viewIndex);

    /// <summary>
    ///     Removes an object from a view after culling has run.
    /// </summary>
    /// <param name="viewIndex">Which view.</param>
    /// <param name="id">Which object.</param>
    /// <remarks>
    ///     <para>
    ///         The seam a refinement pass needs. Frustum culling answers "could this be seen"; LOD
    ///         answers "is this the copy that should be", and the second question can only be asked
    ///         once the first has been — an object outside the frustum has no screen size to measure.
    ///         So a feature narrows the set rather than replacing the test, which is also why this
    ///         only ever clears a bit: a pass that could <em>add</em> visibility would be one that
    ///         could draw something the frustum rejected.
    ///     </para>
    ///     <para>
    ///         Call it between <see cref="Cull" /> and <see cref="RenderSystem.Sort" />; a feature's
    ///         <c>Prepare</c> is exactly that window. Hiding after sorting would leave the object in
    ///         a list something already built.
    ///     </para>
    /// </remarks>
    void Hide(int viewIndex, RenderObjectId id);

    /// <summary>How many objects are visible in a view.</summary>
    /// <param name="viewIndex">Which view.</param>
    int VisibleCount(int viewIndex);

    /// <summary>Tests every object against every view, replacing the previous frame's answer.</summary>
    /// <param name="store">Every renderable in the scene.</param>
    /// <param name="views">The frame's views, in index order.</param>
    /// <param name="scheduler">The job system, or null to run inline.</param>
    /// <remarks>
    ///     The scheduler is a request rather than an instruction: an implementation that does the
    ///     work somewhere other than the CPU has nothing to schedule and ignores it.
    /// </remarks>
    void Cull(RenderObjectStore store, IReadOnlyList<RenderView> views, JobScheduler? scheduler = null);
}

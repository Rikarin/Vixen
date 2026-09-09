// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Diagnostics;
using Vixen.Core.Mathematics;
using Vixen.Ui.Layout;

namespace Vixen.Ui;

/// <summary>What made a region of the interface need doing again.</summary>
/// <remarks>
///     ⚠ <b>The kinds a <i>selector</i> can tell apart, because that is what decides the cost.</b>
///     These are the four invalidation entry points the restyle already distinguishes — see
///     <c>StyleChangeKind</c> — and the distinction is the one a person reading a debug overlay
///     wants: a class change can reach a sibling, an inline write cannot reach anything but a
///     subtree, and <see cref="Document" /> is the one that re-cascades everything.
/// </remarks>
public enum UiInvalidationKind : byte {
    /// <summary>A class was added or removed. Reaches whatever a selector says it does.</summary>
    Class,

    /// <summary>A state — hover, focus, disabled — changed on an element.</summary>
    State,

    /// <summary>A declaration was written on the element itself, reaching only its subtree.</summary>
    Inline,

    /// <summary>Something no record can express, so the next pass is a cold one over the document.</summary>
    Document
}

/// <summary>One thing that was invalidated: where it was, and what invalidated it.</summary>
/// <param name="Bounds">The element's box <i>before</i> the pass that answers this, in document space.</param>
/// <param name="Kind">What made it dirty.</param>
/// <remarks>
///     ⚠ <b>The box as it was, which is the box that has to be repainted.</b> A region recorded
///     after the pass would be where the element ended up, and the pixels a shrinking element left
///     behind — the ones a dirty-region highlight exists to show — would be in neither picture.
/// </remarks>
public readonly record struct UiDirtyRegion(Rectangle Bounds, UiInvalidationKind Kind);

/// <summary>An element's four boxes, as CSS names them.</summary>
/// <param name="Margin">The outermost box, including the margins.</param>
/// <param name="Border">The border box, which is <see cref="UiElement.Bounds" />.</param>
/// <param name="Padding">Inside the borders.</param>
/// <param name="Content">Inside the padding: where the words and the children go.</param>
/// <remarks>
///     ⚠ <b>Four rectangles rather than twelve edges</b>, because the thing a debug overlay draws is
///     four nested outlines and the arithmetic that turns edges into them is the part that is easy
///     to get wrong once per overlay. Reading it costs twelve calls into the layout results and
///     allocates nothing.
/// </remarks>
public readonly record struct UiBoxModel(Rectangle Margin, Rectangle Border, Rectangle Padding, Rectangle Content);

/// <summary>What a debug overlay may read about a document, without being able to disturb it.</summary>
/// <remarks>
///     <para>
///         <b>An aggregator rather than an instrument.</b> Every number here but the dirty regions
///         was already published by the pass that computes it, one property at a time across the
///         <see cref="UiDocument" /> partials — see this assembly's README § Diagnostics, where the
///         shape and the three constraints behind it are argued. What was missing was one place to
///         read them from, which is the whole of what doc 13's UI-debug overlay was blocked on.
///     </para>
///     <para>
///         ⚠ <b>It reads; it never samples.</b> The reactive graph is single-threaded by contract,
///         so a diagnostics surface that could touch a signal to answer a question would be able to
///         perturb the document it is describing. Every member here is a field read or a walk of
///         results that already exist, and none of them marks anything dirty.
///     </para>
///     <para>
///         ⚠ <b>And nothing on the read path allocates.</b> A UI-debug overlay is on for minutes at
///         a time in the frame it is diagnosing, so a surface that allocated per read would be
///         measuring itself — which is the trap
///         <a href="https://github.com/Rikarin/Vixen/issues/597">#597</a> is about one level up,
///         where three boxed <c>IReadOnlyList&lt;T&gt;</c> enumerators cost a settled frame 504
///         bytes. This is a struct over the document, the regions come back as a span, and there is
///         no list or string anywhere in it.
///     </para>
/// </remarks>
/// <param name="document">The document being described.</param>
public readonly struct UiDiagnostics(UiDocument document) {
    /// <summary>Whether this build records dirty regions at all.</summary>
    /// <remarks>
    ///     ⚠ <b>The difference between "nothing changed" and "nobody was watching", and a panel that
    ///     cannot say which is a panel that reports success on the day it does not run.</b>
    ///     <see cref="DirtyRegions" /> is empty in both cases, and only this says which — the same
    ///     job <c>World.EventsEnabled</c> does for the ECS's structural events, and it is a
    ///     <c>const</c> for the same reason: the public surface is identical in every configuration
    ///     because <c>CheckApi</c> baselines Release, and what the flag removes is the recording.
    /// </remarks>
    public const bool RecordsRegions = UiDocument.RecordsRegions;

    /// <summary>How many nodes the layout tree holds.</summary>
    public int LayoutNodes => document.Layout.NodeCount;

    /// <summary>How many elements the last pass cascaded.</summary>
    public int StylesResolved => document.StylesResolved;

    /// <summary>How many had their layout style rebuilt from the result.</summary>
    public int StylesApplied => document.StylesApplied;

    /// <summary>How many container scopes the last pass entered.</summary>
    public int ContainerScopesEntered => document.ContainerScopesEntered;

    /// <summary>How many times the style store has been compacted.</summary>
    public int StyleCompactions => document.StyleCompactions;

    /// <summary>How many passes the last frame needed before nothing moved.</summary>
    public int SettlingPasses => document.SettlingPasses;

    /// <summary>Whether the document has come to rest.</summary>
    public bool Settled => document.Settled;

    /// <summary>Whether the last restyle had to resolve every element rather than a few.</summary>
    /// <remarks>
    ///     The row worth putting a colour on: a cold pass on a frame where one thing moved is the
    ///     defect <a href="https://github.com/Rikarin/Vixen/issues/598">#598</a> was, and it is
    ///     invisible in every other number here — <see cref="StylesApplied" /> reads one either way.
    /// </remarks>
    public bool LastPassWasCold => document.LastPassWasCold;

    /// <summary>What invalidated the document before the pass <c>Update</c> most recently ran.</summary>
    /// <remarks>
    ///     ⚠ <b>Emptied by a frame that finds nothing to do, and it is the same honesty
    ///     <c>Update</c>'s own counters had to learn.</b> A frame that did no work was invalidated by
    ///     nothing, so a settled document reports no regions rather than the last real pass's — the
    ///     failure that made <c>StylesResolved</c> read a few hundred for ever on a document nobody
    ///     was touching. So a dirty-region highlight is drawn in the frame that did the work, which
    ///     is the frame it is about. Empty when <see cref="RecordsRegions" /> is false as well, which
    ///     is why that constant exists.
    /// </remarks>
    public ReadOnlySpan<UiDirtyRegion> DirtyRegions => document.DirtyRegions;

    /// <summary>How many invalidations were recorded, including any the ring had no room for.</summary>
    /// <remarks>
    ///     A count of work rather than of what survived: the ring is small on purpose, and a frame
    ///     with two thousand invalidations in it is exactly the frame whose number matters and whose
    ///     regions do not fit.
    /// </remarks>
    public int RegionsRecorded => document.RegionsRecorded;

    /// <summary>How many times a surface's draw list has been rebuilt from the tree.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>The instrument doc 49 § 7.3 asks for, standing before the work it is meant to
    ///         measure.</b> There is no retained per-element surface and no dirty-rect path:
    ///         <c>DrawListBuilder.Build</c> reconstructs the whole list on every frame of every
    ///         window, whether or not anything moved. The only economy is a content diff
    ///         <i>afterwards</i> — <c>DrawList.Version</c> — which lets a still window skip the
    ///         tessellation and the GPU recording, and does not skip the rebuild that produced the
    ///         identical list again.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Read against <see cref="DrawListsChanged" /> and never alone.</b> Neither number
    ///         means anything by itself; the gap between them <i>is</i> the waste, stated as work
    ///         rather than as watts or milliseconds — a hundred rebuilds and one change is a still
    ///         window rebuilding its drawing ninety-nine times for nothing, and it is the same figure
    ///         on a fast machine and a loaded one, which a wall-clock budget is not.
    ///     </para>
    /// </remarks>
    public int DrawListsBuilt => document.DrawListsBuilt;

    /// <summary>How many of those rebuilds produced drawing that differs from the frame before.</summary>
    /// <remarks>
    ///     ⚠ <b>What actually changed, not what was believed to have changed.</b> It counts
    ///     <c>DrawList.Version</c> moving, and that is a comparison against the previous content — so
    ///     a document invalidated too eagerly still reports one change here, and its eagerness shows
    ///     up as the gap to <see cref="DrawListsBuilt" /> instead of being absorbed and hidden.
    /// </remarks>
    public int DrawListsChanged => document.DrawListsChanged;

    /// <summary>How many times a binding in this document has thrown and been suspended.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>The one number here that counts a defect rather than a cost, and the interface it
    ///         describes looks perfect.</b> A <c>Composition.BuildContext.Bind</c> is an
    ///         <c>Effect</c>, and an effect answers an unhandled exception by suspending itself — it
    ///         keeps its dependencies, keeps its place in the graph and never runs again. The
    ///         assignment that threw had already written whatever it wrote, so the first frame is
    ///         correct and every frame after it is the same frame. Nothing on the element tree, in
    ///         the geometry, or in any other counter on this struct distinguishes that from a model
    ///         that stopped changing.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Always compiled, unlike the region ring.</b> It increments only when something
    ///         has already thrown, so there is no steady-state cost to remove, and a
    ///         <c>Release</c> test that asserts a panel has no broken bindings has to be able to read
    ///         it. See <a href="https://github.com/Rikarin/Vixen/issues/1109">#1109</a>, where the
    ///         symptom was a texturing panel that rendered once and froze.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>It counts every effect this document's scheduler suspends, which used not to be
    ///         the same thing.</b> The count was taken inside <c>BuildContext.Bind</c>'s own
    ///         <c>try</c>, so it saw the effects that method built and nothing else — the four
    ///         production sites that construct an <c>Effect</c> directly stopped as silently as
    ///         before, and a <i>runaway</i> effect, which throws nothing, was never counted by any
    ///         path. <a href="https://github.com/Rikarin/Vixen/issues/1122">#1122</a> moved it to
    ///         <c>EffectScheduler.Suspended</c>, the one point both ways of stopping pass through.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>What it still cannot see is an effect queued somewhere else.</b> An
    ///         <c>Effect</c> constructed with no scheduler takes <c>EffectScheduler.Default</c>,
    ///         which is per <i>thread</i> and belongs to no document. A zero here means "nothing this
    ///         document schedules has stopped", which is what a panel wants and is not the same
    ///         sentence as "nothing has stopped".
    ///     </para>
    ///     <para>
    ///         ⚠ <b>And no production code does that, which is now a gate rather than a hope</b> —
    ///         <a href="https://github.com/Rikarin/Vixen/issues/1129">#1129</a>.
    ///         <c>SchedulerReachTests</c> sweeps <c>.cs</c> and <c>.vxml</c> for the three calls whose
    ///         scheduler is optional — <c>new Effect</c>, <c>new AsyncComputed</c> and
    ///         <c>UiWindowTitle.Bind</c> — and refuses one that does not name it. So the ambiguity
    ///         above is a property of the API and not of this tree: everything the engine schedules
    ///         belongs to a document, and a nought here has one reading. ⚠ The count of that exposure
    ///         was believed to be ~58 sites; that figure is the test assemblies, where the thread's
    ///         scheduler is correct, and the production surface was already nought.
    ///     </para>
    /// </remarks>
    public int BrokenBindings => document.BrokenBindings;

    /// <summary>Where the last such binding was written, and what it threw. Null until one does.</summary>
    /// <remarks>
    ///     A count alone says an interface is broken and not which line of which file to open, and
    ///     the origin is the thing that is hard to recover afterwards: by the time a person notices
    ///     the freeze the effect is inert and its closure is unreachable from the tree.
    /// </remarks>
    public string? LastBrokenBinding => document.LastBrokenBinding;

    /// <summary>The element at a point, and its four boxes.</summary>
    /// <param name="x">Where, in document space.</param>
    /// <param name="y">Ditto.</param>
    /// <param name="element">What is there.</param>
    /// <param name="box">Its boxes.</param>
    /// <returns>Whether anything is there at all.</returns>
    /// <remarks>
    ///     <c>UiDocument.HitTest</c> is what decides, so this describes the element a pointer at
    ///     those coordinates would be talking to rather than the topmost one drawn there — which is
    ///     the question an overlay pinned to the cursor is actually asking.
    /// </remarks>
    public bool TryDescribe(float x, float y, out UiElement? element, out UiBoxModel box) {
        element = document.HitTest(x, y);

        if (element is null) {
            box = default;
            return false;
        }

        box = BoxOf(element);

        return true;
    }

    /// <summary>An element's four boxes.</summary>
    /// <param name="element">The element.</param>
    /// <returns>Its boxes, in document space.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="element" /> is <c>null</c>.</exception>
    /// <exception cref="ArgumentException"><paramref name="element" /> belongs to another document.</exception>
    public UiBoxModel BoxOf(UiElement element) {
        ArgumentNullException.ThrowIfNull(element);

        // ⚠ Refused rather than answered from the other document's layout tree. A `LayoutNodeId` is
        // an index into the tree that issued it, so an element of a different document would read
        // whichever node happens to sit at that index here — a box that is wrong and looks right.
        if (!ReferenceEquals(element.Document, document)) {
            throw new ArgumentException("The element belongs to a different document.", nameof(element));
        }

        var layout = document.Layout;
        var node = element.LayoutNode;
        var border = element.Bounds;

        var marginBox = Grow(
            border,
            layout.GetComputedMargin(node, Edge.Left),
            layout.GetComputedMargin(node, Edge.Top),
            layout.GetComputedMargin(node, Edge.Right),
            layout.GetComputedMargin(node, Edge.Bottom)
        );

        var paddingBox = Grow(
            border,
            -layout.GetComputedBorder(node, Edge.Left),
            -layout.GetComputedBorder(node, Edge.Top),
            -layout.GetComputedBorder(node, Edge.Right),
            -layout.GetComputedBorder(node, Edge.Bottom)
        );

        var contentBox = Grow(
            paddingBox,
            -layout.GetComputedPadding(node, Edge.Left),
            -layout.GetComputedPadding(node, Edge.Top),
            -layout.GetComputedPadding(node, Edge.Right),
            -layout.GetComputedPadding(node, Edge.Bottom)
        );

        return new UiBoxModel(marginBox, border, paddingBox, contentBox);
    }

    /// <summary>Grows a rectangle by an edge each, clamped so that it cannot turn inside out.</summary>
    static Rectangle Grow(Rectangle rectangle, float left, float top, float right, float bottom) =>
        new(
            rectangle.X - left,
            rectangle.Y - top,
            MathF.Max(0f, rectangle.Width + left + right),
            MathF.Max(0f, rectangle.Height + top + bottom)
        );
}

public partial class UiDocument {
#if DEBUG || VIXEN_UI_DIAGNOSTICS
    /// <summary>Whether this build records what invalidated it and where.</summary>
    internal const bool RecordsRegions = true;
#else
    /// <summary>Whether this build records what invalidated it and where.</summary>
    internal const bool RecordsRegions = false;
#endif

    /// <summary>How many regions the ring holds before it starts overwriting.</summary>
    /// <remarks>
    ///     ⚠ <b>A ring rather than a list, because the pathological frame is the interesting one.</b>
    ///     A document being restyled a thousand times in a pass is exactly what a person opens this
    ///     overlay to see, and a list that grew to hold all of it would be an allocation per frame in
    ///     the frame somebody is diagnosing. <see cref="RegionsRecorded" /> is what says how much was
    ///     dropped.
    /// </remarks>
    const int RegionCapacity = 64;

    readonly List<UiDirtyRegion> recording = [];
    readonly List<UiDirtyRegion> recorded = [];

    int recordingCount;

    /// <summary>What invalidated the document before the pass <c>Update</c> most recently ran.</summary>
    internal ReadOnlySpan<UiDirtyRegion> DirtyRegions => System.Runtime.InteropServices.CollectionsMarshal.AsSpan(recorded);

    /// <summary>How many invalidations were recorded, the dropped ones included.</summary>
    internal int RegionsRecorded { get; private set; }

    /// <summary>How many times a surface's draw list has been rebuilt from the tree.</summary>
    /// <remarks>
    ///     ⚠ <b>Always compiled, unlike the region ring above, because this is the number a
    ///     <i>gate</i> reads rather than the number an overlay draws.</b> A counter behind
    ///     <c>DEBUG</c> is a counter a Release gate cannot assert on, and the measurement doc 49 asks
    ///     for — idle-frame work, before and against after — is worthless if the two runs are not the
    ///     same build. It is one increment per window per frame.
    /// </remarks>
    internal int DrawListsBuilt { get; private set; }

    /// <summary>How many of those rebuilds produced drawing that differs from the frame before.</summary>
    internal int DrawListsChanged { get; private set; }

    /// <summary>How many effects in this document have been suspended after misbehaving.</summary>
    internal int BrokenBindings { get; private set; }

    /// <summary>Where the last one was written and what stopped it.</summary>
    internal string? LastBrokenBinding { get; private set; }

    /// <summary>Records an effect of this document's that the scheduler has just suspended.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>Not <c>[Conditional]</c>, and the contrast with <see cref="RecordDirty" /> right
    ///         below is the argument.</b> That one sits in the path a virtualised list walks two
    ///         dozen times a frame, so the call site itself is what has to go. This one is reached
    ///         only by an effect that has already stopped, so there is no cost to compile away — and
    ///         a counter behind <c>DEBUG</c> is a counter no <c>Release</c> gate can assert on, which
    ///         is the whole failure this records.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Reached from <c>EffectScheduler.Suspended</c> and from nowhere else, which is
    ///         <a href="https://github.com/Rikarin/Vixen/issues/1122">#1122</a>.</b> It used to be
    ///         called from inside the <c>try</c> that <c>BuildContext.Bind</c> wraps its assignment
    ///         in, so it saw the effects that one method built and no others — and it could not see
    ///         the runaway detector at all, because that path throws nothing. Both gaps close by
    ///         moving the count to the point every suspension passes through; the two are told apart
    ///         by <paramref name="exception" /> being null.
    ///     </para>
    /// </remarks>
    /// <param name="origin">Where the effect was declared, already formatted as <c>File.cs:12</c>.</param>
    /// <param name="exception">What it threw, or null when the runaway detector suspended it.</param>
    internal void RecordSuspendedEffect(string origin, Exception? exception) {
        BrokenBindings++;

        LastBrokenBinding = exception is null
            ? $"{origin}: re-ran until it was suspended; something it writes is something it reads."
            : $"{origin}: {exception.Message}";
    }

    /// <summary>Counts one rebuild and whether it was worth anything.</summary>
    void CountDrawing(bool changed) {
        DrawListsBuilt++;

        if (changed) {
            DrawListsChanged++;
        }
    }

    /// <summary>What a debug overlay may read about this document.</summary>
    /// <remarks>
    ///     A struct made on each get rather than an object held here: it carries one reference and
    ///     nothing else, so this allocates nothing and there is no second copy of any number in it.
    /// </remarks>
    public UiDiagnostics Diagnostics => new(this);

    /// <summary>Records that something was invalidated, in a build that asked to know.</summary>
    /// <remarks>
    ///     ⚠ <b><c>[Conditional]</c> rather than a runtime <c>if</c>, so that a build without the
    ///     flag has no call site at all</b> — no call, no argument evaluation, and in particular no
    ///     read of <see cref="UiElement.Bounds" />, which goes into the layout results. That is
    ///     <c>World.RaiseCreated</c>'s shape and it is chosen for the same reason: this sits in the
    ///     path a virtualised list walks two dozen times a frame.
    /// </remarks>
    /// <param name="element">What was invalidated, or <c>null</c> before the root exists.</param>
    /// <param name="kind">What invalidated it.</param>
    [Conditional("DEBUG")]
    [Conditional("VIXEN_UI_DIAGNOSTICS")]
    internal void RecordDirty(UiElement? element, UiInvalidationKind kind) {
        // ⚠ Nullable, because the first invalidation of a document's life happens inside its own
        // constructor: `Create` adopts the root and adopting invalidates, so `Root` is still null
        // when the very first region would be recorded. There is nothing to draw a highlight round
        // at that point and no reader to draw it.
        if (element is null) {
            return;
        }

        recordingCount++;

        if (recording.Count < RegionCapacity) {
            recording.Add(new UiDirtyRegion(element.Bounds, kind));
        }
    }

    /// <summary>Hands the recorded regions to the readers and starts a new frame's worth.</summary>
    /// <remarks>
    ///     Called by <see cref="Update" /> on the two paths that decide a frame's fate: one that is
    ///     about to do work, and one that has decided there is none. ⚠ The second is the one that
    ///     makes this honest — a settled frame invalidated nothing, and a surface that kept showing
    ///     the last real pass's regions would be the same defect the counters beside it carry a
    ///     paragraph about.
    /// </remarks>
    [Conditional("DEBUG")]
    [Conditional("VIXEN_UI_DIAGNOSTICS")]
    void TurnRegions() {
        // A span rather than the list, because this runs on every frame including the settled ones:
        // `AddRange(IEnumerable<T>)` is the shape that boxes an enumerator, which is the 504 bytes
        // #597 was.
        recorded.Clear();
        recorded.AddRange(System.Runtime.InteropServices.CollectionsMarshal.AsSpan(recording));
        recording.Clear();

        // ⚠ The count is of what was *recorded*, not of what fits: a frame with two thousand
        // invalidations is the frame worth looking at, and its regions are the ones the ring drops.
        RegionsRecorded = recordingCount;
        recordingCount = 0;
    }
}

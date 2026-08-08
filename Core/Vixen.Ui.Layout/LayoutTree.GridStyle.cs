// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

namespace Vixen.Ui.Layout;

/// <summary>
///     The grid half of the style surface, kept apart because four of its properties are lists.
/// </summary>
/// <remarks>
///     ⚠ <b>Every other setter in this store writes a fixed number of bytes into a struct; these four
///     write into an arena.</b> That is the whole of what grid cost the store on the input side, and
///     it is why the track lists are not fields on <see cref="LayoutStyle" /> the way <c>gap</c> and
///     <c>flex-basis</c> are. See <see cref="TrackArena" />.
/// </remarks>
public sealed partial class LayoutTree {
    /// <summary>Sets <c>grid-template-columns</c>.</summary>
    /// <param name="node">The node.</param>
    /// <param name="tracks">The explicit column tracks, in order.</param>
    public void SetGridTemplateColumns(LayoutNodeId node, ReadOnlySpan<GridTrackSize> tracks) =>
        WriteTemplate(node, ref TemplateOf(Validate(node), GridTemplateSlot.Columns), tracks, GridAutoRepeat.None, 0, -1);

    /// <summary>Sets <c>grid-template-rows</c>.</summary>
    /// <param name="node">The node.</param>
    /// <param name="tracks">The explicit row tracks, in order.</param>
    public void SetGridTemplateRows(LayoutNodeId node, ReadOnlySpan<GridTrackSize> tracks) =>
        WriteTemplate(node, ref TemplateOf(Validate(node), GridTemplateSlot.Rows), tracks, GridAutoRepeat.None, 0, -1);

    /// <summary>Sets <c>grid-template-columns</c> including one <c>repeat(auto-fill|auto-fit, …)</c>.</summary>
    /// <param name="node">The node.</param>
    /// <param name="tracks">
    ///     Every explicit track, with exactly one repetition of the automatic part written inline at
    ///     <paramref name="autoRepeatIndex" />.
    /// </param>
    /// <param name="kind">Which automatic repetition it is.</param>
    /// <param name="autoRepeatIndex">Where the repetition begins in <paramref name="tracks" />.</param>
    /// <param name="autoRepeatCount">How many tracks one repetition holds.</param>
    /// <remarks>
    ///     ⚠ <b>The repetition count is deliberately not a parameter, because it is not a style.</b>
    ///     CSS Grid §7.2.3.2 works it out from the container's own definite size and the tracks'
    ///     sizes, so it changes when the container is resized without the stylesheet changing at all.
    ///     Storing a count here would be storing one frame's answer in the style.
    /// </remarks>
    public void SetGridTemplateColumns(
        LayoutNodeId node,
        ReadOnlySpan<GridTrackSize> tracks,
        GridAutoRepeat kind,
        int autoRepeatIndex,
        int autoRepeatCount
    ) =>
        WriteTemplate(node, ref TemplateOf(Validate(node), GridTemplateSlot.Columns), tracks, kind, autoRepeatCount, autoRepeatIndex);

    /// <inheritdoc cref="SetGridTemplateColumns(LayoutNodeId,ReadOnlySpan{GridTrackSize},GridAutoRepeat,int,int)" />
    public void SetGridTemplateRows(
        LayoutNodeId node,
        ReadOnlySpan<GridTrackSize> tracks,
        GridAutoRepeat kind,
        int autoRepeatIndex,
        int autoRepeatCount
    ) =>
        WriteTemplate(node, ref TemplateOf(Validate(node), GridTemplateSlot.Rows), tracks, kind, autoRepeatCount, autoRepeatIndex);

    /// <summary>Sets <c>grid-auto-columns</c>, the sizes implicit columns cycle through.</summary>
    /// <param name="node">The node.</param>
    /// <param name="tracks">The sizes, in cycling order. Empty means <c>auto</c>.</param>
    public void SetGridAutoColumns(LayoutNodeId node, ReadOnlySpan<GridTrackSize> tracks) =>
        WriteTemplate(node, ref TemplateOf(Validate(node), GridTemplateSlot.AutoColumns), tracks, GridAutoRepeat.None, 0, -1);

    /// <summary>Sets <c>grid-auto-rows</c>, the sizes implicit rows cycle through.</summary>
    /// <param name="node">The node.</param>
    /// <param name="tracks">The sizes, in cycling order. Empty means <c>auto</c>.</param>
    public void SetGridAutoRows(LayoutNodeId node, ReadOnlySpan<GridTrackSize> tracks) =>
        WriteTemplate(node, ref TemplateOf(Validate(node), GridTemplateSlot.AutoRows), tracks, GridAutoRepeat.None, 0, -1);

    /// <summary>Reads back a node's <c>grid-template-columns</c>.</summary>
    /// <param name="node">The node.</param>
    /// <returns>The stored tracks. Empty when none was set.</returns>
    /// <remarks>
    ///     ⚠ The span points into the arena and is invalidated by the next write to any node's track
    ///     list. It is for reading now, not for keeping — the same contract as
    ///     <see cref="ChildArena.Slice" />.
    /// </remarks>
    public ReadOnlySpan<GridTrackSize> GetGridTemplateColumns(LayoutNodeId node) {
        ref var template = ref TemplateOf(Validate(node), GridTemplateSlot.Columns);
        return tracks.Slice(template.Offset, template.Count);
    }

    /// <inheritdoc cref="GetGridTemplateColumns" />
    public ReadOnlySpan<GridTrackSize> GetGridTemplateRows(LayoutNodeId node) {
        ref var template = ref TemplateOf(Validate(node), GridTemplateSlot.Rows);
        return tracks.Slice(template.Offset, template.Count);
    }

    /// <inheritdoc cref="GetGridTemplateColumns" />
    public ReadOnlySpan<GridTrackSize> GetGridAutoColumns(LayoutNodeId node) {
        ref var template = ref TemplateOf(Validate(node), GridTemplateSlot.AutoColumns);
        return tracks.Slice(template.Offset, template.Count);
    }

    /// <inheritdoc cref="GetGridTemplateColumns" />
    public ReadOnlySpan<GridTrackSize> GetGridAutoRows(LayoutNodeId node) {
        ref var template = ref TemplateOf(Validate(node), GridTemplateSlot.AutoRows);
        return tracks.Slice(template.Offset, template.Count);
    }

    /// <summary>Sets which axis auto-placement fills, and whether it backfills.</summary>
    /// <param name="node">The node.</param>
    /// <param name="flow">The flow.</param>
    public void SetGridAutoFlow(LayoutNodeId node, GridAutoFlow flow) {
        var index = Validate(node);
        if (styles[index].GridAutoFlow == flow) {
            return;
        }

        styles[index].GridAutoFlow = flow;
        MarkDirtyAndPropagate(index);
    }

    /// <summary>Sets one of the four placement properties.</summary>
    /// <param name="node">The node.</param>
    /// <param name="edge">
    ///     Which one: <see cref="Edge.Top" /> and <see cref="Edge.Bottom" /> are the row pair,
    ///     <see cref="Edge.Left" /> and <see cref="Edge.Right" /> the column pair.
    /// </param>
    /// <param name="placement">The placement.</param>
    /// <remarks>
    ///     ⚠ Addressed by physical edge rather than by four named setters so that a caller walking a
    ///     stylesheet can write a loop, and because <c>grid-row-start</c> is the block-start edge in
    ///     every writing mode this store supports.
    /// </remarks>
    public void SetGridPlacement(LayoutNodeId node, Edge edge, GridPlacement placement) {
        var index = Validate(node);

        if (edge is not (Edge.Top or Edge.Bottom or Edge.Left or Edge.Right)) {
            throw new ArgumentOutOfRangeException(
                nameof(edge),
                edge,
                "A grid placement names one of the four physical edges; the shorthand edges have no line to point at."
            );
        }

        ref var slot = ref PlacementOf(index, edge);
        if (slot == placement) {
            return;
        }

        slot = placement;
        MarkDirtyAndPropagate(index);
    }

    ref GridPlacement PlacementOf(int index, Edge edge) {
        ref var style = ref styles[index];

        if (edge == Edge.Top) {
            return ref style.GridRowStart;
        }

        if (edge == Edge.Bottom) {
            return ref style.GridRowEnd;
        }

        return ref edge == Edge.Left ? ref style.GridColumnStart : ref style.GridColumnEnd;
    }

    /// <summary>Sets the inline-axis placement of every child of this container.</summary>
    /// <param name="node">The node.</param>
    /// <param name="align">The alignment. <see cref="Align.FlexStart" /> means the inline start.</param>
    public void SetJustifyItems(LayoutNodeId node, Align align) {
        var index = Validate(node);
        if (styles[index].JustifyItems == align) {
            return;
        }

        styles[index].JustifyItems = align;
        MarkDirtyAndPropagate(index);
    }

    /// <summary>Sets this item's own inline-axis placement.</summary>
    /// <param name="node">The node.</param>
    /// <param name="align">The alignment, or <see cref="Align.Auto" /> to defer to the container.</param>
    public void SetJustifySelf(LayoutNodeId node, Align align) {
        var index = Validate(node);
        if (styles[index].JustifySelf == align) {
            return;
        }

        styles[index].JustifySelf = align;
        MarkDirtyAndPropagate(index);
    }

    /// <summary>Which of the four track lists a call is about.</summary>
    enum GridTemplateSlot { Columns, Rows, AutoColumns, AutoRows }

    ref GridTemplate TemplateOf(int index, GridTemplateSlot slot) {
        ref var style = ref styles[index];

        if (slot == GridTemplateSlot.Columns) {
            return ref style.GridTemplateColumns;
        }

        if (slot == GridTemplateSlot.Rows) {
            return ref style.GridTemplateRows;
        }

        return ref slot == GridTemplateSlot.AutoColumns ? ref style.GridAutoColumns : ref style.GridAutoRows;
    }

    void WriteTemplate(
        LayoutNodeId node,
        ref GridTemplate template,
        ReadOnlySpan<GridTrackSize> written,
        GridAutoRepeat kind,
        int autoRepeatCount,
        int autoRepeatIndex
    ) {
        var index = Validate(node);

        // ⚠ The clamp is here rather than in the algorithm because the arena is what a 40 000-track
        // declaration would actually exhaust, and because clamping once on write is the only place
        // the cost is paid once rather than per pass. See LayoutLimits.MaximumGridTracks.
        if (written.Length > LayoutLimits.MaximumGridTracks) {
            written = written[..LayoutLimits.MaximumGridTracks];

            if (autoRepeatIndex >= written.Length) {
                (kind, autoRepeatIndex, autoRepeatCount) = (GridAutoRepeat.None, -1, 0);
            }
        }

        if (kind == GridAutoRepeat.None || autoRepeatCount <= 0) {
            (kind, autoRepeatIndex, autoRepeatCount) = (GridAutoRepeat.None, -1, 0);
        }

        if (Unchanged(in template, written, kind, autoRepeatIndex, autoRepeatCount)) {
            return;
        }

        var (offset, capacity) = tracks.Write(template.Offset, template.Capacity, written);

        template.Offset = offset;
        template.Capacity = capacity;
        template.Count = written.Length;
        template.AutoRepeatKind = kind;
        template.AutoRepeatIndex = autoRepeatIndex;
        template.AutoRepeatCount = autoRepeatCount;

        MarkDirtyAndPropagate(index);
    }

    bool Unchanged(
        in GridTemplate template,
        ReadOnlySpan<GridTrackSize> written,
        GridAutoRepeat kind,
        int autoRepeatIndex,
        int autoRepeatCount
    ) =>
        template.Count == written.Length
        && template.AutoRepeatKind == kind
        && template.AutoRepeatIndex == autoRepeatIndex
        && template.AutoRepeatCount == autoRepeatCount
        && tracks.Slice(template.Offset, template.Count).SequenceEqual(written);

    /// <summary>Hands a node's four track blocks back to the arena.</summary>
    void ReleaseGridTemplates(int index) {
        ref var style = ref styles[index];

        Release(ref style.GridTemplateColumns);
        Release(ref style.GridTemplateRows);
        Release(ref style.GridAutoColumns);
        Release(ref style.GridAutoRows);

        void Release(ref GridTemplate template) {
            tracks.Free(template.Offset, template.Capacity);
            template = GridTemplate.Empty;
        }
    }
}

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

    /// <summary>Sets <c>grid-template-areas</c>, per CSS Grid §7.3.</summary>
    /// <param name="node">The node.</param>
    /// <param name="template">The named areas, or <see langword="null" /> for <c>none</c>.</param>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>A template makes the explicit grid at least as large as itself, and the tracks it
    ///         adds are sized by <c>grid-auto-rows</c>/<c>grid-auto-columns</c>.</b> §7.1: "the size
    ///         of the explicit grid is determined by the larger of the number of rows/columns defined
    ///         by <c>grid-template-areas</c> and the number sized by
    ///         <c>grid-template-rows</c>/<c>-columns</c>" — so a three-row template against a
    ///         one-track <c>grid-template-rows</c> has three <i>explicit</i> rows, which is what
    ///         line −1 counts back from, and two of them take their size from the implicit list.
    ///         Treating the extra two as implicit instead moves every negative line by two.
    ///     </para>
    ///     <para>
    ///         Unlike the four track lists this is not an arena handle: an area template is one
    ///         object per grid container rather than per node. See <see cref="GridAreaTemplate" />.
    ///     </para>
    /// </remarks>
    public void SetGridTemplateAreas(LayoutNodeId node, GridAreaTemplate? template) {
        var index = Validate(node);

        if (gridAreas is null) {
            if (template is null) {
                return;
            }

            gridAreas = new GridAreaTemplate?[capacity];
        }

        if (Equals(gridAreas[index], template)) {
            return;
        }

        gridAreas[index] = template;
        MarkDirtyAndPropagate(index);
    }

    /// <summary>Reads back a node's <c>grid-template-areas</c>.</summary>
    /// <param name="node">The node.</param>
    /// <returns>The template, or <see langword="null" /> when none is set.</returns>
    public GridAreaTemplate? GetGridTemplateAreas(LayoutNodeId node) => gridAreas?[Validate(node)];

    /// <summary>Sets one of the four placement properties to the name of a grid area.</summary>
    /// <param name="node">The node.</param>
    /// <param name="edge">Which edge, as <see cref="SetGridPlacement(LayoutNodeId,Edge,GridPlacement)" /> names them.</param>
    /// <param name="name">The area's name, or <see langword="null" /> to go back to the numeric placement.</param>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>A name beats the numeric placement on the same edge rather than sitting beside
    ///         it.</b> There is one CSS declaration per edge and it is either a line or a name, so
    ///         the two can never both be meant; the bridge writes whichever the cascade produced and
    ///         clears the other. A store that let both stand would answer differently depending on
    ///         which setter ran last.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The name is resolved against the <i>container's</i> template, which is why it is
    ///         stored rather than resolved here.</b> An item can be reparented into a grid with
    ///         different areas without its own style changing at all, and it can be set before its
    ///         parent's template is.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>A name no area matches is <c>auto</c>, and that is a documented divergence.</b>
    ///         §8.3 says the implicit grid lines are all assumed to carry the name, which places the
    ///         item on a line the author never wrote and cannot see; auto-placement is the answer
    ///         this store gives instead, and <c>GridTemplateAreasTests</c> pins it so the choice
    ///         cannot drift into a bug. Named lines in a track list are what would make the spec's
    ///         reading worth implementing, and they are not implemented.
    ///     </para>
    /// </remarks>
    public void SetGridPlacement(LayoutNodeId node, Edge edge, string? name) {
        var index = Validate(node);

        if (edge is not (Edge.Top or Edge.Bottom or Edge.Left or Edge.Right)) {
            throw new ArgumentOutOfRangeException(
                nameof(edge),
                edge,
                "A grid placement names one of the four physical edges; the shorthand edges have no line to point at."
            );
        }

        if (placementNames is null) {
            if (name is null) {
                return;
            }

            placementNames = new GridPlacementNames[capacity];
        }

        var updated = placementNames[index].With(edge, name);

        if (updated == placementNames[index]) {
            return;
        }

        placementNames[index] = updated;
        MarkDirtyAndPropagate(index);
    }

    /// <summary>Reads back one edge's named placement.</summary>
    /// <param name="node">The node.</param>
    /// <param name="edge">Which edge.</param>
    /// <returns>The area's name, or <see langword="null" /> when the edge is placed numerically.</returns>
    public string? GetGridPlacementName(LayoutNodeId node, Edge edge) =>
        placementNames?[Validate(node)].Of(edge);

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

        // ⚠ One CSS declaration per edge, so a numeric placement is not a second opinion beside a
        // named one — it replaces it. Done before the equality check below, because a node whose
        // numeric placement is already `auto` and whose name is `header` would otherwise keep the
        // name through a write that meant to take it away.
        SetGridPlacement(node, edge, name: null);

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
    /// <inheritdoc cref="SetJustifyContent" path="/remarks" />
    /// <param name="node">The node.</param>
    /// <param name="align">The alignment. <see cref="Align.FlexStart" /> means the inline start.</param>
    /// <param name="overflow">What it does for an item wider than its area.</param>
    public void SetJustifyItems(LayoutNodeId node, Align align, OverflowAlignment overflow = OverflowAlignment.Unsafe) {
        var index = Validate(node);
        if (styles[index].JustifyItems == align && styles[index].JustifyItemsOverflow == overflow) {
            return;
        }

        styles[index].JustifyItems = align;
        styles[index].JustifyItemsOverflow = overflow;
        MarkDirtyAndPropagate(index);
    }

    /// <summary>Sets this item's own inline-axis placement.</summary>
    /// <inheritdoc cref="SetJustifyContent" path="/remarks" />
    /// <param name="node">The node.</param>
    /// <param name="align">The alignment, or <see cref="Align.Auto" /> to defer to the container.</param>
    /// <param name="overflow">What it does when this item is wider than its area.</param>
    public void SetJustifySelf(LayoutNodeId node, Align align, OverflowAlignment overflow = OverflowAlignment.Unsafe) {
        var index = Validate(node);
        if (styles[index].JustifySelf == align && styles[index].JustifySelfOverflow == overflow) {
            return;
        }

        styles[index].JustifySelf = align;
        styles[index].JustifySelfOverflow = overflow;
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

    /// <summary>One node's four placement properties, when they are written as area names.</summary>
    /// <remarks>
    ///     A record struct rather than four slots in one flat array, so that <c>Array.Resize</c> in
    ///     <c>Grow</c> is the whole of the capacity story and an index is a node index everywhere.
    /// </remarks>
    readonly record struct GridPlacementNames(string? RowStart, string? RowEnd, string? ColumnStart, string? ColumnEnd) {
        public string? Of(Edge edge) => edge switch {
            Edge.Top => RowStart,
            Edge.Bottom => RowEnd,
            Edge.Left => ColumnStart,
            _ => ColumnEnd
        };

        public GridPlacementNames With(Edge edge, string? name) => edge switch {
            Edge.Top => this with { RowStart = name },
            Edge.Bottom => this with { RowEnd = name },
            Edge.Left => this with { ColumnStart = name },
            _ => this with { ColumnEnd = name }
        };
    }

    /// <summary>How many explicit tracks a container's named areas ask for on one axis.</summary>
    int AreaTrackCount(int index, bool inline) {
        var template = gridAreas?[index];

        if (template is null) {
            return 0;
        }

        return inline ? template.Columns : template.Rows;
    }

    /// <summary>
    ///     One edge's placement as §8 sees it, with an area's name already turned into a line.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>Both callers go through here, and that is the point rather than tidiness.</b> §8's
    ///         in-flow placement and §9's grid area for an out-of-flow child read the same four
    ///         properties from two different files, and a named area resolved in only one of them is
    ///         a <c>position: absolute</c> child that ignores <c>grid-area: header</c> while its
    ///         in-flow sibling honours it — a difference no assertion about either one alone can see.
    ///     </para>
    ///     <para>
    ///         §7.3 gives an area named <c>header</c> the four implicit lines
    ///         <c>header-start</c>/<c>header-end</c>, so this is a line lookup and nothing more.
    ///         The lines are already zero-based track indices, which is what
    ///         <see cref="GridAreaTemplate.TryGetArea" /> returns — <c>ResolveLine</c> would turn
    ///         them back into 1-based numbers only to undo it.
    ///     </para>
    /// </remarks>
    GridPlacement ResolveNamedPlacement(int container, int child, Edge edge, GridPlacement declared) {
        var name = placementNames?[child].Of(edge);

        if (name is null) {
            return declared;
        }

        if (gridAreas?[container] is not { } template || !template.TryGetArea(name, out var rowStart, out var rowEnd, out var columnStart, out var columnEnd)) {
            return GridPlacement.Auto;
        }

        var line = edge switch {
            Edge.Top => rowStart,
            Edge.Bottom => rowEnd,
            Edge.Left => columnStart,
            _ => columnEnd
        };

        // `GridPlacement.Line` counts from 1 and has no zero, and `ResolveLine` turns that back into
        // the index this already holds.
        return GridPlacement.Line(line + 1);
    }

    /// <summary>Forgets a slot's area template and named placements, on create and on destroy.</summary>
    void ClearGridNames(int index) {
        if (gridAreas is not null) {
            gridAreas[index] = null;
        }

        if (placementNames is not null) {
            placementNames[index] = default;
        }
    }

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

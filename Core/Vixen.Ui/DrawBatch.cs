// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

namespace Vixen.Ui;

/// <summary>What kind of work a batch is.</summary>
/// <remarks>
///     <para>
///         ⚠ <b>A coarse stand-in for two of the four things that actually decide a pipeline.</b>
///         <c>Vixen.Rendering</c> already answers this question: a pipeline is keyed on the effect,
///         the stage, the vertex layout and the render output, and its own remarks argue that those
///         four are what makes the key complete rather than merely sufficient so far. Only two of
///         them are a draw list's to know — which shader and which vertex format — because the stage
///         carries blend, depth and raster state and the output carries attachment formats, and both
///         belong to the compositor rather than to anything that walks an element tree.
///     </para>
///     <para>
///         So this is deliberately allowed to be wrong in one direction only: rectangles and borders
///         are grouped on the argument that both are signed-distance quads, and a filled path is
///         separated from a stroked one on the argument that tessellating an interior and
///         tessellating an outline are different work. Both are claims about a shader that has not
///         been written. <b>What it must not do is grow to describe the other two</b> — a draw list
///         that decided its own blend state would be deciding something the pass it is drawn in
///         already decided.
///     </para>
///     <para>
///         What is <i>not</i> a guess is everything the batcher does with these: batches are
///         contiguous, they preserve order, and they are maximal. Those hold whatever the grouping
///         turns out to be.
///     </para>
/// </remarks>
public enum BatchKind : byte {
    /// <summary>Rectangles and borders.</summary>
    Geometry,

    /// <summary>Glyph runs in one font.</summary>
    Text,

    /// <summary>Rectangles sampling one texture.</summary>
    Image,

    /// <summary>The inside of paths under one fill rule.</summary>
    PathFill,

    /// <summary>Lines along paths.</summary>
    PathStroke,

    /// <summary>A clip pushed or popped. Always exactly one command.</summary>
    /// <remarks>
    ///     A state change rather than a draw, and it gets a batch of its own so that the batches
    ///     still cover every command. A consumer then walks one list rather than two, and cannot
    ///     forget to interleave them.
    /// </remarks>
    Clip
}

/// <summary>A run of commands a renderer can submit together.</summary>
/// <param name="Kind">What kind of work it is.</param>
/// <param name="First">The index of its first command.</param>
/// <param name="Count">How many commands it covers.</param>
/// <param name="Font">Which font, for <see cref="BatchKind.Text" />. Zero and unread otherwise.</param>
/// <param name="FillRule">
///     Which fill rule, for <see cref="BatchKind.PathFill" />. Unread otherwise.
/// </param>
public readonly record struct DrawBatch(
    BatchKind Kind,
    int First,
    int Count,
    int Font,
    PathFillRule FillRule
) {
    /// <summary>Which texture, for <see cref="BatchKind.Image" />. Zero and unread otherwise.</summary>
    /// <remarks>
    ///     ⚠ <b>Part of what decides whether a command joins a batch.</b> A texture is a descriptor
    ///     set the renderer binds, so two images from different textures are two draws however
    ///     adjacent they are — merging them would draw the second one with the first one's picture.
    /// </remarks>
    public ulong Image { get; init; }
}

/// <summary>Groups a frame's commands into runs that can be drawn as one.</summary>
/// <remarks>
///     <para>
///         <b>Runs of consecutive commands, and never a reordering.</b> That is the whole design and
///         it is worth being blunt about, because reordering is what batching means everywhere else:
///         a 3D renderer sorts draws by material because a depth buffer decides what ends up in
///         front. A user interface has no depth buffer. Order <i>is</i> the answer to what is in
///         front, so moving two runs of the same font together across the panel between them draws
///         the text over the panel that was supposed to cover it.
///     </para>
///     <para>
///         ⚠ <b>The renderer already agrees, and it is worth knowing where.</b>
///         <c>RenderSortMode.ByGroup</c> exists and its own remarks say it is "for UI and anything
///         else already ordered" — depth left out, sorted on a group value alone. Which means a UI
///         render feature has to make that group <i>be</i> the painting order: the sort is stable and
///         orders by group, so a group that meant anything else — a material, a pipeline, a texture —
///         would reorder the interface on the way to the screen and undo everything below.
///         <s>The batch index is that number.</s>
///     </para>
///     <para>
///         ⚠ <b>It is not the batch index, and <c>Vixen.Ui.Renderer</c> settled it.</b> A render
///         object is one <i>surface</i>, not one batch: the store's objects live across frames and are
///         indexed by a dense id every feature's parallel array is keyed on, so an object per batch
///         would churn the whole store every time a label changed. The painting order within a surface
///         is already the order these batches are in, and no sort can reach it. What the group orders
///         is surfaces against each other — a modal over a document, a tooltip over the modal — which
///         is a real ordering problem the sort is the right answer to. The conclusion above was right
///         about <c>ByGroup</c> and wrong about what it counts.
///     </para>
///     <para>
///         So the win here is bounded and honest: adjacent things that happen to match are merged,
///         and nothing else is. A list of a hundred alternating labels and boxes batches into two
///         hundred batches, and that is the correct answer rather than a failure to optimise. What
///         improves it is emitting fewer interleavings — which is a question for whoever writes the
///         controls, not for this.
///     </para>
///     <para>
///         The batches are a <b>partition</b>: every command is in exactly one, in order, so a
///         consumer walks the batches alone and never has to fall back to the commands to find what
///         it missed.
///     </para>
///     <para>
///         ⚠ <b>The renderer does not work this way, and the difference is the point.</b>
///         <c>MeshRenderFeature</c> has no batch list: it walks its nodes in sorted order and
///         re-binds only when the pipeline handle changes, which reaches the same runs with two
///         locals and no array. That is right for a mesh, whose nodes are rebuilt from culling every
///         frame so nothing precomputed would survive. A user interface is the opposite case — most
///         frames draw exactly what the last one drew — so the runs are worked out <i>behind the
///         frame diff</i> and a still interface pays nothing.
///     </para>
///     <para>
///         ⚠ <b>That open question is closed: this list is used, and not by the render feature.</b>
///         <c>UiGeometryBuilder</c> turns one batch into one <c>UiDraw</c>, which is what carries the
///         resolved clip and the pipeline kind to the renderer; the renderer then does exactly what
///         <c>MeshRenderFeature</c> does and re-binds on change. So the grouping is computed once
///         behind the frame diff and consumed once per frame, which is the arrangement this was
///         written hoping for rather than the one where it is dead weight.
///     </para>
/// </remarks>
public static class DrawBatcher {
    /// <summary>Groups commands into batches.</summary>
    /// <param name="commands">The frame's commands, in painting order.</param>
    /// <param name="into">Where to put the batches. Cleared first.</param>
    public static void Build(IReadOnlyList<DrawCommand> commands, List<DrawBatch> into) {
        ArgumentNullException.ThrowIfNull(commands);
        ArgumentNullException.ThrowIfNull(into);

        into.Clear();

        for (var i = 0; i < commands.Count; i++) {
            var (kind, font, rule) = KeyOf(commands[i]);
            var image = kind == BatchKind.Image ? commands[i].Image : 0ul;

            // ⚠ A clip never joins anything, not even another clip. Two pushes in a row would merge
            // into a batch of two under the general rule, and a batch is a thing to draw — a state
            // change that arrives as part of a draw is a state change somebody will apply once.
            if (kind != BatchKind.Clip && into.Count > 0 && Extends(into[^1], kind, font, rule, image)) {
                into[^1] = into[^1] with { Count = into[^1].Count + 1 };
                continue;
            }

            into.Add(new DrawBatch(kind, i, 1, font, rule) { Image = image });
        }
    }

    /// <summary>Whether a command belongs to the batch being built.</summary>
    /// <remarks>
    ///     A clip already ended the previous batch by being one of its own, so there is no need to
    ///     ask whether the last batch was a clip: it cannot be extended, because its key is
    ///     <see cref="BatchKind.Clip" /> and nothing else has that key.
    /// </remarks>
    static bool Extends(DrawBatch batch, BatchKind kind, int font, PathFillRule rule, ulong image) =>
        batch.Kind == kind && batch.Font == font && batch.FillRule == rule && batch.Image == image;

    /// <summary>What decides which batch a command can join.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <s>The fill rule is part of the key. Two filled paths wound the same way but read by
    ///         different rules are not the same draw — one of them punches its hole and the other does
    ///         not — so merging them silently fills in every counter in an icon set.</s>
    ///     </para>
    ///     <para>
    ///         ⚠ <b>That reason stopped being true when the tessellator was written, and the fill rule
    ///         stays in the key as insurance rather than as a covered claim.</b>
    ///         <c>UiGeometryBuilder</c> reads <c>command.FillRule</c> per <i>command</i>, so two fills
    ///         under different rules in one batch each punch their own holes and nothing is lost by
    ///         merging them. What would make the reason true again is a renderer that resolves the
    ///         rule on the GPU — stencil-then-cover is the standard way to fill a large path, and
    ///         there the rule really is pipeline state. Keeping the key coarse costs a draw call in a
    ///         case that barely happens; taking it out would have to be undone by whoever wrote that
    ///         renderer.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The join and the cap are deliberately <i>not</i> in the key</b>, and that is not
    ///         an inconsistency. There is no renderer in which a join is pipeline state: a join is
    ///         geometry, on any implementation, so two strokes that differ only in it are the same
    ///         draw under every design and not merely under this one.
    ///     </para>
    /// </remarks>
    static (BatchKind Kind, int Font, PathFillRule Rule) KeyOf(in DrawCommand command) =>
        command.Kind switch {
            // A shadow is the box pipeline too — same quad, same distance field, a blur instead of a
            // border — so it batches with its neighbours rather than breaking the run in two.
            DrawCommandKind.Rectangle or DrawCommandKind.Border or DrawCommandKind.Shadow =>
                (BatchKind.Geometry, 0, default),
            DrawCommandKind.Text => (BatchKind.Text, command.Font, default),

            // ⚠ A field path is a text batch, and it is meant to merge with one. Both sample the one
            // atlas through the one shader, so an icon next to the label beside it is a single draw
            // rather than two — which is the point of putting icons in the glyph atlas at all. The
            // font index it carries is zero and unread, exactly as it is for every other kind.
            DrawCommandKind.Field => (BatchKind.Text, command.Font, default),
            DrawCommandKind.Image => (BatchKind.Image, 0, default),
            DrawCommandKind.Path => (BatchKind.PathFill, 0, command.FillRule),
            DrawCommandKind.PathStroke => (BatchKind.PathStroke, 0, default),
            _ => (BatchKind.Clip, 0, default)
        };
}

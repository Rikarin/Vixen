// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

namespace Vixen.Ui;

/// <summary>What kind of work a batch is.</summary>
/// <remarks>
///     ⚠ <b>A guess at where a renderer's pipeline boundaries will be, and there is no renderer yet
///     to check it against.</b> Rounded rectangles and borders are grouped on the argument that both
///     are signed-distance quads; a filled path and a stroked one are separated on the argument that
///     tessellating an interior and tessellating an outline are different work. The UI render feature
///     in phase 5 is what will actually know, and this enum is where it will disagree.
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
);

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

            // ⚠ A clip never joins anything, not even another clip. Two pushes in a row would merge
            // into a batch of two under the general rule, and a batch is a thing to draw — a state
            // change that arrives as part of a draw is a state change somebody will apply once.
            if (kind != BatchKind.Clip && into.Count > 0 && Extends(into[^1], kind, font, rule)) {
                into[^1] = into[^1] with { Count = into[^1].Count + 1 };
                continue;
            }

            into.Add(new DrawBatch(kind, i, 1, font, rule));
        }
    }

    /// <summary>Whether a command belongs to the batch being built.</summary>
    /// <remarks>
    ///     A clip already ended the previous batch by being one of its own, so there is no need to
    ///     ask whether the last batch was a clip: it cannot be extended, because its key is
    ///     <see cref="BatchKind.Clip" /> and nothing else has that key.
    /// </remarks>
    static bool Extends(DrawBatch batch, BatchKind kind, int font, PathFillRule rule) =>
        batch.Kind == kind && batch.Font == font && batch.FillRule == rule;

    /// <summary>What decides which batch a command can join.</summary>
    /// <remarks>
    ///     ⚠ The fill rule is part of the key. Two filled paths wound the same way but read by
    ///     different rules are not the same draw — one of them punches its hole and the other does
    ///     not — so merging them silently fills in every counter in an icon set.
    /// </remarks>
    static (BatchKind Kind, int Font, PathFillRule Rule) KeyOf(in DrawCommand command) =>
        command.Kind switch {
            DrawCommandKind.Rectangle or DrawCommandKind.Border => (BatchKind.Geometry, 0, default),
            DrawCommandKind.Text => (BatchKind.Text, command.Font, default),
            DrawCommandKind.Path => (BatchKind.PathFill, 0, command.FillRule),
            DrawCommandKind.PathStroke => (BatchKind.PathStroke, 0, default),
            _ => (BatchKind.Clip, 0, default)
        };
}

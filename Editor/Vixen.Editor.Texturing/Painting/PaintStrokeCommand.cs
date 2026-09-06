// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Diagnostics.CodeAnalysis;
using Vixen.Editor.Core;

namespace Vixen.Editor.Texturing.Painting;

/// <summary>
///     One drag on a paint layer as one entry in the undo history.
/// </summary>
/// <remarks>
///     <para>
///         <b><c>TerrainStrokeCommand</c>'s shape, and deliberately not a new one.</b> The stroke
///         holds the texels it touched and their values before and after; this is the thing that puts
///         it on <c>CommandStack</c> and tells whoever is drawing what to re-upload either way.
///     </para>
///     <para>
///         ⚠ <b>Merging is off, deliberately and not by omission.</b> Two strokes are two undos,
///         which is what an artist means by "undo that" and what every paint application does. What
///         <em>does</em> merge is inside the stroke: a drag is one <see cref="PaintStroke" /> being
///         extended rather than four hundred commands, so by the time one of these exists the merging
///         has already happened.
///     </para>
///     <para>
///         ⚠ <b>It takes strokes, plural, and that is what symmetry is.</b> Doc 48 § D13: "symmetry
///         is a mirrored second stamp". A mirrored path is its own stroke — its own spacing, its own
///         carried distance, its own record — and one drag with planar symmetry on is therefore two
///         strokes and still exactly one undo entry. Putting the plural here rather than inside
///         <see cref="PaintStroke" /> keeps the stroke a function of one path, which is what makes
///         its arithmetic checkable.
///     </para>
///     <para>
///         ⚠ <b>Built at pointer-up, from strokes that have already been applied</b> — every command
///         in the editor records this way. So <see cref="Do" /> is a <em>redo</em> the first time it
///         is not called, which is why the constructor captures the after-image rather than replaying
///         the brush.
///     </para>
///     <para>
///         ⚠ <b>The change callback fires once per stroke, and the union it used to fire once with is
///         the very thing <a href="https://github.com/Rikarin/Vixen/issues/871">#871</a> took off the
///         pointer path — <a href="https://github.com/Rikarin/Vixen/issues/891">#891</a>.</b> A union
///         of rectangles is a rectangle, and a mirrored drag's two strokes sit wherever the atlas
///         packer put their islands: undoing one paid for the bounding box between them, which is
///         most of the atlas. <see cref="Rect" /> is still the union, because a name for "everything
///         this entry touched" is a different question from "what to recomposite".
///     </para>
///     <para>
///         ⚠ <b>Per stroke and deliberately not per stamp, which is where this stops.</b>
///         <c>PaintStroke.MoveTo</c> hands its per-stamp rectangles to the caller as they happen and
///         keeps none; carrying them here would add a list per stroke to a record whose size is
///         already <a href="https://github.com/Rikarin/Vixen/issues/850">#850</a>'s complaint, to
///         save recompositing inside one path's own swept area — and an undo runs once where a stamp
///         runs hundreds of times. The unbounded case is the mirror, and the mirror is what the
///         plural covers.
///     </para>
/// </remarks>
sealed class PaintStrokeCommand : IEditorCommand {
    readonly IReadOnlyList<PaintStroke> strokes;
    readonly List<PaintStrokeRedo> redo;
    readonly Action<PaintRect>? changed;

    /// <summary>What the last <see cref="Do" /> or <see cref="Undo" /> moved, one entry per stroke.</summary>
    readonly List<PaintRect> moved = [];

    /// <summary>Records an applied drag.</summary>
    /// <param name="strokes">The stroke and its mirrors, already applied.</param>
    /// <param name="name">What the undo entry says.</param>
    /// <param name="changed">
    ///     Told which texels moved, on undo and on redo — <b>once per stroke</b>, not once per call.
    ///     Where a re-upload and a recomposite hang.
    /// </param>
    /// <exception cref="ArgumentException">Every stroke touched nothing.</exception>
    public PaintStrokeCommand(IReadOnlyList<PaintStroke> strokes, string name, Action<PaintRect>? changed = null) {
        ArgumentNullException.ThrowIfNull(strokes);

        var touched = false;

        foreach (var stroke in strokes) {
            touched |= !stroke.IsEmpty;
        }

        if (!touched) {
            throw new ArgumentException(
                "A drag that painted nothing is not an undo entry; check IsEmpty before making one.",
                nameof(strokes)
            );
        }

        this.strokes = strokes;
        this.changed = changed;

        // ⚠ Now, not on the first undo. A capture taken later reads whatever the strokes after this
        // one left, which is `TerrainStrokeCommand`'s remark and the same trap.
        redo = [];

        foreach (var stroke in strokes) {
            redo.Add(stroke.Capture());
        }

        Name = name;
        Rect = Union();
    }

    /// <inheritdoc />
    public string Name { get; }

    /// <summary>Which texels the drag touched, mirrors included.</summary>
    public PaintRect Rect { get; }

    /// <summary>How many texels the record holds, before and after together.</summary>
    public int RecordedTexels {
        get {
            var total = 0;

            foreach (var stroke in strokes) {
                total += stroke.RecordedTexels;
            }

            return total;
        }
    }

    /// <summary>How many bytes it occupies.</summary>
    public long Bytes {
        get {
            var total = 0L;

            foreach (var stroke in strokes) {
                total += stroke.Bytes;
            }

            return total;
        }
    }

    /// <inheritdoc />
    public void Do(EditorContext context) {
        moved.Clear();

        foreach (var entry in redo) {
            moved.Add(entry.Redo());
        }

        Announce();
    }

    /// <inheritdoc />
    public void Undo(EditorContext context) {
        moved.Clear();

        foreach (var stroke in strokes) {
            moved.Add(stroke.Undo());
        }

        Announce();
    }

    /// <inheritdoc />
    /// <remarks>
    ///     Never, and this is the override rather than the default so that the reason is written down
    ///     where somebody adding a second stroke type will read it. See the type's remarks.
    /// </remarks>
    public bool TryMergeWith(IEditorCommand previous, [NotNullWhen(true)] out IEditorCommand? merged) {
        merged = null;

        return false;
    }

    /// <summary>Hands out one rectangle per stroke, skipping the ones that moved nothing.</summary>
    void Announce() {
        if (changed is null) {
            return;
        }

        foreach (var rect in moved) {
            if (!rect.IsEmpty) {
                changed(rect);
            }
        }
    }

    PaintRect Union() {
        var rect = PaintRect.Empty;

        foreach (var stroke in strokes) {
            rect = rect.Union(stroke.Rect);
        }

        return rect;
    }
}

// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Diagnostics.CodeAnalysis;
using Vixen.Editor.Core;
using Vixen.Terrain;
using TerrainMap = Vixen.Terrain.Terrain;

namespace Vixen.Editor.Terrain;

/// <summary>
///     One brush stroke as one entry in the undo history.
/// </summary>
/// <remarks>
///     <para>
///         <b>[docs/plan/31 § D11], and the record it wraps is the kernel's.</b>
///         <see cref="TerrainStroke" /> holds the layer, the union of the rectangles the stroke
///         touched, and that rectangle's deltas before and after; this is the thing that puts it on
///         <see cref="CommandStack" /> and tells the terrain what to recomposite either way.
///     </para>
///     <para>
///         ⚠ <b>Merging is off, deliberately and not by omission.</b> Two strokes are two undos —
///         which is what an artist means by "undo that" and what every paint application does. What
///         <em>does</em> merge is inside the stroke: a drag is one <see cref="TerrainStroke" /> being
///         extended rather than four hundred commands, so by the time one of these exists the merging
///         has already happened.
///     </para>
///     <para>
///         ⚠ <b>Built at pointer-up, from a stroke that has already been applied.</b> Every command
///         in the editor records this way — see <c>EditMeshCommand.Moved</c> — because a drag applies
///         as it goes and the command is what makes the finished state undoable rather than what
///         performs it. So <see cref="Do" /> is a <em>redo</em> the first time it is not called, which
///         is why the constructor takes the captured after-image rather than reapplying the brush.
///     </para>
/// </remarks>
public sealed class TerrainStrokeCommand : IEditorCommand {
    readonly TerrainMap terrain;
    readonly TerrainStroke stroke;
    readonly TerrainStrokeRedo redo;
    readonly Action<TerrainRect>? changed;

    /// <summary>Records an applied stroke.</summary>
    /// <param name="terrain">The terrain it was applied to.</param>
    /// <param name="stroke">The stroke, already applied.</param>
    /// <param name="name">What the undo entry says.</param>
    /// <param name="changed">
    ///     Told which samples moved, on undo and on redo. Where a collider rebuild is hung.
    /// </param>
    /// <exception cref="ArgumentException">The stroke touched nothing.</exception>
    public TerrainStrokeCommand(
        TerrainMap terrain,
        TerrainStroke stroke,
        string name,
        Action<TerrainRect>? changed = null
    ) {
        ArgumentNullException.ThrowIfNull(terrain);
        ArgumentNullException.ThrowIfNull(stroke);

        if (stroke.IsEmpty) {
            throw new ArgumentException(
                "A stroke that touched nothing is not an undo entry; check IsEmpty before making one.",
                nameof(stroke)
            );
        }

        this.terrain = terrain;
        this.stroke = stroke;
        this.changed = changed;

        // ⚠ Now, not on the first undo. `Capture` reads the layer's deltas as they are, and by the
        // time an undo runs the layer holds whatever the strokes after this one left.
        redo = stroke.Capture();

        Name = name;
    }

    /// <inheritdoc />
    public string Name { get; }

    /// <summary>Which samples the stroke touched.</summary>
    public TerrainRect Rect => stroke.Rect;

    /// <summary>Which layer it wrote.</summary>
    public TerrainEditLayer Layer => stroke.Layer;

    /// <summary>How many samples the record holds, before and after together.</summary>
    public int RecordedSamples => stroke.RecordedSamples;

    /// <summary>How many bytes it occupies.</summary>
    public long Bytes => stroke.Bytes;

    /// <inheritdoc />
    public void Do(EditorContext context) => Resolved(redo.Redo());

    /// <inheritdoc />
    public void Undo(EditorContext context) => Resolved(stroke.Undo());

    /// <inheritdoc />
    /// <remarks>
    ///     Never, and this is the override rather than the default so that the reason is written down
    ///     where somebody adding a second stroke type will read it. See the type's remarks.
    /// </remarks>
    public bool TryMergeWith(IEditorCommand previous, [NotNullWhen(true)] out IEditorCommand? merged) {
        merged = null;
        return false;
    }

    /// <summary>Recomposites what moved and tells whoever is listening.</summary>
    /// <remarks>
    ///     ⚠ <b>Resolved here rather than left for the next frame.</b> Undo is not a drag: nothing
    ///     else is about to invalidate the same tiles, and a collider rebuilt from a stale composite
    ///     would be ground the player falls through until the next stroke happens to touch it.
    /// </remarks>
    void Resolved(TerrainRect rect) {
        terrain.Resolve();
        changed?.Invoke(rect);
    }
}

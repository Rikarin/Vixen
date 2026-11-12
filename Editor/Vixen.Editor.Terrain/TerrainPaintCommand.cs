// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Diagnostics.CodeAnalysis;
using Vixen.Editor.Core;
using Vixen.Terrain;
using TerrainMap = Vixen.Terrain.Terrain;

namespace Vixen.Editor.Terrain;

/// <summary>
///     One paint stroke as one entry in the undo history.
/// </summary>
/// <remarks>
///     <para>
///         <b>The sibling of <see cref="TerrainStrokeCommand" />, over a different record.</b> A
///         sculpt stroke holds one layer's deltas; a paint stroke holds <em>every</em> layer's
///         weights, because painting one lowers the rest proportionally — see
///         <see cref="TerrainWeightStroke" />.
///     </para>
///     <para>
///         ⚠ <b>Merging is off, for [§ D11]'s reason.</b> Two strokes are two undos. What merges is
///         inside the stroke: a drag is one record being extended, so by the time one of these exists
///         the merging has already happened.
///     </para>
/// </remarks>
public sealed class TerrainPaintCommand : IEditorCommand {
    readonly TerrainMap terrain;
    readonly TerrainWeightStroke stroke;
    readonly TerrainWeightRedo redo;
    readonly Action<TerrainRect>? changed;

    /// <summary>Records an applied paint stroke.</summary>
    /// <param name="terrain">The terrain it was applied to.</param>
    /// <param name="stroke">The stroke, already applied.</param>
    /// <param name="name">What the undo entry says.</param>
    /// <param name="changed">Told which samples moved, on undo and on redo.</param>
    /// <exception cref="ArgumentException">The stroke touched nothing.</exception>
    public TerrainPaintCommand(
        TerrainMap terrain,
        TerrainWeightStroke stroke,
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

        redo = stroke.Capture();
        Name = name;
    }

    /// <inheritdoc />
    public string Name { get; }

    /// <summary>Which samples the stroke touched.</summary>
    public TerrainRect Rect => stroke.Rect;

    /// <summary>How many samples the record holds.</summary>
    public int RecordedSamples => stroke.RecordedSamples;

    /// <summary>How many bytes it occupies.</summary>
    public long Bytes => stroke.Bytes;

    /// <inheritdoc />
    public void Do(EditorContext context) => Applied(redo.Redo());

    /// <inheritdoc />
    public void Undo(EditorContext context) => Applied(stroke.Undo());

    /// <inheritdoc />
    /// <remarks>Never — the override is here so the reason is beside the type that decided it.</remarks>
    public bool TryMergeWith(IEditorCommand previous, [NotNullWhen(true)] out IEditorCommand? merged) {
        merged = null;
        return false;
    }

    /// <summary>Tells whoever is listening which samples moved.</summary>
    /// <remarks>
    ///     ⚠ <b>No <c>Resolve</c>, unlike the sculpt command's — and that is not an omission.</b>
    ///     A paint stroke changes no height, so the composite it would recompute is the one already
    ///     there. What does need telling is the renderer, whose weightmaps are stale: the tiles are
    ///     marked, and the upload reads the same dirty set the heights do.
    /// </remarks>
    void Applied(TerrainRect rect) => changed?.Invoke(rect);
}

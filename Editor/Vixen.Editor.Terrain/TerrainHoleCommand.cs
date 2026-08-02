// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Editor.Core;
using Vixen.Terrain;
using TerrainMap = Vixen.Terrain.Terrain;

namespace Vixen.Editor.Terrain;

/// <summary>
///     A hole stroke, recorded so it can be undone.
/// </summary>
/// <remarks>
///     <para>
///         <b>The parallel of <see cref="TerrainStroke" />, and a separate type because a hole is not
///         a delta.</b> The seven sculpt tools write signed offsets into an edit layer, so their
///         record is the layer's values either side; holes are one bit on
///         <see cref="TerrainHoles" />, which lives on the terrain rather than on a layer and has no
///         alpha, no stack and no composite. Trying to record one in a
///         <see cref="TerrainStroke" /> would be recording the wrong container's contents and
///         restoring them onto ground the tool never touched.
///     </para>
///     <para>
///         ⚠ <b>The <em>before</em> image is taken lazily and never re-taken</b>, exactly as
///         <see cref="TerrainStroke.Extend" /> does it: a drag crossing the same ground forty times
///         records it once, holding the bit it had before the first crossing. Re-recording would make
///         undo restore the middle of the stroke.
///     </para>
/// </remarks>
public sealed class TerrainHoleStroke {
    readonly TerrainMap terrain;
    readonly Dictionary<long, bool> before = [];

    /// <summary>Begins recording.</summary>
    /// <param name="terrain">The terrain.</param>
    public TerrainHoleStroke(TerrainMap terrain) {
        ArgumentNullException.ThrowIfNull(terrain);
        this.terrain = terrain;
    }

    /// <summary>Everything the stroke has touched.</summary>
    public TerrainRect Rect { get; private set; } = TerrainRect.Empty;

    /// <summary>How many samples the record holds.</summary>
    public int RecordedSamples => before.Count;

    /// <summary>Whether anything has been recorded.</summary>
    public bool IsEmpty => before.Count == 0;

    /// <summary>Records the bits a stamp is about to change, and says which samples those are.</summary>
    /// <param name="brush">The brush about to be applied.</param>
    /// <param name="stamp">Where it is about to land.</param>
    /// <returns>The samples the stamp can reach.</returns>
    /// <remarks>
    ///     Computes the rectangle itself, for <see cref="TerrainStroke.Record" />'s reason: a caller
    ///     who took it from the kernel's return value could only take it afterwards, which records
    ///     what the kernel wrote.
    /// </remarks>
    public TerrainRect Record(in TerrainBrush brush, in BrushStamp stamp) {
        var rect = TerrainSculpt.AffectedRect(terrain.Description, brush, stamp)
            .Clip(new(0, 0, terrain.Description.SamplesX, terrain.Description.SamplesZ));

        if (rect.IsEmpty) {
            return rect;
        }

        Rect = Rect.Union(rect);

        for (var z = rect.Z; z < rect.EndZ; z++) {
            for (var x = rect.X; x < rect.EndX; x++) {
                before.TryAdd(((long)z << 32) | (uint)x, terrain.Holes.IsHole(x, z));
            }
        }

        return rect;
    }

    /// <summary>Puts the mask back the way it was.</summary>
    /// <returns>What the stroke had touched.</returns>
    public TerrainRect Undo() {
        foreach (var (key, hole) in before) {
            terrain.Holes.SetHole((int)(uint)key, (int)(key >> 32), hole);
        }

        return Rect;
    }

    /// <summary>Captures what the stroke left, so it can be redone.</summary>
    /// <returns>The record.</returns>
    public IReadOnlyDictionary<long, bool> Capture() {
        var after = new Dictionary<long, bool>(before.Count);

        foreach (var key in before.Keys) {
            after[key] = terrain.Holes.IsHole((int)(uint)key, (int)(key >> 32));
        }

        return after;
    }
}

/// <summary>One hole stroke as one entry in the undo history.</summary>
/// <remarks>
///     ⚠ <b>The collider is rebuilt for a hole stroke even though no height moved.</b> A hole
///     removes the quads that reference it from the collision shape as well as from the index buffer
///     — [§ The sculpt tools] — so a cave mouth that was punched and not re-collided is a hole the
///     player can see through and still stands on.
/// </remarks>
public sealed class TerrainHoleCommand : IEditorCommand {
    readonly TerrainMap terrain;
    readonly TerrainHoleStroke stroke;
    readonly IReadOnlyDictionary<long, bool> after;
    readonly Action<TerrainRect>? changed;

    /// <summary>Records an applied hole stroke.</summary>
    /// <param name="terrain">The terrain.</param>
    /// <param name="stroke">The stroke, already applied.</param>
    /// <param name="name">What the undo entry says.</param>
    /// <param name="changed">Told which samples moved, on undo and on redo.</param>
    /// <exception cref="ArgumentException">The stroke touched nothing.</exception>
    public TerrainHoleCommand(
        TerrainMap terrain,
        TerrainHoleStroke stroke,
        string name,
        Action<TerrainRect>? changed = null
    ) {
        ArgumentNullException.ThrowIfNull(terrain);
        ArgumentNullException.ThrowIfNull(stroke);

        if (stroke.IsEmpty) {
            throw new ArgumentException("A stroke that touched nothing is not an undo entry.", nameof(stroke));
        }

        this.terrain = terrain;
        this.stroke = stroke;
        this.changed = changed;

        after = stroke.Capture();
        Name = name;
    }

    /// <inheritdoc />
    public string Name { get; }

    /// <summary>Which samples the stroke touched.</summary>
    public TerrainRect Rect => stroke.Rect;

    /// <inheritdoc />
    public void Do(EditorContext context) {
        foreach (var (key, hole) in after) {
            terrain.Holes.SetHole((int)(uint)key, (int)(key >> 32), hole);
        }

        changed?.Invoke(stroke.Rect);
    }

    /// <inheritdoc />
    public void Undo(EditorContext context) => changed?.Invoke(stroke.Undo());
}

// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

namespace Vixen.Terrain;

/// <summary>
///     One brush stroke, recorded so it can be undone.
/// </summary>
/// <remarks>
///     <para>
///         <b>[docs/plan/31 § D11]: a stroke is one command and it stores a rect.</b> Pointer down,
///         drag, pointer up is one entry holding the layer it targeted, the union of the rectangles
///         it touched, and that rectangle's deltas before and after. A 256-square rectangle of
///         deltas is 128 KB each way; a typical stroke is a fraction of one tile.
///     </para>
///     <para>
///         <b>The layer's deltas, not the composite.</b> Restoring the composite would restore a
///         derived value and leave the layer holding what the stroke wrote, so the next
///         recompositing would put the stroke back. It is the same reason the sculpt kernels write
///         the layer and read the composite.
///     </para>
///     <para>
///         <b>Intra-stroke updates merge; two strokes do not.</b> A drag is one command being
///         extended rather than four hundred commands, and "undo that" means the stroke — which is
///         what every paint application does and what an artist means. So the <em>before</em> image
///         is captured lazily as the rectangle grows, never recaptured for ground already covered.
///     </para>
///     <para>
///         ⚠ <b>The recorded rectangle has to be the one the kernel read, not the one it wrote.</b>
///         Smoothing and erosion read a sample beyond their footprint, so a record sized to the write
///         restores a rectangle whose border still holds post-stroke values — and the next smooth
///         over the same place pulls them back in. <see cref="Extend" /> grows by
///         <see cref="TerrainSculpt.NeighbourMargin" /> for that reason.
///     </para>
/// </remarks>
public sealed class TerrainStroke {
    readonly Terrain terrain;
    readonly TerrainEditLayer layer;
    readonly Dictionary<long, short> before = [];

    /// <summary>Begins recording a stroke on a layer.</summary>
    /// <param name="terrain">The terrain.</param>
    /// <param name="layer">The layer the stroke writes.</param>
    /// <exception cref="ArgumentException">The layer does not accept the brush.</exception>
    public TerrainStroke(Terrain terrain, TerrainEditLayer layer) {
        ArgumentNullException.ThrowIfNull(terrain);
        ArgumentNullException.ThrowIfNull(layer);

        if (!layer.AcceptsBrush) {
            throw new ArgumentException(
                layer.IsLocked
                    ? $"The layer '{layer.Name}' is locked."
                    : $"The layer '{layer.Name}' is managed by the {layer.Kind} generator and is "
                    + "regenerated wholesale, so a hand edit would be discarded the next time it ran.",
                nameof(layer)
            );
        }

        this.terrain = terrain;
        this.layer = layer;
    }

    /// <summary>Everything the stroke has touched.</summary>
    public TerrainRect Rect { get; private set; } = TerrainRect.Empty;

    /// <summary>Which layer it wrote.</summary>
    public TerrainEditLayer Layer => layer;

    /// <summary>How many samples the record holds.</summary>
    public int RecordedSamples => before.Count;

    /// <summary>How many bytes the record occupies, both images together.</summary>
    public long Bytes => (long)before.Count * (sizeof(short) + sizeof(long)) * 2;

    /// <summary>Whether anything has been recorded.</summary>
    public bool IsEmpty => before.Count == 0;

    /// <summary>
    ///     Records the ground a stamp is about to change, and says which samples those are.
    /// </summary>
    /// <param name="brush">The brush about to be applied.</param>
    /// <param name="stamp">Where it is about to land.</param>
    /// <returns>The samples the stamp can reach.</returns>
    /// <remarks>
    ///     ⚠ <b>The safe way to use a stroke, and the reason it exists rather than only
    ///     <see cref="Extend" />.</b> A record holds what the ground <em>was</em>, so it has to be
    ///     taken before the kernel runs — and a caller who has to fetch the rectangle from the
    ///     kernel's return value can only take it afterwards, which records what the kernel wrote and
    ///     produces an undo that restores the stroke it was supposed to remove. This computes the
    ///     rectangle itself so the wrong order is not expressible.
    /// </remarks>
    public TerrainRect Record(in TerrainBrush brush, in BrushStamp stamp) {
        var rect = TerrainSculpt.AffectedRect(terrain.Description, brush, stamp);
        Extend(rect);
        return rect;
    }

    /// <summary>
    ///     Records the ground a rectangle covers. Call <em>before</em> applying the kernel.
    /// </summary>
    /// <param name="rect">What the kernel is about to write.</param>
    /// <remarks>
    ///     <para>
    ///         Grown by the neighbour margin, and idempotent per sample — a drag crossing the same
    ///         ground forty times records it once, holding the value it had before the first
    ///         crossing.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Prefer <see cref="Record" />.</b> This takes the rectangle from the caller, which
    ///         means the caller can pass one it obtained from the kernel's return value — after the
    ///         kernel ran. Nothing here can detect that, and the result is an undo that restores the
    ///         stroke instead of removing it. It stays public for the tools whose footprint is not a
    ///         stamp, which is the ramp.
    ///     </para>
    /// </remarks>
    public void Extend(TerrainRect rect) {
        var grown = rect.Grow(TerrainSculpt.NeighbourMargin)
            .Clip(new(0, 0, terrain.Description.SamplesX, terrain.Description.SamplesZ));

        if (grown.IsEmpty) {
            return;
        }

        Rect = Rect.Union(grown);

        for (var z = grown.Z; z < grown.EndZ; z++) {
            for (var x = grown.X; x < grown.EndX; x++) {
                var key = ((long)z << 32) | (uint)x;

                // TryAdd, not an assignment: the first crossing is the one that holds the value the
                // stroke started from, and re-recording on a later crossing would make undo restore
                // the middle of the stroke.
                before.TryAdd(key, layer.DeltaAt(x, z));
            }
        }
    }

    /// <summary>Puts the layer back the way it was before the stroke.</summary>
    /// <returns>What the stroke had touched.</returns>
    public TerrainRect Undo() {
        foreach (var (key, delta) in before) {
            layer.SetDelta((int)(uint)key, (int)(key >> 32), delta);
        }

        terrain.Invalidate(Rect);
        return Rect;
    }

    /// <summary>Captures what the stroke left, so it can be redone.</summary>
    /// <returns>The record, which restores the stroke's result.</returns>
    /// <remarks>
    ///     Taken at pointer-up rather than accumulated, because the after image is only ever needed
    ///     once and building it as the stroke ran would double the record's cost for a stroke nobody
    ///     undoes — which is almost all of them.
    /// </remarks>
    public TerrainStrokeRedo Capture() {
        var after = new Dictionary<long, short>(before.Count);

        foreach (var key in before.Keys) {
            after[key] = layer.DeltaAt((int)(uint)key, (int)(key >> 32));
        }

        return new(terrain, layer, after, Rect);
    }
}

/// <summary>What a stroke left, so it can be put back after an undo.</summary>
/// <remarks>
///     A separate type from <see cref="TerrainStroke" /> because it is a different lifetime: the
///     stroke exists while the pointer is down and this exists for as long as the undo stack holds
///     the entry.
/// </remarks>
public sealed class TerrainStrokeRedo {
    readonly Terrain terrain;
    readonly TerrainEditLayer layer;
    readonly Dictionary<long, short> after;

    internal TerrainStrokeRedo(
        Terrain terrain,
        TerrainEditLayer layer,
        Dictionary<long, short> after,
        TerrainRect rect
    ) {
        this.terrain = terrain;
        this.layer = layer;
        this.after = after;
        Rect = rect;
    }

    /// <summary>Everything the stroke touched.</summary>
    public TerrainRect Rect { get; }

    /// <summary>How many samples the record holds.</summary>
    public int RecordedSamples => after.Count;

    /// <summary>Puts the stroke's result back.</summary>
    /// <returns>What it touched.</returns>
    public TerrainRect Redo() {
        foreach (var (key, delta) in after) {
            layer.SetDelta((int)(uint)key, (int)(key >> 32), delta);
        }

        terrain.Invalidate(Rect);
        return Rect;
    }
}

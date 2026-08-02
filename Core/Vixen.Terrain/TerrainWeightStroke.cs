// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

namespace Vixen.Terrain;

/// <summary>
///     One paint stroke, recorded so it can be undone.
/// </summary>
/// <remarks>
///     <para>
///         <b>The third stroke record, and the one that costs the most per sample.</b>
///         <see cref="TerrainStroke" /> holds one <c>short</c> per sample because a sculpt tool
///         writes one layer's delta; <see cref="TerrainWeightStroke" /> holds <em>every</em> layer's
///         weight, because painting one layer lowers all the others proportionally. A six-layer
///         terrain records six bytes per sample rather than one — still less than the sculpt record's
///         two shorts, which is a coincidence worth not relying on.
///     </para>
///     <para>
///         ⚠ <b>Recording only the target layer is the bug this type exists to prevent.</b> An undo
///         that restored one channel would leave the other five holding what the redistribution gave
///         them, so the sum at every touched sample would come out above 255 — and
///         <see cref="TerrainWeights.Verify" /> would report a drift whose cause is three operations
///         in the past.
///     </para>
///     <para>
///         ⚠ <b>The before image is captured lazily and never re-captured</b>, exactly as
///         <see cref="TerrainStroke.Extend" /> does it: a drag crossing the same ground forty times
///         records it once, holding the weights it had before the first crossing.
///     </para>
/// </remarks>
public sealed class TerrainWeightStroke {
    readonly Terrain terrain;
    readonly Dictionary<long, byte[]> before = [];
    readonly int layerCount;

    /// <summary>Begins recording a paint stroke.</summary>
    /// <param name="terrain">The terrain.</param>
    /// <exception cref="ArgumentException">The terrain has no paint layers.</exception>
    public TerrainWeightStroke(Terrain terrain) {
        ArgumentNullException.ThrowIfNull(terrain);

        if (terrain.Weights.LayerCount == 0) {
            throw new ArgumentException(
                "The terrain has no paint layers, so there is nothing a stroke could record. "
                + "Add one before painting.",
                nameof(terrain)
            );
        }

        this.terrain = terrain;
        layerCount = terrain.Weights.LayerCount;
    }

    /// <summary>Everything the stroke has touched.</summary>
    public TerrainRect Rect { get; private set; } = TerrainRect.Empty;

    /// <summary>How many samples the record holds.</summary>
    public int RecordedSamples => before.Count;

    /// <summary>How many bytes it occupies, both images together.</summary>
    public long Bytes => (long)before.Count * (layerCount + sizeof(long)) * 2;

    /// <summary>Whether anything has been recorded.</summary>
    public bool IsEmpty => before.Count == 0;

    /// <summary>How many layers the record covers.</summary>
    /// <remarks>
    ///     Fixed at construction, which is what <see cref="Undo" /> checks against: a layer added or
    ///     removed mid-stroke would make the recorded rows the wrong width.
    /// </remarks>
    public int LayerCount => layerCount;

    /// <summary>
    ///     Records the weights a stamp is about to change, and says which samples those are.
    /// </summary>
    /// <param name="brush">The brush about to be applied.</param>
    /// <param name="stamp">Where it is about to land.</param>
    /// <returns>The samples the stamp can reach.</returns>
    /// <remarks>
    ///     Computes the rectangle itself, for <see cref="TerrainStroke.Record" />'s reason: a caller
    ///     who took it from the kernel's return value could only take it afterwards, which records
    ///     what the kernel wrote.
    /// </remarks>
    public TerrainRect Record(in TerrainBrush brush, in BrushStamp stamp) {
        var rect = TerrainPaint.AffectedRect(terrain.Description, brush, stamp);
        Extend(rect);
        return rect;
    }

    /// <summary>Records the weights a rectangle covers. Call <em>before</em> applying the kernel.</summary>
    /// <param name="rect">What the kernel is about to write.</param>
    /// <remarks>
    ///     Grown by <see cref="TerrainSculpt.NeighbourMargin" />, because the smooth tool reads a
    ///     sample beyond what it writes.
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

                if (before.ContainsKey(key)) {
                    continue;
                }

                before[key] = Read(x, z);
            }
        }
    }

    /// <summary>Puts the weights back the way they were before the stroke.</summary>
    /// <returns>What the stroke had touched.</returns>
    public TerrainRect Undo() {
        foreach (var (key, weights) in before) {
            Write((int)(uint)key, (int)(key >> 32), weights);
        }

        terrain.Invalidate(Rect);
        return Rect;
    }

    /// <summary>Captures what the stroke left, so it can be redone.</summary>
    /// <returns>The record, which restores the stroke's result.</returns>
    public TerrainWeightRedo Capture() {
        var after = new Dictionary<long, byte[]>(before.Count);

        foreach (var key in before.Keys) {
            after[key] = Read((int)(uint)key, (int)(key >> 32));
        }

        return new(terrain, after, Rect);
    }

    byte[] Read(int x, int z) {
        var weights = new byte[layerCount];

        for (var layer = 0; layer < layerCount; layer++) {
            weights[layer] = terrain.Weights.WeightAt(layer, x, z);
        }

        return weights;
    }

    /// <summary>Puts a whole sample's weights back, bypassing the redistribution.</summary>
    /// <remarks>
    ///     ⚠ <b>Through <see cref="TerrainWeights.Restore" /> rather than through
    ///     <c>SetWeight</c> once per layer.</b> <c>SetWeight</c> redistributes, so restoring six
    ///     layers one at a time would have the first five moved again by the sixth — an undo that
    ///     lands somewhere near where the stroke started and not on it. Restoring a whole sample at
    ///     once is the only spelling that is exact, and it is exact because the recorded row summed
    ///     to the total when it was taken.
    /// </remarks>
    void Write(int x, int z, byte[] weights) => terrain.Weights.Restore(x, z, weights);
}

/// <summary>What a paint stroke left, so it can be put back after an undo.</summary>
public sealed class TerrainWeightRedo {
    readonly Terrain terrain;
    readonly Dictionary<long, byte[]> after;

    internal TerrainWeightRedo(Terrain terrain, Dictionary<long, byte[]> after, TerrainRect rect) {
        this.terrain = terrain;
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
        foreach (var (key, weights) in after) {
            terrain.Weights.Restore((int)(uint)key, (int)(key >> 32), weights);
        }

        terrain.Invalidate(Rect);
        return Rect;
    }
}

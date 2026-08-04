// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

namespace Vixen.Terrain;

/// <summary>What owns an edit layer's contents.</summary>
public enum TerrainLayerKind {
    /// <summary>An author's. Sculpted and painted by hand.</summary>
    Manual,

    /// <summary>Regenerated wholesale from the terrain's splines. Not editable by hand.</summary>
    Splines,

    /// <summary>Regenerated wholesale by the scatter simulation. Not editable by hand.</summary>
    Scatter,

    /// <summary>Regenerated wholesale from the water bodies over it. Not editable by hand.</summary>
    /// <remarks>
    ///     <para>
    ///         [35 § B4](../../docs/plan/35-water.md#b4-the-terrains-reserved-layers-are-a-closed-set).
    ///         A lake, a river or an island carves the ground under it — a channel, a ramp to the
    ///         shoreline and a falloff outward — and does so non-destructively, so moving the body
    ///         restores the old ground and cuts the new.
    ///     </para>
    ///     <para>
    ///         <b>It needed no change to the contract, and that is the point of recording it here.</b>
    ///         The feature that most obviously wants non-destructive terrain deformation was not in
    ///         scope when [31 § D4](../../docs/plan/31-terrain-grass-and-trees.md) designed the
    ///         mechanism, and it is a third member of an enum rather than a mechanism of its own.
    ///     </para>
    /// </remarks>
    Water
}

/// <summary>
///     One non-destructive container of height deltas.
/// </summary>
/// <remarks>
///     <para>
///         <b>[docs/plan/31 § D4], and the reason it is the storage model rather than a feature on
///         top of one.</b> Unreal added edit layers seven years after Landscape shipped, which is why
///         they are still opt-in per landscape there: everything written against the flat heightmap
///         had to keep working. Building the composite in from the first commit means no tool ever
///         has a flat-heightmap path to preserve, and the reserved-layer mechanism splines and
///         scatter need is the same mechanism an author's own layers use.
///     </para>
///     <para>
///         <b>Sparse, in chunks that are not tiles.</b> A layer that touched three tiles stores three
///         tiles' worth, and the allocation granularity is a fixed
///         <see cref="ChunkSize" />-square block of the global sample grid rather than a tile —
///         deliberately, because chunks tile the grid <em>without overlapping</em> and tiles share
///         their boundary rows. A chunk grid aligned to tiles would put the shared row in two chunks,
///         which is [§ D2]'s seam bug reintroduced one level down.
///     </para>
///     <para>
///         <b>Signed deltas in the same units as the samples.</b> A delta of ±32 767 is half the
///         height range, which is as much as any one layer has ever needed to move the ground, and it
///         keeps a layer the same size as the base rather than twice it.
///     </para>
///     <para>
///         ⚠ <b><see cref="HeightAlpha" /> is signed, and a negative one subtracts.</b> That is
///         Unreal's semantic and it is genuinely useful — the same sculpt inverted is a valley — but
///         it means the composite has to clamp, and that the identity value is one rather than zero.
///     </para>
/// </remarks>
public sealed class TerrainEditLayer {
    /// <summary>How many samples a sparse chunk is along each axis.</summary>
    /// <remarks>
    ///     64, which is 8 KB of deltas. Small enough that a stroke in the middle of a big terrain
    ///     allocates kilobytes rather than megabytes, large enough that the dictionary holding them
    ///     stays short.
    /// </remarks>
    public const int ChunkSize = 64;

    readonly Dictionary<int, short[]> chunks = [];
    readonly int chunksX;
    readonly int chunksZ;

    /// <summary>Creates an empty layer over a terrain's shape.</summary>
    /// <param name="description">The terrain's shape.</param>
    /// <param name="name">What the layer is called.</param>
    /// <param name="kind">Who owns its contents.</param>
    public TerrainEditLayer(TerrainDescription description, string name, TerrainLayerKind kind = TerrainLayerKind.Manual) {
        Description = description;
        Name = name;
        Kind = kind;

        chunksX = ((description.SamplesX - 1) / ChunkSize) + 1;
        chunksZ = ((description.SamplesZ - 1) / ChunkSize) + 1;
    }

    /// <summary>The terrain's shape.</summary>
    public TerrainDescription Description { get; }

    /// <summary>What the layer is called.</summary>
    public string Name { get; set; }

    /// <summary>Who owns its contents.</summary>
    public TerrainLayerKind Kind { get; }

    /// <summary>How much of its height deltas reach the composite. Signed; 1 is unchanged.</summary>
    public float HeightAlpha { get; set; } = 1f;

    /// <summary>How much of its paint reaches the composite, 0…1.</summary>
    public float WeightAlpha { get; set; } = 1f;

    /// <summary>Whether it contributes at all.</summary>
    public bool IsVisible { get; set; } = true;

    /// <summary>Whether it refuses edits.</summary>
    public bool IsLocked { get; set; }

    /// <summary>Whether a tool may sculpt or paint into it by hand.</summary>
    /// <remarks>
    ///     A reserved layer is regenerated wholesale by whatever owns it, so a hand edit would be
    ///     discarded the next time a spline moved — silently, and an hour later. The panel greys it
    ///     and names the generator instead.
    /// </remarks>
    public bool AcceptsBrush => Kind == TerrainLayerKind.Manual && !IsLocked;

    /// <summary>How many chunks hold anything.</summary>
    public int ChunkCount => chunks.Count;

    /// <summary>How many bytes its deltas occupy.</summary>
    public long Bytes => (long)chunks.Count * ChunkSize * ChunkSize * sizeof(short);

    /// <summary>Whether it has nothing in it.</summary>
    public bool IsEmpty => chunks.Count == 0;

    /// <summary>A copy of the layer, deltas and all.</summary>
    /// <returns>The copy, which shares nothing with this one.</returns>
    /// <remarks>
    ///     ⚠ <b>What makes a destructive layer operation undoable.</b> Collapsing a layer down adds
    ///     its deltas into the one below and drops it, and the addition has no inverse worth
    ///     computing — the lower layer may already have held something at every sample. So the
    ///     command takes one of these first. Sparse chunks make it cheap for the case that matters: a
    ///     layer holding one building pad copies kilobytes.
    /// </remarks>
    public TerrainEditLayer Clone() {
        var copy = new TerrainEditLayer(Description, Name, Kind) {
            HeightAlpha = HeightAlpha,
            WeightAlpha = WeightAlpha,
            IsVisible = IsVisible,
            IsLocked = IsLocked
        };

        foreach (var (index, chunk) in chunks) {
            copy.chunks[index] = (short[])chunk.Clone();
        }

        return copy;
    }

    /// <summary>What this layer moves one sample by, before its alpha.</summary>
    /// <param name="x">The sample's X index.</param>
    /// <param name="z">The sample's Z index.</param>
    /// <returns>The delta, or zero where the layer has never been written.</returns>
    public short DeltaAt(int x, int z) {
        if ((uint)x >= (uint)Description.SamplesX || (uint)z >= (uint)Description.SamplesZ) {
            return 0;
        }

        return chunks.TryGetValue(ChunkIndex(x, z), out var chunk)
            ? chunk[((z % ChunkSize) * ChunkSize) + (x % ChunkSize)]
            : (short)0;
    }

    /// <summary>Sets what this layer moves one sample by.</summary>
    /// <param name="x">The sample's X index.</param>
    /// <param name="z">The sample's Z index.</param>
    /// <param name="delta">The delta.</param>
    /// <remarks>
    ///     Allocates the chunk on the first non-zero write. Writing a zero into a chunk that does not
    ///     exist allocates nothing, which is what keeps a brush stroke's clipped-away edges free.
    /// </remarks>
    public void SetDelta(int x, int z, short delta) {
        if ((uint)x >= (uint)Description.SamplesX || (uint)z >= (uint)Description.SamplesZ) {
            return;
        }

        var index = ChunkIndex(x, z);

        if (!chunks.TryGetValue(index, out var chunk)) {
            if (delta == 0) {
                return;
            }

            chunk = new short[ChunkSize * ChunkSize];
            chunks[index] = chunk;
        }

        chunk[((z % ChunkSize) * ChunkSize) + (x % ChunkSize)] = delta;
    }

    /// <summary>Adds to what this layer moves one sample by, saturating rather than wrapping.</summary>
    /// <param name="x">The sample's X index.</param>
    /// <param name="z">The sample's Z index.</param>
    /// <param name="delta">How much to add.</param>
    /// <remarks>
    ///     ⚠ <b>Saturating, and that is not a detail.</b> A brush held down on one spot accumulates
    ///     without bound; a wrapping add turns the top of a mountain into a pit the moment it crosses
    ///     32 767, which looks like the terrain exploding and is a one-character bug.
    /// </remarks>
    public void AddDelta(int x, int z, int delta) {
        if (delta == 0) {
            return;
        }

        var current = DeltaAt(x, z);
        SetDelta(x, z, (short)Math.Clamp(current + delta, short.MinValue, short.MaxValue));
    }

    /// <summary>Forgets everything the layer holds.</summary>
    public void Clear() => chunks.Clear();

    /// <summary>Forgets what the layer holds inside a rectangle.</summary>
    /// <param name="rect">Which samples.</param>
    /// <remarks>
    ///     The Erase tool: reverts this layer's sculpting to nothing without touching the layers
    ///     below, which is the correction a stack exists to make possible.
    /// </remarks>
    public void Clear(TerrainRect rect) {
        var clipped = rect.Clip(new(0, 0, Description.SamplesX, Description.SamplesZ));

        for (var z = clipped.Z; z < clipped.EndZ; z++) {
            for (var x = clipped.X; x < clipped.EndX; x++) {
                SetDelta(x, z, 0);
            }
        }
    }

    /// <summary>Which chunks hold anything, as their low corners in sample indices.</summary>
    /// <returns>The corners.</returns>
    /// <remarks>What a serializer walks, and what a memory report counts.</remarks>
    public IEnumerable<(int X, int Z)> OccupiedChunks() {
        foreach (var index in chunks.Keys) {
            yield return (index % chunksX * ChunkSize, index / chunksX * ChunkSize);
        }
    }

    int ChunkIndex(int x, int z) => (z / ChunkSize * chunksX) + (x / ChunkSize);

    /// <summary>Reads a sample's chunk-local delta without the bounds check.</summary>
    internal short DeltaUnchecked(int x, int z) =>
        chunks.TryGetValue((z / ChunkSize * chunksX) + (x / ChunkSize), out var chunk)
            ? chunk[((z % ChunkSize) * ChunkSize) + (x % ChunkSize)]
            : (short)0;

    /// <summary>How many chunks there are along Z, for a caller sizing a walk.</summary>
    internal int ChunksZ => chunksZ;
}

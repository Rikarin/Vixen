// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

namespace Vixen.Terrain;

/// <summary>
///     Which samples of a terrain are not there.
/// </summary>
/// <remarks>
///     <para>
///         What the Holes tool paints — a cave mouth, a doorway into a hillside, a shaft. One bit per
///         sample rather than a height, because a hole is not a very low piece of ground: it has to
///         remove the triangles <em>and</em> the collision, and a height cannot do the second.
///     </para>
///     <para>
///         ⚠ <b>One bit punches the quads around it, not one quad.</b> A quad is four samples, so a
///         hole at a sample kills the up-to-four quads that reference it. Painting a single sample
///         therefore opens a two-by-two hole, which surprises people once and is the only definition
///         that keeps the mesh and the collider agreeing — the physics side has exactly this rule and
///         it is not ours to choose.
///     </para>
/// </remarks>
public sealed class TerrainHoles {
    readonly ulong[] bits;
    readonly int width;
    readonly int height;

    /// <summary>Allocates a mask with no holes in it.</summary>
    /// <param name="description">The terrain's shape.</param>
    public TerrainHoles(TerrainDescription description)
        : this(description.SamplesX, description.SamplesZ) { }

    TerrainHoles(int width, int height) {
        this.width = width;
        this.height = height;
        bits = new ulong[(((long)width * height) + 63) / 64];
    }

    /// <summary>How many samples are holes.</summary>
    public int HoleCount { get; private set; }

    /// <summary>Whether anything has been punched at all.</summary>
    /// <remarks>
    ///     The fast path every consumer wants: a terrain with no holes skips the mask entirely rather
    ///     than testing a bit per sample per tile per rebuild.
    /// </remarks>
    public bool IsEmpty => HoleCount == 0;

    /// <summary>Whether a sample is a hole.</summary>
    /// <param name="x">Its X index.</param>
    /// <param name="z">Its Z index.</param>
    /// <returns>Whether it is a hole. Outside the terrain, false.</returns>
    public bool IsHole(int x, int z) {
        if ((uint)x >= (uint)width || (uint)z >= (uint)height) {
            return false;
        }

        var index = ((long)z * width) + x;
        return (bits[index / 64] & (1ul << (int)(index % 64))) != 0;
    }

    /// <summary>Punches or fills a sample.</summary>
    /// <param name="x">Its X index.</param>
    /// <param name="z">Its Z index.</param>
    /// <param name="hole">Whether it becomes a hole.</param>
    public void SetHole(int x, int z, bool hole) {
        if ((uint)x >= (uint)width || (uint)z >= (uint)height) {
            return;
        }

        var index = ((long)z * width) + x;
        var word = index / 64;
        var mask = 1ul << (int)(index % 64);
        var was = (bits[word] & mask) != 0;

        if (was == hole) {
            return;
        }

        bits[word] = hole ? bits[word] | mask : bits[word] & ~mask;
        HoleCount += hole ? 1 : -1;
    }

    /// <summary>Whether a quad has any of its four corners punched.</summary>
    /// <param name="x">The quad's low X index.</param>
    /// <param name="z">The quad's low Z index.</param>
    /// <returns>Whether the quad is missing.</returns>
    /// <remarks>
    ///     What a tile's index buffer is built against, and the other half of the rule in the class
    ///     remarks. Any corner, not all four: a quad with one corner missing has no shape to be.
    /// </remarks>
    public bool IsQuadMissing(int x, int z) =>
        IsHole(x, z) || IsHole(x + 1, z) || IsHole(x, z + 1) || IsHole(x + 1, z + 1);

    /// <summary>A copy of this mask.</summary>
    /// <returns>The copy, sharing nothing.</returns>
    public TerrainHoles Clone() {
        var copy = new TerrainHoles(width, height) { HoleCount = HoleCount };
        bits.CopyTo(copy.bits, 0);
        return copy;
    }
}

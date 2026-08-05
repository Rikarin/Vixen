// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Graphics;

namespace Vixen.Core.Imaging;

/// <summary>Where one level of a KTX2 file is, said without having read it.</summary>
/// <param name="Level">Which mip level, zero being the largest.</param>
/// <param name="Width">Its width in pixels.</param>
/// <param name="Height">Its height in pixels.</param>
/// <param name="Depth">Its depth in pixels.</param>
/// <param name="Offset">Where its bytes start <em>in the file</em>.</param>
/// <param name="Length">How many bytes it is.</param>
/// <remarks>
///     ⚠ <see cref="Offset" /> is a file offset, unlike <see cref="TextureLevel.Offset" />, which is
///     an offset into a decoded texture's buffer. The two run in opposite directions — see
///     <see cref="Ktx2" /> on why the file stores levels smallest first — so mixing them up produces
///     a texture whose mips are in the wrong order rather than an error.
/// </remarks>
public readonly record struct Ktx2Level(int Level, int Width, int Height, int Depth, long Offset, long Length);

/// <summary>Everything a KTX2 file's header and level index say, and none of its pixels.</summary>
/// <remarks>
///     <para>
///         <b>The half of <see cref="Ktx2.Read" /> that a streamer can afford.</b> A header and a
///         level index are <c>80 + 24n</c> bytes at the front of the file; the pixels are everything
///         else. Reading the first of those says how big the texture is, how many levels it has and
///         where each one lives, which is enough to decide what to load without having loaded any of
///         it.
///     </para>
///     <para>
///         <b>The mip tail is one contiguous run at the front of the level data, and that is the
///         whole point.</b> Level data is stored smallest first, so levels <c>firstLevel</c> through
///         <c>LevelCount - 1</c> occupy a single range starting at <see cref="DataOffset" /> — one
///         seek and one read, whatever the resolution asked for. Were the file stored largest first
///         the same request would be a read per level from scattered offsets, which is the difference
///         between streaming a mip tail and streaming a mip tail badly.
///     </para>
/// </remarks>
public sealed class Ktx2Layout {
    readonly Ktx2Level[] levels;

    internal Ktx2Layout(
        PixelFormat format,
        int width,
        int height,
        int depth,
        int layerCount,
        int faceCount,
        Ktx2Level[] levels
    ) {
        Format = format;
        Width = width;
        Height = height;
        Depth = depth;
        LayerCount = layerCount;
        FaceCount = faceCount;
        this.levels = levels;
    }

    /// <summary>What the bytes mean.</summary>
    public PixelFormat Format { get; }

    /// <summary>The largest level's width.</summary>
    public int Width { get; }

    /// <summary>The largest level's height.</summary>
    public int Height { get; }

    /// <summary>The largest level's depth. One for a 2D texture.</summary>
    public int Depth { get; }

    /// <summary>How many array layers. One for a plain texture.</summary>
    public int LayerCount { get; }

    /// <summary>How many faces. Six for a cube map, one otherwise.</summary>
    public int FaceCount { get; }

    /// <summary>Every level, largest first, each with the file offset its bytes start at.</summary>
    public IReadOnlyList<Ktx2Level> Levels => levels;

    /// <summary>How many mip levels.</summary>
    public int LevelCount => levels.Length;

    /// <summary>Where the level data starts: the smallest level's offset.</summary>
    public long DataOffset => levels[^1].Offset;

    /// <summary>How many bytes every level together occupies.</summary>
    public long DataLength => levels[0].Offset + levels[0].Length - DataOffset;

    /// <summary>How many bytes the tail from a level down to the smallest occupies.</summary>
    /// <param name="firstLevel">The largest level of the tail.</param>
    /// <returns>The length of the contiguous run starting at <see cref="DataOffset" />.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="firstLevel" /> is not a level.</exception>
    public long TailLength(int firstLevel) {
        ArgumentOutOfRangeException.ThrowIfNegative(firstLevel);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(firstLevel, LevelCount);

        return levels[firstLevel].Offset + levels[firstLevel].Length - DataOffset;
    }

    /// <summary>The largest tail that fits in a budget.</summary>
    /// <param name="budget">How many bytes may be read.</param>
    /// <returns>
    ///     The level to start the tail at: <c>0</c> when the whole texture fits, and
    ///     <c>LevelCount - 1</c> when not even two levels do.
    /// </returns>
    /// <remarks>
    ///     The smallest level is returned whether or not it fits, because a texture with nothing
    ///     resident is a texture nothing can sample. A budget below one 1×1 level is a budget that
    ///     was going to be exceeded by the first texture whatever this answered.
    /// </remarks>
    public int TailFor(long budget) {
        for (var level = 0; level < LevelCount; level++) {
            if (TailLength(level) <= budget) {
                return level;
            }
        }

        return LevelCount - 1;
    }

    /// <summary>Describes the texture a tail of this file decodes to.</summary>
    /// <param name="firstLevel">The largest level of the tail.</param>
    /// <returns>An empty texture of the tail's extent and level count, ready to be filled.</returns>
    /// <remarks>
    ///     A mip tail is a whole smaller texture rather than a partial larger one — half the width and
    ///     height per level dropped, and one fewer level — which is what lets a partially streamed
    ///     texture be created, viewed and sampled by code that knows nothing about streaming.
    /// </remarks>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="firstLevel" /> is not a level.</exception>
    public TextureData Allocate(int firstLevel) {
        ArgumentOutOfRangeException.ThrowIfNegative(firstLevel);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(firstLevel, LevelCount);

        var described = levels[firstLevel];

        return new(
            Format,
            described.Width,
            described.Height,
            LevelCount - firstLevel,
            described.Depth,
            LayerCount,
            FaceCount
        );
    }
}

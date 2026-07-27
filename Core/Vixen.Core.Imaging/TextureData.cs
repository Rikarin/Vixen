// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Graphics;

namespace Vixen.Core.Imaging;

/// <summary>One mip level of one layer of one face.</summary>
/// <param name="Level">Which mip level, zero being the largest.</param>
/// <param name="Width">Its width in pixels.</param>
/// <param name="Height">Its height in pixels.</param>
/// <param name="Depth">Its depth in pixels.</param>
/// <param name="Offset">Where its bytes start in the texture's buffer.</param>
/// <param name="Length">How many bytes it is.</param>
public readonly record struct TextureLevel(int Level, int Width, int Height, int Depth, int Offset, int Length);

/// <summary>A texture in memory: the pixels, and enough to say what they mean.</summary>
/// <remarks>
///     <para>
///         One flat buffer with a level table over it, rather than an array of arrays. A texture is
///         written to a file and uploaded to a GPU as a contiguous run of bytes in both cases, and a
///         shape that matches those is one that neither has to copy to reach.
///     </para>
///     <para>
///         Levels are ordered largest first, which is the order everything except the KTX2 file
///         format uses. <see cref="Ktx2" /> reverses on the way out and back on the way in, and that
///         is the only place the other order exists.
///     </para>
/// </remarks>
public sealed class TextureData {
    readonly byte[] pixels;

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

    /// <summary>Every level of every face of every layer, largest level first.</summary>
    public IReadOnlyList<TextureLevel> Levels { get; }

    /// <summary>How many mip levels.</summary>
    public int LevelCount { get; }

    /// <summary>All of the pixels, in level order.</summary>
    public ReadOnlySpan<byte> Pixels => pixels;

    /// <summary>How many bytes the whole texture is.</summary>
    public int ByteLength => pixels.Length;

    /// <summary>Describes a texture and allocates its buffer.</summary>
    /// <param name="format">What the bytes mean.</param>
    /// <param name="width">The largest level's width.</param>
    /// <param name="height">The largest level's height.</param>
    /// <param name="levelCount">How many mip levels, or zero for a full chain.</param>
    /// <param name="depth">The largest level's depth.</param>
    /// <param name="layerCount">How many array layers.</param>
    /// <param name="faceCount">How many faces — six for a cube map.</param>
    /// <exception cref="ArgumentOutOfRangeException">An extent or a count is not positive.</exception>
    public TextureData(
        PixelFormat format,
        int width,
        int height,
        int levelCount = 0,
        int depth = 1,
        int layerCount = 1,
        int faceCount = 1
    ) {
        ArgumentOutOfRangeException.ThrowIfLessThan(width, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(height, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(depth, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(layerCount, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(faceCount, 1);
        ArgumentOutOfRangeException.ThrowIfNegative(levelCount);

        Format = format;
        Width = width;
        Height = height;
        Depth = depth;
        LayerCount = layerCount;
        FaceCount = faceCount;
        LevelCount = levelCount == 0 ? PixelFormats.MipLevelCount(width, height, depth) : levelCount;

        var levels = new List<TextureLevel>(LevelCount);
        var offset = 0;

        for (var level = 0; level < LevelCount; level++) {
            var (levelWidth, levelHeight, levelDepth) = MipChain.ExtentOf(width, height, depth, level);
            var length = checked((int)format.LevelSize(levelWidth, levelHeight, levelDepth)) * layerCount * faceCount;
            levels.Add(new(level, levelWidth, levelHeight, levelDepth, offset, length));
            offset += length;
        }

        Levels = levels;
        pixels = new byte[offset];
    }

    /// <summary>One level's bytes, for reading.</summary>
    /// <param name="level">Which level.</param>
    /// <returns>Its bytes.</returns>
    public ReadOnlySpan<byte> Level(int level) {
        var described = Levels[level];
        return pixels.AsSpan(described.Offset, described.Length);
    }

    /// <summary>One level's bytes, for writing.</summary>
    /// <param name="level">Which level.</param>
    /// <returns>Its bytes.</returns>
    public Span<byte> LevelSpan(int level) {
        var described = Levels[level];
        return pixels.AsSpan(described.Offset, described.Length);
    }

    /// <summary>All of the pixels, for writing.</summary>
    /// <returns>The buffer.</returns>
    public Span<byte> PixelSpan() => pixels;
}

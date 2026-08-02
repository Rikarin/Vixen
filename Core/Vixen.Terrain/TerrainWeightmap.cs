// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

namespace Vixen.Terrain;

/// <summary>
///     One paint layer's coverage, as an 8-bit grayscale image.
/// </summary>
/// <remarks>
///     <para>
///         <b>The weightmap half of [docs/plan/31 § The terrain panel]'s import and export.</b> One
///         layer at a time, one byte per sample, no header — the same bargain
///         <see cref="TerrainHeightmap" /> makes for raw <c>r16</c>: bytes need no image library, so
///         they live in the kernel, and PNG lives with the importer that already depends on
///         <c>Vixen.Core.Imaging</c>.
///     </para>
///     <para>
///         ⚠ <b>An import restores the invariant rather than trusting the file.</b> A weightmap
///         painted in an external tool has no idea the other layers exist, so writing it verbatim
///         leaves every sample it touched summing to something other than 255. What
///         <see cref="Import" /> does is set the layer and let
///         <see cref="TerrainWeights.SetWeight" /> take the difference from the rest — which is
///         exactly what painting it by hand would have done.
///     </para>
///     <para>
///         ⚠ <b>And it resamples, for <see cref="TerrainHeightmap" />'s reason.</b> A terrain of four
///         128-sample tiles is 509 samples across and a mask authored in an image editor is 512. The
///         resample is bilinear and edge-to-edge, so the mask's corners land on the terrain's.
///     </para>
/// </remarks>
public static class TerrainWeightmap {
    /// <summary>How many bytes a weightmap of a terrain is.</summary>
    /// <param name="description">The terrain's shape.</param>
    /// <returns>The count: one byte per sample.</returns>
    public static long ByteCount(in TerrainDescription description) => description.SampleCount;

    /// <summary>Reads a grayscale mask into a paint layer.</summary>
    /// <param name="terrain">The terrain.</param>
    /// <param name="layer">Which paint layer.</param>
    /// <param name="bytes">The mask, one byte per pixel, row-major.</param>
    /// <param name="width">How wide it is.</param>
    /// <param name="height">How tall.</param>
    /// <exception cref="ArgumentException">The bytes do not match the size given.</exception>
    public static void Import(Terrain terrain, int layer, ReadOnlySpan<byte> bytes, int width, int height) {
        ArgumentNullException.ThrowIfNull(terrain);
        ArgumentOutOfRangeException.ThrowIfNegative(layer);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(layer, terrain.Weights.LayerCount);

        if (width < 1 || height < 1) {
            throw new ArgumentException($"A weightmap of {width}×{height} has no pixels.", nameof(width));
        }

        if (bytes.Length < (long)width * height) {
            throw new ArgumentException(
                $"A {width}×{height} weightmap is {(long)width * height} bytes, not {bytes.Length}.",
                nameof(bytes)
            );
        }

        var description = terrain.Description;

        for (var z = 0; z < description.SamplesZ; z++) {
            for (var x = 0; x < description.SamplesX; x++) {
                terrain.Weights.SetWeight(
                    layer,
                    x,
                    z,
                    SampleBilinear(bytes, width, height, x, z, description.SamplesX, description.SamplesZ)
                );
            }
        }

        terrain.InvalidateAll();
    }

    /// <summary>Writes a paint layer's coverage as a grayscale mask.</summary>
    /// <param name="terrain">The terrain.</param>
    /// <param name="layer">Which paint layer.</param>
    /// <param name="destination">Where to put the bytes.</param>
    /// <returns>How many bytes were written.</returns>
    /// <exception cref="ArgumentException">There is not enough room.</exception>
    public static int Export(Terrain terrain, int layer, Span<byte> destination) {
        ArgumentNullException.ThrowIfNull(terrain);
        ArgumentOutOfRangeException.ThrowIfNegative(layer);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(layer, terrain.Weights.LayerCount);

        var required = (int)ByteCount(terrain.Description);

        if (destination.Length < required) {
            throw new ArgumentException(
                $"A weightmap of this terrain is {required} bytes, not {destination.Length}.",
                nameof(destination)
            );
        }

        terrain.Weights.ChannelOf(layer).CopyTo(destination);
        return required;
    }

    /// <summary>A mask sampled at a terrain sample, edge to edge.</summary>
    static byte SampleBilinear(
        ReadOnlySpan<byte> bytes,
        int width,
        int height,
        int x,
        int z,
        int samplesX,
        int samplesZ
    ) {
        // Corner-pinned: sample 0 reads pixel 0 and the last sample reads the last pixel, so the
        // mask's edges land on the terrain's rather than a fraction of a pixel short of them.
        var sourceX = samplesX > 1 ? x * (width - 1f) / (samplesX - 1) : 0f;
        var sourceZ = samplesZ > 1 ? z * (height - 1f) / (samplesZ - 1) : 0f;

        var x0 = Math.Clamp((int)sourceX, 0, width - 1);
        var z0 = Math.Clamp((int)sourceZ, 0, height - 1);
        var x1 = Math.Min(x0 + 1, width - 1);
        var z1 = Math.Min(z0 + 1, height - 1);

        var fx = sourceX - x0;
        var fz = sourceZ - z0;

        var top = float.Lerp(bytes[(z0 * width) + x0], bytes[(z0 * width) + x1], fx);
        var bottom = float.Lerp(bytes[(z1 * width) + x0], bytes[(z1 * width) + x1], fx);

        return (byte)Math.Clamp(MathF.Round(float.Lerp(top, bottom, fz)), 0f, TerrainWeights.Total);
    }
}

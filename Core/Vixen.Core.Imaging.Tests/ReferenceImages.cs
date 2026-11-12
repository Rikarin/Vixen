// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Graphics;

namespace Vixen.Core.Imaging.Tests;

/// <summary>The closed-form pictures block compression is measured on, and the measure.</summary>
/// <remarks>
///     <para>
///         <b>Every one of these is a formula.</b> A downloaded test picture is a licence, a blob and
///         a number nobody can re-derive; these are the same bytes on every machine for ever, and
///         each one is built to stress a particular thing an encoder has to get right. Feature sizes
///         are in texels rather than fractions of the image, so a 4096² render has the same density
///         of edges per block as a 256² one and the PSNR does not drift with the size it was measured
///         at.
///     </para>
///     <para>
///         <c>Benchmarks/Vixen.Benchmarks.Imaging</c> links this file, so the report and the targets
///         the tests hold are about the same pixels.
///     </para>
/// </remarks>
public static class ReferenceImages {
    /// <summary>The eight-bit pictures, by the name the report uses.</summary>
    public static IReadOnlyList<(string Name, Func<int, TextureData> Make)> LowDynamicRange { get; } = [
        ("gradient", Gradient),
        ("two-colour edges", TwoColourEdges),
        ("three-colour edges", ThreeColourEdges),
        ("filtered noise", FilteredNoise),
        ("independent alpha", IndependentAlpha),
        ("normal map", NormalMap)
    ];

    /// <summary>Three ramps that disagree: smooth content, where a line through colour space should win.</summary>
    /// <param name="size">The width and height.</param>
    /// <returns>An <see cref="PixelFormat.Rgba8UNorm" /> image.</returns>
    public static TextureData Gradient(int size) =>
        Fill(
            size,
            (x, y) => {
                var u = x / (float)(size - 1);
                var v = y / (float)(size - 1);

                return (u, v, 1f - 0.5f * (u + v), 1f);
            }
        );

    /// <summary>
    ///     Hard slanted stripes of two colours that share no line with neutral grey. Roughly every
    ///     other block holds an edge, which is what partitioned modes exist for.
    /// </summary>
    /// <param name="size">The width and height.</param>
    /// <returns>An <see cref="PixelFormat.Rgba8UNorm" /> image.</returns>
    public static TextureData TwoColourEdges(int size) =>
        Fill(
            size,
            static (x, y) => {
                var stripe = MathF.Floor((x * 0.93f + y * 0.37f) / 11f);

                return ((int)stripe & 1) == 0 ? (0.85f, 0.15f, 0.20f, 1f) : (0.10f, 0.70f, 0.55f, 1f);
            }
        );

    /// <summary>
    ///     Two families of hard stripes crossing, in three colours. Blocks on a junction hold all
    ///     three, which is what the three-subset modes exist for.
    /// </summary>
    /// <param name="size">The width and height.</param>
    /// <returns>An <see cref="PixelFormat.Rgba8UNorm" /> image.</returns>
    public static TextureData ThreeColourEdges(int size) =>
        Fill(
            size,
            static (x, y) => {
                var first = (int)MathF.Floor((x * 0.93f + y * 0.37f) / 9f);
                var second = (int)MathF.Floor((x * -0.31f + y * 0.95f) / 7f);

                return (((first + second * 2) % 3 + 3) % 3) switch {
                    0 => (0.90f, 0.20f, 0.10f, 1f),
                    1 => (0.15f, 0.75f, 0.25f, 1f),
                    _ => (0.20f, 0.25f, 0.85f, 1f)
                };
            }
        );

    /// <summary>Band-limited colour noise: what most of a material's detail looks like to an encoder.</summary>
    /// <param name="size">The width and height.</param>
    /// <returns>An <see cref="PixelFormat.Rgba8UNorm" /> image.</returns>
    public static TextureData FilteredNoise(int size) =>
        Fill(size, static (x, y) => (Octaves(x, y, 0x11), Octaves(x, y, 0x22), Octaves(x, y, 0x33), 1f));

    /// <summary>Noise over an alpha that has nothing to do with it: soft rings, some of them cut hard.</summary>
    /// <param name="size">The width and height.</param>
    /// <returns>An <see cref="PixelFormat.Rgba8UNorm" /> image.</returns>
    public static TextureData IndependentAlpha(int size) =>
        Fill(
            size,
            static (x, y) => {
                var ring = MathF.Sin(MathF.Sqrt(x * x + y * y) * 0.21f);
                var alpha = ring > 0.75f ? 0f : 0.5f + 0.5f * MathF.Sin(x * 0.13f - y * 0.07f);

                return (Octaves(x, y, 0x44), Octaves(x, y, 0x55), Octaves(x, y, 0x66), alpha);
            }
        );

    /// <summary>A tangent-space normal map of two sine ridges and some noise, from the analytic slope.</summary>
    /// <param name="size">The width and height.</param>
    /// <returns>An <see cref="PixelFormat.Rgba8UNorm" /> image.</returns>
    public static TextureData NormalMap(int size) =>
        Fill(
            size,
            static (x, y) => {
                var dx = 0.55f * MathF.Cos(x * 0.19f) + 0.3f * (2f * Octaves(x, y, 0x77) - 1f);
                var dy = 0.45f * MathF.Cos(y * 0.23f + x * 0.05f) + 0.3f * (2f * Octaves(x, y, 0x88) - 1f);
                var length = MathF.Sqrt(dx * dx + dy * dy + 1f);

                return (0.5f - 0.5f * dx / length, 0.5f - 0.5f * dy / length, 0.5f + 0.5f / length, 1f);
            }
        );

    /// <summary>
    ///     Soft lights on a dim wall and a window with a hard slanted edge eighty times brighter than
    ///     the room: a sum of exponentials, with the discontinuity BC6H's partitions are for.
    /// </summary>
    /// <param name="size">The width and height.</param>
    /// <returns>An <see cref="PixelFormat.Rgba16Float" /> image.</returns>
    public static TextureData HighDynamicRange(int size) {
        var texture = new TextureData(PixelFormat.Rgba16Float, size, size, 1);
        var pixels = texture.PixelSpan();

        for (var y = 0; y < size; y++) {
            for (var x = 0; x < size; x++) {
                var (r, g, b) = Radiance(x, y);
                var at = (y * size + x) * 8;

                BitConverter.TryWriteBytes(pixels[at..], (Half)r);
                BitConverter.TryWriteBytes(pixels[(at + 2)..], (Half)g);
                BitConverter.TryWriteBytes(pixels[(at + 4)..], (Half)b);
                BitConverter.TryWriteBytes(pixels[(at + 6)..], (Half)1f);
            }
        }

        return texture;
    }

    /// <summary>The same picture shifted so a third of it is negative, for the signed BC6H.</summary>
    /// <param name="size">The width and height.</param>
    /// <returns>An <see cref="PixelFormat.Rgba16Float" /> image.</returns>
    public static TextureData SignedHighDynamicRange(int size) {
        var texture = new TextureData(PixelFormat.Rgba16Float, size, size, 1);
        var pixels = texture.PixelSpan();

        for (var y = 0; y < size; y++) {
            for (var x = 0; x < size; x++) {
                var (r, g, b) = Radiance(x, y);
                var at = (y * size + x) * 8;
                var sign = MathF.Sin(x * 0.03f) + MathF.Cos(y * 0.041f) > 0.5f ? -1f : 1f;

                BitConverter.TryWriteBytes(pixels[at..], (Half)(sign * r));
                BitConverter.TryWriteBytes(pixels[(at + 2)..], (Half)(sign * g));
                BitConverter.TryWriteBytes(pixels[(at + 4)..], (Half)(sign * b));
                BitConverter.TryWriteBytes(pixels[(at + 6)..], (Half)1f);
            }
        }

        return texture;
    }

    /// <summary>The peak signal-to-noise ratio of two eight-bit images over their first channels.</summary>
    /// <param name="original">The original, four bytes per texel.</param>
    /// <param name="decoded">What came back, in the same layout.</param>
    /// <param name="channels">How many of the four channels count.</param>
    /// <returns>Decibels, with 255 as the peak. <see cref="double.PositiveInfinity" /> when identical.</returns>
    public static double Psnr(ReadOnlySpan<byte> original, ReadOnlySpan<byte> decoded, int channels = 4) {
        var total = 0L;
        var texels = original.Length / 4;

        for (var texel = 0; texel < texels; texel++) {
            for (var channel = 0; channel < channels; channel++) {
                var difference = original[texel * 4 + channel] - decoded[texel * 4 + channel];
                total += difference * difference;
            }
        }

        return FromError((double)total / (texels * channels), 255.0);
    }

    /// <summary>
    ///     The peak signal-to-noise ratio of two <see cref="PixelFormat.Rgba16Float" /> images, over
    ///     RGB, in half-float bit space.
    /// </summary>
    /// <param name="original">The original.</param>
    /// <param name="decoded">What came back.</param>
    /// <returns>Decibels, with 31 743 — half's largest finite pattern — as the peak.</returns>
    /// <remarks>
    ///     A half's bit pattern is close to 1024·log2 of its value, so this is a logarithmic error:
    ///     1024 units is one stop, and a pattern off by 31 is 2% off in light. It is the space BC6H
    ///     interpolates in and the one the encoder minimises. A negative half is a mirrored positive
    ///     one here, because the same is true of the signed format.
    /// </remarks>
    public static double HalfPsnr(ReadOnlySpan<byte> original, ReadOnlySpan<byte> decoded) {
        var total = 0L;
        var texels = original.Length / 8;

        for (var texel = 0; texel < texels; texel++) {
            for (var channel = 0; channel < 3; channel++) {
                var at = texel * 8 + channel * 2;
                long difference = Pattern(original, at) - Pattern(decoded, at);
                total += difference * difference;
            }
        }

        return FromError((double)total / (texels * 3), 31743.0);

        static int Pattern(ReadOnlySpan<byte> pixels, int at) {
            var bits = pixels[at] | (pixels[at + 1] << 8);
            return (bits & 0x8000) != 0 ? -(bits & 0x7FFF) : bits;
        }
    }

    static double FromError(double meanSquare, double peak) =>
        meanSquare == 0.0 ? double.PositiveInfinity : 10.0 * Math.Log10(peak * peak / meanSquare);

    static TextureData Fill(int size, Func<int, int, (float R, float G, float B, float A)> shade) {
        var texture = new TextureData(PixelFormat.Rgba8UNorm, size, size, 1);
        var pixels = texture.PixelSpan();

        for (var y = 0; y < size; y++) {
            for (var x = 0; x < size; x++) {
                var (r, g, b, a) = shade(x, y);
                var at = (y * size + x) * 4;

                pixels[at] = Byte(r);
                pixels[at + 1] = Byte(g);
                pixels[at + 2] = Byte(b);
                pixels[at + 3] = Byte(a);
            }
        }

        return texture;
    }

    static byte Byte(float value) => (byte)Math.Clamp((int)(value * 255f + 0.5f), 0, 255);

    static (float R, float G, float B) Radiance(int x, int y) {
        var r = 0.02f;
        var g = 0.03f;
        var b = 0.05f;

        // Lights on a sixty-four-texel tile, so every part of a large image has one nearby.
        var u = x % 256;
        var v = y % 256;

        Add(u, v, 60f, 80f, 30f, 12f, 9f, 6f, ref r, ref g, ref b);
        Add(u, v, 180f, 40f, 18f, 2f, 5f, 16f, ref r, ref g, ref b);
        Add(u, v, 120f, 190f, 45f, 3f, 2.5f, 1f, ref r, ref g, ref b);

        // The window: a hard slanted edge, with the sky behind it far above anything in the room.
        var across = x * 0.97f + y * 0.24f;

        if (across % 128f < 41f) {
            var sky = 1f + 0.25f * MathF.Sin(y * 0.02f);
            r += 40f * sky;
            g += 55f * sky;
            b += 80f * sky;
        }

        return (r, g, b);

        static void Add(
            float u,
            float v,
            float cx,
            float cy,
            float reach,
            float red,
            float green,
            float blue,
            ref float r,
            ref float g,
            ref float b
        ) {
            var distance = MathF.Sqrt((u - cx) * (u - cx) + (v - cy) * (v - cy));
            var falloff = MathF.Exp(-distance / reach);

            r += red * falloff;
            g += green * falloff;
            b += blue * falloff;
        }
    }

    /// <summary>Three octaves of value noise, zero to one, from a fixed hash.</summary>
    static float Octaves(int x, int y, uint seed) =>
        0.5f * Lattice(x, y, 13, seed)
        + 0.3f * Lattice(x, y, 5, seed + 1)
        + 0.2f * Lattice(x, y, 2, seed + 2);

    /// <summary>Bilinear interpolation of hashed values on a lattice <paramref name="cell" /> texels apart.</summary>
    static float Lattice(int x, int y, int cell, uint seed) {
        var cellX = x / cell;
        var cellY = y / cell;
        var u = x % cell / (float)cell;
        var v = y % cell / (float)cell;

        var top = Lerp(Hash(cellX, cellY, seed), Hash(cellX + 1, cellY, seed), u);
        var bottom = Lerp(Hash(cellX, cellY + 1, seed), Hash(cellX + 1, cellY + 1, seed), u);

        return Lerp(top, bottom, v);

        static float Lerp(float a, float b, float t) => a + (b - a) * t;
    }

    /// <summary>A fixed integer hash to zero-to-one: MurmurHash3's finaliser over the mixed coordinates.</summary>
    static float Hash(int x, int y, uint seed) {
        var hash = unchecked(((uint)x * 0x9E3779B1u) ^ ((uint)y * 0x85EBCA77u) ^ (seed * 0xC2B2AE3Du));

        hash ^= hash >> 16;
        hash = unchecked(hash * 0x85EBCA6Bu);
        hash ^= hash >> 13;
        hash = unchecked(hash * 0xC2B2AE35u);
        hash ^= hash >> 16;

        return (hash & 0xFFFFFF) / (float)0xFFFFFF;
    }
}

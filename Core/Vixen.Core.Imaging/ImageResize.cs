// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

namespace Vixen.Core.Imaging;

/// <summary>How a picture's texels are combined when it changes size.</summary>
public enum ResizeFilter {
    /// <summary>
    ///     Each texel is the area-weighted mean of the source texels under it. Right for making a
    ///     picture smaller, where every source texel has to vote; when growing, it is
    ///     <see cref="Nearest" /> with exact edges.
    /// </summary>
    Box = 0,

    /// <summary>Each texel interpolates the four source texels round its centre. Right for growing a picture.</summary>
    Bilinear = 1,

    /// <summary>Each texel is the source texel under its centre: no new values, which is what an id or a mask wants.</summary>
    Nearest = 2
}

/// <summary>Changes a <see cref="FloatImage" />'s size.</summary>
/// <remarks>
///     <para>
///         ⚠ <b>Bilinear is the wrong filter for making a picture much smaller</b>: at a quarter of
///         the size it reads four texels out of every sixteen and ignores the rest, which aliases a
///         fine pattern into a coarse one. That is why <see cref="ResizeFilter.Box" /> is the
///         default, and why a mip chain is not built this way.
///     </para>
///     <para>
///         Every lane is filtered alike, on the numbers it holds. A caller that holds sRGB colour
///         and wants it averaged in linear light converts first, as <see cref="MipChain" /> does.
///     </para>
/// </remarks>
public static class ImageResize {
    /// <summary>A copy of a picture at another size.</summary>
    /// <param name="image">The picture.</param>
    /// <param name="width">The new width.</param>
    /// <param name="height">The new height.</param>
    /// <param name="filter">How texels are combined.</param>
    /// <returns>The new picture, or a copy at the same size.</returns>
    /// <exception cref="ArgumentOutOfRangeException">A size is not positive.</exception>
    public static FloatImage Resize(FloatImage image, int width, int height, ResizeFilter filter = ResizeFilter.Box) {
        ArgumentNullException.ThrowIfNull(image);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);

        if (width == image.Width && height == image.Height) {
            return image with { Samples = (float[])image.Samples.Clone() };
        }

        return filter switch {
            ResizeFilter.Nearest => Nearest(image, width, height),
            ResizeFilter.Bilinear => Bilinear(image, width, height),
            _ => Box(image, width, height)
        };
    }

    static FloatImage Nearest(FloatImage image, int width, int height) {
        var lanes = image.Channels;
        var samples = new float[width * height * lanes];

        Parallel.For(
            0,
            height,
            y => {
                var sy = Math.Min(image.Height - 1, (int)((y + 0.5) * image.Height / height));

                for (var x = 0; x < width; x++) {
                    var sx = Math.Min(image.Width - 1, (int)((x + 0.5) * image.Width / width));
                    Array.Copy(image.Samples, (sy * image.Width + sx) * lanes, samples, (y * width + x) * lanes, lanes);
                }
            }
        );

        return new(width, height, lanes, samples);
    }

    static FloatImage Bilinear(FloatImage image, int width, int height) {
        var lanes = image.Channels;
        var samples = new float[width * height * lanes];

        Parallel.For(
            0,
            height,
            y => {
                var v = Math.Clamp((float)((y + 0.5) * image.Height / height - 0.5), 0f, image.Height - 1);
                var top = (int)v;
                var bottom = Math.Min(top + 1, image.Height - 1);
                var t = v - top;

                for (var x = 0; x < width; x++) {
                    var u = Math.Clamp((float)((x + 0.5) * image.Width / width - 0.5), 0f, image.Width - 1);
                    var left = (int)u;
                    var right = Math.Min(left + 1, image.Width - 1);
                    var s = u - left;

                    for (var lane = 0; lane < lanes; lane++) {
                        var upper = float.Lerp(At(image, left, top, lane), At(image, right, top, lane), s);
                        var lower = float.Lerp(At(image, left, bottom, lane), At(image, right, bottom, lane), s);
                        samples[(y * width + x) * lanes + lane] = float.Lerp(upper, lower, t);
                    }
                }
            }
        );

        return new(width, height, lanes, samples);
    }

    static float At(FloatImage image, int x, int y, int lane) =>
        image.Samples[(y * image.Width + x) * image.Channels + lane];

    /// <summary>Area-weighted, one axis at a time: the weights of a box are separable, so this is exact.</summary>
    static FloatImage Box(FloatImage image, int width, int height) {
        var lanes = image.Channels;
        var across = Weights(image.Width, width);
        var down = Weights(image.Height, height);

        var rows = new float[width * image.Height * lanes];

        Parallel.For(
            0,
            image.Height,
            y => {
                for (var x = 0; x < width; x++) {
                    foreach (var (from, weight) in across[x]) {
                        for (var lane = 0; lane < lanes; lane++) {
                            rows[(y * width + x) * lanes + lane] += image.Samples[(y * image.Width + from) * lanes
                                + lane]
                                * weight;
                        }
                    }
                }
            }
        );

        var samples = new float[width * height * lanes];

        Parallel.For(
            0,
            height,
            y => {
                foreach (var (from, weight) in down[y]) {
                    for (var x = 0; x < width; x++) {
                        for (var lane = 0; lane < lanes; lane++) {
                            samples[(y * width + x) * lanes + lane] += rows[(from * width + x) * lanes + lane] * weight;
                        }
                    }
                }
            }
        );

        return new(width, height, lanes, samples);
    }

    /// <summary>For each target texel, the source texels it overlaps and by what share of its own width.</summary>
    static List<(int From, float Weight)>[] Weights(int source, int target) {
        var weights = new List<(int, float)>[target];
        var scale = (double)source / target;

        for (var at = 0; at < target; at++) {
            var start = at * scale;
            var end = (at + 1) * scale;
            List<(int, float)> list = [];

            for (var from = (int)Math.Floor(start); from < Math.Min(source, (int)Math.Ceiling(end)); from++) {
                var overlap = Math.Min(end, from + 1) - Math.Max(start, from);

                if (overlap > 0) {
                    list.Add((from, (float)(overlap / scale)));
                }
            }

            weights[at] = list;
        }

        return weights;
    }
}

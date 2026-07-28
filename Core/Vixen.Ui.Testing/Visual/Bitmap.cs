// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

namespace Vixen.Ui.Testing.Visual;

/// <summary>An 8-bit RGBA image in memory.</summary>
/// <param name="Width">Its width in pixels.</param>
/// <param name="Height">Its height in pixels.</param>
/// <param name="Pixels">Its pixels, row-major, four bytes each.</param>
public readonly record struct Bitmap(int Width, int Height, byte[] Pixels) {
    /// <summary>The byte offset of a pixel.</summary>
    /// <param name="x">Its column.</param>
    /// <param name="y">Its row.</param>
    /// <returns>Where it starts in <see cref="Pixels" />.</returns>
    public int Offset(int x, int y) => ((y * Width) + x) * 4;
}

/// <summary>How far two pictures of the same interface may be apart.</summary>
/// <param name="Channel">How far one channel may differ, in 0–255, before that pixel counts as wrong.</param>
/// <param name="Fraction">What fraction of pixels may be wrong before the picture is.</param>
/// <remarks>
///     <para>
///         ⚠ <b>The default is exact, which a GPU golden suite cannot afford and this can.</b>
///         <c>Vixen.Graphics.Golden.Tests</c> compares perceptually because MoltenVK and lavapipe
///         round the same sRGB conversion differently and both are conformant, so a bitwise suite
///         there is red from the day it is written. Nothing here is rendered by a driver — see
///         <see cref="SoftwareUiRasterizer" /> — so the same arithmetic runs on every machine and a
///         tolerance would have nothing to absorb except real differences.
///     </para>
///     <para>
///         The looser values exist anyway, for the one case that is not about drivers: a reference
///         regenerated after a deliberate change to text shaping or corner geometry, where a
///         reviewer wants to see whether anything <i>else</i> moved.
///     </para>
/// </remarks>
public readonly record struct ImageTolerance(int Channel, double Fraction) {
    /// <summary>Byte for byte.</summary>
    public static ImageTolerance Exact => new(0, 0.0);

    /// <summary>A shade either way, and no pixel allowed to be wholly wrong.</summary>
    public static ImageTolerance Slight => new(2, 0.0);

    /// <summary>For a picture whose antialiased edges may land differently.</summary>
    /// <remarks>
    ///     ⚠ Used sparingly, and never as a first response to a failure: it is the tolerance that can
    ///     hide a real difference, so a screenshot that needs it should say why.
    /// </remarks>
    public static ImageTolerance Edges => new(12, 0.002);
}

/// <summary>What comparing two pictures found.</summary>
/// <param name="Matches">Whether they are within tolerance.</param>
/// <param name="DifferingPixels">How many pixels exceeded the channel tolerance.</param>
/// <param name="TotalPixels">How many there are.</param>
/// <param name="WorstChannel">The largest single-channel difference anywhere.</param>
/// <param name="WorstX">Where that was.</param>
/// <param name="WorstY">Ditto.</param>
public readonly record struct ImageComparison(
    bool Matches,
    int DifferingPixels,
    int TotalPixels,
    int WorstChannel,
    int WorstX,
    int WorstY
) {
    /// <summary>What fraction of pixels differed.</summary>
    public double Fraction => TotalPixels == 0 ? 0 : (double)DifferingPixels / TotalPixels;

    /// <summary>How this reads in a failure message.</summary>
    public override string ToString() =>
        DifferingPixels == 0
            ? "identical"
            : $"{DifferingPixels} of {TotalPixels} pixels differ ({Fraction:P2}), worst by "
            + $"{WorstChannel}/255 at ({WorstX}, {WorstY})";
}

/// <summary>Comparing a rendering against its reference, and saying usefully what changed.</summary>
/// <remarks>
///     ⚠ <b>Counting pixels over a threshold, not a mean-squared error.</b> MSE is the obvious metric
///     and the wrong one: a value low enough to pass a whole image hides a bright artefact in a
///     corner, which is exactly the failure a picture suite exists to catch. Counting pixels that
///     exceed a per-channel threshold catches the small-and-wrong case, and reporting the worst pixel
///     with its coordinates says where to look.
/// </remarks>
public static class ImageComparer {
    /// <summary>Compares two pictures.</summary>
    /// <param name="rendered">What was drawn.</param>
    /// <param name="expected">What is committed.</param>
    /// <param name="tolerance">How far apart they may be.</param>
    /// <returns>What it found.</returns>
    /// <exception cref="ArgumentException">They are not the same size.</exception>
    public static ImageComparison Compare(in Bitmap rendered, in Bitmap expected, ImageTolerance tolerance) {
        if (rendered.Width != expected.Width || rendered.Height != expected.Height) {
            throw new ArgumentException(
                $"The pictures are different sizes: {rendered.Width}×{rendered.Height} against "
                + $"{expected.Width}×{expected.Height}. A size change is a change worth looking at "
                + "rather than something to compare around.",
                nameof(rendered)
            );
        }

        var differing = 0;
        var worst = 0;
        var worstX = 0;
        var worstY = 0;

        for (var y = 0; y < rendered.Height; y++) {
            for (var x = 0; x < rendered.Width; x++) {
                var offset = rendered.Offset(x, y);
                var difference = 0;

                for (var channel = 0; channel < 4; channel++) {
                    difference = Math.Max(
                        difference,
                        Math.Abs(rendered.Pixels[offset + channel] - expected.Pixels[offset + channel])
                    );
                }

                if (difference > worst) {
                    worst = difference;
                    worstX = x;
                    worstY = y;
                }

                if (difference > tolerance.Channel) {
                    differing++;
                }
            }
        }

        var total = rendered.Width * rendered.Height;
        var matches = total == 0 || (double)differing / total <= tolerance.Fraction;
        return new(matches, differing, total, worst, worstX, worstY);
    }

    /// <summary>The differing pixels in red, over a dimmed copy of the reference.</summary>
    /// <param name="rendered">What was drawn.</param>
    /// <param name="expected">What is committed.</param>
    /// <param name="tolerance">What counts as differing.</param>
    /// <returns>The picture to write beside the other two.</returns>
    /// <remarks>
    ///     Dimmed rather than blank, because "twelve pixels differ" is not the question — "which
    ///     twelve, and are they the corner of a button or a line of text" is, and that needs the rest
    ///     of the picture present but out of the way.
    /// </remarks>
    public static Bitmap Diff(in Bitmap rendered, in Bitmap expected, ImageTolerance tolerance) {
        var pixels = new byte[expected.Pixels.Length];

        for (var y = 0; y < expected.Height; y++) {
            for (var x = 0; x < expected.Width; x++) {
                var offset = expected.Offset(x, y);
                var difference = 0;

                for (var channel = 0; channel < 4; channel++) {
                    difference = Math.Max(
                        difference,
                        Math.Abs(rendered.Pixels[offset + channel] - expected.Pixels[offset + channel])
                    );
                }

                if (difference > tolerance.Channel) {
                    pixels[offset] = 255;
                    pixels[offset + 1] = 0;
                    pixels[offset + 2] = 0;
                } else {
                    pixels[offset] = (byte)(expected.Pixels[offset] / 4);
                    pixels[offset + 1] = (byte)(expected.Pixels[offset + 1] / 4);
                    pixels[offset + 2] = (byte)(expected.Pixels[offset + 2] / 4);
                }

                pixels[offset + 3] = 255;
            }
        }

        return new(expected.Width, expected.Height, pixels);
    }
}

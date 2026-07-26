// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Xunit;

namespace Vixen.Graphics.Golden.Tests;

/// <summary>How far two renderings of the same fixture may be apart.</summary>
/// <param name="Channel">
///     How far one channel may differ, in 0–255, before that pixel counts as wrong.
/// </param>
/// <param name="Fraction">
///     What fraction of pixels may be wrong before the image is.
/// </param>
public readonly record struct Tolerance(int Channel, double Fraction) {
    /// <summary>
    ///     What a fixture with flat colour and no interpolation should meet.
    /// </summary>
    /// <remarks>
    ///     Not zero, because two conformant drivers are allowed to differ. A clear to 0.25 lands on
    ///     63 or 64 depending on whether the driver rounds or truncates the sRGB conversion, and both
    ///     are correct.
    /// </remarks>
    public static Tolerance Flat => new(2, 0.0);

    /// <summary>What a fixture with edges should meet.</summary>
    /// <remarks>
    ///     Rasterisation rules are exact in Vulkan, so a triangle's coverage matches between drivers.
    ///     Its <em>interpolated</em> colours do not have to: the specification permits a range of
    ///     precisions for barycentric interpolation, so a gradient can differ by a few levels
    ///     everywhere. Hence a wider channel tolerance and still no allowance for whole pixels being
    ///     wrong — a pixel in the wrong place is a bug, a pixel a shade off is a driver.
    /// </remarks>
    public static Tolerance Interpolated => new(12, 0.0);

    /// <summary>What a fixture whose edges may land differently should meet.</summary>
    /// <remarks>
    ///     A small fraction of pixels allowed to be entirely wrong, for fixtures where a boundary
    ///     falls on a pixel centre and a driver's tie-breaking decides which side it lands. Used
    ///     sparingly: it is the tolerance that can hide a real difference, so a fixture that needs it
    ///     should say why.
    /// </remarks>
    public static Tolerance Edges => new(12, 0.002);
}

/// <summary>What comparing two images found.</summary>
/// <param name="Matches">Whether they are within tolerance.</param>
/// <param name="DifferingPixels">How many pixels exceeded the channel tolerance.</param>
/// <param name="TotalPixels">How many there are.</param>
/// <param name="WorstChannel">The largest single-channel difference anywhere.</param>
/// <param name="WorstAt">Where that was.</param>
public readonly record struct Comparison(
    bool Matches,
    int DifferingPixels,
    int TotalPixels,
    int WorstChannel,
    (int X, int Y) WorstAt
) {
    /// <summary>What fraction of pixels differed.</summary>
    public double Fraction => TotalPixels == 0 ? 0 : (double)DifferingPixels / TotalPixels;
}

/// <summary>Comparing a rendering against its reference, and saying usefully what changed.</summary>
/// <remarks>
///     <para>
///         <b>Perceptual with an explicit threshold, not bitwise</b>
///         ([05](../../docs/plan/05-graphics-rhi.md) § Testing). Bitwise comparison across drivers is
///         a maintenance sinkhole: MoltenVK and lavapipe round the same sRGB conversion differently
///         and both are conformant, so a bitwise suite is red on one machine from the day it is
///         written and gets disabled within a month.
///     </para>
///     <para>
///         The metric is deliberately not mean-squared error, which is the obvious choice and the
///         wrong one. An MSE low enough to pass a whole image can hide a bright artefact in a corner
///         — exactly the failure a golden-image suite exists to catch. Counting pixels that exceed a
///         per-channel threshold catches the small-and-wrong case, and reporting the worst pixel with
///         its coordinates says where to look.
///     </para>
/// </remarks>
public static class GoldenImage {
    /// <summary>Whether the run should rewrite the references rather than check them.</summary>
    /// <remarks>
    ///     Set by the Nuke <c>GoldenImages</c> target's <c>--update-golden</c> parameter. Deliberately
    ///     an environment variable and not a default: a suite that rewrites its own expectations when
    ///     they fail is a suite that always passes.
    /// </remarks>
    public static bool Updating =>
        Environment.GetEnvironmentVariable("VIXEN_UPDATE_GOLDEN") is "1" or "true" or "TRUE";

    /// <summary>Where the reference images live, next to the test binary.</summary>
    public static string ReferenceDirectory => Path.Combine(AppContext.BaseDirectory, "References");

    /// <summary>Where a failure writes what it saw.</summary>
    /// <remarks>
    ///     Under <c>artifacts/</c> so the CI workflow can upload the whole directory without knowing
    ///     what is in it — which is what makes a failure diagnosable from a build page rather than
    ///     only on the machine that produced it.
    /// </remarks>
    public static string DiffDirectory =>
        Environment.GetEnvironmentVariable("VIXEN_GOLDEN_DIFF")
        ?? Path.Combine(AppContext.BaseDirectory, "golden-diff");

    /// <summary>Checks a rendering against its reference, or records it as the new one.</summary>
    /// <param name="name">The fixture's name, which is also its file's.</param>
    /// <param name="rendered">What was rendered.</param>
    /// <param name="tolerance">How far apart they may be.</param>
    public static void Verify(string name, in Bitmap rendered, Tolerance tolerance) {
        var reference = Path.Combine(ReferenceDirectory, $"{name}.png");

        if (Updating) {
            // The source tree, not the output directory: rewriting the copy beside the binary would
            // "pass" and change nothing anybody commits.
            PngCodec.Save(Path.Combine(SourceReferenceDirectory(), $"{name}.png"), rendered);
            return;
        }

        if (!File.Exists(reference)) {
            PngCodec.Save(Path.Combine(DiffDirectory, $"{name}.rendered.png"), rendered);

            Assert.Fail(
                $"There is no reference image for '{name}'. What was rendered has been written to "
                + $"{DiffDirectory}; if it is right, add it with the GoldenImages target's "
                + "--update-golden and commit it."
            );
        }

        var expected = PngCodec.Load(reference);

        if (expected.Width != rendered.Width || expected.Height != rendered.Height) {
            Assert.Fail(
                $"'{name}' rendered at {rendered.Width}×{rendered.Height} and its reference is "
                + $"{expected.Width}×{expected.Height}. A size change is never a rounding difference, "
                + "so it is reported as a failure rather than compared."
            );
        }

        var result = Compare(expected, rendered, tolerance);

        if (result.Matches) {
            return;
        }

        PngCodec.Save(Path.Combine(DiffDirectory, $"{name}.rendered.png"), rendered);
        PngCodec.Save(Path.Combine(DiffDirectory, $"{name}.expected.png"), expected);
        PngCodec.Save(Path.Combine(DiffDirectory, $"{name}.diff.png"), Highlight(expected, rendered, tolerance));

        Assert.Fail(
            $"'{name}' does not match its reference: {result.DifferingPixels} of {result.TotalPixels} "
            + $"pixels ({result.Fraction:P3}) differ by more than {tolerance.Channel}/255, and the "
            + $"worst is {result.WorstChannel}/255 at ({result.WorstAt.X}, {result.WorstAt.Y}). "
            + $"The rendering, the reference and a diff are in {DiffDirectory}."
        );
    }

    /// <summary>Compares two images.</summary>
    /// <param name="expected">The reference.</param>
    /// <param name="actual">What was rendered.</param>
    /// <param name="tolerance">How far apart they may be.</param>
    public static Comparison Compare(in Bitmap expected, in Bitmap actual, Tolerance tolerance) {
        var differing = 0;
        var worst = 0;
        (int X, int Y) worstAt = (0, 0);

        for (var y = 0; y < expected.Height; y++) {
            for (var x = 0; x < expected.Width; x++) {
                var offset = expected.Offset(x, y);
                var delta = 0;

                for (var channel = 0; channel < 4; channel++) {
                    delta = Math.Max(delta, Math.Abs(expected.Pixels[offset + channel] - actual.Pixels[offset + channel]));
                }

                if (delta > worst) {
                    worst = delta;
                    worstAt = (x, y);
                }

                if (delta > tolerance.Channel) {
                    differing++;
                }
            }
        }

        var total = expected.Width * expected.Height;

        return new(
            differing <= tolerance.Fraction * total,
            differing,
            total,
            worst,
            worstAt
        );
    }

    /// <summary>Paints the differing pixels red over a dimmed reference.</summary>
    /// <remarks>
    ///     A side-by-side would be prettier and is worse: what a human needs from a failed golden
    ///     image is <em>where</em>, and a heat map over the original answers that in one glance.
    /// </remarks>
    static Bitmap Highlight(in Bitmap expected, in Bitmap actual, Tolerance tolerance) {
        var pixels = new byte[expected.Pixels.Length];

        for (var y = 0; y < expected.Height; y++) {
            for (var x = 0; x < expected.Width; x++) {
                var offset = expected.Offset(x, y);
                var delta = 0;

                for (var channel = 0; channel < 4; channel++) {
                    delta = Math.Max(delta, Math.Abs(expected.Pixels[offset + channel] - actual.Pixels[offset + channel]));
                }

                if (delta > tolerance.Channel) {
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

    /// <summary>The <c>References</c> directory in the source tree, walking up from the binary.</summary>
    static string SourceReferenceDirectory() {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null) {
            var candidate = Path.Combine(directory.FullName, "Vixen.Graphics.Golden.Tests.csproj");

            if (File.Exists(candidate)) {
                return Path.Combine(directory.FullName, "References");
            }

            directory = directory.Parent;
        }

        throw new InvalidOperationException(
            "The golden-image project could not be found from the test binary, so --update-golden has "
            + "nowhere to write. Run it from a source checkout."
        );
    }
}

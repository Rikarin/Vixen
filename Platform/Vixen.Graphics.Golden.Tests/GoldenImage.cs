// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Imaging;
using Vixen.Ui.Testing.Visual;
using Xunit;

namespace Vixen.Graphics.Golden.Tests;

/// <summary>How far two renderings of the same fixture may be apart.</summary>
/// <param name="Channel">
///     How far one channel may differ, in 0–255, before that pixel counts as wrong.
/// </param>
/// <param name="Fraction">
///     What fraction of pixels may be wrong before the image is.
/// </param>
/// <param name="Mean">
///     How far the <em>average</em> channel may move, in 0–255.
/// </param>
/// <remarks>
///     <para>
///         ⚠ <b><see cref="Mean" /> is not a second opinion about <see cref="Channel" />; it is the
///         failure <see cref="Channel" /> cannot see.</b> Counting pixels over a threshold finds
///         something small and badly wrong and is blind to everything being slightly wrong: a
///         material whose albedo moved four per cent shifts sixty to ninety per cent of a frame by
///         one or two levels and almost nothing by more than three, so every per-pixel threshold at
///         or above two passes it. That is not a hypothetical — it is what a deliberately injected
///         4% albedo change did to the tier goldens, which passed.
///     </para>
///     <para>
///         The two bounds are complementary and the suite needs both. A mean alone is the
///         mean-squared-error mistake this file's remarks describe: low enough to pass a whole image
///         while a corner is blown out. A count alone is the one above. Whichever is crossed first
///         fails, and the message says which.
///     </para>
///     <para>
///         <see cref="double.MaxValue" /> by default so every fixture written before this existed
///         keeps exactly the bound it was written with — a tolerance is a claim somebody made about
///         a specific picture, and tightening forty of them at once from here would be replacing
///         forty claims with a guess.
///     </para>
/// </remarks>
public readonly record struct Tolerance(int Channel, double Fraction, double Mean = double.MaxValue) {
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

    /// <summary>What a whole shaded, tonemapped, antialiased frame should meet.</summary>
    /// <remarks>
    ///     <para>
    ///         <see cref="Edges" />'s two bounds, because a frame this size has an antialiased
    ///         silhouette in it and FXAA's blend sits on a luminance comparison two drivers may
    ///         resolve either way — plus the mean bound, which is the one that catches a shading
    ///         change rather than a geometric one.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>A third of a level, and the number is measured rather than picked.</b> Rendering
    ///         the tier fixture twice on MoltenVK moves the mean by <em>exactly</em> zero — the frames
    ///         are bit-identical — and a 4% albedo change moves it by 1.256 on Low, 1.164 on Medium
    ///         and 0.44 on High and Epic, where local exposure and the defocus damp it. So the gap
    ///         this number sits in is 0 to 0.44, and it is put nearer the noise than the signal.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The cross-driver half of that measurement does not exist yet.</b> The zero above
    ///         is one driver's; lavapipe may well move a fully shaded frame's mean by a fraction of a
    ///         level everywhere, for the interpolation-precision reason
    ///         <see cref="Interpolated" /> gives. If the first cross-driver run needs this raised,
    ///         raise it — but past about 0.45 it stops catching a 4% albedo change on High and Epic,
    ///         and that is a trade to make on purpose rather than by nudging a number until CI is
    ///         green.
    ///     </para>
    /// </remarks>
    public static Tolerance Shaded => new(12, 0.002, 0.35);
}

/// <summary>What comparing two images found.</summary>
/// <param name="Matches">Whether they are within tolerance.</param>
/// <param name="DifferingPixels">How many pixels exceeded the channel tolerance.</param>
/// <param name="TotalPixels">How many there are.</param>
/// <param name="WorstChannel">The largest single-channel difference anywhere.</param>
/// <param name="WorstAt">Where that was.</param>
/// <param name="MeanChannel">The average channel difference over every channel of every pixel.</param>
public readonly record struct Comparison(
    bool Matches,
    int DifferingPixels,
    int TotalPixels,
    int WorstChannel,
    (int X, int Y) WorstAt,
    double MeanChannel = 0
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

        // Which bound was crossed, first, because the two mean different things: a count over the
        // threshold is something in one place being badly wrong, and a mean over it is the whole
        // frame being slightly wrong. "Images differ" sends a reader looking for the wrong shape of
        // bug, and a shading change with nothing over the per-pixel threshold has no "where" to look
        // at at all.
        var crossed = result.MeanChannel > tolerance.Mean
            ? $"the average channel moved by {result.MeanChannel:F3}/255, where {tolerance.Mean:F3} is "
                + "the most it may — a whole-frame shading change rather than an artefact in one place"
            : $"{result.DifferingPixels} of {result.TotalPixels} pixels ({result.Fraction:P3}) differ by "
                + $"more than {tolerance.Channel}/255, where {tolerance.Fraction:P3} is the most that may";

        Assert.Fail(
            $"'{name}' does not match its reference: {crossed}. The worst single channel is "
            + $"{result.WorstChannel}/255 at ({result.WorstAt.X}, {result.WorstAt.Y}), and the average "
            + $"is {result.MeanChannel:F3}/255. The rendering, the reference and a diff are in "
            + $"{DiffDirectory}."
        );
    }

    /// <summary>Compares two images.</summary>
    /// <param name="expected">The reference.</param>
    /// <param name="actual">What was rendered.</param>
    /// <param name="tolerance">How far apart they may be.</param>
    public static Comparison Compare(in Bitmap expected, in Bitmap actual, Tolerance tolerance) {
        var differing = 0;
        var worst = 0;
        var sum = 0L;
        (int X, int Y) worstAt = (0, 0);

        for (var y = 0; y < expected.Height; y++) {
            for (var x = 0; x < expected.Width; x++) {
                var offset = expected.Offset(x, y);
                var delta = 0;

                for (var channel = 0; channel < 4; channel++) {
                    var difference = Math.Abs(expected.Pixels[offset + channel] - actual.Pixels[offset + channel]);

                    // ⚠ Summed over every channel rather than over the worst one per pixel, because
                    // the failure the mean exists for is a shading change — and a shading change
                    // moves all three colour channels. Taking the maximum first would throw away
                    // two thirds of the evidence for the one thing this bound is here to see.
                    sum += difference;
                    delta = Math.Max(delta, difference);
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
        var mean = total == 0 ? 0 : (double)sum / (total * 4);

        return new(
            differing <= tolerance.Fraction * total && mean <= tolerance.Mean,
            differing,
            total,
            worst,
            worstAt,
            mean
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

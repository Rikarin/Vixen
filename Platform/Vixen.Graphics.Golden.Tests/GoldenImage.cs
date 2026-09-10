// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
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
    /// <summary>Guards the report, which every collection in the assembly appends to.</summary>
    static readonly Lock Reporting = new();

    /// <summary>Whether this process has started its report yet.</summary>
    static bool reported;

    /// <summary>Whether the run should rewrite the references rather than check them.</summary>
    /// <remarks>
    ///     Set by the Nuke <c>GoldenImages</c> target's <c>--update-golden</c> parameter. Deliberately
    ///     an environment variable and not a default: a suite that rewrites its own expectations when
    ///     they fail is a suite that always passes.
    /// </remarks>
    public static bool Updating => UpdatingFrom(Environment.GetEnvironmentVariable("VIXEN_UPDATE_GOLDEN"));

    /// <summary>Whether an update run should re-record even the references that already match.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>The reason this distinction exists is #1242.</b> An update run used to rewrite
    ///         <em>every</em> reference it rendered, including the ones whose test was passing — and
    ///         because this suite's comparison is tolerant rather than bitwise, a reference that
    ///         passes at a third of its allowance is not the same picture as the one that would
    ///         replace it. So a drift somebody else's change put under the bound was re-accepted by
    ///         whoever next typed <c>--update-golden</c> for an unrelated reason, the budget was
    ///         silently reset, and nothing in the run said so. That is exactly how
    ///         <c>tier-low</c>'s 0.124/255 survived two people noticing it.
    ///     </para>
    ///     <para>
    ///         <b>It is a real workflow and not a mistake</b>, which is why it is a value rather than
    ///         a refusal: <c>c93474579</c> deliberately re-recorded two <em>passing</em> tier
    ///         references because a dither moved every pixel of them and "a reference that merely
    ///         passes is not what the frame looks like". That run now has to say it meant it.
    ///     </para>
    ///     <para>
    ///         ⚠ <b><c>GoldenFile</c>, the text half, does not have this defect and it is worth
    ///         saying why</b> — not diligence, but that its comparison is exact. Rewriting a snapshot
    ///         that already matches writes identical bytes and <c>git status</c> stays empty. A
    ///         tolerant comparator is what turns the same code into a drift absorber.
    ///     </para>
    /// </remarks>
    public static bool Forcing => ForcingFrom(Environment.GetEnvironmentVariable("VIXEN_UPDATE_GOLDEN"));

    /// <summary>Whether a value of the switch asks for an update run at all.</summary>
    /// <param name="value">What the environment held, or null when it held nothing.</param>
    /// <returns>Whether the references are being rewritten rather than checked.</returns>
    /// <remarks>
    ///     Pure, so the two spellings can be asserted without a test writing a process-wide
    ///     environment variable that every other collection in this assembly reads live — which is
    ///     the shape of race that would make one golden run rewrite the tree under another.
    /// </remarks>
    internal static bool UpdatingFrom(string? value) =>
        value is "1" or "true" or "TRUE" || ForcingFrom(value);

    /// <summary>Whether a value of the switch asks for the matching references too.</summary>
    /// <param name="value">What the environment held, or null when it held nothing.</param>
    /// <returns>Whether a reference whose test passes is re-recorded.</returns>
    internal static bool ForcingFrom(string? value) => value is "force" or "FORCE" or "all" or "ALL";

    /// <summary>Where the reference images live, next to the test binary.</summary>
    public static string ReferenceDirectory => Path.Combine(AppContext.BaseDirectory, "References");

    /// <summary>Where a failure writes what it saw.</summary>
    /// <remarks>
    ///     <para>
    ///         Beside the test binary, unless somebody says otherwise — which is what makes a
    ///         failure diagnosable from a build page rather than only on the machine that produced
    ///         it, because <c>ci.yml</c> uploads that path by glob.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>This used to say "under <c>artifacts/</c> so the CI workflow can upload the
    ///         whole directory", and both halves were wrong.</b> The <c>artifacts/</c> path is not
    ///         the fallback but the override, and <c>./build.sh GoldenImages</c> is the only thing
    ///         in the repository that sets <c>VIXEN_GOLDEN_DIFF</c> — while <i>no</i> CI job runs
    ///         that target, so on CI this property is always the fallback and
    ///         <c>artifacts/golden-diff/</c> is always empty. The workflow names both paths and
    ///         ignores the missing one, which is why nobody noticed.
    ///     </para>
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
            Record(name, rendered, tolerance, reference);
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
            // ⚠ What a passing golden spends is the fact this suite printed nowhere, and it is the
            // fact that would have caught #1242 the day it landed rather than a month later: a
            // reference passing at a third of its mean allowance has already been moved by something
            // nobody attributed, and the next legitimate change fails and is blamed on itself.
            Note($"kept\t{name}\t{Headroom(result, tolerance)}");
            return;
        }

        PngCodec.Save(Path.Combine(DiffDirectory, $"{name}.rendered.png"), rendered);
        PngCodec.Save(Path.Combine(DiffDirectory, $"{name}.expected.png"), expected);
        PngCodec.Save(Path.Combine(DiffDirectory, $"{name}.diff.png"), Highlight(expected, rendered, tolerance));

        // ⚠ The failures are in the report too, and this line is the report's own instrument check:
        // a headroom table listing only the fixtures that passed is a table on which silence means
        // "fine", which is the shape of lie this whole file exists to stop. `Assert.Fail` throws, so
        // it has to be written before rather than after.
        Note($"failed\t{name}\t{Headroom(result, tolerance)}");

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

    /// <summary>What an update run decided about one reference.</summary>
    /// <param name="Record">Whether the rendering replaces the committed reference.</param>
    /// <param name="Reason">Why, in the terms a commit message would have to use.</param>
    internal readonly record struct UpdateDecision(bool Record, string Reason);

    /// <summary>Whether an update run should replace one reference, and why.</summary>
    /// <param name="exists">Whether a reference is committed for this fixture at all.</param>
    /// <param name="sizeAgrees">Whether it has the same dimensions as the rendering.</param>
    /// <param name="comparison">What <see cref="Compare" /> found, when there was something to compare.</param>
    /// <param name="tolerance">The bounds the fixture claims.</param>
    /// <param name="forced">Whether the operator asked for every reference to be re-recorded.</param>
    /// <returns>The decision and the sentence that justifies it.</returns>
    /// <remarks>
    ///     <para>
    ///         <b>Pure, and separate from <see cref="Verify" /> for one reason:</b> the defect this
    ///         exists to prevent is a decision rather than an I/O mistake, and a decision that only
    ///         exists inside a method that needs a GPU, a PNG on disk and a source checkout is a
    ///         decision nothing can test. <c>GoldenUpdateTests</c> drives every arm of it with no
    ///         device at all.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The <paramref name="comparison" /> is not consulted when there is nothing to
    ///         compare against</b>, and that ordering is load-bearing: a <c>default</c>
    ///         <see cref="Comparison" /> has <c>Matches</c> false, so an <c>exists</c> check placed
    ///         after the match check would record a missing reference for the wrong reason and say so
    ///         in the manifest.
    ///     </para>
    /// </remarks>
    internal static UpdateDecision Decide(
        bool exists,
        bool sizeAgrees,
        Comparison comparison,
        Tolerance tolerance,
        bool forced
    ) {
        if (!exists) {
            return new(true, "there was no reference committed for it");
        }

        if (!sizeAgrees) {
            return new(true, "the reference is a different size, which is never a rounding difference");
        }

        if (!comparison.Matches) {
            return new(true, $"it no longer matches: {Headroom(comparison, tolerance)}");
        }

        return forced
            ? new(
                true,
                "VIXEN_UPDATE_GOLDEN asked for every reference, and this one already matched: "
                + Headroom(comparison, tolerance)
            )
            : new(
                false,
                $"it already matches, so re-accepting it would move the reference for no stated reason: "
                + Headroom(comparison, tolerance)
            );
    }

    /// <summary>What a comparison spent of what it was allowed.</summary>
    /// <param name="result">What the comparison found.</param>
    /// <param name="tolerance">What the fixture allows.</param>
    /// <returns>One line naming both bounds, what was spent of each, and the worst pixel.</returns>
    /// <remarks>
    ///     Both bounds, because <see cref="Tolerance" />'s own remarks are that they see different
    ///     failures — and the mean bound is <see cref="double.MaxValue" /> on most fixtures here, so
    ///     the line says "unbounded" rather than printing a percentage of infinity that reads as
    ///     healthy.
    /// </remarks>
    internal static string Headroom(Comparison result, Tolerance tolerance) {
        // ⚠ Every number formatted invariantly and then interpolated, rather than interpolated and
        // formatted by whatever culture the host booted with. This line is asserted on by
        // `GoldenUpdateTests` and written into a TSV, and `F3` under a comma-decimal culture writes
        // "0,350" — a test that fails on a French machine and a column a spreadsheet splits in two.
        var bounded = !double.IsPositiveInfinity(tolerance.Mean) && tolerance.Mean < double.MaxValue;
        var spent = result.MeanChannel.ToString("F3", CultureInfo.InvariantCulture);

        var mean = bounded
            ? $"mean {spent}/255 of {tolerance.Mean.ToString("F3", CultureInfo.InvariantCulture)} "
                + $"({(result.MeanChannel / tolerance.Mean).ToString("P0", CultureInfo.InvariantCulture)} "
                + "of the allowance)"
            : $"mean {spent}/255 against no mean bound";

        var counted = $"{result.DifferingPixels} of {result.TotalPixels} pixels over "
            + $"{tolerance.Channel}/255";

        var pixels = tolerance.Fraction <= 0
            ? $"{counted}, where none may"
            : $"{counted}, where {tolerance.Fraction.ToString("P3", CultureInfo.InvariantCulture)} may "
                + $"({(result.Fraction / tolerance.Fraction).ToString("P0", CultureInfo.InvariantCulture)} "
                + "of the allowance)";

        return $"{mean}; {pixels}; worst {result.WorstChannel}/255 at "
            + $"({result.WorstAt.X}, {result.WorstAt.Y})";
    }

    /// <summary>Records a rendering as the new reference, or says why it was left alone.</summary>
    /// <param name="name">The fixture's name.</param>
    /// <param name="rendered">What was rendered.</param>
    /// <param name="tolerance">How far apart they may be.</param>
    /// <param name="reference">The reference beside the binary, which is the one to compare against.</param>
    static void Record(string name, in Bitmap rendered, Tolerance tolerance, string reference) {
        var exists = File.Exists(reference);
        var comparison = default(Comparison);
        var sizeAgrees = false;

        if (exists) {
            var expected = PngCodec.Load(reference);
            sizeAgrees = expected.Width == rendered.Width && expected.Height == rendered.Height;

            if (sizeAgrees) {
                comparison = Compare(expected, rendered, tolerance);
            }
        }

        var decision = Decide(exists, sizeAgrees, comparison, tolerance, Forcing);

        if (decision.Record) {
            // The source tree, not the output directory: rewriting the copy beside the binary would
            // "pass" and change nothing anybody commits.
            PngCodec.Save(Path.Combine(SourceReferenceDirectory(), $"{name}.png"), rendered);
        }

        Note($"{(decision.Record ? "recorded" : "kept")}\t{name}\t{decision.Reason}");
    }

    /// <summary>Where this run's per-fixture report is written.</summary>
    /// <remarks>
    ///     Named for what the run was doing, because the two answer different questions: an update
    ///     run's reader wants to know which files changed under it, and a checking run's wants to
    ///     know which references are close to their bound before one of them fails.
    /// </remarks>
    public static string ReportPath =>
        Path.Combine(DiffDirectory, Updating ? "golden-update.tsv" : "golden-headroom.tsv");

    /// <summary>Writes one line of the report, to the running test and to the file.</summary>
    /// <param name="line">Verb, fixture, and the sentence that explains it.</param>
    /// <remarks>
    ///     <para>
    ///         Both channels on purpose. The test's own output is what a TRX carries, so a CI failure
    ///         page can be read without the machine; the file is what a developer at a terminal can
    ///         open, because <c>dotnet test</c> prints a passing test's output nowhere.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>What this prints on the day it does not run is nothing at all</b>, and that is
    ///         deliberate rather than overlooked: it is a report and not a gate, so an absent file is
    ///         "no fixtures ran" and cannot be mistaken for "every fixture was healthy". The run's
    ///         own <c>Total</c> is what says whether it ran. Truncated once per process so a stale
    ///         line from yesterday's run cannot be read as today's.
    ///     </para>
    /// </remarks>
    static void Note(string line) {
        TestContext.Current.TestOutputHelper?.WriteLine($"golden {line.Replace('\t', ' ')}");

        try {
            lock (Reporting) {
                Directory.CreateDirectory(DiffDirectory);

                if (!reported) {
                    File.WriteAllText(ReportPath, "verb\tfixture\twhy\n");
                    reported = true;
                }

                File.AppendAllText(ReportPath, line + "\n");
            }
        } catch (IOException) {
            // A report nobody can write is not a reason to fail a picture: the assertion above is the
            // test, and this is the paperwork.
        }
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

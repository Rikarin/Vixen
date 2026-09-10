// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Imaging;
using Xunit;

namespace Vixen.Graphics.Golden.Tests;

/// <summary>What <c>--update-golden</c> does to a reference whose test was passing.</summary>
/// <remarks>
///     <para>
///         ⚠ <b>It used to re-record it, and that is the mechanism behind #1242.</b> This suite's
///         comparison is tolerant rather than bitwise, so "the test passes" and "the picture is the
///         committed one" are different claims — a reference can sit a third of its mean allowance
///         away from what the tree renders and still be green. An update run that rewrote every
///         fixture it rendered therefore re-accepted that gap, reset the budget, and reported nine
///         passing tests. The only thing that caught it was a human noticing <c>git status</c> listed
///         a fourth file when three tests had failed.
///     </para>
///     <para>
///         <b>No device, and that is the point.</b> The decision the defect lives in is arithmetic
///         over a comparison, so it is asserted as arithmetic — a fixture that had to open a GPU to
///         check this would be a test that skips on every machine without one, which is where the
///         eighteen silently-passing golden files came from.
///     </para>
///     <para>
///         ⚠ The tolerant half is asserted here too (<see cref="AReferenceWithinToleranceIsKept" />
///         renders a real difference rather than an identical image), because a test that fed
///         <see cref="GoldenImage.Decide" /> two identical bitmaps would pass just as happily against
///         a comparator that only ever answered "identical" — and this repository has shipped
///         precisely that comparator once.
///     </para>
/// </remarks>
public sealed class GoldenUpdateTests {
    /// <summary>How large the pictures in this file are.</summary>
    const int Side = 16;

    /// <summary>A reference that already matches is left alone, and the run says why.</summary>
    /// <remarks>
    ///     The arm #1242 is about. The two images differ — by a level on a quarter of the pixels,
    ///     which is what a renderer change under the bound looks like — and the comparison calls them
    ///     a match, so the file must not move.
    /// </remarks>
    [Fact]
    public void AReferenceWithinToleranceIsKept() {
        var committed = Flat(100);
        var rendered = Drifted(100, 1, every: 4);
        var comparison = GoldenImage.Compare(committed, rendered, Tolerance.Shaded);

        // The instrument: this is only a statement about `Decide` if the two pictures are genuinely
        // different and genuinely within tolerance. Both halves, before the decision is asked for.
        Assert.NotEqual(committed.Pixels, rendered.Pixels);
        Assert.True(comparison.Matches, "the drift was supposed to be inside Tolerance.Shaded");
        Assert.True(comparison.MeanChannel > 0, "the drift was supposed to move the mean at all");

        var decision = GoldenImage.Decide(
            exists: true,
            sizeAgrees: true,
            comparison,
            Tolerance.Shaded,
            forced: false
        );

        Assert.False(
            decision.Record,
            "an update run rewrote a reference whose test was passing, which is how a drift nobody "
            + "attributed gets absorbed and the tolerance budget silently reset (#1242)."
        );

        Assert.Contains("already matches", decision.Reason, StringComparison.Ordinal);
    }

    /// <summary>And the same reference is re-recorded when the operator asks for all of them.</summary>
    /// <remarks>
    ///     ⚠ Not a loophole but the workflow <c>c93474579</c> used deliberately: a dither moved every
    ///     pixel of two tier references without failing either, and "a reference that merely passes is
    ///     not what the frame looks like". What changed is that the run now has to say it meant it.
    /// </remarks>
    [Fact]
    public void AMatchingReferenceIsRecordedWhenForced() {
        var comparison = GoldenImage.Compare(Flat(100), Drifted(100, 1, every: 4), Tolerance.Shaded);

        var decision = GoldenImage.Decide(
            exists: true,
            sizeAgrees: true,
            comparison,
            Tolerance.Shaded,
            forced: true
        );

        Assert.True(decision.Record);
        Assert.Contains("VIXEN_UPDATE_GOLDEN", decision.Reason, StringComparison.Ordinal);
    }

    /// <summary>A reference that no longer matches is re-recorded, with the numbers.</summary>
    [Fact]
    public void AReferenceOutsideToleranceIsRecorded() {
        var committed = Flat(100);
        var rendered = Flat(160);
        var comparison = GoldenImage.Compare(committed, rendered, Tolerance.Shaded);

        Assert.False(comparison.Matches, "60 levels everywhere was supposed to fail Tolerance.Shaded");

        var decision = GoldenImage.Decide(
            exists: true,
            sizeAgrees: true,
            comparison,
            Tolerance.Shaded,
            forced: false
        );

        Assert.True(decision.Record);
        Assert.Contains("no longer matches", decision.Reason, StringComparison.Ordinal);

        // The reason is what a commit message has to quote, so it carries the measurement rather
        // than the verdict.
        Assert.Contains("worst", decision.Reason, StringComparison.Ordinal);
    }

    /// <summary>A fixture with no committed reference gets one, and for that reason.</summary>
    /// <remarks>
    ///     ⚠ The ordering test as much as the behaviour one. A <c>default</c>
    ///     <see cref="Comparison" /> has <c>Matches</c> false, so a <c>Decide</c> that asked about the
    ///     match before asking whether a reference existed would still record the file — and would
    ///     write "it no longer matches" about a picture there was nothing to match against.
    /// </remarks>
    [Fact]
    public void AMissingReferenceIsRecorded() {
        var decision = GoldenImage.Decide(
            exists: false,
            sizeAgrees: false,
            default,
            Tolerance.Shaded,
            forced: false
        );

        Assert.True(decision.Record);
        Assert.Contains("no reference", decision.Reason, StringComparison.Ordinal);
    }

    /// <summary>A reference of another size is replaced rather than compared.</summary>
    [Fact]
    public void AResizedReferenceIsRecorded() {
        var decision = GoldenImage.Decide(
            exists: true,
            sizeAgrees: false,
            default,
            Tolerance.Shaded,
            forced: false
        );

        Assert.True(decision.Record);
        Assert.Contains("different size", decision.Reason, StringComparison.Ordinal);
    }

    /// <summary>What the switch's values mean, and that forcing implies updating.</summary>
    /// <remarks>
    ///     Read off a string rather than off the environment: <see cref="GoldenImage.Updating" /> is
    ///     read live by every fixture in the assembly, so a test that set the variable would be a test
    ///     that rewrites the repository under whatever else is rendering at that moment.
    /// </remarks>
    [Theory]
    [InlineData(null, false, false)]
    [InlineData("", false, false)]
    [InlineData("0", false, false)]
    [InlineData("1", true, false)]
    [InlineData("true", true, false)]
    [InlineData("TRUE", true, false)]
    [InlineData("force", true, true)]
    [InlineData("FORCE", true, true)]
    [InlineData("all", true, true)]
    [InlineData("ALL", true, true)]
    public void TheSwitchIsReadAsWritten(string? value, bool updating, bool forcing) {
        Assert.Equal(updating, GoldenImage.UpdatingFrom(value));
        Assert.Equal(forcing, GoldenImage.ForcingFrom(value));

        Assert.True(
            !GoldenImage.ForcingFrom(value) || GoldenImage.UpdatingFrom(value),
            $"'{value}' asks for every reference to be re-recorded and is not an update run at all, "
            + "so it would check the pictures and rewrite nothing."
        );
    }

    /// <summary>A passing fixture's headroom is stated as a fraction of what it was allowed.</summary>
    /// <remarks>
    ///     #1242's first question — a reference that passes at 0.124 of 0.350 is a fact the suite
    ///     printed nowhere, and it is the fact that would have caught the drift the day it landed.
    /// </remarks>
    [Fact]
    public void HeadroomNamesBothBounds() {
        var comparison = GoldenImage.Compare(Flat(100), Drifted(100, 1, every: 4), Tolerance.Shaded);
        var line = GoldenImage.Headroom(comparison, Tolerance.Shaded);

        Assert.Contains("of 0.350", line, StringComparison.Ordinal);
        Assert.Contains("of the allowance", line, StringComparison.Ordinal);
        Assert.Contains("pixels over 12/255", line, StringComparison.Ordinal);
    }

    /// <summary>And a fixture with no mean bound says so rather than printing a healthy zero.</summary>
    /// <remarks>
    ///     ⚠ <see cref="Tolerance" />'s default mean is <see cref="double.MaxValue" />, so most
    ///     fixtures here have no mean bound at all. A percentage of that is 0% — which reads exactly
    ///     like a fixture with all of its budget left, and would be the report lying in the direction
    ///     this whole file exists to stop.
    /// </remarks>
    [Fact]
    public void HeadroomRefusesToDivideByAnUnboundedMean() {
        var line = GoldenImage.Headroom(
            GoldenImage.Compare(Flat(100), Drifted(100, 1, every: 4), Tolerance.Edges),
            Tolerance.Edges
        );

        Assert.Contains("no mean bound", line, StringComparison.Ordinal);
        Assert.DoesNotContain("of the allowance)", line[..line.IndexOf(';', StringComparison.Ordinal)],
            StringComparison.Ordinal);
    }

    /// <summary>A flat opaque picture.</summary>
    /// <param name="level">The level every colour channel takes.</param>
    /// <returns>A <see cref="Side" />-square bitmap.</returns>
    static Bitmap Flat(byte level) {
        var pixels = new byte[Side * Side * 4];

        for (var i = 0; i < pixels.Length; i += 4) {
            pixels[i] = level;
            pixels[i + 1] = level;
            pixels[i + 2] = level;
            pixels[i + 3] = 255;
        }

        return new(Side, Side, pixels);
    }

    /// <summary>The same picture with a few pixels a level brighter.</summary>
    /// <param name="level">The level every other pixel takes.</param>
    /// <param name="by">How far the drifted ones move.</param>
    /// <param name="every">One pixel in this many drifts.</param>
    /// <returns>A picture that differs from <see cref="Flat" /> and passes beside it.</returns>
    static Bitmap Drifted(byte level, byte by, int every) {
        var bitmap = Flat(level);

        for (var pixel = 0; pixel < Side * Side; pixel += every) {
            var offset = pixel * 4;

            bitmap.Pixels[offset] = (byte)(level + by);
            bitmap.Pixels[offset + 1] = (byte)(level + by);
            bitmap.Pixels[offset + 2] = (byte)(level + by);
        }

        return bitmap;
    }
}

// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using Xunit;

namespace Vixen.Fuzz.Tests;

/// <summary>What counts as the same failure twice, and what does not.</summary>
/// <remarks>
///     <para>
///         <b>The property under test is not tidiness.</b> <see cref="FuzzSession.MaxFindings" /> ends
///         a run, so a key that splits one defect across the finding list spends the whole budget on
///         repeats of one thing and stops — which is what <c>raven</c> did every night for weeks. Its
///         round-trip oracle names the byte offset of the first difference and the character either
///         side of it, so one dropped attribute list arrived as thirty-two findings, the cap filled
///         after five and a half minutes, and the remaining hundred and fifteen minutes of its
///         nightly budget were never spent.
///     </para>
///     <para>
///         ⚠ <b>Both directions are asserted, and the second is the one that keeps the first
///         honest.</b> A key that collapsed everything would pass the first test and make the harness
///         useless, so a throwing site is a finding of its own and stays one.
///     </para>
/// </remarks>
public sealed class FindingDedupTests {
    /// <summary>Two reports of one defect, differing only in the numbers they quote, are one finding.</summary>
    [Fact]
    public void FailuresThatDifferOnlyInAnEmbeddedNumberCollapse() {
        var outcome = Run(
            input => throw new InvalidOperationException(
                string.Create(CultureInfo.InvariantCulture, $"the parse stopped at {input[0] * 37} of 4,096 bytes")
            )
        );

        var finding = Assert.Single(outcome.Findings);

        Assert.Equal(FuzzFailure.Threw, finding.Failure);
        Assert.True(outcome.Suppressed > 0, "no repeat was suppressed, so the inputs never differed");
    }

    /// <summary>Nor does the character an oracle quotes out of the input make a second defect.</summary>
    /// <remarks>
    ///     The half a plain digit-stripping key would have missed. Raven's oracle reports the
    ///     character it expected and the one it printed, so the thirty-two findings were still eight
    ///     after the offsets came out — one bug, eight ways of writing it down.
    /// </remarks>
    [Fact]
    public void FailuresThatDifferOnlyInAQuotedValueCollapse() {
        var outcome = Run(
            input => throw new InvalidOperationException($"expected '{(char)('a' + (input[0] % 26))}' and printed ' '")
        );

        Assert.Single(outcome.Findings);
        Assert.True(outcome.Suppressed > 0, "no repeat was suppressed, so the inputs never differed");
    }

    /// <summary>Two different failures stay two findings.</summary>
    [Fact]
    public void GenuinelyDifferentFailuresDoNotCollapse() {
        var outcome = Run(
            input => throw (input[0] % 2 == 0
                ? new InvalidOperationException("the tree does not reproduce its source")
                : new InvalidOperationException("an incremental reparse built a different tree"))
        );

        Assert.Equal(2, outcome.Findings.Count);
    }

    /// <summary>So does one exception type against another, from the same line.</summary>
    [Fact]
    public void DifferentExceptionTypesDoNotCollapse() {
        var outcome = Run(
            input => throw (input[0] % 2 == 0
                ? new InvalidOperationException("it went wrong")
                : new ArgumentException("it went wrong"))
        );

        Assert.Equal(2, outcome.Findings.Count);
    }

    /// <summary>The first example's bytes are the ones kept; a reproducer is the point of a finding.</summary>
    [Fact]
    public void TheFirstExamplesBytesAreKept() {
        byte[]? first = null;

        var outcome = Run(
            input => {
                first ??= input.ToArray();

                throw new InvalidOperationException(
                    string.Create(CultureInfo.InvariantCulture, $"stopped at {input[0]}")
                );
            }
        );

        var finding = Assert.Single(outcome.Findings);

        Assert.Equal(first, finding.Input);
    }

    /// <summary>A run that filled the cap says so, and one that did not says what ended it instead.</summary>
    /// <remarks>
    ///     ⚠ <b>The line nobody could read before.</b> <c>raven 109,012 cases … 3 FINDING(S)</c> is
    ///     what a run that stopped at four per cent of its budget printed, and it is the same line a
    ///     run that used all of it would have printed.
    /// </remarks>
    [Fact]
    public void TheSummarySaysWhyTheRunEnded() {
        // Distinct in prose rather than in a number, because a number is exactly what the key blanks.
        var capped = Run(input => throw new InvalidOperationException(Defects[input[0] % Defects.Length]), distinct: true);

        Assert.Equal(FuzzSession.MaxFindings, capped.Findings.Count);
        Assert.Equal("the finding cap", capped.Stopped);
        Assert.Contains("stopped on the finding cap", capped.ToString(), StringComparison.Ordinal);

        var quiet = Run(_ => 1);

        Assert.True(quiet.Clean, quiet.ToString());
        Assert.Equal("the case bound", quiet.Stopped);
        Assert.Contains("stopped on the case bound", quiet.ToString(), StringComparison.Ordinal);
    }

    /// <summary>Messages no normalisation can fold together, to fill the cap with real findings.</summary>
    static readonly string[] Defects =
        [.. Enumerable.Range(0, 48).Select(index => new string((char)('a' + (index % 26)), (index / 26) + 1) + " broke")];

    /// <summary>Runs a misbehaving target over enough cases for the dedup to have something to do.</summary>
    /// <param name="body">What the target does with an input.</param>
    /// <param name="distinct">
    ///     Whether the body's messages are meant to differ in prose, in which case the run has to be
    ///     long enough to reach the cap rather than short enough to finish.
    /// </param>
    static FuzzOutcome Run(Func<byte[], long> body, bool distinct = false) =>
        new FuzzSession(new ScriptedTarget(body), 1) { FindingDirectory = null }.Run(distinct ? 20_000 : 2_000);

    /// <summary>A target that does whatever the test told it to.</summary>
    sealed class ScriptedTarget(Func<byte[], long> body) : IFuzzTarget {
        /// <inheritdoc />
        public string Name => "scripted";

        /// <inheritdoc />
        public string What => "whatever the test asked for";

        /// <inheritdoc />
        public void Seed(ICollection<byte[]> corpus) => corpus?.Add([1, 2, 3, 4, 5, 6, 7, 8]);

        /// <inheritdoc />
        public long Run(ReadOnlySpan<byte> input) => input.Length == 0 ? 0 : body(input.ToArray());
    }
}

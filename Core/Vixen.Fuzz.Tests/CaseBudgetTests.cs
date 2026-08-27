// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Fuzz;
using Xunit;

namespace Vixen.Fuzz.Tests;

/// <summary>The one oracle whose reading the machine can write, and how it is made to mean something.</summary>
/// <remarks>
///     <para>
///         <b>Three of the four post-hoc oracles measure the decode. The fourth measures the
///         host.</b> A throw, an allocation figure and a retained count are properties of the input;
///         elapsed wall time is a property of the input <i>plus</i> everything else the machine was
///         doing, and on a shared CI runner the second term is the larger one. One Windows run
///         reported six targets over a two-second budget in the same job, on inputs that replay in
///         microseconds — including a four-byte one billed at 2.4 seconds.
///     </para>
///     <para>
///         ⚠ <b>The fix is not a bigger number, and these tests exist so that it cannot quietly become
///         one.</b> An over-budget case is now asked again and judged on its cheapest reading, which
///         separates the two populations that a single reading cannot:
///         <see cref="AOneOffStallIsNotAFinding" /> is a cost the input cannot be made to pay twice
///         and passes, <see cref="ACaseThatIsSlowEveryTimeIsStillAFinding" /> is a cost it pays on
///         demand and fails. <see cref="WithoutConfirmationTheSameStallIsAFinding" /> runs the first
///         of those with the confirmations turned off and watches it go red, because a budget that
///         can no longer fire is worse than one that fires when it should not, and the only way to
///         know which of the two this is is to run the same target both ways.
///     </para>
/// </remarks>
public sealed class CaseBudgetTests {
    /// <summary>Comfortably longer than a real case here and far shorter than a stall.</summary>
    static readonly TimeSpan Budget = TimeSpan.FromMilliseconds(40);

    /// <summary>What a stalling call costs, chosen well clear of the scheduler's own resolution.</summary>
    static readonly TimeSpan Stall = TimeSpan.FromMilliseconds(200);

    /// <summary>Decoder calls a run makes before its first measured case: the corpus replay.</summary>
    /// <remarks>
    ///     The one seed each target below offers, plus the empty input every run is given. Both are
    ///     replayed before the loop starts and neither is measured, so they are the only calls in these
    ///     runs that are not a generated case. Asserted rather than allowed for, so that a change to
    ///     what a run replays fails here and is read rather than absorbed.
    /// </remarks>
    const int Replays = 2;

    /// <summary>A cost the input cannot be asked to pay a second time is not a finding.</summary>
    /// <remarks>
    ///     The shape of every false positive this oracle has produced: the first reading is over the
    ///     budget by a factor of five and the input, handed back to the same decoder, costs
    ///     microseconds. <see cref="FuzzOutcome.Acquitted" /> is asserted rather than only
    ///     <see cref="FuzzOutcome.Clean" />, because a run in which the budget was never tripped at all
    ///     is also clean and would pass this without exercising a line of it.
    /// </remarks>
    [Fact]
    public void AOneOffStallIsNotAFinding() {
        var target = new StallsOncePerInputTarget();

        var session = new FuzzSession(target, 1) {
            CaseBudget = Budget,
            WarmUpCases = 0,
            AbandonProcessOnRunaway = false
        };

        var outcome = session.Run(4);

        Assert.True(outcome.Clean, $"{outcome}\n  {string.Join("\n  ", outcome.Findings)}");

        Assert.True(
            outcome.Acquitted > 0,
            $"{outcome} — no case went over the budget, so the confirmation this test is about never ran."
        );
    }

    /// <summary>The same stall, with the confirmations off, is the finding it used to be.</summary>
    /// <remarks>
    ///     ⚠ <b>The half that stops the other two proving nothing.</b> A confirmation that acquitted
    ///     everything would make <see cref="AOneOffStallIsNotAFinding" /> pass for the wrong reason and
    ///     would look exactly the same from a green build. Running the identical target with
    ///     <see cref="FuzzSession.CaseBudgetConfirmations" /> at zero shows the reading that test
    ///     acquits, and shows that what acquitted it was the confirmation rather than the target being
    ///     quick after all.
    /// </remarks>
    [Fact]
    public void WithoutConfirmationTheSameStallIsAFinding() {
        var target = new StallsOncePerInputTarget();

        var session = new FuzzSession(target, 1) {
            CaseBudget = Budget,
            CaseBudgetConfirmations = 0,
            WarmUpCases = 0,
            AbandonProcessOnRunaway = false
        };

        var outcome = session.Run(4);
        var finding = Assert.Single(outcome.Findings);

        Assert.Equal(FuzzFailure.TookTooLong, finding.Failure);
        Assert.Equal(0L, outcome.Acquitted);
    }

    /// <summary>A decode that costs the same seconds every time is still reported, and says so.</summary>
    /// <remarks>
    ///     The property the whole budget exists for, and the one a fix for flakiness is most likely to
    ///     lose. The detail is asserted as well as the failure: a finding that no longer says how many
    ///     readings agreed is a finding somebody will read as a single sample again.
    /// </remarks>
    [Fact]
    public void ACaseThatIsSlowEveryTimeIsStillAFinding() {
        var target = new AlwaysSlowTarget();

        var session = new FuzzSession(target, 1) {
            CaseBudget = Budget,
            WarmUpCases = 0,
            AbandonProcessOnRunaway = false
        };

        var outcome = session.Run(1);
        var finding = Assert.Single(outcome.Findings);

        Assert.Equal(FuzzFailure.TookTooLong, finding.Failure);
        Assert.Contains("the cheapest of 5 readings", finding.Detail, StringComparison.Ordinal);
        Assert.Equal(0L, outcome.Acquitted);

        // Every reading was taken: the first plus the four confirmations, and no early exit, because
        // no reading came back under the budget. The two on top are the corpus replay, which happens
        // before the loop and is not measured.
        Assert.Equal(5 + Replays, target.Calls);
    }

    /// <summary>A quiet target never reaches the confirmation, and pays nothing for it.</summary>
    /// <remarks>
    ///     <para>
    ///         The claim that this costs the healthy path nothing, made checkable: every call into the
    ///         decoder is a generated case, a corpus replay, or a reading the machine spoiled — and the
    ///         last term is asserted to be a rounding error rather than a rate.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>That term is not zero, and finding out that it is not zero here is the whole
    ///         diagnosis in miniature.</b> This target's decode is a fold over at most a couple of
    ///         kilobytes. Written with <see cref="FuzzOutcome.Acquitted" /> asserted at zero, it failed
    ///         on the first run on an idle laptop: one case in two hundred thousand read over forty
    ///         milliseconds, which is four orders of magnitude over what the fold costs. That is the
    ///         same event a shared CI runner produces at the two-second scale, reproduced in three and
    ///         a half seconds — a wall clock read once cannot tell a slow decode from a descheduled
    ///         thread, at any threshold, on any machine.
    ///     </para>
    /// </remarks>
    [Fact]
    public void AHealthyTargetNeverAsksTwice() {
        var target = new QuickTarget();
        var session = new FuzzSession(target, 1) { CaseBudget = Budget, WarmUpCases = 0 };
        var outcome = session.Run(200_000);

        Assert.True(outcome.Clean, $"{outcome}\n  {string.Join("\n  ", outcome.Findings)}");
        Assert.Equal(200_000L, outcome.Cases);

        Assert.True(
            outcome.Acquitted * 1000 < outcome.Cases,
            $"{outcome} — the budget is being tripped at a rate, not by an accident, so this target is "
            + "not the healthy path this test needs it to be."
        );

        // The exact accounting, and what makes the title a claim rather than a hope: a case inside its
        // budget is never decoded twice, so the only calls over the case count and the replay are the
        // confirmations the acquittals paid for, at most one full round each.
        Assert.InRange(
            target.Calls,
            200_000L + Replays,
            200_000L + Replays + (outcome.Acquitted * session.CaseBudgetConfirmations)
        );
    }

    /// <summary>A target whose first sight of an input is expensive and whose second is not.</summary>
    /// <remarks>
    ///     ⚠ <b>Keyed on the input rather than on a call count, so that where the run's corpus replay
    ///     ends does not decide what this test measures.</b> Every case the loop generates is new, so
    ///     every measured case stalls once and is then confirmed cheap — which is the population the
    ///     oracle has been mistaking for a defect, and is also, exactly, the cost an attacker cannot
    ///     make a server pay twice.
    /// </remarks>
    sealed class StallsOncePerInputTarget : IFuzzTarget {
        readonly HashSet<ulong> seen = [];

        /// <inheritdoc />
        public string Name => "stall-once";

        /// <inheritdoc />
        public string What => "a first decode that is expensive and a second that is not";

        /// <inheritdoc />
        public void Seed(ICollection<byte[]> corpus) => corpus?.Add([1, 2, 3, 4]);

        /// <inheritdoc />
        public long Run(ReadOnlySpan<byte> input) {
            if (seen.Add(Corpus.Fingerprint(input))) {
                Thread.Sleep(Stall);
            }

            return input.Length;
        }
    }

    /// <summary>A target that costs the same too-long time however often it is asked.</summary>
    sealed class AlwaysSlowTarget : IFuzzTarget {
        /// <summary>How many times the decoder was actually entered.</summary>
        public int Calls { get; private set; }

        /// <inheritdoc />
        public string Name => "always-slow";

        /// <inheritdoc />
        public string What => "a decode that is superlinear rather than unlucky";

        /// <inheritdoc />
        public void Seed(ICollection<byte[]> corpus) => corpus?.Add([1, 2, 3, 4]);

        /// <inheritdoc />
        public long Run(ReadOnlySpan<byte> input) {
            // Counted from the first measured case rather than from the corpus replay, so the number
            // asserted is the readings the budget took and not the run's whole call history.
            Calls++;
            Thread.Sleep(Stall);

            return input.Length;
        }
    }

    /// <summary>A target that does nothing slowly, and counts how often it was asked.</summary>
    sealed class QuickTarget : IFuzzTarget {
        /// <summary>How many times the decoder was entered, replay included.</summary>
        public long Calls { get; private set; }

        /// <inheritdoc />
        public string Name => "quick";

        /// <inheritdoc />
        public string What => "a fold over the input and nothing else";

        /// <inheritdoc />
        public void Seed(ICollection<byte[]> corpus) => corpus?.Add([1, 2, 3, 4]);

        /// <inheritdoc />
        public long Run(ReadOnlySpan<byte> input) {
            Calls++;

            long signature = 17;

            foreach (var value in input) {
                signature = (signature * 31) + value;
            }

            return signature & 0xFFFF;
        }
    }
}

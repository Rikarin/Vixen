// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Diagnostics;
using Xunit;

namespace Vixen.Fuzz.Tests;

/// <summary>The oracle that has to work while the case is still running.</summary>
/// <remarks>
///     <para>
///         <b>Every target below misbehaves deliberately, and each one has to misbehave in a way a
///         test can survive.</b> The failure being tested is a case that never returns, and a test that
///         reproduced that faithfully would hang the build — so each of these runs away and then stops,
///         and the assertion is that the guard had already reported it by the time it did. The
///         stalling target goes further and only stops <i>because</i> the finding appeared on disk,
///         which is what makes it a test of an in-flight oracle rather than of a post-hoc one: if the
///         guard measured after the call, nothing would ever appear, and the target would sit on its
///         own safety deadline instead.
///     </para>
///     <para>
///         ⚠ <b>Each test names all three ceilings, including the ones it is not testing.</b> The
///         guard reports the first breach it sees and nothing after it, so a run left on the defaults
///         could report the wrong one and the assertion about which limit fired would be a coin toss.
///     </para>
/// </remarks>
public sealed class CaseGuardTests {
    /// <summary>How long a misbehaving target keeps misbehaving before it gives up on its own.</summary>
    /// <remarks>
    ///     The difference between a red test and a build that never finishes. Far above what the guard
    ///     needs — it is asserted that the run came back well inside this, so reaching it is a failure
    ///     rather than a slow pass.
    /// </remarks>
    static readonly TimeSpan Safety = TimeSpan.FromSeconds(10);

    /// <summary>A case that will not come back is a finding, written out, while it is still running.</summary>
    [Fact]
    public void ACaseOverTheWallClockCapIsCaughtInFlight() {
        var directory = Fresh(nameof(ACaseOverTheWallClockCapIsCaughtInFlight));
        var target = new StallingTarget(() => Written(directory, "stall") is not null);

        var session = new FuzzSession(target, 1) {
            HardCaseCap = TimeSpan.FromMilliseconds(50),
            RunawayAllocation = long.MaxValue,
            RunawayRetention = long.MaxValue,
            AbandonProcessOnRunaway = false,
            FindingDirectory = directory
        };

        var clock = Stopwatch.StartNew();
        var outcome = session.Run(64);

        clock.Stop();

        var finding = Assert.Single(outcome.Findings);

        Assert.Equal(FuzzFailure.RanAway, finding.Failure);
        Assert.Contains("against a cap of", finding.Detail, StringComparison.Ordinal);
        Assert.Equal(StallingTarget.Seeded, finding.Input);

        // The target stops only once the guard has written the input out, so a run that took anything
        // like the safety deadline is one where the oracle did not fire until the call had returned.
        Assert.True(
            clock.Elapsed < Safety,
            $"the case ran for {clock.Elapsed} — the guard did not catch it in flight"
        );

        Assert.Equal(StallingTarget.Seeded, Written(directory, "stall"));

        // Nothing after a runaway is worth running: the thread it was on may still be busy.
        Assert.Equal(0L, outcome.Cases);
    }

    /// <summary>A case that keeps what it allocates is a finding before it has kept all of it.</summary>
    [Fact]
    public void ACaseOverTheRetentionCeilingIsCaughtInFlight() {
        var directory = Fresh(nameof(ACaseOverTheRetentionCeilingIsCaughtInFlight));
        var target = new HoardingTarget(() => Written(directory, "hoard") is not null);

        var session = new FuzzSession(target, 1) {
            HardCaseCap = Safety + Safety,
            RunawayAllocation = long.MaxValue,
            RunawayRetention = 16L << 20,
            AbandonProcessOnRunaway = false,
            FindingDirectory = directory
        };

        var outcome = session.Run(64);
        var finding = Assert.Single(outcome.Findings);

        Assert.Equal(FuzzFailure.RanAway, finding.Failure);
        Assert.Contains("holding", finding.Detail, StringComparison.Ordinal);
        Assert.NotNull(Written(directory, "hoard"));

        // The point of an in-flight oracle: it fired before the case had finished growing, so the
        // ceiling is a ceiling rather than a post-mortem.
        Assert.True(
            target.Kept < HoardingTarget.Blocks,
            $"the target had already kept all {HoardingTarget.Blocks} blocks — nothing was caught in flight"
        );
    }

    /// <summary>A case that churns without returning is a finding too, and a separate one.</summary>
    [Fact]
    public void ACaseOverTheAllocationCeilingIsCaughtInFlight() {
        var directory = Fresh(nameof(ACaseOverTheAllocationCeilingIsCaughtInFlight));
        var target = new ChurningTarget(() => Written(directory, "churn") is not null);

        var session = new FuzzSession(target, 1) {
            HardCaseCap = Safety + Safety,
            RunawayAllocation = 32L << 20,
            RunawayRetention = long.MaxValue,
            AbandonProcessOnRunaway = false,
            FindingDirectory = directory
        };

        var outcome = session.Run(64);
        var finding = Assert.Single(outcome.Findings);

        Assert.Equal(FuzzFailure.RanAway, finding.Failure);
        Assert.Contains("allocated so far", finding.Detail, StringComparison.Ordinal);

        Assert.True(
            target.Churned < ChurningTarget.Ceiling,
            $"the target churned its whole {ChurningTarget.Ceiling:N0} B budget — nothing was caught in flight"
        );
    }

    /// <summary>A target that behaves does not notice the guard is there.</summary>
    /// <remarks>
    ///     The number of cases is the assertion that matters. A guard that sampled the worker rather
    ///     than the process, or that took a lock per case, would show up here rather than in a review —
    ///     and a false positive would show up as a finding on a target whose whole body is a fold over
    ///     the input.
    /// </remarks>
    [Fact]
    public void AHealthyTargetIsUnaffected() {
        var target = new QuietTarget();
        var session = new FuzzSession(target, 1) { FindingDirectory = null };
        var outcome = session.Run(200_000);

        Assert.True(outcome.Clean, $"{outcome}\n  {string.Join("\n  ", outcome.Findings)}");
        Assert.Equal(200_000L, outcome.Cases);
    }

    /// <summary>Where a test's findings go, emptied first so a rerun cannot read the last run's.</summary>
    static string Fresh(string name) {
        var directory = Path.Combine(Path.GetTempPath(), "vixen-fuzz-guard", name);

        if (Directory.Exists(directory)) {
            Directory.Delete(directory, recursive: true);
        }

        Directory.CreateDirectory(directory);

        return directory;
    }

    /// <summary>The bytes the guard wrote out for a target, or null if it has not written any.</summary>
    static byte[]? Written(string directory, string target) {
        var folder = Path.Combine(directory, target);

        if (!Directory.Exists(folder)) {
            return null;
        }

        var files = Directory.GetFiles(folder, "*.bin");

        return files.Length == 0 ? null : File.ReadAllBytes(files[0]);
    }

    /// <summary>A target that does not return until somebody else has noticed it has not returned.</summary>
    sealed class StallingTarget(Func<bool> noticed) : IFuzzTarget {
        /// <summary>The one input it is ever given, so a finding can be checked against it.</summary>
        public static byte[] Seeded => [0xDE, 0xAD, 0xBE, 0xEF];

        /// <inheritdoc />
        public string Name => "stall";

        /// <inheritdoc />
        public string What => "a case that will not come back";

        /// <inheritdoc />
        public void Seed(ICollection<byte[]> corpus) => corpus?.Add(Seeded);

        /// <inheritdoc />
        public long Run(ReadOnlySpan<byte> input) {
            var clock = Stopwatch.StartNew();

            while (!noticed() && clock.Elapsed < Safety) {
                Thread.Sleep(1);
            }

            return 1;
        }
    }

    /// <summary>A target that keeps everything it allocates, which is what a dying host looks like.</summary>
    sealed class HoardingTarget(Func<bool> noticed) : IFuzzTarget {
        /// <summary>How many megabytes it will keep before giving up, so the test can end.</summary>
        public const int Blocks = 192;

        readonly List<byte[]> kept = [];

        /// <summary>How many it had kept when the case ended.</summary>
        public int Kept => kept.Count;

        /// <inheritdoc />
        public string Name => "hoard";

        /// <inheritdoc />
        public string What => "a case that keeps what it allocates";

        /// <inheritdoc />
        public void Seed(ICollection<byte[]> corpus) => corpus?.Add([1, 2, 3, 4]);

        /// <inheritdoc />
        public long Run(ReadOnlySpan<byte> input) {
            // Stops the moment the guard has written the finding out, so that the count left behind
            // is the evidence that the ceiling was a ceiling rather than a post-mortem. Blocks is
            // only the backstop for a guard that never fires.
            for (var block = 0; block < Blocks && !noticed(); block++) {
                // Written to, because an untouched array is address space rather than memory and the
                // figure the guard reads would not move for it.
                var megabyte = new byte[1 << 20];

                megabyte[0] = 1;
                megabyte[^1] = 1;
                kept.Add(megabyte);

                Thread.Sleep(2);
            }

            return kept.Count;
        }
    }

    /// <summary>A target that allocates without keeping any of it, which the collector absorbs.</summary>
    sealed class ChurningTarget(Func<bool> noticed) : IFuzzTarget {
        /// <summary>How much it will churn before giving up, so the test can end.</summary>
        public const long Ceiling = 4L << 30;

        /// <summary>How much it had churned when the case ended.</summary>
        public long Churned { get; private set; }

        /// <inheritdoc />
        public string Name => "churn";

        /// <inheritdoc />
        public string What => "a case that allocates and drops without returning";

        /// <inheritdoc />
        public void Seed(ICollection<byte[]> corpus) => corpus?.Add([1, 2, 3, 4]);

        /// <inheritdoc />
        public long Run(ReadOnlySpan<byte> input) {
            var clock = Stopwatch.StartNew();

            // Checked every so often rather than every block, because the check touches the disk and
            // the point of this target is to allocate faster than anything sane.
            while (Churned < Ceiling && clock.Elapsed < Safety) {
                for (var block = 0; block < 256; block++) {
                    var chunk = new byte[1 << 16];

                    chunk[0] = 1;
                    Churned += chunk.Length;
                }

                if (noticed()) {
                    break;
                }
            }

            return Churned;
        }
    }

    /// <summary>A target that does nothing worth watching, at the speed the gate runs at.</summary>
    sealed class QuietTarget : IFuzzTarget {
        /// <inheritdoc />
        public string Name => "quiet";

        /// <inheritdoc />
        public string What => "a fold over the input and nothing else";

        /// <inheritdoc />
        public void Seed(ICollection<byte[]> corpus) => corpus?.Add([1, 2, 3, 4, 5, 6, 7, 8]);

        /// <inheritdoc />
        public long Run(ReadOnlySpan<byte> input) {
            long folded = 17;

            foreach (var value in input) {
                folded = (folded * 31) + value;
            }

            // Bounded, so the corpus is a selection rather than a list — the rule IFuzzTarget.Run's
            // remarks state, and one a test target gets wrong as easily as a real one.
            return folded % 4096;
        }
    }
}

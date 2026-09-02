// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Runtime.CompilerServices;
using Xunit;

namespace Vixen.Core.Threading.Tests;

/// <summary>
///     The access declarations and the race detector they feed.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>Every scheduling test here uses <c>new JobScheduler(0)</c>, and that is the whole
///         reason they are not flaky.</b> With no workers nothing runs until somebody completes a
///         handle, so "job A is still in flight when job B is scheduled" is a fact about the program
///         rather than about how busy the machine was. The detector fires at schedule time, which is
///         what makes that possible; a detector that fired when two jobs actually overlapped could
///         only be tested with a stopwatch.
///     </para>
/// </remarks>
public class JobAccessTests {
    static readonly JobAccess WritesOne = new([], [1]);
    static readonly JobAccess ReadsOne = new([1], []);
    static readonly JobAccess WritesTwo = new([], [2]);

    [Fact]
    public void AWriteImpliesARead() {
        var access = new JobAccess([], [3]);

        Assert.Equal([3], access.Reads);
        Assert.Equal([3], access.Writes);
    }

    [Fact]
    public void ReadAgainstReadIsNotAConflict() => Assert.False(ReadsOne.ConflictsWith(new([1], [])));

    [Fact]
    public void WriteAgainstReadIsAConflict() {
        Assert.True(WritesOne.ConflictsWith(ReadsOne));
        Assert.True(ReadsOne.ConflictsWith(WritesOne));
    }

    [Fact]
    public void DisjointWritesAreNotAConflict() => Assert.False(WritesOne.ConflictsWith(WritesTwo));

    [Fact]
    public void AnUndeclaredJobIsNotPoliced() {
        // The opposite of the ECS's system-level rule, and deliberately: a job that never declared
        // anything has not opted in, and treating it as touching everything would make the detector
        // fire on every asset import that overlapped a frame.
        Assert.False(JobAccess.None.ConflictsWith(JobAccess.Everything));
        Assert.False(JobAccess.Everything.ConflictsWith(JobAccess.None));
    }

    [Fact]
    public void EverythingConflictsWithEveryDeclaredJob() {
        Assert.True(JobAccess.Everything.ConflictsWith(ReadsOne));
        Assert.True(ReadsOne.ConflictsWith(JobAccess.Everything));
        Assert.True(JobAccess.Everything.ConflictsWith(JobAccess.Everything));
    }

    [Fact]
    public void AnIdBeyondTheFirstWordStillCompares() {
        // The bitsets are sized to the largest id, so a declaration naming id 200 and one naming id
        // 3 have different word counts — the case a fixed-width comparison gets wrong by walking
        // off the shorter one or by stopping before the bit that matters.
        var high = new JobAccess([], [200]);

        Assert.True(high.ConflictsWith(new([200], [])));
        Assert.False(high.ConflictsWith(WritesOne));
    }

    [Fact]
    public void TwoConflictingJobsWithNoEdgeBetweenThemAreRefused() {
        Assert.SkipWhen(!JobScheduler.SafetyChecksEnabled, "Compiled out; needs DEBUG or VIXEN_JOB_SAFETY.");

        using var scheduler = new JobScheduler(0);
        var counter = new StrongBox<int>();

        using (scheduler.DeclareAccess(WritesOne)) {
            var first = scheduler.Schedule(new IncrementJob(counter));

            var failure = Assert.Throws<InvalidOperationException>(
                () => scheduler.Schedule(new IncrementJob(counter))
            );

            Assert.Contains("conflict", failure.Message, StringComparison.Ordinal);
            scheduler.Complete(first);
        }

        // The check ran, and it ran against something. Both halves matter: the first number says the
        // declarations arrived, the second says a pair was actually compared.
        Assert.True(scheduler.DeclaredJobsScheduled >= 2);
        Assert.True(scheduler.AccessComparisons >= 1);
    }

    [Fact]
    public void ADependencyEdgeIsWhatMakesTheSameTwoJobsLegal() {
        Assert.SkipWhen(!JobScheduler.SafetyChecksEnabled, "Compiled out; needs DEBUG or VIXEN_JOB_SAFETY.");

        using var scheduler = new JobScheduler(0);
        var counter = new StrongBox<int>();

        using (scheduler.DeclareAccess(WritesOne)) {
            var first = scheduler.Schedule(new IncrementJob(counter));
            var second = scheduler.Schedule(new IncrementJob(counter), first);

            scheduler.Complete(second);
        }

        Assert.Equal(2, counter.Value);
        Assert.Equal(2, scheduler.DeclaredJobsScheduled);
    }

    [Fact]
    public void AnAncestorSeveralEdgesBackIsStillAnAncestor() {
        Assert.SkipWhen(!JobScheduler.SafetyChecksEnabled, "Compiled out; needs DEBUG or VIXEN_JOB_SAFETY.");

        using var scheduler = new JobScheduler(0);
        var counter = new StrongBox<int>();

        using (scheduler.DeclareAccess(WritesOne)) {
            var first = scheduler.Schedule(new IncrementJob(counter));

            // The middle job declares something disjoint, so it is not what exempts the last one —
            // only the inherited ancestry can be.
            JobHandle middle;

            using (scheduler.DeclareAccess(WritesTwo)) {
                middle = scheduler.Schedule(new IncrementJob(counter), first);
            }

            var last = scheduler.Schedule(new IncrementJob(counter), middle);
            scheduler.Complete(last);
        }

        Assert.Equal(3, counter.Value);
    }

    [Fact]
    public void DisjointDeclarationsRunSideBySide() {
        Assert.SkipWhen(!JobScheduler.SafetyChecksEnabled, "Compiled out; needs DEBUG or VIXEN_JOB_SAFETY.");

        using var scheduler = new JobScheduler(0);
        var counter = new StrongBox<int>();
        var first = default(JobHandle);
        var second = default(JobHandle);

        using (scheduler.DeclareAccess(WritesOne)) {
            first = scheduler.Schedule(new IncrementJob(counter));
        }

        using (scheduler.DeclareAccess(WritesTwo)) {
            second = scheduler.Schedule(new IncrementJob(counter));
        }

        scheduler.Complete(first);
        scheduler.Complete(second);

        // Compared and cleared, rather than never compared: the pair was in flight together and the
        // detector looked at it.
        Assert.Equal(2, scheduler.DeclaredJobsScheduled);
        Assert.True(scheduler.AccessComparisons >= 1);
    }

    [Fact]
    public void AScopeEndsWithItself() {
        Assert.SkipWhen(!JobScheduler.SafetyChecksEnabled, "Compiled out; needs DEBUG or VIXEN_JOB_SAFETY.");

        using var scheduler = new JobScheduler(0);
        var counter = new StrongBox<int>();

        using (scheduler.DeclareAccess(WritesOne)) {
            scheduler.Complete(scheduler.Schedule(new IncrementJob(counter)));
        }

        // Outside the scope nothing is declared, so two jobs that would have conflicted inside it
        // are not policed at all.
        var first = scheduler.Schedule(new IncrementJob(counter));
        var second = scheduler.Schedule(new IncrementJob(counter));
        scheduler.Complete(first);
        scheduler.Complete(second);

        Assert.Equal(1, scheduler.DeclaredJobsScheduled);
    }

    [Fact]
    public void ARefusedScheduleDoesNotStrandItsSlot() {
        Assert.SkipWhen(!JobScheduler.SafetyChecksEnabled, "Compiled out; needs DEBUG or VIXEN_JOB_SAFETY.");

        var scheduler = new JobScheduler(0);
        var counter = new StrongBox<int>();

        using (scheduler.DeclareAccess(WritesOne)) {
            var first = scheduler.Schedule(new IncrementJob(counter));
            Assert.Throws<InvalidOperationException>(() => scheduler.Schedule(new IncrementJob(counter)));
            scheduler.Complete(first);
        }

        // ⚠ The assertion is that this returns at all. The detector marks the refused slot failed
        // and lets it travel the ordinary completion path rather than abandoning it between renting
        // and releasing — and a slot abandoned there is neither runnable nor free, so Dispose, which
        // drains rather than times out, would never come back. There is no timeout to write down
        // here; the test either finishes or hangs, and that is deliberate.
        scheduler.Dispose();

        // Refused, so it never ran: one increment, not two.
        Assert.Equal(1, counter.Value);
    }

    [Fact]
    public void ScheduleParallelIsPolicedToo() {
        Assert.SkipWhen(!JobScheduler.SafetyChecksEnabled, "Compiled out; needs DEBUG or VIXEN_JOB_SAFETY.");

        using var scheduler = new JobScheduler(0);
        var visits = new int[8];

        using (scheduler.DeclareAccess(WritesOne)) {
            var first = scheduler.ScheduleParallel(new VisitJob(visits), visits.Length);

            Assert.Throws<InvalidOperationException>(
                () => scheduler.ScheduleParallel(new VisitJob(visits), visits.Length)
            );

            scheduler.Complete(first);
        }
    }

    [Fact]
    public void NothingDeclaredMeansNothingChecked() {
        using var scheduler = new JobScheduler(0);
        var counter = new StrongBox<int>();

        var first = scheduler.Schedule(new IncrementJob(counter));
        var second = scheduler.Schedule(new IncrementJob(counter));
        scheduler.Complete(first);
        scheduler.Complete(second);

        // ⚠ This is the reading that must never be mistaken for "no race was found". A run that
        // never declared anything and a run whose every pair was disjoint both raise no exception;
        // only these two numbers tell them apart, which is why they are public.
        Assert.Equal(0, scheduler.DeclaredJobsScheduled);
        Assert.Equal(0, scheduler.AccessComparisons);
    }
}

// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Xunit;

namespace Vixen.Core.Tests;

/// <summary>
///     Leak tracking for resources the GC cannot see. Tracking is compiled out of release builds,
///     so every test here states which half of that it is checking rather than assuming a
///     configuration.
/// </summary>
public class LeakTrackerTests : IDisposable {
    public LeakTrackerTests() {
        LeakTracker.Reset();
        LeakTracker.IsEnabled = LeakTracker.IsSupported;
    }

    public void Dispose() {
        LeakTracker.Reset();
        LeakTracker.IsEnabled = LeakTracker.IsSupported;
        LeakTracker.CaptureStackTraces = LeakTracker.IsSupported;
        GC.SuppressFinalize(this);
    }

    [Fact]
    public void Release_builds_track_nothing_at_all() {
        // Not `if (IsSupported) Assert.Skip(…)`: IsSupported is a compile-time constant, so in the
        // configuration this test is written for that block is unreachable code — an error here.
        Assert.SkipWhen(LeakTracker.IsSupported, "This build supports tracking; nothing is compiled out.");

        Assert.Equal(LeakTracker.NotTracked, LeakTracker.Track("VkBuffer"));
        Assert.False(LeakTracker.IsEnabled);
        Assert.Equal(0, LeakTracker.LiveCount);
        Assert.Empty(LeakTracker.Snapshot());
        Assert.Equal(string.Empty, LeakTracker.FormatReport());
    }

    [Fact]
    public void A_tracked_resource_stays_live_until_it_is_untracked() {
        Assert.SkipUnless(LeakTracker.IsSupported, "Tracking is compiled out of this build.");

        var handle = LeakTracker.Track("VkBuffer", "vertex staging");

        Assert.NotEqual(LeakTracker.NotTracked, handle);
        Assert.Equal(1, LeakTracker.LiveCount);

        Assert.True(LeakTracker.Untrack(handle));
        Assert.Equal(0, LeakTracker.LiveCount);
    }

    [Fact]
    public void Untracking_the_same_handle_twice_reports_the_second_one() {
        Assert.SkipUnless(LeakTracker.IsSupported, "Tracking is compiled out of this build.");

        var handle = LeakTracker.Track("VkImage");

        Assert.True(LeakTracker.Untrack(handle));
        Assert.False(LeakTracker.Untrack(handle));
        Assert.False(LeakTracker.Untrack(LeakTracker.NotTracked));
    }

    [Fact]
    public void A_snapshot_carries_the_category_description_and_allocation_stack() {
        Assert.SkipUnless(LeakTracker.IsSupported, "Tracking is compiled out of this build.");

        LeakTracker.Track("VkBuffer", "vertex staging");

        var report = Assert.Single(LeakTracker.Snapshot());

        Assert.Equal("VkBuffer", report.Category);
        Assert.Equal("vertex staging", report.Description);
        Assert.NotNull(report.StackTrace);

        // The stack is the whole point: it has to name the method that allocated.
        Assert.Contains(
            nameof(A_snapshot_carries_the_category_description_and_allocation_stack),
            report.StackTrace,
            StringComparison.Ordinal
        );
    }

    [Fact]
    public void Stack_capture_can_be_turned_off_when_it_distorts_a_measurement() {
        Assert.SkipUnless(LeakTracker.IsSupported, "Tracking is compiled out of this build.");

        LeakTracker.CaptureStackTraces = false;
        LeakTracker.Track("VkBuffer");

        Assert.Null(Assert.Single(LeakTracker.Snapshot()).StackTrace);
    }

    [Fact]
    public void Snapshots_come_back_in_allocation_order() {
        Assert.SkipUnless(LeakTracker.IsSupported, "Tracking is compiled out of this build.");

        LeakTracker.Track("first");
        LeakTracker.Track("second");
        LeakTracker.Track("third");

        Assert.Equal(
            new[] { "first", "second", "third" },
            LeakTracker.Snapshot().Select(static report => report.Category)
        );
    }

    [Fact]
    public void Disabling_tracking_stops_recording_without_dropping_what_is_already_live() {
        Assert.SkipUnless(LeakTracker.IsSupported, "Tracking is compiled out of this build.");

        LeakTracker.Track("kept");
        LeakTracker.IsEnabled = false;

        Assert.Equal(LeakTracker.NotTracked, LeakTracker.Track("ignored"));
        Assert.Equal(1, LeakTracker.LiveCount);
    }

    [Fact]
    public void A_report_counts_by_category_and_lists_the_survivors() {
        Assert.SkipUnless(LeakTracker.IsSupported, "Tracking is compiled out of this build.");

        LeakTracker.CaptureStackTraces = false;
        LeakTracker.Track("VkBuffer", "a");
        LeakTracker.Track("VkBuffer", "b");
        LeakTracker.Track("VkImage", "c");

        var report = LeakTracker.FormatReport();

        Assert.Contains("3 undisposed resource(s)", report, StringComparison.Ordinal);
        Assert.Contains("VkBuffer: 2", report, StringComparison.Ordinal);
        Assert.Contains("VkImage: 1", report, StringComparison.Ordinal);
        Assert.Contains("— a", report, StringComparison.Ordinal);
    }

    [Fact]
    public void Nothing_live_means_nothing_to_report() {
        Assert.Equal(string.Empty, LeakTracker.FormatReport());
        Assert.Empty(LeakTracker.Snapshot());
    }
}

// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Xunit;

namespace Vixen.Platform.Desktop.Tests;

/// <summary>The OS end of <c>prefers-reduced-motion</c> and <c>forced-colors</c>.</summary>
/// <remarks>
///     <para>
///         ⚠ <b>Ask what this prints on the day the source is disconnected — which is the day
///         before this file, for every one of these settings.</b> Both media features have evaluated
///         since they landed and <c>Animator.ReduceMotion</c> has honoured the first for as long, and
///         every writer of <c>MediaPreferences</c> in the tree was a test: so an application animated
///         at a user who had switched animation off and every assertion in the repository stayed
///         green. This one goes red, because on a Mac or a Windows box a <c>null</c> here means
///         nobody asked the operating system.
///     </para>
///     <para>
///         <b>macOS and Windows only, and the skip is honest rather than convenient</b> — the same
///         line <see cref="DesktopAppearanceTests" /> draws. Linux's answer is two <c>gsettings</c>
///         subprocesses that a CI container legitimately does not have, so <c>null</c> there is a
///         correct answer and cannot be asserted against.
///     </para>
/// </remarks>
public sealed class DesktopAccessibilityTests {
    [Fact]
    public void TheOperatingSystemIsActuallyAsked() {
        Assert.SkipUnless(
            OperatingSystem.IsMacOS() || OperatingSystem.IsWindows(),
            "Only macOS and Windows have a reader that can insist on an answer."
        );

        var current = new DesktopAccessibility().Current;

        Assert.NotNull(current.ReduceMotion);
        Assert.NotNull(current.HighContrast);
    }

    /// <summary>The re-read cadence is a count of pumps, so it is the same on any machine.</summary>
    /// <remarks>
    ///     ⚠ <b>A counter and not a stopwatch</b>, for the reason the appearance's own cadence test
    ///     gives: a poll interval expressed in milliseconds is this repository's largest flake source
    ///     and is also untestable. Counting pumps makes "it re-reads on the sixteenth and not before"
    ///     something a test can state exactly.
    /// </remarks>
    [Fact]
    public void TheSourceIsRereadOnACountedCadenceAndNotEveryPump() {
        var reads = 0;
        var answer = new SystemAccessibility(false, false);

        var accessibility = new DesktopAccessibility(
            () => {
                reads++;
                return answer;
            },
            repeatable: true
        );

        // One read at construction, which is what makes the value readable before the first frame.
        Assert.Equal(1, reads);
        Assert.Equal(false, accessibility.Current.ReduceMotion);

        answer = new SystemAccessibility(true, false);

        for (var pump = 1; pump < DesktopAppearance.PumpsBetweenReads; pump++) {
            Assert.False(accessibility.Pump());
            Assert.Equal(1, reads);
        }

        Assert.True(accessibility.Pump());
        Assert.Equal(2, reads);
        Assert.Equal(true, accessibility.Current.ReduceMotion);
    }

    /// <summary>A setting that has not moved owes no event.</summary>
    /// <remarks>
    ///     A host applies the value on every one of these, and an event posted sixteen pumps apart
    ///     for a machine nobody touched would re-evaluate every media scope in the document four
    ///     times a second for ever.
    /// </remarks>
    [Fact]
    public void ASettingThatDidNotMovePostsNothing() {
        var accessibility = new DesktopAccessibility(() => new SystemAccessibility(true, true), repeatable: true);

        for (var pump = 0; pump < DesktopAppearance.PumpsBetweenReads * 3; pump++) {
            Assert.False(accessibility.Pump());
        }
    }

    /// <summary>
    ///     ⚠ A platform with no reader never polls at all, rather than polling a <c>null</c>. That is
    ///     what keeps <c>gsettings</c> — two subprocesses — off the frame loop on Linux.
    /// </summary>
    [Fact]
    public void A_platform_with_no_reader_never_polls() {
        var accessibility = new DesktopAccessibility(read: null, repeatable: false);

        Assert.Equal(SystemAccessibility.Unknown, accessibility.Current);

        for (var pump = 0; pump < DesktopAppearance.PumpsBetweenReads * 2; pump++) {
            Assert.False(accessibility.Pump());
        }
    }
}

// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Xunit;

namespace Vixen.Platform.Desktop.Tests;

/// <summary>The OS end of <c>prefers-color-scheme</c>: that something actually reads the system.</summary>
/// <remarks>
///     <para>
///         ⚠ <b>Ask what this prints on the day the source is disconnected.</b> Every other assertion
///         about the colour scheme in this repository runs against a headless platform whose value a
///         test wrote, so all of them stay green with no operating-system reader at all — which is
///         precisely the state the tree was in until this file existed. This one fails, because on a
///         Mac or a Windows box <see cref="SystemColorScheme.Unknown" /> means nobody asked.
///     </para>
///     <para>
///         <b>macOS and Windows only, and the skip is honest rather than convenient.</b> Linux's
///         answer comes from <c>gsettings</c>, which a CI container legitimately does not have — so
///         <see cref="SystemColorScheme.Unknown" /> there is a correct answer and cannot be asserted
///         against.
///     </para>
/// </remarks>
public sealed class DesktopAppearanceTests {
    [Fact]
    public void TheOperatingSystemIsActuallyAsked() {
        Assert.SkipUnless(
            OperatingSystem.IsMacOS() || OperatingSystem.IsWindows(),
            "Only macOS and Windows have an appearance this reader can insist on."
        );

        Assert.NotEqual(SystemColorScheme.Unknown, new DesktopAppearance().Current);
    }

    /// <summary>The re-read cadence is a count of pumps, so it is the same on any machine.</summary>
    /// <remarks>
    ///     ⚠ <b>A counter and not a stopwatch.</b> A poll interval expressed in milliseconds is this
    ///     repository's largest flake source, and it is also untestable: a test would either sleep or
    ///     assert nothing. Counting pumps makes "it re-reads on the sixteenth and not before"
    ///     something a test can state exactly — and the sixteenth read reporting the value the source
    ///     moved to is the whole of the change-notification contract.
    /// </remarks>
    [Fact]
    public void TheSourceIsRereadOnACountedCadenceAndNotEveryPump() {
        var reads = 0;
        var answer = SystemColorScheme.Light;

        var appearance = new DesktopAppearance(
            () => {
                reads++;
                return answer;
            },
            repeatable: true
        );

        // One read at construction, which is what makes the value readable before the first frame.
        Assert.Equal(1, reads);
        Assert.Equal(SystemColorScheme.Light, appearance.Current);

        answer = SystemColorScheme.Dark;

        for (var pump = 1; pump < DesktopAppearance.PumpsBetweenReads; pump++) {
            Assert.False(appearance.Pump(), $"pump {pump} re-read the system, which is {pump} times too often.");
            Assert.Equal(SystemColorScheme.Light, appearance.Current);
        }

        Assert.True(appearance.Pump());
        Assert.Equal(2, reads);
        Assert.Equal(SystemColorScheme.Dark, appearance.Current);

        // And a read that finds no change reports none, or every sixteenth frame would restyle the
        // whole document for nothing.
        for (var pump = 0; pump < DesktopAppearance.PumpsBetweenReads; pump++) {
            Assert.False(appearance.Pump());
        }

        Assert.Equal(3, reads);
    }

    /// <summary>A source too expensive to poll is read once and never again.</summary>
    /// <remarks>
    ///     Linux's is a <c>gsettings</c> subprocess. ⚠ <b>Asserted rather than commented</b>, because
    ///     "do not poll this one" is a rule that survives exactly as long as nobody simplifies the
    ///     branch away — and the cost of losing it is a fork and a D-Bus round trip four times a
    ///     second for the life of the application, which no test would otherwise notice.
    /// </remarks>
    [Fact]
    public void AnUnrepeatableSourceIsNeverPolled() {
        var reads = 0;

        var appearance = new DesktopAppearance(
            () => {
                reads++;
                return SystemColorScheme.Dark;
            },
            repeatable: false
        );

        for (var pump = 0; pump < DesktopAppearance.PumpsBetweenReads * 3; pump++) {
            Assert.False(appearance.Pump());
        }

        Assert.Equal(1, reads);
        Assert.Equal(SystemColorScheme.Dark, appearance.Current);
    }
}

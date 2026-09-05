// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Platform.Linux;
using Vixen.Platform.MacOS;
using Vixen.Platform.Windows;

namespace Vixen.Platform.Desktop;

/// <summary>The OS accessibility settings, read for whichever desktop this is, and re-read at a
/// bounded rate.</summary>
/// <remarks>
///     <para>
///         <b>The same shape as <see cref="DesktopAppearance" /> and deliberately the same cadence.</b>
///         SDL 2 reports none of this either, each desktop answers in its own assembly, and both
///         settings come from the same places for the same money — so counting them on one clock
///         means a user who flips two switches in the same settings pane sees the application catch
///         up with both in the same frame instead of a quarter of a second apart.
///     </para>
///     <para>
///         ⚠ <b>Linux is read once and never re-read</b>, which is <see cref="LinuxAccessibility" />'s
///         two <c>gsettings</c> subprocesses rather than a policy about accessibility. Windows reads
///         two <c>SystemParametersInfo</c> calls and macOS a defaults dictionary; both are
///         microseconds and both are worth polling.
///     </para>
/// </remarks>
sealed class DesktopAccessibility {
    readonly Func<SystemAccessibility>? read;
    readonly bool repeatable;

    int pumps;

    internal DesktopAccessibility(Func<SystemAccessibility>? read, bool repeatable) {
        this.read = read;
        this.repeatable = repeatable;

        Current = read?.Invoke() ?? SystemAccessibility.Unknown;
    }

    /// <summary>The settings for the desktop this process is running on.</summary>
    public DesktopAccessibility()
        : this(Reader(), OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()) { }

    /// <summary>What was last read.</summary>
    public SystemAccessibility Current { get; private set; }

    static Func<SystemAccessibility>? Reader() {
        if (OperatingSystem.IsWindows()) {
            return WindowsAccessibility.Read;
        }

        if (OperatingSystem.IsMacOS()) {
            return MacOSAccessibility.Read;
        }

        return OperatingSystem.IsLinux() ? LinuxAccessibility.Read : null;
    }

    /// <summary>Advances the poll counter and re-reads when it comes round.</summary>
    /// <returns>Whether anything moved, and therefore whether an event is owed.</returns>
    public bool Pump() {
        if (read is null || !repeatable || ++pumps < DesktopAppearance.PumpsBetweenReads) {
            return false;
        }

        pumps = 0;

        var current = read();

        if (current == Current) {
            return false;
        }

        Current = current;
        return true;
    }
}

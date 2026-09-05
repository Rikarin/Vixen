// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Platform.Linux;
using Vixen.Platform.MacOS;
using Vixen.Platform.Windows;

namespace Vixen.Platform.Desktop;

/// <summary>The OS appearance, read for whichever desktop this is, and re-read at a bounded rate.</summary>
/// <remarks>
///     <para>
///         <b>SDL 2 has no answer here at all</b> — no <c>SDL_GetSystemTheme</c>, which arrived in
///         SDL 3 — so unlike almost everything else <c>DesktopPlatform</c> reports, this cannot be
///         asked of the portable layer and delegated to a supplement only for the parts it does
///         better. Each of the three desktops answers it in its own assembly and this picks one,
///         exactly as <see cref="DesktopSupplements" /> picks a supplement.
///     </para>
///     <para>
///         ⚠ <b>Change notification is polling, and the interval is counted in pumps rather than in
///         milliseconds.</b> The proper sources are an <c>NSDistributedNotificationCenter</c>
///         observer and <c>WM_SETTINGCHANGE</c>, and neither reaches a process whose message loop is
///         SDL's — SDL 2 does not forward the Windows message without <c>SDL_SYSWMEVENT</c>, and
///         installing an Objective-C observer needs a class this assembly does not have. A counter
///         is also the form the repository prefers for a cadence: it is deterministic, it is the
///         same on a fast machine and a loaded one, and a test can step it exactly.
///     </para>
///     <para>
///         ⚠ <b>Linux is read once and never re-read, and that is not the same policy.</b>
///         <see cref="LinuxAppearance" /> spawns <c>gsettings</c>; macOS reads a preferences
///         dictionary and Windows reads a registry value, both of which are microseconds. Polling
///         the first at any interval a user would notice a theme change over is a subprocess every
///         few seconds for the life of the application.
///     </para>
/// </remarks>
sealed class DesktopAppearance {
    /// <summary>How many pumps between re-reads, where re-reading is affordable.</summary>
    /// <remarks>
    ///     Sixteen. At a normal frame rate that is a quarter of a second, which is below what a user
    ///     switching their system appearance perceives as a delay, and it makes the read one frame
    ///     in sixteen rather than every one.
    /// </remarks>
    internal const int PumpsBetweenReads = 16;

    readonly Func<SystemColorScheme>? read;
    readonly bool repeatable;

    int pumps;

    internal DesktopAppearance(Func<SystemColorScheme>? read, bool repeatable) {
        this.read = read;
        this.repeatable = repeatable;

        Current = read?.Invoke() ?? SystemColorScheme.Unknown;
    }

    /// <summary>The appearance for the desktop this process is running on.</summary>
    public DesktopAppearance()
        : this(Reader(), OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()) { }

    /// <summary>What was last read.</summary>
    public SystemColorScheme Current { get; private set; }

    static Func<SystemColorScheme>? Reader() {
        if (OperatingSystem.IsWindows()) {
            return WindowsAppearance.Read;
        }

        if (OperatingSystem.IsMacOS()) {
            return MacOSAppearance.Read;
        }

        return OperatingSystem.IsLinux() ? LinuxAppearance.Read : null;
    }

    /// <summary>Advances the poll counter and re-reads when it comes round.</summary>
    /// <returns>Whether the appearance moved, and therefore whether an event is owed.</returns>
    public bool Pump() {
        if (read is null || !repeatable || ++pumps < PumpsBetweenReads) {
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

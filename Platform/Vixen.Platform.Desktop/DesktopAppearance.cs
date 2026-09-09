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
    readonly Func<SystemAccent>? accent;
    readonly bool repeatable;

    int pumps;

    internal DesktopAppearance(Func<SystemColorScheme>? read, bool repeatable, Func<SystemAccent>? accent = null) {
        this.read = read;
        this.accent = accent;
        this.repeatable = repeatable;

        Current = read?.Invoke() ?? SystemColorScheme.Unknown;
        Accent = accent?.Invoke() ?? SystemAccent.Unknown;
    }

    /// <summary>The appearance for the desktop this process is running on.</summary>
    public DesktopAppearance()
        : this(Reader(), OperatingSystem.IsWindows() || OperatingSystem.IsMacOS(), AccentReader()) { }

    /// <summary>What was last read.</summary>
    public SystemColorScheme Current { get; private set; }

    /// <summary>The accent colour that was last read.</summary>
    /// <remarks>
    ///     ⚠ <b>On the appearance's poll rather than one of its own, and reported as an appearance
    ///     change.</b> An accent change is an appearance change as far as a stylesheet is concerned —
    ///     it moves what <c>AccentColor</c> resolves to and nothing else — so a second event kind
    ///     would only have produced hosts that wired one of the two. It also means the accent
    ///     inherits this poller's Linux policy, which is the right one for the same reason: the
    ///     Linux read is a subprocess.
    /// </remarks>
    public SystemAccent Accent { get; private set; }

    static Func<SystemColorScheme>? Reader() {
        if (OperatingSystem.IsWindows()) {
            return WindowsAppearance.Read;
        }

        if (OperatingSystem.IsMacOS()) {
            return MacOSAppearance.Read;
        }

        return OperatingSystem.IsLinux() ? LinuxAppearance.Read : null;
    }

    /// <summary>Which of the three desktops can answer for the accent colour.</summary>
    /// <remarks>
    ///     <para>
    ///         All three now, and each reads the pair its own desktop draws a selection with —
    ///         <c>controlAccentColor</c> with <c>alternateSelectedControlTextColor</c>,
    ///         <c>COLOR_HIGHLIGHT</c> with <c>COLOR_HIGHLIGHTTEXT</c>, and GNOME's
    ///         <c>accent-color</c> with libadwaita's one <c>accent-fg-color</c>. The pair rather
    ///         than the accent alone is the requirement rather than a nicety: <c>ApplyAccent</c>
    ///         moves the theme's token only when both roles were read.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Only the macOS reader has been measured against its own source.</b> The other
    ///         two are asserted where a machine of that kind runs them — a Windows runner reads the
    ///         real system colours, and the GNOME name table is asserted anywhere because it is a
    ///         pure function — and neither was checked on a desktop while this was written. Each
    ///         reads a colour the system chose rather than one this repository invented, which is
    ///         what makes that acceptable; a mis-decoded DWM colourisation word would not have been,
    ///         and <see cref="Vixen.Platform.Windows.WindowsAccent" /> records why it is not the
    ///         source.
    ///     </para>
    ///     <para>
    ///         A platform with no reader still answers <see cref="SystemAccent.Unknown" />, which
    ///         leaves the document's palette on its default tables — and so does a desktop whose
    ///         source is genuinely missing, which is the case an older GNOME lands in.
    ///     </para>
    /// </remarks>
    static Func<SystemAccent>? AccentReader() {
        if (OperatingSystem.IsWindows()) {
            return WindowsAccent.Read;
        }

        if (OperatingSystem.IsMacOS()) {
            return MacOSAccent.Read;
        }

        return OperatingSystem.IsLinux() ? LinuxAccent.Read : null;
    }

    /// <summary>Advances the poll counter and re-reads when it comes round.</summary>
    /// <returns>Whether the appearance moved, and therefore whether an event is owed.</returns>
    public bool Pump() {
        if (read is null || !repeatable || ++pumps < PumpsBetweenReads) {
            return false;
        }

        pumps = 0;

        var current = read();

        // ⚠ Both are read and both are compared, and the accent is read even when the scheme did
        // not move. A user picking a new accent does not touch the appearance, so an accent read
        // guarded by "the scheme changed" would be one that only ever fires on the way into dark
        // mode — which is exactly the shape of an update nobody notices is missing.
        var latest = accent?.Invoke() ?? SystemAccent.Unknown;
        var moved = current != Current || latest != Accent;

        Current = current;
        Accent = latest;

        return moved;
    }
}

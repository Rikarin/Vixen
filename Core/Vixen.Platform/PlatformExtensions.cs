// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

namespace Vixen.Platform;

/// <summary>Conveniences over <see cref="IPlatform" /> that no implementation should redefine.</summary>
/// <remarks>
///     Extension methods rather than default interface members, for a reason that only shows up in
///     use: a default interface member is invisible through the concrete type, so
///     <c>headlessPlatform.Has(…)</c> would not compile and every call site would need a cast to
///     <see cref="IPlatform" /> first. Extensions bind on the static type either way.
/// </remarks>
public static class PlatformExtensions {
    /// <summary>Whether every capability in <paramref name="required" /> is present.</summary>
    /// <param name="platform">The platform to ask.</param>
    /// <param name="required">One or more capabilities.</param>
    /// <remarks>
    ///     The `and` form, not the `or` form: asking for two capabilities and being told "yes"
    ///     because one of them happens to be available is never what a caller meant.
    /// </remarks>
    public static bool Has(this IPlatform platform, PlatformCapabilities required) {
        ArgumentNullException.ThrowIfNull(platform);
        return (platform.Capabilities & required) == required;
    }

    /// <summary>The file pickers, or <see langword="null" /> when this platform has none.</summary>
    /// <param name="platform">The platform to ask.</param>
    /// <returns><see cref="IPlatform.Dialogs" /> when
    /// <see cref="PlatformCapabilities.NativeDialogs" /> is present, and <see langword="null" />
    /// otherwise.</returns>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>Because <see cref="IPlatform.Dialogs" /> is never null and a platform with no
    ///         pickers answers every one of them with nothing-chosen — which is the same answer the
    ///         user pressing Cancel gives.</b> That conflation is deliberate on
    ///         <see cref="INativeDialogs" />: it means a caller has one code path for both. What it
    ///         is not is a way to decide whether an <c>Open…</c> menu item should be *enabled*, and
    ///         a caller that used it for that would offer a command that does nothing on a Linux
    ///         session with no <c>zenity</c>, on a headless build and on the SDL fallback — three
    ///         cases where <c>DesktopDialogs</c>' four stubs return <see langword="null" /> and read
    ///         as a cancellation.
    ///     </para>
    ///     <para>
    ///         <b><see cref="PlatformCapabilities.NativeDialogs" /> is the answer, and this is that
    ///         question spelled once.</b> SDL contributes the flag to nothing —
    ///         <c>DesktopPlatform.DetectCapabilities</c> leaves it out on purpose — and each per-OS
    ///         supplement adds it only when it actually supplied pickers, which on Linux is a runtime
    ///         question about the session. <c>EditorServices.Of</c> has asked it correctly since it
    ///         was written; it was the only place in the tree that knew to, which is what made the
    ///         convention worth moving down here.
    ///     </para>
    ///     <para>
    ///         <see cref="INativeDialogs.ShowMessageAsync" /> is <i>not</i> covered by the flag and is
    ///         always safe to call — SDL's message box is the OS's own, and it is what a fatal-error
    ///         path needs before there is a renderer. So this is about pickers, and a caller that
    ///         only wants to say something reaches <see cref="IPlatform.Dialogs" /> directly.
    ///     </para>
    /// </remarks>
    public static INativeDialogs? Pickers(this IPlatform platform) {
        ArgumentNullException.ThrowIfNull(platform);
        return platform.Has(PlatformCapabilities.NativeDialogs) ? platform.Dialogs : null;
    }

    /// <summary>The window that has keyboard focus, or <see langword="null" /> if none does.</summary>
    /// <param name="platform">The platform to ask.</param>
    /// <remarks>
    ///     <para>
    ///         Skips closed windows, and has to: <see cref="IPlatform.Windows" /> keeps a window the
    ///         application disposed until the start of the next pump, deliberately, so that the list
    ///         does not change under an application walking it inside its own event handling. Every
    ///         member of <see cref="IWindow" /> except <see cref="IWindow.IsClosed" /> throws once it
    ///         is closed, so asking such an entry whether it has focus throws
    ///         <see cref="ObjectDisposedException" />.
    ///     </para>
    ///     <para>
    ///         That is not theoretical. Closing the last window with the title bar's button disposes
    ///         it during <c>PumpEvents</c>, and the rest of that same frame still runs — so the frame
    ///         limiter asked this question about a window destroyed a few microseconds earlier and
    ///         brought the process down on the way out. A closed window has no focus; saying so is
    ///         the whole fix.
    ///     </para>
    /// </remarks>
    public static IWindow? FocusedWindow(this IPlatform platform) {
        ArgumentNullException.ThrowIfNull(platform);

        var windows = platform.Windows;

        // Indexed rather than foreach, and the two conditions written out rather than folded into a
        // property pattern, because the order they are asked in is the entire point: `IsClosed`
        // first, or `IsFocused` throws before anything has a chance to skip it.
        for (var index = 0; index < windows.Count; index++) {
            var window = windows[index];

            if (!window.IsClosed && window.IsFocused) {
                return window;
            }
        }

        return null;
    }
}

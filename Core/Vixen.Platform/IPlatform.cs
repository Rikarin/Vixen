// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Diagnostics.CodeAnalysis;

namespace Vixen.Platform;

/// <summary>Everything the operating system does for us, behind one object.</summary>
/// <remarks>
///     <para>
///         One implementation per platform, chosen by the app head at boot and put in the
///         <c>ServiceRegistry</c>. Nothing above this assembly has a <c>#if ANDROID</c>: code that
///         needs to know what it is running on asks <see cref="Capabilities" />, which is a runtime
///         question with a runtime answer, and gets a fallback path rather than a compile error on
///         five of the six targets.
///     </para>
///     <para>
///         <b>Threading.</b> The platform is owned by the thread that created it. Every member here
///         except <see cref="PlatformEventBuffer.Post" /> on the queue behind
///         <see cref="PumpEvents" /> must be called from that thread, because the underlying APIs
///         demand it — Win32 messages are delivered to the thread that created the window, and
///         AppKit refuses to touch a window from anywhere but the main thread. That is a
///         restriction of the operating systems, not a simplification of ours, and hiding it behind
///         a lock would make it a deadlock instead of an exception.
///     </para>
/// </remarks>
public interface IPlatform : IDisposable {
    /// <summary>A name for logs and crash reports: <c>"Desktop (macOS)"</c>, <c>"Headless"</c>.</summary>
    string Name { get; }

    /// <summary>What this platform can do.</summary>
    PlatformCapabilities Capabilities { get; }

    /// <summary>Every window that exists now, in creation order.</summary>
    IReadOnlyList<IWindow> Windows { get; }

    /// <summary>The displays.</summary>
    IDisplayInfo Displays { get; }

    /// <summary>Whether the user has asked the operating system for a light or a dark appearance.</summary>
    /// <remarks>
    ///     <para>
    ///         A cached answer, refreshed by <see cref="PumpEvents" />, so reading it is a field read
    ///         and reading it once a frame is free. It moves only across a
    ///         <see cref="PlatformEventKind.SystemColorSchemeChanged" />, which is the event a host
    ///         wires to <c>@media (prefers-color-scheme: …)</c>.
    ///     </para>
    ///     <para>
    ///         ⚠ <b><see cref="SystemColorScheme.Unknown" /> is a real answer and not a failure.</b>
    ///         A headless run has no appearance, and neither does a Linux desktop with no settings
    ///         daemon — reporting light for either is how an application ends up choosing a palette
    ///         the system never asked for.
    ///     </para>
    /// </remarks>
    SystemColorScheme ColorScheme { get; }

    /// <summary>Where this platform keeps files.</summary>
    IFileSystemHost FileSystem { get; }

    /// <summary>The system clipboard.</summary>
    IClipboard Clipboard { get; }

    /// <summary>Native pickers and message boxes.</summary>
    INativeDialogs Dialogs { get; }

    /// <summary>The process's lifecycle.</summary>
    ILifecycle Lifecycle { get; }

    /// <summary>Raw input devices and their state.</summary>
    IInputSource Input { get; }

    /// <summary>Text entry and the IME.</summary>
    ITextInput TextInput { get; }

    /// <summary>Battery and thermal state.</summary>
    IPowerInfo Power { get; }

    /// <summary>How many processors there are, and whether threads may be pinned to them.</summary>
    IProcessorTopology Processors { get; }

    /// <summary>Creates a window.</summary>
    /// <param name="options">What to create.</param>
    /// <returns>The window, hidden unless <see cref="WindowOptions.IsVisible" /> said
    /// otherwise.</returns>
    /// <exception cref="PlatformNotSupportedException">
    ///     A second window was asked for and <see cref="PlatformCapabilities.MultiWindow" /> is
    ///     absent.
    /// </exception>
    /// <remarks>
    ///     Succeeds without <see cref="PlatformCapabilities.Windowing" />. A headless window has a
    ///     size, an id and an event stream and shows nobody anything, which is what lets a dedicated
    ///     server run the frame loop the desktop build runs rather than a second one written for it.
    /// </remarks>
    IWindow CreateWindow(in WindowOptions options);

    /// <summary>Finds a window by the id its events carry.</summary>
    /// <param name="id">The id from <see cref="PlatformEvent.WindowId" />.</param>
    /// <param name="window">The window.</param>
    /// <returns><see langword="false" /> if no such window exists, which includes one that has
    /// been disposed since the event was queued.</returns>
    bool TryGetWindow(uint id, [NotNullWhen(true)] out IWindow? window);

    /// <summary>
    ///     Collects everything the OS has to say and returns it, in the order it happened.
    /// </summary>
    /// <returns>
    ///     A span valid until the next call. It is not a copy, so keeping it across a frame boundary
    ///     reads the next frame's events.
    /// </returns>
    /// <remarks>
    ///     Called once per frame from the thread that owns the platform. Not calling it is how a
    ///     window stops responding: the OS decides an application that is not draining its message
    ///     queue has hung, and says so.
    /// </remarks>
    ReadOnlySpan<PlatformEvent> PumpEvents();

    /// <summary>Opens a URL in whatever the user has set as their handler for it.</summary>
    /// <param name="url">An absolute URL. <c>http</c>, <c>https</c> and <c>mailto</c> work
    /// everywhere; anything else is the platform's business.</param>
    /// <returns><see langword="false" /> if the platform would not, or could not, open it.</returns>
    /// <remarks>
    ///     Hands a string to the shell, so it is only ever called with a URL the application
    ///     composed. Passing one through from content — an asset, a network message, a dropped file
    ///     — is how a documentation link becomes a way to run a program.
    /// </remarks>
    bool TryOpenUrl(string url);
}

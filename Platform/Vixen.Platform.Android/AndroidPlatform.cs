// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using Android.Content;
using Android.Content.Res;
using AndroidBuild = Android.OS.Build;
using AndroidUri = Android.Net.Uri;

namespace Vixen.Platform.Android;

/// <summary>Android, behind <see cref="IPlatform" />.</summary>
/// <remarks>
///     <para>
///         Structurally the same as iOS and for the same reason: the activity owns the main thread,
///         so the frame loop is driven from outside — by the <c>Choreographer</c> rather than a
///         <c>CADisplayLink</c> — and <see cref="PumpEvents" /> drains what the platform's callbacks
///         already posted.
///     </para>
///     <para>
///         <b>What is genuinely different is that the surface comes and goes.</b> On iOS the drawable
///         is resized; here the <c>ANativeWindow</c> is destroyed outright when the activity stops
///         and a new one appears when it starts again. <see cref="IWindow.Surface" /> reports
///         <c>CanPresent</c> false in between, which is a state an application spends real time in.
///     </para>
/// </remarks>
public sealed class AndroidPlatform : IPlatform {
    readonly PlatformEventBuffer events = new();
    readonly List<IWindow> windows = [];
    readonly AndroidInput input = new();
    readonly Context context;

    AndroidWindow? window;
    uint nextWindowId = 1;
    bool disposed;

    /// <summary>Creates the platform.</summary>
    /// <param name="context">The activity.</param>
    public AndroidPlatform(Context context) {
        ArgumentNullException.ThrowIfNull(context);

        this.context = context;

        // One buffer, shared. Constructed here rather than in an initialiser because a field
        // initialiser cannot see `events`, and a lifecycle with its own buffer is one whose
        // Suspending and Resumed never reach PumpEvents — silently, and only on a device.
        MobileLifecycle = new(events);

        Displays = new AndroidDisplays(context);
        FileSystem = new AndroidFileSystemHost(context);
        Clipboard = new AndroidClipboard(context);
        TextInput = new AndroidTextInput(context);
        Power = new AndroidPower(context);

        // Seeded here and not left to the first pump: a host reads this to choose a palette before
        // it draws a frame, and a first frame drawn against `Unknown` is a visible flash of the
        // wrong theme on a device that has always been in dark mode.
        ColorScheme = ReadColorScheme();
    }

    /// <inheritdoc />
    public string Name =>
        $"Android {AndroidBuild.VERSION.Release} on {AndroidBuild.Manufacturer} {AndroidBuild.Model}";

    /// <inheritdoc />
    /// <remarks>
    ///     No <see cref="PlatformCapabilities.MultiWindow" />: an activity has one surface, and
    ///     Android's own multi-window is the system putting two applications side by side rather than
    ///     an application opening two windows.
    /// </remarks>
    public PlatformCapabilities Capabilities =>
        PlatformCapabilities.Windowing
        | PlatformCapabilities.DisplayEnumeration
        | PlatformCapabilities.Clipboard
        | PlatformCapabilities.TextInput
        | PlatformCapabilities.PowerInfo
        | PlatformCapabilities.Suspension;

    /// <inheritdoc />
    public IReadOnlyList<IWindow> Windows => windows;

    /// <inheritdoc />
    public IDisplayInfo Displays { get; }

    /// <inheritdoc />
    /// <remarks>
    ///     <para>
    ///         <c>uiMode</c>'s night mask, off the activity's own resources rather than the
    ///         application's — an activity can carry a per-activity night mode override, and the
    ///         override is the one whose palette is on screen.
    ///     </para>
    ///     <para>
    ///         ⚠ <b><c>UiModeNightUndefined</c> is a real third value and is reported as
    ///         <see cref="SystemColorScheme.Unknown" />.</b> It is what a configuration that has
    ///         never resolved says, and mapping it to light is how an application picks a palette on
    ///         a device that has not answered yet.
    ///     </para>
    ///     <para>
    ///         Polled in <see cref="PumpEvents" />: a night-mode change arrives as a configuration
    ///         change, which recreates the activity by default and is therefore not an event this
    ///         platform can see from where it sits.
    ///     </para>
    /// </remarks>
    public SystemColorScheme ColorScheme { get; private set; }

    /// <inheritdoc />
    public IFileSystemHost FileSystem { get; }

    /// <inheritdoc />
    public IClipboard Clipboard { get; }

    /// <inheritdoc />
    /// <remarks>
    ///     Refuses everything. Android's file picker is an intent whose result arrives on the
    ///     activity, and a dialog is a fragment with a lifecycle — both are real work, and both would
    ///     have to return a content URI rather than a path. See the iOS platform, which reaches the
    ///     same conclusion for the same reason.
    /// </remarks>
    public INativeDialogs Dialogs { get; } = new AndroidDialogs();

    /// <inheritdoc />
    public ILifecycle Lifecycle => MobileLifecycle;

    /// <inheritdoc />
    public IInputSource Input => input;

    /// <inheritdoc />
    public ITextInput TextInput { get; }

    /// <inheritdoc />
    public IPowerInfo Power { get; }

    /// <inheritdoc />
    public IProcessorTopology Processors { get; } = new AndroidProcessors();

    /// <summary>The lifecycle, as the thing the activity drives.</summary>
    internal MobileLifecycle MobileLifecycle { get; }

    /// <summary>The view the activity puts on screen.</summary>
    internal AndroidGameView? View => window?.View;

    /// <inheritdoc />
    /// <exception cref="PlatformNotSupportedException">A second window was asked for.</exception>
    public IWindow CreateWindow(in WindowOptions options) {
        ObjectDisposedException.ThrowIf(disposed, this);

        if (window is { IsClosed: false }) {
            throw new PlatformNotSupportedException(
                "An Android activity has one surface. This platform does not report "
                + "PlatformCapabilities.MultiWindow, which is the runtime question to ask first."
            );
        }

        var view = new AndroidGameView(context, events);
        window = new(nextWindowId++, view) { Title = options.Title };

        windows.Clear();
        windows.Add(window);

        return window;
    }

    /// <inheritdoc />
    public bool TryGetWindow(uint id, [NotNullWhen(true)] out IWindow? window) {
        window = this.window is { IsClosed: false } candidate && candidate.Id == id ? candidate : null;
        return window is not null;
    }

    /// <inheritdoc />
    public ReadOnlySpan<PlatformEvent> PumpEvents() {
        ObjectDisposedException.ThrowIf(disposed, this);

        if (window is { IsClosed: true }) {
            windows.Clear();
            window = null;
        }

        var scheme = ReadColorScheme();

        if (scheme != ColorScheme) {
            ColorScheme = scheme;

            events.Post(
                PlatformEvent.Application(PlatformEventKind.SystemColorSchemeChanged, Stopwatch.GetTimestamp())
            );
        }

        return events.Drain();
    }

    /// <inheritdoc />
    /// <remarks>
    ///     Refuses rather than throwing when nothing can handle the URL — an <c>ActivityNotFound</c>
    ///     escaping into a frame would take the application down for a link that did not open.
    /// </remarks>
    public bool TryOpenUrl(string url) {
        ObjectDisposedException.ThrowIf(disposed, this);
        ArgumentException.ThrowIfNullOrEmpty(url);

        try {
            var intent = new Intent(Intent.ActionView, AndroidUri.Parse(url));
            intent.AddFlags(ActivityFlags.NewTask);
            context.StartActivity(intent);
            return true;
        } catch (ActivityNotFoundException) {
            return false;
        }
    }

    SystemColorScheme ReadColorScheme() =>
        (context.Resources?.Configuration?.UiMode & UiMode.NightMask) switch {
            UiMode.NightYes => SystemColorScheme.Dark,
            UiMode.NightNo => SystemColorScheme.Light,
            _ => SystemColorScheme.Unknown
        };

    /// <summary>Posts an event as though the system had raised it.</summary>
    /// <param name="platformEvent">The event.</param>
    public void Post(in PlatformEvent platformEvent) => events.Post(platformEvent);

    /// <summary>Drops every finger, because the activity is going away.</summary>
    internal void ReleaseTouches() => window?.View.ReleaseAllTouches();

    /// <inheritdoc />
    public void Dispose() {
        if (disposed) {
            return;
        }

        disposed = true;
        MobileLifecycle.Stopping();
        window?.Dispose();
        windows.Clear();
    }
}

/// <summary>Nothing, honestly.</summary>
/// <remarks>
///     Every file operation on Android goes through the Storage Access Framework: an intent, a result
///     on the activity, and a content URI rather than a path. Returning one as a <see cref="string" />
///     would produce something that looks like a file and cannot be opened, which is the same
///     conclusion the iOS platform reaches about the document picker. A message box is an
///     <c>AlertDialog</c> and needs an activity with a live window; it is refused here rather than
///     shown from a context that may be finishing.
/// </remarks>
internal sealed class AndroidDialogs : INativeDialogs {
    /// <inheritdoc />
    public ValueTask<string?> OpenFileAsync(
        FileDialogOptions options,
        IWindow? owner = null,
        CancellationToken cancellationToken = default
    ) =>
        ValueTask.FromResult<string?>(null);

    /// <inheritdoc />
    public ValueTask<IReadOnlyList<string>> OpenFilesAsync(
        FileDialogOptions options,
        IWindow? owner = null,
        CancellationToken cancellationToken = default
    ) =>
        ValueTask.FromResult<IReadOnlyList<string>>([]);

    /// <inheritdoc />
    public ValueTask<string?> SaveFileAsync(
        FileDialogOptions options,
        IWindow? owner = null,
        CancellationToken cancellationToken = default
    ) =>
        ValueTask.FromResult<string?>(null);

    /// <inheritdoc />
    public ValueTask<string?> OpenFolderAsync(
        FileDialogOptions options,
        IWindow? owner = null,
        CancellationToken cancellationToken = default
    ) =>
        ValueTask.FromResult<string?>(null);

    /// <inheritdoc />
    public ValueTask<MessageBoxResult> ShowMessageAsync(
        MessageBoxOptions options,
        IWindow? owner = null,
        CancellationToken cancellationToken = default
    ) =>
        ValueTask.FromResult(MessageBoxResult.None);
}

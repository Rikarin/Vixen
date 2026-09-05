// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Diagnostics.CodeAnalysis;
using Foundation;
using UIKit;

namespace Vixen.Platform.Ios;

/// <summary>iOS, behind <see cref="IPlatform" />.</summary>
/// <remarks>
///     <para>
///         <b>The frame loop is not this platform's to own, and that is the one structural
///         difference from the desktop.</b> UIKit owns the main thread from <c>UIApplicationMain</c>
///         onwards; there is no place to put a <c>while</c> loop. So a frame is driven by
///         <see cref="IosApplicationHost" />'s <c>CADisplayLink</c> calling
///         <c>VixenApplication.RunFrame</c> — which is public for exactly this, and is the reason
///         doc 17 insists nothing in the boot path is inaccessible.
///     </para>
///     <para>
///         <b><see cref="PumpEvents" /> therefore drains rather than polls.</b> There is no message
///         queue to ask: UIKit delivered the touches and the lifecycle callbacks on its own schedule,
///         each of them posted into <see cref="PlatformEventBuffer" /> as it arrived. Draining once
///         per frame gives the application the same "everything since the last frame, in order"
///         contract the desktop's real pump does.
///     </para>
///     <para>
///         <b>One window, and asking for a second is an error rather than a silent second screen.</b>
///         <see cref="PlatformCapabilities.MultiWindow" /> is absent, which is the runtime question
///         an application is supposed to ask.
///     </para>
///     <para>
///         Belongs to the main thread, because UIKit does — more strictly than SDL: touching a
///         <c>UIView</c> from a worker is a crash rather than a race.
///     </para>
/// </remarks>
public sealed class IosPlatform : IPlatform {
    readonly PlatformEventBuffer events = new();
    readonly List<IWindow> windows = [];
    readonly IosInput input = new();
    readonly IosTextInput textInput;

    IosWindow? window;
    uint nextWindowId = 1;
    bool disposed;

    /// <summary>Creates the platform.</summary>
    public IosPlatform() {
        textInput = new(events);
        MobileLifecycle = new(events);
        FileSystem = new IosFileSystemHost();
        Dialogs = new IosDialogs(this);

        // Seeded rather than left to the first pump: a host reads this to choose a palette before it
        // draws, and a first frame drawn against `Unknown` is a flash of the wrong theme.
        ColorScheme = ReadColorScheme();
    }

    /// <inheritdoc />
    public string Name => $"iOS {UIDevice.CurrentDevice.SystemVersion} on {UIDevice.CurrentDevice.Model}";

    /// <inheritdoc />
    /// <remarks>
    ///     No <see cref="PlatformCapabilities.MultiWindow" />, no
    ///     <see cref="PlatformCapabilities.WindowPositioning" />, no
    ///     <see cref="PlatformCapabilities.Cursor" />, no
    ///     <see cref="PlatformCapabilities.NativeDialogs" /> and no
    ///     <see cref="PlatformCapabilities.DragAndDrop" /> — each absent because the thing itself is
    ///     absent, not because it is unimplemented. Windowing is present: there is one window and it
    ///     is real.
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
    public IDisplayInfo Displays { get; } = new IosDisplays();

    /// <inheritdoc />
    /// <remarks>
    ///     <para>
    ///         <c>UITraitCollection.CurrentTraitCollection</c>, which is the trait environment of
    ///         whatever is being laid out — and outside a layout pass, the window's. That is the
    ///         answer an application wants: a view controller may override the style for its own
    ///         subtree, and the override is what is on screen.
    ///     </para>
    ///     <para>
    ///         ⚠ <b><c>Unspecified</c> is reported as <see cref="SystemColorScheme.Unknown" />.</b>
    ///         It is what iOS 12 and earlier always say and what a trait collection that has not been
    ///         resolved says; mapping it to light is how an application chooses a palette on a system
    ///         that has not answered.
    ///     </para>
    ///     <para>
    ///         Polled in <see cref="PumpEvents" />, because the change arrives as
    ///         <c>traitCollectionDidChange</c> on a view controller and this platform does not own
    ///         one.
    ///     </para>
    /// </remarks>
    public SystemColorScheme ColorScheme { get; private set; }

    /// <inheritdoc />
    public IFileSystemHost FileSystem { get; }

    /// <inheritdoc />
    public IClipboard Clipboard { get; } = new IosClipboard();

    /// <inheritdoc />
    public INativeDialogs Dialogs { get; }

    /// <inheritdoc />
    public ILifecycle Lifecycle => MobileLifecycle;

    /// <inheritdoc />
    public IInputSource Input => input;

    /// <inheritdoc />
    public ITextInput TextInput => textInput;

    /// <inheritdoc />
    public IPowerInfo Power { get; } = new IosPower();

    /// <inheritdoc />
    public IProcessorTopology Processors { get; } = new IosProcessors();

    /// <summary>The lifecycle, as the thing the delegate drives rather than the thing callers read.</summary>
    internal MobileLifecycle MobileLifecycle { get; }

    /// <summary>What a modal is presented from.</summary>
    internal UIViewController? RootController { get; private set; }

    /// <inheritdoc />
    /// <exception cref="PlatformNotSupportedException">A second window was asked for.</exception>
    /// <remarks>
    ///     Most of <paramref name="options" /> is not applicable and is ignored rather than
    ///     approximated — there is no position, no decoration and no always-on-top on a phone. The
    ///     title is kept because the window keeps it; <see cref="WindowOptions.IsVisible" /> is
    ///     honoured, because a window that is created and not shown is a legitimate thing to want
    ///     while the first frame is prepared.
    /// </remarks>
    public IWindow CreateWindow(in WindowOptions options) {
        ObjectDisposedException.ThrowIf(disposed, this);

        if (window is { IsClosed: false }) {
            throw new PlatformNotSupportedException(
                "iOS has one window. This platform does not report PlatformCapabilities.MultiWindow, "
                + "which is the runtime question to ask before creating a second."
            );
        }

        var native = NewWindow();
        var controller = new IosViewController();

        native.RootViewController = controller;
        RootController = controller;

        window = new(nextWindowId++, native, controller, events) { Title = options.Title };
        windows.Clear();
        windows.Add(window);

        if (options.IsVisible) {
            window.Show();
        }

        return window;
    }

    /// <summary>
    ///     A UIWindow attached to the scene the system connected, if there is one.
    /// </summary>
    /// <remarks>
    ///     Every iOS application has been scene-based underneath since iOS 13, whether or not it
    ///     declares a scene manifest, so the connected scene is normally there and is what iOS 26
    ///     wants a window built from. If it is not — which happens if a window is asked for before
    ///     the scene has connected — there is nothing to fall back to but the frame constructor, and
    ///     that is what the failure would otherwise be: a black screen with no explanation.
    /// </remarks>
    static UIWindow NewWindow() {
        var scene = UIApplication.SharedApplication.ConnectedScenes
            .OfType<UIWindowScene>()
            .FirstOrDefault(candidate => candidate.ActivationState is UISceneActivationState.ForegroundActive
                or UISceneActivationState.ForegroundInactive);

        if (scene is not null) {
            return new(scene);
        }

        throw new InvalidOperationException(
            "No UIWindowScene is connected yet, so there is nothing to attach a window to. Create the "
            + "window from the frame callback or from the scene delegate rather than before UIKit has "
            + "finished launching."
        );
    }

    /// <inheritdoc />
    public bool TryGetWindow(uint id, [NotNullWhen(true)] out IWindow? window) {
        window = this.window is { IsClosed: false } candidate && candidate.Id == id ? candidate : null;
        return window is not null;
    }

    /// <inheritdoc />
    /// <remarks>
    ///     Drops a disposed window at the start of the pump rather than when it was disposed, which
    ///     is the same rule the desktop platform follows and for the same reason: an application
    ///     walking <see cref="Windows" /> inside its own event handling must not have the list change
    ///     under it.
    /// </remarks>
    public ReadOnlySpan<PlatformEvent> PumpEvents() {
        ObjectDisposedException.ThrowIf(disposed, this);

        if (window is { IsClosed: true }) {
            windows.Clear();
            window = null;
            RootController = null;
        }

        var scheme = ReadColorScheme();

        if (scheme != ColorScheme) {
            ColorScheme = scheme;

            events.Post(
                PlatformEvent.Application(
                    PlatformEventKind.SystemColorSchemeChanged,
                    System.Diagnostics.Stopwatch.GetTimestamp()
                )
            );
        }

        return events.Drain();
    }

    static SystemColorScheme ReadColorScheme() =>
        UITraitCollection.CurrentTraitCollection.UserInterfaceStyle switch {
            UIUserInterfaceStyle.Dark => SystemColorScheme.Dark,
            UIUserInterfaceStyle.Light => SystemColorScheme.Light,
            _ => SystemColorScheme.Unknown
        };

    /// <inheritdoc />
    /// <remarks>
    ///     Refuses anything the system will not open rather than reporting success for a URL that
    ///     silently did nothing — iOS declines schemes an application has not declared in
    ///     <c>LSApplicationQueriesSchemes</c>, and <c>CanOpenUrl</c> is how it says so before the
    ///     fact.
    /// </remarks>
    public bool TryOpenUrl(string url) {
        ObjectDisposedException.ThrowIf(disposed, this);
        ArgumentException.ThrowIfNullOrEmpty(url);

        if (NSUrl.FromString(url) is not { } target || !UIApplication.SharedApplication.CanOpenUrl(target)) {
            return false;
        }

        UIApplication.SharedApplication.OpenUrl(target, new UIApplicationOpenUrlOptions(), null);
        return true;
    }

    /// <summary>Posts an event as though the system had raised it.</summary>
    /// <param name="platformEvent">The event.</param>
    /// <remarks>
    ///     For the delegate, which is where UIKit's callbacks arrive, and for a test that needs the
    ///     application to see a lifecycle transition it cannot cause.
    /// </remarks>
    public void Post(in PlatformEvent platformEvent) => events.Post(platformEvent);

    /// <inheritdoc />
    public void Dispose() {
        if (disposed) {
            return;
        }

        disposed = true;
        MobileLifecycle.Stopping();
        textInput.Dispose();
        window?.Dispose();
        windows.Clear();
        RootController = null;
    }

    /// <summary>Drops every finger, because the application is leaving the foreground.</summary>
    internal void ReleaseTouches() => window?.View.ReleaseAllTouches();
}

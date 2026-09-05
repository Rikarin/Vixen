// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using Vixen.Core;

namespace Vixen.Platform.Headless;

/// <summary>What to build a <see cref="HeadlessPlatform" /> out of.</summary>
public readonly record struct HeadlessPlatformOptions() {
    /// <summary>The publisher name used to place <c>/data</c> and <c>/cache</c>.</summary>
    public string Organisation { get; init; } = "Vixen";

    /// <summary>The application name used to place <c>/data</c> and <c>/cache</c>.</summary>
    public string Application { get; init; } = "Headless";

    /// <summary>
    ///     Where files live, or <see langword="null" /> for the OS's standard locations under
    ///     <see cref="Organisation" /> and <see cref="Application" />.
    /// </summary>
    /// <remarks>
    ///     Supplying one is how a test avoids writing into the developer's home directory, and how a
    ///     container image points <c>/data</c> at a mounted volume.
    /// </remarks>
    public IFileSystemHost? FileSystem { get; init; }
}

/// <summary>
///     The platform with nothing attached: no window server, no GPU, no audio device, no user.
/// </summary>
/// <remarks>
///     <para>
///         Two jobs, and they are the same job. It is the head a dedicated server and a batch tool
///         run on (<c>docs/plan/17</c>), and it is the platform every test that needs a platform
///         uses. Because the second happens on every build, the first cannot quietly rot — which is
///         the whole argument for building it in Phase 1 rather than discovering in Phase 9 that
///         four subsystems assume a window exists.
///     </para>
///     <para>
///         Windows still exist here, with sizes and events and focus; they just show nobody
///         anything, and their surfaces report <see cref="SurfaceKind.None" /> so a graphics backend
///         renders offscreen. The frame loop is the desktop's frame loop.
///     </para>
///     <para>
///         Owned by the thread that constructed it, like every other platform. Nothing here needs
///         that restriction, and it is kept anyway: code first written against the headless platform
///         and then run on a desktop should fail in the test suite rather than at the customer.
///     </para>
/// </remarks>
public sealed class HeadlessPlatform : IPlatform {
    readonly PlatformEventBuffer events = new();
    readonly List<IWindow> windows = [];
    readonly int ownerThreadId = Environment.CurrentManagedThreadId;

    uint nextWindowId = 1;
    bool disposed;

    /// <summary>Creates the platform.</summary>
        public HeadlessPlatform() : this(new HeadlessPlatformOptions()) { }

    /// <summary>Creates the platform.</summary>
    /// <param name="options">What to build it out of.</param>
    /// <remarks>
    ///     Two overloads rather than one with <c>= default</c>: a record struct's property
    ///     initialisers do not run for <c>default</c>.
    /// </remarks>
    public HeadlessPlatform(HeadlessPlatformOptions options) {
        var organisation = string.IsNullOrWhiteSpace(options.Organisation) ? "Vixen" : options.Organisation;
        var application = string.IsNullOrWhiteSpace(options.Application) ? "Headless" : options.Application;

        FileSystem = options.FileSystem ?? new StandardFileSystemHost(organisation, application);
        Lifecycle = new HeadlessLifecycle(events);
    }

    /// <inheritdoc />
    public string Name => "Headless";

    /// <summary>
    ///     <see cref="PlatformCapabilities.MultiWindow" />, and nothing else.
    /// </summary>
    /// <remarks>
    ///     Multi-window because windows here cost a few fields and there is no reason to limit them;
    ///     nothing else because there is genuinely nothing else. A subsystem that needs a capability
    ///     this platform lacks takes its fallback path, and that path being taken on every test run
    ///     is the point.
    /// </remarks>
    public PlatformCapabilities Capabilities => PlatformCapabilities.MultiWindow;

    /// <inheritdoc />
    public IReadOnlyList<IWindow> Windows => windows;

    /// <inheritdoc />
    public IDisplayInfo Displays { get; } = new HeadlessDisplays();

    /// <summary>The appearance a headless run reports, and the seam a test drives it through.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b><see cref="SystemColorScheme.Unknown" /> by default, because a headless run has
    ///         no operating-system appearance to report.</b> A test that wants one sets it, and
    ///         setting it queues a <see cref="PlatformEventKind.SystemColorSchemeChanged" /> exactly
    ///         as a desktop's poll would — so a host wired to the event is exercised by the same
    ///         code path the real platform takes, and a host wired to nothing fails the test rather
    ///         than passing it because the value happened to be readable.
    ///     </para>
    ///     <para>
    ///         Setting it to what it already is queues nothing. That is the desktop's rule too, and
    ///         it is what stops a test asserting on an event it caused by writing the same value
    ///         twice.
    ///     </para>
    /// </remarks>
    public SystemColorScheme ColorScheme {
        get;

        set {
            if (field == value) {
                return;
            }

            field = value;

            events.Post(
                PlatformEvent.Application(PlatformEventKind.SystemColorSchemeChanged, Stopwatch.GetTimestamp())
            );
        }
    }

    /// <inheritdoc />
    public IFileSystemHost FileSystem { get; }

    /// <inheritdoc />
    public IClipboard Clipboard { get; } = new HeadlessClipboard();

    /// <inheritdoc />
    public INativeDialogs Dialogs { get; } = new HeadlessDialogs();

    /// <inheritdoc />
    public ILifecycle Lifecycle { get; }

    /// <inheritdoc />
    public IInputSource Input { get; } = new HeadlessInputSource();

    /// <inheritdoc />
    public ITextInput TextInput { get; } = new HeadlessTextInput();

    /// <inheritdoc />
    public IPowerInfo Power { get; } = new HeadlessPowerInfo();

    /// <inheritdoc />
    public IProcessorTopology Processors { get; } = new HeadlessProcessorTopology();

    /// <summary>The lifecycle, typed so a test can suspend and resume it.</summary>
    public HeadlessLifecycle Simulation => (HeadlessLifecycle)Lifecycle;

    /// <summary>The input source, typed so a test can hold keys down.</summary>
    public HeadlessInputSource SimulatedInput => (HeadlessInputSource)Input;

    /// <inheritdoc />
    public IWindow CreateWindow(in WindowOptions options) {
        ThrowIfWrongThread();
        ObjectDisposedException.ThrowIf(disposed, this);

        var window = new HeadlessWindow(nextWindowId++, options, events);
        windows.Add(window);

        if (options.IsVisible) {
            events.Post(PlatformEvent.Window(PlatformEventKind.WindowShown, window.Id, Stopwatch.GetTimestamp()));
        }

        return window;
    }

    /// <inheritdoc />
    public bool TryGetWindow(uint id, [NotNullWhen(true)] out IWindow? window) {
        foreach (var candidate in windows) {
            if (candidate.Id == id) {
                window = candidate;
                return true;
            }
        }

        window = null;
        return false;
    }

    /// <inheritdoc />
    public ReadOnlySpan<PlatformEvent> PumpEvents() {
        ThrowIfWrongThread();
        ObjectDisposedException.ThrowIf(disposed, this);

        // Windows that were disposed since the last pump stop being windows. Done here rather than
        // in Dispose so that an application enumerating Windows inside its event handling sees a
        // list that does not change under it.
        windows.RemoveAll(window => window.IsClosed);

        return events.Drain();
    }

    /// <summary>Adds an event to the queue, as the operating system would.</summary>
    /// <param name="platformEvent">The event.</param>
    /// <returns><see langword="false" /> if the buffer was full and it was dropped.</returns>
    /// <remarks>
    ///     The seam that makes this platform a test rig: a recorded input trace replayed through
    ///     here drives the engine exactly as a keyboard would, deterministically and without one.
    /// </remarks>
    public bool Post(in PlatformEvent platformEvent) => events.Post(platformEvent);

    /// <summary>Always <see langword="false" />: there is no shell to hand a URL to.</summary>
    /// <param name="url">Ignored.</param>
    /// <returns><see langword="false" />.</returns>
    public bool TryOpenUrl(string url) => false;

    /// <inheritdoc />
    public void Dispose() {
        if (disposed) {
            return;
        }

        disposed = true;
        Simulation.Stopping();

        foreach (var window in windows) {
            window.Dispose();
        }

        windows.Clear();
        events.Clear();
    }

    void ThrowIfWrongThread() {
        if (Environment.CurrentManagedThreadId != ownerThreadId) {
            throw new InvalidOperationException(
                $"The platform is owned by thread {ownerThreadId} and was used from "
                + $"{Environment.CurrentManagedThreadId}. Every platform has this restriction — see IPlatform."
            );
        }
    }
}

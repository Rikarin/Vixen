// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Text;
using Silk.NET.SDL;
using Vixen.Core.Mathematics;
using SdlWindow = Silk.NET.SDL.Window;

namespace Vixen.Platform.Desktop;

/// <summary>What to build a <see cref="DesktopPlatform" /> out of.</summary>
public readonly record struct DesktopPlatformOptions() {
    /// <summary>The publisher name used to place <c>/data</c> and <c>/cache</c>.</summary>
    public string Organisation { get; init; } = "Vixen";

    /// <summary>The application name used to place <c>/data</c> and <c>/cache</c>, and the name SDL
    /// gives the OS for its window class and audio streams.</summary>
    public string Application { get; init; } = "Vixen";

    /// <summary>Where files live, or <see langword="null" /> for the OS's standard locations.</summary>
    public IFileSystemHost? FileSystem { get; init; }

    /// <summary>
    ///     Which SDL video driver to insist on — <c>"x11"</c>, <c>"wayland"</c>, <c>"dummy"</c> — or
    ///     <see langword="null" /> to let SDL choose.
    /// </summary>
    /// <remarks>
    ///     Three real uses. <c>"dummy"</c> is a working SDL video driver that creates windows nobody
    ///     can see, which is what makes windowing testable on a CI runner with no display and on
    ///     macOS, where AppKit refuses to be touched from anywhere but the process's main thread and
    ///     a test runner is never on it. <c>"x11"</c> is the escape hatch for a user whose Wayland
    ///     compositor misbehaves. And a dedicated server that wants a window abstraction without a
    ///     display server wants this rather than a second code path.
    /// </remarks>
    public string? VideoDriver { get; init; }

    /// <summary>
    ///     Whether windows should be created ready for a GPU swapchain.
    /// </summary>
    /// <remarks>
    ///     On by default, and it has to be decided before the window exists: SDL cannot add the
    ///     capability afterwards, so a window created without it must be destroyed and recreated to
    ///     get one. Leaving it on also means a machine with no Vulkan fails at window creation with
    ///     SDL saying exactly that, rather than succeeding and failing later somewhere less
    ///     informative.
    ///
    ///     Turn it off for a head that will never draw — a content-build tool, or a test — and for
    ///     the <c>dummy</c> video driver, which has no Vulkan at all.
    /// </remarks>
    public bool RequestGpuSurface { get; init; } = true;

    /// <summary>Whether to initialise the game-controller subsystem.</summary>
    /// <remarks>
    ///     On by default. Turning it off is worth it for a tool that will never read a gamepad:
    ///     enumerating controllers touches every HID device on the machine, which on Windows can add
    ///     visible time to startup and on Linux needs permissions a headless build may not have.
    /// </remarks>
    public bool EnableGameControllers { get; init; } = true;
}

/// <summary>Windows, Linux and macOS, through one SDL implementation.</summary>
/// <remarks>
///     <para>
///         One implementation for all three, because SDL already absorbed the differences that
///         matter for windowing, input, gamepads, clipboard text and IME. What it does not cover —
///         file dialogs, per-OS window chrome, thread affinity — is left visibly missing here rather
///         than approximated, and belongs to the per-OS assemblies <c>docs/plan/02</c> reserves.
///     </para>
///     <para>
///         Owned by the thread that constructed it. That is SDL's rule and the operating systems'
///         beneath it, not a simplification of ours.
///     </para>
/// </remarks>
public sealed unsafe class DesktopPlatform : IPlatform {
    readonly Sdl sdl;
    readonly PlatformEventBuffer events = new();
    readonly List<IWindow> windows = [];
    readonly Dictionary<uint, DesktopWindow> byId = [];
    readonly int ownerThreadId = Environment.CurrentManagedThreadId;
    readonly long startTimestamp;
    readonly uint startTicks;
    readonly bool requestGpuSurface;

    bool disposed;

    /// <summary>Starts SDL and builds the platform.</summary>
    /// <param name="options">What to build it out of.</param>
    /// <exception cref="PlatformNotSupportedException">SDL is not installed, or refused to
    /// start.</exception>
    public DesktopPlatform(DesktopPlatformOptions options = default) {
        var organisation = string.IsNullOrWhiteSpace(options.Organisation) ? "Vixen" : options.Organisation;
        var application = string.IsNullOrWhiteSpace(options.Application) ? "Vixen" : options.Application;

        sdl = SdlLibrary.Load();

        // Told before init, because SDL reads it while creating the window class and the audio
        // client and never looks again.
        sdl.SetHint(Sdl.HintAppName, application);

        // Off by default in SDL, and wanted: without it a window is created at a size in physical
        // pixels rather than points, so a 1280×720 window is half the intended size on a 2× display.
        sdl.SetHint(Sdl.HintWindowsDpiAwareness, "permonitorv2");

        if (!string.IsNullOrWhiteSpace(options.VideoDriver)) {
            // The hint rather than the SDL_VIDEODRIVER environment variable, because .NET on Unix
            // keeps its own copy of the environment and does not push a change back to the native
            // one — so setting the variable from managed code is invisible to SDL's getenv.
            sdl.SetHint(Sdl.HintVideodriver, options.VideoDriver);
        }

        var subsystems = Sdl.InitVideo | Sdl.InitEvents;

        if (options.EnableGameControllers) {
            subsystems |= Sdl.InitGamecontroller;
        }

        if (sdl.Init(subsystems) != 0) {
            throw new PlatformNotSupportedException($"SDL could not start: {sdl.GetErrorS()}");
        }

        requestGpuSurface = options.RequestGpuSurface;
        startTimestamp = Stopwatch.GetTimestamp();
        startTicks = sdl.GetTicks();

        FileSystem = options.FileSystem ?? new StandardFileSystemHost(organisation, application);
        Displays = new DesktopDisplays(sdl);
        Clipboard = new DesktopClipboard(sdl);
        Dialogs = new DesktopDialogs(sdl);
        Lifecycle = new DesktopLifecycle(events);
        Input = new DesktopInputSource(sdl);
        TextInput = new DesktopTextInput(sdl);
        Power = new DesktopPowerInfo(sdl);
        Processors = new DesktopProcessorTopology(sdl);

        Capabilities = DetectCapabilities(options.EnableGameControllers);
    }

    /// <inheritdoc />
    public string Name { get; } = $"Desktop ({(OperatingSystem.IsWindows() ? "Windows"
        : OperatingSystem.IsMacOS() ? "macOS" : "Linux")})";

    /// <summary>
    ///     What SDL can do here, decided at construction from what it reports rather than from the
    ///     operating system's name.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         <see cref="PlatformCapabilities.WindowPositioning" /> is the one that varies within a
    ///         single OS: Wayland deliberately refuses to tell a client where it is or let it choose,
    ///         and the same Linux binary running under X11 can. Asking SDL which video driver it
    ///         chose is the only way to know.
    ///     </para>
    ///     <para>
    ///         <see cref="PlatformCapabilities.NativeDialogs" /> is <em>not</em> reported, because
    ///         SDL 2 has no file picker and the capability covers pickers and message boxes
    ///         together. Message boxes work regardless — <see cref="INativeDialogs.ShowMessageAsync" />
    ///         is safe to call — and the flag turns on when a per-OS assembly supplies the pickers.
    ///     </para>
    /// </remarks>
    public PlatformCapabilities Capabilities { get; }

    /// <inheritdoc />
    public IReadOnlyList<IWindow> Windows => windows;

    /// <inheritdoc />
    public IDisplayInfo Displays { get; }

    /// <inheritdoc />
    public IFileSystemHost FileSystem { get; }

    /// <inheritdoc />
    public IClipboard Clipboard { get; }

    /// <inheritdoc />
    public INativeDialogs Dialogs { get; }

    /// <inheritdoc />
    public ILifecycle Lifecycle { get; }

    /// <inheritdoc />
    public IInputSource Input { get; }

    /// <inheritdoc />
    public ITextInput TextInput { get; }

    /// <inheritdoc />
    public IPowerInfo Power { get; }

    /// <inheritdoc />
    public IProcessorTopology Processors { get; }

    /// <summary>Which video driver SDL chose: <c>"windows"</c>, <c>"cocoa"</c>, <c>"wayland"</c>,
    /// <c>"x11"</c>.</summary>
    /// <remarks>
    ///     Worth logging at boot. Half of the Linux bug reports an engine receives are answered by
    ///     this one string, and the difference between Wayland and X11 changes what the platform can
    ///     do — see <see cref="Capabilities" />.
    /// </remarks>
    public string VideoDriver => sdl.GetCurrentVideoDriverS() ?? "unknown";

    /// <inheritdoc />
    public IWindow CreateWindow(in WindowOptions options) {
        ThrowIfWrongThread();
        ObjectDisposedException.ThrowIf(disposed, this);

        var flags = (uint)WindowFlags.AllowHighdpi;

        if (options.IsResizable) {
            flags |= (uint)WindowFlags.Resizable;
        }

        if (!options.IsDecorated) {
            flags |= (uint)WindowFlags.Borderless;
        }

        if (!options.IsVisible) {
            flags |= (uint)WindowFlags.Hidden;
        }

        if (options.IsAlwaysOnTop) {
            flags |= (uint)WindowFlags.AlwaysOnTop;
        }

        flags |= options.Mode switch {
            WindowMode.Maximised => (uint)WindowFlags.Maximized,
            WindowMode.BorderlessFullscreen => (uint)WindowFlags.FullscreenDesktop,
            WindowMode.ExclusiveFullscreen => (uint)WindowFlags.Fullscreen,
            _ => 0u
        };

        if (requestGpuSurface) {
            // Requested up front because SDL cannot add it afterwards: the flag decides whether the
            // window gets a surface a Vulkan swapchain can be built on, and a window created without
            // it has to be destroyed and recreated to get one.
            flags |= (uint)WindowFlags.Vulkan;
        }

        var position = options.Position;
        var x = position?.X ?? PositionFor(options.DisplayIndex);
        var y = position?.Y ?? PositionFor(options.DisplayIndex);

        SdlWindow* handle;

        fixed (byte* title = Encoding.UTF8.GetBytes((options.Title ?? "Vixen") + '\0')) {
            handle = sdl.CreateWindow(title, x, y, options.Size.X, options.Size.Y, flags);
        }

        if (handle is null) {
            throw new PlatformNotSupportedException($"SDL could not create a window: {sdl.GetErrorS()}");
        }

        var window = new DesktopWindow(sdl, handle);

        if (options.MinimumSize is { } minimum) {
            sdl.SetWindowMinimumSize(handle, minimum.X, minimum.Y);
        }

        if (options.MaximumSize is { } maximum) {
            sdl.SetWindowMaximumSize(handle, maximum.X, maximum.Y);
        }

        windows.Add(window);
        byId.Add(window.Id, window);
        return window;
    }

    /// <inheritdoc />
    public bool TryGetWindow(uint id, [NotNullWhen(true)] out IWindow? window) {
        if (byId.TryGetValue(id, out var found) && !found.IsClosed) {
            window = found;
            return true;
        }

        window = null;
        return false;
    }

    /// <inheritdoc />
    public ReadOnlySpan<PlatformEvent> PumpEvents() {
        ThrowIfWrongThread();
        ObjectDisposedException.ThrowIf(disposed, this);

        Forget();

        Event sdlEvent = default;

        while (sdl.PollEvent(&sdlEvent) != 0) {
            Translate(&sdlEvent);
        }

        return events.Drain();
    }

    /// <inheritdoc />
    public bool TryOpenUrl(string url) {
        ArgumentException.ThrowIfNullOrWhiteSpace(url);
        return sdl.OpenURL(url) == 0;
    }

    /// <inheritdoc />
    public void Dispose() {
        if (disposed) {
            return;
        }

        disposed = true;
        ((DesktopLifecycle)Lifecycle).Stopping();
        ((DesktopInputSource)Input).CloseAll();

        foreach (var window in windows) {
            window.Dispose();
        }

        windows.Clear();
        byId.Clear();
        events.Clear();
        sdl.Quit();
    }

    static int PositionFor(int? display) =>
        display is { } index ? (int)(Sdl.WindowposCenteredMask | (uint)index) : Sdl.WindowposCentered;

    PlatformCapabilities DetectCapabilities(bool controllers) {
        var capabilities = PlatformCapabilities.Windowing
            | PlatformCapabilities.MultiWindow
            | PlatformCapabilities.DisplayEnumeration
            | PlatformCapabilities.Cursor
            | PlatformCapabilities.Clipboard
            | PlatformCapabilities.TextInput
            | PlatformCapabilities.PowerInfo
            | PlatformCapabilities.DragAndDrop;

        if (!string.Equals(VideoDriver, "wayland", StringComparison.Ordinal)) {
            capabilities |= PlatformCapabilities.WindowPositioning;
        }

        if (controllers) {
            capabilities |= PlatformCapabilities.GameControllers | PlatformCapabilities.Haptics;
        }

        return capabilities;
    }

    /// <summary>
    ///     Drops windows the application disposed since the last pump.
    /// </summary>
    /// <remarks>
    ///     Done here rather than in <see cref="DesktopWindow.Dispose" /> so that the list does not
    ///     change while the application is walking it inside its own event handling.
    /// </remarks>
    void Forget() {
        for (var index = windows.Count - 1; index >= 0; index--) {
            if (windows[index].IsClosed) {
                byId.Remove(windows[index].Id);
                windows.RemoveAt(index);
            }
        }
    }

    /// <summary>
    ///     SDL's millisecond clock, expressed in the <see cref="Stopwatch" /> ticks the rest of the
    ///     engine measures in.
    /// </summary>
    /// <remarks>
    ///     Anchored at startup rather than read fresh at translation time, so an event keeps the
    ///     time the OS says it happened rather than the time this loop got round to it. That
    ///     difference is the whole of input latency, and measuring it needs the original number.
    /// </remarks>
    long TimestampOf(uint sdlMilliseconds) =>
        startTimestamp + ((long)(sdlMilliseconds - startTicks) * Stopwatch.Frequency / 1000);

    void Translate(Event* sdlEvent) {
        var kind = (EventType)sdlEvent->Type;

        switch (kind) {
            case EventType.Windowevent:
                TranslateWindow(sdlEvent);
                break;

            case EventType.Keydown or EventType.Keyup:
                events.Post(
                    PlatformEvent.Keyboard(
                        kind == EventType.Keydown ? PlatformEventKind.KeyDown : PlatformEventKind.KeyUp,
                        sdlEvent->Key.WindowID,
                        TimestampOf(sdlEvent->Key.Timestamp),
                        SdlTranslation.ToKey(sdlEvent->Key.Keysym.Scancode),
                        SdlTranslation.ToModifiers((Keymod)sdlEvent->Key.Keysym.Mod),
                        sdlEvent->Key.Repeat != 0
                    )
                );

                break;

            case EventType.Textinput:
                events.Post(
                    PlatformEvent.TextInput(
                        sdlEvent->Text.WindowID,
                        TimestampOf(sdlEvent->Text.Timestamp),
                        ReadFixed(sdlEvent->Text.Text, 32)
                    )
                );

                break;

            case EventType.Textediting:
                events.Post(
                    PlatformEvent.TextEditing(
                        sdlEvent->Edit.WindowID,
                        TimestampOf(sdlEvent->Edit.Timestamp),
                        ReadFixed(sdlEvent->Edit.Text, 32),
                        sdlEvent->Edit.Start,
                        sdlEvent->Edit.Length
                    )
                );

                break;

            case EventType.Mousemotion:
                events.Post(
                    PlatformEvent.MouseMoved(
                        sdlEvent->Motion.WindowID,
                        TimestampOf(sdlEvent->Motion.Timestamp),
                        new(sdlEvent->Motion.X, sdlEvent->Motion.Y),
                        new(sdlEvent->Motion.Xrel, sdlEvent->Motion.Yrel),
                        Modifiers
                    )
                );

                break;

            case EventType.Mousebuttondown or EventType.Mousebuttonup:
                events.Post(
                    PlatformEvent.MouseButtonChanged(
                        kind == EventType.Mousebuttondown
                            ? PlatformEventKind.MouseButtonDown
                            : PlatformEventKind.MouseButtonUp,
                        sdlEvent->Button.WindowID,
                        TimestampOf(sdlEvent->Button.Timestamp),
                        SdlTranslation.ToMouseButton(sdlEvent->Button.Button),
                        new(sdlEvent->Button.X, sdlEvent->Button.Y),
                        Math.Max(1, (int)sdlEvent->Button.Clicks),
                        Modifiers
                    )
                );

                break;

            case EventType.Mousewheel: {
                // A "flipped" wheel event is SDL reporting the platform's natural-scrolling setting
                // rather than the raw device, and the sign has to be undone here — otherwise a Mac
                // with natural scrolling scrolls the wrong way in our windows and the right way in
                // everybody else's.
                var direction = sdlEvent->Wheel.Direction == (uint)MouseWheelDirection.Flipped ? -1f : 1f;

                events.Post(
                    PlatformEvent.MouseWheel(
                        sdlEvent->Wheel.WindowID,
                        TimestampOf(sdlEvent->Wheel.Timestamp),
                        new(sdlEvent->Wheel.MouseX, sdlEvent->Wheel.MouseY),
                        new(sdlEvent->Wheel.PreciseX * direction, sdlEvent->Wheel.PreciseY * direction),
                        Modifiers
                    )
                );

                break;
            }

            case EventType.Fingerdown or EventType.Fingerup or EventType.Fingermotion: {
                var window = FindWindow(sdlEvent->Tfinger.WindowID);
                var size = window?.ClientSize ?? Int2.Zero;

                events.Post(
                    PlatformEvent.Touch(
                        kind switch {
                            EventType.Fingerdown => PlatformEventKind.TouchDown,
                            EventType.Fingerup => PlatformEventKind.TouchUp,
                            _ => PlatformEventKind.TouchMoved
                        },
                        sdlEvent->Tfinger.WindowID,
                        TimestampOf(sdlEvent->Tfinger.Timestamp),
                        // SDL's finger ids are 64-bit and ours are int-sized, because a finger id
                        // only has to be unique among the ten that can be down at once.
                        unchecked((int)sdlEvent->Tfinger.FingerId),
                        // Reported normalised, which is not what anything downstream wants.
                        new(sdlEvent->Tfinger.X * size.X, sdlEvent->Tfinger.Y * size.Y),
                        new(sdlEvent->Tfinger.Dx * size.X, sdlEvent->Tfinger.Dy * size.Y),
                        sdlEvent->Tfinger.Pressure
                    )
                );

                break;
            }

            case EventType.Controllerdeviceadded:
                ((DesktopInputSource)Input).Add(sdlEvent->Cdevice.Which);

                events.Post(
                    PlatformEvent.GamepadConnection(
                        PlatformEventKind.GamepadConnected,
                        TimestampOf(sdlEvent->Cdevice.Timestamp),
                        sdlEvent->Cdevice.Which
                    )
                );

                break;

            case EventType.Controllerdeviceremoved:
                events.Post(
                    PlatformEvent.GamepadConnection(
                        PlatformEventKind.GamepadDisconnected,
                        TimestampOf(sdlEvent->Cdevice.Timestamp),
                        sdlEvent->Cdevice.Which
                    )
                );

                // Removed after the event, so a handler reading Gamepads inside it still sees the
                // device it is being told about.
                ((DesktopInputSource)Input).Remove(sdlEvent->Cdevice.Which);
                break;

            case EventType.Controllerbuttondown or EventType.Controllerbuttonup:
                events.Post(
                    PlatformEvent.GamepadButtonChanged(
                        kind == EventType.Controllerbuttondown
                            ? PlatformEventKind.GamepadButtonDown
                            : PlatformEventKind.GamepadButtonUp,
                        TimestampOf(sdlEvent->Cbutton.Timestamp),
                        sdlEvent->Cbutton.Which,
                        SdlTranslation.ToGamepadButton((GameControllerButton)sdlEvent->Cbutton.Button)
                    )
                );

                break;

            case EventType.Controlleraxismotion: {
                var axis = SdlTranslation.ToGamepadAxis((GameControllerAxis)sdlEvent->Caxis.Axis);

                events.Post(
                    PlatformEvent.GamepadAxisMoved(
                        TimestampOf(sdlEvent->Caxis.Timestamp),
                        sdlEvent->Caxis.Which,
                        axis,
                        SdlTranslation.ToAxisValue(axis, sdlEvent->Caxis.Value)
                    )
                );

                break;
            }

            case EventType.Displayevent:
                events.Post(
                    PlatformEvent.Application(
                        PlatformEventKind.DisplaysChanged,
                        TimestampOf(sdlEvent->Display.Timestamp)
                    )
                );

                break;

            case EventType.Dropfile or EventType.Droptext: {
                // SDL allocated this string and hands ownership over; not freeing it leaks one path
                // per dropped file, which is small and permanent.
                var text = sdlEvent->Drop.File is null ? string.Empty : ReadUtf8(sdlEvent->Drop.File);

                if (sdlEvent->Drop.File is not null) {
                    sdl.Free(sdlEvent->Drop.File);
                }

                events.Post(
                    PlatformEvent.Drop(
                        kind == EventType.Dropfile ? PlatformEventKind.DropFile : PlatformEventKind.DropText,
                        sdlEvent->Drop.WindowID,
                        TimestampOf(sdlEvent->Drop.Timestamp),
                        text,
                        Vector2.Zero
                    )
                );

                break;
            }

            case EventType.Quit:
                ((DesktopLifecycle)Lifecycle).QuitReceived(TimestampOf(sdlEvent->Quit.Timestamp));
                break;

            default:
                break;
        }
    }

    void TranslateWindow(Event* sdlEvent) {
        var id = sdlEvent->Window.WindowID;
        var timestamp = TimestampOf(sdlEvent->Window.Timestamp);
        var lifecycle = (DesktopLifecycle)Lifecycle;

        switch ((WindowEventID)sdlEvent->Window.Event) {
            case WindowEventID.Shown:
                events.Post(PlatformEvent.Window(PlatformEventKind.WindowShown, id, timestamp));
                break;

            case WindowEventID.Hidden:
                events.Post(PlatformEvent.Window(PlatformEventKind.WindowHidden, id, timestamp));
                break;

            case WindowEventID.Moved:
                events.Post(
                    PlatformEvent.WindowMoved(id, timestamp, new(sdlEvent->Window.Data1, sdlEvent->Window.Data2))
                );

                break;

            // SizeChanged rather than Resized: SDL sends this one for every size change and Resized
            // only for changes the user made, and it sends both for those. Handling this one alone
            // catches a programmatic resize and does not report a user's twice.
            case WindowEventID.SizeChanged: {
                var window = FindWindow(id);

                events.Post(
                    PlatformEvent.WindowResized(
                        id,
                        timestamp,
                        new(sdlEvent->Window.Data1, sdlEvent->Window.Data2),
                        window?.FramebufferSize ?? new(sdlEvent->Window.Data1, sdlEvent->Window.Data2)
                    )
                );

                break;
            }

            case WindowEventID.Minimized:
                events.Post(PlatformEvent.Window(PlatformEventKind.WindowMinimised, id, timestamp));
                break;

            case WindowEventID.Maximized:
                events.Post(PlatformEvent.Window(PlatformEventKind.WindowMaximised, id, timestamp));
                break;

            case WindowEventID.Restored:
                events.Post(PlatformEvent.Window(PlatformEventKind.WindowRestored, id, timestamp));
                break;

            case WindowEventID.Enter:
                events.Post(PlatformEvent.Window(PlatformEventKind.WindowMouseEntered, id, timestamp));
                break;

            case WindowEventID.Leave:
                events.Post(PlatformEvent.Window(PlatformEventKind.WindowMouseLeft, id, timestamp));
                break;

            case WindowEventID.FocusGained:
                lifecycle.SetForeground(true);
                events.Post(PlatformEvent.Window(PlatformEventKind.WindowFocusGained, id, timestamp));
                break;

            case WindowEventID.FocusLost:
                lifecycle.SetForeground(false);
                events.Post(PlatformEvent.Window(PlatformEventKind.WindowFocusLost, id, timestamp));
                break;

            case WindowEventID.Close:
                events.Post(PlatformEvent.Window(PlatformEventKind.WindowCloseRequested, id, timestamp));
                break;

            case WindowEventID.DisplayChanged:
                events.Post(
                    PlatformEvent.WindowDpiChanged(id, timestamp, FindWindow(id)?.DpiScale ?? 1f)
                );

                break;

            default:
                break;
        }
    }

    KeyModifiers Modifiers => SdlTranslation.ToModifiers(sdl.GetModState());

    DesktopWindow? FindWindow(uint id) =>
        byId.TryGetValue(id, out var window) && !window.IsClosed ? window : null;

    static string ReadUtf8(byte* value) {
        var length = 0;

        while (value[length] != 0) {
            length++;
        }

        return Encoding.UTF8.GetString(value, length);
    }

    /// <summary>
    ///     Reads one of SDL's fixed inline UTF-8 buffers, which are null-terminated within a fixed
    ///     size and may use every byte of it.
    /// </summary>
    static string ReadFixed(byte* buffer, int capacity) {
        var span = new ReadOnlySpan<byte>(buffer, capacity);
        var end = span.IndexOf((byte)0);
        return Encoding.UTF8.GetString(end < 0 ? span : span[..end]);
    }

    void ThrowIfWrongThread() {
        if (Environment.CurrentManagedThreadId != ownerThreadId) {
            throw new InvalidOperationException(
                $"The platform is owned by thread {ownerThreadId} and was used from "
                + $"{Environment.CurrentManagedThreadId}. SDL delivers window messages to the thread that "
                + "created the window, and AppKit refuses to touch one from anywhere else."
            );
        }
    }
}

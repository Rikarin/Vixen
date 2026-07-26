// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Diagnostics.CodeAnalysis;
using Silk.NET.SDL;
using Vixen.Core.Mathematics;
using Rectangle = Vixen.Core.Mathematics.Rectangle;
using SdlPowerState = Silk.NET.SDL.PowerState;

namespace Vixen.Platform.Desktop;

/// <summary>The displays, as SDL sees them.</summary>
/// <remarks>
///     Re-read on every access rather than cached, because a display list goes stale for reasons
///     nobody controls — a dock, a lid, a resolution change — and a cache would need invalidating on
///     an event that some platforms do not send. Enumerating three displays costs a few microseconds
///     and nothing does it per frame.
/// </remarks>
public sealed unsafe class DesktopDisplays(Sdl sdl) : IDisplayInfo {
    /// <inheritdoc />
    public IReadOnlyList<DisplayInfo> Displays {
        get {
            var count = sdl.GetNumVideoDisplays();

            if (count <= 0) {
                return [];
            }

            var displays = new List<DisplayInfo>(count);

            for (var index = 0; index < count; index++) {
                if (Describe(index) is { } display) {
                    displays.Add(display);
                }
            }

            return displays;
        }
    }

    /// <summary>Display <c>0</c>, which is the one SDL orders first and the OS calls primary.</summary>
    public DisplayInfo? Primary => sdl.GetNumVideoDisplays() > 0 ? Describe(0) : null;

    /// <inheritdoc />
    public bool TryGetForWindow(IWindow window, [NotNullWhen(true)] out DisplayInfo? display) {
        ArgumentNullException.ThrowIfNull(window);

        display = window is DesktopWindow desktop && !desktop.IsClosed
            ? Describe(sdl.GetWindowDisplayIndex(desktop.Handle))
            : null;

        return display is not null;
    }

    /// <inheritdoc />
    public bool TryGetForPoint(Int2 point, [NotNullWhen(true)] out DisplayInfo? display) {
        foreach (var candidate in Displays) {
            if (candidate.Bounds.Contains(point)) {
                display = candidate;
                return true;
            }
        }

        display = null;
        return false;
    }

    DisplayInfo? Describe(int index) {
        if (index < 0 || index >= sdl.GetNumVideoDisplays()) {
            return null;
        }

        Silk.NET.Maths.Rectangle<int> bounds = default;
        Silk.NET.Maths.Rectangle<int> usable = default;

        if (sdl.GetDisplayBounds(index, &bounds) != 0) {
            return null;
        }

        if (sdl.GetDisplayUsableBounds(index, &usable) != 0) {
            usable = bounds;
        }

        Silk.NET.SDL.DisplayMode current = default;

        if (sdl.GetCurrentDisplayMode(index, &current) != 0) {
            return null;
        }

        var modes = new List<DisplayMode>();
        var modeCount = sdl.GetNumDisplayModes(index);

        for (var mode = 0; mode < modeCount; mode++) {
            Silk.NET.SDL.DisplayMode described = default;

            if (sdl.GetDisplayMode(index, mode, &described) == 0) {
                modes.Add(Convert(described));
            }
        }

        return new(
            index,
            sdl.GetDisplayNameS(index) ?? $"Display {index}",
            new(bounds.Origin.X, bounds.Origin.Y, bounds.Size.X, bounds.Size.Y),
            new(usable.Origin.X, usable.Origin.Y, usable.Size.X, usable.Size.Y),
            ScaleOf(index),
            Convert(current),
            modes,
            index == 0
        );
    }

    /// <summary>
    ///     The display's scale factor, as far as SDL 2 can be made to say.
    /// </summary>
    /// <remarks>
    ///     <c>SDL_GetDisplayDPI</c> reports the panel's advertised dots per inch, which is a
    ///     different number from the scale the OS applies, and on Linux it is often whatever the
    ///     monitor's EDID claimed — which is regularly wrong and occasionally zero. Dividing by 96
    ///     is the conventional reading of it, and it is clamped to a plausible range so a bad EDID
    ///     produces 1 rather than 37. <see cref="IWindow.DpiScale" /> is the authoritative number
    ///     for anything that renders, because it is derived from the two sizes SDL reports for a
    ///     real window rather than from what the panel claims about itself.
    /// </remarks>
    float ScaleOf(int index) {
        float diagonal;

        if (sdl.GetDisplayDPI(index, &diagonal, null, null) != 0 || diagonal <= 0f) {
            return 1f;
        }

        return Math.Clamp(diagonal / 96f, 1f, 4f);
    }

    /// <summary>
    ///     SDL 2 has no HDR query at all, so <see cref="DisplayMode.IsHdr" /> is always
    ///     <see langword="false" /> here rather than guessed at from the pixel format.
    /// </summary>
    static DisplayMode Convert(Silk.NET.SDL.DisplayMode mode) =>
        new(new(mode.W, mode.H), mode.RefreshRate, false);
}

/// <summary>The system clipboard, which SDL 2 supports for text and nothing else.</summary>
/// <remarks>
///     Images and application-defined formats need the platform's own clipboard API — Win32 registered
///     formats, <c>NSPasteboard</c> UTIs, X11 atoms — and belong to the per-OS assemblies
///     <c>docs/plan/02</c> reserves for exactly this kind of gap. Until those exist the methods
///     report that they did nothing, which is the truth and is a case every caller already handles.
/// </remarks>
public sealed class DesktopClipboard(Sdl sdl) : IClipboard {
    /// <inheritdoc />
    public bool HasText => sdl.HasClipboardText() == SdlBool.True;

    /// <summary>Always <see langword="false" />: SDL 2 cannot read images from the clipboard.</summary>
    public bool HasImage => false;

    /// <inheritdoc />
    public bool TryGetText([NotNullWhen(true)] out string? text) {
        if (!HasText) {
            text = null;
            return false;
        }

        text = sdl.GetClipboardTextS();
        return !string.IsNullOrEmpty(text);
    }

    /// <inheritdoc />
    public bool SetText(string text) => sdl.SetClipboardText(text ?? string.Empty) == 0;

    /// <summary>Always <see langword="false" />: SDL 2 cannot read images from the clipboard.</summary>
    public bool TryGetImage(out ClipboardImage image) {
        image = default;
        return false;
    }

    /// <summary>Always <see langword="false" />: SDL 2 cannot write images to the clipboard.</summary>
    public bool SetImage(in ClipboardImage image) => false;

    /// <summary>Always <see langword="false" />: SDL 2 has no custom clipboard formats.</summary>
    public bool TryGetData(string format, out ReadOnlyMemory<byte> data) {
        data = default;
        return false;
    }

    /// <summary>Always <see langword="false" />: SDL 2 has no custom clipboard formats.</summary>
    public bool SetData(string format, ReadOnlySpan<byte> data) => false;

    /// <inheritdoc />
    public void Clear() => sdl.SetClipboardText(string.Empty);
}

/// <summary>Message boxes, which SDL has, and file pickers, which it does not.</summary>
/// <remarks>
///     <para>
///         SDL 2 ships no file dialog — SDL 3 added one, and the version of Silk.NET this repository
///         pins binds SDL 2 (see the README). A picker is also the one dialog that most needs to be
///         the platform's own, since it carries the user's places, tags, cloud providers and, inside
///         a sandbox, the permission to read what was picked. Writing a drawn substitute here would
///         be strictly worse than the per-OS implementations <c>docs/plan/02</c> already reserves a
///         project for, so the methods return "nothing chosen" until those exist.
///     </para>
///     <para>
///         Message boxes are implemented properly, including the button sets, because SDL's are the
///         OS's own and they are what a fatal-error path needs before there is a renderer to draw
///         with.
///     </para>
/// </remarks>
public sealed unsafe class DesktopDialogs(Sdl sdl) : INativeDialogs {
    /// <summary>Not implemented on SDL 2. Returns <see langword="null" />.</summary>
    public ValueTask<string?> OpenFileAsync(
        FileDialogOptions options,
        IWindow? owner = null,
        CancellationToken cancellationToken = default
    ) =>
        ValueTask.FromResult<string?>(null);

    /// <summary>Not implemented on SDL 2. Returns an empty list.</summary>
    public ValueTask<IReadOnlyList<string>> OpenFilesAsync(
        FileDialogOptions options,
        IWindow? owner = null,
        CancellationToken cancellationToken = default
    ) =>
        ValueTask.FromResult<IReadOnlyList<string>>([]);

    /// <summary>Not implemented on SDL 2. Returns <see langword="null" />.</summary>
    public ValueTask<string?> SaveFileAsync(
        FileDialogOptions options,
        IWindow? owner = null,
        CancellationToken cancellationToken = default
    ) =>
        ValueTask.FromResult<string?>(null);

    /// <summary>Not implemented on SDL 2. Returns <see langword="null" />.</summary>
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
    ) {
        if (cancellationToken.IsCancellationRequested) {
            return ValueTask.FromResult(MessageBoxResult.None);
        }

        var window = owner is DesktopWindow desktop && !desktop.IsClosed ? desktop.Handle : null;
        var buttons = ButtonsFor(options.Buttons);
        var results = ResultsFor(options.Buttons);

        var titleBytes = System.Text.Encoding.UTF8.GetBytes(options.Title + '\0');
        var messageBytes = System.Text.Encoding.UTF8.GetBytes(options.Message + '\0');
        var labelBytes = buttons.Select(label => System.Text.Encoding.UTF8.GetBytes(label + '\0')).ToArray();

        var handles = new MessageBoxButtonData[buttons.Length];
        var pinned = new System.Runtime.InteropServices.GCHandle[labelBytes.Length];

        try {
            for (var index = 0; index < buttons.Length; index++) {
                pinned[index] = System.Runtime.InteropServices.GCHandle.Alloc(
                    labelBytes[index],
                    System.Runtime.InteropServices.GCHandleType.Pinned
                );

                handles[index] = new() {
                    Buttonid = index,
                    Text = (byte*)pinned[index].AddrOfPinnedObject(),

                    // The first button is what Return activates and the last is what Escape does,
                    // which is the convention on all three desktops and is not SDL's default.
                    Flags = index == 0
                        ? (uint)MessageBoxButtonFlags.ReturnkeyDefault
                        : index == buttons.Length - 1
                            ? (uint)MessageBoxButtonFlags.EscapekeyDefault
                            : 0u
                };
            }

            var chosen = -1;

            fixed (byte* title = titleBytes)
            fixed (byte* message = messageBytes)
            fixed (MessageBoxButtonData* buttonData = handles) {
                var data = new MessageBoxData {
                    Flags = (uint)SeverityFlag(options.Severity),
                    Window = window,
                    Title = title,
                    Message = message,
                    Numbuttons = buttons.Length,
                    Buttons = buttonData
                };

                if (sdl.ShowMessageBox(&data, &chosen) != 0) {
                    return ValueTask.FromResult(MessageBoxResult.None);
                }
            }

            return ValueTask.FromResult(
                chosen >= 0 && chosen < results.Length ? results[chosen] : MessageBoxResult.None
            );
        } finally {
            foreach (var handle in pinned) {
                if (handle.IsAllocated) {
                    handle.Free();
                }
            }
        }
    }

    static string[] ButtonsFor(MessageBoxButtons buttons) => buttons switch {
        MessageBoxButtons.OkCancel => ["OK", "Cancel"],
        MessageBoxButtons.YesNo => ["Yes", "No"],
        MessageBoxButtons.YesNoCancel => ["Yes", "No", "Cancel"],
        _ => ["OK"]
    };

    static MessageBoxResult[] ResultsFor(MessageBoxButtons buttons) => buttons switch {
        MessageBoxButtons.OkCancel => [MessageBoxResult.Ok, MessageBoxResult.Cancel],
        MessageBoxButtons.YesNo => [MessageBoxResult.Yes, MessageBoxResult.No],
        MessageBoxButtons.YesNoCancel => [MessageBoxResult.Yes, MessageBoxResult.No, MessageBoxResult.Cancel],
        _ => [MessageBoxResult.Ok]
    };

    static MessageBoxFlags SeverityFlag(MessageBoxSeverity severity) => severity switch {
        MessageBoxSeverity.Warning => MessageBoxFlags.Warning,
        MessageBoxSeverity.Error => MessageBoxFlags.Error,
        _ => MessageBoxFlags.Information
    };
}

/// <summary>Text entry and the IME.</summary>
public sealed unsafe class DesktopTextInput(Sdl sdl) : ITextInput {
    DesktopWindow? active;

    /// <inheritdoc />
    public bool IsActive => sdl.IsTextInputActive() == SdlBool.True;

    /// <inheritdoc />
    public bool HasOnScreenKeyboard => sdl.HasScreenKeyboardSupport() == SdlBool.True;

    /// <inheritdoc />
    public bool IsOnScreenKeyboardVisible =>
        active is { IsClosed: false } && sdl.IsScreenKeyboardShown(active.Handle) == SdlBool.True;

    /// <summary>
    ///     Always <see cref="Rectangle.Empty" />: no desktop has an on-screen keyboard that covers
    ///     the window.
    /// </summary>
    public Rectangle OnScreenKeyboardArea => Rectangle.Empty;

    /// <inheritdoc />
    public void Activate(IWindow window) {
        ArgumentNullException.ThrowIfNull(window);
        active = window as DesktopWindow;
        sdl.StartTextInput();
    }

    /// <inheritdoc />
    public void Deactivate() {
        sdl.StopTextInput();
        active = null;
    }

    /// <inheritdoc />
    public void SetCandidateArea(IWindow window, Rectangle area) {
        ArgumentNullException.ThrowIfNull(window);

        var rectangle = new Silk.NET.Maths.Rectangle<int>(
            (int)area.X,
            (int)area.Y,
            (int)Math.Max(1f, area.Width),
            (int)Math.Max(1f, area.Height)
        );

        sdl.SetTextInputRect(&rectangle);
    }
}

/// <summary>Battery and charging state. SDL 2 reports no thermal or power-mode information.</summary>
/// <remarks>
///     <see cref="Thermal" /> is <see cref="ThermalState.Nominal" /> and
///     <see cref="IsLowPowerMode" /> is <see langword="false" /> because SDL cannot answer either,
///     not because the answer is known. Both are real signals on mobile, where the platform
///     assemblies that can answer them will.
/// </remarks>
public sealed unsafe class DesktopPowerInfo(Sdl sdl) : IPowerInfo {
    /// <inheritdoc />
    public PowerSource Source {
        get {
            var state = Query(out _, out _);

            return state switch {
                SdlPowerState.OnBattery => PowerSource.Battery,
                SdlPowerState.Charging => PowerSource.Charging,

                // "Charged" means plugged in with a full battery, which is mains as far as anything
                // making a quality decision is concerned.
                SdlPowerState.Charged or SdlPowerState.NoBattery => PowerSource.Mains,
                _ => PowerSource.Unknown
            };
        }
    }

    /// <inheritdoc />
    public float? BatteryLevel {
        get {
            Query(out _, out var percent);
            return percent < 0 ? null : percent / 100f;
        }
    }

    /// <inheritdoc />
    public TimeSpan? EstimatedTimeRemaining {
        get {
            Query(out var seconds, out _);
            return seconds < 0 ? null : TimeSpan.FromSeconds(seconds);
        }
    }

    /// <summary>Always <see cref="ThermalState.Nominal" />: SDL 2 has no thermal query.</summary>
    public ThermalState Thermal => ThermalState.Nominal;

    /// <summary>Always <see langword="false" />: SDL 2 has no power-mode query.</summary>
    public bool IsLowPowerMode => false;

    SdlPowerState Query(out int seconds, out int percent) {
        int remaining, level;
        var state = sdl.GetPowerInfo(&remaining, &level);
        seconds = remaining;
        percent = level;
        return state;
    }
}

/// <summary>Processor counts from SDL. Affinity belongs to the per-OS assemblies.</summary>
/// <remarks>
///     <see cref="AvailableProcessors" /> uses <see cref="Environment.ProcessorCount" /> rather than
///     <c>SDL_GetCPUCount</c>: the runtime's number accounts for a container's CPU quota and the
///     process affinity mask, and SDL's does not. Sizing a worker pool from SDL's would spawn
///     sixty-four workers against a two-core quota.
/// </remarks>
public sealed class DesktopProcessorTopology(Sdl sdl) : IProcessorTopology {
    /// <inheritdoc />
    public int AvailableProcessors => Environment.ProcessorCount;

    /// <summary>What SDL reports, which is logical processors — it has no physical-core query.</summary>
    public int PhysicalCores => Math.Max(1, sdl.GetCPUCount());

    /// <summary>Always <c>0</c>: SDL does not distinguish performance from efficiency cores.</summary>
    public int PerformanceCores => 0;

    /// <summary>Always <see langword="false" />: SDL has no affinity API.</summary>
    public bool SupportsAffinity => false;

    /// <inheritdoc />
    public ProcessorClass ClassOf(int processor) => ProcessorClass.Unknown;

    /// <inheritdoc />
    public bool TrySetAffinity(int processor) => false;

    /// <inheritdoc />
    public void ClearAffinity() { }
}

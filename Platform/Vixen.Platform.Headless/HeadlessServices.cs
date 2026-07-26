// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using Vixen.Core.Mathematics;

namespace Vixen.Platform.Headless;

/// <summary>No displays, which is a legal answer and not an error.</summary>
/// <remarks>
///     Every method returns nothing rather than throwing, so the caller's "no display" branch is the
///     branch that runs — and a subsystem that only has a "there is a display" branch fails here, at
///     a desk, instead of on a server at three in the morning.
/// </remarks>
public sealed class HeadlessDisplays : IDisplayInfo {
    /// <inheritdoc />
    public IReadOnlyList<DisplayInfo> Displays => [];

    /// <inheritdoc />
    public DisplayInfo? Primary => null;

    /// <inheritdoc />
    public bool TryGetForWindow(IWindow window, [NotNullWhen(true)] out DisplayInfo? display) {
        display = null;
        return false;
    }

    /// <inheritdoc />
    public bool TryGetForPoint(Int2 point, [NotNullWhen(true)] out DisplayInfo? display) {
        display = null;
        return false;
    }
}

/// <summary>No clipboard.</summary>
/// <remarks>
///     <para>
///         An in-process buffer pretending to be a clipboard was the obvious alternative and is the
///         wrong one: it would make copy-and-paste appear to work in a headless test and then not
///         work in the product, because a clipboard's entire purpose is to be shared with other
///         applications and there are none here. Refusing is the honest report of what a server can
///         do, and it is what <see cref="PlatformCapabilities.Clipboard" /> already says.
///     </para>
///     <para>
///         Code that wants a controllable clipboard for a test wants a test double, which is a
///         different object with a different name.
///     </para>
/// </remarks>
public sealed class HeadlessClipboard : IClipboard {
    /// <inheritdoc />
    public bool HasText => false;

    /// <inheritdoc />
    public bool HasImage => false;

    /// <inheritdoc />
    public bool TryGetText([NotNullWhen(true)] out string? text) {
        text = null;
        return false;
    }

    /// <inheritdoc />
    public bool SetText(string text) => false;

    /// <inheritdoc />
    public bool TryGetImage(out ClipboardImage image) {
        image = default;
        return false;
    }

    /// <inheritdoc />
    public bool SetImage(in ClipboardImage image) => false;

    /// <inheritdoc />
    public bool TryGetData(string format, out ReadOnlyMemory<byte> data) {
        data = default;
        return false;
    }

    /// <inheritdoc />
    public bool SetData(string format, ReadOnlySpan<byte> data) => false;

    /// <inheritdoc />
    public void Clear() { }
}

/// <summary>No dialogs, because there is no user to show one to.</summary>
/// <remarks>
///     Returns "nothing chosen" rather than throwing, which is the same answer a user pressing
///     Cancel gives — so the caller's existing cancellation path covers this and no headless special
///     case is needed anywhere.
/// </remarks>
public sealed class HeadlessDialogs : INativeDialogs {
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

/// <summary>No text input, and no IME to compose it.</summary>
public sealed class HeadlessTextInput : ITextInput {
    /// <inheritdoc />
    public bool IsActive { get; private set; }

    /// <inheritdoc />
    public bool HasOnScreenKeyboard => false;

    /// <inheritdoc />
    public bool IsOnScreenKeyboardVisible => false;

    /// <inheritdoc />
    public Rectangle OnScreenKeyboardArea => Rectangle.Empty;

    /// <summary>The caret rectangle last given to <see cref="SetCandidateArea" />.</summary>
    /// <remarks>
    ///     Kept so a test can assert that a text field told the IME where it was — the thing that is
    ///     invisible when it is missing and obvious to anyone typing Japanese.
    /// </remarks>
    public Rectangle CandidateArea { get; private set; }

    /// <inheritdoc />
    public void Activate(IWindow window) {
        ArgumentNullException.ThrowIfNull(window);
        IsActive = true;
    }

    /// <inheritdoc />
    public void Deactivate() => IsActive = false;

    /// <inheritdoc />
    public void SetCandidateArea(IWindow window, Rectangle area) {
        ArgumentNullException.ThrowIfNull(window);
        CandidateArea = area;
    }
}

/// <summary>Mains power, room temperature, no battery.</summary>
/// <remarks>
///     What a rack-mounted server truthfully reports. Thermal throttling on a server is real, but it
///     is the data-centre's business and not visible to a process, so <see cref="Thermal" /> stays
///     nominal rather than guessing.
/// </remarks>
public sealed class HeadlessPowerInfo : IPowerInfo {
    /// <inheritdoc />
    public PowerSource Source => PowerSource.Mains;

    /// <inheritdoc />
    public float? BatteryLevel => null;

    /// <inheritdoc />
    public TimeSpan? EstimatedTimeRemaining => null;

    /// <inheritdoc />
    public ThermalState Thermal => ThermalState.Nominal;

    /// <inheritdoc />
    public bool IsLowPowerMode => false;
}

/// <summary>How many processors the runtime says this process may use.</summary>
/// <remarks>
///     <para>
///         <see cref="AvailableProcessors" /> is <see cref="Environment.ProcessorCount" />, which on
///         .NET already accounts for a container's CPU quota and a process affinity mask — so a job
///         system sized from it does the right thing in the container a dedicated server actually
///         ships in.
///     </para>
///     <para>
///         Affinity is not offered. Pinning threads is per-OS work that belongs in the per-OS
///         assemblies, and a headless head is the one place where it matters least: a server shares
///         its machine with other tenants, and a process that pins itself to core 3 there is a
///         process fighting the scheduler that knows more than it does.
///     </para>
/// </remarks>
public sealed class HeadlessProcessorTopology : IProcessorTopology {
    /// <inheritdoc />
    public int AvailableProcessors => Environment.ProcessorCount;

    /// <inheritdoc />
    public int PhysicalCores => Environment.ProcessorCount;

    /// <inheritdoc />
    public int PerformanceCores => 0;

    /// <inheritdoc />
    public bool SupportsAffinity => false;

    /// <inheritdoc />
    public ProcessorClass ClassOf(int processor) => ProcessorClass.Unknown;

    /// <inheritdoc />
    public bool TrySetAffinity(int processor) => false;

    /// <inheritdoc />
    public void ClearAffinity() { }
}

/// <summary>No devices, and keyboard state that never changes.</summary>
/// <remarks>
///     Key and button state can be set, because otherwise nothing that reads input can be tested
///     without a keyboard attached — and the input layer's held-key handling across focus loss is
///     exactly the sort of thing that needs a test rather than a person.
/// </remarks>
public sealed class HeadlessInputSource : IInputSource {
    readonly HashSet<Key> keysDown = [];
    readonly HashSet<MouseButton> buttonsDown = [];

    /// <inheritdoc />
    public IReadOnlyList<IGamepad> Gamepads => [];

    /// <inheritdoc />
    public KeyModifiers Modifiers { get; set; }

    /// <inheritdoc />
    public Vector2 PointerPosition { get; set; }

    /// <inheritdoc />
    public bool TryGetGamepad(int deviceId, [NotNullWhen(true)] out IGamepad? gamepad) {
        gamepad = null;
        return false;
    }

    /// <inheritdoc />
    public bool IsKeyDown(Key key) => keysDown.Contains(key);

    /// <inheritdoc />
    public bool IsMouseButtonDown(MouseButton button) => buttonsDown.Contains(button);

    /// <summary>Sets whether a key is held.</summary>
    /// <param name="key">Which key.</param>
    /// <param name="down">Whether it is held.</param>
    public void SetKey(Key key, bool down) {
        if (down) {
            keysDown.Add(key);
        } else {
            keysDown.Remove(key);
        }
    }

    /// <summary>Sets whether a mouse button is held.</summary>
    /// <param name="button">Which button.</param>
    /// <param name="down">Whether it is held.</param>
    public void SetMouseButton(MouseButton button, bool down) {
        if (down) {
            buttonsDown.Add(button);
        } else {
            buttonsDown.Remove(button);
        }
    }

    /// <summary>Releases everything, as a real platform's focus loss effectively does.</summary>
    public void ReleaseAll() {
        keysDown.Clear();
        buttonsDown.Clear();
        Modifiers = KeyModifiers.None;
    }
}

/// <summary>The lifecycle a headless head has, plus the ability to drive it.</summary>
/// <remarks>
///     <para>
///         A server is never suspended by its OS, so on its own this would be an object that reports
///         <see cref="ApplicationState.Running" /> forever. The reason it is driveable is
///         <c>docs/plan/10 § Android</c>: lifecycle is the largest source of platform bugs, the
///         suspend/resume fault-injection loop it asks for has to run somewhere, and running it on a
///         phone in CI is not a thing that will happen on every pull request. Here it costs
///         milliseconds.
///     </para>
///     <para>
///         Every transition goes through the event buffer, so a test sees exactly what an Android
///         build's frame loop would see, in the same order.
///     </para>
/// </remarks>
public sealed class HeadlessLifecycle(PlatformEventBuffer events) : ILifecycle {
    /// <inheritdoc />
    public ApplicationState State { get; private set; } = ApplicationState.Running;

    /// <inheritdoc />
    public MemoryPressure MemoryPressure { get; private set; }

    /// <inheritdoc />
    public bool IsQuitRequested { get; private set; }

    /// <inheritdoc />
    public void RequestQuit() {
        if (IsQuitRequested) {
            return;
        }

        IsQuitRequested = true;
        events.Post(PlatformEvent.Application(PlatformEventKind.Quit, Stopwatch.GetTimestamp()));
    }

    /// <inheritdoc />
    public void CancelQuit() => IsQuitRequested = false;

    /// <summary>Suspends the application, as a mobile OS would.</summary>
    /// <remarks>Does nothing if it is already suspended, which is what a real platform guarantees
    /// and what a fault-injection loop would otherwise have to remember.</remarks>
    public void Suspend() {
        if (State == ApplicationState.Suspended) {
            return;
        }

        State = ApplicationState.Suspended;
        events.Post(PlatformEvent.Application(PlatformEventKind.Suspending, Stopwatch.GetTimestamp()));
    }

    /// <summary>Resumes the application.</summary>
    public void Resume() {
        if (State != ApplicationState.Suspended) {
            return;
        }

        State = ApplicationState.Running;
        events.Post(PlatformEvent.Application(PlatformEventKind.Resumed, Stopwatch.GetTimestamp()));
    }

    /// <summary>Moves the application in or out of the background.</summary>
    /// <param name="background">Whether it should be in the background.</param>
    public void SetBackground(bool background) {
        if (State is ApplicationState.Suspended or ApplicationState.Stopping) {
            return;
        }

        State = background ? ApplicationState.Background : ApplicationState.Running;
    }

    /// <summary>Reports memory pressure, as iOS's memory warning and Android's
    /// <c>onTrimMemory</c> do.</summary>
    /// <param name="pressure">How much pressure to report.</param>
    public void ReportMemoryPressure(MemoryPressure pressure) {
        MemoryPressure = pressure;

        if (pressure != Platform.MemoryPressure.Normal) {
            events.Post(PlatformEvent.Application(PlatformEventKind.LowMemory, Stopwatch.GetTimestamp()));
        }
    }

    internal void Stopping() => State = ApplicationState.Stopping;
}

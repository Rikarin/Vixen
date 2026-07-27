// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using Android.Content;
using Android.OS;
using Android.Runtime;
using Android.Views;
using Android.Views.InputMethods;
using Vixen.Core.IO;
using Vixen.Core.Mathematics;

namespace Vixen.Platform.Android;

/// <summary>When something happened, in the ticks the rest of the engine measures in.</summary>
/// <remarks>
///     <c>MotionEvent.EventTime</c> is milliseconds on <c>SystemClock.uptimeMillis</c> — a different
///     origin and a coarser unit than <see cref="Stopwatch" />, which is what a
///     <see cref="PlatformEvent" /> carries. Stamped on arrival instead; see the iOS clock for the
///     same decision and the same reason to revisit it when input latency is measured.
/// </remarks>
static class AndroidClock {
    public static long Now => Stopwatch.GetTimestamp();
}

/// <summary>The device's screen.</summary>
internal sealed class AndroidDisplays(Context context) : IDisplayInfo {
    /// <inheritdoc />
    public IReadOnlyList<DisplayInfo> Displays {
        get {
            var metrics = context.Resources?.DisplayMetrics;
            var scale = metrics?.Density ?? 1f;
            var width = metrics?.WidthPixels ?? 0;
            var height = metrics?.HeightPixels ?? 0;

            var bounds = new Rectangle(0, 0, width / scale, height / scale);
            var mode = new DisplayMode(new(width, height), Refresh(), IsHdr: false);

            // No safe-area inset is subtracted. WindowInsets is the right source and needs a window
            // that is attached; reporting the full bounds is wrong under a display cutout and is at
            // least visibly wrong, where a guessed inset would be invisibly wrong.
            return [new(0, "Main", bounds, bounds, scale, mode, [mode], IsPrimary: true)];
        }
    }

    /// <inheritdoc />
    public DisplayInfo? Primary => Displays[0];

    DisplayInfo Screen => Displays[0];

    /// <inheritdoc />
    public bool TryGetForWindow(IWindow window, [NotNullWhen(true)] out DisplayInfo? display) {
        ArgumentNullException.ThrowIfNull(window);
        display = Screen;
        return true;
    }

    /// <inheritdoc />
    public bool TryGetForPoint(Int2 point, [NotNullWhen(true)] out DisplayInfo? display) {
        display = Screen;
        return true;
    }

    float Refresh() {
        var display = context.GetSystemService(Context.WindowService) is IWindowManager manager
            ? manager.DefaultDisplay
            : null;

        return display?.RefreshRate ?? 60f;
    }
}

/// <summary>The system clipboard, text only.</summary>
/// <remarks>
///     Android's clipboard carries a <c>ClipData</c> of typed items, and images in it are content
///     URIs pointing at another application's provider — resolving one means a permission grant and a
///     stream copy. Refused rather than approximated, as on iOS.
/// </remarks>
internal sealed class AndroidClipboard(Context context) : IClipboard {
    /// <inheritdoc />
    public bool HasText => Manager?.HasPrimaryClip is true && Manager.PrimaryClip?.ItemCount > 0;

    /// <inheritdoc />
    public bool HasImage => false;

    /// <inheritdoc />
    public bool TryGetText([NotNullWhen(true)] out string? text) {
        text = Manager?.PrimaryClip is { ItemCount: > 0 } clip
            ? clip.GetItemAt(0)?.CoerceToText(context)
            : null;

        return !string.IsNullOrEmpty(text);
    }

    /// <inheritdoc />
    public bool SetText(string text) {
        if (Manager is not { } manager) {
            return false;
        }

        manager.PrimaryClip = ClipData.NewPlainText("text", text ?? string.Empty);
        return true;
    }

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
    public void Clear() => SetText(string.Empty);

    ClipboardManager? Manager => context.GetSystemService(Context.ClipboardService) as ClipboardManager;
}

/// <summary>Battery and thermal state.</summary>
/// <remarks>
///     Thermal status needs API 29 and is reported as <see cref="ThermalState.Nominal" /> below it —
///     the honest answer being "this device will not say", and the alternative being to infer heat
///     from throttling, which is inferring the cause from the symptom.
/// </remarks>
internal sealed class AndroidPower(Context context) : IPowerInfo {
    /// <inheritdoc />
    public PowerSource Source {
        get {
            if (Manager is not { } manager) {
                return PowerSource.Unknown;
            }

            return manager.IsCharging ? PowerSource.Charging : PowerSource.Battery;
        }
    }

    /// <inheritdoc />
    public float? BatteryLevel {
        get {
            var level = Manager?.GetIntProperty((int)BatteryProperty.Capacity);
            return level is >= 0 and <= 100 ? level.Value / 100f : null;
        }
    }

    /// <inheritdoc />
    /// <remarks>Android does not offer an estimate below API 28, and the API 28 one is a duration
    /// to empty that is absent while charging. Not reported rather than reported sometimes.</remarks>
    public TimeSpan? EstimatedTimeRemaining => null;

    /// <inheritdoc />
    public ThermalState Thermal {
        get {
            if (!OperatingSystem.IsAndroidVersionAtLeast(29)) {
                return ThermalState.Nominal;
            }

            if (context.GetSystemService(Context.PowerService) is not PowerManager manager) {
                return ThermalState.Nominal;
            }

            return manager.CurrentThermalStatus switch {
                >= ThermalStatus.Critical => ThermalState.Critical,
                ThermalStatus.Severe => ThermalState.Serious,
                ThermalStatus.Moderate or ThermalStatus.Light => ThermalState.Fair,
                _ => ThermalState.Nominal
            };
        }
    }

    /// <inheritdoc />
    public bool IsLowPowerMode =>
        context.GetSystemService(Context.PowerService) is PowerManager manager && manager.IsPowerSaveMode;

    BatteryManager? Manager => context.GetSystemService(Context.BatteryService) as BatteryManager;
}

/// <summary>What the processors are.</summary>
/// <remarks>
///     Android is where big.LITTLE came from, and it exposes the split only through
///     <c>/sys/devices/system/cpu</c> — parsing sysfs at boot to decide job-system shape is a real
///     option and is not taken here, because getting it wrong on one vendor's kernel is worse than
///     not asking. Affinity through <c>sched_setaffinity</c> is likewise possible and likewise not
///     claimed until it is measured.
/// </remarks>
internal sealed class AndroidProcessors : IProcessorTopology {
    /// <inheritdoc />
    public int AvailableProcessors => System.Environment.ProcessorCount;

    /// <inheritdoc />
    public int PhysicalCores => System.Environment.ProcessorCount;

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

/// <summary>The directories an Android application may use.</summary>
/// <remarks>
///     <para>
///         <c>/app</c> is the APK's assets rather than a directory, which is the case doc 10 says the
///         virtual file system exists for — see <see cref="AndroidAssetProvider" />.
///     </para>
///     <para>
///         The rest are the application's private container. <c>FilesDir</c> is backed up by default
///         and survives; <c>CacheDir</c> is deleted by the system under storage pressure, which is
///         the contract a downloaded-bundle cache should be written against. External storage is not
///         mounted: it needs a runtime permission on API 26–29 and is scoped away on 30 and later,
///         and a mount that is present but empty is worse than one that is absent.
///     </para>
/// </remarks>
internal sealed class AndroidFileSystemHost(Context context) : IFileSystemHost {
    /// <inheritdoc />
    /// <remarks>The APK itself. Present so that a diagnostic can name it; nothing reads it as a
    /// directory, because it is not one.</remarks>
    public string ApplicationDirectory => context.ApplicationInfo?.SourceDir ?? string.Empty;

    /// <inheritdoc />
    public string DataDirectory => context.FilesDir?.AbsolutePath ?? Path.GetTempPath();

    /// <inheritdoc />
    public string CacheDirectory => context.CacheDir?.AbsolutePath ?? Path.GetTempPath();

    /// <inheritdoc />
    public string TemporaryDirectory => Path.GetTempPath();

    /// <inheritdoc />
    public bool IsSandboxed => true;

    /// <inheritdoc />
    public void MountStandardLocations(VirtualFileSystem fileSystem) {
        ArgumentNullException.ThrowIfNull(fileSystem);

        if (context.Assets is { } assets) {
            fileSystem.Mount(MountPoints.App, new AndroidAssetProvider(assets));
        }

        fileSystem.Mount(MountPoints.Data, new PhysicalFileProvider(DataDirectory));
        fileSystem.Mount(MountPoints.Cache, new PhysicalFileProvider(CacheDirectory));
        fileSystem.Mount(MountPoints.Temp, new PhysicalFileProvider(TemporaryDirectory));
    }

    /// <inheritdoc />
    /// <remarks>
    ///     Reports what is already granted and asks for nothing. A runtime permission request needs
    ///     an <c>Activity</c> and a result callback, and returning a wrong answer immediately is
    ///     worse than declining: the private container needs no permission and is what the engine
    ///     uses.
    /// </remarks>
    public ValueTask<bool> RequestPermissionAsync(
        PermissionKind permission,
        CancellationToken cancellationToken = default
    ) =>
        ValueTask.FromResult(
            permission switch {
                // Scoped storage: an application always has its own container, and on API 30 and
                // later there is nothing else to ask for.
                PermissionKind.ReadExternalStorage or PermissionKind.WriteExternalStorage =>
                    OperatingSystem.IsAndroidVersionAtLeast(30),
                _ => false
            }
        );
}

/// <summary>The soft keyboard.</summary>
/// <remarks>
///     Android shows one when a focused view says it accepts text. This asks the input-method manager
///     directly against the game view, which is the same thing without a text widget. Keystrokes
///     arrive as ordinary key events on the view and are not translated here — see the README for
///     why the key map is owed rather than approximated.
/// </remarks>
internal sealed class AndroidTextInput(Context context) : ITextInput {
    /// <inheritdoc />
    public bool IsActive { get; private set; }

    /// <inheritdoc />
    public bool HasOnScreenKeyboard => true;

    /// <inheritdoc />
    /// <remarks>
    ///     What was asked for, not what is on screen. Android has no supported way to ask whether the
    ///     keyboard is showing — the usual trick compares the window's visible height against its
    ///     total, which is wrong in split-screen and on a device with a hardware keyboard. Saying
    ///     what was requested is at least true about something.
    /// </remarks>
    public bool IsOnScreenKeyboardVisible => IsActive;

    /// <inheritdoc />
    /// <remarks>Empty. See <see cref="IsOnScreenKeyboardVisible" />: the height is not knowable
    /// without the guess this declines to make.</remarks>
    public Rectangle OnScreenKeyboardArea => default;

    /// <inheritdoc />
    public void Activate(IWindow window) {
        ArgumentNullException.ThrowIfNull(window);

        if (window is not AndroidWindow android) {
            throw new ArgumentException("The window was not made by this platform.", nameof(window));
        }

        if (Manager is not { } manager) {
            return;
        }

        android.View.RequestFocus();
        IsActive = manager.ShowSoftInput(android.View, ShowFlags.Implicit);
    }

    /// <inheritdoc />
    public void Deactivate() {
        IsActive = false;
    }

    /// <inheritdoc />
    /// <remarks>Nothing. Android positions the candidate strip itself.</remarks>
    public void SetCandidateArea(IWindow window, Rectangle area) {
        ArgumentNullException.ThrowIfNull(window);
    }

    InputMethodManager? Manager =>
        context.GetSystemService(Context.InputMethodService) as InputMethodManager;
}

/// <summary>Polled input state, which is almost entirely absent here by design.</summary>
/// <remarks>Same reasoning as iOS: touch is an event stream, and gamepads are real work that is
/// honestly absent rather than stubbed.</remarks>
internal sealed class AndroidInput : IInputSource {
    /// <inheritdoc />
    public IReadOnlyList<IGamepad> Gamepads => [];

    /// <inheritdoc />
    public KeyModifiers Modifiers => KeyModifiers.None;

    /// <inheritdoc />
    public Vector2 PointerPosition { get; internal set; }

    /// <inheritdoc />
    public bool TryGetGamepad(int deviceId, [NotNullWhen(true)] out IGamepad? gamepad) {
        gamepad = null;
        return false;
    }

    /// <inheritdoc />
    public bool IsKeyDown(Key key) => false;

    /// <inheritdoc />
    public bool IsMouseButtonDown(MouseButton button) => false;
}

// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Diagnostics.CodeAnalysis;
using Foundation;
using UIKit;
using Vixen.Core.Mathematics;

namespace Vixen.Platform.Ios;

/// <summary>The one screen, as a display list.</summary>
/// <remarks>
///     iOS reports the main screen and, when one is attached, external ones. Only the main screen is
///     described: this platform makes exactly one window and it is on that screen, so an external
///     display an application cannot render to would be an entry that does nothing but invite code
///     to try.
/// </remarks>
internal sealed class IosDisplays : IDisplayInfo {
    /// <inheritdoc />
    public IReadOnlyList<DisplayInfo> Displays {
        get {
            var screen = UIScreen.MainScreen;
            var bounds = screen.Bounds;
            var scale = (float)screen.Scale;

            var area = new Rectangle(0, 0, (float)bounds.Width, (float)bounds.Height);
            var mode = new DisplayMode(
                new((int)(bounds.Width * scale), (int)(bounds.Height * scale)),
                // MaximumFramesPerSecond is the panel's ceiling — 120 on a ProMotion device — and is
                // what a refresh rate means here. What the application will actually get is the
                // display link's business, not the screen's.
                screen.MaximumFramesPerSecond,
                IsHdr: false
            );

            // The safe area is the work area: the notch, the status bar and the home indicator are
            // exactly the "do not put anything important here" that a desktop's work area describes.
            // Asked of the scene's own key window rather than UIApplication.KeyWindow, which iOS 13
            // deprecated because it answers across every connected scene and so belongs to no
            // particular one.
            var insets = UIApplication.SharedApplication.ConnectedScenes
                .OfType<UIWindowScene>()
                .SelectMany(scene => scene.Windows)
                .FirstOrDefault(candidate => candidate.IsKeyWindow)
                ?.SafeAreaInsets ?? default;

            var work = new Rectangle(
                (float)insets.Left,
                (float)insets.Top,
                (float)(bounds.Width - insets.Left - insets.Right),
                (float)(bounds.Height - insets.Top - insets.Bottom)
            );

            return [new(0, "Main", area, work, scale, mode, [mode], IsPrimary: true)];
        }
    }

    /// <inheritdoc />
    public DisplayInfo? Primary => Displays[0];

    /// <summary>The main screen, which on this platform always exists.</summary>
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
}

/// <summary>The system pasteboard.</summary>
/// <remarks>
///     Text works. Images and arbitrary formats are refused rather than approximated: UIPasteboard
///     does carry both, but doing it properly means <c>UIImage</c> encode and decode and a UTI for
///     every format, and a half-done version that silently loses the alpha channel is worse than a
///     <see langword="false" /> the caller can see.
/// </remarks>
internal sealed class IosClipboard : IClipboard {
    /// <inheritdoc />
    public bool HasText => UIPasteboard.General.HasStrings;

    /// <inheritdoc />
    public bool HasImage => false;

    /// <inheritdoc />
    public bool TryGetText([NotNullWhen(true)] out string? text) {
        text = UIPasteboard.General.String;
        return !string.IsNullOrEmpty(text);
    }

    /// <inheritdoc />
    public bool SetText(string text) {
        UIPasteboard.General.String = text ?? string.Empty;
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
    public void Clear() => UIPasteboard.General.String = string.Empty;
}

/// <summary>Battery and thermal state, which on a phone are not background details.</summary>
/// <remarks>
///     Doc 10 asks the renderer to scale quality from thermal state on this platform specifically,
///     because a phone under sustained load throttles rather than getting hot indefinitely — and a
///     game that ignores it gets its frame rate halved by the operating system instead of choosing
///     which half to give up.
/// </remarks>
internal sealed class IosPower : IPowerInfo {
    internal IosPower() {
        // Off by default, and the level reads as -1 until it is on.
        UIDevice.CurrentDevice.BatteryMonitoringEnabled = true;
    }

    /// <inheritdoc />
    public PowerSource Source =>
        UIDevice.CurrentDevice.BatteryState switch {
            UIDeviceBatteryState.Charging => PowerSource.Charging,
            UIDeviceBatteryState.Full => PowerSource.Mains,
            UIDeviceBatteryState.Unplugged => PowerSource.Battery,
            _ => PowerSource.Unknown
        };

    /// <inheritdoc />
    public float? BatteryLevel => UIDevice.CurrentDevice.BatteryLevel is var level and >= 0 ? level : null;

    /// <inheritdoc />
    /// <remarks>iOS does not tell an application how long it has left, and estimating it here would
    /// be inventing a number.</remarks>
    public TimeSpan? EstimatedTimeRemaining => null;

    /// <inheritdoc />
    public ThermalState Thermal =>
        NSProcessInfo.ProcessInfo.ThermalState switch {
            NSProcessInfoThermalState.Fair => ThermalState.Fair,
            NSProcessInfoThermalState.Serious => ThermalState.Serious,
            NSProcessInfoThermalState.Critical => ThermalState.Critical,
            _ => ThermalState.Nominal
        };

    /// <inheritdoc />
    public bool IsLowPowerMode => NSProcessInfo.ProcessInfo.LowPowerModeEnabled;
}

/// <summary>What the processors are, and the fact that they cannot be chosen between.</summary>
/// <remarks>
///     Apple silicon is asymmetric — performance and efficiency cores — and iOS exposes neither the
///     split nor any way to pin a thread to either. Quality of service is the whole of the API, and
///     it is advisory. So the counts are honest and <see cref="SupportsAffinity" /> is false, which
///     is what the job system reads.
/// </remarks>
internal sealed class IosProcessors : IProcessorTopology {
    /// <inheritdoc />
    public int AvailableProcessors => (int)NSProcessInfo.ProcessInfo.ActiveProcessorCount;

    /// <inheritdoc />
    public int PhysicalCores => (int)NSProcessInfo.ProcessInfo.ProcessorCount;

    /// <inheritdoc />
    /// <remarks>Unknown, and reported as zero rather than guessed from the core count.</remarks>
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

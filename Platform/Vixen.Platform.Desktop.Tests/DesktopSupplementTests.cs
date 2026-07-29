// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Diagnostics.CodeAnalysis;
using Xunit;

namespace Vixen.Platform.Desktop.Tests;

/// <summary>
///     How the per-OS assemblies get in, tested with one that is not any of them.
/// </summary>
/// <remarks>
///     The three real supplements are tested beside the assemblies that contain them. What is here
///     is the wiring: that the baseline handed over is SDL's, that what comes back is what the
///     platform then exposes, that the capabilities it adds are added, and that it is disposed with
///     the platform. A fake is the right subject for all four, because it is the only supplement
///     whose behaviour is the same on all three operating systems.
/// </remarks>
public sealed class DesktopSupplementTests {
    [Fact]
    public void TheSupplementIsAskedAndItsAnswerIsWhatThePlatformExposes() {
        Assert.SkipUnless(SdlLibrary.IsAvailable, "SDL2 is not installed on this machine.");

        var supplement = new RecordingSupplement();

        using (var platform = new DesktopPlatform(Options(supplement))) {
            Assert.Same(supplement.Clipboard, platform.Clipboard);
            Assert.Same(supplement.Dialogs, platform.Dialogs);
            Assert.Same(supplement.Power, platform.Power);
            Assert.Same(supplement.Processors, platform.Processors);

            // The baseline it was given is SDL's, not a set of nulls it has to check.
            Assert.IsType<DesktopClipboard>(supplement.Baseline.Clipboard);
            Assert.IsType<DesktopDialogs>(supplement.Baseline.Dialogs);
            Assert.IsType<DesktopPowerInfo>(supplement.Baseline.Power);
            Assert.IsType<DesktopProcessorTopology>(supplement.Baseline.Processors);

            Assert.True(platform.Has(PlatformCapabilities.NativeDialogs));
            Assert.False(supplement.IsDisposed);
        }

        Assert.True(supplement.IsDisposed);
    }

    /// <summary>
    ///     SDL has no file picker, so a platform without a supplement must not claim one — and it
    ///     is <see cref="PlatformCapabilities.NativeDialogs" /> that an "open project…" menu item
    ///     reads to decide whether it can work.
    /// </summary>
    [Fact]
    public void WithoutASupplementTheServicesAreSdlsAndThereAreNoDialogs() {
        Assert.SkipUnless(SdlLibrary.IsAvailable, "SDL2 is not installed on this machine.");

        using var platform = new DesktopPlatform(
            Options(null) with { UseNativeSupplement = false }
        );

        Assert.IsType<DesktopClipboard>(platform.Clipboard);
        Assert.IsType<DesktopDialogs>(platform.Dialogs);
        Assert.IsType<DesktopPowerInfo>(platform.Power);
        Assert.IsType<DesktopProcessorTopology>(platform.Processors);
        Assert.False(platform.Has(PlatformCapabilities.NativeDialogs));
    }

    /// <summary>
    ///     Every desktop this repository targets has one, and a machine that somehow does not gets
    ///     a working platform rather than an exception.
    /// </summary>
    [Fact]
    public void ThereIsASupplementForThisOperatingSystem() {
        using var supplement = DesktopSupplements.ForCurrentOperatingSystem();

        if (OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() || OperatingSystem.IsLinux()) {
            Assert.NotNull(supplement);
            Assert.False(string.IsNullOrWhiteSpace(supplement.Name));
        } else {
            Assert.Null(supplement);
        }
    }

    static DesktopPlatformOptions Options(IPlatformSupplement? supplement) =>
        new() {
            Application = "Vixen.Tests",
            EnableGameControllers = false,
            VideoDriver = "dummy",
            RequestGpuSurface = false,
            Supplement = supplement
        };

    /// <summary>A supplement that replaces everything with itself and remembers what it was given.</summary>
    sealed class RecordingSupplement : IPlatformSupplement {
        public PlatformServices Baseline { get; private set; }

        public bool IsDisposed { get; private set; }

        public IClipboard Clipboard { get; } = new NothingClipboard();

        public INativeDialogs Dialogs { get; } = new NothingDialogs();

        public IPowerInfo Power { get; } = new NothingPowerInfo();

        public IProcessorTopology Processors { get; } = new NothingProcessorTopology();

        public string Name => "Recording";

        public PlatformServices Augment(in PlatformServices baseline) {
            Baseline = baseline;

            return baseline with {
                Clipboard = Clipboard,
                Dialogs = Dialogs,
                Power = Power,
                Processors = Processors,
                Capabilities = baseline.Capabilities | PlatformCapabilities.NativeDialogs
            };
        }

        public void Dispose() => IsDisposed = true;
    }

    sealed class NothingClipboard : IClipboard {
        public bool HasText => false;

        public bool HasImage => false;

        public bool TryGetText([NotNullWhen(true)] out string? text) {
            text = null;
            return false;
        }

        public bool SetText(string text) => false;

        public bool TryGetImage(out ClipboardImage image) {
            image = default;
            return false;
        }

        public bool SetImage(in ClipboardImage image) => false;

        public bool TryGetData(string format, out ReadOnlyMemory<byte> data) {
            data = default;
            return false;
        }

        public bool SetData(string format, ReadOnlySpan<byte> data) => false;

        public void Clear() { }
    }

    sealed class NothingDialogs : INativeDialogs {
        public ValueTask<string?> OpenFileAsync(
            FileDialogOptions options,
            IWindow? owner = null,
            CancellationToken cancellationToken = default
        ) =>
            ValueTask.FromResult<string?>(null);

        public ValueTask<IReadOnlyList<string>> OpenFilesAsync(
            FileDialogOptions options,
            IWindow? owner = null,
            CancellationToken cancellationToken = default
        ) =>
            ValueTask.FromResult<IReadOnlyList<string>>([]);

        public ValueTask<string?> SaveFileAsync(
            FileDialogOptions options,
            IWindow? owner = null,
            CancellationToken cancellationToken = default
        ) =>
            ValueTask.FromResult<string?>(null);

        public ValueTask<string?> OpenFolderAsync(
            FileDialogOptions options,
            IWindow? owner = null,
            CancellationToken cancellationToken = default
        ) =>
            ValueTask.FromResult<string?>(null);

        public ValueTask<MessageBoxResult> ShowMessageAsync(
            MessageBoxOptions options,
            IWindow? owner = null,
            CancellationToken cancellationToken = default
        ) =>
            ValueTask.FromResult(MessageBoxResult.None);
    }

    sealed class NothingPowerInfo : IPowerInfo {
        public PowerSource Source => PowerSource.Unknown;

        public float? BatteryLevel => null;

        public TimeSpan? EstimatedTimeRemaining => null;

        public ThermalState Thermal => ThermalState.Nominal;

        public bool IsLowPowerMode => false;
    }

    sealed class NothingProcessorTopology : IProcessorTopology {
        public int AvailableProcessors => 1;

        public int PhysicalCores => 1;

        public int PerformanceCores => 0;

        public bool SupportsAffinity => false;

        public ProcessorClass ClassOf(int processor) => ProcessorClass.Unknown;

        public bool TrySetAffinity(int processor) => false;

        public void ClearAffinity() { }
    }
}

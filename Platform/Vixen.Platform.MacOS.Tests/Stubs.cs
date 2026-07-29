// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Diagnostics.CodeAnalysis;
using Vixen.Core.Mathematics;

namespace Vixen.Platform.MacOS.Tests;

/// <summary>
///     The baseline a supplement is given, with nothing behind it.
/// </summary>
/// <remarks>
///     Every service here answers "no". That is what makes it useful as a fallback in these tests: a
///     value that came from the platform-specific implementation is distinguishable from one that
///     was passed through, without a mocking framework and without SDL — which cannot start on a
///     test runner's thread on this operating system anyway.
/// </remarks>
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

/// <inheritdoc cref="NothingClipboard" />
sealed class NothingPowerInfo : IPowerInfo {
    public PowerSource Source => PowerSource.Unknown;

    public float? BatteryLevel => null;

    public TimeSpan? EstimatedTimeRemaining => null;

    public ThermalState Thermal => ThermalState.Nominal;

    public bool IsLowPowerMode => false;
}

/// <inheritdoc cref="NothingClipboard" />
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

/// <inheritdoc cref="NothingClipboard" />
sealed class NothingProcessorTopology : IProcessorTopology {
    public int AvailableProcessors => 1;

    public int PhysicalCores => 1;

    public int PerformanceCores => 0;

    public bool SupportsAffinity => false;

    public ProcessorClass ClassOf(int processor) => ProcessorClass.Unknown;

    public bool TrySetAffinity(int processor) => false;

    public void ClearAffinity() { }
}

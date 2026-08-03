// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Diagnostics.CodeAnalysis;
using Vixen.Core.Mathematics;

namespace Vixen.Platform;

/// <summary>One video mode a display can be switched to.</summary>
/// <param name="Size">The mode's resolution in physical pixels.</param>
/// <param name="RefreshRate">Refresh rate in hertz. Fractional, because 59.94 Hz is real.</param>
/// <param name="IsHdr">Whether this mode carries a wide-gamut, high-dynamic-range signal.</param>
public readonly record struct DisplayMode(Int2 Size, float RefreshRate, bool IsHdr) {
    /// <inheritdoc />
    public override string ToString() =>
        $"{Size.X}×{Size.Y} @ {RefreshRate:0.##} Hz{(IsHdr ? " HDR" : string.Empty)}";
}

/// <summary>One display, as the platform describes it.</summary>
/// <param name="Index">Its position in <see cref="IDisplayInfo.Displays" />, stable until a
/// <see cref="PlatformEventKind.DisplaysChanged" />.</param>
/// <param name="Name">A human-readable name, for a settings screen.</param>
/// <param name="Bounds">Its position and size in the desktop's coordinate space, in logical
/// points.</param>
/// <param name="WorkArea">The part of <paramref name="Bounds" /> not covered by a taskbar, dock or
/// menu bar — where a window should be placed.</param>
/// <param name="DpiScale">Physical pixels per logical point.</param>
/// <param name="CurrentMode">The mode it is running now.</param>
/// <param name="Modes">Every mode it can be switched to, or empty where the platform does not allow
/// mode switching.</param>
/// <param name="IsPrimary">Whether this is the display the OS considers the main one.</param>
public sealed record DisplayInfo(
    int Index,
    string Name,
    Rectangle Bounds,
    Rectangle WorkArea,
    float DpiScale,
    DisplayMode CurrentMode,
    IReadOnlyList<DisplayMode> Modes,
    bool IsPrimary
) {
    /// <summary>Whether any of this display's modes carries an HDR signal.</summary>
    public bool SupportsHdr => CurrentMode.IsHdr || Modes.Any(mode => mode.IsHdr);

    /// <inheritdoc />
    public override string ToString() => $"[{Index}] {Name} {CurrentMode} ×{DpiScale:0.##}";
}

/// <summary>What displays exist and what they can do.</summary>
/// <remarks>
///     <para>
///         Everything here is a snapshot. Displays are hot-pluggable, a laptop lid closing removes
///         one, and a scale change rewrites all of them — so a
///         <see cref="PlatformEventKind.DisplaysChanged" /> invalidates anything cached from this
///         interface, and code that stored a <see cref="DisplayInfo" /> at boot is code that has a
///         bug on a docking station.
///     </para>
///     <para>
///         Absent entirely on a headless platform, where <see cref="Displays" /> is empty. That is a
///         legal state, not an error: a dedicated server has no displays and asking it for one
///         should give an empty list rather than an exception, so the caller's fallback path is the
///         ordinary path.
///     </para>
/// </remarks>
public interface IDisplayInfo {
    /// <summary>Every display, in the platform's order. Empty when there are none.</summary>
    IReadOnlyList<DisplayInfo> Displays { get; }

    /// <summary>The display the OS considers primary, or <see langword="null" /> when there are
    /// none.</summary>
    DisplayInfo? Primary { get; }

    /// <summary>Finds the display a window is mostly on.</summary>
    /// <param name="window">The window to locate.</param>
    /// <param name="display">The display it is on.</param>
    /// <returns><see langword="false" /> when there are no displays, or the window is not on
    /// one.</returns>
    bool TryGetForWindow(IWindow window, [NotNullWhen(true)] out DisplayInfo? display);

    /// <summary>Finds the display containing a point in desktop coordinates.</summary>
    /// <param name="point">The point, in logical points.</param>
    /// <param name="display">The display containing it.</param>
    /// <returns><see langword="false" /> when no display contains the point.</returns>
    bool TryGetForPoint(Int2 point, [NotNullWhen(true)] out DisplayInfo? display);
}

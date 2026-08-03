// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

namespace Vixen.Platform;

/// <summary>Where the machine's power is coming from.</summary>
public enum PowerSource : byte {
    /// <summary>The platform does not say.</summary>
    Unknown = 0,

    /// <summary>Mains, or a device with no battery at all.</summary>
    Mains = 1,

    /// <summary>Battery, and discharging.</summary>
    Battery = 2,

    /// <summary>Battery, and charging.</summary>
    Charging = 3
}

/// <summary>How hot the device is, as the OS grades it.</summary>
/// <remarks>
///     The four levels are Apple's <c>NSProcessInfoThermalState</c> and map onto Android's
///     <c>PowerManager</c> thermal statuses closely enough to share a vocabulary. Desktops report
///     <see cref="Nominal" /> and mean it; phones spend a surprising amount of a long session above
///     it, which is why a quality-scaling policy that only reads frame time reacts a minute late.
/// </remarks>
public enum ThermalState : byte {
    /// <summary>Cool, or not reported.</summary>
    Nominal = 0,

    /// <summary>Warm. Fans are audible; nothing has been throttled yet.</summary>
    Fair = 1,

    /// <summary>Hot. The OS has begun throttling, and the frame budget is smaller than it was.</summary>
    Serious = 2,

    /// <summary>Very hot. The OS will start suspending things, ours included.</summary>
    Critical = 3
}

/// <summary>Battery, charging and thermal state.</summary>
/// <remarks>
///     <para>
///         Present so that quality scaling on mobile has something to scale on
///         (<c>docs/plan/10 § iOS</c>). A renderer that drops shadow resolution when
///         <see cref="Thermal" /> reaches <see cref="ThermalState.Serious" /> keeps a stable frame
///         rate; one that waits to notice frame times rise has already given the player a stutter.
///     </para>
///     <para>
///         Everything is optional. A desktop with no battery reports <see cref="PowerSource.Mains" />
///         and no level, and a headless server reports nothing at all — which is why the numeric
///         properties are nullable rather than sentinel-valued.
///     </para>
/// </remarks>
public interface IPowerInfo {
    /// <summary>Where power is coming from.</summary>
    PowerSource Source { get; }

    /// <summary>How full the battery is, <c>[0, 1]</c>, or <see langword="null" /> if there is no
    /// battery or the platform will not say.</summary>
    float? BatteryLevel { get; }

    /// <summary>How long the platform thinks the battery will last, or <see langword="null" /> if it
    /// does not estimate.</summary>
    TimeSpan? EstimatedTimeRemaining { get; }

    /// <summary>How hot the device is.</summary>
    ThermalState Thermal { get; }

    /// <summary>Whether the user has asked the OS to save power.</summary>
    /// <remarks>
    ///     An explicit instruction from the user, and the strongest signal available that a lower
    ///     frame rate is what they want. Ignoring it to hold 60 fps is choosing for them.
    /// </remarks>
    bool IsLowPowerMode { get; }
}

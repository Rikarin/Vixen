// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Runtime.Versioning;

namespace Vixen.Platform.Windows;

/// <summary>Battery and battery saver, from <c>GetSystemPowerStatus</c>.</summary>
/// <remarks>
///     <para>
///         <b>The reason this exists is one byte.</b> <c>SystemStatusFlag</c> is the user having
///         turned battery saver on, which is the strongest statement a user can make that they want
///         a lower frame rate, and SDL does not report it. The rest of the structure is what SDL
///         already reads and is read here too rather than delegated, because reading three fields of
///         a structure that has already been filled in is cheaper than the call that would fill it
///         in again.
///     </para>
///     <para>
///         <b>Thermal state is not answered here, and that is not an omission.</b> Windows has no
///         user-mode API for thermal pressure — no counterpart to <c>NSProcessInfo</c>'s
///         <c>thermalState</c> or Android's <c>PowerManager</c>. What exists is a WMI thermal-zone
///         temperature that most laptops do not populate, and a power-setting notification for
///         "the machine is about to shut down", which is a different question asked too late. So
///         <see cref="Thermal" /> is whatever the portable implementation says, which is
///         <see cref="ThermalState.Nominal" />, and a Windows title that wants to scale quality
///         reads frame time.
///     </para>
/// </remarks>
/// <param name="fallback">The portable power info, which keeps the thermal state.</param>
[SupportedOSPlatform("windows")]
public sealed class WindowsPowerInfo(IPowerInfo fallback) : IPowerInfo {
    const byte AcOffline = 0;
    const byte AcOnline = 1;
    const byte BatteryCharging = 8;
    const byte BatteryNone = 128;
    const byte Unknown = 255;

    /// <inheritdoc />
    public PowerSource Source {
        get {
            if (!Win32.GetSystemPowerStatus(out var status)) {
                return PowerSource.Unknown;
            }

            if ((status.BatteryFlag & BatteryNone) != 0) {
                return PowerSource.Mains;
            }

            return status.AcLineStatus switch {
                AcOnline => (status.BatteryFlag & BatteryCharging) != 0 ? PowerSource.Charging : PowerSource.Mains,
                AcOffline => PowerSource.Battery,
                _ => PowerSource.Unknown
            };
        }
    }

    /// <inheritdoc />
    public float? BatteryLevel {
        get {
            if (!Win32.GetSystemPowerStatus(out var status) || status.BatteryLifePercent == Unknown) {
                return null;
            }

            // Documented as 0–100 and observed as 255 for "no idea"; the clamp is for the firmware
            // that reports 101 on a fully charged pack.
            return Math.Clamp(status.BatteryLifePercent / 100f, 0f, 1f);
        }
    }

    /// <inheritdoc />
    public TimeSpan? EstimatedTimeRemaining {
        get {
            if (!Win32.GetSystemPowerStatus(out var status) || status.BatteryLifeTime == uint.MaxValue) {
                return null;
            }

            return TimeSpan.FromSeconds(status.BatteryLifeTime);
        }
    }

    /// <summary>What the portable implementation says. See the remarks on this class.</summary>
    public ThermalState Thermal => fallback.Thermal;

    /// <inheritdoc />
    public bool IsLowPowerMode =>
        Win32.GetSystemPowerStatus(out var status) && status.SystemStatusFlag != 0;
}

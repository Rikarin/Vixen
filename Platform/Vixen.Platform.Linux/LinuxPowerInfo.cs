// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Runtime.Versioning;

namespace Vixen.Platform.Linux;

/// <summary>Battery, thermal pressure and the power profile, from sysfs.</summary>
/// <remarks>
///     <para>
///         <b>Thermal state is the reason this exists.</b> SDL reports the battery and cannot report
///         how hot the machine is, and a quality-scaling policy that waits to notice frame times
///         rise has already given the player the stutter it exists to prevent. Linux publishes
///         thermal zones with the same trip points the kernel throttles on, which is a better signal
///         than a temperature in degrees: 82 °C means nothing without knowing what this chassis
///         considers hot, and "past the passive trip point" means exactly what
///         <see cref="ThermalState.Fair" /> is defined as.
///     </para>
///     <para>
///         <b>Read at most once a second.</b> These are properties, they look free, and a policy
///         that consults them per frame would do a dozen virtual-file-system round trips per frame
///         to watch a number that changes on the scale of tens of seconds. The cache is the reason
///         reading them in a hot loop is not a mistake.
///     </para>
///     <para>
///         <b>Low-power mode is the ACPI platform profile.</b> It is what
///         <c>power-profiles-daemon</c> and the desktop's own battery menu write to, so it is the
///         one place where "the user asked for less" is recorded regardless of which desktop asked.
///     </para>
/// </remarks>
/// <param name="fallback">The portable power info, which keeps the battery estimate.</param>
[SupportedOSPlatform("linux")]
public sealed class LinuxPowerInfo(IPowerInfo fallback) : IPowerInfo {
    const string PowerSupply = "/sys/class/power_supply";
    const string ThermalZones = "/sys/class/thermal";
    const string PlatformProfile = "/sys/firmware/acpi/platform_profile";

    const long CacheMilliseconds = 1000;

    long readAt = long.MinValue;
    PowerSource source;
    float? level;
    ThermalState thermal;
    bool lowPower;

    /// <inheritdoc />
    public PowerSource Source {
        get {
            Refresh();
            return source;
        }
    }

    /// <inheritdoc />
    public float? BatteryLevel {
        get {
            Refresh();
            return level;
        }
    }

    /// <summary>What the portable implementation estimates.</summary>
    /// <remarks>
    ///     Deferred rather than computed. sysfs publishes charge and current, and dividing one by
    ///     the other gives a number that swings by an hour when the user opens a browser tab. SDL
    ///     reads UPower's estimate, which is smoothed over time by something whose job that is.
    /// </remarks>
    public TimeSpan? EstimatedTimeRemaining => fallback.EstimatedTimeRemaining;

    /// <inheritdoc />
    public ThermalState Thermal {
        get {
            Refresh();
            return thermal;
        }
    }

    /// <inheritdoc />
    public bool IsLowPowerMode {
        get {
            Refresh();
            return lowPower;
        }
    }

    void Refresh() {
        var now = Environment.TickCount64;

        if (now - readAt < CacheMilliseconds) {
            return;
        }

        readAt = now;
        (source, level) = ReadBattery();
        thermal = ReadThermal();

        var profile = Sysfs.ReadText(PlatformProfile);

        // "low-power" is the profile power-profiles-daemon writes; "quiet" is what several vendors'
        // firmware calls the same thing.
        lowPower = profile is "low-power" or "quiet";
    }

    static (PowerSource Source, float? Level) ReadBattery() {
        if (!Directory.Exists(PowerSupply)) {
            return (PowerSource.Unknown, null);
        }

        var mains = false;
        float? capacity = null;
        string? status = null;

        foreach (var supply in Sysfs.Directories(PowerSupply)) {
            var kind = Sysfs.ReadText(Path.Combine(supply, "type"));

            if (kind is "Mains" or "USB") {
                mains |= Sysfs.ReadText(Path.Combine(supply, "online")) == "1";
                continue;
            }

            if (kind != "Battery") {
                continue;
            }

            // The first battery, and on a laptop with two the numbers are per-pack and there is no
            // meaningful way to average them without their design capacities. One is what every
            // desktop's own indicator shows.
            capacity ??= int.TryParse(
                Sysfs.ReadText(Path.Combine(supply, "capacity")),
                out var percent
            )
                ? Math.Clamp(percent / 100f, 0f, 1f)
                : null;

            status ??= Sysfs.ReadText(Path.Combine(supply, "status"));
        }

        if (capacity is null && status is null) {
            // Power supplies, and none of them a battery: a desktop. Mains, and mean it — which is
            // what PowerSource.Mains is defined to include.
            return (PowerSource.Mains, null);
        }

        var powerSource = status switch {
            "Charging" => PowerSource.Charging,
            "Discharging" => PowerSource.Battery,
            "Full" or "Not charging" => PowerSource.Mains,
            _ => mains ? PowerSource.Mains : PowerSource.Battery
        };

        return (powerSource, capacity);
    }

    /// <summary>
    ///     The hottest zone, graded against the trip points the kernel will act on.
    /// </summary>
    /// <remarks>
    ///     Every zone rather than a named one: which zone matters is a per-machine question — the
    ///     package on a desktop, the skin on a laptop, the battery on some phones — and taking the
    ///     worst of them is the reading that a quality-scaling policy wants. A zone with no trip
    ///     points contributes nothing, because a temperature with nothing to compare it to is not a
    ///     state.
    /// </remarks>
    static ThermalState ReadThermal() {
        var worst = ThermalState.Nominal;

        if (!Directory.Exists(ThermalZones)) {
            return worst;
        }

        foreach (var zone in Sysfs.Directories(ThermalZones)) {
            if (!Path.GetFileName(zone).StartsWith("thermal_zone", StringComparison.Ordinal)) {
                continue;
            }

            if (!int.TryParse(Sysfs.ReadText(Path.Combine(zone, "temp")), out var temperature)) {
                continue;
            }

            for (var trip = 0; ; trip++) {
                var kind = Sysfs.ReadText(Path.Combine(zone, $"trip_point_{trip}_type"));

                if (kind is null) {
                    break;
                }

                if (!int.TryParse(
                        Sysfs.ReadText(Path.Combine(zone, $"trip_point_{trip}_temp")),
                        out var threshold
                    )
                    || threshold <= 0
                    || temperature < threshold) {
                    continue;
                }

                var state = kind switch {
                    "critical" => ThermalState.Critical,
                    "hot" => ThermalState.Serious,
                    "passive" or "active" => ThermalState.Fair,
                    _ => ThermalState.Nominal
                };

                if (state > worst) {
                    worst = state;
                }
            }
        }

        return worst;
    }
}

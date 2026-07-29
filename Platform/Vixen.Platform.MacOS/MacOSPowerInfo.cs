// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Runtime.Versioning;

namespace Vixen.Platform.MacOS;

/// <summary>Thermal pressure and Low Power Mode, from <c>NSProcessInfo</c>.</summary>
/// <remarks>
///     <para>
///         <b>This is where <see cref="ThermalState" /> came from.</b> The four levels in that enum
///         are <c>NSProcessInfoThermalState</c>'s, chosen because Apple's are the ones with a
///         published meaning and Android's map onto them — so here the conversion is a cast, and the
///         only platform where the vocabulary is native is the one it was borrowed from.
///     </para>
///     <para>
///         <b>The battery stays with the portable implementation.</b> SDL reads it through IOKit's
///         power sources, which is the same place a second implementation here would read it from,
///         and doing that from managed code means Core Foundation dictionaries — a retain/release
///         discipline over a type system this assembly otherwise does not touch. There is nothing to
///         gain.
///     </para>
///     <para>
///         <b>Polled, not observed.</b> macOS posts
///         <c>NSProcessInfoThermalStateDidChangeNotification</c>, and observing it means registering
///         an Objective-C object with the notification centre — a class created at runtime, with
///         method implementations that are managed function pointers. A property read is two
///         message sends and a frame has budget for that; a runtime-created class is a great deal of
///         machinery for a value that changes every few minutes.
///     </para>
/// </remarks>
/// <param name="fallback">The portable power info, which keeps the battery.</param>
[SupportedOSPlatform("macos")]
public sealed class MacOSPowerInfo(IPowerInfo fallback) : IPowerInfo {
    /// <summary>What the portable implementation reports.</summary>
    public PowerSource Source => fallback.Source;

    /// <summary>What the portable implementation reports.</summary>
    public float? BatteryLevel => fallback.BatteryLevel;

    /// <summary>What the portable implementation reports.</summary>
    public TimeSpan? EstimatedTimeRemaining => fallback.EstimatedTimeRemaining;

    /// <inheritdoc />
    public ThermalState Thermal {
        get {
            if (ProcessInfo() is var info && info == 0) {
                return ThermalState.Nominal;
            }

            var state = ObjC.Send(info, ObjC.Selector("thermalState"));

            // The cast is the point — see the remarks. Anything outside the four is a level Apple
            // added after this was written, and treating an unknown level as nominal would be the
            // one reading that cannot be right.
            return state is >= 0 and <= 3 ? (ThermalState)(byte)state : ThermalState.Critical;
        }
    }

    /// <inheritdoc />
    public bool IsLowPowerMode =>
        ProcessInfo() is var info && info != 0
        && ObjC.SendBool(info, ObjC.Selector("isLowPowerModeEnabled"));

    static nint ProcessInfo() =>
        ObjC.Load() ? ObjC.Send(ObjC.GetClass("NSProcessInfo"), ObjC.Selector("processInfo")) : 0;
}

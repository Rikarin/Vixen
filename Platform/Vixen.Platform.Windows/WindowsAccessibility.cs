// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Runtime.Versioning;

namespace Vixen.Platform.Windows;

/// <summary>Which accessibility settings Windows is running with.</summary>
/// <remarks>
///     <para>
///         <b>Two <c>SystemParametersInfo</c> reads, which is what every native toolkit does here.</b>
///         Neither has a registry value worth reading instead: the animation switch is derived from
///         several settings pages and high contrast is a scheme rather than a flag, so the API is the
///         only place the answer is assembled.
///     </para>
///     <para>
///         ⚠ <b><c>SPI_GETCLIENTAREAANIMATION</c> is the <i>inverse</i> of the preference.</b> It
///         answers "may I animate", so <c>TRUE</c> means the user has <i>not</i> asked for reduced
///         motion. It is the same reading trap <see cref="WindowsAppearance" /> carries one setting
///         over, and getting it backwards ships more animation to exactly the users who asked for
///         less.
///     </para>
///     <para>
///         ⚠ <b>A failed call is <c>null</c> and not <c>false</c>.</b> The action is absent on
///         Windows old enough not to have the setting, and a policy can refuse it — reporting "the
///         user did not ask for reduced motion" for either is an answer this never learned. See
///         <see cref="SystemAccessibility" />.
///     </para>
/// </remarks>
[SupportedOSPlatform("windows")]
public static class WindowsAccessibility {
    /// <summary>Reads the current settings.</summary>
    public static unsafe SystemAccessibility Read() => new(ReduceMotion(), HighContrast());

    static unsafe bool? ReduceMotion() {
        var animate = 0;

        return Win32.SystemParametersInfo(Win32.SpiGetClientAreaAnimation, 0, &animate, 0)
            ? animate == 0
            : null;
    }

    static unsafe bool? HighContrast() {
        var contrast = new Win32.HighContrast { Size = (uint)sizeof(Win32.HighContrast) };

        return Win32.SystemParametersInfo(Win32.SpiGetHighContrast, contrast.Size, &contrast, 0)
            ? (contrast.Flags & Win32.HcfHighContrastOn) != 0
            : null;
    }
}

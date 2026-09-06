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
    const string Accessibility = @"Software\Microsoft\Accessibility";

    /// <summary>Reads the current settings.</summary>
    public static unsafe SystemAccessibility Read() => new(ReduceMotion(), HighContrast(), TextScale());

    /// <summary>The "Make text bigger" slider, as a multiplier.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>A registry value and not a <c>SystemParametersInfo</c> action, unlike the two
    ///         above, because Windows has no API for this outside WinRT.</b>
    ///         <c>UISettings.TextScaleFactor</c> is the documented reader and it is a Windows Runtime
    ///         type; the slider writes <c>HKCU\Software\Microsoft\Accessibility\TextScaleFactor</c>
    ///         and WinUI reads the same key underneath. Taking the registry keeps this assembly free
    ///         of a WinRT dependency for one integer.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>An absent value is one and not <c>null</c>.</b> The key is written when the
    ///         slider is first moved and simply does not exist until then, so a missing value is a
    ///         genuine "the user is at the default", exactly the reading <c>MacOSAccessibility</c>
    ///         applies to an absent defaults domain. <c>null</c> is kept for a read that <i>failed</i>
    ///         — a policy denying the key — because that is the case a host might want to log.
    ///     </para>
    /// </remarks>
    static unsafe float? TextScale() {
        var size = (uint)sizeof(uint);

        var status = Win32.RegGetValue(
            Win32.HkeyCurrentUser,
            Accessibility,
            "TextScaleFactor",
            Win32.RrfRtRegDword,
            out _,
            out var percent,
            ref size
        );

        // ERROR_FILE_NOT_FOUND (2) is the slider never having been moved, which is a scale of one.
        // Every other failure is a read this could not make.
        return status switch {
            0 => percent / 100f,
            2 => 1f,
            _ => null
        };
    }

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

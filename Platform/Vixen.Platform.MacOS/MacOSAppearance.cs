// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Runtime.Versioning;

namespace Vixen.Platform.MacOS;

/// <summary>What appearance macOS is set to, read out of the user's defaults.</summary>
/// <remarks>
///     <para>
///         <b><c>AppleInterfaceStyle</c> in <c>NSUserDefaults</c>, and not
///         <c>NSApp.effectiveAppearance</c>.</b> The two agree on a normal AppKit application, and
///         only one of them answers in a process that never made an <c>NSApplication</c> — which is
///         every SDL application, this engine's included. <c>effectiveAppearance</c> on a nil
///         <c>NSApp</c> is a message to nil, and a message to nil returns zero, which reads as
///         light.
///     </para>
///     <para>
///         ⚠ <b>The key is absent under a light appearance rather than set to <c>"Light"</c>.</b>
///         Dark mode writes <c>"Dark"</c>; turning it off removes the key. So the two answers this
///         can give are "the string says dark" and "there is no string", and the second one is
///         genuinely light rather than unknown — <c>NSUserDefaults</c> itself is available on every
///         macOS, so reaching it and finding nothing is an answer.
///     </para>
///     <para>
///         Foundation, not AppKit, so it does not need the main thread — see
///         <see cref="ObjC.IsMainThread" /> for what does.
///     </para>
/// </remarks>
[SupportedOSPlatform("macos")]
public static class MacOSAppearance {
    /// <summary>Reads the current appearance.</summary>
    /// <returns>The appearance, or <see cref="SystemColorScheme.Unknown" /> when the Objective-C
    /// runtime could not be loaded at all.</returns>
    public static SystemColorScheme Read() {
        if (!ObjC.Load()) {
            return SystemColorScheme.Unknown;
        }

        var defaults = ObjC.Send(ObjC.GetClass("NSUserDefaults"), ObjC.Selector("standardUserDefaults"));

        if (defaults == 0) {
            return SystemColorScheme.Unknown;
        }

        var style = ObjC.ToString(
            ObjC.Send(defaults, ObjC.Selector("stringForKey:"), ObjC.String("AppleInterfaceStyle"))
        );

        // ⚠ `StartsWith`, because the documented values are `Dark` and — on a system whose accent is
        // set to graphite with dark mode on — still `Dark`, but Apple has shipped `DarkAqua` through
        // the same key in seed builds. An equality test would report light for a system that is
        // visibly dark, which is the failure this whole path exists to stop.
        return style is null
            ? SystemColorScheme.Light
            : style.StartsWith("Dark", StringComparison.OrdinalIgnoreCase)
                ? SystemColorScheme.Dark
                : SystemColorScheme.Light;
    }
}

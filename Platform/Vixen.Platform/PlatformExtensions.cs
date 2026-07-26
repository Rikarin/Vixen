// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

namespace Vixen.Platform;

/// <summary>Conveniences over <see cref="IPlatform" /> that no implementation should redefine.</summary>
/// <remarks>
///     Extension methods rather than default interface members, for a reason that only shows up in
///     use: a default interface member is invisible through the concrete type, so
///     <c>headlessPlatform.Has(…)</c> would not compile and every call site would need a cast to
///     <see cref="IPlatform" /> first. Extensions bind on the static type either way.
/// </remarks>
public static class PlatformExtensions {
    /// <summary>Whether every capability in <paramref name="required" /> is present.</summary>
    /// <param name="platform">The platform to ask.</param>
    /// <param name="required">One or more capabilities.</param>
    /// <remarks>
    ///     The `and` form, not the `or` form: asking for two capabilities and being told "yes"
    ///     because one of them happens to be available is never what a caller meant.
    /// </remarks>
    public static bool Has(this IPlatform platform, PlatformCapabilities required) {
        ArgumentNullException.ThrowIfNull(platform);
        return (platform.Capabilities & required) == required;
    }

    /// <summary>The window that has keyboard focus, or <see langword="null" /> if none does.</summary>
    /// <param name="platform">The platform to ask.</param>
    public static IWindow? FocusedWindow(this IPlatform platform) {
        ArgumentNullException.ThrowIfNull(platform);

        foreach (var window in platform.Windows) {
            if (window.IsFocused) {
                return window;
            }
        }

        return null;
    }
}

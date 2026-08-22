// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Immutable;

namespace Vixen.Ui.HotReload;

/// <summary>Which of the three things a reload can be about.</summary>
public enum ReloadChannel : byte {
    /// <summary>A <c>.vcss</c> changed. Nothing is rebuilt; the cascade runs again.</summary>
    Styles,

    /// <summary>A <c>.vxml</c> changed, and its component has been recompiled.</summary>
    Markup,

    /// <summary>A component instance was replaced outright.</summary>
    Component
}

/// <summary>What a reload did, and what it could not do.</summary>
/// <param name="Channel">Which channel it was.</param>
/// <param name="Components">How many components were rebuilt.</param>
/// <param name="FocusRestored">Whether the focus went back where it was.</param>
/// <param name="Errors">
///     What went wrong. A styles reload with errors changed nothing — it put the previous text back.
/// </param>
/// <remarks>
///     Returned rather than logged, because a hot reload is a thing a developer is watching happen
///     and a silent one is indistinguishable from a broken watcher.
/// </remarks>
public readonly record struct ReloadReport(
    ReloadChannel Channel,
    int Components,
    bool FocusRestored,
    ImmutableArray<string> Errors
) {
    /// <summary>Whether it worked.</summary>
    public bool Succeeded => Errors.IsDefaultOrEmpty;

    /// <summary>How many component <i>instances</i> were thrown away and made again.</summary>
    /// <remarks>
    ///     ⚠ <b>Separate from <see cref="Components" /> because it is the number that costs
    ///     something.</b> A rebuild keeps the object and therefore its signals; a replacement keeps
    ///     only what <see cref="HotReloadStateAttribute" /> marked, which in practice is nothing at
    ///     all. A developer who sees a panel forget where it was is entitled to a number that says
    ///     so, and a report that folded the two together would make the expensive case
    ///     indistinguishable from the free one.
    ///     <para>
    ///         Not a positional parameter: a reload is a rebuild almost always, and every caller
    ///         that constructs a report about one would have to write a zero.
    ///     </para>
    /// </remarks>
    public int Replaced { get; init; }
}

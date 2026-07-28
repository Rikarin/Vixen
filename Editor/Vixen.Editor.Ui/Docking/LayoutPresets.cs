// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Ui.Controls;
using Vixen.Ui.Controls.Advanced;

namespace Vixen.Editor.Ui;

/// <summary>The arrangements doc 11 names, as shapes rather than as panel lists.</summary>
/// <remarks>
///     <para>
///         ⚠ <b>These take panel ids rather than knowing any.</b> "Default / Scene / Shading /
///         Animation / Debug" are the five presets the editor ships, but which panels are in them is
///         the application's business — a plugin adds a panel and a game team ships a preset with it
///         in. What is reusable is the arrangement: a browser on the left, a viewport in the middle,
///         an inspector on the right, a console along the bottom.
///     </para>
///     <para>
///         The ratios are the ones that survive contact with a 1280-wide window, which is the
///         smallest anybody edits on: a quarter to the left column is enough for a hierarchy and not
///         enough to matter, and a fifth to the bottom is two dozen lines of log.
///     </para>
/// </remarks>
public static class LayoutPresets {
    /// <summary>An arrangement with one panel filling the window.</summary>
    /// <param name="panel">Its id.</param>
    /// <returns>The arrangement.</returns>
    public static DockLayout Single(string panel) {
        ArgumentNullException.ThrowIfNull(panel);
        return new DockLayout { Root = new DockGroupNode(panel) };
    }

    /// <summary>A browser, a centre, an inspector, and a console under the middle.</summary>
    /// <param name="left">What goes down the left, as tabs of one group.</param>
    /// <param name="centre">What goes in the middle.</param>
    /// <param name="right">What goes down the right.</param>
    /// <param name="bottom">What goes along the bottom of the middle, or nothing.</param>
    /// <returns>The arrangement.</returns>
    /// <remarks>
    ///     ⚠ <b>The console splits the <i>centre</i> rather than the window.</b> A log along the
    ///     whole bottom edge pushes the inspector up and leaves it half the height it needs, which
    ///     is the arrangement every editor tries once.
    /// </remarks>
    public static DockLayout Standard(
        IReadOnlyList<string> left,
        IReadOnlyList<string> centre,
        IReadOnlyList<string> right,
        IReadOnlyList<string>? bottom = null
    ) {
        ArgumentNullException.ThrowIfNull(left);
        ArgumentNullException.ThrowIfNull(centre);
        ArgumentNullException.ThrowIfNull(right);

        DockNode middle = Group(centre) ?? new DockGroupNode();

        if (Group(bottom) is { } console) {
            middle = new DockSplitNode(Orientation.Vertical, middle, console, 0.72f);
        }

        if (Group(right) is { } inspector) {
            middle = new DockSplitNode(Orientation.Horizontal, middle, inspector, 0.74f);
        }

        return new DockLayout {
            Root = Group(left) is { } browser
                ? new DockSplitNode(Orientation.Horizontal, browser, middle, 0.2f)
                : middle
        };
    }

    /// <summary>Two panels side by side, which is what a graph editor and its preview want.</summary>
    /// <param name="first">The left half.</param>
    /// <param name="second">The right half.</param>
    /// <param name="ratio">How much of the width the first takes.</param>
    /// <returns>The arrangement.</returns>
    public static DockLayout Split(IReadOnlyList<string> first, IReadOnlyList<string> second, float ratio = 0.5f) {
        ArgumentNullException.ThrowIfNull(first);
        ArgumentNullException.ThrowIfNull(second);

        var left = Group(first);
        var right = Group(second);

        return new DockLayout {
            Root = left is null
                ? right
                : right is null
                    ? left
                    : new DockSplitNode(Orientation.Horizontal, left, right, ratio)
        };
    }

    /// <summary>A group of tabs, or <c>null</c> if there are none.</summary>
    /// <remarks>
    ///     Empty gives <c>null</c> rather than an empty group, for the reason
    ///     <c>DockGroupNode.Read</c> gives: an empty group is a tab strip taking up half the window
    ///     with nothing in it.
    /// </remarks>
    static DockGroupNode? Group(IReadOnlyList<string>? panels) =>
        panels is { Count: > 0 } ? new DockGroupNode([.. panels]) : null;
}

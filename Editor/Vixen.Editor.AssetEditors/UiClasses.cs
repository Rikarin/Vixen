// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Ui;
using Vixen.Ui.Controls;

namespace Vixen.Editor.AssetEditors;

/// <summary>The two element helpers this assembly's views wanted and <c>Vixen.Ui</c> does not have.</summary>
/// <remarks>
///     ⚠ <b>Here rather than in <c>Vixen.Ui</c>, because neither is a claim about the framework.</b>
///     "Add a class when a condition holds" and "empty an element" are shapes this assembly's panels
///     use constantly and that a UI framework can reasonably leave to its callers — adding them to
///     <c>UiElement</c> would be widening a public surface for the convenience of one consumer.
/// </remarks>
static class UiClasses {
    /// <summary>Adds or removes a class according to a condition.</summary>
    /// <param name="element">The element.</param>
    /// <param name="className">The class.</param>
    /// <param name="on">Whether it should have it.</param>
    public static void SetClass(this UiElement element, string className, bool on) {
        ArgumentNullException.ThrowIfNull(element);

        if (on) {
            element.AddClass(className);
        } else {
            element.RemoveClass(className);
        }
    }

    /// <summary>Removes every child of an element.</summary>
    /// <param name="element">The element.</param>
    /// <remarks>
    ///     ⚠ <b>From the end, which is what makes it linear.</b> Removing the first child of a list
    ///     shifts every other one down, so the obvious loop is quadratic in a panel with a few hundred
    ///     rows — which the coverage list and the diagnostics list both are.
    /// </remarks>
    public static void Empty(this UiElement element) {
        ArgumentNullException.ThrowIfNull(element);

        while (element.Children.Count > 0) {
            element.Children[^1].Remove();
        }
    }

    /// <summary>Which option is selected, by position, or −1.</summary>
    /// <param name="select">The select.</param>
    /// <returns>The index.</returns>
    /// <remarks>
    ///     <see cref="Select" /> is a control over <i>values</i> and deliberately has no index — two
    ///     options with one value would make an index ambiguous. Where a caller genuinely wants the
    ///     position, this is that lookup in one place rather than in three panels.
    /// </remarks>
    public static int IndexOfSelection(this Select select) {
        ArgumentNullException.ThrowIfNull(select);

        for (var index = 0; index < select.Options.Count; index++) {
            if (string.Equals(select.Options[index].Value, select.Value, StringComparison.Ordinal)) {
                return index;
            }
        }

        return -1;
    }
}

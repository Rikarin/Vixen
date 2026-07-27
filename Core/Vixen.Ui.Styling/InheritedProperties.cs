// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

namespace Vixen.Ui.Styling;

/// <summary>Which properties a child gets from its parent without asking.</summary>
/// <remarks>
///     <para>
///         Inheritance is not a convenience, it is what makes a stylesheet finite. Setting
///         <c>color</c> on a panel and having every label inside it follow is the difference between
///         a theme and a per-element assignment for every element.
///     </para>
///     <para>
///         The list is CSS's, and it is short for a reason worth knowing: a property inherits when
///         inheriting it is nearly always what someone wants. Text properties inherit; box properties
///         do not, because a panel with <c>padding: 8px</c> whose every descendant also got 8px would
///         be unusable. Getting this list wrong in either direction produces a UI that looks broken
///         in a way that reads as a layout bug.
///     </para>
///     <para>
///         Custom properties (<c>--x</c>) always inherit and are not in the list — they are
///         recognised by their name, since there is no finite set of them.
///     </para>
/// </remarks>
public sealed class InheritedProperties {
    static readonly string[] Names = [
        "color",
        "font-family",
        "font-size",
        "font-style",
        "font-weight",
        "font-stretch",
        "font-variant",
        "line-height",
        "letter-spacing",
        "word-spacing",
        "text-align",
        "text-indent",
        "text-transform",
        "white-space",
        "word-break",
        "overflow-wrap",
        "direction",
        "visibility",
        "cursor",

        // Vixen's own, and inherited for the same reason `color` is: a panel that dims its contents
        // should not have to name every one of them.
        "tint",
        "font-feature-settings"
    ];

    readonly HashSet<int> ids = [];
    readonly Dictionary<int, bool> custom = [];
    readonly NameTable properties;

    /// <summary>Interns the inherited property names into a table.</summary>
    /// <param name="properties">The table property names live in.</param>
    public InheritedProperties(NameTable properties) {
        ArgumentNullException.ThrowIfNull(properties);
        this.properties = properties;

        foreach (var name in Names) {
            ids.Add(properties.Intern(name));
        }
    }

    /// <summary>Whether a property inherits.</summary>
    /// <param name="property">Its interned name.</param>
    /// <returns>Whether a child gets it from its parent unasked.</returns>
    /// <remarks>
    ///     Whether an id is a custom property is answered once per id and remembered. The alternative
    ///     is a string prefix test per property per element per restyle, which would spend the
    ///     interning back.
    /// </remarks>
    public bool Inherits(int property) {
        if (ids.Contains(property)) {
            return true;
        }

        if (custom.TryGetValue(property, out var isCustom)) {
            return isCustom;
        }

        isCustom = IsCustomProperty(properties.NameOf(property));
        custom[property] = isCustom;
        return isCustom;
    }

    /// <summary>Whether two styles differ in any property a child would have inherited.</summary>
    /// <param name="before">One style.</param>
    /// <param name="after">The other.</param>
    /// <returns>Whether a child of an element holding these could be affected by the difference.</returns>
    /// <remarks>
    ///     <para>
    ///         The question a restyle pass asks at every element it touches, and asking the coarser
    ///         version instead — <i>did anything at all change</i> — is what made selecting one row
    ///         of a grid restyle its hundred cells. A highlight that changes <c>background</c> cannot
    ///         reach a child; one that changes <c>color</c> reaches all of them. Only the second is a
    ///         reason to descend.
    ///     </para>
    ///     <para>
    ///         A merge over two arrays already sorted by property id, so it costs one pass over the
    ///         two tables and no allocation.
    ///     </para>
    /// </remarks>
    public bool InheritedPortionDiffers(ComputedStyle before, ComputedStyle after) {
        ArgumentNullException.ThrowIfNull(before);
        ArgumentNullException.ThrowIfNull(after);

        if (ReferenceEquals(before, after)) {
            return false;
        }

        var left = before.Properties;
        var right = after.Properties;
        int i = 0, j = 0;

        while (i < left.Length && j < right.Length) {
            if (left[i] == right[j]) {
                if (before.Values[i] != after.Values[j] && Inherits(left[i])) {
                    return true;
                }

                i++;
                j++;
            } else if (left[i] < right[j]) {
                // Set on the way in and gone on the way out.
                if (Inherits(left[i++])) {
                    return true;
                }
            } else if (Inherits(right[j++])) {
                return true;
            }
        }

        while (i < left.Length) {
            if (Inherits(left[i++])) {
                return true;
            }
        }

        while (j < right.Length) {
            if (Inherits(right[j++])) {
                return true;
            }
        }

        return false;
    }

    /// <summary>Whether a property name is a custom property.</summary>
    /// <param name="name">The property name.</param>
    /// <returns>Whether it begins with <c>--</c>.</returns>
    public static bool IsCustomProperty(string name) {
        ArgumentNullException.ThrowIfNull(name);
        return name.StartsWith("--", StringComparison.Ordinal);
    }
}

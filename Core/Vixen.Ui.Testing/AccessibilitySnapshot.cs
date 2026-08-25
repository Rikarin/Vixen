// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Text;

namespace Vixen.Ui.Testing;

/// <summary>The accessibility tree of a document, as text a test can compare.</summary>
/// <remarks>
///     <para>
///         <b>Doc 09's Testing table promises an "ARIA-role snapshot" per control, and this is the
///         thing that makes writing one a line of test code.</b> It walks the element tree, emits
///         only the elements that are nodes — <see cref="UiElement.IsInAccessibilityTree" /> — and
///         renders each as its role, its accessible name, its value and its states, indented by
///         depth. A control's snapshot is then a string literal in a test, and a change to what a
///         screen reader would say shows up as a diff rather than as nothing.
///     </para>
///     <para>
///         ⚠ <b>A snapshot that is empty is a snapshot that passes, which is why
///         <see cref="Unnamed" /> exists beside it.</b> "The tree matches this expected text" is
///         satisfied perfectly by a tree with nothing in it and an expectation to match, and that is
///         this repository's commonest defect rather than a hypothetical. <see cref="Unnamed" /> is
///         the assertion that cannot be satisfied vacuously: every element that a user can operate
///         must have a role <i>and</i> a non-empty name, and an interface with no such elements has
///         no interactive elements to get wrong.
///     </para>
///     <para>
///         ⚠ <b>Here rather than in a test project, because there are two control assemblies.</b>
///         <c>Vixen.Ui.Controls.Tests</c> and <c>Vixen.Ui.Controls.Advanced.Tests</c> cannot see each
///         other, and a renderer copied into both would be two renderers producing two formats the
///         day one of them was improved.
///     </para>
/// </remarks>
public static class AccessibilitySnapshot {
    /// <summary>The ARIA roles that stand for something a user operates.</summary>
    /// <remarks>
    ///     WAI-ARIA 1.2's <i>widget roles</i> — its § 5.3.2 category, which is the specification's
    ///     own answer to "which of these is a control" rather than a list chosen here. It is what
    ///     <see cref="Unnamed" /> holds to the naming rule.
    /// </remarks>
    static readonly HashSet<AccessibleRole> Widgets = [
        AccessibleRole.Button,
        AccessibleRole.CheckBox,
        AccessibleRole.ComboBox,
        AccessibleRole.GridCell,
        AccessibleRole.Link,
        AccessibleRole.ListBox,
        AccessibleRole.MenuItem,
        AccessibleRole.MenuItemCheckBox,
        AccessibleRole.MenuItemRadio,
        AccessibleRole.Option,
        AccessibleRole.Radio,
        AccessibleRole.ScrollBar,
        AccessibleRole.SearchBox,
        AccessibleRole.Slider,
        AccessibleRole.SpinButton,
        AccessibleRole.Switch,
        AccessibleRole.Tab,
        AccessibleRole.TextBox,
        AccessibleRole.TreeItem
    ];

    /// <summary>Renders the accessibility tree under an element.</summary>
    /// <param name="root">Where to start. Usually <c>document.Root</c> or the control under test.</param>
    /// <returns>One line per node, indented two spaces per level, newline-separated, no trailing newline.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="root" /> is null.</exception>
    /// <remarks>
    ///     <para>
    ///         <b>The shape of a line is</b>
    ///         <c>role "name" = "value" [state state]</c>, with the name, the value and the bracket
    ///         each omitted when there is nothing to say. The role token is the ARIA one —
    ///         <see cref="AccessibleRole" /> keeps its member names spelled so that lowercasing is
    ///         the mapping — and the states are ARIA and AT-SPI state names, in declaration order so
    ///         that a snapshot does not churn on a set's iteration order.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>An element that is not a node is walked <i>through</i> rather than skipped.</b>
    ///         Its children rise to its parent's depth, which is what <c>role="none"</c> means and is
    ///         the difference between a tree a screen reader can read and thirty nested groups. So
    ///         the indentation is accessibility-tree depth and not element depth, and a control that
    ///         grows a wrapper element does not move in the snapshot.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>An owned element is emitted under its owner and not where the tree has it.</b> A
    ///         <c>Select</c>'s list is a child of the document root — an overlay inside the field
    ///         that opens it would be clipped — and <see cref="AccessibleRelation.Owns" /> is how the
    ///         control says so. Rendering it where the elements happen to live would produce a
    ///         snapshot in which every combo box in the document was empty and a pile of loose lists
    ///         sat at the end, which is exactly the picture the relation exists to correct.
    ///     </para>
    /// </remarks>
    public static string Render(UiElement root) {
        ArgumentNullException.ThrowIfNull(root);

        var owned = new HashSet<UiElement>();
        CollectOwned(root, owned);

        var text = new StringBuilder();
        Walk(root, 0, owned, text);

        return text.ToString().TrimEnd('\n');
    }

    /// <summary>The interactive elements under an element that have no accessible name.</summary>
    /// <param name="root">Where to start.</param>
    /// <returns>A description of each offender, in tree order. Empty when there are none.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="root" /> is null.</exception>
    /// <remarks>
    ///     <para>
    ///         <b>The half of an accessibility gate that cannot pass by accident.</b> A snapshot
    ///         asserts that the tree is what it was; this asserts that it is worth having. Every
    ///         element whose role is one WAI-ARIA calls a widget must answer a non-empty
    ///         <see cref="UiElement.AccessibleName" />, and the two ways a control set fails —
    ///         a control with no role at all, and a control with a role and nothing to call it — are
    ///         both this list being non-empty.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>An element that is focusable and has no role is an offender too</b>, and it is
    ///         the one a per-control pass forgets. A control the keyboard can reach is by definition
    ///         something the user operates; if it is not in the tree, a screen-reader user tabs onto
    ///         silence.
    ///     </para>
    /// </remarks>
    public static IReadOnlyList<string> Unnamed(UiElement root) {
        ArgumentNullException.ThrowIfNull(root);

        var offenders = new List<string>();
        Audit(root, offenders);

        return offenders;
    }

    static void Audit(UiElement element, List<string> offenders) {
        var role = element.Role;

        if (element.Focusable && role == AccessibleRole.None) {
            offenders.Add($"<{element.Tag}> is focusable and has no role");
        } else if (Widgets.Contains(role) && string.IsNullOrEmpty(element.AccessibleName)) {
            offenders.Add($"<{element.Tag}> is a {Token(role)} and has no accessible name");
        }

        foreach (var child in element.Children) {
            Audit(child, offenders);
        }
    }

    static void CollectOwned(UiElement element, HashSet<UiElement> owned) {
        foreach (var relationship in element.AccessibleRelationships) {
            if (relationship.Relation == AccessibleRelation.Owns) {
                owned.Add(relationship.Target);
            }
        }

        foreach (var child in element.Children) {
            CollectOwned(child, owned);
        }
    }

    static void Walk(UiElement element, int depth, HashSet<UiElement> owned, StringBuilder text) {
        var isNode = element.IsInAccessibilityTree;

        if (isNode) {
            text.Append(' ', depth * 2);
            Describe(element, text);
            text.Append('\n');
        }

        var childDepth = isNode ? depth + 1 : depth;

        foreach (var child in element.Children) {
            // Rendered under whoever owns it, further down or further up. Emitting it here as well
            // would put a `Select`'s list in the snapshot twice.
            if (owned.Contains(child)) {
                continue;
            }

            Walk(child, childDepth, owned, text);
        }

        foreach (var relationship in element.AccessibleRelationships) {
            if (relationship.Relation == AccessibleRelation.Owns) {
                Walk(relationship.Target, childDepth, owned, text);
            }
        }
    }

    static void Describe(UiElement element, StringBuilder text) {
        text.Append(Token(element.Role));

        if (element.AccessibleName is { Length: > 0 } name) {
            text.Append(" \"").Append(name).Append('"');
        }

        if (element.AccessibleValue is { } value) {
            text.Append(" = \"").Append(value).Append('"');
        }

        // ⚠ Not `AccessibleState`, and the omission is deliberate: `Focused` would make every
        // snapshot depend on where the focus happened to be when the test rendered it, and
        // `Focusable` would put the word on two lines in three. A test that cares about either
        // asserts on `AccessibleState` directly, where it is a fact rather than a line of prose.
        var states = element.AccessibleState
            & ~AccessibleStates.Focused
            & ~AccessibleStates.Focusable;

        if (states == AccessibleStates.None) {
            return;
        }

        text.Append(" [");
        var first = true;

        foreach (var state in Enum.GetValues<AccessibleStates>()) {
            if (state == AccessibleStates.None || (states & state) == 0) {
                continue;
            }

            if (!first) {
                text.Append(' ');
            }

            text.Append(state.ToString().ToLowerInvariant());
            first = false;
        }

        text.Append(']');
    }

    /// <summary>The ARIA token for a role.</summary>
    /// <remarks>
    ///     Lowercasing the member name, with no table beside it — see <see cref="AccessibleRole.Img" />
    ///     for why that is a rule the enum keeps rather than a coincidence this relies on.
    /// </remarks>
    static string Token(AccessibleRole role) => role.ToString().ToLowerInvariant();
}

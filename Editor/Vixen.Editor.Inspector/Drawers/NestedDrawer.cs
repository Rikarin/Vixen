// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Ui;
using Vixen.Ui.Controls;

namespace Vixen.Editor.Inspector.Drawers;

/// <summary>The controls a composite drawer keeps between its build and its refresh.</summary>
/// <remarks>
///     ⚠ <b>State on the element rather than on the drawer.</b> A drawer is registered once and drawn
///     for every row of every inspector in the process, so a field on it would be shared between two
///     panels showing two different objects. The element is per row, which is what the value being
///     edited is.
/// </remarks>
public sealed partial class CompositeEditor : Control {
    /// <inheritdoc />
    protected override string TagName => "composite-editor";

    /// <inheritdoc />
    protected override bool AcceptsFocus => false;

    /// <summary>The foldout the members are under.</summary>
    public Expander Fold { get; internal set; } = null!;

    /// <summary>The rows, one per member or element.</summary>
    public List<InspectorRow> Rows { get; } = [];

    /// <summary>
    ///     One working value per outer target, which the child rows are bound to and which is written
    ///     back through the outer member.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>Mutated in place rather than replaced.</b> An <see cref="InspectorField" /> holds the
    ///     list it was constructed with, so refreshing after an undo has to put the newly-read values
    ///     into <i>this</i> list — handing the child fields a new one would leave them editing the
    ///     values the object had before the undo.
    /// </remarks>
    public List<object> Working { get; } = [];
}

/// <summary>A member whose own type has members: drawn as a foldout of rows.</summary>
/// <remarks>
///     <para>
///         <b>The reason an inspector stops at one level otherwise.</b> A component holding a
///         <c>Bounds</c>, a settings object holding a nested block of options, a material holding a
///         sampler description — each was previously one line of <c>ToString</c> from
///         <see cref="ReadOnlyDrawer" />, which is the last resort saying it has nothing better.
///     </para>
///     <para>
///         ⚠ <b>Only a class, and that is the generator's rule rather than this drawer's.</b>
///         <c>VXI0103</c> refuses <c>[Inspector]</c> on a member of a value type, because
///         <c>InspectorMember&lt;TOwner, TValue&gt;</c> constrains its owner to a class — so a struct
///         never has a descriptor and this drawer never claims one. What that costs is real and is
///         worth naming: a <c>Bounds</c>, a <c>Rect</c> or a settings block declared as a struct is
///         still drawn by the last resort. Lifting it means a boxed owner and a write-back through
///         the outer member, which is a change to the descriptor layer rather than to a drawer.
///     </para>
///     <para>
///         Because the member is a reference, the child rows edit the object itself and are handed
///         the document — so the edit is undoable under the nested type's own name rather than as a
///         replacement of the whole object.
///     </para>
///     <para>
///         ⚠ <b>Depth is bounded.</b> A type that contains itself — directly, or through two types
///         that name each other — would otherwise build rows until the stack ran out, at the moment
///         somebody selected one.
///     </para>
/// </remarks>
public sealed class NestedDrawer : IPropertyDrawer {
    /// <summary>How far down a chain of nested types the rows are built.</summary>
    /// <remarks>
    ///     Six, which is deeper than any real settings object and shallow enough that a cycle costs
    ///     six foldouts rather than a crash. Below it the last resort draws the value as text, which
    ///     is what a member nothing can edit already gets.
    /// </remarks>
    public const int MaxDepth = 6;

    /// <summary>How deep the current build is, counted across the recursion.</summary>
    /// <remarks>
    ///     ⚠ <b>An instance field on a shared drawer, which is safe only because a build is
    ///     synchronous and the document tree is single-threaded.</b> It is incremented and decremented
    ///     around the recursive call in <see cref="Build" /> and is zero between rows.
    /// </remarks>
    int depth;

    /// <summary>Which drawer edits which member, for the rows this builds.</summary>
    /// <remarks>
    ///     ⚠ <b>The registry this was registered in, not <see cref="DrawerRegistry.Default" />.</b> A
    ///     test with a registry of its own, or a game that replaced the colour drawer, must see its
    ///     own drawers inside a nested object as well as outside one. Null means the default, which is
    ///     what a drawer constructed by hand and never registered gets.
    /// </remarks>
    public DrawerRegistry? Drawers { get; set; }

    /// <inheritdoc />
    public bool CanDraw(InspectorMember member) {
        ArgumentNullException.ThrowIfNull(member);

        // ⚠ Not a string, and not an enum. Both would find members — `String.Length`, an enum's
        // underlying value — and both already have a drawer that is the right one. This only claims
        // types somebody described on purpose.
        return depth < MaxDepth && InspectorRegistry.Find(member.MemberType) is not null;
    }

    /// <inheritdoc />
    public UiElement Build(InspectorField field, UiElement parent) {
        ArgumentNullException.ThrowIfNull(field);
        ArgumentNullException.ThrowIfNull(parent);

        var editor = parent.Add<CompositeEditor>();

        // ⚠ The outer row's own label is hidden by the theme when this class is on it. A nested
        // object does not fit in the editor column beside a label — it is a block of rows — so the
        // foldout carries the name and the row spans.
        if (parent.Parent is InspectorRow outer) {
            outer.AddClass("nested");
        }

        editor.Fold = editor.Add<Expander>();
        editor.Fold.Label = field.Member.DisplayName;
        editor.Fold.IsExpanded = true;

        if (InspectorRegistry.Find(field.Member.MemberType) is not { } descriptor) {
            return editor;
        }

        Fill(editor, field);

        if (editor.Working.Count == 0) {
            // Every target holds null, which for a class member is a real state and not an error.
            var empty = editor.Fold.Content.Add<TextBlock>();

            empty.AddClass("property-readonly");
            empty.Text = "None";

            return editor;
        }

        depth++;

        try {
            foreach (var member in descriptor.Members) {
                // The document goes down, so a nested edit is one step on the same stack under the
                // nested member's own name — "Set Author" rather than "Set Credit".
                var child = new InspectorField(descriptor, member, editor.Working, field.Document, field.Prefab);

                var row = InspectorRows.Add(
                    editor.Fold.Content,
                    child,
                    Drawers ?? DrawerRegistry.Default,
                    made => child.Changed += _ => InspectorRows.Restate(made)
                );

                if (row is not null) {
                    editor.Rows.Add(row);
                }
            }
        } finally {
            depth--;
        }

        return editor;
    }

    /// <inheritdoc />
    public void Show(InspectorField field, UiElement editor) {
        ArgumentNullException.ThrowIfNull(field);

        if (editor is not CompositeEditor composite) {
            return;
        }

        // ⚠ The working values are re-read rather than trusted. An undo, a gizmo drag or a paste
        // changes the outer member behind these rows, and a foldout still showing the box it was
        // built with is one whose numbers stop agreeing with the object the moment anything else
        // touches it.
        Fill(composite, field);

        foreach (var row in composite.Rows) {
            InspectorRows.Show(row);
        }
    }

    /// <summary>Puts each target's current value into the working list, in place.</summary>
    static void Fill(CompositeEditor editor, InspectorField field) {
        var index = 0;

        foreach (var target in field.Targets) {
            if (field.Member.GetBoxed(target) is not { } value) {
                // A null class member has nothing to draw rows against, and drawing the other
                // objects' rows would be an inspector editing a subset it does not say it is editing.
                editor.Working.Clear();

                return;
            }

            if (index < editor.Working.Count) {
                editor.Working[index] = value;
            } else {
                editor.Working.Add(value);
            }

            index++;
        }

        while (editor.Working.Count > index) {
            editor.Working.RemoveAt(editor.Working.Count - 1);
        }
    }
}

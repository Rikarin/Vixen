// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Editor.Core;
using Vixen.Ui;
using Vixen.Ui.Controls;

namespace Vixen.Editor.Inspector;

/// <summary>The row the default inspector would have drawn, wherever markup asks for one.</summary>
/// <remarks>
///     <para>
///         <b>Doc 36 § P4's <c>PropertyField</c>, and the reason an inspector can be markup at
///         all.</b> Nine rows out of ten in a hand-written inspector are "draw this member the way
///         you would have anyway" — a slider for a float with a range, a colour well for a
///         <c>Color4</c>, a tick for a bool. Writing each of those out is what makes people not write
///         custom inspectors; naming the member and getting the row is what makes them.
///     </para>
///     <para>
///         ⚠ <b>Empty until it is bound, which is Unity's rule and is not an implementation
///         detail.</b> A markup tree is built by a <c>Build</c> body that knows nothing about what is
///         being edited — it cannot, or the markup would have to name a C# type — so the join happens
///         afterwards, in <see cref="MarkupBinding.Bind" />. A <c>PropertyField</c> that is never
///         bound draws nothing and says why in the panel rather than throwing.
///     </para>
///     <para>
///         ⚠ <b>It builds an <c>InspectorRow</c> inside itself rather than being one.</b> An
///         <c>InspectorRow</c> is what a drawer is handed and what the reset button and the override
///         bar hang off; making this a subclass would mean markup could produce a row with no field
///         in it, which every one of those parts would then have to tolerate.
///     </para>
/// </remarks>
public sealed class PropertyField : Control {
    /// <inheritdoc />
    protected override string TagName => "property-field";

    /// <inheritdoc />
    protected override bool AcceptsFocus => false;

    /// <summary>Which member it shows. Set from markup, read when the tree is bound.</summary>
    /// <remarks>
    ///     ⚠ <b>An ordinary parameter rather than <c>binding-path</c>.</b> This control's whole
    ///     subject is a member, so the path is a property of it — where on a <c>&lt;Slider&gt;</c> the
    ///     same idea has to be an attribute, because a slider has no such property and every control
    ///     would need one.
    /// </remarks>
    public string? Path { get; set; }

    /// <summary>The row that was built, or <see langword="null" /> before binding.</summary>
    public InspectorRow? Row { get; private set; }

    /// <summary>Fills it in against an edit target.</summary>
    /// <param name="target">What is being edited.</param>
    /// <param name="drawers">Which drawer edits which member.</param>
    /// <returns>Whether a row was built.</returns>
    internal bool Bind(EditTarget target, DrawerRegistry drawers) {
        while (Children.Count > 0) {
            Children[^1].Remove();
        }

        Row = null;

        if (Path is not { Length: > 0 } path) {
            return Explain("A <PropertyField> with no Path has no member to draw.");
        }

        if (target.Find(path) is not InspectorField field) {
            // ⚠ Named rather than silent, and this is the mistake markup makes. A path is a string
            // the compiler never sees — a renamed member leaves a `binding-path` that resolves to
            // nothing, and a row that quietly vanishes reads as the value being unset.
            return Explain($"'{path}' is not a member of {target.CommonType?.Name ?? "the selection"}.");
        }

        // The row restates itself from whatever wrote the member — its own drawer, a paste, a gizmo
        // — which is what the generated rows do and is the difference between drawing the same row
        // and being the same row. Without it a markup row keeps the reset button and the override
        // bar it happened to have at the moment it was built, so a value edited away from its
        // default never grows the button that puts it back.
        Row = InspectorRows.Add(this, field, drawers, made => Restating(field, made));

        return Row is not null || Explain($"Nothing can draw '{path}'.");
    }

    /// <summary>Keeps one row's two marks in step with the member it draws.</summary>
    /// <remarks>
    ///     ⚠ <b>The subscription outlives the row, which is why it prunes itself.</b> The field is
    ///     cached on the <see cref="EditTarget" /> and the row is not: re-binding — a second
    ///     <see cref="MarkupBinding.Bind" /> over one target, or the re-bind a hot reload performs
    ///     after it has thrown the elements away — leaves the previous row's handler on a field that
    ///     is still very much alive. Restating a removed element throws from inside somebody's edit,
    ///     which is a worse outcome than the stale button this exists to fix, and a handler that
    ///     merely returned early would still accumulate one per reload for the life of the selection.
    /// </remarks>
    static void Restating(InspectorField field, InspectorRow row) {
        field.Changed += Restate;

        void Restate(EditProperty property) {
            if (row.IsRemoved) {
                property.Changed -= Restate;
                return;
            }

            InspectorRows.Restate(row);
        }
    }

    bool Explain(string message) {
        var text = Add<TextBlock>();

        text.AddClass("property-readonly");
        text.Text = message;

        return false;
    }
}

/// <summary>Joining a built markup tree to what it is editing.</summary>
/// <remarks>
///     <para>
///         <b>Doc 36 § P4, and it is Unity's arrangement exactly.</b> A custom inspector clones a
///         tree and returns it; binding happens <i>after</i> the method returns, by walking what was
///         built and wiring each element that named a member. The markup does not know about the C#
///         and the C# does not know about the markup — the two are joined by name, which is the whole
///         reason a <c>.vxml</c> inspector needs no code-behind.
///     </para>
///     <para>
///         ⚠ <b>Two ways to name a member, because they answer different questions.</b>
///         <c>&lt;PropertyField Path="Speed" /&gt;</c> says "draw this the way you would have";
///         <c>&lt;Slider binding-path="Speed" /&gt;</c> says "I have chosen the control, join it up".
///         An inspector that only had the first could not lay anything out; one that only had the
///         second would make every author reimplement the default row.
///     </para>
///     <para>
///         ⚠ <b>The walk stops at a nested <c>PropertyField</c> and nowhere else.</b> A drawer builds
///         its own elements inside the row, and some of them are the very controls this binds — a
///         second pass over them would join a colour well's red channel to whatever member the
///         enclosing field named.
///     </para>
/// </remarks>
public static class MarkupBinding {
    /// <summary>Joins every element under a root that named a member.</summary>
    /// <param name="root">What was built.</param>
    /// <param name="target">What is being edited.</param>
    /// <param name="drawers">Which drawer edits which member, or <see langword="null" /> for the default.</param>
    /// <returns>How many elements were bound.</returns>
    public static int Bind(UiElement root, EditTarget target, DrawerRegistry? drawers = null) {
        ArgumentNullException.ThrowIfNull(root);
        ArgumentNullException.ThrowIfNull(target);

        return Walk(root, target, drawers ?? DrawerRegistry.Default);
    }

    static int Walk(UiElement element, EditTarget target, DrawerRegistry drawers) {
        if (element is PropertyField field) {
            return field.Bind(target, drawers) ? 1 : 0;
        }

        var bound = element.Attribute("binding-path") is { Length: > 0 } path && Join(element, target, path) ? 1 : 0;

        // ⚠ A snapshot, because building a row can add an element to something this walk has not
        // reached yet. A colour drawer hangs its popover off the document root — overlays are the
        // root's children wherever the control that owns them sits — so walking the live list threw
        // "collection was modified" the first time an inspector with a colour in it was bound.
        foreach (var child in element.Children.ToArray()) {
            bound += Walk(child, target, drawers);
        }

        return bound;
    }

    /// <summary>Wires one control to one member, both ways.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>A table of the controls an inspector is actually made of, rather than a general
    ///         mechanism.</b> A control's "value" is a differently-named property of a different type
    ///         on each of them, and the alternative to naming them is reflection over property names
    ///         — which ADR-002 forbids and which would turn a typo into a control that silently does
    ///         nothing. A control that is not here is one a <c>&lt;PropertyField&gt;</c> covers.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The read is inside <see cref="EditProperty.Refreshing" />.</b> Assigning to a
    ///         control raises its own changed event, so a field showing a mixed selection would write
    ///         the neutral position it parked at to every selected object the moment it was drawn.
    ///     </para>
    /// </remarks>
    static bool Join(UiElement element, EditTarget target, string path) {
        if (target.Find(path) is not { } property) {
            return false;
        }

        switch (element) {
            case NumericInput number:
                Show(property, () => number.Number = property.Read().Or(0d));
                number.NumberChanged += (_, value) => Write(property, value);
                return true;

            case Slider slider:
                Show(property, () => slider.Value = property.Read().Or(0f));
                slider.ValueChanged += (_, value) => Write(property, value);
                return true;

            case CheckBox check:
                Show(property, () => check.IsChecked = property.Read().Or(false));
                check.CheckedChanged += (_, value) => property.Write(value);
                return true;

            case Switch toggle:
                Show(property, () => toggle.IsChecked = property.Read().Or(false));
                toggle.CheckedChanged += (_, value) => property.Write(value);
                return true;

            case TextField text:
                Show(property, () => text.Value = property.Read().Or(string.Empty));
                text.ValueChanged += (_, value) => property.Write(value ?? string.Empty);
                return true;

            // ⚠ Last, and read-only. A `TextBlock` is what an author reaches for to *show* a member
            // beside the one they are editing — a name in a header, a count in a caption — and it has
            // no way to write one back, so binding it is a display and not an editor.
            case TextBlock label:
                Show(property, () => label.Text = property.Read().Value?.ToString() ?? string.Empty);
                return true;

            default:
                return false;
        }
    }

    /// <summary>Puts the value into the control now, and again whenever it changes elsewhere.</summary>
    static void Show(EditProperty property, Action show) {
        using (property.Refreshing()) {
            show();
        }

        property.Changed += _ => {
            using (property.Refreshing()) {
                show();
            }
        };
    }

    /// <summary>Writes a number back as the member's own type.</summary>
    /// <remarks>
    ///     ⚠ <b>A slider is a <c>float</c> and a numeric input is a <c>double</c>, and the member is
    ///     whichever it is.</b> Writing the control's type straight through put a boxed <c>double</c>
    ///     on a <c>float</c> field, which the setter rejects at run time — a row that appears to work
    ///     and changes nothing.
    /// </remarks>
    static void Write(EditProperty property, double value) =>
        property.Write(Convert.ChangeType(value, property.Member.ValueType, System.Globalization.CultureInfo.InvariantCulture));
}

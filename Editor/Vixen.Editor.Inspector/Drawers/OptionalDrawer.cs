// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Reflection;
using Vixen.Ui;
using Vixen.Ui.Controls;

namespace Vixen.Editor.Inspector.Drawers;

/// <summary>A checkbox and an editor, for a member that may have no value at all.</summary>
/// <remarks>
///     ⚠ <b>State on the element rather than on the drawer</b>, the reason
///     <see cref="CompositeEditor" /> gives: a drawer is registered once and drawn for every row of
///     every inspector in the process.
/// </remarks>
public sealed partial class OptionalEditor : Control {
    /// <inheritdoc />
    protected override string TagName => "optional-editor";

    /// <inheritdoc />
    protected override bool AcceptsFocus => false;

    /// <summary>Whether the member has a value.</summary>
    public CheckBox Toggle { get; internal set; } = null!;

    /// <summary>The editor for the value itself, built by whichever drawer owns the inner type.</summary>
    public UiElement? Inner { get; internal set; }

    /// <summary>That drawer, so refreshing can reach it.</summary>
    public IPropertyDrawer? Drawer { get; internal set; }
}

/// <summary>
///     A member whose type is <see cref="Nullable{T}" />: a checkbox that turns the value on, and the
///     inner type's own editor beside it.
/// </summary>
/// <remarks>
///     <para>
///         <b>Because "unset" and "zero" are different states, and only one of them is visible
///         without this.</b> Every optional member in the engine — a post-process volume's settings
///         are twenty of them — means "I have no opinion" by being null, and a plain number box shows
///         null as <c>0</c>. That is the worst available answer: it reads as an override to black, to
///         no bloom, to zero saturation. Unreal pairs every one of its post-process properties with a
///         checkbox for exactly this reason.
///     </para>
///     <para>
///         ⚠ <b>Unticking writes null; it does not write a default.</b> The two are indistinguishable
///         in a number box and completely different in a fold — a volume that says "bloom is 0 here"
///         suppresses the glow, and one that says nothing lets the room underneath decide.
///     </para>
///     <para>
///         ⚠ <b>Ticking writes the inner type's neutral value rather than leaving null.</b> Otherwise
///         the box turns on, the editor beside it stays disabled-looking and nothing happens until
///         the user also types a number — a control that appears not to work on first click. What
///         "neutral" is belongs to the type; see <see cref="Fresh" />.
///     </para>
///     <para>
///         <b>The inner editor is whatever the registry has for the underlying type</b>, so a
///         <c>float?</c> gets the number drawer with its <c>[Range]</c> slider and a
///         <c>Vector3?</c> gets the three-field vector editor. This drawer contributes the checkbox
///         and nothing else, which is what keeps it one file rather than one per optional type.
///     </para>
/// </remarks>
/// <param name="drawers">Where the inner type's drawer comes from.</param>
public sealed class OptionalDrawer(DrawerRegistry drawers) : IPropertyDrawer {
    /// <inheritdoc />
    public bool CanDraw(InspectorMember member) {
        ArgumentNullException.ThrowIfNull(member);

        return Nullable.GetUnderlyingType(member.MemberType) is not null;
    }

    /// <inheritdoc />
    public UiElement Build(InspectorField field, UiElement parent) {
        ArgumentNullException.ThrowIfNull(field);
        ArgumentNullException.ThrowIfNull(parent);

        var editor = parent.Add<OptionalEditor>();

        editor.Toggle = editor.Add<CheckBox>();
        editor.Toggle.Disabled = !field.CanWrite;

        editor.Toggle.CheckedChanged += (_, on) => {
            // ⚠ Ticking supplies a value rather than leaving null, and unticking clears rather than
            // zeroing. See the type's remarks: both directions are a state somebody meant.
            if (field.Write(on ? Fresh(field) : null)) {
                field.Seal();
            }
        };

        // ⚠ Resolved against the *underlying* type, and this drawer is excluded by construction:
        // `Nullable.GetUnderlyingType(float)` is null, so `CanDraw` refuses and the registry cannot
        // hand the inner slot back to this drawer and recurse for ever.
        if (Nullable.GetUnderlyingType(field.Member.MemberType) is { } inner) {
            editor.Drawer = drawers.Resolve(inner, field.Member);
            editor.Inner = editor.Drawer?.Build(field, editor);
        }

        return editor;
    }

    /// <inheritdoc />
    public void Show(InspectorField field, UiElement editor) {
        ArgumentNullException.ThrowIfNull(field);

        if (editor is not OptionalEditor optional) {
            return;
        }

        var value = field.Read();

        optional.Toggle.IsIndeterminate = value.IsMixed;
        optional.Toggle.IsChecked = !value.IsMixed && value.Value is not null;

        if (optional.Inner is not { } control) {
            return;
        }

        // ⚠ The inner editor is greyed rather than hidden when the value is unset. Hidden makes the
        // row jump as the box is ticked and gives no clue what turning it on would produce; a
        // disabled-looking control shows the number that is about to become real.
        //
        // `Disabled` is `Control`'s rather than `UiElement`'s, and an inner drawer is free to return
        // something that is not a control — a composite, a panel of three fields. Those keep their
        // own enabled state, which is the honest outcome: the checkbox is still the thing that says
        // whether the value exists.
        if (control is Control inner) {
            inner.Disabled = !field.CanWrite || value.IsMixed || value.Value is null;
        }

        optional.Drawer?.Show(field, control);
    }

    /// <summary>What ticking the box supplies.</summary>
    /// <remarks>
    ///     <para>
    ///         The inner type's declared neutral value when it has one, and its zero otherwise. For a
    ///         scalar the zero is a value with a meaning — a bloom intensity of nothing, a neutral hue
    ///         shift — but for a struct the zero can be the trap the engine's <c>Neutral</c> convention
    ///         exists to route around: <c>default(ColorGrading)</c> is a saturation of zero and a gain
    ///         of zero, so ticking "I have an opinion" would supply a black greyscale frame.
    ///     </para>
    ///     <para>
    ///         ⚠ <b><c>Neutral</c> before <c>Default</c></b>, because a type declaring both means
    ///         different things by them — "changes nothing" is what a value that has just been turned
    ///         on should say, and a member wanting something else still has <c>[Range]</c>'s minimum
    ///         or a default the descriptor knows, which <see cref="InspectorField.Reset" /> reads.
    ///     </para>
    /// </remarks>
    static object? Fresh(InspectorField field) {
        if (Nullable.GetUnderlyingType(field.Member.MemberType) is not { } inner) {
            return null;
        }

        return Declared(inner, "Neutral") ?? Declared(inner, "Default") ?? Activator.CreateInstance(inner);
    }

    /// <summary>The value of the type's own static <paramref name="name" /> property, if it has one.</summary>
    /// <remarks>
    ///     Only a property of the type itself counts: <c>Neutral</c> returning something else is a
    ///     coincidence of naming, not the convention this reads.
    /// </remarks>
    static object? Declared(Type inner, string name) =>
        inner.GetProperty(name, BindingFlags.Public | BindingFlags.Static) is { GetMethod: not null } property
        && property.PropertyType == inner
            ? property.GetValue(null)
            : null;
}

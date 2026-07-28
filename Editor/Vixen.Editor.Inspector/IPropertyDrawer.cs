// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Ui;

namespace Vixen.Editor.Inspector;

/// <summary>Turns one member into controls, and controls back into a value.</summary>
/// <remarks>
///     <para>
///         <b>Two methods, and the split is the whole design.</b> <see cref="Build" /> runs once and
///         makes the elements; <see cref="Show" /> runs whenever the value may have changed and puts
///         numbers into elements that already exist. A gizmo dragging an object calls the second one
///         forty times a second, and a drawer that rebuilt would take the focus out of whatever the
///         user was typing into.
///     </para>
///     <para>
///         <b>A drawer never touches an undo stack.</b> It calls <see cref="InspectorField.Write" />
///         and <see cref="InspectorField.Seal" />, which is what makes "every edit produces a command"
///         true by construction rather than by every drawer remembering to do it.
///     </para>
/// </remarks>
public interface IPropertyDrawer {
    /// <summary>Whether this drawer can edit a member.</summary>
    /// <param name="member">The member.</param>
    /// <returns>Whether it can.</returns>
    /// <remarks>
    ///     Consulted after the registry has already matched by type or by attribute, so this is the
    ///     drawer's own last word — a colour drawer registered for <c>Color4</c> refusing an HDR
    ///     usage it cannot render, rather than a drawer re-deciding what it was registered for.
    /// </remarks>
    bool CanDraw(InspectorMember member) => true;

    /// <summary>Makes the controls, once.</summary>
    /// <param name="field">The member, bound to what is being inspected.</param>
    /// <param name="parent">Where the controls go.</param>
    /// <returns>The element <see cref="Show" /> will be handed back.</returns>
    UiElement Build(InspectorField field, UiElement parent);

    /// <summary>Puts the current value into controls that already exist.</summary>
    /// <param name="field">The member, bound to what is being inspected.</param>
    /// <param name="editor">What <see cref="Build" /> returned.</param>
    void Show(InspectorField field, UiElement editor);
}

/// <summary>A drawer for one value type, with the cast done once.</summary>
/// <typeparam name="TValue">What it edits.</typeparam>
/// <typeparam name="TEditor">The element it builds.</typeparam>
/// <remarks>
///     Convenience, not a different contract: it exists so that a custom drawer is three short
///     methods rather than three short methods plus two casts and a null check each.
/// </remarks>
public abstract class PropertyDrawer<TValue, TEditor> : IPropertyDrawer where TEditor : UiElement {
    /// <inheritdoc />
    public virtual bool CanDraw(InspectorMember member) {
        ArgumentNullException.ThrowIfNull(member);

        return member.MemberType == typeof(TValue);
    }

    /// <summary>Makes the controls, once.</summary>
    /// <param name="field">The member, bound to what is being inspected.</param>
    /// <param name="parent">Where the controls go.</param>
    /// <returns>The editor element.</returns>
    protected abstract TEditor Build(InspectorField field, UiElement parent);

    /// <summary>Puts the current value into the controls.</summary>
    /// <param name="field">The member, bound to what is being inspected.</param>
    /// <param name="editor">The editor element.</param>
    /// <param name="value">What every target holds, or nothing when they disagree.</param>
    /// <param name="isMixed">Whether the targets disagree.</param>
    protected abstract void Show(InspectorField field, TEditor editor, TValue? value, bool isMixed);

    UiElement IPropertyDrawer.Build(InspectorField field, UiElement parent) => Build(field, parent);

    void IPropertyDrawer.Show(InspectorField field, UiElement editor) {
        ArgumentNullException.ThrowIfNull(field);

        var (value, mixed) = field.Read();
        Show(field, (TEditor) editor, mixed ? default : (TValue?) value, mixed);
    }
}

// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Diagnostics.CodeAnalysis;

namespace Vixen.Ui;

public partial class UiElement {
    Dictionary<Type, object>? ambient;

    /// <summary>Makes a value available to everything inside this element.</summary>
    /// <typeparam name="T">What it is to be found as. <b>The key is this type and nothing else.</b></typeparam>
    /// <param name="value">The value.</param>
    /// <remarks>
    ///     <para>
    ///         <b>SwiftUI's <c>Environment</c>, with the walk written down.</b> Every cross-cutting
    ///         value in this tree — a theme, a selection, a view-model, a service — was threaded
    ///         through props by hand, which is why <c>Samples/02-HelloUi/Shell.vxml</c> repeats
    ///         <c>Model="@Model"</c> on three panels in a row. A value provided here is found by
    ///         <see cref="Inject{T}" /> from any descendant, however deep.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The key is the type argument, not the value's runtime type.</b>
    ///         <c>Provide&lt;ITheme&gt;(new DarkTheme())</c> is found by
    ///         <c>Inject&lt;ITheme&gt;</c> and <i>not</i> by <c>Inject&lt;DarkTheme&gt;</c>, which
    ///         is what makes an interface the useful key and what stops a subclass silently
    ///         shadowing its base. It is also why this is a generic method rather than one taking
    ///         <c>object</c>: the key would otherwise be whatever the caller happened to construct.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Not the cascade, and not <c>[UiProperty(Inherits = true)]</c>.</b> That
    ///         attribute's generated walk tests <c>ancestor is TOwner</c>, so it inherits down one
    ///         kind of element and is CSS inheritance wearing a C# name — its only producers in the
    ///         whole tree are three test fixtures. This is keyed by an arbitrary type, reaches
    ///         everything below, and carries a whole object rather than a styleable value.
    ///     </para>
    /// </remarks>
    public void Provide<T>(T value) where T : notnull {
        ArgumentNullException.ThrowIfNull(value);

        (ambient ??= [])[typeof(T)] = value;
    }

    /// <summary>Takes a provided value back off this element.</summary>
    /// <typeparam name="T">The key it was provided under.</typeparam>
    /// <returns>Whether this element was providing one.</returns>
    /// <remarks>
    ///     ⚠ <b>What it reveals is the next one up, not nothing.</b> A panel that overrode the
    ///     application's theme and then stopped goes back to the application's, which is the whole
    ///     point of a walk — and is why this is a removal rather than an assignment of null.
    /// </remarks>
    public bool Unprovide<T>() => ambient?.Remove(typeof(T)) == true;

    /// <summary>Whether this element itself provides one, ignoring its ancestors.</summary>
    /// <typeparam name="T">The key.</typeparam>
    public bool Provides<T>() => ambient?.ContainsKey(typeof(T)) == true;

    /// <summary>The nearest provided value of that type, looking up from here.</summary>
    /// <typeparam name="T">The key it was provided under.</typeparam>
    /// <returns>The value, or <see langword="null" /> if nothing on the way up provides one.</returns>
    /// <remarks>
    ///     ⚠ <b>This element first, then its ancestors, then the document.</b> An element that
    ///     provides a value can read its own — a panel that overrides the theme for its subtree is
    ///     inside that subtree — and a component reading one it provided itself getting the
    ///     application's instead would be the surprising answer.
    /// </remarks>
    public T? Inject<T>() => TryInject<T>(out var value) ? value : default;

    /// <summary>The same, told apart from a provided <c>null</c>-shaped default.</summary>
    /// <typeparam name="T">The key.</typeparam>
    /// <param name="value">The value, if one was found.</param>
    /// <returns>Whether anything on the way up provides one.</returns>
    /// <remarks>
    ///     ⚠ <b>Walked on every ask rather than cached</b>, for <see cref="FindUndoManager" />'s
    ///     reason and <see cref="FindEditedDocument" />'s: an element is reparented, a panel is torn
    ///     off into its own window, and a cached answer is the one that was nearest when the control
    ///     was built. It is the same walk those two make and is deliberately the same shape — the
    ///     nearest declaration wins, and the document is the last word.
    /// </remarks>
    public bool TryInject<T>([NotNullWhen(true)] out T? value) {
        for (var element = this; element is not null; element = element.Parent) {
            if (element.ambient?.TryGetValue(typeof(T), out var found) == true && found is T typed) {
                value = typed;
                return true;
            }
        }

        if (document?.TryInject<T>(out var wide) == true) {
            value = wide;
            return true;
        }

        value = default;
        return false;
    }
}

public sealed partial class UiDocument {
    Dictionary<Type, object>? ambient;

    /// <summary>Makes a value available to the whole document.</summary>
    /// <typeparam name="T">The key.</typeparam>
    /// <param name="value">The value.</param>
    /// <remarks>
    ///     The application's answer, and the last one any walk reaches. Anything an element provides
    ///     wins over it inside that element, which is what makes a preview pane able to show one
    ///     theme while the application is in another.
    /// </remarks>
    public void Provide<T>(T value) where T : notnull {
        ArgumentNullException.ThrowIfNull(value);

        (ambient ??= [])[typeof(T)] = value;
    }

    /// <summary>Takes a document-wide value back.</summary>
    /// <typeparam name="T">The key.</typeparam>
    /// <returns>Whether there was one.</returns>
    public bool Unprovide<T>() => ambient?.Remove(typeof(T)) == true;

    /// <summary>The document-wide value of that type.</summary>
    /// <typeparam name="T">The key.</typeparam>
    /// <returns>The value, or <see langword="null" />.</returns>
    public T? Inject<T>() => TryInject<T>(out var value) ? value : default;

    /// <summary>The same, told apart from nothing.</summary>
    /// <typeparam name="T">The key.</typeparam>
    /// <param name="value">The value, if there is one.</param>
    /// <returns>Whether there is.</returns>
    public bool TryInject<T>([NotNullWhen(true)] out T? value) {
        if (ambient?.TryGetValue(typeof(T), out var found) == true && found is T typed) {
            value = typed;
            return true;
        }

        value = default;
        return false;
    }
}

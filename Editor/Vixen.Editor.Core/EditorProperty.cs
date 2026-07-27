// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Ui.Reactive;

namespace Vixen.Editor.Core;

/// <summary>One editable value, without knowing what type it holds.</summary>
/// <remarks>
///     What a generic inspector, a property-search box or a copy-paste-property command sees. Reading
///     through <see cref="BoxedValue" /> costs an allocation for a struct, which is why it is the
///     un-generic path and not the one a drawer generated for a known type takes.
/// </remarks>
public interface IEditorProperty {
    /// <summary>What the member is called.</summary>
    string Name { get; }

    /// <summary>What it holds.</summary>
    Type ValueType { get; }

    /// <summary>The object it belongs to.</summary>
    EditorObject Owner { get; }

    /// <summary>The current value, boxed. Records a dependency like any other read.</summary>
    object? BoxedValue { get; }
}

/// <summary>A value the editor edits: a signal, plus the fact that writing it is undoable.</summary>
/// <typeparam name="T">What it holds.</typeparam>
/// <remarks>
///     <para>
///         <b>The object model is signal-backed, and this is where that pays off outside the UI
///         framework.</b> The inspector binds to the property; a gizmo drag writes it; the inspector
///         updates. There is no change event to raise, no listener list to unsubscribe from on tab
///         close, and no path by which the two views of one value disagree.
///     </para>
///     <para>
///         <b>The value is read-only through the signal and written through <see cref="Set" />.</b>
///         That asymmetry is the point: a write goes to the owning document's command stack, so
///         "every edit produces a command" is true by construction rather than by every drawer
///         remembering to do it.
///     </para>
/// </remarks>
public sealed class EditorProperty<T> : IReadOnlySignal<T>, IEditorProperty {
    readonly IEqualityComparer<T> comparer;
    readonly Signal<T> signal;

    /// <inheritdoc />
    public string Name { get; }

    /// <inheritdoc />
    public EditorObject Owner { get; }

    /// <inheritdoc />
    public Type ValueType => typeof(T);

    /// <summary>Whether consecutive edits to this property collapse into one undo step.</summary>
    /// <remarks>
    ///     On, because the property this exists for is a slider. Off for a value where two edits in a
    ///     row are two decisions — a dropdown, an object reference — and collapsing them would take
    ///     away an undo the user is entitled to.
    /// </remarks>
    public bool CoalescesEdits { get; }

    /// <inheritdoc />
    public T Value => signal.Value;

    /// <inheritdoc />
    object? IEditorProperty.BoxedValue => signal.Value;

    internal EditorProperty(
        EditorObject owner,
        string name,
        T initial,
        IEqualityComparer<T>? comparer,
        bool coalescesEdits
    ) {
        Owner = owner;
        Name = name;
        CoalescesEdits = coalescesEdits;
        this.comparer = comparer ?? EqualityComparer<T>.Default;
        signal = new(initial, this.comparer);
    }

    /// <inheritdoc />
    public T Peek() => signal.Peek();

    /// <summary>Changes the value, undoably.</summary>
    /// <param name="value">The new value.</param>
    /// <remarks>
    ///     Writing the value it already holds does nothing and records nothing — a slider that
    ///     re-emits its position every frame of a drag it is not moving in leaves no trace. On an
    ///     object whose owner has no document there is no stack to record on, so the write happens
    ///     and is not undoable; that is the case where a property is being used as plain state.
    /// </remarks>
    public void Set(T value) {
        if (comparer.Equals(signal.Peek(), value)) {
            return;
        }

        if (Owner.Document?.Stack is not { } stack) {
            signal.Value = value;
            return;
        }

        stack.Execute(new SetPropertyCommand<T>(this, signal.Peek(), value));
    }

    internal void Assign(T value) => signal.Value = value;
}

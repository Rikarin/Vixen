// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Diagnostics.CodeAnalysis;
using Vixen.Ui.Reactive;

namespace Vixen.Editor.Core;

/// <summary>Something with editable properties, belonging to a document.</summary>
/// <remarks>
///     <para>
///         Derive from it and declare the properties in the constructor:
///     </para>
///     <code>
///     sealed class MaterialObject : EditorObject {
///         public EditorProperty&lt;float&gt; Roughness { get; }
///
///         public MaterialObject(EditorDocument document) : base(document) {
///             Roughness = Property("Roughness", 0.2f);
///         }
///     }
///     </code>
///     <para>
///         The document is what the properties' writes are recorded against, and it is nullable
///         because the same type is useful without one — a preview of an asset nobody is editing, a
///         value bag in a test. What that costs is stated at <see cref="EditorProperty{T}.Set" />:
///         those writes happen and are not undoable.
///     </para>
/// </remarks>
public class EditorObject {
    readonly Dictionary<string, IEditorProperty> byName = new(StringComparer.Ordinal);
    readonly List<IEditorProperty> properties = [];

    /// <summary>Where writes to these properties are recorded, if anywhere.</summary>
    public EditorDocument? Document { get; }

    /// <summary>Its properties, in the order they were declared.</summary>
    /// <remarks>
    ///     Declaration order rather than alphabetical, because an inspector that reorders a type's
    ///     members is an inspector nobody can find anything in.
    /// </remarks>
    public IReadOnlyList<IEditorProperty> Properties => properties;

    /// <summary>Creates an object whose edits go to a document.</summary>
    /// <param name="document">The document, or <see langword="null" /> for one nobody is editing.</param>
    public EditorObject(EditorDocument? document) => Document = document;

    /// <summary>Declares a property.</summary>
    /// <typeparam name="T">What it holds.</typeparam>
    /// <param name="name">What it is called, both in the inspector and in the undo history.</param>
    /// <param name="initial">Its starting value.</param>
    /// <param name="comparer">
    ///     How to decide a write changed nothing. Defaults to the type's own equality; a mutable
    ///     reference type wants <see cref="SignalComparer.Never{T}" /> or it will never look changed.
    /// </param>
    /// <param name="coalescesEdits">Whether consecutive edits collapse into one undo step.</param>
    /// <returns>The property.</returns>
    protected internal EditorProperty<T> Property<T>(
        string name,
        T initial,
        IEqualityComparer<T>? comparer = null,
        bool coalescesEdits = true
    ) {
        ArgumentException.ThrowIfNullOrEmpty(name);

        var property = new EditorProperty<T>(this, name, initial, comparer, coalescesEdits);
        byName.Add(name, property);
        properties.Add(property);
        return property;
    }

    /// <summary>Finds a property by name.</summary>
    /// <param name="name">What it is called.</param>
    /// <param name="property">The property.</param>
    /// <returns>Whether there is one.</returns>
    public bool TryGetProperty(string name, [MaybeNullWhen(false)] out IEditorProperty property) =>
        byName.TryGetValue(name, out property);
}

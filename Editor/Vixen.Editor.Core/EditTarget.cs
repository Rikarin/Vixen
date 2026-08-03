// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Diagnostics.CodeAnalysis;

namespace Vixen.Editor.Core;

/// <summary>What is being edited: some objects, where their edits are recorded, and how to reach their members.</summary>
/// <remarks>
///     <para>
///         <b>Doc 36 § D1's single source of truth, and the thing markup binds against.</b> A panel
///         is handed one of these and asks it for properties by name; it does not know whether the
///         objects are entities, graph nodes, an asset or a settings file, and it does not know how
///         many of them there are. That is what makes one panel work for a selection of one and a
///         selection of twenty, and what will let a <c>.vxml</c> attribute name a member without the
///         markup knowing any C# type.
///     </para>
///     <para>
///         ⚠ <b>One type, not a common base.</b> <see cref="CommonType" /> is null unless every
///         object is exactly the same type. Falling back to whatever a light and a camera both derive
///         from would produce a different set of editors depending on what else happened to be
///         selected; a mixed selection having nothing to edit is honest, and is what every editor
///         that tried the alternative ended up doing.
///     </para>
///     <para>
///         <b>The objects are held, not copied.</b> A selection that changes is a new
///         <see cref="EditTarget" />, because a target whose contents shifted underneath it would
///         make an in-flight drag write to something the user did not aim at.
///     </para>
/// </remarks>
public class EditTarget {
    readonly Dictionary<string, EditProperty> properties = new(StringComparer.Ordinal);

    /// <summary>What is being edited, in selection order.</summary>
    public IReadOnlyList<object> Objects { get; }

    /// <summary>Where the edits are recorded, or <see langword="null" /> for a preview nobody is editing.</summary>
    public EditorDocument? Document { get; }

    /// <summary>How the members are reached.</summary>
    public IEditProvider Provider { get; }

    /// <summary>The type every object is, or <see langword="null" /> when they are not all one.</summary>
    public Type? CommonType { get; }

    /// <summary>Whether there is anything here to edit.</summary>
    public bool IsEmpty => CommonType is null;

    /// <summary>Binds a selection to a provider.</summary>
    /// <param name="objects">What is being edited, in selection order.</param>
    /// <param name="provider">How to reach their members, or <see langword="null" /> for none.</param>
    /// <param name="document">Where edits are recorded, or <see langword="null" />.</param>
    /// <remarks>
    ///     ⚠ <b>Not sealed, and <see cref="Create" /> is the only reason.</b> A surface whose binding
    ///     carries more than the base one — the inspector's reset button, its prefab override — has to
    ///     be able to say what a <c>Find</c> hands back, or it ends up building a second instance
    ///     beside the cached one and subscribing to the wrong <c>Changed</c>.
    /// </remarks>
    public EditTarget(
        IReadOnlyList<object> objects,
        IEditProvider? provider = null,
        EditorDocument? document = null
    ) {
        ArgumentNullException.ThrowIfNull(objects);

        foreach (var target in objects) {
            ArgumentNullException.ThrowIfNull(target, nameof(objects));
        }

        Objects = objects;
        Provider = provider ?? EmptyEditProvider.Instance;
        Document = document;
        CommonType = TypeOf(objects);
    }

    /// <summary>The members every object has, in the order they should be shown.</summary>
    public IReadOnlyList<IEditMember> Members =>
        CommonType is { } type ? Provider.MembersOf(type) : [];

    /// <summary>Binds one member by name.</summary>
    /// <param name="path">What the member is called.</param>
    /// <param name="property">The binding.</param>
    /// <returns>Whether the objects have a member by that name.</returns>
    /// <remarks>
    ///     <b>Cached per name</b>, so a panel that asks for the same member every frame gets the same
    ///     binding — which is what makes <see cref="EditProperty.Changed" /> subscribable at all. A
    ///     fresh instance per call would hand out an event nothing is ever raised on.
    /// </remarks>
    public bool TryFind(string path, [NotNullWhen(true)] out EditProperty? property) {
        ArgumentException.ThrowIfNullOrEmpty(path);

        if (properties.TryGetValue(path, out property)) {
            return true;
        }

        if (CommonType is not { } type || !Provider.TryResolve(type, path, out var member)) {
            property = null;
            return false;
        }

        property = Create(member);
        properties[path] = property;

        return true;
    }

    /// <summary>Makes the binding for one member.</summary>
    /// <param name="member">The member, already resolved by the provider.</param>
    /// <returns>The binding.</returns>
    /// <remarks>
    ///     ⚠ <b>Virtual so that a surface with a richer property can hand one back and still be an
    ///     <see cref="EditTarget" />.</b> The inspector's is an <c>InspectorField</c> — the same
    ///     binding plus a reset, a prefab override and a visibility — and without this a panel that
    ///     wanted those had to build its own instance beside the cached one, which is an instance
    ///     nothing ever raises <see cref="EditProperty.Changed" /> on.
    /// </remarks>
    protected virtual EditProperty Create(IEditMember member) => new(member, Objects, Document);

    /// <summary>Binds one member by name, or nothing.</summary>
    /// <param name="path">What the member is called.</param>
    /// <returns>The binding, or <see langword="null" />.</returns>
    public EditProperty? Find(string path) => TryFind(path, out var property) ? property : null;

    /// <summary>Binds every member.</summary>
    /// <returns>The bindings, in the order the members are declared.</returns>
    public IReadOnlyList<EditProperty> Properties() {
        var members = Members;

        if (members.Count == 0) {
            return [];
        }

        var bound = new EditProperty[members.Count];

        for (var index = 0; index < members.Count; index++) {
            bound[index] = Find(members[index].Name)
                ?? throw new InvalidOperationException(
                    $"The provider listed '{members[index].Name}' among '{CommonType}'s members and then "
                    + "could not resolve it. Those two answers come from one description and cannot disagree."
                );
        }

        return bound;
    }

    /// <summary>Collects everything written until it closes into one undo entry.</summary>
    /// <param name="name">What the one entry is called.</param>
    /// <returns>The transaction, or <see langword="null" /> when nothing is recording.</returns>
    /// <remarks>
    ///     What a surface opens when one gesture writes several members — a transform handle setting
    ///     position, rotation and scale, a paste filling in a whole component. Disposing commits.
    /// </remarks>
    public CommandTransaction? Begin(string name) => Document?.Stack.BeginTransaction(name);

    /// <summary>Ends the run of edits that were collapsing into one undo entry.</summary>
    public void Seal() => Document?.Stack.Seal();

    static Type? TypeOf(IReadOnlyList<object> objects) {
        if (objects.Count == 0) {
            return null;
        }

        var type = objects[0].GetType();

        for (var index = 1; index < objects.Count; index++) {
            if (objects[index].GetType() != type) {
                return null;
            }
        }

        return type;
    }
}

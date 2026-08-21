// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Editor.Core;

namespace Vixen.Editor.Inspector;

/// <summary>An <see cref="EditTarget" /> whose properties are inspector fields.</summary>
/// <remarks>
///     <para>
///         <b>What markup binds against.</b> A <c>binding-path</c> resolves through the base's
///         <c>Find</c>, and the thing it hands back has to be the one an inspector row is built from
///         — the reset button, the prefab override bar and the visibility all live on
///         <see cref="InspectorField" /> rather than on <c>EditProperty</c>, and a
///         <c>&lt;PropertyField&gt;</c> that had none of them would be visibly less than the row the
///         generated inspector draws for the same member.
///     </para>
///     <para>
///         ⚠ <b>A subclass rather than a second lookup, so the caching still holds.</b>
///         <c>EditTarget.TryFind</c> keeps one binding per name precisely so a panel can subscribe to
///         its <c>Changed</c>; building an <c>InspectorField</c> beside the cached
///         <c>EditProperty</c> would give the panel an event nothing is ever raised on. That is the
///         bug this type exists to make unavailable.
///     </para>
///     <para>
///         ⚠ <b>A member the descriptor did not describe falls back to the plain binding.</b> A
///         provider is free to answer with any <c>IEditMember</c> — a graph node's port, a settings
///         file's key — and only an <see cref="InspectorMember" /> has the type default and the
///         drawer hint an inspector row needs. Refusing would make this less capable than its base
///         for no gain.
///     </para>
/// </remarks>
public sealed class InspectorTarget : EditTarget {
    readonly InspectorDescriptor? descriptor;
    readonly IPrefabSource? prefab;

    /// <summary>Binds a selection through the described-type provider.</summary>
    /// <param name="objects">What is being edited, in selection order.</param>
    /// <param name="document">Where edits are recorded, or <see langword="null" />.</param>
    /// <param name="prefab">What the objects came from, for the override bar, or <see langword="null" />.</param>
    /// <param name="provider">
    ///     How members are reached, or <see langword="null" /> for <see cref="InspectorEditProvider.Default" />.
    /// </param>
    /// <param name="descriptor">
    ///     What the fields are built against, or <see langword="null" /> to look the common type up in
    ///     <see cref="InspectorRegistry" />.
    /// </param>
    /// <remarks>
    ///     ⚠ <b>A caller that hands over a provider usually has to hand over a descriptor too.</b>
    ///     The two answer different questions — one reaches the members, the other supplies the
    ///     defaults a reset button needs and the identity a field is built against — and the registry
    ///     can only answer the second for a type the generator described. A provider for something it
    ///     did not, a graph node's ports being the case this was added for, would otherwise get plain
    ///     <c>EditProperty</c>s and no rows.
    /// </remarks>
    public InspectorTarget(
        IReadOnlyList<object> objects,
        EditorDocument? document = null,
        IPrefabSource? prefab = null,
        IEditProvider? provider = null,
        InspectorDescriptor? descriptor = null
    ) : base(objects, provider ?? InspectorEditProvider.Default, document) {
        this.prefab = prefab;

        this.descriptor = descriptor ?? (CommonType is { } type ? InspectorRegistry.Find(type) : null);
    }

    /// <summary>The description the fields are built against, if the type has one.</summary>
    public InspectorDescriptor? Descriptor => descriptor;

    /// <inheritdoc />
    protected override EditProperty Create(IEditMember member) =>
        descriptor is not null && member is InspectorMember described
            ? new InspectorField(descriptor, described, Objects, Document, prefab)
            : base.Create(member);
}

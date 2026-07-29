// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Diagnostics.CodeAnalysis;

namespace Vixen.Editor.Inspector;

/// <summary>Everything the inspector knows about one type, decided at compile time.</summary>
/// <remarks>
///     <para>
///         One of these per <c>[Inspector]</c>-carrying type, emitted by the generator and registered
///         from a module initializer, so referencing the assembly is enough for its types to be
///         inspectable. A type with no annotated members has no descriptor and costs nothing.
///     </para>
///     <para>
///         <b>Members are in declaration order, and that is a decision.</b> An inspector that sorted
///         a type's members alphabetically is one nobody can find anything in, and it reorders itself
///         when somebody renames a field. <c>[Inspector(Order = …)]</c> is the escape hatch, and it
///         is a coarse one on purpose.
///     </para>
///     <para>
///         <b>The default instance is made once per type and kept.</b> It is what reset-to-default
///         reads from, and what decides whether a row shows as modified. A type that cannot be
///         constructed has no defaults to offer, and the affordances that need them are hidden rather
///         than guessing.
///     </para>
/// </remarks>
public sealed class InspectorDescriptor {
    readonly Dictionary<string, InspectorMember> byName = new(StringComparer.Ordinal);
    readonly Func<object>? factory;
    object? defaults;
    bool defaultsFailed;

    /// <summary>The type described.</summary>
    public Type Type { get; }

    /// <summary>Its members, in the order they are drawn.</summary>
    public IReadOnlyList<InspectorMember> Members { get; }

    /// <summary>Whether a fresh instance can be made, which is what defaults come from.</summary>
    public bool CanCreate => factory is not null;

    /// <summary>What makes a fresh instance, for a descriptor being rebuilt over a type's bases.</summary>
    internal Func<object>? Factory => factory;

    /// <summary>Describes a type.</summary>
    /// <param name="type">The type.</param>
    /// <param name="members">Its members, in the order they are drawn.</param>
    /// <param name="factory">Makes a fresh instance, or <see langword="null" /> if none can be.</param>
    /// <exception cref="ArgumentException">Two members share a name.</exception>
    public InspectorDescriptor(Type type, IReadOnlyList<InspectorMember> members, Func<object>? factory = null) {
        ArgumentNullException.ThrowIfNull(type);
        ArgumentNullException.ThrowIfNull(members);

        Type = type;
        Members = members;
        this.factory = factory;

        foreach (var member in members) {
            if (!byName.TryAdd(member.Name, member)) {
                throw new ArgumentException(
                    $"'{type}' describes '{member.Name}' twice. A condition names a member by name, so "
                    + "two members sharing one would make [ShowIf] mean whichever was registered first.",
                    nameof(members)
                );
            }
        }
    }

    /// <summary>Finds a member by its name in source.</summary>
    /// <param name="name">The name.</param>
    /// <param name="member">The member.</param>
    /// <returns>Whether there is one.</returns>
    public bool TryGetMember(string name, [MaybeNullWhen(false)] out InspectorMember member) =>
        byName.TryGetValue(name, out member);

    /// <summary>The value a member has on a freshly made instance.</summary>
    /// <param name="member">The member.</param>
    /// <param name="value">Its default.</param>
    /// <returns>Whether there is a default to be had.</returns>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>A constructor that throws is treated as "no defaults", once.</b> A type whose
    ///         parameterless constructor needs a graphics device is a real thing, and an inspector
    ///         that threw every time it drew one of those — or that retried the throwing constructor
    ///         per row — would be worse than one that quietly offers no reset button.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>A member this descriptor does not own has no default here, and asking is not an
    ///         error.</b> A composite drawer builds fields over members that belong to something else
    ///         — a list's third element belongs to the list — and hands them the descriptor it has,
    ///         because a field needs one. Reading such a member off a fresh instance of <i>this</i>
    ///         type is a cast that cannot succeed, and it used to be an exception thrown while
    ///         drawing a row.
    ///     </para>
    /// </remarks>
    public bool TryGetDefault(InspectorMember member, out object? value) {
        ArgumentNullException.ThrowIfNull(member);

        value = null;

        if (factory is null || defaultsFailed || !Owns(member)) {
            return false;
        }

        if (defaults is null) {
            try {
                defaults = factory();
            } catch (Exception exception) when (exception is not OutOfMemoryException and not StackOverflowException) {
                defaultsFailed = true;
                return false;
            }
        }

        value = member.GetBoxed(defaults);
        return true;
    }

    /// <summary>Whether a member is one of this type's own.</summary>
    /// <remarks>
    ///     By identity rather than by name: a nested type and its owner can both declare a
    ///     <c>Padding</c>, and reading one off the other would produce a default from the wrong type
    ///     rather than an obvious failure.
    /// </remarks>
    bool Owns(InspectorMember member) {
        foreach (var candidate in Members) {
            if (ReferenceEquals(candidate, member)) {
                return true;
            }
        }

        return false;
    }

    /// <summary>Renders the descriptor as its type's name.</summary>
    /// <returns>The text.</returns>
    public override string ToString() => Type.Name;
}

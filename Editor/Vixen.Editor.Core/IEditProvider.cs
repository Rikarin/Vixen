// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Diagnostics.CodeAnalysis;

namespace Vixen.Editor.Core;

/// <summary>How an <see cref="EditTarget" /> reaches the members of what it is holding.</summary>
/// <remarks>
///     <para>
///         <b>The seam that stops "one editing pipeline" from meaning "one kind of thing".</b> An
///         entity's components are described by a generator; a graph node's ports are described by
///         the node; a settings asset is described by <c>Vixen.Core.Reflection</c>; a plugin's own
///         object is described by whatever the plugin ships. Four implementations of this, and one
///         pipeline over all of them — rather than four pipelines, which is what the editor had.
///     </para>
///     <para>
///         ⚠ <b>Resolution is by type, not by instance.</b> A selection of twenty objects has one
///         member list, and asking each object for its own would make a mixed selection a set of
///         lists to intersect. <see cref="EditTarget" /> works out the common type first and asks
///         once.
///     </para>
/// </remarks>
public interface IEditProvider {
    /// <summary>The members of a type, in the order they should be shown.</summary>
    /// <param name="type">The type.</param>
    /// <returns>Its members, or empty if the provider does not describe it.</returns>
    IReadOnlyList<IEditMember> MembersOf(Type type);

    /// <summary>Finds one member of a type by name.</summary>
    /// <param name="type">The type.</param>
    /// <param name="path">What the member is called.</param>
    /// <param name="member">The member.</param>
    /// <returns>Whether the type has one by that name.</returns>
    bool TryResolve(Type type, string path, [NotNullWhen(true)] out IEditMember? member);
}

/// <summary>A provider that describes nothing.</summary>
/// <remarks>
///     What an <see cref="EditTarget" /> built over objects nobody has described falls back to, so
///     that "no provider" is a target with no properties rather than a null reference at the first
///     read. A test fixture and a preview of an unregistered type are the two real cases.
/// </remarks>
public sealed class EmptyEditProvider : IEditProvider {
    /// <summary>The one instance.</summary>
    public static EmptyEditProvider Instance { get; } = new();

    EmptyEditProvider() { }

    /// <inheritdoc />
    public IReadOnlyList<IEditMember> MembersOf(Type type) => [];

    /// <inheritdoc />
    public bool TryResolve(Type type, string path, [NotNullWhen(true)] out IEditMember? member) {
        member = null;
        return false;
    }
}

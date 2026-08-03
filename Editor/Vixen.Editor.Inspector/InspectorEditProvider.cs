// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Diagnostics.CodeAnalysis;
using Vixen.Editor.Core;

namespace Vixen.Editor.Inspector;

/// <summary>The editing pipeline's view of what the generator described.</summary>
/// <remarks>
///     <para>
///         <b>The first of doc 36 § D1's providers, and the one that proves the seam is real.</b>
///         Everything it answers comes from <see cref="InspectorRegistry" /> — module initializers
///         the generator emitted, no reflection pass and no assembly scan — so an
///         <see cref="EditTarget" /> over a described type finds its members at the cost of a
///         dictionary lookup.
///     </para>
///     <para>
///         ⚠ <b>It is a lookup, not a store.</b> Registration stays where it was; this type adds no
///         second place a member can be declared, which is the mistake the editor already made once
///         with two component registries.
///     </para>
/// </remarks>
public sealed class InspectorEditProvider : IEditProvider {
    /// <summary>The one over the process-wide registry.</summary>
    public static InspectorEditProvider Default { get; } = new();

    /// <inheritdoc />
    /// <remarks>
    ///     <see cref="IReadOnlyList{T}" /> is covariant, so the descriptor's own list is handed back
    ///     rather than copied into a second one — a panel asking for a type's members every frame
    ///     allocates nothing.
    /// </remarks>
    public IReadOnlyList<IEditMember> MembersOf(Type type) {
        ArgumentNullException.ThrowIfNull(type);

        return InspectorRegistry.Find(type)?.Members ?? [];
    }

    /// <inheritdoc />
    public bool TryResolve(Type type, string path, [NotNullWhen(true)] out IEditMember? member) {
        ArgumentNullException.ThrowIfNull(type);
        ArgumentException.ThrowIfNullOrEmpty(path);

        if (InspectorRegistry.TryGet(type, out var descriptor) && descriptor.TryGetMember(path, out var found)) {
            member = found;
            return true;
        }

        member = null;
        return false;
    }
}

// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Diagnostics.CodeAnalysis;
using Vixen.Editor.Core;

namespace Vixen.Editor.Inspector;

/// <summary>The editing pipeline's view of what the generator described.</summary>
/// <remarks>
///     <para>
///         <b>The first of doc 36 § D1's providers, and the one that proves the seam is real.</b>
///         Everything it answers comes from a registry a generator filled — module initializers the
///         generator emitted, no reflection pass and no assembly scan — so an
///         <see cref="EditTarget" /> over a described type finds its members at the cost of a
///         dictionary lookup.
///     </para>
///     <para>
///         ⚠ <b>Two registries, and it used to read one.</b> <c>InspectorRegistry</c> holds what
///         <c>[Inspector]</c> described; <c>TypeRegistry</c> holds what the serialization generator
///         described, and <see cref="ReflectedDescriptor" /> builds an inspector descriptor out of
///         the second where there is no entry in the first. Reading only the first made the editing
///         pipeline strictly narrower than the panel that draws it: a <c>[DataContract]</c> type
///         with no <c>[Inspector]</c> annotation — a settings asset, which is
///         <see cref="IEditProvider" />'s own worked example — drew rows and answered with an empty
///         member list, so it had no <c>EditProperty</c>, no undo, no mixed state and no markup
///         binding. ⚠ <b>And it was silent in the direction that looks fine</b>: an empty list is a
///         target with no properties rather than an error.
///     </para>
///     <para>
///         ⚠ <b>The cost sentence above survives the change, which is why the fallback is here
///         rather than in a second provider callers opt into.</b> <c>TypeRegistry</c> is filled by
///         a <c>[ModuleInitializer]</c> the generator emits per assembly — its own remarks say
///         there is no scan and nothing that stops working when the trimmer removes metadata — so
///         "reflected" in <see cref="ReflectedDescriptor" /> names <c>Vixen.Core.Reflection</c> and
///         not <c>System.Reflection</c>. What this added is a second dictionary lookup on the miss
///         path, and it is cached.
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

        return ReflectedDescriptor.For(type)?.Members ?? [];
    }

    /// <inheritdoc />
    public bool TryResolve(Type type, string path, [NotNullWhen(true)] out IEditMember? member) {
        ArgumentNullException.ThrowIfNull(type);
        ArgumentException.ThrowIfNullOrEmpty(path);

        if (ReflectedDescriptor.TryGet(type, out var descriptor) && descriptor.TryGetMember(path, out var found)) {
            member = found;
            return true;
        }

        member = null;
        return false;
    }
}

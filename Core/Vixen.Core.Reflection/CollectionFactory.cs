// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;

namespace Vixen.Core.Reflection;

/// <summary>How to make a collection whose element type is only known at run time.</summary>
/// <remarks>
///     <para>
///         <b>This exists because <c>Array.CreateInstance(elementType, n)</c> is the one thing a
///         data binder needs and NativeAOT cannot do.</b> So can <c>MakeGenericType</c> and
///         <c>Activator.CreateInstance(Type)</c>; all three are <c>RequiresDynamicCode</c>, and a
///         binder built on them works on a desktop and throws on a phone. The engine found this the
///         moment the first <c>.meta</c> file needed a list of per-target overrides, which is what
///         [14](../../docs/plan/14-roadmap.md) means by scheduling Phase 3 early.
///     </para>
///     <para>
///         The answer is the same one the rest of this assembly gives: <b>a generator saw the type in
///         the source, so a generator can write the constructor.</b> A member declared
///         <c>TargetOverride[]</c> produces <c>static count =&gt; new TargetOverride[count]</c> —
///         ordinary C#, statically typed, bound at compile time. Nothing at run time has to build a
///         type.
///     </para>
///     <para>
///         Interfaces are registered too, backed by an array: a member declared
///         <c>IReadOnlyList&lt;T&gt;</c> is filled with a <c>T[]</c>, which satisfies it with no copy.
///     </para>
/// </remarks>
public static class CollectionFactory {
    static readonly ConcurrentDictionary<Type, Func<int, object>> Factories = new();

    /// <summary>How many collection types are registered.</summary>
    public static int Count => Factories.Count;

    /// <summary>Records how to make one.</summary>
    /// <param name="type">The collection type, as declared on a member.</param>
    /// <param name="create">Makes one, given how many elements it will hold.</param>
    /// <remarks>Registering the same type twice replaces, so a type described in two assemblies is not a conflict.</remarks>
    public static void Register(Type type, Func<int, object> create) {
        ArgumentNullException.ThrowIfNull(type);
        ArgumentNullException.ThrowIfNull(create);
        Factories[type] = create;
    }

    /// <summary>Makes one.</summary>
    /// <param name="type">The collection type.</param>
    /// <param name="capacity">How many elements it will hold.</param>
    /// <param name="instance">The new collection.</param>
    /// <returns><see langword="false" /> if nothing registered a way to make this type.</returns>
    public static bool TryCreate(Type type, int capacity, [NotNullWhen(true)] out object? instance) {
        ArgumentNullException.ThrowIfNull(type);

        if (Factories.TryGetValue(type, out var create)) {
            instance = create(capacity);
            return true;
        }

        instance = null;
        return false;
    }

    /// <summary>Forgets everything. For tests that need an empty registry.</summary>
    public static void Clear() => Factories.Clear();
}

// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;

namespace Vixen.Editor.Inspector;

/// <summary>Every described type in the process, by type.</summary>
/// <remarks>
///     <para>
///         Filled by module initializers the generator emits, so referencing an assembly is enough
///         for its types to be inspectable — no scan, no <c>AppDomain.GetAssemblies</c>. Same shape
///         as <c>Vixen.Core.Reflection</c>'s <c>TypeRegistry</c>, and for the same reasons.
///     </para>
///     <para>
///         <b>A base type's members are inherited by a derived one.</b> A behaviour deriving from a
///         behaviour is the normal case, and an inspector that showed only what the leaf declared
///         would hide half of it. The walk is done at lookup and cached, rather than by the generator
///         emitting the base's members into every derived descriptor: the base may live in another
///         assembly compiled before the derived type existed.
///     </para>
/// </remarks>
public static class InspectorRegistry {
    static readonly ConcurrentDictionary<Type, InspectorDescriptor> Declared = new();
    static readonly ConcurrentDictionary<Type, InspectorDescriptor?> Resolved = new();

    /// <summary>The types that declared members of their own.</summary>
    public static IReadOnlyCollection<Type> DeclaredTypes => (IReadOnlyCollection<Type>) Declared.Keys;

    /// <summary>Registers a type's descriptor.</summary>
    /// <param name="descriptor">The descriptor.</param>
    /// <exception cref="ArgumentException">The type is already registered with a different descriptor.</exception>
    /// <remarks>
    ///     Idempotent for the same instance, because a generator's module initializer can run twice
    ///     when an assembly is loaded into two contexts — which is exactly what plugin loading does.
    ///     Two <i>different</i> descriptors for one type is a real conflict and throws.
    /// </remarks>
    public static void Register(InspectorDescriptor descriptor) {
        ArgumentNullException.ThrowIfNull(descriptor);

        var existing = Declared.GetOrAdd(descriptor.Type, descriptor);

        if (!ReferenceEquals(existing, descriptor)) {
            throw new ArgumentException(
                $"'{descriptor.Type}' already has a different inspector descriptor. Two descriptors for "
                + "one type would make which editors are drawn depend on which assembly initialised first.",
                nameof(descriptor)
            );
        }

        Resolved.Clear();
    }

    /// <summary>Forgets everything. For tests.</summary>
    internal static void Clear() {
        Declared.Clear();
        Resolved.Clear();
    }

    /// <summary>The descriptor for a type, including what it inherits.</summary>
    /// <param name="type">The type.</param>
    /// <param name="descriptor">The descriptor.</param>
    /// <returns>Whether the type or any of its bases declares inspected members.</returns>
    public static bool TryGet(Type type, [MaybeNullWhen(false)] out InspectorDescriptor descriptor) {
        ArgumentNullException.ThrowIfNull(type);

        descriptor = Resolved.GetOrAdd(type, static key => Resolve(key));
        return descriptor is not null;
    }

    /// <summary>The descriptor for a type, or <see langword="null" />.</summary>
    /// <param name="type">The type.</param>
    /// <returns>The descriptor.</returns>
    public static InspectorDescriptor? Find(Type type) => TryGet(type, out var descriptor) ? descriptor : null;

    /// <summary>The most derived type every object has in common, if they all share one exactly.</summary>
    /// <param name="objects">The objects.</param>
    /// <returns>The type, or <see langword="null" /> when they are not all the same.</returns>
    /// <remarks>
    ///     ⚠ <b>One type, not a common base.</b> Inspecting a light and a camera together would have
    ///     to fall back to whatever they both derive from, which produces a different set of editors
    ///     depending on what else happened to be selected. A mixed selection shows nothing, which is
    ///     honest, and is what every inspector that tried the alternative ended up doing.
    /// </remarks>
    public static Type? CommonType(IReadOnlyList<object> objects) {
        ArgumentNullException.ThrowIfNull(objects);

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

    static InspectorDescriptor? Resolve(Type type) {
        List<InspectorDescriptor> chain = [];

        for (var current = type; current is not null; current = current.BaseType) {
            if (Declared.TryGetValue(current, out var declaredHere)) {
                chain.Add(declaredHere);
            }
        }

        if (chain.Count == 0) {
            return null;
        }

        if (chain.Count == 1 && chain[0].Type == type) {
            return chain[0];
        }

        // Base first, so an inherited row is above the ones the leaf added — which is the order the
        // fields were written in if you read the hierarchy top down, and the order every other
        // engine's inspector uses.
        chain.Reverse();

        List<InspectorMember> members = [];
        HashSet<string> seen = new(StringComparer.Ordinal);

        foreach (var descriptor in chain) {
            foreach (var member in descriptor.Members) {
                // A `new` member shadowing a base's keeps the base's position and the leaf's
                // accessors, because moving the row would make adding a `new` reorder the inspector.
                if (seen.Add(member.Name)) {
                    members.Add(member);
                }
            }
        }

        var ordered = members
            .Select(static (member, index) => (member, index))
            .OrderBy(static pair => pair.member.Order)
            .ThenBy(static pair => pair.index)
            .Select(static pair => pair.member)
            .ToArray();

        // The factory has to be the *leaf's*: the defaults a reset reads are the ones a fresh
        // instance of the inspected type has, and a base's constructor would hand back an object
        // whose derived members do not exist.
        var leaf = chain[^1];

        return new(type, ordered, leaf.Type == type ? leaf.Factory : null);
    }
}

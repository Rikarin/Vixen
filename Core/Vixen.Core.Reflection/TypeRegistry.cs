// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.Loader;

namespace Vixen.Core.Reflection;

/// <summary>Every annotated type in every loaded assembly.</summary>
/// <remarks>
///     <para>
///         Filled by a <c>[ModuleInitializer]</c> the generator emits per assembly, so referencing
///         an assembly is enough for its types to be discoverable — there is no scan, no
///         <c>AppDomain.GetAssemblies</c>, and nothing that stops working when the trimmer removes
///         the metadata a scan would have read.
///     </para>
///     <para>
///         Registration is idempotent by type. An assembly loaded twice, or a module initializer
///         that runs after a test has registered a descriptor by hand, replaces rather than
///         duplicates.
///     </para>
/// </remarks>
public static class TypeRegistry {
    static readonly ConcurrentDictionary<Type, TypeDescriptor> ByType = new();
    static readonly ConcurrentDictionary<string, TypeDescriptor> ByAlias = new(StringComparer.Ordinal);

    /// <summary>How many types are registered.</summary>
    public static int Count => ByType.Count;

    /// <summary>Forgets every type an assembly described.</summary>
    /// <param name="assembly">The assembly being unloaded.</param>
    /// <returns>How many were forgotten.</returns>
    /// <remarks>
    ///     <inheritdoc cref="Vixen.Core.Serialization.SerializerRegistry.Evict" select="remarks/para[1]" />
    ///     <para>
    ///         ⚠ <b>A descriptor holds accessor delegates over the type it describes</b>, so one left
    ///         behind keeps the whole context alive — which is the difference between an unload that
    ///         completes and a file the next build cannot overwrite.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>An alias is only given up by whoever is holding it</b>, which matters exactly
    ///         where <see cref="Claim" /> now lets one type be loaded twice —
    ///         <a href="https://github.com/Rikarin/Vixen/issues/798">#798</a>. Unloading the
    ///         collectible copy of a plugin assembly must not take the alias the default context's
    ///         copy owns away with it, and an unconditional remove did exactly that: the plugin comes
    ///         out, and the name it shares with the host stops resolving for the rest of the session.
    ///     </para>
    /// </remarks>
    public static int Evict(System.Reflection.Assembly assembly) {
        ArgumentNullException.ThrowIfNull(assembly);

        var evicted = 0;

        foreach (var pair in ByType.ToArray()) {
            if (pair.Key.Assembly != assembly) {
                continue;
            }

            ByType.TryRemove(pair.Key, out _);

            if (ByAlias.TryGetValue(pair.Value.Alias, out var holder) && holder.Type == pair.Key) {
                ByAlias.TryRemove(pair.Value.Alias, out _);
            }

            evicted++;
        }

        return evicted;
    }

    /// <summary>Everything registered.</summary>
    public static IEnumerable<TypeDescriptor> All => ByType.Values;

    /// <summary>Registers a descriptor, replacing any previous one for the same type.</summary>
    /// <param name="descriptor">The descriptor.</param>
    /// <exception cref="InvalidOperationException">Another type already claims the same alias.</exception>
    public static void Register(TypeDescriptor descriptor) {
        ArgumentNullException.ThrowIfNull(descriptor);
        ByType[descriptor.Type] = descriptor;
        Claim(descriptor.Alias, descriptor);

        foreach (var former in descriptor.FormerAliases) {
            Claim(former, descriptor);
        }
    }

    /// <summary>Finds the descriptor for a type.</summary>
    /// <param name="type">The type.</param>
    /// <param name="descriptor">Its descriptor.</param>
    /// <returns><see langword="false" /> if it is not registered.</returns>
    public static bool TryGet(Type type, [NotNullWhen(true)] out TypeDescriptor? descriptor) {
        ArgumentNullException.ThrowIfNull(type);
        return ByType.TryGetValue(type, out descriptor);
    }

    /// <summary>Finds the descriptor for a type.</summary>
    /// <typeparam name="T">The type.</typeparam>
    /// <param name="descriptor">Its descriptor.</param>
    /// <returns><see langword="false" /> if it is not registered.</returns>
    public static bool TryGet<T>([NotNullWhen(true)] out TypeDescriptor? descriptor) =>
        ByType.TryGetValue(typeof(T), out descriptor);

    /// <summary>Finds the descriptor registered under a name.</summary>
    /// <param name="alias">The name, current or former.</param>
    /// <param name="descriptor">Its descriptor.</param>
    /// <returns><see langword="false" /> if nothing claims that name.</returns>
    public static bool TryGetByAlias(string alias, [NotNullWhen(true)] out TypeDescriptor? descriptor) {
        ArgumentException.ThrowIfNullOrEmpty(alias);
        return ByAlias.TryGetValue(alias, out descriptor);
    }

    /// <summary>Everything carrying all of <paramref name="traits" />.</summary>
    /// <param name="traits">What to look for.</param>
    /// <returns>The descriptors.</returns>
    /// <remarks>
    ///     What the ECS asks at boot to find its components, and what the editor asks to fill an
    ///     "add component" menu. Both used to be an assembly scan.
    /// </remarks>
    public static IEnumerable<TypeDescriptor> With(TypeTraits traits) {
        foreach (var descriptor in ByType.Values) {
            if (descriptor.Has(traits)) {
                yield return descriptor;
            }
        }
    }

    /// <summary>Everything assignable to <typeparamref name="TBase" />.</summary>
    /// <typeparam name="TBase">The base type or interface.</typeparam>
    /// <returns>The descriptors, including <typeparamref name="TBase" />'s own if it is registered.</returns>
    /// <remarks>
    ///     The one query that genuinely wants <see cref="Type.IsAssignableFrom" />, and the reason it
    ///     is safe here: the candidate set is the registry rather than every type in the process, so
    ///     the answer does not depend on which assemblies a scan happened to reach.
    /// </remarks>
    public static IEnumerable<TypeDescriptor> AssignableTo<TBase>() {
        foreach (var descriptor in ByType.Values) {
            if (typeof(TBase).IsAssignableFrom(descriptor.Type)) {
                yield return descriptor;
            }
        }
    }

    /// <summary>Forgets everything. For tests that need an empty registry.</summary>
    public static void Clear() {
        ByType.Clear();
        ByAlias.Clear();
    }

    static void Claim(string alias, TypeDescriptor descriptor) {
        var existing = ByAlias.GetOrAdd(alias, descriptor);

        if (existing.Type == descriptor.Type) {
            ByAlias[alias] = descriptor;

            return;
        }

        if (IsOneTypeLoadedTwice(existing.Type, descriptor.Type)) {
            // ⚠ The default context's copy wins, and "keep the first" would have been wrong. Which of
            // the two arrives first depends on whether the host had touched the assembly before the
            // plugin loaded — and everything that resolves a type by name afterwards (the serializer,
            // the inspector, anything holding a `Type` it compiled against) resolves into the default
            // context. An alias answering with the collectible copy hands back a type nothing else can
            // use, and goes on doing so after the plugin has been unloaded.
            if (IsDefaultContext(descriptor.Type)) {
                ByAlias[alias] = descriptor;
            }

            return;
        }

        throw new InvalidOperationException(
            $"Both '{existing.Type}' and '{descriptor.Type}' claim the name '{alias}'. Give one of them "
            + "an explicit [DataContract(\"…\")] alias; the name is what data and tools refer to it by."
        );
    }

    /// <summary>Whether two claimants are one type that has been loaded into two contexts.</summary>
    /// <param name="existing">What holds the alias.</param>
    /// <param name="claiming">What is asking for it.</param>
    /// <returns>Whether they are the same type from different load contexts.</returns>
    /// <remarks>
    ///     <para>
    ///         <b>The one arrangement that looks like an alias collision and is not</b> —
    ///         <a href="https://github.com/Rikarin/Vixen/issues/798">#798</a>. An editor plugin's
    ///         entry assembly is loaded from its own path into a <em>collectible</em> context while
    ///         every dependency goes to the default one — which is precisely what makes unloading a
    ///         plugin mean anything — so the assembly exists twice and its generated module
    ///         initializer runs twice. Refusing the second claim threw a
    ///         <c>TypeInitializationException</c> out of <c>&lt;Module&gt;</c>, which took the whole
    ///         plugin load down rather than the serializer, and made "a plugin may not declare a
    ///         <c>[DataContract]</c>" a rule the plugin contract had to carry.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Assembly-qualified names would call these two the same and must not be used.</b>
    ///         <c>Type.AssemblyQualifiedName</c> carries the name, version, culture and public key of
    ///         the assembly and says nothing at all about the context it was loaded into — which is
    ///         the whole of the difference here. The load context is asked for directly, and two
    ///         assemblies of the same identity in <em>one</em> context cannot happen, so a
    ///         <see langword="true" /> here is never a real collision being waved through.
    ///     </para>
    /// </remarks>
    static bool IsOneTypeLoadedTwice(Type existing, Type claiming) =>
        string.Equals(existing.FullName, claiming.FullName, StringComparison.Ordinal)
        && string.Equals(
            existing.Assembly.GetName().Name,
            claiming.Assembly.GetName().Name,
            StringComparison.Ordinal
        )
        && !ReferenceEquals(
            AssemblyLoadContext.GetLoadContext(existing.Assembly),
            AssemblyLoadContext.GetLoadContext(claiming.Assembly)
        );

    /// <summary>Whether a type came out of the context everything else resolves into.</summary>
    /// <param name="type">The type.</param>
    /// <returns>Whether its assembly is in the default load context.</returns>
    static bool IsDefaultContext(Type type) =>
        ReferenceEquals(AssemblyLoadContext.GetLoadContext(type.Assembly), AssemblyLoadContext.Default);
}

// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using Xunit;

namespace Vixen.Core.Serialization.Tests;

/// <summary>The serialised-name half of <a href="https://github.com/Rikarin/Vixen/issues/798">#798</a>.</summary>
/// <remarks>
///     <para>
///         <b>Two registries refuse a second claim, not one, and fixing either alone closes
///         nothing.</b> A <c>[DataContract]</c> generates a descriptor into <c>TypeRegistry</c>
///         <em>and</em> a serializer into <see cref="SerializerRegistry" />, both from the same
///         module initializer. An editor plugin's entry assembly is loaded from its own path into a
///         collectible context while every dependency goes to the default one, so that initializer
///         runs twice — and this registry threw a <see cref="SerializationException" /> on the second
///         claim exactly as the other one threw an <see cref="InvalidOperationException" />.
///     </para>
///     <para>
///         ⚠ <b><see cref="SerializerRegistry.Evict" />'s remarks describe the collision as a
///         <em>rebuild</em> problem and name eviction as the answer, which is right about a rebuild
///         and cannot help here.</b> A plugin's two copies are simultaneous: the host's is registered
///         and stays registered while the plugin's context loads beside it. There is nothing to evict
///         first.
///     </para>
///     <para>
///         ⚠ <b>The registration goes through <c>MakeGenericMethod</c> because it has to.</b>
///         <c>SerializerRegistry.Register</c> is generic over the type it serialises, and the second
///         copy of that type exists only as a runtime <see cref="Type" /> — there is no way to name
///         it in source, which is the same reason the defect could only ever be seen at run time.
///     </para>
/// </remarks>
public class SerializerRegistryLoadContextTests {
    /// <summary>⚠ The second claim is tolerated, and the name keeps the default context's type.</summary>
    /// <remarks>
    ///     Which copy holds the name is the half a "does it throw" test cannot see. A polymorphic
    ///     stream carries the alias and nothing else, and everything that reads one resolves into the
    ///     default context — so a name answering with the collectible copy hands back a serializer
    ///     for a type nothing else can use, and goes on doing so after the plugin has been unloaded.
    /// </remarks>
    [Fact]
    public void The_same_type_from_two_load_contexts_shares_one_serialised_name() {
        var second = Loaded();

        Assert.NotSame(typeof(Twin), second);
        Assert.Equal(typeof(Twin).FullName, second.FullName);

        try {
            SerializerRegistry.Register<Twin>("LoadContextTwinSerializer", new TwinSerializer<Twin>());
            RegisterOther(second, "LoadContextTwinSerializer");

            Assert.True(SerializerRegistry.TryGetByAlias("LoadContextTwinSerializer", out var held));
            Assert.Same(typeof(Twin), held.SerializedType);
        } finally {
            SerializerRegistry.Evict(second.Assembly);
        }
    }

    /// <summary>⚠ Two genuinely different types still collide, which is what the check is for.</summary>
    /// <remarks>
    ///     The refutation half. A fix that let any second claim through would make a polymorphic
    ///     stream load as whichever type initialised last, silently — which is worse than the throw
    ///     it replaced and is what #798's option 1 was warned about.
    /// </remarks>
    [Fact]
    public void Two_different_types_claiming_one_name_is_still_refused() {
        SerializerRegistry.Register<Twin>("SharedTwinName", new TwinSerializer<Twin>());

        var thrown = Assert.Throws<SerializationException>(
            () => SerializerRegistry.Register<Other>("SharedTwinName", new TwinSerializer<Other>())
        );

        Assert.Contains("claim the serialised name", thrown.Message, StringComparison.Ordinal);
    }

    /// <summary>⚠ And the by-id map keeps the default context's copy too.</summary>
    /// <remarks>
    ///     <para>
    ///         <b>The third place #798 reaches, and the one nothing had ever seen.</b>
    ///         <c>ContentHash.TypeId</c> is computed from the type's name, so two copies of one type
    ///         hash to one id — and the refusal in <c>Claim</c> threw before the registration ever
    ///         reached that map, which is why the collision was invisible for as long as the bug it
    ///         hid behind existed. Tolerating the second claim exposes it.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>It was found by a suite failing for a reason that read like nonsense</b>, which is
    ///         the same sentence <see cref="SerializerRegistry.Evict" />'s remarks already use:
    ///         <c>ObjectDatabaseTests</c> went red with <c>Expected: SettableClass … Actual:
    ///         SettableClass</c>, because loading this assembly into a second context ran its whole
    ///         generated module initializer and the last writer of each id won.
    ///     </para>
    /// </remarks>
    [Fact]
    public void A_chunk_read_by_type_id_gets_the_default_contexts_serializer() {
        var second = Loaded();

        try {
            SerializerRegistry.Register<Twin>(new TwinSerializer<Twin>());
            RegisterOther(second, "TypeIdTwinSerializer");

            Assert.True(
                SerializerRegistry.TryGetByTypeId(Storage.ContentHash.TypeId(typeof(Twin)), out var held)
            );

            Assert.Same(typeof(Twin), held.SerializedType);
        } finally {
            SerializerRegistry.Evict(second.Assembly);
        }
    }

    /// <summary>⚠ Unloading the plugin's copy does not take the host's name with it.</summary>
    /// <remarks>
    ///     The defect tolerating the second claim would otherwise have created.
    ///     <see cref="SerializerRegistry.Evict" /> removed the alias entry unconditionally, so
    ///     evicting the collectible copy — which is what unloading a plugin does — deleted the entry
    ///     the default context's copy owns, and every polymorphic stream carrying that name stopped
    ///     loading for the rest of the session.
    /// </remarks>
    [Fact]
    public void Evicting_the_collectible_copy_leaves_the_hosts_name_alone() {
        var second = Loaded();

        try {
            SerializerRegistry.Register<Twin>("EvictedTwinSerializer", new TwinSerializer<Twin>());
            RegisterOther(second, "EvictedTwinSerializer");

            SerializerRegistry.Evict(second.Assembly);

            Assert.True(SerializerRegistry.TryGetByAlias("EvictedTwinSerializer", out var held));
            Assert.Same(typeof(Twin), held.SerializedType);
        } finally {
            SerializerRegistry.Evict(second.Assembly);
        }
    }

    /// <summary>Registers the other context's copy under a name, the only way it can be done.</summary>
    /// <param name="type">The copy, which exists only as a runtime type.</param>
    /// <param name="alias">The name both copies claim.</param>
    static void RegisterOther(Type type, string alias) {
        var serializer = Activator.CreateInstance(typeof(TwinSerializer<>).MakeGenericType(type))!;

        var register = typeof(SerializerRegistry)
            .GetMethods(BindingFlags.Public | BindingFlags.Static)
            .Single(method => method.Name == "Register" && method.GetParameters().Length == 3)
            .MakeGenericMethod(type);

        register.Invoke(null, [alias, serializer, Array.Empty<string>()]);
    }

    /// <summary>This assembly's <see cref="Twin" />, out of a second load context.</summary>
    /// <returns>The other copy of the type.</returns>
    /// <remarks>
    ///     ⚠ <b>The context is made here rather than held by the caller</b>: a debug build roots every
    ///     local for the whole method it is declared in, so a context in a variable outlives the test
    ///     that made it and pins a collectible context for the rest of the run.
    /// </remarks>
    [MethodImpl(MethodImplOptions.NoInlining)]
    static Type Loaded() {
        var context = new AssemblyLoadContext("serializer-registry-second-copy", isCollectible: true);
        var assembly = context.LoadFromAssemblyPath(typeof(Twin).Assembly.Location);

        return assembly.GetType(typeof(Twin).FullName!)!;
    }

    /// <summary>A type of this suite's own, so nothing else in the assembly reads what it registers.</summary>
    public sealed class Twin {
        /// <summary>Something to write.</summary>
        public int Value { get; set; }
    }

    /// <summary>A second, genuinely different type, for the collision that must still be refused.</summary>
    public sealed class Other {
        /// <summary>Something to write.</summary>
        public int Value { get; set; }
    }

    /// <summary>A serializer that writes nothing, because what is under test is the registration.</summary>
    /// <typeparam name="T">Whatever it is registered for, including a type from another context.</typeparam>
    public sealed class TwinSerializer<T> : DataSerializer<T> {
        /// <inheritdoc />
        public override void Serialize(ref SerializationWriter writer, in T value) { }

        /// <inheritdoc />
        public override void Deserialize(ref SerializationReader reader, ref T value) { }
    }
}

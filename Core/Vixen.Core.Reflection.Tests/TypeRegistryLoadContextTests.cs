// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using Xunit;

namespace Vixen.Core.Reflection.Tests;

/// <summary>One type loaded into two contexts is not two types claiming one name.</summary>
/// <remarks>
///     <para>
///         <b><a href="https://github.com/Rikarin/Vixen/issues/798">#798</a>: a plugin's own entry
///         assembly could not declare a <c>[DataContract]</c>.</b>
///         <c>PluginLoadContext.Load</c> sends every <c>Vixen.*</c> dependency to the <em>default</em>
///         context and loads the entry assembly from its own path into a collectible one — which is
///         precisely what makes <c>PluginHost.WaitForCollection</c> mean anything. So the assembly
///         exists twice, the generated descriptor module initializer runs twice, and
///         <see cref="TypeRegistry" /> saw one alias claimed by two <see cref="Type" /> objects with
///         identical full names and threw. ⚠ Out of <c>&lt;Module&gt;</c>, as a
///         <c>TypeInitializationException</c>, which took the whole plugin load down rather than the
///         serializer — the least debuggable place it could have happened.
///     </para>
///     <para>
///         ⚠ <b>Reproduced here rather than in an editor test, because the editor has no assembly
///         with the defect.</b> The issue names <c>Vixen.Editor.Terrain</c> as carrying "the same
///         landmine, armed" — that is <b>false</b>, and checked: it declares six
///         <c>[DataContract]</c> types and references neither <c>Vixen.Core.Reflection</c> nor its
///         generator, so nothing is generated, nothing registers, and loading it twice into a
///         collectible context is uneventful. <c>Vixen.Editor.Texturing</c> avoids the reference on
///         purpose and maps its file key by key instead. This assembly has the generator, so it is
///         the one place the mechanism can be shown at all.
///     </para>
///     <para>
///         ⚠ <b>The instrument is checked before the claim.</b> A second context that quietly
///         resolved this assembly by name would hand back the very same <see cref="Type" /> object,
///         and every assertion below would pass against a registry that had done nothing — so the two
///         are asserted distinct first.
///     </para>
///     <para>
///         ⚠ <b>In <see cref="TypeRegistryTests" />' collection, so the two never run at once.</b>
///         <see cref="TypeRegistry" /> is process-wide and that suite reads <c>Count</c> either side
///         of a registration; a second class registering concurrently would make it fail for a reason
///         that has nothing to do with it.
///     </para>
/// </remarks>
[Collection(TypeRegistryTestGroup.Name)]
public class TypeRegistryLoadContextTests {
    /// <summary>⚠ The second claim is tolerated, and the alias keeps the default context's type.</summary>
    /// <remarks>
    ///     <para>
    ///         <b>Why tolerate rather than refuse loudly.</b> #798's option 2 was an architecture rule
    ///         forbidding a <c>[DataContract]</c> in any project containing an <c>IEditorPlugin</c>,
    ///         which is cheap and forbids the thing every plugin eventually wants — a serialisable
    ///         setting. The check that throws exists to catch two <em>different</em> types claiming
    ///         one name, and <c>TwoTypesClaimingOneNameIsAnErrorRatherThanLastOneWins</c> is what
    ///         holds it to that; this is the one arrangement that is not that.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Which copy the alias keeps is the half a "does it throw" test cannot see.</b>
    ///         Everything that later resolves a type by name — the serializer, the inspector,
    ///         anything holding a <c>Type</c> it compiled against — resolves into the default
    ///         context. An alias answering with the collectible copy hands back a type nothing else
    ///         can use, and goes on doing so after the plugin has been unloaded, which is a worse
    ///         failure than the throw and an entirely silent one.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Registered in both orders, because "keep the first" and "keep the default one"
    ///         agree in exactly one of them.</b> Which copy arrives first depends on whether the host
    ///         had touched the assembly before the plugin loaded, and that is not something the answer
    ///         should be at the mercy of.
    ///     </para>
    /// </remarks>
    [Fact]
    public void The_same_type_from_two_load_contexts_shares_one_alias() {
        var second = Loaded();

        Assert.NotSame(typeof(Twin), second);
        Assert.Equal(typeof(Twin).FullName, second.FullName);

        try {
            TypeRegistry.Register(new(typeof(Twin), "LoadContextTwin", TypeTraits.DataContract, []));
            TypeRegistry.Register(new(second, "LoadContextTwin", TypeTraits.DataContract, []));

            Assert.True(TypeRegistry.TryGetByAlias("LoadContextTwin", out var held));
            Assert.Same(typeof(Twin), held.Type);

            // And the other way round, which is the order the editor takes when the host has not
            // touched the plugin's assembly before the plugin loads.
            TypeRegistry.Register(new(second, "LoadContextTwin", TypeTraits.DataContract, []));

            Assert.True(TypeRegistry.TryGetByAlias("LoadContextTwin", out var still));
            Assert.Same(typeof(Twin), still.Type);
        } finally {
            // The second copy's descriptor holds accessors over a type in a collectible context, so
            // it goes back out whatever happened above.
            TypeRegistry.Evict(second.Assembly);
        }
    }

    /// <summary>⚠ Unloading the plugin's copy does not take the host's alias with it.</summary>
    /// <remarks>
    ///     The defect that tolerating the second claim would otherwise have created.
    ///     <see cref="TypeRegistry.Evict" /> removed the alias entry unconditionally, so evicting the
    ///     collectible copy — which is what unloading a plugin does — deleted the entry the default
    ///     context's copy owns, and the name stopped resolving for the rest of the session. Nothing
    ///     would report that: the type is still registered <em>by type</em>, and only a lookup by name
    ///     fails.
    /// </remarks>
    [Fact]
    public void Evicting_the_collectible_copy_leaves_the_hosts_alias_alone() {
        var second = Loaded();

        try {
            TypeRegistry.Register(new(typeof(Twin), "EvictedTwin", TypeTraits.DataContract, []));
            TypeRegistry.Register(new(second, "EvictedTwin", TypeTraits.DataContract, []));

            Assert.Equal(1, TypeRegistry.Evict(second.Assembly));

            Assert.True(TypeRegistry.TryGetByAlias("EvictedTwin", out var held));
            Assert.Same(typeof(Twin), held.Type);
        } finally {
            TypeRegistry.Evict(second.Assembly);
        }
    }

    /// <summary>This assembly's <see cref="Twin" />, out of a second load context.</summary>
    /// <returns>The other copy of the type.</returns>
    /// <remarks>
    ///     ⚠ <b>The context is made here rather than held by the caller</b>, for
    ///     <c>TexturingCollectionTests.Activate</c>'s reason: a debug build roots every local for the
    ///     whole method it is declared in, so a context in a variable outlives the test that made it.
    ///     Nothing here asserts collection, but a test that pinned one for the rest of the run would
    ///     be the reason a later suite could not.
    /// </remarks>
    [MethodImpl(MethodImplOptions.NoInlining)]
    static Type Loaded() {
        var context = new AssemblyLoadContext("type-registry-second-copy", isCollectible: true);
        var assembly = context.LoadFromAssemblyPath(typeof(Twin).Assembly.Location);

        return assembly.GetType(typeof(Twin).FullName!)!;
    }

    /// <summary>A type of this suite's own, registered by hand under an alias nothing else uses.</summary>
    /// <remarks>
    ///     ⚠ <b>Not one of <c>Described.cs</c>'s, deliberately.</b> Registering a hand-built
    ///     descriptor for <c>Health</c> would replace the generated one every other test in this
    ///     assembly reads — its category, its members and its serializer — for as long as this test
    ///     ran. The descriptors here are hand-built because the point is the load context rather than
    ///     the generator, so the type they describe should be one nothing else asks about.
    /// </remarks>
    public sealed class Twin {
        /// <summary>Something for a descriptor to be about.</summary>
        public int Value { get; set; }
    }
}

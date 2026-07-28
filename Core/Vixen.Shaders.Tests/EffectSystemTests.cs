// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Immutable;
using Vixen.Shaders;
using Xunit;

namespace Tests;

/// <summary>
///     Resolving a shader variant — docs/plan/06 § Effect permutations.
/// </summary>
public class EffectSystemTests {
    static Effect Compiled(EffectKey key) => new() { Key = key, Stages = [] };

    sealed class Provider(Func<EffectKey, Effect?> answer) : IEffectProvider {
        public int Calls { get; private set; }

        public Effect? TryGet(EffectKey key) {
            Calls++;
            return answer(key);
        }
    }

    // --- The key ------------------------------------------------------------

    /// <summary>
    ///     The same values set in a different order give the same key.
    /// </summary>
    /// <remarks>
    ///     Without the normal form the cache holds one entry per insertion order and hits almost
    ///     never — a miss that presents as a frame-time cliff rather than as a wrong image, which is
    ///     the harder kind to attribute.
    /// </remarks>
    [Fact]
    public void Order_does_not_change_a_key() {
        var shadows = ParameterKeys.NewPermutation(false, "Test.Effect.Order.Shadows");
        var lights = ParameterKeys.NewPermutation(4, "Test.Effect.Order.Lights");

        var first = new ParameterCollection();
        first.Set(shadows, true);
        first.Set(lights, 8);

        var second = new ParameterCollection();
        second.Set(lights, 8);
        second.Set(shadows, true);

        Assert.Equal(
            EffectKey.From("Lighting", first, [shadows, lights]),
            EffectKey.From("Lighting", second, [lights, shadows])
        );
    }

    /// <summary>
    ///     A permutation the shader never branched on does not multiply the cache.
    /// </summary>
    /// <remarks>
    ///     This is what Raven's <c>UsedPermutationKeys</c> is for, and it is the difference between a
    ///     tractable cache and 2ⁿ entries where a handful are distinct: twenty declared flags with
    ///     three that matter is eight shaders, not a million.
    /// </remarks>
    [Fact]
    public void An_unused_permutation_does_not_make_a_second_variant() {
        var used = ParameterKeys.NewPermutation(false, "Test.Effect.Unused.Used");
        var unused = ParameterKeys.NewPermutation(false, "Test.Effect.Unused.Unused");

        var left = new ParameterCollection();
        left.Set(unused, true);

        var right = new ParameterCollection();
        right.Set(unused, false);

        Assert.Equal(EffectKey.From("S", left, [used]), EffectKey.From("S", right, [used]));
    }

    [Fact]
    public void A_permutation_that_was_never_set_takes_the_shaders_default() {
        var lights = ParameterKeys.NewPermutation(16, "Test.Effect.Default.Lights");

        var key = EffectKey.From("S", new(), [lights]);

        Assert.Equal("16", Assert.Single(key.Values).Value);
    }

    /// <summary>A value key is refused where a permutation belongs.</summary>
    /// <remarks>
    ///     Confusing the two is expensive in one direction: putting a per-draw colour in the effect
    ///     key gives one compiled shader per colour, which is a cache that grows without bound and a
    ///     stall on every new object.
    /// </remarks>
    [Fact]
    public void A_value_key_cannot_select_a_variant() {
        var value = ParameterKeys.New<float>("Test.Effect.Wrong.Value");

        var error = Assert.Throws<ArgumentException>(() => EffectKey.From("S", new(), [value]));
        Assert.Contains("value key", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_key_reads_as_something_a_cache_filename_could_be() {
        var shadows = ParameterKeys.NewPermutation(false, "Test.Effect.Text.Shadows");

        var parameters = new ParameterCollection();
        parameters.Set(shadows, true);

        Assert.Equal("Lighting[Test.Effect.Text.Shadows=true]", EffectKey.From("Lighting", parameters, [shadows]).ToString());
        Assert.Equal("Lighting", EffectKey.Of("Lighting").ToString());
    }

    // --- The composition ------------------------------------------------------

    /// <summary>
    ///     Two compositions of one shader are two keys.
    /// </summary>
    /// <remarks>
    ///     The property the whole material system rests on. A composition decides which shaders the
    ///     compilation contains, so two materials with the same name and the same permutations are
    ///     different code — and a key blind to that returns the first one compiled for both, which is
    ///     a metal-roughness object drawn with a specular-glossiness shader and nothing logged.
    /// </remarks>
    [Fact]
    public void Two_compositions_of_one_shader_are_two_keys() {
        var metal = ShaderComposition.Of([new("surface", "MetalRoughnessSurface")]);
        var gloss = ShaderComposition.Of([new("surface", "SpecularGlossinessSurface")]);

        Assert.NotEqual(
            EffectKey.From("ForwardPlus", new(), [], metal),
            EffectKey.From("ForwardPlus", new(), [], gloss)
        );
    }

    /// <summary>The same slots filled in a different order are one key.</summary>
    /// <remarks>
    ///     The same normal form the permutation values have, and for the same reason: a material
    ///     whose features were enumerated in a different order is the same material.
    /// </remarks>
    [Fact]
    public void Order_does_not_change_a_composition() {
        var first = ShaderComposition.Of([new("surface", "CompositeSurface"), new("shading", "CelShading")]);
        var second = ShaderComposition.Of([new("shading", "CelShading"), new("surface", "CompositeSurface")]);

        Assert.Equal(first, second);
        Assert.Equal(
            EffectKey.From("ForwardPlus", new(), [], first),
            EffectKey.From("ForwardPlus", new(), [], second)
        );
    }

    /// <summary>A slot bound twice takes the last binding, so defaults can be laid over.</summary>
    [Fact]
    public void A_slot_bound_twice_takes_the_last_one() {
        var composition = ShaderComposition.Of([
            new("surface", "IdentitySurface"),
            new("surface", "MetalRoughnessSurface")
        ]);

        Assert.Equal("MetalRoughnessSurface", composition.Resolve("surface"));
        Assert.Equal(1, composition.Count);
    }

    /// <summary>A shader with no slots is keyed exactly as it was before compositions existed.</summary>
    /// <remarks>
    ///     Every post effect and the depth-only pass are this shape, so an empty composition has to
    ///     be free — in the key's text as well as its equality, because an on-disk cache is keyed by
    ///     that text and a changed filename is a cache that misses on everything it already has.
    /// </remarks>
    [Fact]
    public void A_shader_with_no_composition_is_unchanged() {
        Assert.Equal(EffectKey.Of("Copy"), EffectKey.From("Copy", new(), []));
        Assert.Equal("Copy", EffectKey.From("Copy", new(), []).ToString());
    }

    /// <summary>The composition is in the key's text, so a cache filename distinguishes them.</summary>
    [Fact]
    public void A_composed_key_reads_as_something_a_cache_filename_could_be() {
        var composition = ShaderComposition.Of([
            new("surface", "CompositeSurface"),
            new("shading", "CelShading")
        ]);

        Assert.Equal(
            "ForwardPlus{shading=CelShading,surface=CompositeSurface}",
            EffectKey.From("ForwardPlus", new(), [], composition).ToString()
        );
    }

    // --- The system ---------------------------------------------------------

    [Fact]
    public void An_effect_is_asked_for_once_and_remembered() {
        var system = new EffectSystem();
        var key = EffectKey.Of("Remembered");
        var provider = new Provider(k => Compiled(k));

        system.AddProvider(provider);

        var first = system.Resolve(key);
        var second = system.Resolve(key);

        Assert.NotNull(first);
        Assert.Same(first, second);
        Assert.Equal(1, provider.Calls);
    }

    /// <summary>
    ///     Providers are asked in order, and the first that answers wins.
    /// </summary>
    /// <remarks>
    ///     The tiering doc 06 describes: the baked bundle first, the disk cache next, the compiler
    ///     last. A shipping build adds only the first, which is what makes "no runtime compilation"
    ///     structural rather than a flag.
    /// </remarks>
    [Fact]
    public void The_first_provider_that_answers_wins() {
        var system = new EffectSystem();
        var key = EffectKey.Of("Tiered");

        var bundle = new Provider(_ => null);
        var disk = new Provider(k => Compiled(k));
        var compiler = new Provider(k => Compiled(k));

        system.AddProvider(bundle);
        system.AddProvider(disk);
        system.AddProvider(compiler);

        Assert.NotNull(system.Resolve(key));
        Assert.Equal(1, bundle.Calls);
        Assert.Equal(1, disk.Calls);
        Assert.Equal(0, compiler.Calls);
    }

    /// <summary>
    ///     A key nothing can satisfy is recorded, so "no runtime compilation" can be a test.
    /// </summary>
    /// <remarks>
    ///     Doc 06's testing table asks for the build-time enumerator's output to be asserted a
    ///     superset of what a playthrough requests. This is the other half of that assertion: run
    ///     the playthrough against the bundle alone and check the miss list is empty.
    /// </remarks>
    [Fact]
    public void A_key_nothing_supplies_is_recorded_as_a_miss() {
        var system = new EffectSystem();
        var key = EffectKey.Of("Missing");

        system.AddProvider(new Provider(_ => null));

        Assert.Null(system.Resolve(key));
        Assert.Equal(1, system.MissCount);
        Assert.Equal(key, Assert.Single(system.Misses));

        // Asking again does not double-count: it is one missing permutation, however often it is hit.
        system.Resolve(key);
        Assert.Equal(1, system.MissCount);
    }

    [Fact]
    public void Supplying_a_missed_key_clears_the_miss() {
        var system = new EffectSystem();
        var key = EffectKey.Of("Recovered");

        system.AddProvider(new Provider(_ => null));
        system.Resolve(key);

        system.Add(Compiled(key));

        Assert.Equal(0, system.MissCount);
        Assert.NotNull(system.Resolve(key));
    }

    /// <summary>Two keys that differ only by a permutation resolve to different effects.</summary>
    [Fact]
    public void Two_variants_are_two_effects() {
        var system = new EffectSystem();
        var shadows = ParameterKeys.NewPermutation(false, "Test.Effect.Two.Shadows");

        var on = new ParameterCollection();
        on.Set(shadows, true);

        var off = new ParameterCollection();
        off.Set(shadows, false);

        system.AddProvider(new Provider(Compiled));

        var withShadows = system.Resolve(EffectKey.From("S", on, [shadows]));
        var without = system.Resolve(EffectKey.From("S", off, [shadows]));

        Assert.NotSame(withShadows, without);
        Assert.Equal(2, system.Count);
    }

    /// <summary>
    ///     A hot reload forgets what was compiled without forgetting where effects come from.
    /// </summary>
    [Fact]
    public void Invalidating_drops_the_effects_and_keeps_the_providers() {
        var system = new EffectSystem();
        var key = EffectKey.Of("Reloaded");
        var provider = new Provider(Compiled);

        system.AddProvider(provider);
        system.Resolve(key);

        system.Invalidate();

        Assert.Equal(0, system.Count);
        Assert.NotNull(system.Resolve(key));
        Assert.Equal(2, provider.Calls);
    }

    [Fact]
    public void An_effect_carries_what_the_next_key_is_built_from() {
        var shadows = ParameterKeys.NewPermutation(false, "Test.Effect.Carried.Shadows");

        var effect = new Effect {
            Key = EffectKey.Of("Carried"),
            Stages = [],
            UsedPermutationKeys = [shadows],
            ConstantBufferSize = 96
        };

        Assert.Equal(96, effect.ConstantBufferSize);
        Assert.Equal(shadows, Assert.Single(effect.UsedPermutationKeys));
        Assert.Empty(effect.Parameters);
        Assert.True(effect.Stages.IsEmpty);
        Assert.Equal(ImmutableArray<EffectStage>.Empty, effect.Stages);
    }
}

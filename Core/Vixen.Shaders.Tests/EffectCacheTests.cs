// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;
using Vixen.Core.Serialization;
using Vixen.Graphics;
using Vixen.Graphics.Null;
using Vixen.Shaders;
using Vixen.Shaders.Generated;
using Xunit;

namespace Tests;

/// <summary>
///     The tiers below the in-memory dictionary — docs/plan/06 § Effect permutations.
/// </summary>
/// <remarks>
///     <para>
///         What these are really about is that a variant can exist without the thing that made it.
///         Raven's own <c>.rvnfx</c> already holds bytecode and reflection, and reading one links the
///         parser, both backends and the lowerer — so the runtime reads <see cref="EffectData" />
///         instead, and every claim below is a claim about that record surviving a trip it has to
///         survive: through the serializer into a bundle, through a directory of files, and onto a
///         device that did not exist when it was baked.
///     </para>
/// </remarks>
public class EffectCacheTests {
    /// <summary>A variant of the fixture shader, near enough to what the compiler produces.</summary>
    /// <remarks>
    ///     Built by hand rather than compiled, deliberately. What is under test here is the runtime
    ///     half — the record, the cache, the loader — and driving Raven to produce one would make
    ///     every assertion below also an assertion about the compiler.
    /// </remarks>
    static EffectData Variant(string shader = "Lighting", bool shadows = true, string hash = "abc") =>
        new() {
            ShaderName = shader,
            Target = "spirv",
            SourceHash = hash,
            Permutations = [
                new("Lighting.MaxLights", "4", ShaderValueKind.Int, "4"),
                new("Lighting.UseShadows", shadows ? "true" : "false", ShaderValueKind.Bool, "false")
            ],
            Stages = [
                new(ShaderStage.Vertex, [1, 2, 3, 4]),
                new(ShaderStage.Fragment, [5, 6, 7, 8])
            ],
            Bindings = [
                new("constants", DescriptorSetSlot.PerMaterial, 0, DescriptorKind.UniformBuffer, ShaderStage.Vertex | ShaderStage.Fragment),
                new("albedo", DescriptorSetSlot.PerMaterial, 1, DescriptorKind.SampledTexture, ShaderStage.Fragment)
            ],
            ConstantBufferSize = 128,
            Parameters = [
                new("Lighting.worldViewProjection", ShaderValueKind.Matrix4x4, 0, 64),
                new("Lighting.ambient", ShaderValueKind.Float3, 112, 12),
                new("Lighting.exposure", ShaderValueKind.Float, 124, 4)
            ]
        };

    static string Scratch() {
        var path = Path.Combine(Path.GetTempPath(), "vixen-effect-cache", Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(path);
        return path;
    }

    /// <summary>A source that counts how often it was asked, and can be told to have nothing.</summary>
    sealed class Counted(EffectData? answer) : IEffectSource {
        public int Calls { get; private set; }

        public EffectData? TryGet(EffectKey key) {
            Calls++;
            return answer;
        }
    }

    // --- The record ---------------------------------------------------------

    /// <summary>
    ///     A baked variant is filed under the key a draw asks with.
    /// </summary>
    /// <remarks>
    ///     The claim the other three tiers rest on. A draw builds its key from a
    ///     <see cref="ParameterCollection" /> and the shader's used keys; a build wrote text into a
    ///     file months earlier. If those two do not produce the same key the bundle is a set of
    ///     shaders nothing can find, and the symptom is a shipping build that misses on everything
    ///     rather than an error anybody could read.
    /// </remarks>
    [Fact]
    public void A_baked_variant_is_filed_under_the_key_a_draw_asks_with() {
        var parameters = new ParameterCollection();
        parameters.Set(LightingKeys.UseShadows, true);
        parameters.Set(LightingKeys.MaxLights, 4);

        var drawn = EffectKey.From(LightingKeys.ShaderName, parameters, LightingKeys.UsedPermutationKeys);

        Assert.Equal(drawn, Variant().ToKey());
    }

    /// <summary>The record survives the serializer, bytes and all.</summary>
    [Fact]
    public void A_bundle_survives_the_round_trip_through_content() {
        var bundle = new EffectBundle { Effects = [Variant(shadows: true), Variant(shadows: false)] };

        var read = Serializer.Read<EffectBundle>(Serializer.ToBytes(bundle));
        var store = new EffectStore(read);

        Assert.Equal(2, store.Count);

        var loaded = store.TryGet(Variant(shadows: false).ToKey());

        Assert.NotNull(loaded);
        Assert.Equal("spirv", loaded.Target);
        Assert.Equal([5, 6, 7, 8], loaded.Stages.Single(stage => stage.Stage == ShaderStage.Fragment).Bytecode);
        Assert.Equal(128, loaded.ConstantBufferSize);
        Assert.Equal(DescriptorKind.SampledTexture, loaded.Bindings.Single(binding => binding.Name == "albedo").Kind);
    }

    /// <summary>Two records under one key is a build that compiled the same variant twice.</summary>
    [Fact]
    public void A_store_refuses_the_same_variant_twice() {
        var store = new EffectStore();
        store.Add(Variant());

        Assert.Throws<ArgumentException>(() => store.Add(Variant()));
    }

    /// <summary>What a store bakes is ordered, so two builds of one set are one file.</summary>
    [Fact]
    public void A_baked_bundle_is_ordered() {
        var store = new EffectStore();
        store.Add(Variant(shadows: true));
        store.Add(Variant(shadows: false));
        store.Add(Variant("Shadow"));

        Assert.Equal(
            ["Lighting[Lighting.MaxLights=4,Lighting.UseShadows=false]", "Lighting[Lighting.MaxLights=4,Lighting.UseShadows=true]", "Shadow[Lighting.MaxLights=4,Lighting.UseShadows=true]"],
            store.ToBundle().Effects.Select(effect => effect.ToKey().ToString())
        );
    }

    // --- The loader ---------------------------------------------------------

    /// <summary>
    ///     A key the loader interns is the key generated code already has.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         The load-bearing agreement in the whole arrangement, and the one that cannot be
    ///         checked by reading either side. A parameter key is interned by name and carries a CLR
    ///         type; the generator picks that type from Raven's reflection at build time, and the
    ///         loader picks it from a <see cref="ShaderValueKind" /> stored in a file. Two answers to
    ///         one question.
    ///     </para>
    ///     <para>
    ///         They agree here or the interning table throws naming both, which is the good failure.
    ///         The bad one would be a table that allowed two entries: a render feature would set
    ///         <c>LightingKeys.Exposure</c> and the effect would write a different key's offset, and
    ///         the frame would come out dark with nothing logged anywhere.
    ///     </para>
    /// </remarks>
    [Fact]
    public void A_loaded_key_is_the_generated_key() {
        using var device = new NullDevice();
        var effect = new EffectLoader(device).Load(Variant());

        Assert.Same(LightingKeys.Exposure, effect.Parameters.Single(parameter => parameter.Key.Name == "Lighting.exposure").Key);
        Assert.Same(LightingKeys.WorldViewProjection, effect.Parameters.Single(parameter => parameter.Key.Name == "Lighting.worldViewProjection").Key);
        Assert.Same(LightingKeys.Ambient, effect.Parameters.Single(parameter => parameter.Key.Name == "Lighting.ambient").Key);
        Assert.Same(LightingKeys.UseShadows, effect.UsedPermutationKeys.Single(key => key.Name == "Lighting.UseShadows"));
        Assert.Same(LightingKeys.MaxLights, effect.UsedPermutationKeys.Single(key => key.Name == "Lighting.MaxLights"));

        // And the type came from the record, so a value key is a value key and a permutation is not.
        Assert.Equal(typeof(Vector3), effect.Parameters.Single(parameter => parameter.Key.Name == "Lighting.ambient").Key.ValueType);
    }

    /// <summary>An effect comes off the device ready to be bound.</summary>
    [Fact]
    public void A_loaded_effect_carries_its_layouts_and_its_offsets() {
        using var device = new NullDevice();
        var effect = new EffectLoader(device).Load(Variant());

        Assert.Equal(4, effect.SetLayouts.Length);
        Assert.True(effect.Layout.IsValid);
        Assert.Equal(124, effect.Parameters.Single(parameter => parameter.Key.Name == "Lighting.exposure").Offset);

        var albedo = effect.BindingOf("albedo");

        Assert.NotNull(albedo);
        Assert.Equal(DescriptorSetSlot.PerMaterial, albedo.Value.Set);
        Assert.Equal(1u, albedo.Value.Binding);
    }

    /// <summary>
    ///     Two effects with the same set get the same layout.
    /// </summary>
    /// <remarks>
    ///     Not an economy. A descriptor set allocated against one layout handle cannot be bound to a
    ///     pipeline built from another, so a loader that created a fresh layout per effect would make
    ///     the per-frame set unshareable — every pipeline in the frame allocating its own copy of the
    ///     camera.
    /// </remarks>
    [Fact]
    public void Two_effects_with_the_same_set_share_one_layout() {
        using var device = new NullDevice();
        var loader = new EffectLoader(device);

        var first = loader.Load(Variant(shadows: true));
        var second = loader.Load(Variant(shadows: false));

        Assert.Equal(first.SetLayouts[(int)DescriptorSetSlot.PerMaterial], second.SetLayouts[(int)DescriptorSetSlot.PerMaterial]);

        // Three empty sets and one material set, once each — not eight. The empty ones are three
        // rather than one because a layout carries which slot it is for, and a backend is entitled
        // to care.
        Assert.Equal(4, loader.LayoutCount);
    }

    /// <summary>An empty set still gets a layout, because set indices are positional.</summary>
    /// <remarks>
    ///     A shader binding only the per-material set binds it at index two. A pipeline layout that
    ///     skipped the two empty ones below it would put that set at index zero, and every descriptor
    ///     set in the frame would land somewhere the shader was not looking.
    /// </remarks>
    [Fact]
    public void An_empty_set_still_has_a_layout() {
        using var device = new NullDevice();
        var effect = new EffectLoader(device).Load(Variant());

        Assert.All(effect.SetLayouts, layout => Assert.True(layout.IsValid));
    }

    // --- The disk cache -----------------------------------------------------

    /// <summary>A miss is compiled once and read from disk thereafter.</summary>
    [Fact]
    public void The_disk_cache_asks_once_and_reads_the_rest() {
        var directory = Scratch();
        var inner = new Counted(Variant());
        var cache = new EffectDiskCache(directory, "spirv", inner);

        var key = Variant().ToKey();

        Assert.NotNull(cache.TryGet(key));
        Assert.Equal(1, inner.Calls);
        Assert.Equal(1, cache.Writes);

        // A second cache over the same directory, because the point is that the answer outlived the
        // object — an in-memory dictionary would pass a test that reused the first one.
        var reopened = new EffectDiskCache(directory, "spirv", inner);

        var again = reopened.TryGet(key);

        Assert.NotNull(again);
        Assert.Equal(1, inner.Calls);
        Assert.Equal(1, reopened.Hits);
        Assert.Equal("abc", again.SourceHash);
    }

    /// <summary>A cache with nothing behind it answers what it has and nothing else.</summary>
    [Fact]
    public void A_read_only_cache_misses_quietly() {
        var cache = new EffectDiskCache(Scratch(), "spirv");

        Assert.Null(cache.TryGet(Variant().ToKey()));
    }

    /// <summary>Two backends' artefacts for one key coexist.</summary>
    /// <remarks>
    ///     One machine building for desktop and for mobile out of one tree is the ordinary case. If
    ///     the target were not in the entry's name the second build would read the first's SPIR-V and
    ///     hand it to a GLES device.
    /// </remarks>
    [Fact]
    public void Two_targets_do_not_share_an_entry() {
        var directory = Scratch();
        var key = Variant().ToKey();

        Assert.NotEqual(
            new EffectDiskCache(directory, "spirv").PathOf(key),
            new EffectDiskCache(directory, "glsl").PathOf(key)
        );
    }

    /// <summary>
    ///     A truncated entry costs a recompile, not a crash.
    /// </summary>
    /// <remarks>
    ///     A build killed halfway through a write, a full disk, a cache directory two machines share
    ///     over a network. A cache is an optimisation and its failure mode has to be "slower".
    /// </remarks>
    [Fact]
    public void A_corrupt_entry_is_a_miss() {
        var directory = Scratch();
        var inner = new Counted(Variant());
        var cache = new EffectDiskCache(directory, "spirv", inner);
        var key = Variant().ToKey();

        File.WriteAllBytes(cache.PathOf(key), [0xFF, 0xFE, 0xFD]);

        Assert.NotNull(cache.TryGet(key));
        Assert.Equal(1, inner.Calls);

        // And the good entry replaced the bad one rather than sitting behind it.
        Assert.NotNull(new EffectDiskCache(directory, "spirv").TryGet(key));
    }

    /// <summary>
    ///     Editing a shader invalidates exactly the variants of it.
    /// </summary>
    /// <remarks>
    ///     The source hash rides inside the entry rather than in its name, because a reader has to be
    ///     able to <em>find</em> the entry and a runtime asking for a variant does not know what the
    ///     source hashed to — the compiler that knew is the thing this tier exists to avoid running.
    ///     A host that does know sets <see cref="EffectDiskCache.Expect" />.
    /// </remarks>
    [Fact]
    public void A_stale_entry_is_a_miss_for_a_host_that_knows_the_hash() {
        var directory = Scratch();
        var key = Variant().ToKey();

        new EffectDiskCache(directory, "spirv").Store(Variant(hash: "old"));

        var inner = new Counted(Variant(hash: "new"));
        var cache = new EffectDiskCache(directory, "spirv", inner) { Expect = "new" };

        var loaded = cache.TryGet(key);

        Assert.Equal(1, inner.Calls);
        Assert.NotNull(loaded);
        Assert.Equal("new", loaded.SourceHash);

        // A host with no sources to hash accepts what it finds — which is the shipping case, where
        // rejecting a perfectly good cache written by the build that shipped with it helps nobody.
        Assert.NotNull(new EffectDiskCache(directory, "spirv").TryGet(key));
    }

    // --- The provider seam --------------------------------------------------

    /// <summary>A source becomes a tier, and the system caches what it produced.</summary>
    [Fact]
    public void A_source_becomes_a_tier() {
        using var device = new NullDevice();

        var store = new EffectStore();
        store.Add(Variant());

        var system = new EffectSystem();
        system.AddProvider(new EffectSourceProvider(store, new(device)));

        var key = Variant().ToKey();
        var effect = system.Resolve(key);

        Assert.NotNull(effect);
        Assert.Same(effect, system.Resolve(key));
        Assert.Empty(system.Misses);
    }

    /// <summary>
    ///     Every key a run asked for is recorded, whether or not it was there.
    /// </summary>
    /// <remarks>
    ///     What a manifest is dumped from. Recorded before the in-memory tier, so a key asked for a
    ///     thousand times and cached after the first is still in the list — otherwise the capture
    ///     would hold only what the first frame happened to need.
    /// </remarks>
    [Fact]
    public void A_run_records_what_it_asked_for() {
        using var device = new NullDevice();

        var store = new EffectStore();
        store.Add(Variant(shadows: true));

        var system = new EffectSystem();
        system.AddProvider(new EffectSourceProvider(store, new(device)));

        system.Resolve(Variant(shadows: true).ToKey());
        system.Resolve(Variant(shadows: true).ToKey());
        system.Resolve(Variant(shadows: false).ToKey());

        Assert.Equal(2, system.RequestCount);
        Assert.Equal(Variant(shadows: false).ToKey(), Assert.Single(system.Misses));

        system.ClearRequests();

        Assert.Empty(system.Requests);
        Assert.NotNull(system.Resolve(Variant(shadows: true).ToKey()));
    }

    // --- Compiling without stalling -----------------------------------------

    /// <summary>
    ///     A variant nobody has yet draws something, and the frame does not wait.
    /// </summary>
    /// <remarks>
    ///     Doc 06 in one sentence: "development builds compile on demand, asynchronously, rendering
    ///     with a placeholder material for the frames until ready — never a hitch, never a stall." A
    ///     compile is hundreds of milliseconds and it happens the first time a material is seen,
    ///     which is exactly when somebody is walking into a new room.
    /// </remarks>
    [Fact]
    public void A_variant_being_compiled_draws_a_placeholder() {
        using var device = new NullDevice();
        var loader = new EffectLoader(device);

        var store = new EffectStore();
        store.Add(Variant());

        var system = new EffectSystem();
        system.AddProvider(new EffectSourceProvider(store, loader));
        system.Placeholder = loader.Load(Variant("Placeholder")).AsPlaceholder();

        var key = Variant().ToKey();
        var first = system.Resolve(key);

        Assert.NotNull(first);
        Assert.True(first.IsPlaceholder);
        Assert.Equal(1, system.PendingCount);

        // Nothing was produced by asking, which is the point: the provider was not touched on the
        // frame that asked.
        Assert.Equal(0, system.Count);

        Assert.Equal(1, system.Pump());

        var second = system.Resolve(key);

        Assert.NotNull(second);
        Assert.False(second.IsPlaceholder);
        Assert.Equal(key, second.Key);
    }

    /// <summary>
    ///     The placeholder is never what a key resolved to.
    /// </summary>
    /// <remarks>
    ///     The failure this prevents is silent and permanent: a cache holding the temporary answer
    ///     means the compile finishes, the real variant is produced, and the object stays magenta
    ///     forever with nothing logged.
    /// </remarks>
    [Fact]
    public void A_placeholder_is_never_cached() {
        using var device = new NullDevice();
        var loader = new EffectLoader(device);

        var store = new EffectStore();
        store.Add(Variant());

        var system = new EffectSystem();
        system.AddProvider(new EffectSourceProvider(store, loader));
        system.Placeholder = loader.Load(Variant("Placeholder")).AsPlaceholder();

        system.Resolve(Variant().ToKey());

        Assert.Equal(0, system.Count);

        system.Pump();

        // One entry, and it is the real one.
        Assert.Equal(1, system.Count);
        Assert.False(system.Resolve(Variant().ToKey())!.IsPlaceholder);
    }

    /// <summary>A variant asked for every frame is queued once.</summary>
    /// <remarks>
    ///     A thousand objects of one material ask on the same frame, and every frame until it
    ///     arrives. A queue that took them all would compile the same shader a thousand times, which
    ///     is the stall this arrangement exists to avoid, arriving by a different route.
    /// </remarks>
    [Fact]
    public void A_variant_asked_for_every_frame_is_queued_once() {
        using var device = new NullDevice();
        var loader = new EffectLoader(device);

        var store = new EffectStore();
        store.Add(Variant());

        var counted = new Counted(Variant());
        var system = new EffectSystem();
        system.AddProvider(new EffectSourceProvider(counted, loader));
        system.Placeholder = loader.Load(Variant("Placeholder")).AsPlaceholder();

        for (var frame = 0; frame < 10; frame++) {
            system.Resolve(Variant().ToKey());
        }

        Assert.Equal(1, system.PendingCount);
        Assert.Equal(1, system.Pump());
        Assert.Equal(1, counted.Calls);
    }

    /// <summary>
    ///     A key nothing can supply is a miss once, not a compilation per frame.
    /// </summary>
    /// <remarks>
    ///     It keeps drawing the placeholder, which is right — the object is visible and unmistakably
    ///     unfinished — and it stops asking, which is what keeps a bad key from costing a compile
    ///     every frame for as long as it is on screen.
    /// </remarks>
    [Fact]
    public void A_variant_nothing_can_supply_is_asked_for_once() {
        using var device = new NullDevice();
        var loader = new EffectLoader(device);

        var counted = new Counted(null);
        var system = new EffectSystem();
        system.AddProvider(new EffectSourceProvider(counted, loader));
        system.Placeholder = loader.Load(Variant("Placeholder")).AsPlaceholder();

        Assert.True(system.Resolve(Variant().ToKey())!.IsPlaceholder);
        Assert.Equal(0, system.Pump());
        Assert.Equal(Variant().ToKey(), Assert.Single(system.Misses));

        system.Resolve(Variant().ToKey());

        Assert.Equal(0, system.PendingCount);
        Assert.Equal(1, counted.Calls);
    }

    /// <summary>A caller may bound how much compiling one frame pays for.</summary>
    [Fact]
    public void A_pump_produces_at_most_what_it_was_asked_for() {
        using var device = new NullDevice();
        var loader = new EffectLoader(device);

        var store = new EffectStore();
        store.Add(Variant(shadows: true));
        store.Add(Variant(shadows: false));

        var system = new EffectSystem();
        system.AddProvider(new EffectSourceProvider(store, loader));
        system.Placeholder = loader.Load(Variant("Placeholder")).AsPlaceholder();

        system.Resolve(Variant(shadows: true).ToKey());
        system.Resolve(Variant(shadows: false).ToKey());

        Assert.Equal(1, system.Pump(1));
        Assert.Equal(1, system.PendingCount);
        Assert.Equal(1, system.Pump(1));
        Assert.Equal(0, system.PendingCount);
    }

    /// <summary>With no placeholder, resolution is what it always was.</summary>
    /// <remarks>
    ///     The shipping arrangement. There is nothing that could compile later, so there is nothing
    ///     for a placeholder to be a placeholder for — and a miss has to be a miss on the frame it
    ///     happens rather than a queue nobody pumps.
    /// </remarks>
    [Fact]
    public void Without_a_placeholder_a_miss_is_immediate() {
        using var device = new NullDevice();

        var system = new EffectSystem();
        system.AddProvider(new EffectSourceProvider(new EffectStore(), new(device)));

        Assert.Null(system.Resolve(Variant().ToKey()));
        Assert.Equal(0, system.PendingCount);
        Assert.Single(system.Misses);
    }

    // --- The manifest -------------------------------------------------------

    /// <summary>What a run asked for is what a build is told to make.</summary>
    [Fact]
    public void A_manifest_round_trips_through_its_own_text() {
        var keys = new[] {
            Variant(shadows: false).ToKey(),
            Variant(shadows: true).ToKey(),
            EffectKey.Of("ForwardPlus").With(ShaderComposition.Of([new("surface", "MetalRoughnessSurface")]))
        };

        var manifest = EffectManifest.Parse(EffectManifest.Of(keys).ToJson());

        Assert.Equal(keys.Order(Comparer<EffectKey>.Create(static (left, right) => string.CompareOrdinal(left.ToString(), right.ToString()))), manifest.ToKeys());
        Assert.Equal("MetalRoughnessSurface", manifest.Effects.Single(request => request.Shader == "ForwardPlus").Composition["surface"]);
    }

    /// <summary>
    ///     A manifest somebody wrote by hand, leaving out everything that did not apply.
    /// </summary>
    /// <remarks>
    ///     The ordinary shape of the file: most variants have no composition, and plenty have no
    ///     permutations either. It is worth its own test because the JSON source generator builds a
    ///     type whose properties are init-only through an object initializer — which assigns every
    ///     property, so an omitted field arrives as null rather than as its initialiser, and the
    ///     first thing to touch it throws somewhere that says nothing about a manifest.
    /// </remarks>
    [Fact]
    public void A_manifest_may_leave_out_what_does_not_apply() {
        var manifest = EffectManifest.Parse(
            """{ "Effects": [ { "Shader": "Tonemap" }, { "Shader": "Lighting", "Permutations": { "Lighting.UseShadows": "true" } } ] }"""
        );

        Assert.Equal(
            ["Lighting[Lighting.UseShadows=true]", "Tonemap"],
            manifest.ToKeys().Select(key => key.ToString()).Order(StringComparer.Ordinal)
        );
    }

    /// <summary>The same set of keys in a different order is the same manifest.</summary>
    /// <remarks>
    ///     A build input that changed with the order a playthrough happened to ask in would produce a
    ///     different file from every run of the same level, and the diff would be unreadable exactly
    ///     when it mattered.
    /// </remarks>
    [Fact]
    public void A_manifest_does_not_depend_on_the_order_it_was_captured_in() {
        var keys = new[] { Variant(shadows: false).ToKey(), Variant(shadows: true).ToKey() };

        Assert.Equal(
            EffectManifest.Of(keys).ToJson(),
            EffectManifest.Of([keys[1], keys[0], keys[1]]).ToJson()
        );
    }
    /// <summary>A shadow map and a colour map do not share one set layout.</summary>
    /// <remarks>
    ///     ⚠ <b>Everything else about the two bindings is identical.</b> Same binding number, same
    ///     <see cref="DescriptorKind" />, same stages, same count — a shadow map differs from a colour
    ///     map only in its <see cref="DescriptorSampleType" />, which is exactly why leaving it out of
    ///     the key looks harmless. The first of the two to be loaded would hand its layout to the
    ///     second, and a comparison sampler would then be bound through a filtering entry: a
    ///     validation failure at the draw rather than a slightly wrong picture.
    ///     <para>
    ///         The key is asserted rather than the layout, because the whole defect is that the two
    ///         layouts <i>would be</i> the same object. There is nothing to compare downstream of the
    ///         cache once it has answered.
    ///     </para>
    /// </remarks>
    [Fact]
    public void A_depth_binding_and_a_colour_binding_key_two_different_layouts() {
        List<DescriptorBinding> colour = [
            new(0, DescriptorKind.SampledTexture, ShaderStage.Fragment, 1, DescriptorSampleType.Float)
        ];

        List<DescriptorBinding> depth = [
            new(0, DescriptorKind.SampledTexture, ShaderStage.Fragment, 1, DescriptorSampleType.Depth)
        ];

        Assert.NotEqual(
            EffectLoader.Shape(DescriptorSetSlot.PerMaterial, colour, 1),
            EffectLoader.Shape(DescriptorSetSlot.PerMaterial, depth, 1)
        );

        // And the same pair of samplers, which is the half that faults rather than merely reading
        // the wrong texels.
        List<DescriptorBinding> filtering = [new(1, DescriptorKind.Sampler, ShaderStage.Fragment)];
        List<DescriptorBinding> comparison = [
            new(1, DescriptorKind.Sampler, ShaderStage.Fragment, 1, DescriptorSampleType.Depth)
        ];

        Assert.NotEqual(
            EffectLoader.Shape(DescriptorSetSlot.PerMaterial, filtering, 1),
            EffectLoader.Shape(DescriptorSetSlot.PerMaterial, comparison, 1)
        );
    }

}

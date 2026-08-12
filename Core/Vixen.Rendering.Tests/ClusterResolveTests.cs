// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Immutable;
using Vixen.Core.Mathematics;
using Vixen.Graphics;
using Vixen.Graphics.Null;
using Vixen.Rendering;
using Vixen.Rendering.VirtualGeometry;
using Vixen.Shaders;
using Vixen.Shaders.Generated;
using Xunit;

namespace Tests;

/// <summary>
///     The per-material resolve dispatch: one indirect command per material, over the bin the binning
///     pass filled.
/// </summary>
/// <remarks>
///     <para>
///         Two claims worth a test, and they are the two the phase turns on. That a resolve variant is
///         the <em>material's own composition</em> — otherwise "one material tree, two entry contracts"
///         is a sentence rather than a fact. And that each material dispatches against its own bin's
///         arguments — otherwise one material shades another's tiles, which is a picture and a plausible
///         one.
///     </para>
///     <para>
///         The device is the null one and the effects are a fixture, because what is under test is the
///         host's bookkeeping. Whether the shader shades correctly is <c>LibraryTreeTests</c>' business
///         (the composition) and <c>ClusterAttributeTests</c>' (the reconstruction).
///     </para>
/// </remarks>
public sealed class ClusterResolveTests : IDisposable {
    readonly NullDevice device = new(new() { Record = true });
    readonly EffectSystem effects = new();
    readonly ComputePipelineCache pipelines;

    public ClusterResolveTests() {
        effects.AddProvider(new AlwaysCompiles(device));
        pipelines = new(device);
    }

    public void Dispose() {
        device.Dispose();
        GC.SuppressFinalize(this);
    }

    /// <summary>
    ///     A resolve variant carries the material's own composition, and differs from the forward
    ///     variant only in the shader and the gradient permutation.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         <b>The claim the phase is built on, made checkable.</b> A material is one surface feature
    ///         chain and one shading model; if the resolve's key dropped the composition, every material
    ///         would resolve to the same default variant — which compiles, dispatches and shades
    ///         something. Grey, probably, and nobody would look at the key.
    ///     </para>
    ///     <para>
    ///         The gradient permutation is the one legitimate difference: a compute stage has no quad, so
    ///         <c>Sample</c> is undefined there. Everything else about which shader runs comes from the
    ///         material.
    ///     </para>
    /// </remarks>
    [Fact]
    public void A_resolve_variant_is_the_materials_own_composition() {
        var material = Textured();
        var key = GpuClusterResolve.Key(material);

        Assert.Equal("VisibilityResolve", key.ShaderName);
        Assert.Equal(material.Composition, key.Composition);

        // The gradients are on, which is what makes the sampling defined in a stage with no quad.
        Assert.Contains(key.Values, pair => pair.Key == GpuClusterResolve.AnalyticGradientsKey && pair.Value == "true");

        // Two materials with different compositions are two variants; the same material twice is one.
        Assert.NotEqual(key, GpuClusterResolve.Key(Untextured()));
        Assert.Equal(key, GpuClusterResolve.Key(material));

        // And the ambient permutation reaches the key, so a project that turns IBL off gets a variant
        // without it rather than one that samples an unbound cube.
        Assert.NotEqual(key, GpuClusterResolve.Key(material, imageBasedLighting: false));

        // The ambient split too — the same key name the forward pass compiles under, because the two
        // paths shade the same frame and must split together or not at all.
        Assert.NotEqual(key, GpuClusterResolve.Key(material, splitOutputs: true));

        Assert.Contains(
            GpuClusterResolve.Key(material, splitOutputs: true).Values,
            pair => pair.Key == VisibilityResolveKeys.SplitOutputs.Name && pair.Value == "true"
        );
    }

    /// <summary>
    ///     The split's three planes are filled in every variant, and with a view that exists — the
    ///     caller's planes when the split is on, the colour aliased when it is off.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         The fixture's set 2 declares all three planes exactly as the reflection does, so
    ///         <c>EffectSetWriter</c> refuses the whole set if any of them goes unfilled — which is why
    ///         part of the assertion here is that Prepare still succeeds, with and without planes of
    ///         its own to bind.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>That half alone does not catch a forgotten alias, and it used to be the whole
    ///         test.</b> <c>EffectSetWriter</c> asks whether a name is <em>set</em>, not whether what
    ///         it is set to exists — so a <c>Prepare</c> that passed the caller's invalid plane
    ///         straight through instead of aliasing the colour into it produced a complete set of
    ///         well-formed descriptors, one of which named nothing, and this test passed. Verified by
    ///         doing exactly that. The absent case was guarded and the wrong-handle case was not,
    ///         which is the asymmetry worth naming: a set written short refuses every draw and is
    ///         noticed within a frame, while a descriptor pointing at a dead slot samples black on one
    ///         backend and is undefined on another.
    ///     </para>
    ///     <para>
    ///         So the handles are checked where they actually land — in the writes the device was
    ///         handed. A stand-in for a pass that is off is legitimate and this engine uses several,
    ///         but every one of them is a <em>valid</em> handle standing in for absent data. An
    ///         invalid one is not a stand-in; it is a binding nobody filled that says it was.
    ///     </para>
    /// </remarks>
    [Fact]
    public void The_split_planes_are_filled_in_every_variant() {
        using var visibility = Registered(out var pages, out var source);
        using var pool = new MeshletPagePool(device, source, pages.Pages.Length, pages.PageSize);
        using var tiles = new GpuVisibilityTiles(device) { Effects = effects, Pipelines = pipelines, Visibility = visibility };
        using var resolve = Resolve(visibility, tiles, pool);

        resolve.Materials = [new(Textured(), 0)];

        var identities = Identities();
        Record(list => tiles.Record(list, identities, new(64, 64)));

        // Off: no planes handed in, the colour aliased into all three slots, the set complete.
        Assert.True(resolve.Prepare(Camera(), Target(), identities, new(64, 64)));
        Assert.Equal(0, resolve.Unresolved);
        EveryViewNamesSomething();

        // On: the caller's own planes.
        resolve.SplitOutputs = true;

        Assert.True(resolve.Prepare(Camera(), Target(), identities, new(64, 64), Target(), Target(), Target()));
        Assert.Equal(0, resolve.Unresolved);
        EveryViewNamesSomething();

        // ⚠ And a caller that hands in only the two the split had before the f0 plane: the third
        // slot still has to be filled, because a set is written wholly or not at all and the
        // alternative to an alias is not a missing plane but every dispatch refused. This is the
        // case the validity check exists for — the alias that was written once and not extended
        // when a plane arrived beside it.
        Assert.True(resolve.Prepare(Camera(), Target(), identities, new(64, 64), Target(), Target()));
        Assert.Equal(0, resolve.Unresolved);
        EveryViewNamesSomething();
    }

    /// <summary>
    ///     Every texture descriptor the device has been handed so far points at a view that exists.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         Over the whole recorded stream rather than the last set, because the claim is about the
    ///         pass and not about one call: a plane aliased correctly in one variant and forgotten in
    ///         another is the same defect.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The views and not the buffers, and the reason is about this fixture rather than
    ///         about the rule.</b> Every storage buffer in the resolve's set comes off
    ///         <c>GpuClusterVisibility</c>, which creates them in <c>EnsureBuffers</c> — reached only
    ///         from its own <c>Prepare</c>, which a real frame runs before the resolve's and this
    ///         fixture does not. So <c>visible</c>, <c>geometry</c>, <c>meshes</c>, <c>residency</c>
    ///         and <c>clusterMaterials</c> are written here naming nothing, and asserting on them
    ///         would be asserting on a state no frame has. The views are the handles this test
    ///         actually hands in, which makes them the ones its arguments are a claim about.
    ///     </para>
    /// </remarks>
    void EveryViewNamesSomething() =>
        Assert.All(
            device.RecordedWrites!.Where(
                write => write.Kind is DescriptorKind.SampledTexture or DescriptorKind.StorageTexture
            ),
            write => Assert.True(
                write.TextureView.IsValid,
                $"Binding {write.Binding} was written as a {write.Kind} naming nothing. The set is "
                + "complete and the descriptor is well-formed, which is exactly why this is not "
                + "visible anywhere else: a host resolved the name to a handle it never created."
            )
        );

    /// <summary>
    ///     Each material dispatches against its own bin's arguments, and only the prepared ones dispatch.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         The offset is the whole content: the binning pass wrote a tile count into each material's
    ///         argument triple, and a dispatch reading the wrong triple launches another material's
    ///         workgroup count over this material's tile list. That shades the right pixels the wrong
    ///         number of times, which is a hole or an overdraw depending on which way the counts differ.
    ///     </para>
    ///     <para>
    ///         Asserted through the recorded command stream, because an indirect dispatch has nothing to
    ///         observe on the host — the count is a word on the device, which is the point.
    ///     </para>
    /// </remarks>
    [Fact]
    public void Every_material_dispatches_against_its_own_bin() {
        using var visibility = Registered(out var pages, out var source);
        using var pool = new MeshletPagePool(device, source, pages.Pages.Length, pages.PageSize);
        using var tiles = new GpuVisibilityTiles(device) { Effects = effects, Pipelines = pipelines, Visibility = visibility };
        using var resolve = Resolve(visibility, tiles, pool);

        resolve.Materials = [new(Textured(), 0), new(Untextured(), 3)];

        // The binning has to have run, because that is what creates the argument buffer the dispatch
        // names. One recorded frame of it, submitted, so the Null device sees the whole sequence.
        var identities = Identities();
        Record(list => tiles.Record(list, identities, new(64, 64)));

        Assert.True(resolve.Prepare(Camera(), Target(), identities, new(64, 64)));
        Assert.Equal(2, resolve.ResolvedMaterials);
        Assert.Equal(0, resolve.Unresolved);

        var recorded = Record(list => resolve.Record(list));

        var dispatches = recorded
            .Where(command => command.Kind == RecordedCommandKind.DispatchIndirect)
            .Select(command => command.B)
            .ToArray();

        Assert.Equal(2, dispatches.Length);
        Assert.Contains(GpuVisibilityTiles.ArgumentOffset(0), dispatches);
        Assert.Contains(GpuVisibilityTiles.ArgumentOffset(3), dispatches);

        // And bin one and bin two, which no material was bound to, dispatch nothing rather than reading
        // a triple the binning left at zero.
        Assert.DoesNotContain(GpuVisibilityTiles.ArgumentOffset(1), dispatches);
        Assert.DoesNotContain(GpuVisibilityTiles.ArgumentOffset(2), dispatches);
    }

    /// <summary>
    ///     A bin with no material dispatches nothing, and says so.
    /// </summary>
    /// <remarks>
    ///     Three ways for a bin to have no variant — no material bound to it, an index past the ceiling,
    ///     and a variant still compiling — and all three are a hole in the picture rather than a wrong
    ///     colour. That is the right outcome and the reason it needs a counter: a hole that fills itself
    ///     once a variant lands is a different thing from one that never will, and neither is visible in
    ///     a screenshot as anything but a hole.
    /// </remarks>
    [Fact]
    public void A_bin_with_no_material_is_counted_rather_than_drawn() {
        using var visibility = Registered(out var pages, out var source);
        using var pool = new MeshletPagePool(device, source, pages.Pages.Length, pages.PageSize);
        using var tiles = new GpuVisibilityTiles(device) { Effects = effects, Pipelines = pipelines, Visibility = visibility };
        using var resolve = Resolve(visibility, tiles, pool);

        resolve.Materials = [new(null!, 0), new(Textured(), GpuVisibilityTiles.MaxMaterials), new(Textured(), -1)];

        var identities = Identities();
        Record(list => tiles.Record(list, identities, new(64, 64)));

        Assert.False(resolve.Prepare(Camera(), Target(), identities, new(64, 64)));
        Assert.Equal(0, resolve.ResolvedMaterials);
        Assert.Equal(3, resolve.Unresolved);

        Assert.DoesNotContain(
            Record(list => resolve.Record(list)),
            command => command.Kind == RecordedCommandKind.DispatchIndirect
        );
    }

    /// <summary>
    ///     A prepared bin dispatches once per preparation, not once per frame it was ever prepared in.
    /// </summary>
    /// <remarks>
    ///     The failure being ruled out is a set bound to a stale block: a bin whose `Prepare` did not run
    ///     this frame holds last frame's tile base and last frame's camera, and dispatching it again
    ///     shades this frame's tiles with them. Cheaper to make readiness a per-frame fact than to make
    ///     every caller remember.
    /// </remarks>
    [Fact]
    public void A_bin_dispatches_once_per_preparation() {
        using var visibility = Registered(out var pages, out var source);
        using var pool = new MeshletPagePool(device, source, pages.Pages.Length, pages.PageSize);
        using var tiles = new GpuVisibilityTiles(device) { Effects = effects, Pipelines = pipelines, Visibility = visibility };
        using var resolve = Resolve(visibility, tiles, pool);

        resolve.Materials = [new(Textured(), 0)];

        var identities = Identities();
        Record(list => tiles.Record(list, identities, new(64, 64)));
        Assert.True(resolve.Prepare(Camera(), Target(), identities, new(64, 64)));

        Assert.Equal(1, resolve.Record(Open()));
        Assert.Equal(0, resolve.Record(Open()));

        // Prepared again, so it dispatches again — with this frame's numbers in the block.
        Assert.True(resolve.Prepare(Camera(), Target(), identities, new(64, 64)));
        Assert.Equal(1, resolve.Record(Open()));
    }

    GpuClusterResolve Resolve(GpuClusterVisibility visibility, GpuVisibilityTiles tiles, MeshletPagePool pool) =>
        new(device) {
            Effects = effects,
            Pipelines = pipelines,
            Visibility = visibility,
            Tiles = tiles,
            Pages = pool
        };

    GpuClusterVisibility Registered(out MeshletPageSet pages, out MemoryMeshletPageSource source) {
        var input = Sphere(8, 16);
        var mesh = MeshletBuilder.Build(input);

        pages = MeshletPageBuilder.Build(mesh, input.Positions, [], new() { PageSize = 4 * 1024 });

        var registry = new MemoryMeshletPageSource();
        registry.Add(0, pages);
        source = registry;

        var visibility = new GpuClusterVisibility(device);
        visibility.Register(mesh, pages, 0);
        visibility.Begin(1);
        visibility.Set(0, new() { Flags = GpuCulling.Alive, Scale = 1f });
        visibility.UploadInstances();

        return visibility;
    }

    IReadOnlyList<RecordedCommand> Record(Action<ICommandList> record) {
        var list = device.BeginCommandList(QueueKind.Compute);
        record(list);
        list.Finish();
        device.ComputeQueue.Submit([list]);

        return device.Recorder!.Commands;
    }

    ICommandList Open() {
        var list = device.BeginCommandList(QueueKind.Compute);
        return list;
    }

    TextureViewHandle Identities() =>
        device.CreateTextureView(
            device.CreateTexture(
                new(GpuClusterRaster.Format, 64, 64, TextureUsage.ColourTarget | TextureUsage.Sampled, Name: "Identities")
            )
        );

    TextureViewHandle Target() =>
        device.CreateTextureView(
            device.CreateTexture(
                new(PixelFormat.Rgba16Float, 64, 64, TextureUsage.Storage | TextureUsage.Sampled, Name: "SceneColour")
            )
        );

    static RenderView Camera() {
        var view = Matrix4x4.LookAt(Vector3.Zero, new(0f, 0f, 1f), new(0f, 1f, 0f));
        var projection = Matrix4x4.PerspectiveFieldOfView(MathF.PI / 3f, 1f, 0.1f, 1000f);

        return new("camera") { Position = Vector3.Zero, ViewProjection = view * projection };
    }

    static Material Textured() =>
        new("ForwardPlus") {
            Composition = ShaderComposition.Of(
                [
                    new("surface", "TexturedMetalRoughnessSurface"),
                    new("shading", "StandardShading")
                ]
            )
        };

    static Material Untextured() =>
        new("ForwardPlus") {
            Composition = ShaderComposition.Of(
                [
                    new("surface", "MetalRoughnessSurface"),
                    new("shading", "SheenShading")
                ]
            )
        };

    /// <summary>A closed UV sphere: one vertex per pole, and the seam welded.</summary>
    static MeshletBuildInput Sphere(int rings, int segments) {
        var positions = new List<Vector3> { new(0f, 1f, 0f) };

        for (var ring = 1; ring < rings; ring++) {
            var phi = MathF.PI * ring / rings;

            for (var segment = 0; segment < segments; segment++) {
                var theta = 2f * MathF.PI * segment / segments;

                positions.Add(
                    new(MathF.Sin(phi) * MathF.Cos(theta), MathF.Cos(phi), MathF.Sin(phi) * MathF.Sin(theta))
                );
            }
        }

        positions.Add(new(0f, -1f, 0f));

        var indices = new List<int>();
        var last = positions.Count - 1;

        int At(int ring, int segment) => 1 + ((ring - 1) * segments) + (segment % segments);

        for (var segment = 0; segment < segments; segment++) {
            indices.AddRange([0, At(1, segment + 1), At(1, segment)]);
            indices.AddRange([last, At(rings - 1, segment), At(rings - 1, segment + 1)]);
        }

        for (var ring = 1; ring < rings - 1; ring++) {
            for (var segment = 0; segment < segments; segment++) {
                var a = At(ring, segment);
                var b = At(ring, segment + 1);
                var c = At(ring + 1, segment);
                var d = At(ring + 1, segment + 1);

                indices.AddRange([a, b, c]);
                indices.AddRange([b, d, c]);
            }
        }

        return new() { Positions = [.. positions], Indices = [.. indices] };
    }

    /// <summary>
    ///     Every variant, with the real binding list for the two compute passes.
    /// </summary>
    /// <remarks>
    ///     The bindings are not decoration: <see cref="EffectSetWriter" /> fills a set from the effect's
    ///     own plan and refuses one it cannot fill entirely, so a fixture that declared fewer bindings
    ///     than the shader would assert that a host filling nothing fills nothing.
    /// </remarks>
    sealed class AlwaysCompiles(NullDevice device) : IEffectProvider {
        // Set 2 exactly as VisibilityResolve.reflect.json reports it — the resolve's own resources, and
        // nothing of the lighting: since that moved into ClusteredShading the sun, the cascades and the
        // environment are the frame's sets, bound by whatever already fills them for a forward draw.
        static readonly ImmutableArray<EffectBinding> ResolveBindings = [
            new("constants", DescriptorSetSlot.PerMaterial, 0, DescriptorKind.UniformBuffer) { Size = 256 },
            new("identities", DescriptorSetSlot.PerMaterial, 1, DescriptorKind.SampledTexture),
            new("visible", DescriptorSetSlot.PerMaterial, 2, DescriptorKind.StorageBuffer),
            new("instances", DescriptorSetSlot.PerMaterial, 3, DescriptorKind.StorageBuffer),
            new("geometry", DescriptorSetSlot.PerMaterial, 4, DescriptorKind.StorageBuffer),
            new("meshes", DescriptorSetSlot.PerMaterial, 5, DescriptorKind.StorageBuffer),
            new("residency", DescriptorSetSlot.PerMaterial, 6, DescriptorKind.StorageBuffer),
            new("pages", DescriptorSetSlot.PerMaterial, 7, DescriptorKind.StorageBuffer),
            new("clusterMaterials", DescriptorSetSlot.PerMaterial, 8, DescriptorKind.StorageBuffer),
            new("tiles", DescriptorSetSlot.PerMaterial, 9, DescriptorKind.StorageBuffer),
            new("target", DescriptorSetSlot.PerMaterial, 10, DescriptorKind.StorageTexture),

            // The ambient split's three planes. In the set for every variant — a binding is
            // declared, not read into existence — which is what obliges Prepare to fill them even
            // with the split off, and what this fixture exists to hold it to: leave one out here and
            // a Prepare that forgot its alias would still return true.
            new("albedoTarget", DescriptorSetSlot.PerMaterial, 11, DescriptorKind.StorageTexture),
            new("normalTarget", DescriptorSetSlot.PerMaterial, 12, DescriptorKind.StorageTexture),
            new("specularTarget", DescriptorSetSlot.PerMaterial, 13, DescriptorKind.StorageTexture)
        ];

        static readonly ImmutableArray<EffectBinding> TileBindings = [
            new("constants", DescriptorSetSlot.PerMaterial, 0, DescriptorKind.UniformBuffer) { Size = 16 },
            new("identities", DescriptorSetSlot.PerMaterial, 1, DescriptorKind.SampledTexture),
            new("instances", DescriptorSetSlot.PerMaterial, 2, DescriptorKind.StorageBuffer),
            new("clusterMaterials", DescriptorSetSlot.PerMaterial, 3, DescriptorKind.StorageBuffer),
            new("visible", DescriptorSetSlot.PerMaterial, 4, DescriptorKind.StorageBuffer),
            new("tiles", DescriptorSetSlot.PerMaterial, 5, DescriptorKind.StorageBuffer),
            new("arguments", DescriptorSetSlot.PerMaterial, 6, DescriptorKind.StorageBuffer)
        ];

        readonly DescriptorSetLayoutHandle resolveLayout = Layout(device, ResolveBindings, "VisibilityResolve");
        readonly DescriptorSetLayoutHandle tileLayout = Layout(device, TileBindings, "VisibilityTiles");

        public Effect? TryGet(EffectKey key) =>
            key.ShaderName switch {
                "VisibilityResolve" => Compiled(key, ResolveBindings, resolveLayout),
                "VisibilityTiles" => Compiled(key, TileBindings, tileLayout),
                _ => new() { Key = key, Stages = [new(ShaderStage.Compute, [1, 2, 3, 4], "main")] }
            };

        static Effect Compiled(EffectKey key, ImmutableArray<EffectBinding> bindings, DescriptorSetLayoutHandle layout) =>
            new() {
                Key = key,
                Stages = [new(ShaderStage.Compute, [1, 2, 3, 4], "main")],
                SetLayouts = [default, default, layout, default, default],

                // Not decoration: EffectSetWriter fills the uniform binding from the block, and a block
                // whose size is zero fills nothing — so a fixture that left this out would assert that a
                // host which binds nothing binds nothing.
                ConstantBufferSize = bindings[0].Size,
                Bindings = bindings
            };

        static DescriptorSetLayoutHandle Layout(NullDevice device, ImmutableArray<EffectBinding> bindings, string name) =>
            device.CreateDescriptorSetLayout(
                new(
                    DescriptorSetSlot.PerMaterial,
                    [.. bindings.Select(b => new DescriptorBinding(b.Binding, b.Kind, ShaderStage.Compute))],
                    name
                )
            );
    }
}

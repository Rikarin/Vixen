// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Immutable;
using Vixen.Core.Mathematics;
using Vixen.Graphics;
using Vixen.Graphics.Null;
using Vixen.Rendering;
using Vixen.Rendering.Features;
using Vixen.Shaders;
using Xunit;

namespace Tests;

/// <summary>
///     Two stages that impose the same shader, and the per-material set each of them is bound from.
/// </summary>
/// <remarks>
///     <para>
///         <strong>A shadow caster's set belongs to the stage, not to the material.</strong>
///         <c>ShadowCaster</c> declares an opacity map, a sampler and a bone palette that no material
///         in any project has a name for, because they belong to a pass no material has heard of —
///         <see cref="RenderStage.Parameters" /> is where they come from, and
///         <c>MaterialRenderFeature.Bind</c> uses the imposing stage's collection as the fallback for
///         every binding the material cannot fill.
///     </para>
///     <para>
///         ⚠ <b>Which is why the imposing stage is part of a variant's identity, and why it used not
///         to be.</b> Keyed on <c>(material, flags, shader)</c> alone, two stages naming one shader
///         collapsed to one variant and whichever of them reached a material <em>first</em> decided
///         what every stage drew with. A frame with one caster stage could never notice. Splitting the
///         cascades' casters into a cached half and a moving half made the second stage's objects
///         resolve a variant whose fallback collection was empty — and a set is written wholly or not
///         at all, so the pipeline was bound, the draw recorded, and set 2 left empty: a validation
///         message with the layers on, and a segfault inside <c>vkQueueSubmit</c> on macOS, where
///         they are not installed.
///     </para>
/// </remarks>
public sealed class CasterStageParameterTests : IDisposable {
    const uint OpacityBinding = 0;
    const uint OpacitySamplerBinding = 1;

    static readonly ParameterKey<TextureViewHandle> CasterOpacity =
        ParameterKeys.New<TextureViewHandle>("ShadowCaster.opacityMap");

    static readonly ParameterKey<SamplerHandle> CasterOpacitySampler =
        ParameterKeys.New<SamplerHandle>("ShadowCaster.opacitySampler");

    static readonly ParameterKey<TextureViewHandle> Albedo = ParameterKeys.New<TextureViewHandle>("Lit.albedo");
    static readonly ParameterKey<SamplerHandle> AlbedoSampler = ParameterKeys.New<SamplerHandle>("Lit.albedoSampler");

    readonly NullDevice device = new(new() { Record = true });
    readonly EffectSystem effects = new();
    readonly DescriptorAllocator allocator;
    readonly DescriptorSetLayoutHandle litLayout;
    readonly DescriptorSetLayoutHandle casterLayout;
    readonly SamplerHandle sampler;

    /// <summary>One stand-in per caster stage, so a set says which stage filled it.</summary>
    TextureViewHandle MoverOpacity { get; }

    TextureViewHandle StaticOpacity { get; }

    public CasterStageParameterTests() {
        allocator = new(device);
        sampler = device.CreateSampler(new());
        MoverOpacity = Texture();
        StaticOpacity = Texture();

        litLayout = device.CreateDescriptorSetLayout(
            new(
                DescriptorSetSlot.PerMaterial,
                [
                    new(OpacityBinding, DescriptorKind.SampledTexture, ShaderStage.Fragment),
                    new(OpacitySamplerBinding, DescriptorKind.Sampler, ShaderStage.Fragment)
                ],
                "Lit"
            )
        );

        casterLayout = device.CreateDescriptorSetLayout(
            new(
                DescriptorSetSlot.PerMaterial,
                [
                    new(OpacityBinding, DescriptorKind.SampledTexture, ShaderStage.Fragment),
                    new(OpacitySamplerBinding, DescriptorKind.Sampler, ShaderStage.Fragment)
                ],
                "ShadowCaster"
            )
        );

        effects.AddProvider(new Compiles(litLayout, casterLayout));
    }

    public void Dispose() {
        allocator.Dispose();
        device.Dispose();
    }

    /// <summary>
    ///     <c>ShadowCaster</c> declares a per-material set no material has a name for, exactly as
    ///     <c>Library/Pipeline/ShadowCaster.rvn</c> does.
    /// </summary>
    sealed class Compiles(DescriptorSetLayoutHandle lit, DescriptorSetLayoutHandle caster) : IEffectProvider {
        public Effect? TryGet(EffectKey key) =>
            key.ShaderName switch {
                "ShadowCaster" => new() {
                    Key = key,
                    Stages = Modules,
                    SetLayouts = [default, default, caster, default],
                    Bindings = [
                        new("opacityMap", DescriptorSetSlot.PerMaterial, OpacityBinding, DescriptorKind.SampledTexture),
                        new("opacitySampler", DescriptorSetSlot.PerMaterial, OpacitySamplerBinding, DescriptorKind.Sampler)
                    ]
                },
                _ => new() {
                    Key = key,
                    Stages = Modules,
                    SetLayouts = [default, default, lit, default],
                    Bindings = [
                        new("albedo", DescriptorSetSlot.PerMaterial, OpacityBinding, DescriptorKind.SampledTexture),
                        new("albedoSampler", DescriptorSetSlot.PerMaterial, OpacitySamplerBinding, DescriptorKind.Sampler)
                    ]
                }
            };

        static ImmutableArray<EffectStage> Modules =>
            [new(ShaderStage.Vertex, [1, 2, 3, 4], "main"), new(ShaderStage.Fragment, [5, 6, 7, 8], "main")];
    }

    sealed class Harness : IDisposable {
        public required RenderSystem System { get; init; }
        public required RenderStage Opaque { get; init; }
        public required RenderStage Movers { get; init; }
        public required RenderStage Statics { get; init; }
        public required RenderView View { get; init; }
        public required MeshRenderFeature Meshes { get; init; }
        public required MaterialRenderFeature Materials { get; init; }

        public void Dispose() => System.Dispose();
    }

    /// <summary>
    ///     The sample's frame in miniature: two caster stages naming one shader, and only the first of
    ///     them given the stand-ins that shader's set needs.
    /// </summary>
    Harness Build(bool fillStatics = false) {
        var system = new RenderSystem();
        var opaque = system.AddStage(new("Opaque"));
        var movers = system.AddStage(new("Shadow") { ShaderName = "ShadowCaster" });
        var statics = system.AddStage(new("ShadowStatic") { ShaderName = "ShadowCaster" });

        // What ArenaFrame.ApplyCaster does, on the one stage Arena.cs hands it.
        movers.Parameters.Set(CasterOpacity, MoverOpacity);
        movers.Parameters.Set(CasterOpacitySampler, sampler);

        // And what a host that filled both would say — with a stand-in of its own, so that "which
        // stage's collection did this set come from" is answerable by comparing the sets.
        if (fillStatics) {
            statics.Parameters.Set(CasterOpacity, StaticOpacity);
            statics.Parameters.Set(CasterOpacitySampler, sampler);
        }

        var meshes = new MeshRenderFeature();
        var materials = new MaterialRenderFeature { Effects = effects, Device = device, Descriptors = allocator };

        meshes.Add(materials);
        system.AddFeature(meshes);

        var view = Matrix4x4.LookAt(Vector3.Zero, new(0f, 0f, 1f), new(0f, 1f, 0f));
        var projection = Matrix4x4.PerspectiveFieldOfView(MathF.PI / 3f, 1f, 0.1f, 1000f);

        var camera = new RenderView("camera") {
            Stages = opaque.Mask | movers.Mask | statics.Mask,
            Position = Vector3.Zero,
            Frustum = new(view * projection)
        };

        system.SetViews([camera]);

        return new() {
            System = system,
            Opaque = opaque,
            Movers = movers,
            Statics = statics,
            View = camera,
            Meshes = meshes,
            Materials = materials
        };
    }

    RenderObjectId AddMesh(Harness h, RenderStageMask stages, Material? shared = null) {
        var id = h.System.Objects.Add(
            new() { Bounds = new(new Vector3(0f, 0f, 10f), 1f), Stages = stages, FeatureIndex = h.Meshes.Index }
        );

        h.Materials.Assign(h.System, id, shared ?? Lit());
        return id;
    }

    Material Lit() {
        var material = new Material("Lit");

        material.Parameters.Set(Albedo, Texture());
        material.Parameters.Set(AlbedoSampler, sampler);

        return material;
    }

    TextureViewHandle Texture() =>
        device.CreateTextureView(
            device.CreateTexture(
                new() {
                    Width = 4, Height = 4, Depth = 1, MipLevels = 1, ArrayLayers = 1, SampleCount = 1,
                    Format = PixelFormat.Rgba8UNorm, Usage = TextureUsage.Sampled
                }
            )
        );

    void Frame(Harness h) {
        allocator.BeginFrame();
        h.System.Draw();
    }

    // --- What a stage owes the shader it imposes -----------------------------

    /// <summary>
    ///     A stage that imposes a shader and fills none of its bindings writes no set, and is counted.
    /// </summary>
    /// <remarks>
    ///     ⚠ The failure this file exists for, and it is silent by construction: the effect resolves,
    ///     the pipeline is built, the draw is recorded, and the set the pipeline statically uses was
    ///     never bound. <see cref="MaterialRenderFeature.UnboundCount" /> and
    ///     <see cref="MaterialRenderFeature.Unbound" /> are what turn that into a number and a name.
    /// </remarks>
    [Fact]
    public void An_unfilled_caster_stage_writes_no_set_and_says_which() {
        using var h = Build();
        var id = AddMesh(h, h.Opaque.Mask | h.Statics.Mask);

        Frame(h);

        // The variant resolves — this is not a compilation failure.
        Assert.NotNull(h.Materials.EffectOf(h.System, id, h.Statics));

        Assert.False(h.Materials.DescriptorsOf(h.System, id, h.Statics).IsValid);
        Assert.Equal(1, h.Materials.UnboundCount);
        Assert.Equal(("ShadowCaster", "ShadowStatic"), h.Materials.Unbound);
    }

    /// <summary>And a stage whose parameters the host did fill is bound, and counted as bound.</summary>
    [Fact]
    public void A_filled_caster_stage_is_bound() {
        using var h = Build(fillStatics: true);
        var id = AddMesh(h, h.Opaque.Mask | h.Statics.Mask);

        Frame(h);

        Assert.True(h.Materials.DescriptorsOf(h.System, id, h.Statics).IsValid);
        Assert.Equal(0, h.Materials.UnboundCount);
        Assert.Null(h.Materials.Unbound);
    }

    // --- One material, two stages -------------------------------------------

    /// <summary>
    ///     Two objects of one material, one per caster stage, are each bound from their own stage.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         <strong>The regression.</strong> One variant per (material, stage) rather than per
    ///         material, so the stage a set was written for is a property of the draw rather than of
    ///         which object happened to be extracted first.
    ///     </para>
    ///     <para>
    ///         Asserted by comparing the sets, which makes it a claim about the bytes rather than about
    ///         a handle being valid: the allocator is content-addressed, so two sets holding the same
    ///         texture and sampler <em>are</em> one set. Two stages whose stand-ins differ must not
    ///         collapse into one.
    ///     </para>
    /// </remarks>
    [Fact]
    public void Each_caster_stage_is_bound_from_its_own_parameters() {
        using var h = Build(fillStatics: true);

        // One material, which is what makes this one material's worth of variants — two crates out of
        // the same material, one of them nailed down.
        var material = Lit();
        var mover = AddMesh(h, h.Opaque.Mask | h.Movers.Mask, material);
        var stationary = AddMesh(h, h.Opaque.Mask | h.Statics.Mask, material);

        Frame(h);

        var moving = h.Materials.DescriptorsOf(h.System, mover, h.Movers);
        var still = h.Materials.DescriptorsOf(h.System, stationary, h.Statics);

        Assert.True(moving.IsValid);
        Assert.True(still.IsValid);
        Assert.NotEqual(moving, still);
    }

    /// <summary>
    ///     Two stages that fill their parameters identically still cost one descriptor set.
    /// </summary>
    /// <remarks>
    ///     The economy the extra variants were weighed against. A variant is a record and a resolve;
    ///     the <see cref="Effect" /> behind it comes from <see cref="EffectSystem" />'s own cache and is
    ///     shared, and the set is the allocator's, which hands back the same handle for the same
    ///     writes. So two stages that agree cost a list entry and nothing on the device.
    /// </remarks>
    [Fact]
    public void Two_caster_stages_that_agree_share_one_set() {
        using var h = Build();

        // The static stage filled with the *same* stand-ins as the mover stage.
        h.Statics.Parameters.Set(CasterOpacity, MoverOpacity);
        h.Statics.Parameters.Set(CasterOpacitySampler, sampler);

        var material = Lit();
        var mover = AddMesh(h, h.Opaque.Mask | h.Movers.Mask, material);
        var stationary = AddMesh(h, h.Opaque.Mask | h.Statics.Mask, material);

        Frame(h);

        Assert.Equal(
            h.Materials.DescriptorsOf(h.System, mover, h.Movers),
            h.Materials.DescriptorsOf(h.System, stationary, h.Statics)
        );
    }

    /// <summary>An object in both caster stages is bound in both.</summary>
    /// <remarks>
    ///     The control the bisection turned on: with the stage out of the variant's identity, two
    ///     stages holding the <em>same</em> objects worked and two stages holding disjoint ones did
    ///     not, which is what made the defect look like a property of the render graph.
    /// </remarks>
    [Fact]
    public void A_caster_in_both_stages_is_bound_in_both() {
        using var h = Build(fillStatics: true);
        var id = AddMesh(h, h.Opaque.Mask | h.Movers.Mask | h.Statics.Mask);

        Frame(h);

        Assert.True(h.Materials.DescriptorsOf(h.System, id, h.Movers).IsValid);
        Assert.True(h.Materials.DescriptorsOf(h.System, id, h.Statics).IsValid);
        Assert.Equal(0, h.Materials.UnboundCount);
    }
}

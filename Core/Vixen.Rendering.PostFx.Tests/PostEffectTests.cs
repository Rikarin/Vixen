// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;
using Vixen.Engine.Cameras;
using Vixen.Graphics;
using Vixen.Graphics.Null;
using Vixen.Graphics.RenderGraph;
using Vixen.Rendering;
using Vixen.Rendering.Compositor;
using Vixen.Rendering.Ecs;
using Vixen.Rendering.Lighting;
using Vixen.Rendering.PostFx;
using Vixen.Shaders;
using Vixen.Shaders.Generated;
using Xunit;

namespace Tests;

/// <summary>
///     The effect set: what each pass declares, reads and binds.
/// </summary>
/// <remarks>
///     <para>
///         Every one of these is a shader that existed and had nothing calling it, so the claim worth
///         testing is not "the arithmetic is right" — that is the shader's, and the golden fixtures'.
///         It is that a pass runs the shader it says it does, reads the textures it binds, binds them
///         where that shader's reflection says, and declares a target the graph can find.
///     </para>
///     <para>
///         Driven through the Null backend, so what is asserted is the commands that were recorded
///         rather than an intention. A pass that declared a read it never bound, or bound a texture at
///         a number nobody assigned, produces a frame that is wrong in a way only a device would
///         notice — which is exactly what the recording device is for.
///     </para>
/// </remarks>
public class PostEffectTests : IDisposable {
    // The pass that reads whatever effect is under test. Not a shipped shader — nothing here is
    // asserting about a copy — so its three bindings are named here rather than generated.
    const string ConsumerShader = "Copy";
    const uint ConsumerConstantBinding = 0;
    const uint ConsumerSourceBinding = 1;
    const uint ConsumerSamplerBinding = 2;

    readonly NullDevice device = new(new() { Record = true, FramesInFlight = 2 });
    readonly EffectSystem effects = new();
    readonly DescriptorAllocator allocator;
    readonly SamplerCache samplers;
    readonly EffectPipelineDescriber describer;
    readonly Dictionary<string, DescriptorSetLayoutHandle> layouts = [];

    public PostEffectTests() {
        allocator = new(device);
        samplers = new(device);
        describer = new(device);

        // A layout per shader, each set 2 in the shape that shader's reflection reports.
        //
        // One layout wide enough for all of them looks like it would serve — the outline has the most
        // bindings and every other effect's are a prefix of the count — but the effects do not agree
        // on what an index *holds*: Fxaa's sampler is binding 2, where the outline has a texture, and
        // the ambient occlusion pass's is binding 3, where the outline has another. A shared layout is
        // therefore a set most of these passes write a sampler into a texture binding of, which is
        // undefined on a device, silent on a driver, and what the Null backend's kind check catches.
        //
        // The indices are the generated constants rather than numbers written here, for the reason
        // the effects themselves take them from there: Raven assigns a binding index from declaration
        // order within a set, so adding a texture above another in the .rvn renumbers everything below.
        Declare(
            FxaaKeys.ShaderName,
            new(FxaaKeys.ConstantBufferBinding, DescriptorKind.UniformBuffer, ShaderStage.Fragment),
            new(FxaaKeys.SourceBinding, DescriptorKind.SampledTexture, ShaderStage.Fragment),
            new(FxaaKeys.SourceSamplerBinding, DescriptorKind.Sampler, ShaderStage.Fragment)
        );

        Declare(
            SharpenKeys.ShaderName,
            new(SharpenKeys.ConstantBufferBinding, DescriptorKind.UniformBuffer, ShaderStage.Fragment),
            new(SharpenKeys.SourceBinding, DescriptorKind.SampledTexture, ShaderStage.Fragment),
            new(SharpenKeys.PointSamplerBinding, DescriptorKind.Sampler, ShaderStage.Fragment)
        );

        Declare(
            VignetteKeys.ShaderName,
            new(VignetteKeys.ConstantBufferBinding, DescriptorKind.UniformBuffer, ShaderStage.Fragment),
            new(VignetteKeys.SourceBinding, DescriptorKind.SampledTexture, ShaderStage.Fragment),
            new(VignetteKeys.LinearSamplerBinding, DescriptorKind.Sampler, ShaderStage.Fragment)
        );

        Declare(
            FogKeys.ShaderName,
            new(FogKeys.ConstantBufferBinding, DescriptorKind.UniformBuffer, ShaderStage.Fragment),
            new(FogKeys.SourceBinding, DescriptorKind.SampledTexture, ShaderStage.Fragment),
            new(FogKeys.DepthBufferBinding, DescriptorKind.SampledTexture, ShaderStage.Fragment),
            new(FogKeys.LinearSamplerBinding, DescriptorKind.Sampler, ShaderStage.Fragment)
        );

        Declare(
            OutlineKeys.ShaderName,
            new(OutlineKeys.ConstantBufferBinding, DescriptorKind.UniformBuffer, ShaderStage.Fragment),
            new(OutlineKeys.SourceBinding, DescriptorKind.SampledTexture, ShaderStage.Fragment),
            new(OutlineKeys.DepthBufferBinding, DescriptorKind.SampledTexture, ShaderStage.Fragment),
            new(OutlineKeys.NormalBufferBinding, DescriptorKind.SampledTexture, ShaderStage.Fragment),
            new(OutlineKeys.SelectionMaskBinding, DescriptorKind.SampledTexture, ShaderStage.Fragment),
            new(OutlineKeys.PointSamplerBinding, DescriptorKind.Sampler, ShaderStage.Fragment)
        );

        Declare(
            SsaoKeys.ShaderName,
            new(SsaoKeys.ConstantBufferBinding, DescriptorKind.UniformBuffer, ShaderStage.Fragment),
            new(SsaoKeys.DepthBufferBinding, DescriptorKind.SampledTexture, ShaderStage.Fragment),
            new(SsaoKeys.NormalBufferBinding, DescriptorKind.SampledTexture, ShaderStage.Fragment),
            new(SsaoKeys.PointSamplerBinding, DescriptorKind.Sampler, ShaderStage.Fragment)
        );

        Declare(
            TonemapKeys.ShaderName,
            new(TonemapKeys.ConstantBufferBinding, DescriptorKind.UniformBuffer, ShaderStage.Fragment),
            new(TonemapKeys.SourceBinding, DescriptorKind.SampledTexture, ShaderStage.Fragment),
            new(TonemapKeys.LutBinding, DescriptorKind.SampledTexture, ShaderStage.Fragment),
            new(TonemapKeys.BloomBinding, DescriptorKind.SampledTexture, ShaderStage.Fragment),
            new(TonemapKeys.BloomDirtBinding, DescriptorKind.SampledTexture, ShaderStage.Fragment),
            new(TonemapKeys.SourceSamplerBinding, DescriptorKind.Sampler, ShaderStage.Fragment),
            new(TonemapKeys.LutSamplerBinding, DescriptorKind.Sampler, ShaderStage.Fragment),
            new(TonemapKeys.BloomSamplerBinding, DescriptorKind.Sampler, ShaderStage.Fragment),
            new(TonemapKeys.BloomDirtSamplerBinding, DescriptorKind.Sampler, ShaderStage.Fragment),

            // ⚠ Declared here whether or not a case in this file meters, because the *shader* declares
            // it whether or not a variant reads it — and a stub layout that models a permutation as
            // folding the binding away is a stub that agrees with the mistake rather than the shader.
            // These twelve cases all draw with a fixed exposure, so this is the line that makes them
            // exercise the stand-in instead of passing while the real device refuses the frame.
            new(TonemapKeys.ExposureBufferBinding, DescriptorKind.StorageBuffer, ShaderStage.Fragment)
        );

        Declare(
            DepthOfFieldKeys.ShaderName,
            new(DepthOfFieldKeys.ConstantBufferBinding, DescriptorKind.UniformBuffer, ShaderStage.Fragment),
            new(DepthOfFieldKeys.SourceBinding, DescriptorKind.SampledTexture, ShaderStage.Fragment),
            new(DepthOfFieldKeys.DepthBufferBinding, DescriptorKind.SampledTexture, ShaderStage.Fragment),
            new(DepthOfFieldKeys.SourceSamplerBinding, DescriptorKind.Sampler, ShaderStage.Fragment),
            new(DepthOfFieldKeys.PointSamplerBinding, DescriptorKind.Sampler, ShaderStage.Fragment)
        );

        Declare(
            TaaKeys.ShaderName,
            new(TaaKeys.ConstantBufferBinding, DescriptorKind.UniformBuffer, ShaderStage.Fragment),
            new(TaaKeys.CurrentBinding, DescriptorKind.SampledTexture, ShaderStage.Fragment),
            new(TaaKeys.HistoryBinding, DescriptorKind.SampledTexture, ShaderStage.Fragment),
            new(TaaKeys.MotionVectorsBinding, DescriptorKind.SampledTexture, ShaderStage.Fragment),
            new(TaaKeys.DepthBufferBinding, DescriptorKind.SampledTexture, ShaderStage.Fragment),
            new(TaaKeys.LinearSamplerBinding, DescriptorKind.Sampler, ShaderStage.Fragment),
            new(TaaKeys.PointSamplerBinding, DescriptorKind.Sampler, ShaderStage.Fragment)
        );

        Declare(
            AmbientCombineKeys.ShaderName,
            new(AmbientCombineKeys.ConstantBufferBinding, DescriptorKind.UniformBuffer, ShaderStage.Fragment),
            new(AmbientCombineKeys.DirectBinding, DescriptorKind.SampledTexture, ShaderStage.Fragment),
            new(AmbientCombineKeys.AlbedoBinding, DescriptorKind.SampledTexture, ShaderStage.Fragment),
            new(AmbientCombineKeys.NormalsBinding, DescriptorKind.SampledTexture, ShaderStage.Fragment),
            new(AmbientCombineKeys.IrradianceBinding, DescriptorKind.SampledTexture, ShaderStage.Fragment),
            new(AmbientCombineKeys.OcclusionBinding, DescriptorKind.SampledTexture, ShaderStage.Fragment),
            new(AmbientCombineKeys.ContactOcclusionBinding, DescriptorKind.SampledTexture, ShaderStage.Fragment),
            new(AmbientCombineKeys.ReflectionsBinding, DescriptorKind.SampledTexture, ShaderStage.Fragment),
            new(AmbientCombineKeys.SpecularBinding, DescriptorKind.SampledTexture, ShaderStage.Fragment),
            new(AmbientCombineKeys.DepthBufferBinding, DescriptorKind.SampledTexture, ShaderStage.Fragment),
            new(AmbientCombineKeys.PointSamplerBinding, DescriptorKind.Sampler, ShaderStage.Fragment),
            new(AmbientCombineKeys.LinearSamplerBinding, DescriptorKind.Sampler, ShaderStage.Fragment)
        );

        // The consumer's shader, which is this fixture's own rather than one that ships: a block, the
        // texture it copies and the sampler it copies through.
        Declare(
            ConsumerShader,
            new(ConsumerConstantBinding, DescriptorKind.UniformBuffer, ShaderStage.Fragment),
            new(ConsumerSourceBinding, DescriptorKind.SampledTexture, ShaderStage.Fragment),
            new(ConsumerSamplerBinding, DescriptorKind.Sampler, ShaderStage.Fragment)
        );

        effects.AddProvider(new AlwaysCompiles(layouts));
    }

    void Declare(string shader, params DescriptorBinding[] bindings) =>
        layouts[shader] = device.CreateDescriptorSetLayout(new(DescriptorSetSlot.PerMaterial, bindings, shader));

    /// <inheritdoc />
    public void Dispose() {
        samplers.Dispose();
        allocator.Dispose();
        device.Dispose();
        GC.SuppressFinalize(this);
    }

    // --- Each effect runs its own shader ------------------------------------

    /// <summary>
    ///     Every effect resolves the shader it is a pass over, and draws once.
    /// </summary>
    /// <remarks>
    ///     The claim the whole project exists to make. Each of these shaders shipped in
    ///     <c>Raven/Library/PostFx</c> with nothing in the engine calling it, which compiles, validates
    ///     and shades nothing — so "a draw happened, with this effect" is the difference between a
    ///     shader and an effect.
    /// </remarks>
    [Theory]
    [InlineData("Fxaa")]
    [InlineData("Sharpen")]
    [InlineData("Vignette")]
    [InlineData("Fog")]
    [InlineData("Outline")]
    [InlineData("Ssao")]
    [InlineData("AmbientCombine")]
    public void Every_effect_draws_its_own_shader(string shader) {
        using var effect = Create(shader);
        using var h = Build(effect);

        Frame(h);

        // Two: this effect, and the pass that reads it — an effect nothing read would be culled.
        var draws = device.Recorder!.OfKind(RecordedCommandKind.Draw);

        Assert.Equal(2, draws.Count);

        // Three vertices and no vertex buffer: the full-screen triangle comes out of SV_VertexID.
        Assert.Equal(3, draws[0].A);
        Assert.Empty(device.Recorder.OfKind(RecordedCommandKind.BindVertexBuffer));

        Assert.Equal(shader, effect.Pass.ShaderName);
        Assert.Equal(1, effect.Pass.PipelineCount);
    }

    /// <summary>
    ///     An effect reads exactly the textures it binds, and binds them where the shader says.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         The two have to agree. The binding is what the shader samples through, and the read is
    ///         what orders this pass after whatever wrote the texture and keeps that producer from
    ///         being culled — so a binding without a read is a race and a read without a binding is a
    ///         pass that waited for something it never used.
    ///     </para>
    ///     <para>
    ///         The indices are the generated ones rather than numbers written here, because a binding
    ///         index is assigned by Raven from declaration order and adding a texture above another in
    ///         the <c>.rvn</c> renumbers everything below it.
    ///     </para>
    /// </remarks>
    [Fact]
    public void An_effect_binds_what_it_reads() {
        using var effect = new FogRenderer { Source = "SceneColour", Depth = "SceneDepth", Output = "Fogged" };
        using var h = Build(effect);

        Frame(h);

        var textures = effect.Pass.Descriptors.Bindings
            .Where(binding => binding.Kind == DescriptorKind.SampledTexture)
            .ToArray();

        Assert.Equal(effect.Pass.Reads.Count, textures.Length);

        Assert.Equal(FogKeys.SourceBinding, textures.Single(b => b.Resource == "SceneColour").Binding);
        Assert.Equal(FogKeys.DepthBufferBinding, textures.Single(b => b.Resource == "SceneDepth").Binding);

        Assert.Contains(effect.Pass.Descriptors.Bindings, binding => binding.Kind == DescriptorKind.Sampler);
    }

    /// <summary>An effect declares the target it publishes, so a chain can be built from names.</summary>
    /// <remarks>
    ///     An effect whose output somebody else had to remember to declare is one you cannot drop into
    ///     a chain — and the graph's argument that a pass nothing reads costs nothing only holds if the
    ///     resource it writes is the graph's to drop.
    /// </remarks>
    [Fact]
    public void An_effect_declares_its_own_target() {
        using var effect = new SharpenRenderer { Source = "SceneColour", Output = "Sharpened" };
        using var h = Build(effect);

        Frame(h);

        Assert.Contains("Sharpened", effect.Pass.ColourTargets);

        // Declared by the effect and found by name from the pass that reads it, which is the whole
        // point: a chain is built out of names, not out of textures somebody passed around.
        Assert.Equal(2, device.Recorder!.CountOf(RecordedCommandKind.Draw));
    }

    /// <summary>Ambient occlusion runs at a fraction of the frame, and reads the full-size depth.</summary>
    /// <remarks>
    ///     Occlusion from a hemisphere is low frequency almost everywhere, so half resolution is the
    ///     standard trade — but the march has to step in the *depth buffer's* texel grid, not its own.
    ///     Stepping in the target's would march twice as far per step and measure a horizon that is
    ///     not there.
    /// </remarks>
    [Fact]
    public void Ambient_occlusion_runs_at_half_resolution() {
        using var effect = new AmbientOcclusionRenderer {
            Depth = "SceneDepth",
            Normals = "SceneNormals",
            Output = "Occlusion",
            Scale = 0.5f
        };

        using var h = Build(effect);

        Frame(h);

        var texel = effect.Pass.Parameters.Get(SsaoKeys.TexelSize);

        Assert.Equal(1f / 320f, texel.X, 5);
        Assert.Equal(1f / 180f, texel.Y, 5);

        // And the texel is what the march's first sample is measured in: it steps in to one texel
        // of the buffer above rather than to `radius / Steps`, so the wall a pixel actually touches
        // is inside the search. `bias` is what pays for standing that close — see the shader.
        Assert.Equal(0.1f, effect.Pass.Parameters.Get(SsaoKeys.Bias), 5);
    }

    /// <summary>A document's own horizon bias reaches the march.</summary>
    [Fact]
    public void Ambient_occlusion_carries_its_horizon_bias() {
        using var system = new RenderSystem();

        var declared = new SsaoAsset {
            Name = "Ssao",
            Depth = "SceneDepth",
            Normals = "SceneNormals",
            Bias = 0.25f
        };

        using var node = (AmbientOcclusionRenderer)new PostEffectFactory().Create(declared, new(system))!;

        Assert.Equal(0.25f, node.Bias, 5);
    }

    /// <summary>
    ///     The traced pass runs at a fraction of the frame, and its own set carries only what it owns.
    /// </summary>
    /// <remarks>
    ///     The clipmap — the volumes, their textures, their sampler — is not here and must not be. It
    ///     follows the camera and belongs to the frame's set 0, put there by
    ///     <c>GlobalDistanceFieldRenderer</c>. What this pass owns is the depth, the normals and the
    ///     numbers of its own march, and a test that found a volume in this collection would be
    ///     finding the two halves fighting over who binds the field.
    /// </remarks>
    [Fact]
    public void Distance_field_occlusion_owns_its_march_and_not_the_field() {
        using var effect = new DistanceFieldAoRenderer {
            Depth = "SceneDepth",
            Normals = "SceneNormals",
            Output = "TracedOcclusion",
            Scale = 0.5f,
            OcclusionRadius = 3.5f,
            SunShadow = false
        };

        using var h = Build(effect);

        Frame(h);

        Assert.Equal(3.5f, effect.Pass.Parameters.Get(DistanceFieldAoKeys.OcclusionRadius), 5);
        Assert.False(effect.Pass.Parameters.Get(DistanceFieldAoKeys.SunShadow));
        Assert.Contains("TracedOcclusion", effect.Pass.ColourTargets);

        // The clipmap's own names belong to set 0 and are nobody's business here.
        Assert.False(
            effect.Pass.Parameters.Has(
                ParameterKeys.New<float>("DistanceFieldAo.GlobalDistanceField.distanceFieldVolumes[0].maxDistance")
            )
        );
    }

    /// <summary>
    ///     An effect with no shader declares nothing rather than throwing.
    /// </summary>
    /// <remarks>
    ///     A miss is the ordinary answer in a build that has not compiled a variant yet, and it is
    ///     reported through <see cref="EffectSystem.Misses" /> like every other — which is what keeps
    ///     "no runtime compilation in a shipping build" a test rather than a hope.
    /// </remarks>
    [Fact]
    public void An_effect_whose_shader_is_missing_declares_nothing() {
        using var effect = new FxaaRenderer { Source = "SceneColour", Output = "Antialiased" };
        using var h = Build(effect);
        var empty = new EffectSystem();

        h.Graph.Reset();
        h.Compositor.Build(h.Graph, empty, device);

        // The effect's own key is among them — the consumer misses too, which is the same answer for
        // the same reason and not what this is about.
        Assert.Contains(empty.Misses, key => key.ShaderName == FxaaKeys.ShaderName);
        Assert.Empty(device.Recorder!.OfKind(RecordedCommandKind.Draw));

        // ⚠ And the node itself says so, which the miss list cannot: a frame with nine misses and one
        // node that cared about one of them is exactly where "what failed to compile" and "which node
        // in this document therefore drew nothing" are different questions.
        var (node, reason) = Assert.Single(Degradations(h), pair => pair.Node == effect.ToString());

        Assert.Equal(effect.ToString(), node);
        Assert.Contains($"'{FxaaKeys.ShaderName}' did not resolve", reason, StringComparison.Ordinal);
    }

    // --- What a post effect says when it declines ---------------------------

    /// <summary>
    ///     ⚠ <b>The documented case, at the node that writes the swapchain.</b>
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         <see cref="FullScreenRenderer" /> returned above its resolve when
    ///         <c>CompositorBuilder</c> had left its device and modules unset, so the effect system
    ///         recorded no miss and the tonemap silently did not run. A post effect's pass is not in
    ///         <see cref="SceneRenderer.Nested" /> — it is the effect's own, not a node a document
    ///         named — so <see cref="GraphicsCompositor.Degradations" /> cannot reach it and
    ///         <see cref="PostEffectRenderer" /> carries the answer out by hand.
    ///     </para>
    ///     <para>
    ///         The assertion is on the compositor's walk rather than on the node's property, because
    ///         the walk is what a host reads and the forwarding is the part that could be dropped.
    ///     </para>
    /// </remarks>
    [Fact]
    public void A_tonemap_with_no_modules_is_named_by_the_compositors_walk() {
        using var tonemap = new TonemapRenderer { Source = "SceneColour", Output = "Out" };
        using var h = Build(tonemap);

        // ⚠ Off, because an effect that declares nothing takes the consumer down with it: "node
        // 'Present' refers to target 'Out', which nothing bound". That exception is the *lucky* case
        // — the frame that ends the document is the one whose target is imported, so it binds, and
        // the frame that silently did not run is the shape this reason exists for.
        h.Consumer.Enabled = false;

        tonemap.Modules = null;
        Frame(h);

        var (node, reason) = Assert.Single(Degradations(h), pair => pair.Node == tonemap.ToString());

        Assert.Equal(tonemap.ToString(), node);
        Assert.Contains("no Modules", reason, StringComparison.Ordinal);
        Assert.Contains("never declared", reason, StringComparison.Ordinal);
    }

    /// <summary>And the same node, wired, is not in the list.</summary>
    /// <remarks>
    ///     The half that separates a report from a scar. A post effect answers from
    ///     <see cref="PostEffectRenderer.Configure" /> or from its pass, and both are cleared and
    ///     re-decided every frame — so recovering is as visible as degrading.
    /// </remarks>
    [Fact]
    public void A_tonemap_that_is_handed_its_modules_back_leaves_the_report() {
        using var tonemap = new TonemapRenderer { Source = "SceneColour", Output = "Out" };
        using var h = Build(tonemap);

        var modules = tonemap.Modules;

        h.Consumer.Enabled = false;
        tonemap.Modules = null;
        Frame(h);
        Assert.Contains(Degradations(h), pair => pair.Node == tonemap.ToString());

        tonemap.Modules = modules;
        Frame(h);

        Assert.DoesNotContain(Degradations(h), pair => pair.Node == tonemap.ToString());
    }

    /// <summary>
    ///     ⚠ An effect that answers from <c>Configure</c> keeps its own answer through the base.
    /// </summary>
    /// <remarks>
    ///     <b>The regression this exists to prevent has already happened once, in the commit that
    ///     added the forwarding above.</b> <see cref="SkyRenderer" /> and
    ///     <see cref="AmbientCombineRenderer" /> both degrade from inside <c>Configure</c> — that is
    ///     where an effect reads the frame — and a base class that wrote its own conclusion over the
    ///     top would have replaced "every pixel samples one direction" with silence, in the one node
    ///     whose wrong picture is stable, plausible and stationary.
    /// </remarks>
    [Fact]
    public void A_sky_with_no_view_keeps_the_reason_it_set_from_configure() {
        using var sky = new SkyRenderer { Output = "Out" };
        using var h = Build(sky);

        Assert.Null(sky.View);
        Frame(h);

        var (_, reason) = Assert.Single(Degradations(h), pair => pair.Node == sky.ToString());

        Assert.Contains("no View", reason, StringComparison.Ordinal);
        Assert.Contains("same direction", reason, StringComparison.Ordinal);
    }

    /// <summary>The frame's whole list, which is what a host actually reads.</summary>
    static List<(string Node, string Reason)> Degradations(Harness h) {
        List<(string, string)> found = [];
        var reported = h.Compositor.Degradations(found);

        Assert.Equal(found.Count, reported);
        return found;
    }

    // --- Temporal antialiasing ----------------------------------------------

    /// <summary>
    ///     Temporal antialiasing reads what the last frame wrote, and writes the other one.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         The property the whole effect rests on: a pass cannot read the target it writes, so
    ///         there are two textures and the node alternates. If it did not, the pass would sample the
    ///         attachment it is writing — undefined on every backend, and on some of them a frame that
    ///         looks almost right.
    ///     </para>
    ///     <para>
    ///         Asserted as "the target and the history swap places between frames", which is what
    ///         alternating means and does not depend on which of the two starts.
    ///     </para>
    /// </remarks>
    [Fact]
    public void Temporal_antialiasing_alternates_its_history() {
        using var effect = new TemporalAntialiasingRenderer {
            Source = "SceneColour",
            MotionVectors = "Motion",
            Depth = "SceneDepth",
            Output = "Resolved"
        };

        using var h = Build(effect);

        Frame(h);
        var (target, history) = (effect.Target, effect.History);

        Assert.NotEqual(target, history);

        Frame(h);

        Assert.Equal(target, effect.History);
        Assert.Equal(history, effect.Target);

        // Two frames, two draws: the alternation is not a pass that stopped running.
        Assert.Equal(4, device.Recorder!.CountOf(RecordedCommandKind.Draw));
    }

    /// <summary>
    ///     The first frame takes the current frame whole rather than blending undefined memory.
    /// </summary>
    /// <remarks>
    ///     A history texture that has never been written holds whatever the allocator handed back,
    ///     which on some drivers is the last thing that lived there. Blending nine tenths of that into
    ///     the frame is a frame of garbage that then fades out over twenty more.
    /// </remarks>
    [Fact]
    public void The_first_frame_has_no_history_to_blend() {
        using var effect = new TemporalAntialiasingRenderer {
            Source = "SceneColour",
            MotionVectors = "Motion",
            Depth = "SceneDepth",
            Output = "Resolved",
            Feedback = 0.9f
        };

        using var h = Build(effect);

        Assert.False(effect.HasHistory);

        Frame(h);
        Assert.Equal(0f, effect.Pass.Parameters.Get(TaaKeys.Feedback));

        Frame(h);
        Assert.True(effect.HasHistory);
        Assert.Equal(0.9f, effect.Pass.Parameters.Get(TaaKeys.Feedback));
    }

    /// <summary>
    ///     The jitter sequence fills the pixel, centred on zero.
    /// </summary>
    /// <remarks>
    ///     Halton (2, 3), which is the standard choice because it fills the pixel evenly at every
    ///     prefix length rather than only in the limit — the camera usually stops after eight frames,
    ///     not a thousand. Centred, because an offset that averaged to anything but zero would shift
    ///     the whole image by a fraction of a pixel.
    /// </remarks>
    [Fact]
    public void The_jitter_sequence_is_centred_and_spread() {
        var sum = Vector2.Zero;
        var largest = 0f;

        for (var i = 0; i < 16; i++) {
            var jitter = TemporalAntialiasingRenderer.Jitter(i);

            sum += jitter;
            largest = MathF.Max(largest, MathF.Max(MathF.Abs(jitter.X), MathF.Abs(jitter.Y)));

            Assert.InRange(jitter.X, -0.5f, 0.5f);
            Assert.InRange(jitter.Y, -0.5f, 0.5f);
        }

        Assert.Equal(0f, sum.X / 16f, 1);
        Assert.Equal(0f, sum.Y / 16f, 1);

        // And it actually moves: a sequence that returned zero every frame would pass everything above.
        Assert.True(largest > 0.2f, $"the jitter never left the middle of the pixel ({largest})");
    }

    // --- The document -------------------------------------------------------

    /// <summary>
    ///     ⚠ Every screen-space effect the project ships is a node kind a document can name.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         Thirteen renderers existed and five had a name. The other eight compiled, reached both
    ///         backends, and could only be run by writing a <c>!FullScreen</c> with the shader's
    ///         binding indices spelled out by hand — which is the thing node kinds exist to stop, and
    ///         which no project ever did, so the effects were dead weight.
    ///     </para>
    ///     <para>
    ///         This asserts the whole set builds, because the failure mode of a missing factory case
    ///         is <c>Create</c> returning null and the node silently not being in the frame.
    ///     </para>
    /// </remarks>
    [Fact]
    public void Every_screen_space_effect_is_a_node_a_document_can_name() {
        using var system = new RenderSystem();
        var camera = new RenderView("Camera");

        var builder = new CompositorBuilder(system) {
            Device = device,
            Modules = describer,
            Descriptors = allocator,
            Samplers = samplers
        };

        builder.Views["Camera"] = camera;
        builder.Factories.Add(new PostEffectFactory());

        var compositor = builder.Build(
            new() {
                Version = CompositorBuilder.SupportedVersion,
                Game = new SequenceAsset {
                    Name = "Frame",
                    Children = [
                        new SsaoAsset { Name = "Ssao", Depth = "Depth", Normals = "Normals", View = "Camera" },
                        new AutoExposureAsset { Name = "Exposure", Source = "SceneColour", MiddleGrey = 0.2f },
                        new TemporalAntialiasingAsset {
                            Name = "Taa",
                            Source = "SceneColour",
                            MotionVectors = "Motion",
                            Depth = "Depth",
                            Feedback = 0.8f
                        },
                        new FogAsset { Name = "Fog", Source = "SceneColour", Depth = "Depth", View = "Camera" },
                        new OutlineAsset { Name = "Outline", Source = "Fogged", Depth = "Depth", Thickness = 3f },
                        new VignetteAsset { Name = "Lens", Source = "Outlined", GrainIntensity = 0.1f },
                        new FxaaAsset { Name = "Fxaa", Source = "Lensed", EdgeThreshold = 0.2f },
                        new SharpenAsset { Name = "Sharpen", Source = "Antialiased", Sharpness = 0.7f },
                        new MotionBlurAsset {
                            Name = "Shutter",
                            Source = "SceneColour",
                            MotionVectors = "Motion",
                            View = "Camera",
                            Samples = 12
                        },
                        new LocalExposureAsset {
                            Name = "Local",
                            Source = "SceneColour",
                            View = "Camera",
                            HighlightContrast = 0.8f
                        },
                        new LensFlareAsset { Name = "Flare", Source = "SceneColour", GhostIntensity = 0.2f }
                    ]
                }
            }
        );

        var sequence = Assert.IsType<SceneRendererSequence>(compositor.Game);

        Assert.Equal(11, sequence.Children.Count);

        // The view reaches the two nodes that unproject a depth buffer. Without it each has an
        // identity matrix, which is a picture of the wrong place rather than no picture.
        Assert.Same(camera, Assert.IsType<AmbientOcclusionRenderer>(sequence.Children[0]).View);
        Assert.Same(camera, Assert.IsType<FogRenderer>(sequence.Children[3]).View);

        Assert.Equal(0.2f, Assert.IsType<AutoExposureRenderer>(sequence.Children[1]).MiddleGrey);
        Assert.Equal(0.8f, Assert.IsType<TemporalAntialiasingRenderer>(sequence.Children[2]).Feedback);
        Assert.Equal(3f, Assert.IsType<OutlineRenderer>(sequence.Children[4]).Thickness);
        Assert.Equal(0.1f, Assert.IsType<VignetteRenderer>(sequence.Children[5]).GrainIntensity);
        Assert.Equal(0.2f, Assert.IsType<FxaaRenderer>(sequence.Children[6]).EdgeThreshold);
        Assert.Equal(0.7f, Assert.IsType<SharpenRenderer>(sequence.Children[7]).Sharpness);
        Assert.Equal(12, Assert.IsType<MotionBlurRenderer>(sequence.Children[8]).Samples);
        Assert.Equal(0.8f, Assert.IsType<LocalExposureRenderer>(sequence.Children[9]).HighlightContrast);
        Assert.Equal(0.2f, Assert.IsType<LensFlareRenderer>(sequence.Children[10]).GhostIntensity);

        // ⚠ All three read the camera, and each for a different number off the same component: the
        // blur takes the shutter, the local exposure takes the exposure value the tonemap will use,
        // and the flare takes the blade count that also shapes the bokeh. One lens, four answers.
        Assert.Same(camera, Assert.IsType<MotionBlurRenderer>(sequence.Children[8]).View);
        Assert.Same(camera, Assert.IsType<LocalExposureRenderer>(sequence.Children[9]).View);

        foreach (var child in sequence.Children) {
            (child as IDisposable)?.Dispose();
        }
    }

    /// <summary>
    ///     A document naming the effect set's node kinds builds through the factory.
    /// </summary>
    /// <remarks>
    ///     The other half of the seam, from the side that supplies it. <c>CompositorBuilder</c> cannot
    ///     switch on these types — this project is downstream of it — so a document saying
    ///     <c>!Bloom</c> reaches <see cref="PostEffectFactory" /> instead, and comes back a node with
    ///     the device, the module cache and the allocators the file could not carry.
    /// </remarks>
    [Fact]
    public void A_document_can_name_the_effect_sets_nodes() {
        using var system = new RenderSystem();

        var builder = new CompositorBuilder(system) {
            Device = device,
            Modules = describer,
            Descriptors = allocator,
            Samplers = samplers
        };

        builder.Factories.Add(new PostEffectFactory());

        var compositor = builder.Build(
            new() {
                Version = CompositorBuilder.SupportedVersion,
                Game = new SequenceAsset {
                    Name = "Frame",
                    Children = [
                        new BloomAsset { Name = "Bloom", Source = "SceneColour", Output = "Bloom", Levels = 3 },
                        new TonemapAsset { Name = "Tonemap", Source = "Bloom", Output = "Display", Exposure = 2f }
                    ]
                }
            }
        );

        var sequence = Assert.IsType<SceneRendererSequence>(compositor.Game);

        var bloom = Assert.IsType<BloomRenderer>(sequence.Children[0]);
        Assert.Equal(3, bloom.Levels);
        Assert.Same(samplers, bloom.Samplers);

        var tonemap = Assert.IsType<TonemapRenderer>(sequence.Children[1]);
        Assert.Equal(2f, tonemap.Exposure);
        Assert.Same(allocator, tonemap.Allocator);

        bloom.Dispose();
        tonemap.Dispose();
    }

    /// <summary>
    ///     And the same seam carries doc 19's two screen passes, which is what makes dynamic global
    ///     illumination something a project turns on in a file.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         Every renderer in that chain existed and none of them had an asset, so a game could
    ///         reach doc 19 only by assembling its compositor in C#. These two are the consumers —
    ///         <c>GlobalDistanceField</c> and <c>IrradianceField</c> composite what they read, and both
    ///         pairs agree by naming the same shader, because that name is the compose-slot prefix one
    ///         writes its bindings under and the other reads them from.
    ///     </para>
    ///     <para>
    ///         ⚠ The <c>Source</c> left empty is asserted deliberately: it has to keep the renderer's
    ///         own default rather than become a nameless slot. Those defaults are two different right
    ///         answers — "nothing is near, fully lit", and "no indirect light and an unshadowed sun" —
    ///         and a zero for the second would put every surface in the world into shadow.
    ///     </para>
    /// </remarks>
    [Fact]
    public void A_document_can_name_the_lit_paths_screen_passes() {
        using var system = new RenderSystem();

        var builder = new CompositorBuilder(system) {
            Device = device,
            Modules = describer,
            Descriptors = allocator,
            Samplers = samplers
        };

        builder.Factories.Add(new PostEffectFactory());

        var compositor = builder.Build(
            new() {
                Version = CompositorBuilder.SupportedVersion,
                Game = new SequenceAsset {
                    Name = "Frame",
                    Children = [
                        new DistanceFieldAoAsset {
                            Name = "Occlusion",
                            Depth = "SceneDepth",
                            Normals = "SceneNormals",
                            Source = "DistanceFieldAo.GlobalDistanceField",
                            SunShadow = false
                        },
                        new IndirectDiffuseAsset {
                            Name = "Indirect",
                            Depth = "SceneDepth",
                            Normals = "SceneNormals",
                            Intensity = 0.75f
                        }
                    ]
                }
            }
        );

        var sequence = Assert.IsType<SceneRendererSequence>(compositor.Game);

        var occlusion = Assert.IsType<DistanceFieldAoRenderer>(sequence.Children[0]);
        Assert.Equal("SceneDepth", occlusion.Depth);
        Assert.Equal("SceneNormals", occlusion.Normals);
        Assert.False(occlusion.SunShadow);
        Assert.Equal("DistanceFieldAo.GlobalDistanceField", occlusion.Source);
        Assert.Same(samplers, occlusion.Samplers);

        var indirect = Assert.IsType<IndirectDiffuseRenderer>(sequence.Children[1]);
        Assert.Equal(0.75f, indirect.Intensity);
        Assert.Same(allocator, indirect.Allocator);

        // Named nothing, so the honest default stands rather than a slot nothing fills.
        Assert.NotEmpty(indirect.Source);

        occlusion.Dispose();
        indirect.Dispose();
    }

    /// <summary>
    ///     And doc 19's last two: the probe gather and the reflections, host chain and all.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         The seam's final extension. The gather's tracer, resolver, accumulator and filter each
    ///         carry composed sources and device state a file cannot make, so the builder grows a
    ///         slot per link — and the reflections' kernel resolves through the host's own effect
    ///         system, because two effect systems are two variant caches disagreeing about one
    ///         shader.
    ///     </para>
    ///     <para>
    ///         ⚠ The pyramid assertion is the one worth reading twice: one nearest chain serves both
    ///         nodes, and two rebuilds before one submission need a ring two deep — which
    ///         <c>TakeTracePyramid</c> counts so the host does not have to remember to.
    ///     </para>
    /// </remarks>
    [Fact]
    public void A_document_can_name_the_probe_gather_and_the_reflections() {
        using var system = new RenderSystem();
        var camera = new RenderView("Camera");

        var builder = new CompositorBuilder(system) {
            Device = device,
            Modules = describer,
            Descriptors = allocator,
            Samplers = samplers,
            Effects = effects
        };

        builder.Views["Camera"] = camera;

        using var tracer = new ScreenProbeTraceFill(device);
        using var resolver = new ScreenProbeResolve(device);
        using var accumulator = new ScreenProbeAccumulateFill(device);
        using var filter = new ScreenProbeFilterFill(device);
        using var pyramid = new HiZPyramid(device) { Reduction = HiZReduction.Nearest };

        builder.ScreenProbeTracer = tracer;
        builder.ScreenProbeResolver = resolver;
        builder.ScreenProbeAccumulator = accumulator;
        builder.ScreenProbeFilter = filter;
        builder.TracePyramid = pyramid;

        builder.Factories.Add(new PostEffectFactory());

        var compositor = builder.Build(
            new() {
                Version = CompositorBuilder.SupportedVersion,
                Game = new SequenceAsset {
                    Name = "Frame",
                    Children = [
                        new ScreenProbeGatherAsset {
                            Name = "Gather",
                            Depth = "SceneDepth",
                            Normals = "SceneNormals",
                            View = "Camera",
                            TileSize = 8,
                            Intensity = 0.5f,
                            ScreenTraces = true,
                            Latency = 1,
                            PlaneTolerance = 0.05f
                        },
                        new ReflectionsAsset {
                            Name = "Mirror",
                            Depth = "SceneDepth",
                            Normals = "SceneNormals",
                            Colour = "SceneHdr",
                            Target = "SceneReflections",
                            View = "Camera",
                            RoughnessThreshold = 0.4f,
                            MaxDistance = 50f,
                            ScreenSteps = 24
                        }
                    ]
                }
            }
        );

        var sequence = Assert.IsType<SceneRendererSequence>(compositor.Game);
        var gather = Assert.IsType<ScreenProbeGatherRenderer>(sequence.Children[0]);
        var mirror = Assert.IsType<ReflectionRenderer>(sequence.Children[1]);

        // The host's chain reached the node, link by link.
        Assert.Same(tracer, gather.Tracer);
        Assert.Same(resolver, gather.Resolver);
        Assert.Same(accumulator, gather.Accumulator);
        Assert.Same(filter, gather.SpatialFilter);
        Assert.Same(pyramid, gather.Pyramid);
        Assert.Same(camera, gather.View);

        // And the numbers the document chose reached the parts that take numbers.
        Assert.Equal(8, gather.TileSize);
        Assert.Equal(0.5f, gather.Intensity);
        Assert.True(gather.ScreenTraces);
        Assert.Equal(1, gather.Latency);
        Assert.Equal(0.05f, gather.PlaneTolerance);

        Assert.Same(effects, mirror.Effects);
        Assert.NotNull(mirror.Pipelines);
        Assert.Same(pyramid, mirror.Pyramid);
        Assert.Same(camera, mirror.View);
        Assert.Equal("SceneHdr", mirror.Colour);
        Assert.Equal("SceneReflections", mirror.Target);
        Assert.Equal(0.4f, mirror.RoughnessThreshold);
        Assert.Equal(50f, mirror.MaxDistance);
        Assert.Equal(24, mirror.ScreenSteps);

        // One chain, two nodes: a ring two deep, counted by the builder rather than remembered by
        // the host.
        Assert.Equal(2, pyramid.BuildsPerFrame);

        gather.Dispose();
        mirror.Dispose();
    }

    /// <summary>The same document on a host that supplied nothing builds nodes that do nothing.</summary>
    /// <remarks>
    ///     The <c>!IrradianceField</c> stance, extended: one document serves a project that has no
    ///     probe machinery, and the difference is an inert node rather than a throw — which is what
    ///     lets the file open in an editor that has not started the game.
    /// </remarks>
    [Fact]
    public void The_same_document_builds_on_a_host_with_no_probe_machinery() {
        using var system = new RenderSystem();
        var builder = new CompositorBuilder(system);

        builder.Factories.Add(new PostEffectFactory());

        var compositor = builder.Build(
            new() {
                Version = CompositorBuilder.SupportedVersion,
                Game = new SequenceAsset {
                    Name = "Frame",
                    Children = [
                        new ScreenProbeGatherAsset { Name = "Gather", Depth = "SceneDepth", Normals = "SceneNormals" },
                        new ReflectionsAsset { Name = "Mirror" }
                    ]
                }
            }
        );

        var sequence = Assert.IsType<SceneRendererSequence>(compositor.Game);
        var gather = Assert.IsType<ScreenProbeGatherRenderer>(sequence.Children[0]);
        var mirror = Assert.IsType<ReflectionRenderer>(sequence.Children[1]);

        Assert.Null(gather.Tracer);
        Assert.Null(gather.Accumulator);
        Assert.Null(gather.Pyramid);

        Assert.Null(mirror.Effects);
        Assert.Null(mirror.Pipelines);
        Assert.Null(mirror.Trace);

        // And the empty colour stayed null rather than becoming a resource of no name, which the
        // frame would refuse by name on the first build.
        Assert.Null(mirror.Colour);

        gather.Dispose();
        mirror.Dispose();
    }

    /// <summary>A gather that marches no screen leaves the shared chain to the reflections.</summary>
    /// <remarks>
    ///     Taking the pyramid deepens its descriptor ring, and the gather only builds the chain when
    ///     its screen trace runs — so a document with the trace off must not cost a ring slot for a
    ///     rebuild that never happens, or the ring is a number that does not describe the frame.
    /// </remarks>
    [Fact]
    public void A_gather_without_screen_traces_leaves_the_chain_to_the_reflections() {
        using var system = new RenderSystem();
        using var pyramid = new HiZPyramid(device) { Reduction = HiZReduction.Nearest };

        var builder = new CompositorBuilder(system) { TracePyramid = pyramid };

        builder.Factories.Add(new PostEffectFactory());

        var compositor = builder.Build(
            new() {
                Version = CompositorBuilder.SupportedVersion,
                Game = new SequenceAsset {
                    Name = "Frame",
                    Children = [
                        new ScreenProbeGatherAsset { Name = "Gather", Depth = "SceneDepth", Normals = "SceneNormals" },
                        new ReflectionsAsset { Name = "Mirror" }
                    ]
                }
            }
        );

        var sequence = Assert.IsType<SceneRendererSequence>(compositor.Game);
        var gather = Assert.IsType<ScreenProbeGatherRenderer>(sequence.Children[0]);
        var mirror = Assert.IsType<ReflectionRenderer>(sequence.Children[1]);

        Assert.Null(gather.Pyramid);
        Assert.Same(pyramid, mirror.Pyramid);
        Assert.Equal(1, pyramid.BuildsPerFrame);

        gather.Dispose();
        mirror.Dispose();
    }

    /// <summary>
    ///     Tonemapping without a grading table binds something valid where the table goes.
    /// </summary>
    /// <remarks>
    ///     The shader declares the table in its default variant, so the binding exists whether or not
    ///     a frame has one — and a descriptor set with a hole in it is a validation error rather than
    ///     an unused slot. The source stands in, and <c>UseLut</c> is what stops it being read.
    /// </remarks>
    [Fact]
    public void Tonemapping_without_a_table_still_fills_its_binding() {
        using var effect = new TonemapRenderer { Source = "SceneColour", Output = "Display" };
        using var h = Build(effect);

        Frame(h);

        Assert.False(effect.Pass.Parameters.Get(TonemapKeys.UseLut));

        var table = effect.Pass.Descriptors.Bindings.Single(b => b.Binding == TonemapKeys.LutBinding);
        Assert.Equal("SceneColour", table.Resource);

        // And with one, the permutation turns on and the binding is the table.
        using var graded = new TonemapRenderer { Source = "SceneColour", Lut = "Grade", Output = "Display" };
        using var second = Build(graded);

        second.Compositor.Imports["Grade"] = Colour("Grade");

        Frame(second);

        Assert.True(graded.Pass.Parameters.Get(TonemapKeys.UseLut));
        Assert.Equal("Grade", graded.Pass.Descriptors.Bindings.Single(b => b.Binding == TonemapKeys.LutBinding).Resource);
    }

    /// <summary>
    ///     A frame with no bloom fills the bloom binding, and a frame with one composites it.
    /// </summary>
    /// <remarks>
    ///     The same rule as the grading table above, and the reason it needs a test of its own is what
    ///     the <em>other</em> half prevents: <c>!Bloom</c> publishes the pyramid, not the scene with a
    ///     glow added, so the only way a document had to use one was to point the tonemap's
    ///     <c>source</c> at it — which throws the scene away and, in a level lit below the threshold,
    ///     produces a black frame that every counter calls a success. <c>bloom</c> being a separate
    ///     input is what makes that unavailable.
    /// </remarks>
    [Fact]
    public void Tonemapping_composites_a_bloom_pyramid_and_stands_in_without_one() {
        using var plain = new TonemapRenderer { Source = "SceneColour", Output = "Display" };
        using var h = Build(plain);

        Frame(h);

        Assert.False(plain.Pass.Parameters.Get(TonemapKeys.UseBloom));
        Assert.Equal(
            "SceneColour",
            plain.Pass.Descriptors.Bindings.Single(b => b.Binding == TonemapKeys.BloomBinding).Resource
        );

        using var glowing = new TonemapRenderer {
            Source = "SceneColour",
            Bloom = "Pyramid",
            BloomIntensity = 0.4f,
            Output = "Display"
        };

        using var second = Build(glowing);

        second.Compositor.Imports["Pyramid"] = Colour("Pyramid");

        Frame(second);

        Assert.True(glowing.Pass.Parameters.Get(TonemapKeys.UseBloom));
        Assert.Equal(0.4f, glowing.Pass.Parameters.Get(TonemapKeys.BloomIntensity), 5);

        Assert.Equal(
            "Pyramid",
            glowing.Pass.Descriptors.Bindings.Single(b => b.Binding == TonemapKeys.BloomBinding).Resource
        );
    }

    /// <summary>
    ///     The grade is a permutation, so a frame with none compiles none of it.
    /// </summary>
    /// <remarks>
    ///     Twenty-two uniforms and a four-way luminance blend, all of which fold out when
    ///     <c>Grading</c> is null. Null and <see cref="ColorGrading.Neutral" /> are the same picture
    ///     and not the same variant, which is the distinction worth pinning.
    /// </remarks>
    [Fact]
    public void Tonemapping_compiles_the_colour_decision_list_only_where_a_grade_is_set() {
        using var plain = new TonemapRenderer { Source = "SceneColour", Output = "Display" };
        using var h = Build(plain);

        Frame(h);
        Assert.False(plain.Pass.Parameters.Get(TonemapKeys.UseColorGrading));

        using var graded = new TonemapRenderer {
            Source = "SceneColour",
            Output = "Display",
            Grading = ColorGrading.Neutral with {
                Shadows = ColorGradingRange.Neutral with { Gain = new(0.8f, 0.9f, 1.3f) },
                HighlightsMin = 0.6f
            }
        };

        using var second = Build(graded);

        Frame(second);

        Assert.True(graded.Pass.Parameters.Get(TonemapKeys.UseColorGrading));
        Assert.Equal(new Vector3(0.8f, 0.9f, 1.3f), graded.Pass.Parameters.Get(TonemapKeys.GradeShadowGain));
        Assert.Equal(0.6f, graded.Pass.Parameters.Get(TonemapKeys.GradeHighlightsMin), 5);

        // Everything not authored is the identity rather than a zero, which is what
        // ColorGradingRange.Neutral is for — a zeroed range is a black greyscale image.
        Assert.Equal(1f, graded.Pass.Parameters.Get(TonemapKeys.GradeGlobalSaturation), 5);
        Assert.Equal(Vector3.One, graded.Pass.Parameters.Get(TonemapKeys.GradeMidtoneGain));
    }

    /// <summary>A clean lens still fills the dirt binding, and never reads it.</summary>
    [Fact]
    public void A_clean_lens_fills_its_binding_and_contributes_nothing() {
        using var clean = new TonemapRenderer { Source = "SceneColour", Output = "Display" };
        using var h = Build(clean);

        Frame(h);

        Assert.Equal(
            "SceneColour",
            clean.Pass.Descriptors.Bindings.Single(b => b.Binding == TonemapKeys.BloomDirtBinding).Resource
        );

        // ⚠ Zero whatever the intensity says, because the texture standing in is the scene: an
        // intensity that survived without a dirt map would multiply the bloom by the picture.
        Assert.Equal(0f, clean.Pass.Parameters.Get(TonemapKeys.BloomDirtIntensity), 5);
    }

    /// <summary>
    ///     ⚠ The lens fills in for an exposure nobody authored, and never overrides one.
    /// </summary>
    /// <remarks>
    ///     <b>This reverses while a lens was optional.</b> Every <c>Camera</c> is a physical camera
    ///     now, so "the view has a lens" is true of every frame — it can no longer mean "the author
    ///     asked for a physical exposure". A document that names an <c>ev100</c> has decided something
    ///     about the level, and a lens quietly winning would move every authored frame by however many
    ///     stops its aperture happens to be. So the factory turns <c>LensExposure</c> on exactly when
    ///     the document named neither, and that is what these two halves hold.
    /// </remarks>
    [Fact]
    public void Tonemapping_takes_its_exposure_from_the_lens_only_when_nothing_authored_one() {
        // Sunny sixteen, which a light meter calls EV 15. That the lens agrees is PhysicalCameraTests'
        // claim; what is asserted here is only which of the two sources won — comparing against a
        // number written out here would be testing the arithmetic twice and pinning a rounding.
        var lens = Camera.Perspective with { Aperture = 16f, ShutterTime = 1f / 125f, Sensitivity = 100f };
        var physical = new RenderView("Camera") { Camera = RenderCamera.Default with { Lens = lens } };

        using var authored = new TonemapRenderer {
            Source = "SceneColour",
            Output = "Display",
            Exposure = 2f,
            LensExposure = false,
            View = physical
        };

        using var h = Build(authored);

        Frame(h);
        Assert.Equal(2f, authored.Pass.Parameters.Get(TonemapKeys.Exposure), 5);

        using var metered = new TonemapRenderer {
            Source = "SceneColour",
            Output = "Display",
            Exposure = 1f,
            LensExposure = true,
            View = physical
        };

        using var second = Build(metered);

        Frame(second);

        Assert.Equal(
            Photometry.ExposureFromEv100(lens.Ev100),
            metered.Pass.Parameters.Get(TonemapKeys.Exposure),
            9
        );

        Assert.NotEqual(1f, metered.Pass.Parameters.Get(TonemapKeys.Exposure));
    }

    /// <summary>
    ///     ⚠ A volume's overlay reaches a node, and lays over its authored value without eating it.
    /// </summary>
    /// <remarks>
    ///     <b>The drift this rules out is the one that would look like a working feature for one
    ///     frame.</b> A node that wrote the overlay into its own properties would lose what the
    ///     document said the first time a volume reached it — and walking back out would restore the
    ///     volume's numbers rather than the document's, for ever. So the same node is framed twice
    ///     here: once inside a volume and once after it has gone.
    /// </remarks>
    [Fact]
    public void A_volume_lays_over_the_authored_value_and_gives_it_back() {
        using var node = new TonemapRenderer {
            Source = "SceneColour",
            Output = "Display",
            Saturation = 1.2f,
            Contrast = 1.06f
        };

        using var h = Build(node);

        Frame(h);
        Assert.Equal(1.2f, node.Pass.Parameters.Get(TonemapKeys.Saturation), 5);

        var inside = PostProcessOverlay.None;
        inside.Add(new() { Saturation = 0.4f }, 1f);

        node.Apply(inside);
        Frame(h);

        Assert.Equal(0.4f, node.Pass.Parameters.Get(TonemapKeys.Saturation), 5);

        // ⚠ And the contrast the volume said nothing about is still the document's, which is what
        // makes a volume an override rather than a replacement.
        Assert.Equal(1.06f, node.Pass.Parameters.Get(TonemapKeys.Contrast), 5);

        // Walking out. An empty overlay is delivered rather than skipped, and this is why.
        node.Apply(PostProcessOverlay.None);
        Frame(h);

        Assert.Equal(1.2f, node.Pass.Parameters.Get(TonemapKeys.Saturation), 5);
    }

    /// <summary>
    ///     ⚠ A volume's temperature never overshoots on its way in, at any weight.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         <b>The regression this pins was visible and I shipped it.</b> Walking into a volume that
    ///         asks for 7800 K, with no authored temperature, used to interpolate the <em>kelvin</em> —
    ///         so a thousandth of the way in the frame was being balanced for 7.8 K, which
    ///         <c>Photometry.FromTemperature</c> clamps to 1667 K, whose reciprocal is a huge blue
    ///         gain. The multiplier went (1, 1, 1) → (0.000, 0.003, 13.8) → (1.04, 1.01, 0.82): a hard
    ///         flip to blue at the volume's edge, getting <em>less</em> blue further in.
    ///     </para>
    ///     <para>
    ///         Temperature is the only field in <c>PostProcessSettings</c> whose zero is a sentinel
    ///         rather than a value, which is why it is the only one that cannot be lerped in its own
    ///         units. The check is the general one: no channel may leave the interval its two endpoints
    ///         span.
    ///     </para>
    /// </remarks>
    [Theory]
    [InlineData(0f, 7800f)]
    [InlineData(0f, 2400f)]
    [InlineData(3200f, 7800f)]
    [InlineData(7800f, 0f)]
    public void A_volumes_temperature_never_overshoots_its_endpoints(float authored, float wanted) {
        using var node = new TonemapRenderer { Source = "SceneColour", Output = "Display", Temperature = authored };
        using var h = Build(node);

        node.Apply(PostProcessOverlay.None);
        Frame(h);

        var start = node.Pass.Parameters.Get(TonemapKeys.WhiteBalance);

        var full = PostProcessOverlay.None;
        full.Add(new() { Temperature = wanted }, 1f);
        node.Apply(full);
        Frame(h);

        var end = node.Pass.Parameters.Get(TonemapKeys.WhiteBalance);

        // Every weight in between, including the sliver where the old version exploded.
        foreach (var weight in new[] { 0f, 0.0001f, 0.001f, 0.01f, 0.1f, 0.25f, 0.5f, 0.75f, 0.9f, 1f }) {
            var overlay = PostProcessOverlay.None;
            overlay.Add(new() { Temperature = wanted }, weight);

            node.Apply(overlay);
            Frame(h);

            var at = node.Pass.Parameters.Get(TonemapKeys.WhiteBalance);

            Between(start.X, end.X, at.X, weight, "red");
            Between(start.Y, end.Y, at.Y, weight, "green");
            Between(start.Z, end.Z, at.Z, weight, "blue");
        }

        static void Between(float from, float to, float value, float weight, string channel) {
            var low = MathF.Min(from, to) - 1e-4f;
            var high = MathF.Max(from, to) + 1e-4f;

            Assert.True(
                value >= low && value <= high,
                $"at weight {weight} the {channel} gain was {value}, outside [{low}, {high}]"
            );
        }
    }

    /// <summary>
    ///     ⚠ And a temperature nobody sets leaves the authored balance exactly alone.
    /// </summary>
    /// <remarks>
    ///     The other half: the fix resolves a *target* balance whenever either half of the white
    ///     balance is claimed, so a volume that claims neither must not round-trip the authored one
    ///     through a lerp at weight zero and come back subtly different.
    /// </remarks>
    [Fact]
    public void A_frame_with_no_volume_keeps_the_authored_white_balance() {
        using var node = new TonemapRenderer {
            Source = "SceneColour",
            Output = "Display",
            Temperature = 3200f,
            Tint = 0.15f
        };

        using var h = Build(node);

        Frame(h);

        var expected = Photometry.WhiteBalance(3200f, 0.15f);
        var actual = node.Pass.Parameters.Get(TonemapKeys.WhiteBalance);

        Assert.Equal(expected.R, actual.X, 6);
        Assert.Equal(expected.G, actual.Y, 6);
        Assert.Equal(expected.B, actual.Z, 6);
    }

    /// <summary>
    ///     ⚠ Exposure arrives as a compensation, so it composes with a meter rather than fighting it.
    /// </summary>
    /// <remarks>
    ///     A metered frame ignores the tonemap's <c>Exposure</c> entirely — the shader reads the
    ///     buffer — so a volume that darkened a cellar by scaling that number would do nothing in
    ///     exactly the frames that have an auto-exposure. The compensation multiplies whichever source
    ///     won, which is a separate uniform for that reason.
    /// </remarks>
    [Fact]
    public void A_volumes_exposure_is_a_compensation_and_not_a_multiplier() {
        using var node = new TonemapRenderer { Source = "SceneColour", Output = "Display", Exposure = 2f };
        using var h = Build(node);

        Frame(h);

        Assert.Equal(0f, node.Pass.Parameters.Get(TonemapKeys.ExposureCompensation), 5);

        var overlay = PostProcessOverlay.None;
        overlay.Add(new() { ExposureCompensation = -2f }, 1f);

        node.Apply(overlay);
        Frame(h);

        // The compensation moved and the exposure did not: two stops down, applied on top of
        // whichever source the frame ended up with.
        Assert.Equal(-2f, node.Pass.Parameters.Get(TonemapKeys.ExposureCompensation), 5);
        Assert.Equal(2f, node.Pass.Parameters.Get(TonemapKeys.Exposure), 5);
    }

    /// <summary>
    ///     ⚠ A look's <c>ev100</c> pins the fixed exposure, and the blend between the two runs in
    ///     stops rather than in linear exposure.
    /// </summary>
    /// <remarks>
    ///     The temperature's convert-then-blend rule in the one other unit whose raw lerp is wrong:
    ///     half way between two exposures four stops apart is two stops, not the linear midpoint a
    ///     stop brighter. The full-weight case is the look profile's — its base layer always arrives
    ///     at weight 1 — and the half-weight case is a volume repinning the EV across a doorway.
    /// </remarks>
    [Fact]
    public void A_looks_ev100_pins_the_fixed_exposure_and_blends_in_stops() {
        using var node = new TonemapRenderer { Source = "SceneColour", Output = "Display", Exposure = 2f };
        using var h = Build(node);

        var pinned = PostProcessOverlay.None;
        pinned.Add(new() { Ev100 = 13f }, 1f);

        node.Apply(pinned);
        Frame(h);

        // Six places, not nine: even at full weight the pin is a lerp, and its last-bit rounding
        // survives the EV round trip.
        Assert.Equal(Photometry.ExposureFromEv100(13f), node.Pass.Parameters.Get(TonemapKeys.Exposure), 6);

        var half = PostProcessOverlay.None;
        half.Add(new() { Ev100 = 13f }, 0.5f);

        node.Apply(half);
        Frame(h);

        var halfWay = MathUtil.Lerp(Photometry.Ev100FromExposure(2f), 13f, 0.5f);

        Assert.Equal(Photometry.ExposureFromEv100(halfWay), node.Pass.Parameters.Get(TonemapKeys.Exposure), 6);

        // And the pin leaves with the look, restoring the document's own exposure.
        node.Apply(PostProcessOverlay.None);
        Frame(h);

        Assert.Equal(2f, node.Pass.Parameters.Get(TonemapKeys.Exposure), 5);
    }

    /// <summary>
    ///     ⚠ A look's grade reaches a tonemap whose own grade is off — the permutation turns on, the
    ///     blend starts from neutral, and walking away turns it back off.
    /// </summary>
    /// <remarks>
    ///     The case the Standard Frame creates on purpose: its emitted tonemap is neutral, with
    ///     <c>Grading</c> null, and the project's grade arrives through the fold. The base the
    ///     overlay lays over must be <see cref="ColorGrading.Neutral" /> and never <c>default</c> —
    ///     a zeroed range is a saturation of zero and a gain of zero, which is a black greyscale
    ///     frame delivered only to projects using the feature as designed.
    /// </remarks>
    [Fact]
    public void A_looks_grade_reaches_a_neutral_tonemap_and_leaves_when_the_look_does() {
        using var node = new TonemapRenderer { Source = "SceneColour", Output = "Display" };
        using var h = Build(node);

        Frame(h);
        Assert.False(node.Pass.Parameters.Get(TonemapKeys.UseColorGrading));

        var overlay = PostProcessOverlay.None;

        overlay.Add(
            new() {
                Grading = ColorGrading.Neutral with {
                    Shadows = ColorGradingRange.Neutral with { Gain = new(0.8f, 0.9f, 1.3f) },
                    HighlightsMin = 0.6f
                }
            },
            1f
        );

        node.Apply(overlay);
        Frame(h);

        Assert.True(node.Pass.Parameters.Get(TonemapKeys.UseColorGrading));
        Assert.Equal(new Vector3(0.8f, 0.9f, 1.3f), node.Pass.Parameters.Get(TonemapKeys.GradeShadowGain));
        Assert.Equal(0.6f, node.Pass.Parameters.Get(TonemapKeys.GradeHighlightsMin), 5);

        // Everything the grade left neutral is the identity, which is the whole "from Neutral" claim.
        Assert.Equal(1f, node.Pass.Parameters.Get(TonemapKeys.GradeGlobalSaturation), 5);
        Assert.Equal(Vector3.One, node.Pass.Parameters.Get(TonemapKeys.GradeMidtoneGain));

        // Walking away: the permutation folds back out rather than a neutral grade lingering.
        node.Apply(PostProcessOverlay.None);
        Frame(h);

        Assert.False(node.Pass.Parameters.Get(TonemapKeys.UseColorGrading));
    }

    /// <summary>The fog's two switches take a look's word, and flip at the blend's midpoint.</summary>
    /// <remarks>
    ///     A switch cannot crossfade — there is no forty-percent atmosphere model — so
    ///     <see cref="BlendedToggle" /> takes the heavier side. Below half the authored value holds;
    ///     past half the opinion does.
    /// </remarks>
    [Fact]
    public void A_looks_fog_switches_reach_the_pass_and_flip_at_half_weight() {
        using var node = new FogRenderer { Source = "SceneColour", Depth = "SceneDepth", Output = "Fogged" };
        using var h = Build(node);

        Frame(h);
        Assert.True(node.Pass.Parameters.Get(FogKeys.HeightFalloff));

        var faint = PostProcessOverlay.None;
        faint.Add(new() { FogHeightFalloff = false, FogSunScattering = false }, 0.4f);

        node.Apply(faint);
        Frame(h);

        Assert.True(node.Pass.Parameters.Get(FogKeys.HeightFalloff));
        Assert.True(node.Pass.Parameters.Get(FogKeys.SunScattering));

        var committed = PostProcessOverlay.None;
        committed.Add(new() { FogHeightFalloff = false, FogSunScattering = false }, 1f);

        node.Apply(committed);
        Frame(h);

        Assert.False(node.Pass.Parameters.Get(FogKeys.HeightFalloff));
        Assert.False(node.Pass.Parameters.Get(FogKeys.SunScattering));
    }

    /// <summary>
    ///     ⚠ The shader has always declared these; nothing wrote them. A binding nobody fills keeps
    ///     the shader's own default, so the scattering peak sat at straight up and the mist plane at
    ///     y=0 whatever the scene was — a wrong picture rather than no picture.
    /// </summary>
    [Fact]
    public void The_suns_direction_and_the_mist_plane_reach_the_pass() {
        using var node = new FogRenderer {
            Source = "SceneColour",
            Depth = "SceneDepth",
            Output = "Fogged",
            Height = 12f,
            HeightFalloffRate = 0.2f,
            SunDirection = new(0.3f, -0.8f, 0.5f),
            SunColour = new(1f, 0.5f, 0.25f),
            SunAnisotropy = 0.55f
        };

        using var h = Build(node);

        Frame(h);

        Assert.Equal(12f, node.Pass.Parameters.Get(FogKeys.FogHeight));
        Assert.Equal(0.2f, node.Pass.Parameters.Get(FogKeys.HeightFalloffRate));
        Assert.Equal(new Vector3(0.3f, -0.8f, 0.5f), node.Pass.Parameters.Get(FogKeys.SunDirection));
        Assert.Equal(new Vector3(1f, 0.5f, 0.25f), node.Pass.Parameters.Get(FogKeys.SunColor));
        Assert.Equal(0.55f, node.Pass.Parameters.Get(FogKeys.SunAnisotropy));
    }

    /// <summary>
    ///     A document is where the sun is authored, on <c>!Water</c>'s terms — the two are the same
    ///     quantity and a scene whose water and fog disagree about the hour is the failure.
    /// </summary>
    [Fact]
    public void A_document_names_the_suns_direction_for_the_fog() {
        using var system = new RenderSystem();
        using var scene = new SceneConstants(device);

        var star = new Star(null);

        using var node = (FogRenderer)new PostEffectFactory().Create(
            new FogAsset {
                Source = "SceneColour",
                Depth = "SceneDepth",
                Height = 3f,
                HeightFalloffRate = 0.11f,
                SunDirection = new(0f, -0.6f, -0.8f),
                SunColour = new(0.9f, 0.8f, 0.6f),
                SunAnisotropy = 0.42f
            },
            new(system) { Sun = star, SceneConstants = scene }
        )!;

        // ⚠ The two the document cannot carry, and the reason a `!Fog` node needs no colours at all.
        // Both of this pass's targets are radiances in the frame's own units, so a node left to its
        // authored numbers is the pass this task existed to find. See `FogRenderer.Scattering`.
        Assert.Same(star, node.Sun);
        Assert.Same(scene, node.Frame);

        Assert.Equal(3f, node.Height);
        Assert.Equal(0.11f, node.HeightFalloffRate);
        Assert.Equal(new Vector3(0f, -0.6f, -0.8f), node.SunDirection);
        Assert.Equal(new Vector3(0.9f, 0.8f, 0.6f), node.SunColour);
        Assert.Equal(0.42f, node.SunAnisotropy);

        // The unstated asset stands where the shader's own declaration does, so a document that
        // says nothing renders what it rendered before this wiring existed.
        var bare = new FogAsset();

        Assert.Equal(FogKeys.FogHeight.DefaultValue, bare.Height);
        Assert.Equal(FogKeys.HeightFalloffRate.DefaultValue, bare.HeightFalloffRate);
        Assert.Equal(FogKeys.SunDirection.DefaultValue, bare.SunDirection);
        Assert.Equal(FogKeys.SunColor.DefaultValue, bare.SunColour);
        Assert.Equal(FogKeys.SunAnisotropy.DefaultValue, bare.SunAnisotropy);
    }

    /// <summary>
    ///     The analytic fog's two colours come from the frame's own sky and sun, in the frame's units.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>Both are radiances in cd/m², and both shipped as tints.</b> <c>Fog.rvn</c> ends in
    ///         <c>lerp(colour, tint, amount)</c> against a scene lit in cd/m², so its targets are what
    ///         a pixel <em>becomes</em> — and <c>(0.5, 0.6, 0.7)</c> against a sun of ninety thousand
    ///         lux is not a pale veil but a lerp toward black. It is the third pass to ship this
    ///         mistake, after <c>!Water</c> and <c>!VolumetricFog</c>, and the third to be
    ///         indistinguishable from one that never ran: zeroing this node's contribution on sample
    ///         13 at 512 frames moved the picture by 0.023/255 mean channel against a 0.023/255
    ///         run-to-run floor. Deriving the two from the scene moved it by 20.2/255.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The sky term is the environment's mean radiance and not its <c>L00</c></b>, which
    ///         is 4π·Y₀ = 3.54× larger; and the sun term is an <em>illuminance</em> added to that sky,
    ///         because <c>lerp(fog, fog + E, p)</c> is <c>fog + p·E</c> — the source function. Getting
    ///         either of those wrong leaves a number nobody has a second source for.
    ///     </para>
    /// </remarks>
    [Fact]
    public void The_frames_own_sky_and_sun_are_what_the_fog_scatters() {
        using var node = new FogRenderer { Source = "SceneColour", Depth = "SceneDepth", Output = "Fogged" };
        using var h = Build(node);
        using var scene = new SceneConstants(device);

        var star = new RenderLight {
            Kind = LightKind.Directional,
            Direction = Vector3.Normalize(new(-0.35f, -0.65f, -0.4f)),
            Colour = new(1f, 0.9f, 0.7f),
            Intensity = 90_000f,
            Unit = LightUnit.Lux
        };

        scene.Lighting = new() {
            Environment = new() { Irradiance = new() { L00 = new(6698f, 6254f, 5249f) }, Intensity = 2f }
        };

        node.Sun = new Star(star);
        node.Frame = scene;

        Frame(h);

        var sky = new Vector3(6698f, 6254f, 5249f) * (0.282095f * 2f);
        var tint = node.Pass.Parameters.Get(FogKeys.FogColor);
        var peak = node.Pass.Parameters.Get(FogKeys.SunColor);

        Assert.Equal(star.Direction, node.Pass.Parameters.Get(FogKeys.SunDirection));
        Assert.Equal(sky, tint);
        Assert.Equal(sky + star.Radiance, peak);

        // ⚠ The coefficient itself would pass an equality written against the code, so the ratio is
        // asserted against the physics instead: an SH projection of a uniform environment of radiance
        // L stores L·Y₀·4π, and handing that over is three and a half times too much sky.
        Assert.True(
            tint.X < 6698f * 2f * 0.9f,
            $"The sky reached the fog as {tint}, which is its L00 rather than its mean radiance."
        );

        // Stated as a magnitude as well as an equality, because `Radiance` could itself regress to a
        // tint and every other assertion in this file would still pass.
        Assert.True(
            peak.X > 1000f && tint.X > 100f,
            $"The fog's targets reached the shader as {tint} and {peak}, which are tints and not "
            + "radiances. A frame lit in cd/m² is lerped toward these, so unit-scale values are not "
            + "a subtle fog — they are a fade to black."
        );
    }

    /// <summary>
    ///     A frame with neither a sun nor a sky leaves the authored pair exactly alone.
    /// </summary>
    /// <remarks>
    ///     Availability rather than preference, on <c>VolumetricFogRenderer.Sunlight</c>'s rule: a
    ///     frame between scenes has no directional light and no environment for a frame or two, and
    ///     fog that went black for them would flash. ⚠ The fallback goes through the same identity
    ///     the derivation does — the peak is <c>Colour + (SunColour − Colour)</c> — so a document that
    ///     authors both gets both back unchanged rather than approximately.
    /// </remarks>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Without_a_sun_or_a_sky_the_authored_pair_is_what_the_fog_gets(bool sourced) {
        using var node = new FogRenderer {
            Source = "SceneColour",
            Depth = "SceneDepth",
            Output = "Fogged",
            Colour = new(1200f, 1100f, 1500f),
            SunDirection = Vector3.Normalize(new(1f, -2f, 0.5f)),
            SunColour = new(21_000f, 13_000f, 4_000f)
        };

        using var h = Build(node);

        if (sourced) {
            node.Sun = new Star(null);
            node.Frame = new SceneConstants(device);
        }

        Frame(h);

        node.Frame?.Dispose();

        Assert.Equal(node.SunDirection, node.Pass.Parameters.Get(FogKeys.SunDirection));
        Assert.Equal(node.Colour, node.Pass.Parameters.Get(FogKeys.FogColor));
        Assert.Equal(node.SunColour, node.Pass.Parameters.Get(FogKeys.SunColor));
    }

    /// <summary>
    ///     The fog's two authored defaults are photometric quantities and not colours.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>A test about a default, deliberately.</b> Every other assertion about this pass passes
    ///     for a fog whose targets are fifteen stops down — the falloff is right, the height term is
    ///     right, the binding is right, and the frame has no atmosphere in it. A default inside
    ///     <c>[0, 1]</c> is what a colour picker produces and is the shape all three of these bugs
    ///     had, so the value itself is the thing worth pinning.
    /// </remarks>
    [Fact]
    public void The_fogs_two_defaults_are_radiances_and_not_tints() {
        using var node = new FogRenderer { Source = "SceneColour", Depth = "SceneDepth", Output = "Fogged" };

        foreach (var (name, value) in new (string, Vector3)[] {
                     ("FogRenderer.Colour", node.Colour),
                     ("FogRenderer.SunColour", node.SunColour),
                     ("FogAsset.Colour", new FogAsset().Colour),
                     ("FogAsset.SunColour", new FogAsset().SunColour),
                     ("Fog.rvn fogColor", FogKeys.FogColor.DefaultValue),
                     ("Fog.rvn sunColor", FogKeys.SunColor.DefaultValue)
                 }) {
            Assert.True(
                Math.Max(value.X, Math.Max(value.Y, value.Z)) > 1f,
                $"{name} defaults to {value}, which is a tint. It is a target the scene's own "
                + "radiance is lerped toward, so a value inside [0, 1] is a fog that fades a frame "
                + "lit in cd/m² to black rather than hazing it."
            );
        }
    }

    /// <summary>A stand-in for the frame's directional light, or for a frame that has none.</summary>
    sealed class Star(RenderLight? sun) : ISunSource {
        public RenderLight? Sun { get; } = sun;
    }

    /// <summary>
    ///     ⚠ The factory is what decides, and it decides from what the document left out.
    /// </summary>
    [Fact]
    public void A_document_that_names_no_exposure_is_the_one_that_gets_the_lens() {
        Assert.True(Lens(new TonemapAsset { Source = "SceneHdr", View = "Camera" }));
        Assert.False(Lens(new TonemapAsset { Source = "SceneHdr", View = "Camera", Ev100 = 13f }));
        Assert.False(Lens(new TonemapAsset { Source = "SceneHdr", View = "Camera", Exposure = 2f }));

        static bool Lens(TonemapAsset declared) {
            using var system = new RenderSystem();
            using var node = (TonemapRenderer)new PostEffectFactory().Create(declared, new(system))!;

            return node.LensExposure;
        }
    }

    /// <summary>
    ///     ⚠ Defocus comes off the lens, and a camera without one leaves the frame sharp.
    /// </summary>
    /// <remarks>
    ///     There is no manual mode on purpose. An aperture that sets the exposure and a blur radius
    ///     typed beside it are two answers to one question, and the failure mode of a guessed default
    ///     is a project that never asked for depth of field getting a soft frame it cannot explain.
    /// </remarks>
    [Fact]
    public void Defocus_reads_the_lens_and_is_a_copy_without_one() {
        var bare = new RenderView("Camera") { Camera = RenderCamera.Default };

        using var sharp = new DepthOfFieldRenderer { Source = "SceneColour", Depth = "SceneDepth", View = bare };
        using var h = Build(sharp);

        Frame(h);

        // Zero is what the shader reads as "focused at infinity", which blurs nothing.
        Assert.Equal(0f, sharp.Pass.Parameters.Get(DepthOfFieldKeys.FocusDistance), 6);

        var lens = Camera.Perspective with { FocalLength = 85f, Aperture = 1.4f, FocusDistance = 4f };
        var physical = new RenderView("Camera") { Camera = RenderCamera.Default with { Lens = lens } };

        using var defocused = new DepthOfFieldRenderer {
            Source = "SceneColour",
            Depth = "SceneDepth",
            View = physical
        };

        using var second = Build(defocused);

        Frame(second);

        Assert.Equal(4f, defocused.Pass.Parameters.Get(DepthOfFieldKeys.FocusDistance), 5);
        Assert.Equal(1.4f, defocused.Pass.Parameters.Get(DepthOfFieldKeys.Aperture), 5);

        // ⚠ Metres, converted once here. Millimetres are the author's unit and the scene's is metres;
        // a shader multiplying by 0.001 per pixel would be a unit nobody could see.
        Assert.Equal(0.085f, defocused.Pass.Parameters.Get(DepthOfFieldKeys.FocalLength), 6);
        Assert.Equal(0.036f, defocused.Pass.Parameters.Get(DepthOfFieldKeys.SensorWidth), 6);
    }

    // --- The ambient combine --------------------------------------------------

    /// <summary>
    ///     An absent optional plane binds the direct plane as a stand-in, with its switch at zero.
    /// </summary>
    /// <remarks>
    ///     The set has a slot per binding whether or not a document named a plane — a set is written
    ///     wholly or not at all — so what "optional" means is a stand-in in the slot and a switch
    ///     that keeps its texels out of the answer. Each resource is declared read once, however
    ///     many slots it fills: four stand-ins over one plane is one dependency, not four.
    /// </remarks>
    [Fact]
    public void The_combine_stands_in_for_what_a_document_left_out() {
        using var combine = new AmbientCombineRenderer {
            Direct = "SceneColour",
            Albedo = "SceneAlbedo",
            Normals = "SceneNormals",
            Output = "Out"
        };

        using var h = Build(combine);

        h.Compositor.Imports["SceneAlbedo"] = Colour("SceneAlbedo");

        Frame(h);

        var textures = combine.Pass.Descriptors.Bindings
            .Where(binding => binding.Kind == DescriptorKind.SampledTexture)
            .ToArray();

        Assert.Equal(9, textures.Length);

        // Every optional slot holds the direct plane — the depth the upsample would test against
        // among them, which is exactly why its own switch has to be off below.
        foreach (var binding in (uint[]) [
                     AmbientCombineKeys.IrradianceBinding,
                     AmbientCombineKeys.OcclusionBinding,
                     AmbientCombineKeys.ContactOcclusionBinding,
                     AmbientCombineKeys.ReflectionsBinding,
                     AmbientCombineKeys.SpecularBinding,
                     AmbientCombineKeys.DepthBufferBinding
                 ]) {
            Assert.Equal("SceneColour", textures.Single(b => b.Binding == binding).Resource);
        }

        // And every switch is off, so the stand-ins' texels are never part of the answer.
        Assert.Equal(0f, combine.Pass.Parameters.Get(AmbientCombineKeys.UseIrradiance));
        Assert.Equal(0f, combine.Pass.Parameters.Get(AmbientCombineKeys.UseOcclusion));
        Assert.Equal(0f, combine.Pass.Parameters.Get(AmbientCombineKeys.UseContactOcclusion));
        Assert.Equal(0f, combine.Pass.Parameters.Get(AmbientCombineKeys.UseReflections));
        Assert.Equal(0f, combine.Pass.Parameters.Get(AmbientCombineKeys.UseSpecular));
        Assert.Equal(0f, combine.Pass.Parameters.Get(AmbientCombineKeys.UseBilateral));

        // The sky's too, and it is the one switch that is off for a *scene* reason rather than a
        // document one: this node has no Lighting, so there are no coefficients to rebuild ambient
        // from and the honest answer is none. A frame that does have an environment is the test
        // below.
        Assert.Equal(0f, combine.Pass.Parameters.Get(AmbientCombineKeys.UseEnvironmentSh));

        // One read per distinct plane, not per binding.
        Assert.Equal(3, combine.Pass.Reads.Count);
    }

    /// <summary>
    ///     With no irradiance plane the combine rebuilds ambient from the scene's own sky.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>The frame this defends had no diffuse ambient in it whatsoever.</b> A split
    ///         shading pass withholds the diffuse half unconditionally — <c>ForwardPlus.rvn</c>'s
    ///         <c>SplitOutputs</c> branch adds <c>Direct</c>, <c>Punctual</c> and
    ///         <c>AmbientSpecular</c> and pointedly not <c>Ambient</c> — on the promise that this
    ///         node puts it back, and the only thing it had to put back was a screen irradiance
    ///         plane. <c>!StandardFrame</c> splits for reflections as well as for GI and names an
    ///         irradiance plane only at <c>gi: probes</c>, so <c>gi: ambient</c> and
    ///         <c>gi: off, reflections: screen</c> both arrived here with the term withheld and
    ///         nothing to restore it: <c>albedo × irradiance</c> with the irradiance identically
    ///         zero, over the whole frame.
    ///     </para>
    ///     <para>
    ///         So the stand-in for irradiance is the scene's environment rather than nothing, and it
    ///         travels the protocol <see cref="EnvironmentLight.Apply" /> already is — the same nine
    ///         names and the same <c>ambientIntensity</c> a shading pass reads, so the shader cannot
    ///         be lit by a sky that disagrees with the one the pass beside it used.
    ///     </para>
    /// </remarks>
    [Fact]
    public void The_combine_lights_a_frame_that_publishes_no_irradiance() {
        var sky = new ShCoefficients { L00 = new(6_698f, 6_254f, 5_249f), L10 = new(120f, 130f, 190f) };

        using var combine = new AmbientCombineRenderer {
            Direct = "SceneColour",
            Albedo = "SceneAlbedo",
            Normals = "SceneNormals",
            Output = "Out",
            Lighting = new() { Environment = new() { Irradiance = sky, Intensity = 1.5f } }
        };

        using var h = Build(combine);

        h.Compositor.Imports["SceneAlbedo"] = Colour("SceneAlbedo");

        Frame(h);

        // On, with no irradiance plane named — which is the whole point: the two switches are not
        // the same question, and an empty seat is "no screen plane" rather than "no ambient".
        Assert.Equal(0f, combine.Pass.Parameters.Get(AmbientCombineKeys.UseIrradiance));
        Assert.Equal(1f, combine.Pass.Parameters.Get(AmbientCombineKeys.UseEnvironmentSh));

        // The scene's coefficients, under the names EnvironmentLight.Apply writes and the shader
        // declares. Two of the nine, because the failure this catches is the whole block missing
        // rather than one band landing at the wrong offset — which the reflection decides.
        Assert.Equal(sky.L00, combine.Pass.Parameters.Get(AmbientCombineKeys.EnvironmentShL00));
        Assert.Equal(sky.L10, combine.Pass.Parameters.Get(AmbientCombineKeys.EnvironmentShL10));

        // ⚠ The scene's intensity, and not the document's. They are separate seats with separate
        // writers: this one is EnvironmentLight.Intensity, the number the unsplit pass applies to
        // its own ambient, and folding it into `intensity` below would have the compositor and the
        // scene overwriting each other.
        Assert.Equal(1.5f, combine.Pass.Parameters.Get(AmbientCombineKeys.AmbientIntensity));
        Assert.Equal(1f, combine.Pass.Parameters.Get(AmbientCombineKeys.Intensity));
    }

    /// <summary>A named plane lands in its own slot with its switch on.</summary>
    [Fact]
    public void The_combine_reads_each_plane_a_document_names() {
        using var combine = new AmbientCombineRenderer {
            Direct = "SceneColour",
            Albedo = "SceneAlbedo",
            Normals = "SceneNormals",
            Irradiance = "Indirect",
            Occlusion = "AmbientOcclusion",
            ContactOcclusion = "Contact",
            Reflections = "Mirrors",
            Specular = "SceneSpecular",
            Intensity = 2f,
            Output = "Out"
        };

        using var h = Build(combine);

        foreach (var name in (string[])[
                     "SceneAlbedo", "Indirect", "AmbientOcclusion", "Contact", "Mirrors", "SceneSpecular"
                 ]) {
            h.Compositor.Imports[name] = Colour(name);
        }

        Frame(h);

        var textures = combine.Pass.Descriptors.Bindings
            .Where(binding => binding.Kind == DescriptorKind.SampledTexture)
            .ToArray();

        Assert.Equal("Indirect", textures.Single(b => b.Binding == AmbientCombineKeys.IrradianceBinding).Resource);

        Assert.Equal(
            "AmbientOcclusion",
            textures.Single(b => b.Binding == AmbientCombineKeys.OcclusionBinding).Resource
        );

        Assert.Equal(
            "Contact",
            textures.Single(b => b.Binding == AmbientCombineKeys.ContactOcclusionBinding).Resource
        );

        Assert.Equal("Mirrors", textures.Single(b => b.Binding == AmbientCombineKeys.ReflectionsBinding).Resource);

        Assert.Equal(
            "SceneSpecular",
            textures.Single(b => b.Binding == AmbientCombineKeys.SpecularBinding).Resource
        );

        Assert.Equal(1f, combine.Pass.Parameters.Get(AmbientCombineKeys.UseIrradiance));
        Assert.Equal(1f, combine.Pass.Parameters.Get(AmbientCombineKeys.UseOcclusion));
        Assert.Equal(1f, combine.Pass.Parameters.Get(AmbientCombineKeys.UseContactOcclusion));
        Assert.Equal(1f, combine.Pass.Parameters.Get(AmbientCombineKeys.UseReflections));
        Assert.Equal(1f, combine.Pass.Parameters.Get(AmbientCombineKeys.UseSpecular));
        Assert.Equal(2f, combine.Pass.Parameters.Get(AmbientCombineKeys.Intensity));

        Assert.Equal(8, combine.Pass.Reads.Count);
    }

    /// <summary>
    ///     The traced plane replaces the shading pass's own specular ambient, or neither happens —
    ///     one condition, so the two can never disagree.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>This is the invariant the whole f0 plane rests on.</b> The shader <em>adds</em>
    ///         the traced reflection weighted by the surface's f0, which only balances once the
    ///         shading pass has stopped writing the prefiltered-cube term the addition replaces —
    ///         and it only stops when <c>SceneLighting.AmbientSpecular</c> reaches zero. On with one
    ///         of the two and the frame counts the dusk sky twice; off with the other and every
    ///         surface loses its specular ambient outright. Neither failure throws.
    ///     </para>
    ///     <para>
    ///         So a half-migrated document — reflections named, no f0 plane — draws the frame it
    ///         drew before rather than either of those, which is what the middle two cases pin.
    ///     </para>
    /// </remarks>
    [Theory]
    [InlineData(true, true, 1f, 0f)]
    [InlineData(true, false, 0f, 1f)]
    [InlineData(false, true, 0f, 1f)]
    [InlineData(false, false, 0f, 1f)]
    public void The_trace_replaces_the_passes_specular_ambient_or_neither_moves(
        bool reflections,
        bool specular,
        float useReflections,
        float ambientSpecular
    ) {
        var lighting = new SceneLighting();

        using var combine = new AmbientCombineRenderer {
            Direct = "SceneColour",
            Albedo = "SceneAlbedo",
            Normals = "SceneNormals",
            Reflections = reflections ? "Mirrors" : null,
            Specular = specular ? "SceneSpecular" : null,
            Lighting = lighting,
            Output = "Out"
        };

        using var h = Build(combine);

        foreach (var name in (string[])["SceneAlbedo", "Mirrors", "SceneSpecular"]) {
            h.Compositor.Imports[name] = Colour(name);
        }

        Frame(h);

        Assert.Equal(useReflections, combine.Pass.Parameters.Get(AmbientCombineKeys.UseReflections));
        Assert.Equal(ambientSpecular, lighting.AmbientSpecular);
    }

    /// <summary>
    ///     Naming a depth turns the bilateral upsample on, and each AO plane's texel comes off the
    ///     texture the graph actually declared.
    /// </summary>
    /// <remarks>
    ///     The AO pair runs at a fraction of the frame and this pass is the one place it meets the
    ///     full-resolution frame, so the upsample lives here — and it can only find a reduced
    ///     plane's texel centres if it is told that plane's own size. Guessing a scale would break
    ///     the day a document runs one AO pass at half resolution and the other at full, which is
    ///     what the two different sizes below stand for.
    /// </remarks>
    [Fact]
    public void The_combine_upsamples_each_reduced_plane_by_its_own_texel() {
        using var combine = new AmbientCombineRenderer {
            Direct = "SceneColour",
            Albedo = "SceneAlbedo",
            Normals = "SceneNormals",
            Occlusion = "AmbientOcclusion",
            ContactOcclusion = "Contact",
            Depth = "SceneDepth",
            Output = "Out"
        };

        using var h = Build(combine);

        h.Compositor.Imports["SceneAlbedo"] = Colour("SceneAlbedo");

        // Half the frame's 320 × 180, and a quarter — two AO planes that disagree about their scale.
        h.Compositor.Imports["AmbientOcclusion"] = Sized("AmbientOcclusion", 160, 90);
        h.Compositor.Imports["Contact"] = Sized("Contact", 80, 45);

        Frame(h);

        Assert.Equal(1f, combine.Pass.Parameters.Get(AmbientCombineKeys.UseBilateral));

        var occlusion = combine.Pass.Parameters.Get(AmbientCombineKeys.OcclusionTexel);
        Assert.Equal(1f / 160f, occlusion.X, 5);
        Assert.Equal(1f / 90f, occlusion.Y, 5);

        var contact = combine.Pass.Parameters.Get(AmbientCombineKeys.ContactTexel);
        Assert.Equal(1f / 80f, contact.X, 5);
        Assert.Equal(1f / 45f, contact.Y, 5);

        // The depth is a real read, not a stand-in: the plane test is what the pass is for.
        var textures = combine.Pass.Descriptors.Bindings
            .Where(binding => binding.Kind == DescriptorKind.SampledTexture)
            .ToArray();

        Assert.Equal("SceneDepth", textures.Single(b => b.Binding == AmbientCombineKeys.DepthBufferBinding).Resource);
        Assert.Contains("SceneDepth", combine.Pass.Reads);
    }

    /// <summary>
    ///     A depth with no AO plane beside it leaves the upsample off.
    /// </summary>
    /// <remarks>
    ///     Four extra taps and an unprojection per pixel, deciding a term both switches discard.
    ///     The guard is cheap and the alternative is a pass that pays for an answer nobody reads.
    /// </remarks>
    [Fact]
    public void The_combine_does_not_upsample_planes_no_document_named() {
        using var combine = new AmbientCombineRenderer {
            Direct = "SceneColour",
            Albedo = "SceneAlbedo",
            Normals = "SceneNormals",
            Depth = "SceneDepth",
            Output = "Out"
        };

        using var h = Build(combine);

        h.Compositor.Imports["SceneAlbedo"] = Colour("SceneAlbedo");

        Frame(h);

        Assert.Equal(0f, combine.Pass.Parameters.Get(AmbientCombineKeys.UseBilateral));
    }

    /// <summary>The factory leaves an empty optional null rather than a resource of no name.</summary>
    [Fact]
    public void A_document_names_the_combine_and_empty_optionals_stay_null() {
        using var system = new RenderSystem();

        var declared = new AmbientCombineAsset {
            Name = "Combine",
            Direct = "SceneHdr",
            Albedo = "SceneAlbedo",
            Normals = "SceneNormals",
            Irradiance = "Indirect"
        };

        using var node = (AmbientCombineRenderer)new PostEffectFactory().Create(declared, new(system))!;

        Assert.Equal("Indirect", node.Irradiance);
        Assert.Null(node.Occlusion);
        Assert.Null(node.ContactOcclusion);
        Assert.Null(node.Reflections);
    }

    /// <summary>
    ///     ⚠ The reflections node publishes its target into the frame's namespace, where a document
    ///     line — the combine's <c>reflections:</c> — can resolve it.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         <b>The regression the engine suites missed, and why they missed it:</b> every other
    ///         fixture here hand-feeds <c>Compositor.Imports</c>, so a consumer naming any texture
    ///         resolves it whether or not the producing node ever published — which papers over
    ///         exactly this seam. <c>ReflectionRenderer</c> imported its target into the render
    ///         graph and never <c>frame.Add</c>ed it, so a combine naming the target was a
    ///         <see cref="CompositorBindingException" /> out of every frame's build — found by
    ///         Samples/13 turning the node on, not by any suite.
    ///     </para>
    ///     <para>
    ///         So this build deliberately imports everything <em>except</em> the reflections target:
    ///         built through the factory on the Null device the way a document builds, "Reflections"
    ///         reaches the combine only if the node itself puts the name into the frame.
    ///     </para>
    /// </remarks>
    [Fact]
    public void The_reflections_target_resolves_for_a_consumer_without_an_import() {
        using var system = new RenderSystem();

        var builder = new CompositorBuilder(system) {
            Device = device,
            Modules = describer,
            Descriptors = allocator,
            Samplers = samplers,
            Effects = effects
        };

        builder.Factories.Add(new PostEffectFactory());

        var compositor = builder.Build(
            new() {
                Version = CompositorBuilder.SupportedVersion,
                Game = new SequenceAsset {
                    Name = "Frame",
                    Children = [
                        new ReflectionsAsset {
                            Name = "Mirrors",
                            Depth = "SceneDepth",
                            Normals = "SceneNormals",
                            Target = "Reflections"
                        },
                        new AmbientCombineAsset {
                            Name = "Combine",
                            Direct = "SceneColour",
                            Albedo = "SceneAlbedo",
                            Normals = "SceneNormals",
                            Reflections = "Reflections",

                            // Named with it, because the two are one switch: without the f0 plane
                            // the node leaves the reflection blend off and this test would be
                            // asserting a term nothing turned on.
                            Specular = "SceneSpecular",
                            Output = "Combined"
                        }
                    ]
                }
            }
        );

        compositor.FrameSize = new(320, 180);

        // Everything the combine reads except the target under test. Importing "Reflections" too
        // would make this test the same lie the other fixtures tell.
        foreach (var name in (string[])["SceneColour", "SceneDepth", "SceneNormals", "SceneAlbedo", "SceneSpecular"]) {
            compositor.Imports[name] = Colour(name);
        }

        var graph = new RenderGraph(device);
        var list = device.BeginCommandList();

        allocator.BeginFrame();
        graph.Reset();

        // The line under test: before the node published, this threw resolving `reflections:`.
        compositor.Build(graph, effects, device);

        graph.Execute(list);
        list.Finish();
        device.GraphicsQueue.Submit([list]);

        var sequence = Assert.IsType<SceneRendererSequence>(compositor.Game);
        var mirror = Assert.IsType<ReflectionRenderer>(sequence.Children[0]);
        var combine = Assert.IsType<AmbientCombineRenderer>(sequence.Children[1]);

        // The name resolved as the node's own texture, and the combine treats it as a real plane:
        // switch on, slot holding the published resource rather than a stand-in.
        Assert.True(mirror.Output.IsValid);
        Assert.Equal(1f, combine.Pass.Parameters.Get(AmbientCombineKeys.UseReflections));

        Assert.Equal(
            "Reflections",
            combine.Pass.Descriptors.Bindings.Single(b => b.Binding == AmbientCombineKeys.ReflectionsBinding).Resource
        );

        graph.DisposePool();
        mirror.Dispose();
        combine.Dispose();
    }

    /// <summary>
    ///     A neighbour tap steps through the plane the shader reads, not through the window.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         <b>The claim <c>RenderScale</c> rests on, and the one nothing made.</b> Every offset
    ///         tap in this library is <c>uv + offset * texelSize</c>, and <c>texelSize</c> came from
    ///         <c>frame.Size</c> everywhere — which is the window's, not the sampled plane's. Those
    ///         are the same number in a frame rendered at native resolution, so the mistake was
    ///         invisible: it only appears once a quality tier declares the scene planes at a fraction
    ///         and the window stays where it is.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Nothing about the failure is loud.</b> Every tap still lands inside the texture,
    ///         the pass still runs, the draw is still recorded and the frame still arrives. What
    ///         changes is only the distance: at half scale FXAA's four luminance taps fall half a
    ///         source texel apart, the range collapses under the edge threshold, and the pass costs
    ///         what it always cost and blends nothing. The point-sampled ones are worse — sharpen,
    ///         the outline, TAA's neighbourhood — because sub-texel steps quantise onto the centre
    ///         texel and the kernel's minimum, maximum and centre become one number.
    ///     </para>
    ///     <para>
    ///         Stated per plane rather than per pass, because the plane is what the number is about:
    ///         the occlusion march wants the depth it unprojects, motion blur wants the grid its
    ///         vectors were written on, and the three colour filters want their source. A pass whose
    ///         key described its own target instead would be asserting the old bug.
    ///     </para>
    /// </remarks>
    [Theory]
    [InlineData("Fxaa", "SceneColour")]
    [InlineData("Sharpen", "SceneColour")]
    [InlineData("Outline", "SceneColour")]
    [InlineData("Ssao", "SceneDepth")]
    [InlineData("MotionBlur", "Motion")]
    [InlineData("Taa", "SceneColour")]
    public void A_neighbour_tap_is_measured_in_the_plane_it_reads(string shader, string plane) {
        using var effect = Create(shader);
        using var h = Build(effect);

        // The scene planes at half the window, which is what a tier's render scale below one
        // declares. The frame stays 320×180 — it is the window, and scaling it would be describing a
        // smaller display rather than a cheaper frame.
        h.Compositor.Imports[plane] = Sized(plane, 160, 90);

        Frame(h);

        var texel = effect.Pass.Parameters.Get(TexelKey(shader));

        Assert.Equal(1f / 160f, texel.X, 5);
        Assert.Equal(1f / 90f, texel.Y, 5);
    }

    /// <summary>The same passes at native resolution are unmoved, which is the safety half.</summary>
    /// <remarks>
    ///     Asked separately rather than folded into the theory above, because the two claims fail for
    ///     opposite reasons: that one catches a texel that stayed the window's, and this one catches a
    ///     texel that stopped being anything. Together they say the change is a no-op wherever the
    ///     scene is rendered at the window's size — which is every tier the engine ships.
    /// </remarks>
    [Theory]
    [InlineData("Fxaa")]
    [InlineData("Sharpen")]
    [InlineData("Outline")]
    [InlineData("Ssao")]
    [InlineData("MotionBlur")]
    [InlineData("Taa")]
    public void At_native_resolution_the_texel_is_still_the_frames(string shader) {
        using var effect = Create(shader);
        using var h = Build(effect);

        Frame(h);

        var texel = effect.Pass.Parameters.Get(TexelKey(shader));

        Assert.Equal(1f / 320f, texel.X, 5);
        Assert.Equal(1f / 180f, texel.Y, 5);
    }

    // --- The fixture --------------------------------------------------------

    /// <summary>Where each effect keeps the texel of the plane it taps.</summary>
    static ParameterKey<Vector2> TexelKey(string shader) =>
        shader switch {
            "Fxaa" => FxaaKeys.TexelSize,
            "Sharpen" => SharpenKeys.TexelSize,
            "Outline" => OutlineKeys.TexelSize,
            "Ssao" => SsaoKeys.TexelSize,
            "MotionBlur" => MotionBlurKeys.TexelSize,
            "Taa" => TaaKeys.TexelSize,
            _ => throw new ArgumentOutOfRangeException(nameof(shader), shader, "no such effect")
        };

    static PostEffectRenderer Create(string shader) =>
        shader switch {
            "Fxaa" => new FxaaRenderer { Source = "SceneColour", Output = "Out" },
            "Sharpen" => new SharpenRenderer { Source = "SceneColour", Output = "Out" },
            "Vignette" => new VignetteRenderer { Source = "SceneColour", Output = "Out" },
            "Fog" => new FogRenderer { Source = "SceneColour", Depth = "SceneDepth", Output = "Out" },
            "Outline" => new OutlineRenderer { Source = "SceneColour", Depth = "SceneDepth", Output = "Out" },
            "MotionBlur" => new MotionBlurRenderer {
                Source = "SceneColour",
                MotionVectors = "Motion",
                Output = "Out"
            },
            "Taa" => new TemporalAntialiasingRenderer {
                Source = "SceneColour",
                MotionVectors = "Motion",
                Depth = "SceneDepth",
                Output = "Out"
            },
            "Ssao" => new AmbientOcclusionRenderer {
                Depth = "SceneDepth",
                Normals = "SceneNormals",
                Output = "Out"
            },
            "DistanceFieldAo" => new DistanceFieldAoRenderer {
                Depth = "SceneDepth",
                Normals = "SceneNormals",
                Output = "Out"
            },
            "AmbientCombine" => new AmbientCombineRenderer {
                Direct = "SceneColour",
                Albedo = "SceneColour",
                Normals = "SceneNormals",
                Output = "Out"
            },
            _ => throw new ArgumentOutOfRangeException(nameof(shader), shader, "no such effect")
        };

    /// <summary>
    ///     The effect, plus something that reads it.
    /// </summary>
    /// <remarks>
    ///     The consumer is not scaffolding — it is what makes the frame a frame. An effect's output is
    ///     a transient the graph owns, so a pass nothing reads is a pass the graph culls, and the
    ///     first version of this fixture recorded no draws at all for exactly that reason. Every
    ///     effect here is therefore tested the way one is used: in a chain, with its result going
    ///     somewhere the frame keeps.
    /// </remarks>
    Harness Build(PostEffectRenderer effect) {
        var system = new RenderSystem();

        effect.Modules = describer;
        effect.Device = device;
        effect.Samplers = samplers;
        effect.Allocator = allocator;

        var consumer = new FullScreenRenderer {
            Name = "Present",
            ShaderName = ConsumerShader,
            ConstantBinding = ConsumerConstantBinding,
            Modules = describer,
            Device = device,
            Samplers = samplers
        };

        consumer.ColourTargets.Add("Display");
        consumer.Reads.Add(effect.Output);
        consumer.Descriptors.Allocator = allocator;

        consumer.Descriptors.Bindings.Add(
            new() { Binding = ConsumerSourceBinding, Kind = DescriptorKind.SampledTexture, Resource = effect.Output }
        );

        consumer.Descriptors.Bindings.Add(
            new() { Binding = ConsumerSamplerBinding, Kind = DescriptorKind.Sampler, Sampler = samplers.LinearClamp }
        );

        var compositor = new GraphicsCompositor(system) {
            FrameSize = new(320, 180),
            Game = new SceneRendererSequence { Children = { effect, consumer } }
        };

        compositor.Imports["Display"] = Colour("Display");

        compositor.Imports["SceneColour"] = Colour("SceneColour");
        compositor.Imports["SceneDepth"] = Colour("SceneDepth");
        compositor.Imports["SceneNormals"] = Colour("SceneNormals");
        compositor.Imports["Motion"] = Colour("Motion");

        return new() { System = system, Compositor = compositor, Graph = new(device), Consumer = consumer };
    }

    ImportedTexture Colour(string name) => Sized(name, 320, 180);

    /// <summary>An import of a stated size, for a plane that does not run at the frame's.</summary>
    ImportedTexture Sized(string name, int width, int height) {
        var description = new TextureDescription(
            PixelFormat.Rgba16Float,
            width,
            height,
            TextureUsage.ColourTarget | TextureUsage.Sampled,
            Name: name
        );

        var texture = device.CreateTexture(description);
        return new(texture, device.CreateTextureView(texture), description);
    }

    void Frame(Harness h) {
        var list = device.BeginCommandList();

        allocator.BeginFrame();
        h.Graph.Reset();
        h.Compositor.Build(h.Graph, effects, device);
        h.Graph.Execute(list);

        list.Finish();
        device.GraphicsQueue.Submit([list]);
    }

    /// <summary>An effect for any key, so a pass under test always resolves one.</summary>
    sealed class AlwaysCompiles(Dictionary<string, DescriptorSetLayoutHandle> layouts) : IEffectProvider {
        public Effect? TryGet(EffectKey key) =>
            new() {
                Key = key,
                Stages = [
                    new(ShaderStage.Vertex, [1, 2, 3, 4], "main"),
                    new(ShaderStage.Fragment, [5, 6, 7, 8], "main")
                ],
                SetLayouts = [default, default, layouts.GetValueOrDefault(key.ShaderName), default],
                ConstantBufferSize = 64
            };
    }

    sealed class Harness : IDisposable {
        public required RenderSystem System { get; init; }

        public required GraphicsCompositor Compositor { get; init; }

        public required RenderGraph Graph { get; init; }

        public required FullScreenRenderer Consumer { get; init; }

        public void Dispose() {
            Consumer.Dispose();
            Graph.DisposePool();
            System.Dispose();
        }
    }
}

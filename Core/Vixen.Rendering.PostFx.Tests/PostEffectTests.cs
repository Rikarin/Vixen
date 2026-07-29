// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;
using Vixen.Graphics;
using Vixen.Graphics.Null;
using Vixen.Graphics.RenderGraph;
using Vixen.Rendering;
using Vixen.Rendering.Compositor;
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
    readonly NullDevice device = new(new() { Record = true, FramesInFlight = 2 });
    readonly EffectSystem effects = new();
    readonly DescriptorAllocator allocator;
    readonly SamplerCache samplers;
    readonly EffectPipelineDescriber describer;
    readonly DescriptorSetLayoutHandle layout;

    public PostEffectTests() {
        allocator = new(device);
        samplers = new(device);
        describer = new(device);

        // Wide enough for the effect with the most bindings — the outline's four textures, its
        // sampler and its block — so one layout serves every pass here.
        layout = device.CreateDescriptorSetLayout(
            new(
                DescriptorSetSlot.PerMaterial,
                [
                    new(0, DescriptorKind.UniformBuffer, ShaderStage.Fragment),
                    new(1, DescriptorKind.SampledTexture, ShaderStage.Fragment),
                    new(2, DescriptorKind.SampledTexture, ShaderStage.Fragment),
                    new(3, DescriptorKind.SampledTexture, ShaderStage.Fragment),
                    new(4, DescriptorKind.SampledTexture, ShaderStage.Fragment),
                    new(5, DescriptorKind.Sampler, ShaderStage.Fragment),
                    new(6, DescriptorKind.Sampler, ShaderStage.Fragment)
                ],
                "PostFx"
            )
        );

        effects.AddProvider(new AlwaysCompiles(layout));
    }

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

    // --- The fixture --------------------------------------------------------

    static PostEffectRenderer Create(string shader) =>
        shader switch {
            "Fxaa" => new FxaaRenderer { Source = "SceneColour", Output = "Out" },
            "Sharpen" => new SharpenRenderer { Source = "SceneColour", Output = "Out" },
            "Vignette" => new VignetteRenderer { Source = "SceneColour", Output = "Out" },
            "Fog" => new FogRenderer { Source = "SceneColour", Depth = "SceneDepth", Output = "Out" },
            "Outline" => new OutlineRenderer { Source = "SceneColour", Depth = "SceneDepth", Output = "Out" },
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
            ShaderName = "Copy",
            ConstantBinding = 0,
            Modules = describer,
            Device = device,
            Samplers = samplers
        };

        consumer.ColourTargets.Add("Display");
        consumer.Reads.Add(effect.Output);
        consumer.Descriptors.Allocator = allocator;

        consumer.Descriptors.Bindings.Add(
            new() { Binding = 1, Kind = DescriptorKind.SampledTexture, Resource = effect.Output }
        );

        consumer.Descriptors.Bindings.Add(
            new() { Binding = 5, Kind = DescriptorKind.Sampler, Sampler = samplers.LinearClamp }
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

    ImportedTexture Colour(string name) {
        var description = new TextureDescription(
            PixelFormat.Rgba16Float,
            320,
            180,
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
    sealed class AlwaysCompiles(DescriptorSetLayoutHandle layout) : IEffectProvider {
        public Effect? TryGet(EffectKey key) =>
            new() {
                Key = key,
                Stages = [
                    new(ShaderStage.Vertex, [1, 2, 3, 4], "main"),
                    new(ShaderStage.Fragment, [5, 6, 7, 8], "main")
                ],
                SetLayouts = [default, default, layout, default],
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

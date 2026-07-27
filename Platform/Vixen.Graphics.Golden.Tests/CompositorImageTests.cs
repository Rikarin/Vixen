// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Immutable;
using Vixen.Rendering;
using Vixen.Rendering.Compositor;
using Vixen.Shaders;
using Vixen.Shaders.Generated;
using Xunit;

namespace Vixen.Graphics.Golden.Tests;

/// <summary>
///     A composed frame, on a real device, compared against a picture.
/// </summary>
/// <remarks>
///     <para>
///         Everything else in this suite renders from a command list. These render from a
///         <see cref="GraphicsCompositor" /> — the layer engine code actually uses — and that
///         distinction is the whole point of the file. The compositor, the descriptor allocator and
///         the constant-buffer writer had been asserted entirely against a recording backend, which
///         agrees with whatever it is told: it will happily record a set bound to the wrong index and
///         a uniform written at the wrong offset, and report that the calls were made.
///     </para>
///     <para>
///         Only a picture separates "the calls I meant" from "the calls that draw". Each fixture here
///         is chosen so that the mistakes it is looking for are visible rather than subtle — an
///         upside-down picture, a black one, a blown-out one.
///     </para>
/// </remarks>
[Collection("Vulkan")]
public sealed class CompositorImageTests {
    static bool TryOpen(out Fixture? fixture) {
        if (Fixture.TryOpen(out fixture, out var reason)) {
            return true;
        }

        if (Environment.GetEnvironmentVariable("VIXEN_REQUIRE_VULKAN") is "1" or "true" or "TRUE") {
            Assert.Fail($"VIXEN_REQUIRE_VULKAN is set, so the golden images may not be skipped: {reason}");
        }

        Assert.Skip(reason ?? "no Vulkan");
        return false;
    }

    /// <summary>
    ///     A scene pass and a full-screen post pass, composed and run.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         The triangle is drawn into a declared-by-name target, then a
    ///         <see cref="FullScreenRenderer" /> samples it and dims it. Five things that had never
    ///         drawn anything all have to be right for the picture to be:
    ///     </para>
    ///     <list type="bullet">
    ///         <item><description>
    ///             the <strong>fullscreen triangle</strong> covers the viewport from
    ///             <c>gl_VertexIndex</c> alone — no vertex buffer is bound anywhere in this frame;
    ///         </description></item>
    ///         <item><description>
    ///             its <strong>UVs</strong> put the source the right way up, where a flipped V is the
    ///             most common way a post pass is wrong and needs a picture to see;
    ///         </description></item>
    ///         <item><description>
    ///             the <strong>descriptor set the allocator wrote</strong> points binding 0 at the
    ///             texture the node declared and binding 1 at the sampler, or the frame is black;
    ///         </description></item>
    ///         <item><description>
    ///             <strong>exposure landed at its own offset</strong>, which is what dims it — writing
    ///             it at the wrong one leaves the triangle at full brightness;
    ///         </description></item>
    ///         <item><description>
    ///             <strong>the graph ordered and barriered the two passes</strong>, or the post pass
    ///             samples a target that has not been drawn into.
    ///         </description></item>
    ///     </list>
    ///     <para>
    ///         <strong><c>whitePoint</c> is deliberately never set.</strong> It reaches the shader as
    ///         the 4 the key carries, all the way from the initialiser in <c>Tonemap.rvn</c> — and if
    ///         that chain were broken anywhere it would arrive as zero, which this shader turns into a
    ///         black frame. The whole default-carrying argument, as a picture.
    ///     </para>
    /// </remarks>
    [Fact]
    public void TonemappedTriangle() {
        if (!TryOpen(out var fixture)) {
            return;
        }

        using var owned = fixture!;
        var device = owned.Device;

        var scene = owned.Owned("scene", TextureUsage.ColourTarget | TextureUsage.Sampled);
        var display = owned.Owned("display", TextureUsage.ColourTarget | TextureUsage.CopySource);

        using var allocator = new DescriptorAllocator(device);
        using var samplers = new SamplerCache(device);
        using var system = new RenderSystem();

        var describer = new EffectPipelineDescriber(device);
        var effects = new EffectSystem();
        effects.AddProvider(new Tonemap(owned));

        var triangle = owned.Pipeline(
            owned.Shader("triangle.vert.spv", ShaderStage.Vertex),
            owned.Shader("triangle.frag.spv", ShaderStage.Fragment),
            BlendState.Opaque,
            DepthStencilState.Disabled
        );

        var post = new FullScreenRenderer {
            Name = "Tonemap",
            ShaderName = TonemapKeys.ShaderName,
            Modules = describer,
            Device = device,
            ConstantBinding = 2,
            Descriptors = { Slot = DescriptorSetSlot.PerFrame, Allocator = allocator }
        };

        post.ColourTargets.Add("Display");
        post.Reads.Add("SceneColour");

        post.Descriptors.Bindings.Add(
            new() { Binding = 0, Kind = DescriptorKind.SampledTexture, Resource = "SceneColour" }
        );

        post.Descriptors.Bindings.Add(
            new() { Binding = 1, Kind = DescriptorKind.Sampler, Sampler = samplers.LinearClamp }
        );

        // Half brightness, and nothing said about the white point.
        post.Parameters.Set(TonemapKeys.Exposure, 0.5f);

        var pass = new RenderPassRenderer { Name = "Scene", ClearColour = new(0f, 0f, 0f, 1f) };
        pass.ColourTargets.Add("SceneColour");

        pass.Children.Add(
            new DelegateSceneRenderer {
                OnRecord = (_, context) => {
                    context.CommandList.BindPipeline(triangle);
                    context.CommandList.Draw(3);
                }
            }
        );

        var compositor = new GraphicsCompositor(system) {
            FrameSize = new(Fixture.Side, Fixture.Side),
            Game = new SceneRendererSequence { Children = { pass, post } }
        };

        compositor.Imports["SceneColour"] = new(scene.Texture, scene.View, scene.Description);

        compositor.Imports["Display"] = new(
            display.Texture,
            display.View,
            display.Description,
            ResourceState.Undefined,
            ResourceState.CopySource
        );

        allocator.BeginFrame();
        var frame = compositor.Build(owned.Graph, effects, device);

        // A gradient, dimmed and rolled off — so the tolerance is the interpolated one rather than
        // the flat one, for the reason the triangle fixture uses it.
        GoldenImage.Verify(
            "tonemapped-triangle",
            owned.Render(frame.Texture("harness", "Display")),
            Tolerance.Interpolated
        );
    }

    /// <summary>
    ///     The effect the post pass resolves to: the fixture's SPIR-V, and the layout it was built for.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         Hand-assembled rather than loaded from a compiled effect, because nothing in this
    ///         repository yet builds <c>Raven/Library</c> into bytecode a test can load — the shaders
    ///         beside this file are the same arithmetic written in GLSL. What is <em>not</em>
    ///         hand-assembled is the parameter table's keys: those are the generated ones, so a rename
    ///         in <c>Tonemap.rvn</c> breaks this build.
    ///     </para>
    ///     <para>
    ///         Set 0 rather than the per-material set the real shader uses, so the pipeline layout
    ///         needs one descriptor set rather than three empty ones in front of it. What is under
    ///         test is which binding each resource landed at, and that is the same question at any set
    ///         index.
    ///     </para>
    /// </remarks>
    sealed class Tonemap : IEffectProvider {
        readonly Effect effect;

        public Tonemap(Fixture fixture) {
            var device = fixture.Device;

            var set = device.CreateDescriptorSetLayout(
                new(
                    DescriptorSetSlot.PerFrame,
                    [
                        new(0, DescriptorKind.SampledTexture, ShaderStage.Fragment),
                        new(1, DescriptorKind.Sampler, ShaderStage.Fragment),
                        new(2, DescriptorKind.UniformBuffer, ShaderStage.Fragment)
                    ],
                    "tonemap"
                )
            );

            var layout = device.CreatePipelineLayout(new([set], null, "tonemap"));

            fixture.Owns(() => {
                device.Destroy(layout);
                device.Destroy(set);
            });

            effect = new() {
                Key = EffectKey.From(TonemapKeys.ShaderName, new(), []),
                Stages = [
                    new(ShaderStage.Vertex, Read(fixture, "fullscreen.vert.spv"), "main"),
                    new(ShaderStage.Fragment, Read(fixture, "tonemap.frag.spv"), "main")
                ],
                SetLayouts = [set],
                Layout = layout,

                // The offsets the GLSL block beside this file has, which are also the ones
                // Tonemap.rvn reports for the same two parameters.
                ConstantBufferSize = 8,
                Parameters = [new(TonemapKeys.Exposure, 0, 4), new(TonemapKeys.WhitePoint, 4, 4)]
            };
        }

        static ImmutableArray<byte> Read(Fixture fixture, string name) =>
            [.. File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Shaders", name))];

        public Effect? TryGet(EffectKey key) => effect;
    }
}

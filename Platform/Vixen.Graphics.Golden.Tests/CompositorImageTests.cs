// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Immutable;
using Vixen.Core.Mathematics;
using Vixen.Rendering;
using Vixen.Rendering.Features;
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
    ///     A depth prepass, and the overdraw it is supposed to remove.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         The hard part of testing a prepass is that when it works the image is identical to one
    ///         without it — that is the entire point of it. So this fixture makes the rejection
    ///         visible: the colour stage blends <strong>additively</strong> and tests
    ///         <c>Equal</c> without writing depth, so a fragment that survives shades once and a
    ///         fragment that survives twice shades twice.
    ///     </para>
    ///     <para>
    ///         Two quads overlap, the red one nearer. Three outcomes, and each says something
    ///         different:
    ///     </para>
    ///     <list type="bullet">
    ///         <item><description>
    ///             <strong>red where they overlap</strong> — the prepass wrote the nearer depth and
    ///             the far quad was rejected. What is committed;
    ///         </description></item>
    ///         <item><description>
    ///             <strong>yellow where they overlap</strong> — both shaded, so nothing was rejected
    ///             and the prepass bought nothing at all;
    ///         </description></item>
    ///         <item><description>
    ///             <strong>black</strong> — the depth an <c>Equal</c> test compares against is not
    ///             the depth the prepass wrote, so every fragment failed. That is what a prepass
    ///             whose vertex stage disagrees with the material's by one instruction looks like.
    ///         </description></item>
    ///     </list>
    ///     <para>
    ///         It is also the first picture the <em>render system</em> has produced rather than the
    ///         compositor alone. Two render objects go through extract, cull, sort and record; the
    ///         prepass stage draws them with <c>DepthOnly</c> and the colour stage with their
    ///         material, off one extraction, which is what <see cref="RenderStage.ShaderName" />
    ///         exists for. And the transform reaches the shader as a push constant, so
    ///         <c>world * position</c> being the right way round — docs/plan/07 § E, which says only a
    ///         device can prove it — is proven here.
    ///     </para>
    /// </remarks>
    [Fact]
    public void DepthPrepass() {
        if (!TryOpen(out var fixture)) {
            return;
        }

        using var owned = fixture!;
        var device = owned.Device;
        var display = owned.Owned("display", TextureUsage.ColourTarget | TextureUsage.CopySource);

        using var system = new RenderSystem();

        // Depth written once, then only tested. `Equal` is what makes the prepass load-bearing: with
        // `Greater` the colour pass would reject the far quad on its own and the prepass would prove
        // nothing.
        var prepass = system.AddStage(new("DepthPrepass") {
            ShaderName = "DepthOnly",
            Rasterizer = RasterizerState.TwoSided
        });

        var opaque = system.AddStage(new("Opaque") {
            DepthStencil = new(DepthWrite: false, DepthCompare: CompareFunction.Equal),
            Blend = BlendState.Additive,
            Rasterizer = RasterizerState.TwoSided
        });

        var describer = new EffectPipelineDescriber(device);

        describer.VertexLayouts.Add([
            new VertexBufferLayout(
                Vertex.Stride,
                [new(0, VertexFormat.Float32X3, 0), new(1, VertexFormat.Float32X4, 12)]
            )
        ]);

        var effects = new EffectSystem();
        effects.AddProvider(new Scene(owned));

        var meshes = new MeshRenderFeature { Pipelines = new(device), Describer = describer };
        var transforms = new TransformRenderFeature();
        var materials = new MaterialRenderFeature { Effects = effects };

        meshes.Add(transforms);
        meshes.Add(materials);
        system.AddFeature(meshes);

        // One quad's worth of geometry twice over, so the two draws differ by an index range and a
        // transform rather than by geometry — which is what puts FirstIndex and the push constant
        // under test alongside everything else.
        var vertices = owned.Buffer<Vertex>(Vertex.Quads.AsSpan(), BufferUsage.Vertex);
        var indices = owned.Buffer<ushort>([0, 1, 2, 2, 1, 3, 4, 5, 6, 6, 5, 7], BufferUsage.Index);

        var view = new RenderView("camera") { Frustum = new(Matrix4x4.Identity) };
        var material = new Material("Mesh");

        // Nearer is *larger* under reversed depth. The near quad is left of centre and the far one
        // right, so they overlap down the middle.
        Add(system, meshes, transforms, materials, material, vertices, indices, 0, -0.15f, 0.75f);
        Add(system, meshes, transforms, materials, material, vertices, indices, 6, 0.15f, 0.25f);

        var depth = new RenderPassRenderer {
            Name = "Prepass",
            DepthTarget = "SceneDepth",

            // Zero is *far*. Clearing to one would put every fragment behind the clear and the
            // prepass would write nothing — the reversed-depth mistake, as a black picture.
            ClearDepth = 0f
        };

        depth.Children.Add(new SingleStageRenderer { View = view, Stage = prepass });

        var colour = new RenderPassRenderer {
            Name = "Scene",
            DepthTarget = "SceneDepth",
            DepthLoad = LoadAction.Load,
            ReadOnlyDepth = true,
            ClearColour = new(0f, 0f, 0f, 1f)
        };

        colour.ColourTargets.Add("Display");
        colour.Children.Add(new SingleStageRenderer { View = view, Stage = opaque });

        var compositor = new GraphicsCompositor(system) {
            FrameSize = new(Fixture.Side, Fixture.Side),
            Game = new SceneRendererSequence { Children = { depth, colour } }
        };

        // Declared, not imported: the depth buffer lives and dies inside the frame, so it is the
        // graph's to allocate — and the two passes over it are what keep it alive between them.
        compositor.Resources.Add(
            new() {
                Name = "SceneDepth",
                Format = PixelFormat.Depth32Float,
                Usage = TextureUsage.DepthStencilTarget
            }
        );

        compositor.Imports["Display"] = new(
            display.Texture,
            display.View,
            display.Description,
            ResourceState.Undefined,
            ResourceState.CopySource
        );

        var frame = compositor.Build(owned.Graph, effects, device);

        // Flat colours with straight edges, so a pixel a shade off is a driver and a pixel in the
        // wrong place is a bug.
        GoldenImage.Verify(
            "depth-prepass",
            owned.Render(frame.Texture("harness", "Display")),
            Tolerance.Flat
        );
    }

    /// <summary>Adds one quad to the scene, in both stages.</summary>
    static void Add(
        RenderSystem system,
        MeshRenderFeature meshes,
        TransformRenderFeature transforms,
        MaterialRenderFeature materials,
        Material material,
        BufferHandle vertices,
        BufferHandle indices,
        int firstIndex,
        float x,
        float z
    ) {
        var id = system.Objects.Add(
            new() {
                // Large enough to intersect any frustum. What is under test is the depth test, and a
                // fixture that quietly culled one of its two objects would look like one that worked.
                Bounds = new(Vector3.Zero, 100f),
                Stages = system.Stages[0].Mask | system.Stages[1].Mask,
                FeatureIndex = meshes.Index
            }
        );

        system.Objects.Data.Data(meshes.Draws)[id.Index] = new() {
            VertexBuffer = vertices,
            IndexBuffer = indices,
            IndexFormat = IndexFormat.UInt16,
            Count = 6,
            FirstIndex = firstIndex,
            InstanceCount = 1
        };

        // Row-vector convention: the translation is in M41..M43, which the shader reads as column 3.
        system.Objects.Data.Data(transforms.World)[id.Index] = new(
            new Vector4(1f, 0f, 0f, 0f),
            new Vector4(0f, 1f, 0f, 0f),
            new Vector4(0f, 0f, 1f, 0f),
            new Vector4(x, 0f, z, 1f)
        );

        materials.Assign(system, id, material);
    }

    /// <summary>One vertex of the fixture's geometry: a position and a flat colour.</summary>
    struct Vertex {
        public const int Stride = 28;

        public Vector3 Position;
        public Vector4 Colour;

        static Vertex At(float x, float y, Vector4 colour) => new() { Position = new(x, y, 0f), Colour = colour };

        /// <summary>Two overlapping quads, the first red and the second green.</summary>
        /// <remarks>
        ///     Half a unit each side of the origin, then moved apart by their transforms. Additive
        ///     blending makes their overlap yellow if both shade and red if only the nearer does,
        ///     which is the entire assertion.
        /// </remarks>
        public static Vertex[] Quads => [
            At(-0.5f, -0.5f, Red), At(0.5f, -0.5f, Red), At(-0.5f, 0.5f, Red), At(0.5f, 0.5f, Red),
            At(-0.5f, -0.5f, Green), At(0.5f, -0.5f, Green), At(-0.5f, 0.5f, Green), At(0.5f, 0.5f, Green)
        ];

        static Vector4 Red => new(0.8f, 0.1f, 0.1f, 1f);
        static Vector4 Green => new(0.1f, 0.8f, 0.1f, 1f);
    }

    /// <summary>
    ///     The two effects the two stages resolve to, sharing one pipeline layout.
    /// </summary>
    /// <remarks>
    ///     The depth-only one has <em>no fragment stage at all</em>, which is what a pass with no
    ///     colour attachments wants and what a prepass is for. One push-constant range, sixty-four
    ///     bytes at zero, which is where <see cref="TransformRenderFeature" /> puts the transform.
    /// </remarks>
    sealed class Scene : IEffectProvider {
        readonly Effect mesh;
        readonly Effect depth;

        public Scene(Fixture fixture) {
            var device = fixture.Device;
            var layout = device.CreatePipelineLayout(new([], [new(ShaderStage.Vertex, 0, 64)], "scene"));

            fixture.Owns(() => device.Destroy(layout));

            mesh = new() {
                Key = EffectKey.From("Mesh", new(), []),
                Stages = [
                    new(ShaderStage.Vertex, Read("scene.vert.spv"), "main"),
                    new(ShaderStage.Fragment, Read("scene.frag.spv"), "main")
                ],
                Layout = layout
            };

            depth = new() {
                Key = EffectKey.From("DepthOnly", new(), []),
                Stages = [new(ShaderStage.Vertex, Read("prepass.vert.spv"), "main")],
                Layout = layout
            };
        }

        static ImmutableArray<byte> Read(string name) =>
            [.. File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Shaders", name))];

        public Effect? TryGet(EffectKey key) => key.ShaderName == "DepthOnly" ? depth : mesh;
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

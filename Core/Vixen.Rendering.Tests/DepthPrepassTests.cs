// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Immutable;
using Vixen.Core.Mathematics;
using Vixen.Graphics;
using Vixen.Graphics.Null;
using Vixen.Graphics.RenderGraph;
using Vixen.Rendering;
using Vixen.Rendering.Compositor;
using Vixen.Rendering.Features;
using Vixen.Shaders;
using Xunit;

namespace Tests;

/// <summary>
///     The depth prepass, and the stage shader override that makes it one.
/// </summary>
/// <remarks>
///     <para>
///         A prepass drawn with each object's own material is not a prepass — it runs every fragment
///         shader twice and costs more than the overdraw it removes. So the load-bearing thing here is
///         not the second pass, which the compositor could always express: it is
///         <see cref="RenderStage.ShaderName" />, which lets one stage draw the same objects with
///         <c>Library/Pipeline/DepthOnly.rvn</c> while another draws them with their materials, in the
///         same frame and off the same extraction.
///     </para>
///     <para>
///         It is the same fix a shadow-caster stage wants, for the same reason: a shadow map records
///         depth, so a caster has no business evaluating a BRDF.
///     </para>
/// </remarks>
public class DepthPrepassTests : IDisposable {
    readonly NullDevice device = new(new() { Record = true });
    readonly EffectSystem effects = new();
    readonly DescriptorSetLayoutHandle materialLayout;
    readonly DescriptorSetHandle materialSet;

    public DepthPrepassTests() {
        materialLayout = device.CreateDescriptorSetLayout(
            new(
                DescriptorSetSlot.PerMaterial,
                [new(0, DescriptorKind.SampledTexture, ShaderStage.Fragment)],
                "Lit"
            )
        );

        materialSet = device.CreateDescriptorSet(materialLayout, "Lit");
        effects.AddProvider(new AlwaysCompiles(materialLayout));
    }

    /// <inheritdoc />
    public void Dispose() {
        device.Dispose();
        GC.SuppressFinalize(this);
    }

    /// <summary>
    ///     A stage that overrides the shader draws a different effect from the one the material named.
    /// </summary>
    /// <remarks>
    ///     One object, one material, two stages, two variants — off one extraction and one cull. That
    ///     is the whole claim, and everything else here is a consequence of it.
    /// </remarks>
    [Fact]
    public void A_stage_with_an_override_resolves_a_different_effect() {
        using var h = Build();
        AddMesh(h);
        Frame(h);

        var prepass = h.Materials.EffectOf(h.System, h.Object, h.Prepass);
        var opaque = h.Materials.EffectOf(h.System, h.Object, h.Opaque);

        Assert.NotNull(prepass);
        Assert.NotNull(opaque);
        Assert.Equal("DepthOnly", prepass.Key.ShaderName);
        Assert.Equal("Lit", opaque.Key.ShaderName);
    }

    /// <summary>A stage that asked for nothing still gets the material's own shader.</summary>
    /// <remarks>
    ///     The check on the previous test: if the override applied everywhere, the two would agree and
    ///     it would look like it worked.
    /// </remarks>
    [Fact]
    public void A_stage_without_an_override_is_unaffected() {
        using var h = Build();
        AddMesh(h);
        Frame(h);

        Assert.Equal(
            h.Materials.EffectOf(h.System, h.Object),
            h.Materials.EffectOf(h.System, h.Object, h.Opaque)
        );
    }

    /// <summary>The two stages bind two different pipelines for the same object.</summary>
    [Fact]
    public void The_prepass_and_the_colour_pass_bind_different_pipelines() {
        using var h = Build();
        AddMesh(h);
        Frame(h);

        var bound = device.Recorder!.OfKind(RecordedCommandKind.BindPipeline).Select(c => c.A).Distinct();

        Assert.Equal(2, bound.Count());
        Assert.Equal(2, device.Recorder.CountOf(RecordedCommandKind.Draw));
    }

    /// <summary>
    ///     The material's set is not bound in a stage that overrode the shader.
    /// </summary>
    /// <remarks>
    ///     A depth-only pipeline's layout has no per-material set in it, so binding one is a
    ///     validation error rather than a wasted call — and the whole point of the override is that
    ///     the prepass reads no material at all.
    /// </remarks>
    [Fact]
    public void The_material_set_is_bound_only_where_the_shader_reads_one() {
        using var h = Build();
        AddMesh(h);
        Frame(h);

        var bind = Assert.Single(device.Recorder!.OfKind(RecordedCommandKind.BindDescriptorSet));

        Assert.Equal((long)DescriptorSetSlot.PerMaterial, bind.A);
        Assert.Equal((long)materialSet.Value.Packed, bind.B);
    }

    /// <summary>
    ///     Every object in a prepass shares a sort group, so the stage sorts purely front to back.
    /// </summary>
    /// <remarks>
    ///     Which is what a prepass wants and what the colour pass does not: with one pipeline for
    ///     everything there is no state to group by, and drawing nearest-first is what makes early-Z
    ///     reject the most.
    /// </remarks>
    [Fact]
    public void Every_object_in_the_prepass_shares_a_sort_group() {
        using var h = Build();

        for (var i = 0; i < 4; i++) {
            AddMesh(h, $"Material{i}", -10f - i);
        }

        Frame(h);

        var prepass = h.System.Objects.All.Length;
        var groups = new HashSet<uint>();
        var colour = new HashSet<uint>();

        for (var i = 0; i < prepass; i++) {
            if (!h.System.Objects.All[i].IsAlive) {
                continue;
            }

            groups.Add(h.Materials.SortGroupOf(h.System, new(i), h.Prepass));
            colour.Add(h.Materials.SortGroupOf(h.System, new(i), h.Opaque));
        }

        Assert.Single(groups);
        Assert.Equal(4, colour.Count);
    }

    /// <summary>The prepass runs first, and the colour pass loads the depth it wrote.</summary>
    /// <remarks>
    ///     Two passes over one depth attachment, which is a dependency the graph derives from the
    ///     attachment rather than from an ordering anybody wrote down. Clearing depth in the second
    ///     pass would throw away everything the first one was for, and nothing but the load action
    ///     says so.
    /// </remarks>
    [Fact]
    public void The_prepass_runs_first_and_the_colour_pass_keeps_its_depth() {
        using var h = Build();
        AddMesh(h);
        Frame(h);

        var stream = device.Recorder!.Commands.ToList();
        var passes = stream.Where(c => c.Kind == RecordedCommandKind.BeginRenderPass).ToList();

        Assert.Equal(2, passes.Count);

        // colour=0 depth=yes for the prepass; colour=1 depth=yes for the pass that follows it.
        Assert.Equal(0, passes[0].A);
        Assert.NotEqual(0, passes[0].B);
        Assert.Equal(1, passes[1].A);
        Assert.NotEqual(0, passes[1].B);
        Assert.Equal(LoadAction.Load, h.Colour.DepthLoad);
    }

    /// <summary>Two shaders over one material is two variants, resolved once each.</summary>
    [Fact]
    public void The_override_costs_one_extra_variant_per_material() {
        using var h = Build();
        AddMesh(h);

        Frame(h);
        var after = h.Materials.VariantCount;

        for (var i = 0; i < 5; i++) {
            Frame(h);
        }

        // The sentinel, the material's own variant, and its depth-only one. Resolution happens per
        // distinct (material, flags, shader), so five more frames add nothing.
        Assert.Equal(3, after);
        Assert.Equal(after, h.Materials.VariantCount);
    }

    // --- The fixture --------------------------------------------------------

    static Effect Compiled(EffectKey key, DescriptorSetLayoutHandle material) =>
        new() {
            Key = key,
            Stages = [
                new(ShaderStage.Vertex, [1, 2, 3, 4], "main"),
                new(ShaderStage.Fragment, [5, 6, 7, 8], "main")
            ],
            // A depth-only variant declares no per-material set, because it reads no material. That
            // is what the renderer keys "should I bind one" off, rather than off the stage — the
            // shader is what knows.
            SetLayouts = key.ShaderName is "DepthOnly"
                ? [default, default, default, default]
                : [default, default, material, default]
        };

    sealed class AlwaysCompiles(DescriptorSetLayoutHandle material) : IEffectProvider {
        public Effect? TryGet(EffectKey key) => Compiled(key, material);
    }

    sealed class Harness : IDisposable {
        public required RenderSystem System { get; init; }
        public required GraphicsCompositor Compositor { get; init; }
        public required RenderGraph Graph { get; init; }
        public required RenderStage Prepass { get; init; }
        public required RenderStage Opaque { get; init; }
        public required RenderPassRenderer Colour { get; init; }
        public required MeshRenderFeature Meshes { get; init; }
        public required MaterialRenderFeature Materials { get; init; }
        public required BufferHandle Vertices { get; init; }

        public RenderObjectId Object { get; set; }

        public void Dispose() {
            Graph.DisposePool();
            System.Dispose();
        }
    }

    ImportedTexture Texture(string name, PixelFormat format, TextureUsage usage) {
        var description = new TextureDescription(format, 320, 180, usage, Name: name);
        var texture = device.CreateTexture(description);
        return new(texture, device.CreateTextureView(texture), description);
    }

    Harness Build() {
        var system = new RenderSystem();

        // Depth written in the prepass and only tested afterwards. The colour stage's DepthStencil is
        // what stops it writing depth a second time for values it already knows.
        var prepass = system.AddStage(new("DepthPrepass") { ShaderName = "DepthOnly" });
        var opaque = system.AddStage(new("Opaque") { DepthStencil = DepthStencilState.TestOnly });

        var meshes = new MeshRenderFeature {
            Pipelines = new(device),
            Describer = new EffectPipelineDescriber(device)
        };

        var materials = new MaterialRenderFeature { Effects = effects };

        meshes.Add(materials);
        system.AddFeature(meshes);

        var view = Matrix4x4.LookAt(Vector3.Zero, new(0f, 0f, -1f), new(0f, 1f, 0f));
        var projection = Matrix4x4.PerspectiveFieldOfView(MathF.PI / 3f, 16f / 9f, 0.1f, 1000f);
        var camera = new RenderView("camera") { Position = Vector3.Zero, Frustum = new(view * projection) };

        var depthPass = new RenderPassRenderer { Name = "Prepass", DepthTarget = "SceneDepth" };
        depthPass.Children.Add(new SingleStageRenderer { View = camera, Stage = prepass });

        var colourPass = new RenderPassRenderer {
            Name = "Forward",
            DepthTarget = "SceneDepth",
            DepthLoad = LoadAction.Load,
            ReadOnlyDepth = true
        };

        colourPass.ColourTargets.Add("SceneColour");
        colourPass.Children.Add(new SingleStageRenderer { View = camera, Stage = opaque });

        var compositor = new GraphicsCompositor(system) {
            FrameSize = new(320, 180),
            Game = new SceneRendererSequence { Children = { depthPass, colourPass } }
        };

        compositor.Imports["SceneColour"] =
            Texture("SceneColour", PixelFormat.Rgba16Float, TextureUsage.ColourTarget | TextureUsage.Sampled);

        compositor.Imports["SceneDepth"] =
            Texture("SceneDepth", PixelFormat.Depth32Float, TextureUsage.DepthStencilTarget | TextureUsage.Sampled);

        return new() {
            System = system,
            Compositor = compositor,
            Graph = new(device),
            Prepass = prepass,
            Opaque = opaque,
            Colour = colourPass,
            Meshes = meshes,
            Materials = materials,
            Vertices = device.CreateBuffer(new() { Size = 1024, Usage = BufferUsage.Vertex })
        };
    }

    void AddMesh(Harness h, string material = "Lit", float z = -10f) {
        var id = h.System.Objects.Add(
            new() {
                Bounds = new(new Vector3(0f, 0f, z), 1f),
                Stages = h.Prepass.Mask | h.Opaque.Mask,
                FeatureIndex = h.Meshes.Index
            }
        );

        h.System.Objects.Data.Data(h.Meshes.Draws)[id.Index] = new() {
            VertexBuffer = h.Vertices, Count = 3, InstanceCount = 1
        };

        h.Materials.Assign(h.System, id, Lit(material));
        h.Object = id;
    }

    Material Lit(string name) => new(name) { Descriptors = materialSet };

    void Frame(Harness h) {
        var list = device.BeginCommandList();

        h.Graph.Reset();
        h.Compositor.Build(h.Graph, effects, device);
        h.Graph.Execute(list);

        list.Finish();
        device.GraphicsQueue.Submit([list]);
    }
}

// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;
using Vixen.Core.Yaml;
using Vixen.Rendering;
using Vixen.Rendering.Compositor;
using Vixen.Rendering.PostFx;
using Vixen.Rendering.Terrain;
using Xunit;

namespace Vixen.Samples.PbrShowcase.Tests;

/// <summary>The showcase's frame document, expanded and built the way the game builds it.</summary>
/// <remarks>
///     <para>
///         Sample 13's <c>FrameDocumentTests</c>, for the seven-knob document: the file is loaded by
///         address inside <c>AppGraphics</c>' constructor, so a bad tag or a node kind nothing
///         registered throws from inside start-up — on a machine with a window and a GPU, which CI
///         is not. This parses the same file and builds it against nothing, so the failure is a
///         test.
///     </para>
///     <para>
///         What this document adds over the template's seven lines is the terrain splice, so what
///         these tests hold is the splice: the <c>!Terrain</c> tag resolves, the expansion puts the
///         ground where opaque ground belongs, and the node lands on the standard frame's own
///         targets without the document naming them.
///     </para>
/// </remarks>
public sealed class FrameDocumentTests {
    static string DocumentPath => Path.Combine(AppContext.BaseDirectory, "Assets", "Frame.vxcompositor");

    [Fact]
    public void The_document_parses_and_builds_with_the_two_factories_the_game_registers() {
        using var built = Build();

        Assert.NotNull(built.Compositor.Game);
        Assert.True(built.Builder.Nodes.ContainsKey("Ground"), "the document lost its 'Ground' node");
    }

    /// <summary>The splice's position: after the Main pass, which is the afterOpaque contract.</summary>
    [Fact]
    public void The_ground_lands_immediately_after_the_main_pass() {
        using var built = Build();

        var children = Assert.IsType<SceneRendererSequence>(built.Compositor.Game).Children;
        var names = children.Select(child => child.Name).ToArray();

        Assert.Equal(Array.IndexOf(names, "Main") + 1, Array.IndexOf(names, "Ground"));
    }

    /// <summary>And the caster splice: directly after the sun, before anything samples the atlas.</summary>
    /// <remarks>
    ///     The terrain factory's transform inserts it wherever a <c>!Terrain</c> node and a
    ///     <c>!ShadowMap</c> share an atlas — which the document's <c>shadows: Cascades</c> line
    ///     and the <c>!Terrain</c> splice together arrange. Position is the contract: after the
    ///     Main pass it would shadow only the ground itself.
    /// </remarks>
    [Fact]
    public void The_terrain_casters_land_between_the_sun_and_the_main_pass() {
        using var built = Build();

        var children = Assert.IsType<SceneRendererSequence>(built.Compositor.Game).Children;
        var names = children.Select(child => child.Name).ToArray();

        var sun = Array.IndexOf(names, "Sun");
        var casters = Array.IndexOf(names, "Ground.Casters");
        var main = Array.IndexOf(names, "Main");

        Assert.True(sun >= 0, "the document's cascades knob lost its sun node");
        Assert.Equal(sun + 1, casters);
        Assert.True(casters < main, "the caster node must build before the pass that samples the atlas");
    }

    /// <summary>The node's defaults bind the standard frame's names — no target authored, none wrong.</summary>
    [Fact]
    public void The_ground_shares_the_frames_colour_and_depth() {
        using var built = Build();

        var ground = Assert.IsType<TerrainSceneRenderer>(built.Builder.Nodes["Ground"]);

        Assert.Equal("SceneHdr", ground.Output);
        Assert.Equal("SceneDepth", ground.Depth);
        Assert.True(ground.Grass, "the grass switch defaults on, and the document does not turn it off");
    }

    /// <summary>The factory's scene list reaches the node, which is what the host wires at startup.</summary>
    [Fact]
    public void The_ground_reads_the_factorys_scene_list() {
        using var built = Build();

        var ground = Assert.IsType<TerrainSceneRenderer>(built.Builder.Nodes["Ground"]);

        Assert.Same(built.Scene, ground.Scene);
    }

    static Built Build() {
        // Constructing the factories is also what first touches their assemblies, whose module
        // initializers register the !StandardFrame and !Terrain YAML tags — parse before that and
        // the tags are unknown. The game's OnConfigure makes the same point about the same lines.
        var effects = new PostEffectFactory();
        var terrain = new TerrainFactory { Scene = new() };
        var asset = YamlSerializer.Parse<GraphicsCompositorAsset>(File.ReadAllText(DocumentPath));

        Assert.Equal(CompositorBuilder.SupportedVersion, asset.Version);

        var system = new RenderSystem();
        var builder = new CompositorBuilder(system);

        builder.Factories.Add(effects);
        builder.Factories.Add(terrain);

        var view = Matrix4x4.LookAt(Vector3.Zero, new(0f, 0f, -1f), new(0f, 1f, 0f));
        var projection = Matrix4x4.PerspectiveFieldOfView(MathF.PI / 3f, 1f, 0.1f, 1000f);

        builder.Views["Camera"] = new("camera") { Position = Vector3.Zero, Frustum = new(view * projection) };

        return new(system, builder, builder.Build(asset), terrain.Scene);
    }

    sealed record Built(
        RenderSystem System,
        CompositorBuilder Builder,
        GraphicsCompositor Compositor,
        TerrainSceneSource Scene
    ) : IDisposable {
        public void Dispose() => System.Dispose();
    }
}

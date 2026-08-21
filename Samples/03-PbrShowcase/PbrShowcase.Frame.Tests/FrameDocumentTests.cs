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

    /// <summary>Every stage the host owes stand-ins to, found by the shader it imposes.</summary>
    /// <remarks>
    ///     <para>
    ///         <c>PbrShowcaseGame.SupplyFrame</c> used to ask the builder for the stage called
    ///         <c>Shadow</c>. That is right only for as long as there is exactly one caster stage, and
    ///         it is what sample 13 was doing when splitting its cascades into a cached half and a
    ///         moving half gave it a second: a stage whose <see cref="RenderStage.Parameters" /> nobody
    ///         fills writes no per-material set at all — a set is written wholly or not at all — so its
    ///         draws go out with set 2 empty, which is a validation error with the layers on and a
    ///         segfault inside <c>vkQueueSubmit</c> without them.
    ///     </para>
    ///     <para>
    ///         So this asserts the two halves of the shape the host now has: matching on the shader
    ///         finds every stage that imposes it, and <b>it finds at least one</b> — a loop that
    ///         matches nothing is the silent failure the name lookup could not have, and the reason
    ///         the count is asserted rather than the loop merely running.
    ///     </para>
    /// </remarks>
    [Fact]
    public void Matching_on_the_shader_finds_every_caster_stage_and_at_least_one() {
        using var built = Build();

        // Spelled out rather than taken from `ShowcaseFrame.CasterShader`: this project deliberately
        // does not reference the game — it reads the authored document and builds it against the Null
        // backend, which is what lets it run on a machine with no GPU. The const and this literal are
        // the same string, and a change to one that misses the other fails here.
        var byShader = built.Builder.Stages.Values
            .Where(stage => string.Equals(stage.ShaderName, "ShadowCaster", StringComparison.Ordinal))
            .Select(stage => stage.Name)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();

        // At least one, because a loop that matches nothing leaves the caster shader's set unwritten
        // just as surely as a stage the host skipped, and reports nothing at all while doing it.
        Assert.NotEmpty(byShader);

        // And the stage the name lookup used to reach is among them, which is what says the const is
        // spelled the way the expansion spells it. `!StandardFrame` emits exactly this one today.
        Assert.Contains("Shadow", byShader);
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

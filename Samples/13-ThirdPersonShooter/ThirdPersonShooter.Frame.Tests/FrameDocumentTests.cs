// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;
using Vixen.Core.Threading;
using Vixen.Core.Yaml;
using Vixen.Core.Yaml.Meta;
using Vixen.Graphics.Null;
using Vixen.Rendering;
using Vixen.Rendering.Compositor;
using Vixen.Rendering.Materials;
using Vixen.Rendering.PostFx;
using Xunit;

namespace Vixen.Samples.ThirdPersonShooter.Tests;

/// <summary>The project's frame document, parsed and built the way the game builds it.</summary>
/// <remarks>
///     <para>
///         <b>Why this exists: a YAML mistake in <c>Frame.vxcompositor</c> used to be a launch.</b>
///         The document is loaded by address inside <c>AppGraphics</c>' constructor, so a bad tag,
///         a renamed stage or a node kind nothing registered threw from inside start-up — on a
///         machine with a window and a GPU, which CI is not. This builds the same document against
///         the Null device on <c>CompositorAssetTests</c>' pattern, so the failure is a test.
///     </para>
///     <para>
///         It builds with <em>empty host slots</em>, deliberately — no visibility group, no store,
///         no fillers — because that is exactly the state of the game's first build, the one that
///         runs before <c>OnInitialise</c> exists to wire anything. A document that only builds
///         once the host is fully wired is a document that crashes every editor that opens it.
///     </para>
/// </remarks>
public sealed class FrameDocumentTests : IDisposable {
    /// <summary>The same registration <c>CompositorImporter</c> makes, for the same reason: the
    ///     document writes colours and vectors as plain scalars — <c>colour: 0.42 0.30 0.20</c> —
    ///     and the generator describes no such shape on its own.</summary>
    static FrameDocumentTests() => MathScalars.Register();

    readonly NullDevice device = new(new() { Record = true });

    /// <summary>The names this document promises, and the game's code reaches for by name.</summary>
    /// <remarks>
    ///     <c>Arena</c> finds the clipmap node to fill its instances, <c>ArenaIllumination.Feed</c>
    ///     finds four more, and the tonemap's <c>Meter.Exposure</c> buffer name is derived from
    ///     <c>Meter</c> — so a rename here is game code silently doing nothing, which is why the
    ///     list is asserted rather than merely enumerated.
    /// </remarks>
    static readonly string[] NamedNodes = [
        "Cull", "Clipmap", "Probes", "Cache", "Sun", "Lamps", "Traversal", "Visibility", "Sky",
        "Main", "Velocity", "Sparks", "Occluders", "SunPages", "Gather", "Mirrors", "Occlusion",
        "Indirect", "ContactOcclusion", "Combine", "Accumulate", "Air", "Defocus", "Shutter",
        "Meter", "Adapt", "Flare", "Glow", "Tonemap", "Edges", "Recover", "Glass", "Edging",
        "Lake", "WaterBehind", "Water"
    ];

    static string DocumentPath => Path.Combine(AppContext.BaseDirectory, "Assets", "Frame.vxcompositor");

    [Fact]
    public void The_document_parses_and_builds_against_a_headless_device() {
        using var built = Build();

        Assert.NotNull(built.Compositor.Game);

        foreach (var name in NamedNodes) {
            Assert.True(built.Builder.Nodes.ContainsKey(name), $"the document lost its '{name}' node");
        }
    }

    /// <summary>The enabled set is the split's: everything that composes is on, and what is off has a reason.</summary>
    /// <remarks>
    ///     <para>
    ///         Still a lock on the honest state, and the state moved twice: the ambient split closed
    ///         the gaps that kept the gather, the occlusion pair and the combine off, and the engine
    ///         publishing <c>ReflectionRenderer</c>'s target into the frame's namespace closed the
    ///         seam that kept the mirrors off — so every node on doc 19's page is on now, and its
    ///         plane reaches the combine's <c>reflections:</c> seat. Whoever changes the document's
    ///         lines changes this list with them, in that order.
    ///     </para>
    ///     <para>
    ///         What stays off stays for stated reasons rather than closed-and-forgotten gaps:
    ///         <c>!IndirectDiffuse</c> because the gather already supplies the combine's screen
    ///         irradiance — running both is the same skylight added twice — and <c>!Outline</c>
    ///         because it is a look this level does not want.
    ///     </para>
    /// </remarks>
    [Fact]
    public void The_enabled_set_is_what_the_split_made_composable() {
        using var built = Build();

        Assert.True(built.Builder.Nodes["Gather"].Enabled, "the split gave the gather its depth decode and its normals producer");
        Assert.True(built.Builder.Nodes["Occlusion"].Enabled, "the combine consumes the occlusion plane now");
        Assert.True(built.Builder.Nodes["ContactOcclusion"].Enabled, "the combine multiplies contact into the same ambient");
        Assert.True(built.Builder.Nodes["Combine"].Enabled, "the combine is where the split frame becomes whole");
        Assert.True(built.Builder.Nodes["Mirrors"].Enabled, "the reflections target enters the frame's namespace now — the last of doc 19's nodes to flip");

        Assert.False(built.Builder.Nodes["Indirect"].Enabled, "the gather already supplies the screen irradiance — both is double-counted skylight");
        Assert.False(built.Builder.Nodes["Edging"].Enabled, "outlines are a look this level does not want");

        Assert.False(built.Builder.Nodes["Traversal"].Enabled, "nothing mounts WorldRenderer.Clusters — the traversal ran for an empty list");

        Assert.False(
            built.Builder.Nodes["Visibility"].Enabled,
            "its colour was overwritten by !Sky and its depth cleared by Main — the re-enable checklist is on the node"
        );
    }

    /// <summary>The Main pass's target order is ForwardPlus.SplitOutputs' contract, member for member.</summary>
    /// <remarks>
    ///     Location 0 is direct light, 1 is albedo with occlusion in alpha, 2 is world normal with
    ///     roughness in alpha, 3 is the surface's f0 — the shader dictates the order and the
    ///     document can only repeat it, and the f0 plane is appended for exactly that reason,
    ///     so a reorder here is albedo shaded as radiance with every counter reporting success.
    ///     Asserted with the visibility resolve's split knobs, because the two paths writing the
    ///     same planes on the same terms is the whole reason the knobs exist — and with the
    ///     combine's seats, because the planes the frame produces and the names the combine reads
    ///     are one contract seen from both ends, the mirrors' plane now among them.
    /// </remarks>
    [Fact]
    public void The_main_pass_writes_the_split_targets_in_the_shaders_order() {
        using var built = Build();

        var main = Assert.IsType<RenderPassRenderer>(built.Builder.Nodes["Main"]);

        Assert.Equal(["SceneHdr", "SceneAlbedo", "SceneNormals", "SceneSpecular"], main.ColourTargets);

        var visibility = Assert.IsType<VisibilityBufferRenderer>(built.Builder.Nodes["Visibility"]);

        Assert.Equal("SceneAlbedo", visibility.Albedo);
        Assert.Equal("SceneNormals", visibility.Normals);
        Assert.Equal("SceneSpecular", visibility.Specular);

        // The consuming end of the same contract: each seat names exactly the plane a node above
        // publishes, the reflections target included now that the renderer can publish one.
        var combine = Assert.IsType<AmbientCombineRenderer>(built.Builder.Nodes["Combine"]);

        Assert.Equal("SceneHdr", combine.Direct);
        Assert.Equal("SceneAlbedo", combine.Albedo);
        Assert.Equal("SceneNormals", combine.Normals);
        Assert.Equal("Reflections", combine.Reflections);

        // ⚠ Named together with the line above it or neither moves: the combine adds the traced
        // plane weighted by this one's f0, and the shading pass only holds its own specular ambient
        // back when both are there. Half the pair is the frame this document had before.
        Assert.Equal("SceneSpecular", combine.Specular);

        // ⚠ Both AO planes run at half resolution and this node is the only full-resolution reader
        // either of them has, so the depth and the camera are what make the difference between an
        // upsample that respects the depth edge at a corner and a linear tap that smears occlusion
        // across it. A key the document names and nothing binds is silently the second one.
        Assert.Equal("SceneDepth", combine.Depth);
        Assert.NotNull(combine.View);

        Assert.Equal(
            "Reflections",
            Assert.IsType<ReflectionRenderer>(built.Builder.Nodes["Mirrors"]).Target
        );
    }

    /// <summary>Two nodes march the nearest chain, so its ring must be sized for both.</summary>
    /// <remarks>
    ///     The gather's screen traces and the reflections kernel both skip by the host's one
    ///     nearest-reduced pyramid, and each rebuilds it once a frame — two descriptor rewrites
    ///     before a single submission, which is exactly what <c>TakeTracePyramid</c> counts takers
    ///     for. A ring left at one is a rewrite beneath a frame in flight, and nothing about the
    ///     picture would say so. Both rebuilds are real now that the mirrors are enabled; the
    ///     count was two even while they were off, because the factory takes the chain for the
    ///     node it builds, not for the frames it happens to record.
    /// </remarks>
    [Fact]
    public void Both_marching_nodes_deepen_the_trace_chains_ring() {
        using var chain = new HiZPyramid(device) { Reduction = HiZReduction.Nearest };

        using var built = Build(builder => builder.TracePyramid = chain);

        Assert.Equal(2, chain.BuildsPerFrame);
    }

    /// <summary>Both marches measure their shell in metres, and in the same metres.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>Unset is not neutral here — it is the hit test collapsing.</b> Left alone, both
    ///         nodes keep the device-depth shell of 0.02, and under this camera's reverse-Z 0.1/1000
    ///         planes that shell reaches past the far plane for every surface beyond 4.98 m. The
    ///         frame's median surface sits at five to eight metres, so over most of the picture
    ///         "is this sample inside that surface" becomes "is this sample behind it at all", and a
    ///         ray thirty metres past a wall reports a hit on the wall. Nothing about the frame says
    ///         so: the colour is right and the place is wrong.
    ///     </para>
    ///     <para>
    ///         One metre is <c>Arena.vxscene</c>'s number, not a tuned one — the floor slab, the four
    ///         walls and the four pillars are all authored at <c>halfExtents</c> 0.5 on their thin
    ///         axis, so it is the thickness of every solid that bounds this room. The two nodes are
    ///         asserted together because they march one chain over one depth buffer, and a shell that
    ///         differs between them is the same ray stopped in two places.
    ///     </para>
    /// </remarks>
    [Fact]
    public void Both_marches_measure_their_shell_in_the_levels_own_metres() {
        using var built = Build();

        var gather = Assert.IsType<ScreenProbeGatherRenderer>(built.Builder.Nodes["Gather"]);
        var mirrors = Assert.IsType<ReflectionRenderer>(built.Builder.Nodes["Mirrors"]);

        Assert.Equal(1f, gather.ScreenLinearThickness);
        Assert.Equal(mirrors.ScreenLinearThickness, gather.ScreenLinearThickness);
    }

    /// <summary>The culling node adopts the host's group, which is the handover the game relies on.</summary>
    [Fact]
    public void The_document_turns_the_hosts_culling_group_on() {
        using var visibility = new GpuVisibilityGroup(device);
        using var pyramid = new HiZPyramid(device);

        using var built = Build(
            builder => {
                builder.Visibility = visibility;
                builder.Occluders = pyramid;
            }
        );

        Assert.Same(visibility, built.System.Visibility);
        Assert.Same(pyramid, visibility.Occluders);
        Assert.Same(pyramid, Assert.IsType<HiZRenderer>(built.Builder.Nodes["Occluders"]).Pyramid);
    }

    /// <summary>The virtual shadow node adopts the host's atlas, and publishes under the one prefix
    ///     the shading pass declares.</summary>
    /// <remarks>
    ///     <para>
    ///         The handover half is the culling test's above: the atlas is host-owned because a
    ///         shadow page cache is the one thing in the frame whose whole point is outliving it,
    ///         and a node that captured null does nothing while every counter says the frame drew.
    ///     </para>
    ///     <para>
    ///         The prefix half is the seam this feature actually hangs by: a composed slot's bindings
    ///         are named for what fills it, so the node has to publish under
    ///         <c>ForwardPlus.VirtualShadowLookup</c> — <c>Arena.Paint</c> composes that shader
    ///         behind <c>ForwardPlus.directionalShadow</c> — and a bare pass name here is a map
    ///         rendered, uploaded and read by nobody, with the cascades quietly covering it.
    ///     </para>
    /// </remarks>
    [Fact]
    public void The_document_turns_the_hosts_virtual_shadow_atlas_on() {
        using var atlas = new VirtualShadowAtlas(device);

        using var built = Build(builder => builder.VirtualShadows = atlas);

        var node = Assert.IsType<VirtualShadowRenderer>(built.Builder.Nodes["SunPages"]);

        Assert.Same(atlas, node.Atlas);
        Assert.Equal("SceneDepth", node.Depth);
        Assert.Equal(["ForwardPlus.VirtualShadowLookup"], node.Passes);

        // The A/B: the virtual map shades the sun where its pages are drawn, and the cascades stay
        // to cover everywhere it has nothing — deleting the !ShadowMap node is owed the per-page
        // cluster cut, not this increment.
        Assert.True(built.Builder.Nodes["Sun"].Enabled, "the cascades are the map's fall-through, not its casualty");
    }

    /// <summary>The ground is spliced after the pass that draws the level and before the one that
    ///     writes its velocity — the seat a <c>!StandardFrame</c>'s <c>afterOpaque</c> would use.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>Position, not presence.</b> A <c>!Terrain</c> node anywhere in the sequence
    ///         builds, draws and reports every counter as a success — and put before <c>Main</c> it
    ///         is 252 m of heightfield the opaque pass then clears the depth of, and put after
    ///         <c>Occluders</c> it is ground the next frame's cull cannot see. Both are frames that
    ///         render. So the assertion is the two neighbours rather than the node's existence.
    ///     </para>
    ///     <para>
    ///         It shares <c>Main</c>'s depth by naming it, which is also what makes the order load
    ///         bearing: the arena's own floor and walls are the near occluders that should already
    ///         be in that buffer when the ground behind them is rasterised.
    ///     </para>
    /// </remarks>
    [Fact]
    public void The_ground_is_spliced_after_the_opaque_pass_and_before_the_velocity_one() {
        using var built = Build();

        var order = built.Compositor.Game is SceneRendererSequence sequence
            ? sequence.Children.Select(child => child.ToString()).ToList()
            : [];

        var main = order.IndexOf("Main");
        var ground = order.IndexOf("Ground");
        var velocity = order.IndexOf("Velocity");

        Assert.True(main >= 0 && ground >= 0 && velocity >= 0, $"a node went missing: {string.Join(", ", order)}");
        Assert.True(main < ground, "the ground draws before the pass that clears the depth it shares");
        Assert.True(ground < velocity, "the ground draws after the velocity pass it should be in front of");
    }

    /// <summary>The node takes this document's own target names rather than its defaults.</summary>
    /// <remarks>
    ///     Every one of these happens to equal <c>TerrainNodeAsset</c>'s default, which is exactly
    ///     why it is worth pinning: a rename in the resources block at the top of the document would
    ///     leave the node reading a name nothing produces, and the failure would be at build rather
    ///     than here.
    /// </remarks>
    [Fact]
    public void The_ground_writes_the_frames_own_split_targets() {
        using var built = Build();

        var node = Assert.IsType<Vixen.Rendering.Terrain.TerrainSceneRenderer>(built.Builder.Nodes["Ground"]);

        Assert.Equal("SceneHdr", node.Output);
        Assert.Equal("SceneDepth", node.Depth);
        Assert.Equal("SceneAlbedo", node.Albedo);
        Assert.Equal("SceneNormals", node.Normals);
        Assert.Equal("SceneSpecular", node.Specular);
        Assert.Equal("ShadowAtlas", node.ShadowAtlas);
    }

    /// <summary>Every arena material binds its base-colour map, and binds it under the one name the
    ///     host pairs.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b><c>baseColorMap</c> is not a name a material may choose.</b>
    ///         <c>WorldRenderer.Paired</c> keys its single <c>TextureIndices</c> entry off
    ///         <c>new TexturedMetalRoughnessFeature().BaseColorMap</c> — the feature's *default* — so
    ///         a material that renamed its map resolves nothing, takes slot zero and samples the
    ///         table's fallback. Nothing refuses it; the wall just draws in somebody else's texture.
    ///     </para>
    ///     <para>
    ///         The <c>textures:</c> entry and the feature's name are asserted together because
    ///         either alone is silent: a feature naming a map with no entry samples the fallback, and
    ///         an entry naming a parameter no feature declares is a dependency the material carries
    ///         and never reads.
    ///     </para>
    /// </remarks>
    [Theory]
    [InlineData("wall")]
    [InlineData("pillar")]
    [InlineData("floor")]
    [InlineData("crate")]
    [InlineData("ramp")]
    public void An_arena_material_binds_its_base_colour_map(string material) {
        var path = Path.Combine(AppContext.BaseDirectory, "Assets", "Materials", $"{material}.vxmat");
        var content = YamlSerializer.Parse<MaterialContent>(File.ReadAllText(path));

        var textured = Assert.Single(content.Features.OfType<TexturedMetalRoughnessFeature>());
        var normal = Assert.Single(content.Features.OfType<TexturedNormalMapFeature>());
        var orm = Assert.Single(content.Features.OfType<TexturedOrmFeature>());

        // ⚠ Three features and three names, none of which the material may choose: `Paired` keys one
        // TextureIndices entry per feature off that feature's *default* map name, so a material that
        // renamed any of them resolves nothing for it and takes slot zero.
        Assert.Equal("baseColorMap", textured.BaseColorMap);
        Assert.Equal("normalMap", normal.NormalMap);
        Assert.Equal("ormMap", orm.OrmMap);

        // ⚠ And the base feature at metalness zero wherever an ORM map supplies one, which is the
        // constraint TexturedOrmSurface cannot check for itself: it reads `diffuseColor` back as the
        // albedo, and that is only the albedo when the feature before it left the split alone.
        Assert.Equal(0f, textured.Metalness);

        foreach (var name in new[] { textured.BaseColorMap, normal.NormalMap, orm.OrmMap }) {
            var binding = Assert.Single(content.Textures, entry => entry.Parameter == name);

            // A reference that does not parse is answered with nothing by AssetTerrainTextures and by
            // AssetTextureSource alike — both draw the fallback, which over generated noise is a
            // texture that looks like it loaded.
            Assert.False(binding.Texture.IsNull, $"{material}'s {name} is not a reference");
        }

        // No entry naming a parameter no feature declares: that is a dependency the material carries,
        // the bundle ships and the pool makes resident, and nothing ever samples.
        Assert.Equal(3, content.Textures.Length);
    }

    /// <summary>The committed heightfield's three layers each name an albedo that parses.</summary>
    /// <remarks>
    ///     The other half of the material test, on the other path into the same
    ///     <c>AssetTextureSource</c>: <c>AssetTerrainTextures.Resolve</c> counts an unparsed
    ///     reference and answers nothing, so a typo here is a layer drawn in the renderer's white
    ///     default rather than a refusal. It also pins the absence — no layer names a normal map,
    ///     because <c>TerrainRenderer</c> resolves <c>Albedo</c> and <c>Surface</c> and nothing else.
    /// </remarks>
    [Fact]
    public void Every_terrain_layer_names_an_albedo_and_no_normal_map() {
        var path = Path.Combine(AppContext.BaseDirectory, "Assets", "Terrain", "Outskirts.vxterrain");
        var terrain = Vixen.Terrain.TerrainStore.Read(File.ReadAllBytes(path));

        Assert.Equal(3, terrain.Weights.LayerCount);

        for (var index = 0; index < terrain.Weights.LayerCount; index++) {
            var layer = terrain.Weights.LayerOf(index);

            Assert.True(
                Vixen.Core.AssetReference.TryParse(layer.Albedo, out var albedo) && !albedo.IsNull,
                $"layer '{layer.Name}' names '{layer.Albedo}', which is not an asset reference"
            );

            Assert.True(
                Vixen.Core.AssetReference.TryParse(layer.Surface, out var surface) && !surface.IsNull,
                $"layer '{layer.Name}' names '{layer.Surface}' as its surface map"
            );

            Assert.True(layer.Normal.Length == 0, $"layer '{layer.Name}' names a normal map nothing binds");
        }
    }

    /// <summary>The water's three nodes are in the order doc 35 § D8 requires, and the copy is between them.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>The ordering is the whole of § B1, and getting it wrong is undefined behaviour
    ///         rather than a wrong picture.</b> <c>!Water</c> samples what is behind the surface and
    ///         writes the finished pixel into the same target the frame was already in; reading a
    ///         target a pass is also writing is undefined on a driver, so a <c>!Copy</c> has to sit
    ///         between the lit frame and the composite. A document that dropped the copy would render
    ///         on one machine and not another.
    ///     </para>
    ///     <para>
    ///         And <c>Lake</c> — the surface mesh — is before the copy rather than after it, because
    ///         the copy is of the frame the water is composited <em>over</em>. It is also after
    ///         <c>Ground</c>, because it rasterises against a depth that pass writes: its depth state
    ///         is Greater with no write and it loads rather than clears, so against a depth buffer
    ///         that is not yet real every fragment fails silently.
    ///     </para>
    /// </remarks>
    [Fact]
    public void The_water_composites_after_a_copy_of_the_frame_it_is_drawn_over() {
        using var built = Build();

        var order = built.Compositor.Game is SceneRendererSequence sequence
            ? sequence.Children.Select(child => child.ToString()).ToList()
            : [];

        var ground = order.IndexOf("Ground");
        var surface = order.IndexOf("Lake");
        var combine = order.IndexOf("Combine");
        var copy = order.IndexOf("WaterBehind");
        var water = order.IndexOf("Water");
        var accumulate = order.IndexOf("Accumulate");

        Assert.True(
            ground >= 0 && surface >= 0 && combine >= 0 && copy >= 0 && water >= 0 && accumulate >= 0,
            $"a water node went missing: {string.Join(", ", order)}"
        );

        Assert.True(ground < surface, "the surface rasterises against a depth the ground has not written yet");
        Assert.True(surface < copy, "the copy is taken before the surface the water is composited over exists");
        Assert.True(combine < copy, "the copy is of a frame the ambient has not been added to yet");
        Assert.True(copy < water, "the water reads the target it writes, which is undefined rather than wrong");
        Assert.True(water < accumulate, "the accumulator resolves a frame with no water in it");
    }

    /// <summary>The composite's two ends are the copy's two ends, and neither is the other.</summary>
    /// <remarks>
    ///     ⚠ <b><c>Behind</c> equal to <c>Output</c> is refused by the node itself</b> — see
    ///     <c>WaterRenderer.Build</c> — so what this catches is the subtler pair: a composite reading
    ///     a copy of something else, or a mesh writing a plane the composite does not read. Both build
    ///     cleanly and give a lake composited over a frame from somewhere else, or no lake at all.
    /// </remarks>
    [Fact]
    public void The_composite_reads_the_planes_the_mesh_writes_and_a_copy_of_what_it_writes() {
        using var built = Build();

        var water = Assert.IsType<Vixen.Rendering.Water.WaterRenderer>(built.Builder.Nodes["Water"]);
        var surface = Assert.IsType<Vixen.Rendering.Water.WaterMeshRenderer>(built.Builder.Nodes["Lake"]);

        Assert.NotEqual(water.Output, water.Behind);

        // The two planes the mesh writes are the two the composite reads. Both happen to equal the
        // node defaults, and that is exactly why they are worth pinning: a rename in the resources
        // block would leave one node writing a name the other does not read, and the frame would build.
        Assert.Equal(surface.Surface, water.Surface);
        Assert.Equal(surface.Normal, water.Normal);
        Assert.Equal("SceneDepth", surface.SceneDepth);
        Assert.Equal("SceneDepth", water.SceneDepth);
        Assert.Equal("SceneCombined", water.Output);
        Assert.Equal("SceneCombinedCopy", water.Behind);
    }

    /// <summary>The surface the document draws is the surface the terrain seed dug a bed for.</summary>
    /// <remarks>
    ///     ⚠ <b>Depth is <em>surface minus ground</em> and doc 35 § D3 stores neither</b>, so the two
    ///     numbers live in two files — the bed in <c>TerrainSeed</c>'s committed heightfield, the
    ///     surface in <c>Arena.vxscene</c> — and nothing in the build compares them. A surface typed
    ///     below the bed is a lake that is simply dry, with no error anywhere; one typed well above it
    ///     is a lake with no shoreline, spilling across the shelf. This is the comparison, and
    ///     <c>restHeight</c> on the mesh node is a third copy of the same number.
    /// </remarks>
    [Fact]
    public void The_lake_in_the_scene_sits_at_the_height_the_frame_and_the_seed_both_say() {
        using var built = Build();

        var surface = Assert.IsType<Vixen.Rendering.Water.WaterMeshRenderer>(built.Builder.Nodes["Lake"]);
        var scene = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Assets", "Scenes", "Arena.vxscene"));

        Assert.Contains("surfaceHeight: 1.2", scene, StringComparison.Ordinal);
        Assert.Equal(1.2f, surface.Settings.RestHeight, 3);

        // And the ring, whose radius the seed's bowl has to be wider than — see TerrainSeed.LakeRadius
        // for why coverage must run out before depth does.
        var ring = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Assets", "Water", "Lake.vxspline"));

        Assert.Contains("isClosed: true", ring, StringComparison.Ordinal);
        Assert.Contains("position: 0.0000 0 20.0000", ring, StringComparison.Ordinal);
    }

    /// <summary>Every gate is the same hole in the mesh and in the collider.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>The two halves of a wall segment are authored independently and nothing in the
    ///         build checks that they agree.</b> A <c>scale</c> on the entity decides what you can
    ///         see and a <c>!BoxCollision</c> beside it decides what you can walk through, and a hole
    ///         you can see and not walk through — or walk through and not see — is worse than no hole.
    ///     </para>
    ///     <para>
    ///         <c>arena-wall.obj</c> is 64 × 6 × 1 with its base at y = 0, so a segment scaled by
    ///         <c>s</c> on x is 64<c>s</c> metres long and its half-extent must be 32<c>s</c>. Eight
    ///         segments carrying a scale is four eight-metre gates.
    ///     </para>
    /// </remarks>
    [Fact]
    public void Every_gate_is_the_same_hole_in_the_mesh_and_in_the_collider() {
        var scene = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Assets", "Scenes", "Arena.vxscene"));
        var lines = scene.Split('\n');
        var segments = 0;

        for (var index = 0; index < lines.Length; index++) {
            if (!lines[index].TrimStart().StartsWith("- name: Wall", StringComparison.Ordinal)) {
                continue;
            }

            var scale = 0f;
            var half = 0f;

            for (var scan = index + 1;
                scan < lines.Length && !lines[scan].TrimStart().StartsWith("- name:", StringComparison.Ordinal);
                scan++) {
                var line = lines[scan].Trim();

                if (line.StartsWith("scale:", StringComparison.Ordinal)) {
                    scale = Number(line["scale:".Length..]);
                }

                if (line.StartsWith("- !BoxCollision", StringComparison.Ordinal)) {
                    var at = line.IndexOf("halfExtents:", StringComparison.Ordinal);

                    half = Number(line[(at + "halfExtents:".Length)..]);
                }
            }

            // The houses' walls are Wall-named too and are built from the crate cube; the perimeter is
            // the set that carries a scale at all.
            if (scale <= 0f) {
                continue;
            }

            Assert.Equal(32f * scale, half, 3);
            segments++;
        }

        Assert.Equal(8, segments);
    }

    /// <summary>The first number on a YAML scalar line, in the invariant culture.</summary>
    static float Number(string text) =>
        float.Parse(
            text.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries)[0],
            System.Globalization.CultureInfo.InvariantCulture
        );

    /// <summary>
    ///     This document's clipmap node is built against whatever job system the host has.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         <b>The sample end of the chain the background tier waited on.</b> The composite this
    ///         node does is the most expensive thing in the frame and about 97 per cent stale by
    ///         design, and given a scheduler it goes into <c>JobPriority.Background</c> with the handle
    ///         kept — so a walking camera draws a clipmap one refresh old rather than stopping for one.
    ///         Without a scheduler the node still works and the frame still waits, which is what a
    ///         tool or a test that builds this document gets.
    ///     </para>
    ///     <para>
    ///         ⚠ Asserted here as well as on the builder, because <i>this</i> document is what the
    ///         sample ships and a node kind it did not place is a node nothing hands anything to. The
    ///         engine-side test proves the builder forwards it; this proves there is something in this
    ///         file to forward it to.
    ///     </para>
    /// </remarks>
    [Fact]
    public void The_documents_clipmap_takes_the_hosts_job_system() {
        using var jobs = new JobScheduler(0);
        using var built = Build(builder => builder.Jobs = jobs);

        var clipmap = Assert.Single(built.Builder.Nodes.Values.OfType<GlobalDistanceFieldRenderer>());

        Assert.Same(jobs, clipmap.Jobs);
    }

    static Built Build(Action<CompositorBuilder>? wire = null) {
        // Constructing the factory is also what first touches Vixen.Rendering.PostFx, whose module
        // initializer registers the !Bloom-family YAML tags — parse before that and the tags are
        // unknown. The game's OnConfigure makes the same point about the same line.
        var factory = new PostEffectFactory();

        // And the ground's, on exactly the same terms: without this the document's !Terrain tag is
        // a name nothing in the build claims, which throws from inside Build below.
        var terrain = new Vixen.Rendering.Terrain.TerrainFactory();

        // And the lake's, for the same reason again — and deliberately with no Zones, because that is
        // the state the game's own first build is in: AppGraphics hands the factory its zone system
        // after this point, and a !WaterSurface node with none draws nothing rather than throwing.
        var water = new Vixen.Rendering.Water.WaterRendererFactory();
        var asset = YamlSerializer.Parse<GraphicsCompositorAsset>(File.ReadAllText(DocumentPath));

        Assert.Equal(CompositorBuilder.SupportedVersion, asset.Version);

        var system = new RenderSystem();

        var view = Matrix4x4.LookAt(Vector3.Zero, new(0f, 0f, -1f), new(0f, 1f, 0f));
        var projection = Matrix4x4.PerspectiveFieldOfView(MathF.PI / 3f, 1f, 0.1f, 1000f);

        var builder = new CompositorBuilder(system);

        builder.Factories.Add(factory);
        builder.Factories.Add(terrain);
        builder.Factories.Add(water);
        builder.Views["Camera"] = new("camera") { Position = Vector3.Zero, Frustum = new(view * projection) };

        wire?.Invoke(builder);

        return new(system, builder, builder.Build(asset));
    }

    /// <summary>One build of the document, and the three things that have to be given back.</summary>
    /// <remarks>
    ///     ⚠ <b>The compositor before the render system.</b> A compositor owns the nodes the builder
    ///     made for it — a cached shadow atlas is device memory a frame cannot lend, so the node holds
    ///     it — and this document has two such nodes. The render system last, because a feature's
    ///     tear-down gives table slots back.
    /// </remarks>
    sealed record Built(RenderSystem System, CompositorBuilder Builder, GraphicsCompositor Compositor) : IDisposable {
        public void Dispose() {
            Compositor.Dispose();
            System.Dispose();
        }
    }

    /// <inheritdoc />
    public void Dispose() {
        device.Dispose();
        GC.SuppressFinalize(this);
    }
}

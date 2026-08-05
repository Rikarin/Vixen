// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;
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
        "Meter", "Adapt", "Flare", "Glow", "Tonemap", "Edges", "Recover", "Glass", "Edging"
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
    ///     roughness in alpha — the shader dictates the order and the document can only repeat it,
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

        Assert.Equal(["SceneHdr", "SceneAlbedo", "SceneNormals"], main.ColourTargets);

        var visibility = Assert.IsType<VisibilityBufferRenderer>(built.Builder.Nodes["Visibility"]);

        Assert.Equal("SceneAlbedo", visibility.Albedo);
        Assert.Equal("SceneNormals", visibility.Normals);

        // The consuming end of the same contract: each seat names exactly the plane a node above
        // publishes, the reflections target included now that the renderer can publish one.
        var combine = Assert.IsType<AmbientCombineRenderer>(built.Builder.Nodes["Combine"]);

        Assert.Equal("SceneHdr", combine.Direct);
        Assert.Equal("SceneAlbedo", combine.Albedo);
        Assert.Equal("SceneNormals", combine.Normals);
        Assert.Equal("Reflections", combine.Reflections);

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

    static Built Build(Action<CompositorBuilder>? wire = null) {
        // Constructing the factory is also what first touches Vixen.Rendering.PostFx, whose module
        // initializer registers the !Bloom-family YAML tags — parse before that and the tags are
        // unknown. The game's OnConfigure makes the same point about the same line.
        var factory = new PostEffectFactory();

        // And the ground's, on exactly the same terms: without this the document's !Terrain tag is
        // a name nothing in the build claims, which throws from inside Build below.
        var terrain = new Vixen.Rendering.Terrain.TerrainFactory();
        var asset = YamlSerializer.Parse<GraphicsCompositorAsset>(File.ReadAllText(DocumentPath));

        Assert.Equal(CompositorBuilder.SupportedVersion, asset.Version);

        var system = new RenderSystem();

        var view = Matrix4x4.LookAt(Vector3.Zero, new(0f, 0f, -1f), new(0f, 1f, 0f));
        var projection = Matrix4x4.PerspectiveFieldOfView(MathF.PI / 3f, 1f, 0.1f, 1000f);

        var builder = new CompositorBuilder(system);

        builder.Factories.Add(factory);
        builder.Factories.Add(terrain);
        builder.Views["Camera"] = new("camera") { Position = Vector3.Zero, Frustum = new(view * projection) };

        wire?.Invoke(builder);

        return new(system, builder, builder.Build(asset));
    }

    sealed record Built(RenderSystem System, CompositorBuilder Builder, GraphicsCompositor Compositor) : IDisposable {
        public void Dispose() => System.Dispose();
    }

    /// <inheritdoc />
    public void Dispose() {
        device.Dispose();
        GC.SuppressFinalize(this);
    }
}

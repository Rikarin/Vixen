// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;
using Vixen.Graphics;
using Vixen.Graphics.Null;
using Vixen.Graphics.RenderGraph;
using Vixen.Rendering;
using Vixen.Rendering.Compositor;
using Vixen.Rendering.Lighting;
using Vixen.Rendering.Water;
using Vixen.Shaders;
using Xunit;

namespace Tests;

/// <summary>
///     What the water pass declares, and what it refuses — [docs/plan/35 § D8].
/// </summary>
/// <remarks>
///     The look is a golden image's to check and the arithmetic is the seam test's. What is here is
///     the third thing, and the one that is silent when it is wrong: whether the pass is <em>in the
///     graph</em> reading what it says it reads, so that it is ordered behind the surface pass and the
///     copy rather than wherever the host happened to build it.
/// </remarks>
public sealed class WaterPassTests : IDisposable {
    readonly NullDevice device = new(new() { Record = true });
    readonly EffectSystem effects = new();
    readonly RenderSystem system = new();
    readonly RenderGraph graph;
    readonly SamplerCache samplers;
    readonly DescriptorAllocator descriptors;

    public WaterPassTests() {
        graph = new(device);
        samplers = new(device);
        descriptors = new(device);
    }

    /// <inheritdoc />
    public void Dispose() {
        graph.DisposePool();
        descriptors.Dispose();
        samplers.Dispose();
        system.Dispose();
        device.Dispose();
        GC.SuppressFinalize(this);
    }

    // --- The fixture --------------------------------------------------------

    const int Size = 16;

    static RenderResourceAsset Declared(string name, TextureUsage usage = TextureUsage.ColourTarget | TextureUsage.Sampled) =>
        new() { Name = name, Format = PixelFormat.Rgba16Float, Usage = usage };

    WaterRenderer Node(string behind = "SceneColourCopy", string reflections = "") =>
        new() {
            Name = "Water",
            Output = "SceneColour",
            Behind = behind,
            SceneDepth = "SceneDepth",
            Surface = "WaterSurface",
            Normal = "WaterNormal",
            Reflections = reflections,
            Samplers = samplers,
            Allocator = descriptors,
            Device = device
        };

    GraphicsCompositor Compositor(SceneRenderer node) {
        var sequence = new SceneRendererSequence { Name = "Frame" };

        sequence.Children.Add(Writer("SceneColourCopy"));
        sequence.Children.Add(Writer("SceneDepth"));
        sequence.Children.Add(Writer("WaterSurface"));
        sequence.Children.Add(Writer("WaterNormal"));
        sequence.Children.Add(Writer("Reflections"));
        sequence.Children.Add(node);

        var compositor = new GraphicsCompositor(system) { FrameSize = new(Size, Size), Game = sequence };

        foreach (var name in (string[])["SceneColour", "SceneColourCopy", "SceneDepth", "WaterSurface", "WaterNormal", "Reflections"]) {
            compositor.Resources.Add(Declared(name));
        }

        return compositor;
    }

    /// <summary>A node that writes a plane, so the water pass has something to be ordered behind.</summary>
    static DelegateSceneRenderer Writer(string target) =>
        new() {
            Name = "Writer " + target,
            OnBuild = (_, frame) => {
                var texture = frame.Texture("Writer", target);

                frame.Graph.AddPass(
                    "Writer " + target,
                    pass => {
                        pass.ColourAttachment(texture);
                        pass.Execute(context => context.CommandList.Draw(3));
                    }
                );
            }
        };

    CompositorFrame Build(GraphicsCompositor compositor) {
        graph.Reset();
        return compositor.Build(graph, effects, device);
    }

    // --- What it declares ---------------------------------------------------

    /// <summary>The pass reads every plane it says it reads, which is what orders it.</summary>
    /// <remarks>
    ///     ⚠ The binding is what the shader reads a plane <em>through</em>; the declared read is what
    ///     orders this pass after whatever wrote it and keeps that producer from being culled. One
    ///     without the other is a validation error or a race, so both are asserted together.
    /// </remarks>
    [Fact]
    public void The_pass_declares_every_plane_it_binds() {
        using var water = Node(reflections: "Reflections");

        Build(Compositor(water));

        Assert.Equal(1, water.BuildCount);

        Assert.Contains("SceneColourCopy", water.Pass.Reads);
        Assert.Contains("SceneDepth", water.Pass.Reads);
        Assert.Contains("WaterSurface", water.Pass.Reads);
        Assert.Contains("WaterNormal", water.Pass.Reads);
        Assert.Contains("Reflections", water.Pass.Reads);
        Assert.Contains("SceneColour", water.Pass.ColourTargets);

        // Five sampled planes and two samplers, each on the binding the generated keys say.
        Assert.Equal(5, water.Pass.Descriptors.Bindings.Count(b => b.Kind == DescriptorKind.SampledTexture));
        Assert.Equal(2, water.Pass.Descriptors.Bindings.Count(b => b.Kind == DescriptorKind.Sampler));
    }

    /// <summary>
    ///     ⚠ The reflection plane is bound even when the permutation is off.
    /// </summary>
    /// <remarks>
    ///     A descriptor set is written wholly or not at all — the rule <c>!AmbientCombine</c>'s
    ///     stand-in planes follow. What switches the term off is the permutation; what would happen
    ///     without a binding is a descriptor the driver refuses, which is a frame that does not render
    ///     rather than a frame with no reflections.
    /// </remarks>
    [Fact]
    public void A_document_with_no_reflection_plane_still_binds_one() {
        using var water = Node();

        Build(Compositor(water));

        Assert.Equal(5, water.Pass.Descriptors.Bindings.Count(b => b.Kind == DescriptorKind.SampledTexture));
        Assert.DoesNotContain("Reflections", water.Pass.Reads);
    }

    /// <summary>
    ///     ⚠ Reading the target it writes is refused, by name, at build time.
    /// </summary>
    /// <remarks>
    ///     [docs/plan/35 § B1] calls the copy the blocker rather than the pass: sampling a target a
    ///     pass is also writing is <em>undefined</em>, which means it renders on one driver and not
    ///     another. A document that made the mistake would otherwise ship.
    /// </remarks>
    [Fact]
    public void Reading_the_target_it_writes_is_refused() {
        using var water = Node(behind: "SceneColour");

        var thrown = Assert.Throws<CompositorBindingException>(() => Build(Compositor(water)));

        Assert.Contains("!Copy", thrown.Message, StringComparison.Ordinal);
        Assert.Contains("undefined", thrown.Message, StringComparison.Ordinal);
    }

    /// <summary>A host that has not wired the renderer up gets a frame with no water.</summary>
    /// <remarks>
    ///     The terms <c>!ScreenProbeGather</c> and <c>!SurfaceCache</c> are built on: a node built
    ///     with nothing supplied does nothing rather than throwing, so one authored document serves a
    ///     project that has no water renderer.
    /// </remarks>
    [Fact]
    public void A_node_with_no_device_does_nothing_rather_than_throwing() {
        using var water = new WaterRenderer {
            Name = "Water",
            Output = "SceneColour",
            Behind = "SceneColourCopy",
            SceneDepth = "SceneDepth",
            Surface = "WaterSurface",
            Normal = "WaterNormal"
        };

        Build(Compositor(water));

        Assert.Equal(0, water.BuildCount);
        Assert.Empty(water.Pass.Reads);
    }

    // --- § D8's tile classification -----------------------------------------

    /// <summary>
    ///     ⚠ Tiled, the draw is two triangles an instance over the tiles, into a target it loads.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         <b>Three properties that fail in three different ways, so they are asserted together.</b>
    ///         Three vertices instead of six is half of every tile missing — a checkerboard of water.
    ///         One instance instead of one per tile is the whole pass collapsed into the top-left tile.
    ///         And <see cref="LoadAction.DontCare" /> on a draw that covers part of the screen is the
    ///         pixels no instance covered holding whatever the allocator handed over, which on most
    ///         drivers is the previous frame — so it reads as smearing rather than as an uninitialised
    ///         target, and it is the one of the three that looks plausible.
    ///     </para>
    /// </remarks>
    [Fact]
    public void A_tiled_pass_draws_two_triangles_per_tile_over_a_loaded_target() {
        using var water = Node();

        water.Tiled = true;
        water.Pipelines = new(device);

        Build(Compositor(water));

        var tiles = WaterTiles.CountFor(new(Size, Size));

        Assert.Equal(tiles, water.TileCount);
        Assert.Equal(WaterTiles.VerticesPerTile, water.Pass.Vertices);
        Assert.Equal(WaterTiles.Total(tiles), water.Pass.Instances);
        Assert.Equal(LoadAction.Load, water.Pass.Load);

        // And the classification reads the coverage mask and writes the flags, which is what orders it
        // between the surface pass and the draw.
        Assert.Contains("WaterSurface", water.Classification.Reads);
        Assert.Equal(new Int3(tiles.X, tiles.Y, 1), water.Classification.Groups);
        Assert.Single(water.Classification.BufferWrites);
        Assert.Equal(water.Classification.BufferWrites[0], water.Pass.BufferReads.Single());
    }

    /// <summary>Untiled, it is the full-screen triangle it has always been — and still binds the flags.</summary>
    /// <remarks>
    ///     ⚠ <b>A descriptor set is written wholly or not at all</b>, so the untiled variant binds the
    ///     tile buffer too: a shader's bindings come from its declarations and not from the variant it
    ///     was compiled into. Without it the driver refuses the set in the path that has no tiling at
    ///     all, which is a frame that does not render rather than a frame with no optimisation.
    /// </remarks>
    [Fact]
    public void An_untiled_pass_is_one_triangle_and_binds_the_tile_buffer_anyway() {
        using var water = Node();

        Build(Compositor(water));

        Assert.Equal(3, water.Pass.Vertices);
        Assert.Equal(1, water.Pass.Instances);
        Assert.Equal(LoadAction.DontCare, water.Pass.Load);
        Assert.Equal(default, water.TileCount);

        Assert.Single(water.Pass.Descriptors.Bindings, binding => binding.Kind == DescriptorKind.StorageBuffer);
        Assert.Single(water.Pass.BufferReads);
        Assert.Empty(water.Classification.BufferWrites);
    }

    /// <summary>A host that asked for tiling without a compute cache gets the untiled pass.</summary>
    /// <remarks>
    ///     The terms every other optional half of this renderer is built on: the picture is the same
    ///     either way, so a missing dependency costs the optimisation rather than the frame.
    /// </remarks>
    [Fact]
    public void Tiling_without_a_compute_cache_is_off_rather_than_broken() {
        using var water = Node();

        water.Tiled = true;

        Build(Compositor(water));

        Assert.Equal(3, water.Pass.Vertices);
        Assert.Equal(1, water.Pass.Instances);
        Assert.Equal(default, water.TileCount);
    }

    // --- What a document says -----------------------------------------------

    /// <summary>The factory builds the node a document named, with the numbers it stated.</summary>
    [Fact]
    public void The_factory_carries_a_documents_numbers_onto_the_node() {
        var builder = new CompositorBuilder(system) { Samplers = samplers, Descriptors = descriptors, Device = device };

        var asset = new WaterAsset {
            Name = "Sea",
            Behind = "Snapshot",
            Reflections = "Mirrors",
            Absorption = new(0.4f, 0.05f, 0.01f),
            PhaseG = 0.55f,
            BehindScale = new(0.5f, 0.5f, 0.5f),
            SunColour = new(1.2f, 1f, 0.8f),
            SunDirection = new(0.5f, -0.7f, 0.5f),
            Foam = false,
            Tiled = false
        };

        using var node = Assert.IsType<WaterRenderer>(new WaterRendererFactory().Create(asset, builder));

        Assert.Equal("Sea", node.Name);
        Assert.Equal("Snapshot", node.Behind);
        Assert.Equal("Mirrors", node.Reflections);
        Assert.Equal(0.55f, node.PhaseG);
        Assert.Equal(new Vector3(0.5f, 0.5f, 0.5f), node.BehindScale);
        Assert.False(node.Foam);

        // ⚠ The sun rides the document too. Without these two the phase function's forward peak sits
        // under a noon sun whatever the sky in the same document says — a lake lit from a different
        // day than its sky, and nothing an author types elsewhere can move it.
        Assert.Equal(new Vector3(1.2f, 1f, 0.8f), node.SunColour);
        Assert.Equal(new Vector3(0.5f, -0.7f, 0.5f), node.SunDirection);

        // ⚠ And § D8's tiling rides it too — on by default for a document, because a document has the
        // !Copy that makes a tiled pass and an untiled one the same picture, and a node somebody wired
        // by hand does not. See WaterRenderer.Tiled.
        Assert.False(node.Tiled);
        Assert.True(new WaterAsset().Tiled);
        Assert.NotNull(node.Pipelines);

        // ⚠ And its defaults are water's, not zero. `behindScale` at zero is a perfectly black frame
        // behind the water, which reads as "the water is opaque" rather than as a parameter nobody set.
        Assert.Equal(Vector3.One, new WaterAsset().BehindScale);
        Assert.Equal(0.02f, new WaterAsset().SurfaceF0);
        Assert.Equal(new Vector3(0f, -1f, 0f), new WaterAsset().SunDirection);
    }

    /// <summary>A sun and a sky, as a frame holds them.</summary>
    sealed class Frame(RenderLight? sun) : ISunSource {
        public RenderLight? Sun => sun;
    }

    /// <summary>
    ///     ⚠ The composite's radiance comes from the frame's own lighting, not from the document.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         <b>Task #119, as a property.</b> <c>sunColour</c> and <c>skyColour</c> are radiances in
    ///         the frame's units and a document can only write a tint — this level's sun is twenty
    ///         thousand and the number an author types is one. The volume then integrates <em>exactly
    ///         correctly</em> to a value four decades under the exposure, which tonemaps to the same
    ///         black as unlit ground: a lake that is four decades too dim and a water pass that never
    ///         ran are pixel-for-pixel the same picture, and every counter in the stack says success.
    ///     </para>
    ///     <para>
    ///         ⚠ The sky assertion is against the environment's <em>mean radiance</em> and not its
    ///         <c>L00</c>. An SH projection of a uniform environment has <c>L00 = L·Y₀·4π</c>, so
    ///         handing the coefficient over is 3.54× too much sky — a lake that glows, from a number
    ///         with no second source to check it against.
    ///     </para>
    /// </remarks>
    [Fact]
    public void The_composite_takes_its_radiance_from_the_frames_own_lighting() {
        using var node = Node();

        // What a document can write: a tint, around one.
        node.SunColour = new(1f, 0.72f, 0.42f);
        node.SkyColour = new(0.36f, 0.30f, 0.30f);
        node.SunDirection = new(0f, -1f, 0f);

        var sun = RenderLight.Directional(
            Vector3.Normalize(new(-0.57f, -0.14f, 0.81f)),
            new(1f, 0.62f, 0.18f),
            13_785f
        );

        var sky = new EnvironmentLight { Intensity = 1f, Irradiance = new() { L00 = new(6698f, 6254f, 5249f) } };
        var lighting = new SceneLighting { Sun = new Frame(sun), Environment = sky };

        Assert.True(node.LightFrom(lighting));

        Assert.Equal(sun.Direction, node.SunDirection);
        Assert.Equal(sun.Radiance, node.SunColour);
        Assert.Equal(sky.Irradiance.L00 * 0.282095f, node.SkyColour);

        // ⚠ The assertion that is the bug rather than the getter: what the frame supplies is orders of
        // magnitude above anything a document states, and it is that gap — not a hue — that decides
        // whether there is a lake in the picture. A hundred is far below the four decades measured and
        // far above any tint.
        Assert.True(
            node.SunColour.X > 100f,
            $"the water is lit at {node.SunColour.X}, which is a tint rather than a radiance"
        );

        Assert.True(node.SkyColour.X > 100f, $"the sky over the water is {node.SkyColour.X}");
    }

    /// <summary>
    ///     ⚠ And a frame with no sun leaves what the document stated exactly where it was.
    /// </summary>
    /// <remarks>
    ///     The other half of the same decision, and the reason this returns a bool. A host that feeds
    ///     the node before its lighting has a sun — which is every host, on the first frame — must get
    ///     the authored fallback rather than black: zeroed radiance is a lake lit by nothing, which is
    ///     the very picture this whole wiring exists to stop being possible.
    /// </remarks>
    [Fact]
    public void A_frame_with_no_sun_leaves_the_authored_numbers_alone() {
        using var node = Node();

        node.SunColour = new(1f, 0.72f, 0.42f);
        node.SkyColour = new(0.36f, 0.30f, 0.30f);

        Assert.False(node.LightFrom(new SceneLighting { Sun = new Frame(null) }));
        Assert.False(node.LightFrom(new SceneLighting()));

        Assert.Equal(new Vector3(1f, 0.72f, 0.42f), node.SunColour);
        Assert.Equal(new Vector3(0.36f, 0.30f, 0.30f), node.SkyColour);
    }

    /// <summary>A factory answers nothing for a node kind that is not its own.</summary>
    /// <remarks>
    ///     Asked after the built-ins, so a factory that claimed everything would quietly replace a
    ///     node kind the document's schema already defines.
    /// </remarks>
    [Fact]
    public void The_factory_declines_a_node_that_is_not_its_own() {
        var builder = new CompositorBuilder(system);

        Assert.Null(new WaterRendererFactory().Create(new SequenceAsset { Name = "Frame" }, builder));
    }
}

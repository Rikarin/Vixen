// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core;
using Vixen.Core.Mathematics;
using Vixen.Ecs;
using Vixen.Engine.Transforms;
using Vixen.Graphics;
using Vixen.Graphics.Null;
using Vixen.Graphics.RenderGraph;
using Vixen.Rendering;
using Vixen.Rendering.Compositor;
using Vixen.Rendering.Water;
using Vixen.Shaders;
using Vixen.Water;
using Xunit;

namespace Tests;

/// <summary>
///     The waterline composite — [docs/plan/35 § D9]'s second half.
/// </summary>
/// <remarks>
///     <para>
///         <b>§ D9 warns about this one twice</b>: "designing the volume path first and discovering
///         the waterline second is how you get a system where the transition is a hard cut and the fix
///         is architectural". The volume half is <c>UnderwaterShape</c> over doc 32's fold and grades
///         the whole frame; a fold produces one weight and a waterline is a <em>curve</em>, so it
///         cannot be the same feature however much it looks like one.
///     </para>
///     <para>
///         What is asserted here is the plane the curve is solved from and the wiring that carries it
///         — the look belongs to a golden image and the arithmetic to the shader. The plane is the
///         part that is silent when it is wrong: a waterline solved from the rest height sits at mean
///         sea level while the drawn surface moves around it, which reads as the camera being wrong.
///     </para>
/// </remarks>
public sealed class UnderwaterPassTests : IDisposable {
    const int Size = 16;

    readonly NullDevice device = new(new() { Record = true });
    readonly EffectSystem effects = new();
    readonly RenderSystem system = new();
    readonly RenderGraph graph;
    readonly SamplerCache samplers;
    readonly DescriptorAllocator descriptors;
    readonly World world = new();
    readonly RenderView view = new("Camera");

    public UnderwaterPassTests() {
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
        world.Dispose();
        GC.SuppressFinalize(this);
    }

    // --- The fixture --------------------------------------------------------

    static RenderResourceAsset Declared(string name) =>
        new() { Name = name, Format = PixelFormat.Rgba16Float, Usage = TextureUsage.ColourTarget | TextureUsage.Sampled };

    UnderwaterRenderer Node(string behind = "SceneColourCopy", WaterZoneSystem? zones = null) =>
        new() {
            Name = "Underwater",
            Output = "SceneColour",
            Behind = behind,
            SceneDepth = "SceneDepth",
            Surface = "WaterSurface",
            Zones = zones,
            Samplers = samplers,
            Allocator = descriptors,
            Device = device
        };

    GraphicsCompositor Compositor(SceneRenderer node) {
        var sequence = new SceneRendererSequence { Name = "Frame" };

        sequence.Children.Add(Writer("SceneColourCopy"));
        sequence.Children.Add(Writer("SceneDepth"));
        sequence.Children.Add(Writer("WaterSurface"));
        sequence.Children.Add(node);

        var compositor = new GraphicsCompositor(system) { FrameSize = new(Size, Size), Game = sequence };

        foreach (var name in (string[])["SceneColour", "SceneColourCopy", "SceneDepth", "WaterSurface"]) {
            compositor.Resources.Add(Declared(name));
        }

        return compositor;
    }

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

    /// <summary>A zone system over one square lake at a height, folded once.</summary>
    WaterZoneSystem Lake(float height) {
        var zone = world.Create();

        world.Add(zone, WaterZoneComponent.Default with { Waves = WaterWaveSpectrum.Calm });
        world.Add(zone, new WorldTransform { Value = Matrix4x4.Identity });

        var body = world.Create();

        world.Add(body, WaterBodyComponent.Default with { Spline = "Lake", SurfaceHeight = height });
        world.Add(body, new WorldTransform { Value = Matrix4x4.Identity });

        var system = new WaterZoneSystem(view) {
            Splines = new Square(60f, height),
            Ground = new FlatWaterGround(height - 20f)
        };

        system.Fold(world);

        return system;
    }

    /// <summary>A source that hands out one square lake at a height.</summary>
    sealed class Square(float half, float height) : IWaterSplineSource {
        public Spline? SplineFor(string name, in Matrix4x4 placement) =>
            name.Length == 0
                ? null
                : new(
                    Spline.SmoothTangents(
                        [
                            new(-half, height, -half), new(half, height, -half),
                            new(half, height, half), new(-half, height, half)
                        ],
                        closed: true,
                        tension: 1f
                    ),
                    closed: true
                );
    }

    // --- What it declares ---------------------------------------------------

    /// <summary>The pass reads every plane it says it reads, which is what orders it.</summary>
    [Fact]
    public void The_pass_declares_every_plane_it_binds() {
        using var node = Node();

        Build(Compositor(node));

        Assert.Equal(1, node.BuildCount);

        Assert.Contains("SceneColourCopy", node.Pass.Reads);
        Assert.Contains("SceneDepth", node.Pass.Reads);
        Assert.Contains("WaterSurface", node.Pass.Reads);
        Assert.Contains("SceneColour", node.Pass.ColourTargets);

        Assert.Equal(3, node.Pass.Descriptors.Bindings.Count(binding => binding.Kind == DescriptorKind.SampledTexture));
        Assert.Equal(2, node.Pass.Descriptors.Bindings.Count(binding => binding.Kind == DescriptorKind.Sampler));
    }

    /// <summary>
    ///     ⚠ Reading the target it writes is refused, by name, at build time.
    /// </summary>
    /// <remarks>
    ///     [docs/plan/35 § B1]'s rule, and this is the pass where a driver that seemed to tolerate it
    ///     would show the tolerance up: the read is <em>displaced</em> by the refraction, so a sampled
    ///     write-in-flight is a visible smear rather than a subtly wrong pixel.
    /// </remarks>
    [Fact]
    public void Reading_the_target_it_writes_is_refused() {
        using var node = Node(behind: "SceneColour");

        var thrown = Assert.Throws<CompositorBindingException>(() => Build(Compositor(node)));

        Assert.Contains("!Copy", thrown.Message, StringComparison.Ordinal);
        Assert.Contains("smear", thrown.Message, StringComparison.Ordinal);
    }

    /// <summary>A host that has not wired the renderer up gets a frame with no grade.</summary>
    [Fact]
    public void A_node_with_no_device_does_nothing_rather_than_throwing() {
        using var node = new UnderwaterRenderer {
            Name = "Underwater",
            Output = "SceneColour",
            Behind = "SceneColourCopy",
            SceneDepth = "SceneDepth",
            Surface = "WaterSurface"
        };

        Build(Compositor(node));

        Assert.Equal(0, node.BuildCount);
        Assert.Empty(node.Pass.Reads);
    }

    // --- The plane the curve is solved from ---------------------------------

    /// <summary>
    ///     ⚠ A node with no zone system grades nothing, rather than grading everything.
    /// </summary>
    /// <remarks>
    ///     The negative control, and the failure is loud in the wrong direction: a waterline with no
    ///     water would fog the whole world blue, which is a much worse frame than one with no effect
    ///     at all. So the submersion stays at its "above" value and every pixel takes the shader's
    ///     first branch.
    /// </remarks>
    [Fact]
    public void A_node_with_no_zones_is_above_the_water() {
        using var node = Node();

        Build(Compositor(node));

        Assert.False(node.IsSubmerged);
        Assert.True(node.Submersion < 0f);

        // ⚠ And the normal is +Y and not zero. A zeroed normal makes the plane test `dot(x, 0)`,
        // which is zero everywhere — a waterline that is simultaneously nowhere and across the whole
        // screen, depending on which side of the feather zero lands on.
        Assert.Equal(Vector3.UnitY, node.SurfaceNormal);
    }

    /// <summary>
    ///     A camera under the surface finds the plane, at the height the water is <em>drawn</em> at.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>The same <c>WaterQuery</c> the buoyancy solver and the volume fold read.</b> § D2's
    ///     claim is that there is one definition of where the surface is; a waterline evaluated from
    ///     anything else is a line somewhere the water is not, on a frame where nothing else looks
    ///     wrong.
    /// </remarks>
    [Fact]
    public void A_camera_under_the_surface_finds_the_plane_it_is_under() {
        var zones = Lake(10f);

        view.Position = new(0f, 9f, 0f);

        using var node = Node(zones: zones);

        node.View = view;

        Build(Compositor(node));

        Assert.True(node.IsSubmerged);
        Assert.Equal(1f, node.Submersion, 0.05f);

        // The plane passes through the surface directly over the camera, not through the camera.
        Assert.Equal(0f, node.SurfacePoint.X, 4);
        Assert.Equal(10f, node.SurfacePoint.Y, 0.05f);
        Assert.True(node.SurfaceNormal.Y > 0.9f, "the surface normal is not roughly up");
    }

    /// <summary>And a camera above it is above it, by the distance a person would measure.</summary>
    [Fact]
    public void A_camera_above_the_surface_is_not_submerged() {
        var zones = Lake(10f);

        view.Position = new(0f, 12f, 0f);

        using var node = Node(zones: zones);

        node.View = view;

        Build(Compositor(node));

        Assert.False(node.IsSubmerged);
        Assert.Equal(-2f, node.Submersion, 0.05f);
    }

    /// <summary>A camera outside every window is above water rather than an error.</summary>
    /// <remarks>
    ///     It is what a camera flying away from a lake is, and the whole-frame blue the other answer
    ///     produces is a much louder bug than the one it would prevent.
    /// </remarks>
    [Fact]
    public void A_camera_outside_every_window_grades_nothing() {
        var zones = Lake(10f);

        view.Position = new(5_000f, 9f, 0f);

        using var node = Node(zones: zones);

        node.View = view;

        Build(Compositor(node));

        Assert.False(node.IsSubmerged);
        Assert.True(node.Submersion < 0f);
    }

    // --- What a document says -----------------------------------------------

    /// <summary>The factory builds the node a document named, with the numbers it stated.</summary>
    [Fact]
    public void The_factory_carries_a_documents_numbers_onto_the_node() {
        var builder = new CompositorBuilder(system) { Samplers = samplers, Descriptors = descriptors, Device = device };

        var asset = new UnderwaterAsset {
            Name = "Below",
            Behind = "Snapshot",
            WaterlineFeather = 0.1f,
            CausticAmount = 0.3f,
            Distortion = false
        };

        using var node = Assert.IsType<UnderwaterRenderer>(new WaterRendererFactory().Create(asset, builder));

        Assert.Equal("Below", node.Name);
        Assert.Equal("Snapshot", node.Behind);
        Assert.Equal(0.1f, node.WaterlineFeather);
        Assert.Equal(0.3f, node.CausticAmount);
        Assert.False(node.Distortion);
    }

    /// <summary>
    ///     ⚠ Its medium is the same triple <c>!Water</c> integrates with, by default.
    /// </summary>
    /// <remarks>
    ///     <b>The cheapest assertion here and the one that catches the loudest bug.</b> A frame whose
    ///     surface pass and whose underwater pass disagree about the medium is a lake that changes
    ///     colour when you put your head under — which reads as a bug in whichever pass you happen to
    ///     look at second, and is a pair of numbers that drifted apart because nothing held them
    ///     together.
    /// </remarks>
    [Fact]
    public void The_medium_matches_the_water_passs_by_default() {
        var water = new WaterAsset();
        var under = new UnderwaterAsset();

        Assert.Equal(water.Scattering, under.Scattering);
        Assert.Equal(water.Absorption, under.Absorption);
        Assert.Equal(water.PhaseG, under.PhaseG);
        Assert.Equal(water.SkyColour, under.SkyColour);
        Assert.Equal(water.BehindScale, under.BehindScale);
    }

    /// <summary>
    ///     ⚠ The waterline's feather is narrow and is never zero.
    /// </summary>
    /// <remarks>
    ///     A hard step across a curve that moves with the swell is an aliased, crawling edge — the
    ///     kind that sends people to their antialiasing settings to fix a geometry problem. Narrow is
    ///     the point; soft is not.
    /// </remarks>
    [Fact]
    public void The_waterlines_feather_is_narrow_and_not_zero() {
        var feather = new UnderwaterAsset().WaterlineFeather;

        Assert.InRange(feather, 0.005f, 0.25f);
    }

}

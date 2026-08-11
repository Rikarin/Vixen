// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core;
using Vixen.Core.Imaging;
using Vixen.Core.Mathematics;
using Vixen.Ecs;
using Vixen.Engine.Transforms;
using Vixen.Graphics.Vulkan;
using Vixen.Rendering;
using Vixen.Rendering.Compositor;
using Vixen.Rendering.Water;
using Vixen.Shaders;
using Vixen.Ui.Testing.Visual;
using Vixen.Water;
using Xunit;

namespace Vixen.Graphics.Golden.Tests;

/// <summary>
///     An ocean to the horizon — [docs/plan/35 § W4]'s exit criterion, drawn.
/// </summary>
/// <remarks>
///     <para>
///         <b>§ Risks says why this shot and not another:</b> "a flat surface to the horizon is where
///         depth precision, LOD error metrics and half-float quantisation all show at once, and it is
///         the shot every water screenshot is taken from. It is a golden image from W4, not a polish
///         item."
///     </para>
///     <para>
///         <b>The arithmetic halves are <c>WaterSurfaceMeshTests</c>' and are device-free</b> — the
///         no-crack property, the morph continuity, the coverage predicate, the far-mesh cut and the
///         amplitude bound. What none of them can see is a <em>rasterised</em> crack: they assert that
///         two adjacent nodes agree about a shared edge's vertices, and a hole that opens because the
///         far skirt was drawn in the wrong order, or because a patch was dropped over the cap, is a
///         property of the draw and not of the descent.
///     </para>
///     <para>
///         ⚠ <b>Assertions on the geometry rather than a reference bitmap</b>, on
///         <see cref="WaterPassImageTests" />' stated terms: a golden PNG says "these bytes changed"
///         and leaves somebody to work out whether it was the selection, the morph, the depth or the
///         skirt. What is asserted here is each separately — that the ocean reaches the horizon, that
///         it is drawn nearest-first down the screen, that nothing inside it is unwritten, and that it
///         cost two draws.
///     </para>
///     <para>
///         Serialised with the rest of the driver tests: <see cref="VulkanDiagnostics" /> is
///         process-wide.
///     </para>
/// </remarks>
[Collection("Vulkan")]
public sealed class WaterHorizonImageTests {
    /// <summary>How high the camera sits above the surface, in metres — a person on a beach.</summary>
    const float Eye = 1.7f;

    /// <summary>Where the ocean's surface is, in world units.</summary>
    const float Surface = 0f;

    /// <summary>
    ///     ⚠ The ocean reaches the horizon, and it takes two draws to do it.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         <b>The whole of § W4's criterion in one frame.</b> A window is five hundred metres
    ///         across and a horizon from a standing eye is five kilometres away, so an ocean that
    ///         stopped at the window's edge would be an ocean with a straight edge across the middle
    ///         of the screen — which is exactly what the far skirt exists to prevent and exactly what
    ///         no device-free test can see.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Two draws and not one, and the doc's "one draw call" is per <em>mesh</em>.</b> The
    ///         window and the skirt are two instanced draws of the same grid patch, because they are
    ///         two vertex permutations — the window reads the info texture and the skirt has none to
    ///         read. A frame that reported one drew only half the ocean; a frame reporting three has
    ///         a second zone somebody did not mean to place.
    ///     </para>
    /// </remarks>
    [Fact]
    public void An_ocean_reaches_the_horizon_in_two_draws() {
        if (!TryOpen(out var fixture)) {
            return;
        }

        using var owned = fixture!;

        var frame = Draw(owned, out var mesh, out var image);

        Assert.Equal(1, mesh.ZonesDrawn);
        Assert.True(mesh.PatchesDrawn > 0, "the surface selected no patches at all");

        // ⚠ Non-zero is a hole in the water — see WaterSurfacePass.MaxPatches. A frame that dropped
        // patches would fail the crack assertion below for a reason that is not a crack.
        Assert.Equal(0, mesh.DroppedPatches);

        var covered = Rows(image);

        // The ocean starts somewhere above the middle of the screen — the horizon from 1.7 m — and
        // runs to the bottom.
        Assert.True(covered.First >= 0, "no pixel of the frame has any water coverage at all");
        Assert.True(covered.Last >= Fixture.Side - 2, $"the ocean stops {Fixture.Side - covered.Last} rows short of the camera");

        // ⚠ And it reaches *past* the window. A 512 m window seen from 1.7 m subtends a few degrees;
        // if the skirt were missing, the covered band would be a fraction of what it is.
        Assert.True(
            covered.Last - covered.First > Fixture.Side / 3,
            $"the ocean covers rows {covered.First}…{covered.Last}, which is a window with no skirt beyond it"
        );

        _ = frame;
    }

    /// <summary>
    ///     ⚠ Nothing inside the ocean is unwritten — the crack test, rasterised.
    /// </summary>
    /// <remarks>
    ///     <b>What the device-free no-crack test cannot see.</b> That test asserts two adjacent nodes
    ///     agree about the vertices along their shared edge, which is the descent being right. A hole
    ///     that opens because the far skirt was drawn second, or because a patch fell over the cap, or
    ///     because the depth state let one fragment win where the other should have, is a property of
    ///     the <em>draw</em> — and it looks exactly like the sky, one pixel wide, at a level boundary
    ///     nobody was looking at.
    /// </remarks>
    [Fact]
    public void There_is_no_unwritten_pixel_inside_the_ocean() {
        if (!TryOpen(out var fixture)) {
            return;
        }

        using var owned = fixture!;

        Draw(owned, out _, out var image);

        var covered = Rows(image);

        Assert.True(covered.First >= 0, "no pixel of the frame has any water coverage at all");

        var holes = 0;

        // Two rows in from the first covered one, so the horizon's own partially covered row is not
        // counted as a crack: a silhouette edge is a real partial pixel and is not a hole.
        for (var y = covered.First + 2; y <= covered.Last; y++) {
            for (var x = 1; x < Fixture.Side - 1; x++) {
                if (Coverage(image, x, y) > 0.1f) {
                    continue;
                }

                // Surrounded on both sides is a hole; at the edge of the frame it is the shoreline of
                // the visible cone and is not.
                if (Coverage(image, x - 1, y) > 0.5f && Coverage(image, x + 1, y) > 0.5f) {
                    holes++;
                }
            }
        }

        Assert.True(holes == 0, $"{holes} pixels inside the ocean have no surface over them");
    }

    /// <summary>
    ///     ⚠ The surface is nearer at the bottom of the screen than at the top, everywhere.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         <b>Depth precision and the reverse-Z convention, in one property.</b> The engine's near
    ///         plane is 1 and its far plane is 0 (§ E), so a flat surface seen from above has a device
    ///         depth that <em>decreases</em> up the screen — monotonically, because the surface is a
    ///         plane and the camera is looking along it.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Non-monotone is the failure this shot exists to catch.</b> A patch whose morph put
    ///         its vertices at the wrong distance, a skirt drawn at the rest height where the window
    ///         has a crest, or a depth written the textbook way round all produce a band that is
    ///         nearer than the band in front of it — which in a picture is a seam of slightly
    ///         differently-shaded water and is attributed to the shading.
    ///     </para>
    ///     <para>
    ///         Measured at the centre column and with a tolerance, because a wave <em>is</em> allowed
    ///         to move a vertex toward the camera. What is not allowed is a whole band.
    ///     </para>
    /// </remarks>
    [Fact]
    public void The_surface_gets_nearer_down_the_screen() {
        if (!TryOpen(out var fixture)) {
            return;
        }

        using var owned = fixture!;

        Draw(owned, out _, out var image);

        var covered = Rows(image);

        // ⚠ Guarded, because everything below is a loop over the covered rows — and a frame with none
        // would run it zero times and pass. A vacuous green here is what a silent no-draw looks like
        // from a test, which is exactly the failure this file's other two caught on the first device
        // run.
        Assert.True(covered.First >= 0, "no pixel of the frame has any water coverage at all");
        Assert.True(covered.Last - covered.First > 8, $"only rows {covered.First}…{covered.Last} are water");

        var column = Fixture.Side / 2;
        var previous = -1f;
        var reversals = 0;
        var measured = 0;

        for (var y = covered.First + 2; y <= covered.Last; y++) {
            if (Coverage(image, column, y) <= 0.5f) {
                continue;
            }

            var depth = Depth(image, column, y);

            // Reverse-Z: nearer is larger, and down the screen is nearer.
            if (previous >= 0f && depth < previous - 0.02f) {
                reversals++;
            }

            previous = depth;
            measured++;
        }

        Assert.True(measured > 8, $"only {measured} rows of the centre column are water");
        Assert.True(reversals == 0, $"{reversals} rows of the ocean are further away than the row above them");
    }

    /// <summary>The first and last screen rows with any water coverage in them.</summary>
    static (int First, int Last) Rows(in Bitmap image) {
        var first = -1;
        var last = -1;

        for (var y = 0; y < Fixture.Side; y++) {
            var any = false;

            for (var x = 0; x < Fixture.Side && !any; x++) {
                any = Coverage(image, x, y) > 0.5f;
            }

            if (!any) {
                continue;
            }

            first = first < 0 ? y : first;
            last = y;
        }

        return (first, last);
    }

    /// <summary>Draws one frame of open ocean seen from a standing eye.</summary>
    static CompositorFrame Draw(Fixture fixture, out WaterMeshRenderer mesh, out Bitmap image) {
        var device = fixture.Device;

        using var world = new World();

        var view = new RenderView("Camera") {
            Position = new(0f, Eye, 0f),

            // Looking along −Z at the horizon, with a small downward tilt so the ocean fills the
            // lower part of the frame rather than exactly half — which is what a person standing on a
            // beach is doing and what makes the covered band worth measuring.
            //
            // ⚠ Set through ViewProjection, which re-derives the frustum. Setting the frustum
            // separately is two properties describing one volume, and the patch selection culls
            // against the frustum while the vertex stage projects with the matrix.
            ViewProjection = Matrix4x4.LookAt(new(0f, Eye, 0f), new(0f, Eye - 0.35f, -1f), Vector3.UnitY)
                * Matrix4x4.PerspectiveFieldOfView(1.0f, 1f, 0.1f, 20_000f)
        };

        var zones = Ocean(world, view);

        using var allocator = new DescriptorAllocator(device);
        using var samplers = new SamplerCache(device);
        using var system = new RenderSystem();

        var describer = new EffectPipelineDescriber(device);
        var effects = new EffectSystem();

        effects.AddProvider(
            new Compiling(
                new EffectLoader(device),
                _ => RavenEffects.Only(
                    ["Core", "Geometry", "Shading", "Water"],
                    Path.Combine("PostFx", "Fullscreen.rvn")
                )
            )
        );

        using var node = new WaterMeshRenderer {
            Name = "WaterSurface",
            Surface = "WaterSurface",
            Normal = "WaterNormal",
            SceneDepth = "SceneDepth",
            View = view,
            Zones = zones,

            // A dead calm, deliberately. This shot is about where the geometry is, and a swell would
            // make every depth assertion below a question about which part of a wave a row landed on.
            Settings = WaterMeshSettings.Default with { RestHeight = Surface },
            Modules = describer,
            Samplers = samplers,
            Device = device
        };

        // ⚠ The depth has to be cleared before the surface is drawn, and nothing in the water stack
        // does it. WaterMeshRenderer declares LoadAction.Load — the surface joins a frame that already
        // has its opaque depth — and its depth state is Greater with no write, so against an
        // *undefined* depth buffer every fragment fails the test and the frame is empty with no
        // validation error anywhere. Which is the silent no-draw this fixture is for.
        //
        // Zero is the far plane under reverse-Z: an empty scene with nothing in front of the water.
        var sequence = new SceneRendererSequence { Name = "Frame" };

        sequence.Children.Add(
            new DelegateSceneRenderer {
                Name = "Clear depth",
                OnBuild = (_, built) => {
                    var target = built.Texture("harness", "SceneDepth");

                    built.Graph.AddPass(
                        "Clear depth",
                        pass => {
                            pass.DepthAttachment(target, LoadAction.Clear, 0f);
                            pass.SideEffect();
                            pass.Execute(_ => { });
                        }
                    );
                }
            }
        );

        sequence.Children.Add(node);

        var compositor = new GraphicsCompositor(system) {
            FrameSize = new(Fixture.Side, Fixture.Side),
            Game = sequence
        };

        // ⚠ Owned rather than ColourTarget'd, and the fixture's own remarks say why: a compositor
        // imports its targets by name, so a harness that had already imported them would hand the
        // graph two virtual resources over one texture and get a barrier between a pass and itself.
        var surface = fixture.Owned("WaterSurface", TextureUsage.ColourTarget | TextureUsage.CopySource);
        var normal = fixture.Owned("WaterNormal", TextureUsage.ColourTarget | TextureUsage.CopySource);

        var depth = fixture.Owned(
            "SceneDepth",
            TextureUsage.DepthStencilTarget | TextureUsage.Sampled,
            PixelFormat.Depth32Float
        );

        compositor.Imports["WaterSurface"] = new(surface.Texture, surface.View, surface.Description, ResourceState.Undefined, ResourceState.CopySource);
        compositor.Imports["WaterNormal"] = new(normal.Texture, normal.View, normal.Description);

        // ⚠ Cleared to zero, which under reverse-Z is the far plane — an empty scene with nothing in
        // front of the water. Cleared to one it would be a scene where everything is at the near
        // plane, and the surface would be depth-tested away everywhere.
        compositor.Imports["SceneDepth"] = new(depth.Texture, depth.View, depth.Description);

        allocator.BeginFrame();

        fixture.Graph.Reset();

        var built = compositor.Build(fixture.Graph, effects, device);

        Assert.Empty(effects.Misses);

        image = fixture.Render(built.Texture("harness", "WaterSurface"));
        mesh = node;

        return built;
    }

    /// <summary>An open ocean with no shoreline anywhere near, folded once.</summary>
    /// <remarks>
    ///     ⚠ <b>A body large enough that the window is entirely inside it</b>, so no part of this
    ///     picture is a shoreline. A coverage that fell off at the window's edge would make the
    ///     crack assertion count the shore as holes, which is the assertion measuring the fixture.
    /// </remarks>
    static WaterZoneSystem Ocean(World world, RenderView view) {
        var zone = world.Create();

        world.Add(
            zone,
            WaterZoneComponent.Default with {
                Extent = 512f,
                Resolution = 257,
                Waves = WaterWaveSpectrum.Calm with { WindSpeed = 0f, AmplitudeScale = 0f }
            }
        );

        world.Add(zone, new WorldTransform { Value = Matrix4x4.Identity });

        var body = world.Create();

        world.Add(
            body,
            WaterBodyComponent.Default with {
                Kind = WaterBodyKind.Ocean,
                Spline = "Ocean",
                SurfaceHeight = Surface,
                Depth = 200f
            }
        );

        world.Add(body, new WorldTransform { Value = Matrix4x4.Identity });

        var zones = new WaterZoneSystem(view) {
            Splines = new Square(4_000f, Surface),
            Ground = new FlatWaterGround(Surface - 200f)
        };

        zones.Fold(world);

        return zones;
    }

    /// <summary>A square body four kilometres on a side, which is past the horizon.</summary>
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

    /// <summary>The coverage the surface plane wrote at a pixel — its green channel.</summary>
    static float Coverage(in Bitmap image, int x, int y) =>
        image.Pixels[image.Offset(Math.Clamp(x, 0, image.Width - 1), Math.Clamp(y, 0, image.Height - 1)) + 1] / 255f;

    /// <summary>And the surface's device depth — its red channel.</summary>
    static float Depth(in Bitmap image, int x, int y) =>
        image.Pixels[image.Offset(Math.Clamp(x, 0, image.Width - 1), Math.Clamp(y, 0, image.Height - 1))] / 255f;

    static bool TryOpen(out Fixture? fixture) {
        if (Fixture.TryOpen(out fixture, out var reason)) {
            return true;
        }

        if (Environment.GetEnvironmentVariable("VIXEN_REQUIRE_VULKAN") is "1" or "true" or "TRUE") {
            Assert.Fail($"VIXEN_REQUIRE_VULKAN is set and no device could be opened: {reason}");
        }

        return false;
    }
}

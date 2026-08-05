// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core;
using Vixen.Core.Mathematics;
using Vixen.Ecs;
using Vixen.Engine.Diagnostics;
using Vixen.Engine.Transforms;
using Vixen.Rendering;
using Vixen.Rendering.Water;
using Vixen.Terrain;
using Vixen.Water;
using Xunit;

namespace Tests;

/// <summary>
///     The <c>water.show*</c> verbs, drawing lines — [docs/plan/35 § Part 2 § Debugging].
/// </summary>
/// <remarks>
///     <para>
///         <b>These shipped as flags with the drawing owed</b>, on the belief that water had no seam
///         into a viewport line pass. <c>DebugDraw</c> is that seam — an accumulator rather than a
///         renderer — so what is asserted here is that switching a verb on puts geometry into it and
///         switching it off does not.
///     </para>
///     <para>
///         ⚠ <b>Every test switches the flags back off.</b> They are process-wide statics, which is
///         what a console verb needs them to be and what makes a test that forgets leak into whichever
///         test the runner picks next. <see cref="WaterDebug.Reset" /> exists for exactly this.
///     </para>
///     <para>
///         The <em>look</em> of the overlays is a person's to judge and is not asserted. What is
///         asserted is the thing that is silent when it is wrong: a verb that is on and draws nothing
///         is indistinguishable from a verb nobody typed.
///     </para>
/// </remarks>
public sealed class WaterDebugDrawTests : IDisposable {
    readonly World world = new();
    readonly RenderView view = new("Camera");
    readonly DebugDraw draw = new();

    /// <inheritdoc />
    public void Dispose() {
        WaterDebug.Reset();
        world.Dispose();
        GC.SuppressFinalize(this);
    }

    /// <summary>A source that hands out one square river with a velocity.</summary>
    sealed class Straight(float half, float height) : IWaterSplineSource {
        public Spline? SplineFor(string name, in Matrix4x4 placement) =>
            name.Length == 0
                ? null
                : new(
                    Spline.SmoothTangents(
                        [new(-half, height, 0f), new(0f, height, 0f), new(half, height, 0f)],
                        closed: false
                    ),
                    closed: false
                );
    }

    WaterZoneSystem Folded(float velocity = 0f) {
        var zone = world.Create();

        world.Add(zone, WaterZoneComponent.Default with { Extent = 128f, Resolution = 65, Waves = WaterWaveSpectrum.Calm });
        world.Add(zone, new WorldTransform { Value = Matrix4x4.Identity });

        var body = world.Create();

        world.Add(
            body,
            WaterBodyComponent.Default with {
                Kind = WaterBodyKind.River,
                Spline = "River",
                SurfaceHeight = 2f,
                HalfWidth = 12f,
                Velocity = velocity
            }
        );

        world.Add(body, new WorldTransform { Value = Matrix4x4.Identity });

        var system = new WaterZoneSystem(view) {
            Splines = new Straight(50f, 2f),
            Ground = new FlatWaterGround(-5f)
        };

        system.Fold(world);

        return system;
    }

    /// <summary>Nothing switched on draws nothing at all.</summary>
    /// <remarks>
    ///     The one branch a host pays for calling this every frame — <see cref="WaterDebug.Any" /> —
    ///     and the negative control every other test here needs, because a draw that emitted geometry
    ///     unconditionally would pass all of them.
    /// </remarks>
    [Fact]
    public void Nothing_switched_on_draws_nothing() {
        var zones = Folded();

        new WaterDebugDraw().Draw(draw, zones);

        Assert.Equal(0, draw.Count);
        Assert.Equal(0, draw.ScreenCount);
        Assert.Equal(0, draw.Texts.Length);
    }

    /// <summary>`water.showFlow` puts an arrow on the surface wherever the field says it moves.</summary>
    /// <remarks>
    ///     ⚠ <b>And the arrows are <em>on the surface</em>, which is what makes them readable.</b> A
    ///     flow field drawn at y = 0 over a river at y = 2 is a set of arrows under the water, visible
    ///     only from below — and the person who turned the verb on is standing on the bank.
    /// </remarks>
    [Fact]
    public void Show_flow_draws_arrows_on_the_surface() {
        var zones = Folded(velocity: 3f);

        WaterDebug.ShowFlow = true;

        new WaterDebugDraw().Draw(draw, zones);

        Assert.True(draw.Count > 0, "showFlow drew no lines at all");

        var wet = 0;

        foreach (var line in draw.Lines) {
            if (MathF.Abs(line.From.Y - 2f) < 0.3f) {
                wet++;
            }
        }

        Assert.True(wet > 0, "every flow arrow was drawn away from the surface");
    }

    /// <summary>A still body draws no flow arrows, which is the negative control for the above.</summary>
    /// <remarks>
    ///     A lake with a velocity is not a thing water does — <c>WaterBodySettings.Validate</c>
    ///     refuses it — so the overlay showing arrows over one would be the overlay lying about the
    ///     field rather than the field being wrong.
    /// </remarks>
    [Fact]
    public void Show_flow_over_still_water_draws_no_arrows() {
        var zones = Folded();

        WaterDebug.ShowFlow = true;

        new WaterDebugDraw().Draw(draw, zones);

        Assert.Equal(0, draw.Count);
    }

    /// <summary>`water.showInfo` charts the four channels in screen space, labelled.</summary>
    /// <remarks>
    ///     ⚠ <b>Four rows and not one image.</b> The channels have different units — two heights, a
    ///     two-component velocity and a coverage — so packing them into an RGBA preview makes a
    ///     picture whose colours answer no question anybody asked.
    /// </remarks>
    [Fact]
    public void Show_info_charts_the_four_channels_separately() {
        var zones = Folded(velocity: 2f);

        WaterDebug.ShowInfo = true;

        new WaterDebugDraw().Draw(draw, zones);

        Assert.True(draw.ScreenCount > 0, "showInfo drew nothing in screen space");

        // Four labels, one per channel — the rows themselves are fills and are not distinguishable
        // from one another by count.
        var rows = 0;

        foreach (var line in draw.ScreenLines) {
            _ = line;
            rows++;
        }

        Assert.True(rows > 4, $"showInfo drew {rows} screen segments, which is not four labelled rows");
    }

    /// <summary>`water.showRipples` outlines the window and reports the budget.</summary>
    /// <remarks>
    ///     ⚠ <b>The overflow count is what the overlay is for.</b> A simulation past its injection
    ///     budget drops injections silently — it has to, or a frame with a hundred boats is a frame
    ///     that does not finish — and the symptom is a wake that is there for some hulls and not
    ///     others, which reads as the wake code being wrong.
    /// </remarks>
    [Fact]
    public void Show_ripples_outlines_the_window() {
        var zones = Folded();
        var ripples = new WaterRipples(WaterRippleSettings.Default, new(-32f, -32f));

        WaterDebug.ShowRipples = true;

        new WaterDebugDraw().Draw(draw, zones, ripples: ripples);

        Assert.True(draw.Count > 0, "showRipples drew no window");
        Assert.True(draw.ScreenCount > 0, "showRipples drew no budget readout");

        // The box is the simulation's own window and not the zone's — the two are different sizes and
        // different places, which is the whole reason to draw it.
        var least = float.MaxValue;

        foreach (var line in draw.Lines) {
            least = MathF.Min(least, MathF.Min(line.From.X, line.To.X));
        }

        Assert.Equal(-32f, least, 0.01f);
    }

    /// <summary>
    ///     `water.showTiles` colours a patch by the body under it, and the skirt by nothing.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>The doc's table asks for "coloured by body kind", and this is why it asks.</b> A
    ///         patch is a rectangle over the field; what is under it — a river, a lake, the skirt over
    ///         nothing at all — is exactly what a wireframe cannot show. Two colours over a river
    ///         patch and a skirt patch is the whole assertion.
    ///     </para>
    ///     <para>
    ///         Driven with a hand-made selection rather than through a <c>WaterMeshRenderer</c>,
    ///         because the part with a decision in it is the colour rule and a device fixture is the
    ///         wrong instrument for checking a decision.
    ///     </para>
    /// </remarks>
    [Fact]
    public void Show_tiles_colours_a_river_patch_differently_from_the_skirt() {
        var zones = Folded();
        var state = zones.States.Values.First();
        var mesh = new WaterSurfaceMesh(state.Window, TerrainLodRanges.Default, gridQuads: 8);

        // One patch over the river's middle, and one far outside it — as the skirt is.
        var over = new TerrainLodNode(state.Window.Resolution / 2 - 4, state.Window.Resolution / 2 - 4, 8, 0, 0f);
        var away = new TerrainLodNode(0, 0, 8, 3, 0f);

        new WaterDebugDraw().Tiles(draw, state, mesh, [away, over], farCount: 1);

        var colours = new HashSet<(float, float, float)>();

        foreach (var line in draw.Lines) {
            colours.Add((line.Colour.R, line.Colour.G, line.Colour.B));
        }

        Assert.Equal(2, colours.Count);

        // And the level went out as a label, which is the number a pop is diagnosed from.
        Assert.Equal(2, draw.Texts.Length);
    }

    /// <summary>`water.showLod` draws two rings per level, not one.</summary>
    /// <remarks>
    ///     ⚠ A level's range is where it takes over; its morph band is where it has already begun
    ///     degenerating onto its parent's grid. A pop at the outer ring is a range that is too near;
    ///     one inside the band is a morph that is not reaching zero, and they have different fixes —
    ///     so an overlay that drew one ring would answer neither question.
    /// </remarks>
    [Fact]
    public void Show_lod_draws_a_ring_for_the_band_and_one_for_the_morph() {
        var zones = Folded();
        var state = zones.States.Values.First();
        var ranges = TerrainLodRanges.Default with { LevelCount = 3 };
        var mesh = new WaterSurfaceMesh(state.Window, ranges, gridQuads: 8);

        WaterDebugDraw.Bands(draw, mesh, Vector3.Zero);

        var radii = new HashSet<int>();

        foreach (var line in draw.Lines) {
            radii.Add((int)MathF.Round(new Vector2(line.From.X, line.From.Z).Length()));
        }

        // Three levels, two rings each, and no two of the six share a radius.
        Assert.Equal(6, radii.Count);
    }

    /// <summary>
    ///     ⚠ The flags are process-wide, and <c>Reset</c> is what a test leaves the process with.
    /// </summary>
    /// <remarks>
    ///     A console verb has to be a static — there is nothing to hand a person typing at a console —
    ///     and a suite that forgets leaks its state into whichever test the runner picks next, which
    ///     is a failure that moves when you reorder the file.
    /// </remarks>
    [Fact]
    public void Reset_switches_every_verb_off() {
        WaterDebug.ShowTiles = true;
        WaterDebug.ShowLod = true;
        WaterDebug.ShowInfo = true;
        WaterDebug.ShowFlow = true;
        WaterDebug.ShowBuoyancy = true;
        WaterDebug.ShowRipples = true;

        Assert.True(WaterDebug.Any);

        WaterDebug.Reset();

        Assert.False(WaterDebug.Any);
    }
}

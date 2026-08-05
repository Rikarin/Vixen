// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;
using Vixen.Rendering.Water;
using Vixen.Water;
using Xunit;

namespace Vixen.Editor.Water.Tests;

/// <summary>The gesture's arithmetic, and the panel's derived numbers — [docs/plan/35 § W9].</summary>
public sealed class WaterEditTests {
    // --- The draw -----------------------------------------------------------

    /// <summary>Two clicks in the same place are one point.</summary>
    /// <remarks>
    ///     ⚠ A spline segment of no length has no tangent, so a body built from one has a boundary
    ///     walk that divides by zero. Refused at the gesture, because the person who did it is holding
    ///     the mouse.
    /// </remarks>
    [Fact]
    public void A_click_too_near_the_last_one_lays_nothing() {
        var edit = new WaterEdit { MinimumSpacing = 1f };

        Assert.True(edit.Add(new(0f, 0f, 0f)));
        Assert.False(edit.Add(new(0.3f, 0f, 0.3f)));
        Assert.True(edit.Add(new(4f, 0f, 0f)));

        Assert.Equal(2, edit.Points.Count);
    }

    /// <summary>The spacing is measured on the ground plane, not in three dimensions.</summary>
    /// <remarks>
    ///     ⚠ A river down a cliff has two points a metre apart horizontally and twenty vertically, and
    ///     a three-dimensional test would accept them — which is two control points at the same place
    ///     on the ground and a boundary that folds back on itself.
    /// </remarks>
    [Fact]
    public void The_spacing_is_horizontal() {
        var edit = new WaterEdit { MinimumSpacing = 1f };

        edit.Add(new(0f, 0f, 0f));

        Assert.False(edit.Add(new(0.2f, 50f, 0.2f)));
    }

    [Fact]
    public void Undo_takes_the_last_point_back_and_stops_at_empty() {
        var edit = new WaterEdit();

        edit.Add(new(0f, 0f, 0f));
        edit.Add(new(10f, 0f, 0f));

        Assert.True(edit.Undo());
        Assert.Single(edit.Points);

        Assert.True(edit.Undo());
        Assert.False(edit.Undo());
    }

    /// <summary>Clicking the first point again closes a lake — and never closes a river.</summary>
    /// <remarks>
    ///     ⚠ <b>The UI layer has no double click</b>: <c>PointerAction</c> is moves, presses and
    ///     releases, and a click count is a fact about time the event does not carry. So the gesture
    ///     every polygon tool offers second is the one offered first here.
    /// </remarks>
    [Fact]
    public void Clicking_the_first_point_closes_a_lake() {
        var edit = new WaterEdit { Kind = WaterBodyKind.Lake, CloseRadius = 2f };

        edit.Add(new(0f, 0f, 0f));
        edit.Add(new(20f, 0f, 0f));

        // Two points is not yet a lake, so coming back to the start is a point rather than a close.
        Assert.False(edit.ClosesAt(new(0.5f, 0f, 0.5f)));

        edit.Add(new(20f, 0f, 20f));

        Assert.True(edit.ClosesAt(new(0.5f, 0f, 0.5f)));
        Assert.False(edit.ClosesAt(new(9f, 0f, 9f)));
    }

    [Fact]
    public void A_river_never_closes_by_clicking() {
        var edit = new WaterEdit { Kind = WaterBodyKind.River, CloseRadius = 2f };

        edit.Add(new(0f, 0f, 0f));
        edit.Add(new(20f, 0f, 0f));
        edit.Add(new(0.2f, 0f, 0.2f));

        Assert.False(edit.ClosesAt(new(0.1f, 0f, 0.1f)));
    }

    // --- The profile handles ------------------------------------------------

    /// <summary>Both width handles edit the same number, and the sign is the difference.</summary>
    /// <remarks>
    ///     ⚠ A river's channel is symmetric about its centreline, so two handles is two grips on one
    ///     value. Two independent half-widths would be a second number to author and a river whose
    ///     centreline is not its centre.
    /// </remarks>
    [Fact]
    public void Dragging_either_bank_outward_widens_the_channel() {
        var edit = new WaterEdit();
        var profile = new WaterProfilePoint { HalfWidth = 5f, Depth = 2f };

        edit.Grab(WaterHandle.WidthRight, 0);
        Assert.Equal(8f, edit.Drag(profile, 3f).HalfWidth, 4);

        edit.Grab(WaterHandle.WidthLeft, 0);
        Assert.Equal(8f, edit.Drag(profile, -3f).HalfWidth, 4);
    }

    /// <summary>And neither can take it below zero.</summary>
    /// <remarks>
    ///     ⚠ A negative half-width inverts the containment test, so the body covers everywhere except
    ///     itself — the whole zone floods, and it reads as a renderer bug.
    /// </remarks>
    [Fact]
    public void A_width_drag_cannot_go_negative() {
        var edit = new WaterEdit();
        var profile = new WaterProfilePoint { HalfWidth = 1f, Depth = 2f };

        edit.Grab(WaterHandle.WidthRight, 0);

        Assert.Equal(0f, edit.Drag(profile, -50f).HalfWidth, 4);
    }

    [Fact]
    public void Dragging_the_depth_handle_down_deepens_the_bed() {
        var edit = new WaterEdit();
        var profile = new WaterProfilePoint { HalfWidth = 5f, Depth = 2f };

        edit.Grab(WaterHandle.Depth, 0);

        Assert.Equal(5f, edit.Drag(profile, -3f).Depth, 4);
        Assert.Equal(0f, edit.Drag(profile, 50f).Depth, 4);
    }

    [Fact]
    public void Holding_nothing_changes_nothing() {
        var edit = new WaterEdit();
        var profile = new WaterProfilePoint { HalfWidth = 5f, Depth = 2f };

        edit.Release();

        Assert.Equal(profile, edit.Drag(profile, 100f));
    }

    /// <summary>The handles sit across the curve, in the curve's own frame.</summary>
    /// <remarks>
    ///     ⚠ <b>The side is the curve's and not world X.</b> A river that bends would otherwise have
    ///     its handles cross its own bank halfway round, and dragging one would widen the channel in
    ///     the wrong direction — which is what makes a viewport handle worse than a number field.
    /// </remarks>
    [Fact]
    public void The_handles_straddle_the_curve() {
        // A river running along +Z, so its "side" is world X.
        var river = new WaterBody(
            WaterBodyKind.River,
            new Spline([
                SplinePoint.Smooth(new(0f, 10f, 0f), new(0f, 0f, 20f)),
                SplinePoint.Smooth(new(0f, 10f, 20f), new(0f, 0f, 20f)),
                SplinePoint.Smooth(new(0f, 10f, 40f), new(0f, 0f, 20f))
            ]),
            defaults: new() { HalfWidth = 6f, Depth = 3f }
        );

        var (left, right, depth) = WaterEdit.HandlesOf(river, 1);

        // Control point 1 is at (0, 10, 20), so all three handles sit on that station of the curve.
        Assert.Equal(20f, left.Z, 2);
        Assert.Equal(20f, right.Z, 2);
        Assert.Equal(20f, depth.Z, 2);

        // Six metres either side of the centreline — symmetric about it, because a channel is.
        Assert.Equal(12f, MathF.Abs(left.X - right.X), 3);
        Assert.Equal(0f, left.X + right.X, 3);

        // And three metres below it.
        Assert.Equal(7f, depth.Y, 3);
    }

    // --- The zone panel's derived numbers -----------------------------------

    /// <summary>
    ///     A resolution is meaningless and a metre per texel is not, so the panel derives both.
    /// </summary>
    /// <remarks>
    ///     § D3: "a number an author types into <c>render_target_resolution</c> with no idea what it
    ///     buys is how the reference gets configured wrongly". The arithmetic is the kernel's, so the
    ///     panel cannot be right about a configuration the renderer refuses.
    /// </remarks>
    [Fact]
    public void The_zone_panel_derives_what_a_resolution_buys() {
        var settings = new WaterZoneSettings { Extent = 512f, Resolution = 257 };
        var facts = settings.Facts().ToDictionary(fact => fact.Label, fact => fact.Value);

        // 512 m over 257 samples is two metres exactly — the "power of two plus one" the whole zone
        // is sized around.
        Assert.Equal("2 m", facts["Metres per texel"]);

        // 257² texels × 4 channels × 4 bytes.
        Assert.Equal($"{257f * 257f * 4f * 4f / (1024f * 1024f):0.##} MB", facts["Info texture"]);

        Assert.Equal("66,049", facts["Vertices, full window"]);
        Assert.Contains("full precision", facts["Height quantum"]);
        Assert.True(facts.ContainsKey("Maximum amplitude"));
    }

    /// <summary>Half precision says what it costs in metres, rather than leaving it to be found.</summary>
    /// <remarks>
    ///     ⚠ § "The zone panel": a half float over a large zone is a <em>quantised</em> surface, and
    ///     the panel says so rather than leaving somebody to discover a stepped horizon.
    /// </remarks>
    [Fact]
    public void Half_precision_states_its_quantum() {
        var settings = new WaterZoneSettings { Precision = WaterInfoPrecision.Half };
        var facts = settings.Facts().ToDictionary(fact => fact.Label, fact => fact.Value);

        Assert.Contains("half precision", facts["Height quantum"]);
        Assert.DoesNotContain("exact", facts["Height quantum"]);
    }

    /// <summary>The panel refuses exactly what the renderer would, because it asks the kernel.</summary>
    [Fact]
    public void The_zone_panel_refuses_a_snap_grid_that_is_not_a_whole_texel() {
        var settings = new WaterZoneSettings { Extent = 512f, Resolution = 257, CoarsestTexel = 4f };

        Assert.Null(settings.Validate());

        // 3 m is not a whole number of the 2 m texels, which is the shoreline crawl § D3 warns about.
        settings.CoarsestTexel = 3f;

        Assert.NotNull(settings.Validate());
    }

    /// <summary>A closed body with a velocity is refused rather than silently ignored.</summary>
    /// <remarks>
    ///     ⚠ It is a number an author typed and will look for on screen. Dropping it quietly is worse
    ///     than saying that a lake has no direction to flow in.
    /// </remarks>
    [Fact]
    public void A_lake_with_a_velocity_is_refused() {
        var body = new WaterBodySettings { Kind = WaterBodyKind.Lake, Velocity = 2f };

        Assert.NotNull(body.Validate());

        body.Kind = WaterBodyKind.River;

        Assert.Null(body.Validate());
    }

    /// <summary>And the settings become the component a scene actually carries.</summary>
    [Fact]
    public void The_settings_become_the_scene_component() {
        var body = new WaterBodySettings {
            Kind = WaterBodyKind.River,
            HalfWidth = 7f,
            Depth = 2.5f,
            Velocity = 1.5f,
            ShoreFalloff = 3f
        };

        var component = body.ComponentFor("Brook");

        Assert.Equal("Brook", component.Spline);
        Assert.Equal(WaterBodyKind.River, component.Kind);
        Assert.Equal(7f, component.HalfWidth, 4);
        Assert.Equal(1.5f, component.Velocity, 4);
        Assert.Equal(3f, component.ShoreFalloff, 4);

        // And the profile the kernel reads is the same numbers.
        Assert.Equal(7f, component.Profile.HalfWidth, 4);
        Assert.Equal(2.5f, component.Profile.Depth, 4);
    }

    /// <summary>A zone's settings become a component the fold can read, sea state included.</summary>
    [Fact]
    public void The_zone_settings_become_the_scene_component() {
        var settings = new WaterZoneSettings {
            Extent = 256f,
            Resolution = 129,
            WindSpeed = 12f,
            WaveCount = WaterWaveCount.ThirtyTwo,
            AttenuationDepth = 5f
        };

        var component = settings.Component;

        Assert.Equal(256f, component.Extent, 4);
        Assert.Equal(129, component.Resolution);
        Assert.Equal(12f, component.Waves.WindSpeed, 4);
        Assert.Equal(WaterWaveCount.ThirtyTwo, component.Waves.Count);
        Assert.Equal(5f, component.AttenuationDepth, 4);

        // And the zone the kernel sizes its texture from is the one the panel showed.
        Assert.Equal(settings.Zone.MetresPerTexel, component.Zone.MetresPerTexel, 5);
    }
}

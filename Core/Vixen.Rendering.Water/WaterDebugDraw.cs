// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;
using Vixen.Engine.Diagnostics;
using Vixen.Terrain;
using Vixen.Water;

namespace Vixen.Rendering.Water;

/// <summary>
///     What <see cref="WaterDebug" />'s five world-space verbs actually draw.
/// </summary>
/// <remarks>
///     <para>
///         <b>[35 § Part 2 § Debugging](../../docs/plan/35-water.md#debugging), and the half that was
///         owed.</b> <c>stat water</c> and <c>stat watermesh</c> needed only numbers somebody already
///         publishes and shipped with the mode; these needed lines, and water had no seam into a
///         viewport line pass. It turns out it did not need one — <see cref="DebugDraw" /> is exactly
///         that seam, and it is the accumulator rather than the renderer, so this assembly draws into
///         it without knowing what a line pass is.
///     </para>
///     <para>
///         ⚠ <b>Five and not six.</b> <c>water.showBuoyancy</c> lives in <c>Vixen.Water.Physics</c>,
///         because the pontoons and the forces are that assembly's and this one must not reference it
///         — § D1 puts the physics join in its own assembly precisely so that nothing linking Jolt is
///         linked by a renderer. The flag is here because the console verb is; the drawing is where
///         the data is.
///     </para>
///     <para>
///         ⚠ <b>Every draw reads the frame's own answer rather than recomputing one.</b> The patches
///         come from <see cref="WaterSurfacePass.Selected" />, the surface from the zone's
///         <see cref="WaterQuery" />, the window from the field that was rasterised. A debug overlay
///         that descended the quadtree again, or evaluated the waves at its own time, would agree with
///         the frame until the moment it stopped — and that moment is exactly the bug somebody turned
///         the overlay on to find.
///     </para>
///     <para>
///         <b>Nothing here allocates per line.</b> <see cref="DebugDraw" /> appends into three lists it
///         owns; what this adds is a bounded walk over the patches a frame actually drew.
///     </para>
/// </remarks>
public sealed class WaterDebugDraw {
    /// <summary>How many samples across a zone's window the flow and info grids take.</summary>
    /// <remarks>
    ///     ⚠ <b>A grid over the window and not over the texels.</b> A 257² field is sixty-six thousand
    ///     arrows, which is a frame that does not finish and a picture nobody can read. Sixteen is
    ///     enough to see a river's direction reverse, which is what the overlay is for.
    /// </remarks>
    public int GridSamples { get; set; } = 16;

    /// <summary>How long an arrow one metre a second of flow draws, in metres.</summary>
    public float FlowScale { get; set; } = 1.5f;

    /// <summary>How far above the surface the overlays are lifted, in metres.</summary>
    /// <remarks>
    ///     ⚠ <b>Not zero.</b> A line drawn exactly on the surface z-fights with it along its whole
    ///     length, which reads as the overlay flickering rather than as the overlay being on the
    ///     surface. A few centimetres is invisible at any distance a person reads these from.
    /// </remarks>
    public float Lift { get; set; } = 0.05f;

    /// <summary>Where the info channels are charted, in pixels from the top-left.</summary>
    public Vector2 ChartOrigin { get; set; } = new(16f, 220f);

    /// <summary>How large each channel's row is, in pixels.</summary>
    public Vector2 ChartSize { get; set; } = new(256f, 24f);

    /// <summary>Draws whichever of the five verbs are switched on.</summary>
    /// <param name="into">Where the geometry goes.</param>
    /// <param name="zones">The fold, which is where the fields and the bodies are.</param>
    /// <param name="mesh">The surface node, or null if the frame draws no surface.</param>
    /// <param name="ripples">A ripple simulation to outline, or null.</param>
    /// <exception cref="ArgumentNullException"><paramref name="into" /> or <paramref name="zones" /> is null.</exception>
    /// <remarks>
    ///     ⚠ <b>One branch when everything is off</b> — <see cref="WaterDebug.Any" /> — because this is
    ///     called once a frame from a host that has no idea whether anybody is looking.
    /// </remarks>
    public void Draw(
        DebugDraw into,
        WaterZoneSystem zones,
        WaterMeshRenderer? mesh = null,
        WaterRipples? ripples = null
    ) {
        ArgumentNullException.ThrowIfNull(into);
        ArgumentNullException.ThrowIfNull(zones);

        if (!WaterDebug.Any) {
            return;
        }

        if (mesh is not null && (WaterDebug.ShowTiles || WaterDebug.ShowLod)) {
            Patches(into, zones, mesh);
        }

        if (WaterDebug.ShowFlow) {
            Flow(into, zones);
        }

        if (WaterDebug.ShowInfo) {
            Info(into, zones);
        }

        if (WaterDebug.ShowRipples && ripples is not null) {
            Ripples(into, ripples);
        }
    }

    /// <summary>`water.showTiles` and `water.showLod`: the patches a frame drew, and their bands.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>Coloured by body kind, which is what the doc's table asks for and is the reason
    ///         the overlay is worth having.</b> A patch is a rectangle over the field, and what is
    ///         under it — a river, a lake, the far skirt over nothing at all — is the thing you cannot
    ///         see from the wireframe. The kind is the highest-priority body whose boundary contains
    ///         the patch's centre; a patch over open water outside every body is the skirt's colour.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The bounds are the selector's own, amplitude included.</b> Drawing the flat
    ///         rectangle instead would hide the one thing the box is for: a node bounded by its rest
    ///         height is culled with a crest in front of the camera, and the box is where you would
    ///         see that the crest was not inside it.
    ///     </para>
    /// </remarks>
    void Patches(DebugDraw into, WaterZoneSystem zones, WaterMeshRenderer mesh) {
        foreach (var draw in mesh.Draws) {
            if (!zones.States.TryGetValue(draw.Entity, out var state) || state.Field is null) {
                continue;
            }

            if (WaterDebug.ShowTiles) {
                Tiles(into, state, draw.Mesh, draw.Pass.Selected, draw.Pass.FarPatches);
            }

            if (WaterDebug.ShowLod) {
                Bands(into, draw.Mesh, zones.View.Position);
            }
        }
    }

    /// <summary>Draws one zone's patches, coloured by what is under each.</summary>
    /// <param name="into">Where the geometry goes.</param>
    /// <param name="state">The zone, whose field and bodies decide the colours.</param>
    /// <param name="mesh">Its quadtree, which turns a node into world coordinates.</param>
    /// <param name="selected">The patches — the skirt's first, then the window's.</param>
    /// <param name="farCount">How many of them are the skirt's.</param>
    /// <exception cref="ArgumentNullException">Any reference argument is null.</exception>
    /// <remarks>
    ///     ⚠ <b>Public and taking the patches rather than reading them off a renderer, so it is
    ///     testable without a device.</b> The colour rule is the part with a decision in it — highest
    ///     priority wins, skirt is its own colour — and a device fixture is the wrong instrument for
    ///     checking a decision. It also lets a tool draw a selection it produced itself.
    /// </remarks>
    public void Tiles(
        DebugDraw into,
        WaterZoneState state,
        WaterSurfaceMesh mesh,
        ReadOnlySpan<TerrainLodNode> selected,
        int farCount
    ) {
        ArgumentNullException.ThrowIfNull(into);
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(mesh);

        for (var index = 0; index < selected.Length; index++) {
            Tile(into, mesh, state, in selected[index], skirt: index < farCount);
        }
    }

    void Tile(DebugDraw into, WaterSurfaceMesh mesh, WaterZoneState state, in TerrainLodNode node, bool skirt) {
        var low = mesh.GroundOf(in node, 0, 0);
        var high = mesh.GroundOf(in node, mesh.GridQuads, mesh.GridQuads);
        var centre = (low + high) * 0.5f;

        var height = state.Field?.Sample(centre).SurfaceHeight ?? 0f;
        var colour = skirt ? SkirtColour : ColourOf(state, centre);

        into.Box(
            new(new(low.X, height - mesh.MaximumAmplitude, low.Y), new(high.X, height + mesh.MaximumAmplitude, high.Y)),
            colour
        );

        // The level as a label, at the patch's centre — which is the number a pop is diagnosed from.
        into.Text(new(centre.X, height + Lift, centre.Y), node.Level.ToString(), colour, 0.35f * (high.X - low.X) * 0.05f);
    }

    /// <summary>Draws the morph bands as rings about a point — where a pop is diagnosed.</summary>
    /// <param name="into">Where the geometry goes.</param>
    /// <param name="mesh">The quadtree, whose ranges are the radii.</param>
    /// <param name="eye">Where the descent was run from.</param>
    /// <exception cref="ArgumentNullException"><paramref name="into" /> or <paramref name="mesh" /> is null.</exception>
    /// <remarks>
    ///     ⚠ <b>Two rings per level and not one.</b> A level's range is where it takes over; its morph
    ///     band is where it has already begun degenerating onto its parent's grid. A pop that happens
    ///     at the outer ring is a range that is too near; one that happens inside the band is a morph
    ///     that is not reaching zero, and they have different fixes.
    /// </remarks>
    public static void Bands(DebugDraw into, WaterSurfaceMesh mesh, Vector3 eye) {
        ArgumentNullException.ThrowIfNull(into);
        ArgumentNullException.ThrowIfNull(mesh);

        var ranges = mesh.Ranges;

        for (var level = 0; level < ranges.LevelCount; level++) {
            var colour = LevelColour(level);

            // The band's outer edge, where the next level takes over, and the radius at which this
            // one has started morphing onto it. See the remarks for why both.
            into.Circle(eye, Vector3.UnitY, ranges.MorphEndOf(level), colour);
            into.Circle(eye, Vector3.UnitY, ranges.MorphStartOf(level), new(colour.R, colour.G, colour.B, 0.4f));
        }
    }

    /// <summary>`water.showFlow`: the flow on the surface, and each body's own velocity.</summary>
    /// <remarks>
    ///     ⚠ <b>Two things drawn, because they answer different questions.</b> The grid arrows are the
    ///     <em>field's</em> flow — what a boat is actually dragged by — and the spline arrows are what
    ///     the body was authored with. A river whose grid arrows point the wrong way with the spline
    ///     arrows right is a rasterisation bug; both wrong is an authored velocity on a closed body,
    ///     which is a thing water does not do and <c>WaterBodySettings.Validate</c> refuses.
    /// </remarks>
    void Flow(DebugDraw into, WaterZoneSystem zones) {
        var samples = Math.Max(GridSamples, 2);

        foreach (var (entity, component) in zones.Zones) {
            if (!zones.States.TryGetValue(entity, out var state) || state.Field is null) {
                continue;
            }

            var window = state.Window;
            var step = window.Extent / (samples - 1);

            for (var z = 0; z < samples; z++) {
                for (var x = 0; x < samples; x++) {
                    var at = window.Origin + new Vector2(x * step, z * step);
                    var sample = state.Field.Sample(at);

                    if (sample.Coverage <= 0f || sample.Flow == Vector2.Zero) {
                        continue;
                    }

                    var from = new Vector3(at.X, sample.SurfaceHeight + Lift, at.Y);
                    var to = from + new Vector3(sample.Flow.X, 0f, sample.Flow.Y) * FlowScale;

                    into.Arrow(from, to, FlowColour);
                }
            }

            // And what each body says about itself, along its own curve.
            foreach (var body in state.Bodies) {
                Velocity(into, body);
            }

            _ = component;
        }
    }

    void Velocity(DebugDraw into, WaterBody body) {
        const int Steps = 12;

        for (var index = 0; index < Steps; index++) {
            var t = index / (float)(Steps - 1);
            var profile = body.ProfileAt(t);

            if (profile.Velocity == 0f) {
                continue;
            }

            var point = body.Spline.Evaluate(t);
            var tangent = body.Spline.Tangent(t);

            if (tangent.LengthSquared() <= 0f) {
                continue;
            }

            var forward = Vector3.Normalize(tangent);

            into.Arrow(
                point + new Vector3(0f, Lift, 0f),
                point + new Vector3(0f, Lift, 0f) + (forward * profile.Velocity * FlowScale),
                SplineColour
            );
        }
    }

    /// <summary>`water.showInfo`: the field's four channels, charted separately.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>Four rows and not one image, which is what "individually" in the doc's table
    ///         means.</b> The channels have different units — two heights, a two-component velocity
    ///         and a coverage — and packing them into an RGBA preview makes a picture whose colours
    ///         are the answer to no question anybody asked. A row per channel, normalised over what is
    ///         actually in the window, is readable.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Normalised over the window rather than over a fixed range</b>, because a lake at
    ///         y = 400 and a lake at y = 0 are the same picture and a fixed range shows one of them as
    ///         flat white. The trade is that two frames are not comparable, which is the right way
    ///         round for an overlay somebody is looking at now.
    ///     </para>
    /// </remarks>
    void Info(DebugDraw into, WaterZoneSystem zones) {
        var samples = Math.Max(GridSamples, 2);
        var row = 0;

        foreach (var (entity, _) in zones.Zones) {
            if (!zones.States.TryGetValue(entity, out var state) || state.Field is null) {
                continue;
            }

            var window = state.Window;
            var step = window.Extent / (samples - 1);
            var cell = new Vector2(ChartSize.X / samples, ChartSize.Y);

            for (var channel = 0; channel < 4; channel++) {
                var top = ChartOrigin + new Vector2(0f, row * (ChartSize.Y + 4f));

                into.ScreenText(top - new Vector2(0f, 10f), ChannelNames[channel], ChannelColours[channel], 8f);

                // Two passes: the range first, so the row is normalised over what is in the window.
                var least = float.MaxValue;
                var most = float.MinValue;

                for (var x = 0; x < samples; x++) {
                    var value = Channel(state, window.Origin + new Vector2(x * step, window.Extent * 0.5f), channel);

                    least = MathF.Min(least, value);
                    most = MathF.Max(most, value);
                }

                var span = MathF.Max(most - least, 1e-4f);

                for (var x = 0; x < samples; x++) {
                    var value = Channel(state, window.Origin + new Vector2(x * step, window.Extent * 0.5f), channel);
                    var level = Math.Clamp((value - least) / span, 0f, 1f);
                    var colour = ChannelColours[channel];

                    into.ScreenFill(
                        top + new Vector2(x * cell.X, 0f),
                        cell,
                        new(colour.R * level, colour.G * level, colour.B * level, 1f),
                        spacing: 3f
                    );
                }

                row++;
            }
        }
    }

    static float Channel(WaterZoneState state, Vector2 at, int channel) {
        var sample = state.Field!.Sample(at);

        return channel switch {
            0 => sample.SurfaceHeight,
            1 => sample.Flow.X,
            2 => sample.Flow.Y,
            _ => sample.GroundHeight
        };
    }

    /// <summary>`water.showRipples`: the window's bounds, and the injections against the budget.</summary>
    /// <remarks>
    ///     ⚠ <b>The overflow count is what the overlay is for.</b> A simulation past its injection
    ///     budget drops injections silently — it has to, or a frame with a hundred boats is a frame
    ///     that does not finish — and the symptom is a wake that is there for some hulls and not
    ///     others, which reads as the wake code being wrong.
    /// </remarks>
    void Ripples(DebugDraw into, WaterRipples ripples) {
        var extent = ripples.Settings.Extent;
        var low = ripples.Origin;
        var high = low + new Vector2(extent);

        into.Box(new(new(low.X, -Lift, low.Y), new(high.X, Lift, high.Y)), RippleColour);

        into.ScreenText(
            ChartOrigin - new Vector2(0f, 32f),
            $"ripples {ripples.Injections}/{ripples.Settings.InjectionBudget}  dropped {ripples.Overflowed}",
            ripples.Overflowed > 0 ? Bad : RippleColour,
            10f
        );
    }

    /// <summary>The colour of the body under a place, by kind — the doc's table's own request.</summary>
    /// <remarks>
    ///     <para>
    ///         Highest priority first, because that is what the field composited: a river running into
    ///         a lake is drawn as the river where the river won, which is the same rule the rasteriser
    ///         used and therefore the same answer.
    ///     </para>
    ///     <para>
    ///         ⚠ <b><see cref="WaterBody.Sample" /> and not <see cref="WaterBody.Contains" />, and
    ///         the difference is the whole overlay.</b> <c>Contains</c> is an even-odd test on a
    ///         closed boundary and answers <see langword="false" /> for <em>every</em> open body — so
    ///         a rule written on it colours every river as the skirt, which is precisely the case the
    ///         verb exists for. The contribution is the function the field itself was rasterised from,
    ///         so the colours agree with the water by construction.
    ///     </para>
    /// </remarks>
    static Color4 ColourOf(WaterZoneState state, Vector2 at) {
        var best = int.MinValue;
        var colour = SkirtColour;

        foreach (var body in state.Bodies) {
            if (body.Priority < best || body.Sample(at).Coverage <= 0f) {
                continue;
            }

            best = body.Priority;
            colour = KindColour(body.Kind);
        }

        return colour;
    }

    static Color4 KindColour(WaterBodyKind kind) =>
        kind switch {
            WaterBodyKind.Ocean => new(0.2f, 0.4f, 1f, 1f),
            WaterBodyKind.Lake => new(0.2f, 0.9f, 0.9f, 1f),
            WaterBodyKind.River => new(0.3f, 1f, 0.4f, 1f),
            WaterBodyKind.Island => new(1f, 0.6f, 0.2f, 1f),
            _ => new(0.8f, 0.8f, 0.8f, 1f)
        };

    /// <summary>A level's colour, cycling so that neighbouring levels never share one.</summary>
    static Color4 LevelColour(int level) =>
        (level % 5) switch {
            0 => new(1f, 0.2f, 0.2f, 1f),
            1 => new(1f, 0.7f, 0.1f, 1f),
            2 => new(0.9f, 0.9f, 0.2f, 1f),
            3 => new(0.3f, 0.9f, 0.4f, 1f),
            _ => new(0.4f, 0.5f, 1f, 1f)
        };

    static readonly Color4 SkirtColour = new(0.45f, 0.45f, 0.55f, 1f);
    static readonly Color4 FlowColour = new(0.3f, 0.8f, 1f, 1f);
    static readonly Color4 SplineColour = new(1f, 0.9f, 0.3f, 1f);
    static readonly Color4 RippleColour = new(0.8f, 0.4f, 1f, 1f);
    static readonly Color4 Bad = new(1f, 0.3f, 0.3f, 1f);

    static readonly string[] ChannelNames = ["surface", "flow x", "flow z", "ground"];

    static readonly Color4[] ChannelColours = [
        new(0.4f, 0.7f, 1f, 1f),
        new(1f, 0.5f, 0.4f, 1f),
        new(0.5f, 1f, 0.5f, 1f),
        new(0.8f, 0.7f, 0.4f, 1f)
    ];
}

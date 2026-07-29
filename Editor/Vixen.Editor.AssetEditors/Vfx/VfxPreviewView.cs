// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using Vixen.Core.Mathematics;
using Vixen.Ui;
using Vixen.Vfx;

namespace Vixen.Editor.AssetEditors.Vfx;

/// <summary>The effect, running, drawn as the particles it actually has.</summary>
/// <remarks>
///     <para>
///         <b>The simulation is real and the picture is honest about being a projection.</b> What
///         steps is <see cref="VfxSystem" /> — the class a game runs, over the
///         <c>VfxCompiledGraph</c> the document just compiled — so what an author is looking at is
///         their graph's behaviour rather than a mock of it. What draws is this control, projecting
///         each particle's position with a fixed orbit camera and filling a disc of its size in its
///         colour.
///     </para>
///     <para>
///         ⚠ <b>A drawn projection rather than the scene viewport, and the reason is doc 20's own.</b>
///         Particles are drawn by a material, the editor's viewport is a tool renderer with no
///         materials, and Phase 7's wiring is what closes that — the same dependency the picking
///         stage and three view modes are waiting on. A preview that borrowed the tool renderer
///         would be a second thing to rewrite when the real one lands and would still not show a
///         textured sprite. Projecting here costs one transform per particle, needs no device, and
///         is therefore the only form of this that a headless test can assert.
///     </para>
///     <para>
///         ⚠ <b>Nothing is allocated per particle per frame.</b> A preview of ten thousand particles
///         at sixty frames a second is the one control in this assembly where that would matter, so
///         the projection writes into a reused array and the discs are drawn straight from it.
///     </para>
/// </remarks>
public sealed class VfxPreviewView : UiElement {
    Vector3[] projected = [];
    float[] radii = [];

    /// <inheritdoc />
    protected override string TagName => "vfx-preview";

    /// <summary>The system to draw, or <see langword="null" /> for nothing.</summary>
    public VfxSystem? System { get; set; }

    /// <summary>How the effect is looked at, in radians around Y.</summary>
    public float Yaw { get; set; } = 0.6f;

    /// <summary>And how far above it, in radians.</summary>
    public float Pitch { get; set; } = 0.35f;

    /// <summary>How far away the camera is, in world units.</summary>
    public float Distance { get; set; } = 8f;

    /// <summary>Whether the effect is being stepped.</summary>
    public bool IsPlaying { get; set; } = true;

    /// <summary>How much simulated time has passed since the preview last restarted.</summary>
    public float Elapsed { get; private set; }

    /// <summary>Steps the simulation, if there is one and it is playing.</summary>
    /// <param name="delta">How long the last frame took.</param>
    /// <remarks>
    ///     ⚠ <b>Clamped, and that is not tidiness.</b> A step is a real integration, and the frame
    ///     after a breakpoint, a modal dialog or a content import is arbitrarily long — an effect
    ///     handed a two-second delta jumps its particles to somewhere no author authored and looks
    ///     like the graph being wrong.
    /// </remarks>
    public void Step(TimeSpan delta) {
        if (System is not { } system || !IsPlaying) {
            return;
        }

        var step = Math.Clamp((float) delta.TotalSeconds, 0f, MaximumStep);

        if (step <= 0f) {
            return;
        }

        system.Step(step);
        Elapsed += step;
    }

    /// <summary>Starts the effect again from nothing.</summary>
    public void Restart() {
        System?.Reset();
        Elapsed = 0f;
    }

    /// <summary>The longest step the preview will take, in seconds.</summary>
    const float MaximumStep = 1f / 20f;

    /// <inheritdoc />
    protected override void OnDraw(DrawContext context) {
        base.OnDraw(context);

        var bounds = context.Bounds;

        if (bounds.Width <= 2f || bounds.Height <= 2f) {
            return;
        }

        Grid(context, bounds);

        if (System is not { Count: > 0 } system) {
            return;
        }

        Project(system, bounds);

        // ⚠ Back to front, so the near particles cover the far ones. The buffer is in spawn order
        // and a preview that drew it that way would put the oldest particles in front for the whole
        // of every effect that moves towards the camera.
        var order = Order(system.Count);

        var positions = system.Particles.Position;
        var colours = system.Particles.Has(VfxAttribute.Colour) ? system.Particles.Colour : default;

        for (var index = 0; index < order.Length; index++) {
            var particle = order[index];

            if (particle >= positions.Length) {
                continue;
            }

            var point = projected[particle];

            if (point.Z <= 0f) {
                continue;
            }

            var radius = Math.Clamp(radii[particle], 1f, 32f);

            var colour = colours.Length > particle
                ? new Color4(colours[particle].X, colours[particle].Y, colours[particle].Z, colours[particle].W)
                : new Color4(1f, 0.82f, 0.45f, 1f);

            context.FillRectangle(
                new Rectangle(point.X - radius, point.Y - radius, radius * 2f, radius * 2f),
                colour,
                radius
            );
        }
    }

    int[] order = [];

    /// <summary>The particle indices, sorted far to near.</summary>
    ReadOnlySpan<int> Order(int count) {
        if (order.Length < count) {
            order = new int[Math.Max(count, 64)];
        }

        for (var index = 0; index < count; index++) {
            order[index] = index;
        }

        var slice = order.AsSpan(0, count);
        var depths = projected;

        // A comparison sort over indices rather than over the particles: the buffer belongs to the
        // simulation and reordering it would change what the next step integrates.
        slice.Sort((left, right) => depths[right].Z.CompareTo(depths[left].Z));

        return slice;
    }

    /// <summary>Projects every live particle into the control's box.</summary>
    void Project(VfxSystem system, Rectangle bounds) {
        var count = system.Count;

        if (projected.Length < count) {
            projected = new Vector3[Math.Max(count, 64)];
            radii = new float[Math.Max(count, 64)];
        }

        var positions = system.Particles.Position;
        var sizes = system.Particles.Has(VfxAttribute.Size) ? system.Particles.Size : default;

        var cosYaw = MathF.Cos(Yaw);
        var sinYaw = MathF.Sin(Yaw);
        var cosPitch = MathF.Cos(Pitch);
        var sinPitch = MathF.Sin(Pitch);

        // One field of view for both axes, scaled by the shorter one, so the picture does not
        // stretch when the panel is docked into a wide strip.
        var scale = Math.Min(bounds.Width, bounds.Height) * 0.5f;
        var centre = new Vector2(bounds.X + (bounds.Width * 0.5f), bounds.Y + (bounds.Height * 0.5f));

        for (var index = 0; index < count && index < positions.Length; index++) {
            var world = positions[index];

            var x = (world.X * cosYaw) - (world.Z * sinYaw);
            var z = (world.X * sinYaw) + (world.Z * cosYaw);
            var y = (world.Y * cosPitch) - (z * sinPitch);
            var depth = (world.Y * sinPitch) + (z * cosPitch) + Distance;

            if (depth <= 0.05f) {
                projected[index] = new(0f, 0f, 0f);
                continue;
            }

            var perspective = scale / depth;

            projected[index] = new(centre.X + (x * perspective), centre.Y - (y * perspective), depth);
            radii[index] = (sizes.Length > index ? Math.Max(sizes[index], 0.01f) : 0.1f) * perspective * 0.5f;
        }
    }

    readonly PathBuilder grid = new();

    /// <summary>A ground plane, so the effect has somewhere to be.</summary>
    void Grid(DrawContext context, Rectangle bounds) {
        grid.Clear();

        var centre = new Vector2(bounds.X + (bounds.Width * 0.5f), bounds.Y + (bounds.Height * 0.5f));
        var scale = Math.Min(bounds.Width, bounds.Height) * 0.5f;

        var cosYaw = MathF.Cos(Yaw);
        var sinYaw = MathF.Sin(Yaw);
        var cosPitch = MathF.Cos(Pitch);
        var sinPitch = MathF.Sin(Pitch);

        for (var line = -GridExtent; line <= GridExtent; line++) {
            Segment(new(line, 0f, -GridExtent), new(line, 0f, GridExtent));
            Segment(new(-GridExtent, 0f, line), new(GridExtent, 0f, line));
        }

        context.Stroke(grid, new Color4(1f, 1f, 1f, 0.08f), 1f);

        void Segment(Vector3 from, Vector3 to) {
            if (!Screen(from, out var a) || !Screen(to, out var b)) {
                return;
            }

            grid.MoveTo(a).LineTo(b);
        }

        bool Screen(Vector3 world, out Vector2 point) {
            var x = (world.X * cosYaw) - (world.Z * sinYaw);
            var z = (world.X * sinYaw) + (world.Z * cosYaw);
            var y = (world.Y * cosPitch) - (z * sinPitch);
            var depth = (world.Y * sinPitch) + (z * cosPitch) + Distance;

            if (depth <= 0.05f) {
                point = default;
                return false;
            }

            var perspective = scale / depth;

            point = new(centre.X + (x * perspective), centre.Y - (y * perspective));
            return true;
        }
    }

    /// <summary>How far the ground plane goes, in metres either way.</summary>
    const int GridExtent = 4;

    /// <summary>What the readout under the preview says.</summary>
    /// <returns>The line.</returns>
    public string Readout() =>
        System is not { } system
            ? "Nothing compiled."
            : string.Create(
                CultureInfo.InvariantCulture,
                $"{system.Count} / {system.Graph.Capacity} particles · {Elapsed:0.0} s"
            );
}

// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;

namespace Vixen.Water;

/// <summary>The shape and the limits of a ripple simulation.</summary>
/// <remarks>
///     <para>
///         <b>Every one of these is a bound, and that is the point</b> —
///         [35 § D12](../../docs/plan/35-water.md#d12-ripples-are-a-sliding-window-height-field-and-they-are-displacement-not-geometry).
///         § Risks calls this "an unbounded feature wearing a bounded name": a fixed resolution, a
///         fixed step and an explicit injection budget are what keep it from becoming a frame-time
///         cliff in a busy scene, and the moment somebody proposes making the window adaptive it has
///         failed the document's own left-column test.
///     </para>
/// </remarks>
public readonly record struct WaterRippleSettings {
    /// <summary>How wide and deep the window is, in metres.</summary>
    public float Extent { get; init; }

    /// <summary>How many texels along each axis.</summary>
    public int Resolution { get; init; }

    /// <summary>How fast a disturbance travels across the field, in metres a second.</summary>
    /// <remarks>
    ///     ⚠ <b>Bounded by the grid, not by taste.</b> An explicit wave-equation step is stable only
    ///     while <c>c·Δt ≤ Δx/√2</c>; past that the field does not merely look wrong, it grows without
    ///     limit in a few dozen steps and every consumer downstream reads a NaN.
    ///     <see cref="MaximumSpeed" /> is that limit, and <see cref="Validate" /> refuses above it.
    /// </remarks>
    public float Speed { get; init; }

    /// <summary>How fast a disturbance dies away, per second.</summary>
    public float Damping { get; init; }

    /// <summary>How wide the band is over which the edge is damped to nothing, in metres.</summary>
    /// <remarks>
    ///     ⚠ <b>Without it the boundary is a mirror.</b> A wave reaching the edge of the window
    ///     reflects and comes back, so a boat's wake returns to meet it a second later — which reads
    ///     as the lake having invisible walls exactly as far away as the window is wide.
    /// </remarks>
    public float EdgeFade { get; init; }

    /// <summary>How many injections a step will accept before it starts reporting overflow.</summary>
    /// <remarks>
    ///     ⚠ <b>Reported and not silently dropped</b> — § D12's own wording. An unbounded number of
    ///     sources is how this feature becomes a frame-time cliff, and a budget nobody can see is one
    ///     nobody raises before shipping. <see cref="WaterRipples.Overflowed" /> is the number.
    /// </remarks>
    public int InjectionBudget { get; init; }

    /// <summary>A 64-metre window at a quarter of a metre a texel — the CPU reference's rate.</summary>
    public static WaterRippleSettings Default =>
        new() {
            Extent = 64f,
            Resolution = 257,
            Speed = 6f,
            Damping = 0.6f,
            EdgeFade = 6f,
            InjectionBudget = 64
        };

    /// <summary>How many metres apart two neighbouring texels are.</summary>
    /// <remarks>
    ///     <c>Extent / (Resolution − 1)</c>, exactly as <see cref="WaterFieldDescription" />'s is and
    ///     for the same reason: the samples include both edges.
    /// </remarks>
    public float MetresPerTexel => Resolution > 1 ? Extent / (Resolution - 1) : 0f;

    /// <summary>The fastest a disturbance may travel for a step of a given length to be stable.</summary>
    /// <param name="deltaTime">How long the step is, in seconds.</param>
    /// <returns>The speed, in metres a second.</returns>
    /// <remarks>
    ///     The Courant condition for the two-dimensional wave equation on a square grid:
    ///     <c>c ≤ Δx ⁄ (Δt·√2)</c>. Stated as a function rather than baked in, because the step is the
    ///     simulation's and the spacing is the window's, and an author who halves the extent has
    ///     halved this without touching it.
    /// </remarks>
    public float MaximumSpeed(float deltaTime) =>
        deltaTime > 0f ? MetresPerTexel / (deltaTime * MathF.Sqrt(2f)) : float.PositiveInfinity;

    /// <summary>Why this simulation cannot be run, or <see langword="null" /> if it can.</summary>
    /// <param name="deltaTime">The fixed step it will be advanced by, in seconds.</param>
    /// <returns>The reason, in words a panel can show.</returns>
    public string? Validate(float deltaTime) {
        if (Resolution < 3) {
            return $"A ripple field of {Resolution} texels has no interior to propagate across.";
        }

        if (!(Extent > 0f)) {
            return "A ripple field of no extent covers nothing.";
        }

        if (!(deltaTime > 0f)) {
            return "A ripple simulation needs a step of some length.";
        }

        var limit = MaximumSpeed(deltaTime);

        if (Speed > limit) {
            return $"A speed of {Speed} m/s is above the {limit:0.##} m/s this grid and step are "
                + "stable at. Past the Courant limit an explicit wave equation does not look wrong, "
                + "it grows without bound in a few dozen steps and every consumer reads a NaN.";
        }

        return null;
    }
}

/// <summary>
///     A sliding-window height field: the CPU half of § D12, and the reference the GPU is held to.
/// </summary>
/// <remarks>
///     <para>
///         <b>Displacement, not geometry</b>
///         ([35 § D12](../../docs/plan/35-water.md#d12-ripples-are-a-sliding-window-height-field-and-they-are-displacement-not-geometry)).
///         A texture of surface displacement and its velocity, advanced by a wave-equation step, with
///         entities injecting into it — a character wading writes a moving depression, a boat writes a
///         hull-shaped one, an impact writes an impulse. The result is added by
///         <see cref="WaterEvaluator" />, so a second boat <em>rides</em> the first one's wake and the
///         buoyancy solver feels it.
///     </para>
///     <para>
///         ⚠ <b>This exists on the CPU even when a GPU simulation is running, and that is a stated
///         behaviour rather than duplication.</b> Buoyancy sampling a GPU texture cannot see the
///         current frame's step without a stall, so the simulation is <em>also</em> run here at a
///         lower resolution over the same injections — deterministic, jobbable, and compared against
///         the device version at a <b>stated tolerance that is not exact</b>, unlike everything else
///         in this assembly. That asymmetry is why the ripple contribution is separated from the wave
///         sum in the evaluator's signature: the closed-form part is exact and the simulated part is
///         not, and a caller that needs the exact one — the network path — asks for it alone by
///         passing no ripples at all.
///     </para>
///     <para>
///         ⚠ <b>The window scrolls by <em>shifting its contents</em>, unlike
///         <see cref="WaterField" />, which forgets them.</b> A field is a function of bodies and
///         ground that can be recomputed; a simulation's state is its history, and throwing it away
///         when the camera walks would delete every wake in the scene. So the shift is by whole texels
///         — which is also why the origin snaps.
///     </para>
/// </remarks>
public sealed class WaterRipples : IWaterRipples {
    readonly float[] height;

    // ⚠ Not readonly, and the pair is why: Step writes the new velocities into `scratch` and then
    // swaps the two references rather than copying a field's worth of floats back. `height` never
    // moves — it is written element by element — so it is the one of the three that can be.
    float[] velocity;
    float[] scratch;

    /// <summary>Creates an empty simulation.</summary>
    /// <param name="settings">Its shape and its limits.</param>
    /// <param name="origin">Where the window's low corner starts, on the ground plane.</param>
    /// <exception cref="ArgumentException">The settings are not ones that can be run.</exception>
    public WaterRipples(in WaterRippleSettings settings, Vector2 origin = default) {
        if (settings.Resolution < 3 || !(settings.Extent > 0f)) {
            throw new ArgumentException(
                settings.Validate(1f / 60f) ?? "The ripple settings cannot be used.",
                nameof(settings)
            );
        }

        Settings = settings;
        Origin = origin;

        var texels = settings.Resolution * settings.Resolution;

        height = new float[texels];
        velocity = new float[texels];
        scratch = new float[texels];
    }

    /// <summary>Its shape and its limits.</summary>
    public WaterRippleSettings Settings { get; }

    /// <summary>Where the window's low corner is, on the ground plane.</summary>
    public Vector2 Origin { get; private set; }

    /// <summary>How many injections this step accepted.</summary>
    public int Injections { get; private set; }

    /// <summary>How many it had to refuse, over the budget.</summary>
    /// <remarks>
    ///     ⚠ <b>The number § D12 asks to be reported rather than absorbed.</b> Non-zero is a scene
    ///     whose ripples are arbitrary — whichever sources happened to be walked first got the budget
    ///     — and the fix is either fewer sources or a bigger number, both of which need somebody to
    ///     know it happened.
    /// </remarks>
    public int Overflowed { get; private set; }

    /// <summary>How many steps have been taken.</summary>
    public int StepCount { get; private set; }

    /// <summary>Forgets everything, keeping the window where it is.</summary>
    public void Clear() {
        Array.Clear(height);
        Array.Clear(velocity);

        Injections = 0;
        Overflowed = 0;
    }

    /// <summary>Moves the window to follow a point, carrying the state with it.</summary>
    /// <param name="centre">Where to centre it, on the ground plane.</param>
    /// <returns>Whether it moved.</returns>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>Snapped to a whole texel, with the same warning as § D3's window.</b> A window that
    ///         moved by a fraction of a texel would resample its own state every step, and a
    ///         resampling filter applied repeatedly is a low-pass filter — the wake would blur away
    ///         over a few seconds of walking and be sharp again the moment the camera stopped.
    ///     </para>
    ///     <para>
    ///         Floor rather than round, so a window following a camera never steps backwards: rounding
    ///         changes direction at the midpoint, and a window that goes back one texel and forward
    ///         two makes the whole field jitter.
    ///     </para>
    /// </remarks>
    public bool Follow(Vector2 centre) {
        var step = Settings.MetresPerTexel;

        if (!(step > 0f)) {
            return false;
        }

        var low = centre - new Vector2(Settings.Extent * 0.5f);

        var wanted = new Vector2(
            MathF.Floor(low.X / step) * step,
            MathF.Floor(low.Y / step) * step
        );

        var shiftX = (int)MathF.Round((wanted.X - Origin.X) / step);
        var shiftZ = (int)MathF.Round((wanted.Y - Origin.Y) / step);

        if (shiftX == 0 && shiftZ == 0) {
            return false;
        }

        Shift(shiftX, shiftZ);
        Origin = wanted;

        return true;
    }

    /// <summary>Adds a disturbance to the field.</summary>
    /// <param name="position">Where, on the ground plane.</param>
    /// <param name="radius">How wide it is, in metres.</param>
    /// <param name="amount">How far it pushes the surface, in metres. Negative is a depression.</param>
    /// <returns>Whether it was accepted, or refused for want of budget.</returns>
    /// <remarks>
    ///     <para>
    ///         <b>Into the <em>velocity</em> and not the height</b>, which is what makes an injection a
    ///         disturbance rather than a dent. Writing the height directly holds the surface at a shape
    ///         until something else moves it — so a boat sitting still would carve a permanent hole in
    ///         the lake — where a velocity impulse propagates away as a ring, which is what a splash is.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The budget is per step and is spent in arrival order</b>, which means a scene over
    ///         budget has arbitrary ripples rather than merely fewer. That is honest and is why
    ///         <see cref="Overflowed" /> exists: the alternative — a priority somebody assigns — is a
    ///         second thing to author and would still drop something.
    ///     </para>
    /// </remarks>
    public bool Inject(Vector2 position, float radius, float amount) {
        if (Injections >= Math.Max(Settings.InjectionBudget, 0)) {
            Overflowed++;

            return false;
        }

        Injections++;

        var step = Settings.MetresPerTexel;
        var resolution = Settings.Resolution;
        var reach = MathF.Max(radius, step);

        var local = (position - Origin) / step;
        var span = reach / step;

        var x0 = Math.Clamp((int)MathF.Floor(local.X - span), 0, resolution - 1);
        var z0 = Math.Clamp((int)MathF.Floor(local.Y - span), 0, resolution - 1);
        var x1 = Math.Clamp((int)MathF.Ceiling(local.X + span), 0, resolution - 1);
        var z1 = Math.Clamp((int)MathF.Ceiling(local.Y + span), 0, resolution - 1);

        for (var z = z0; z <= z1; z++) {
            for (var x = x0; x <= x1; x++) {
                var dx = (x - local.X) * step;
                var dz = (z - local.Y) * step;
                var distance = MathF.Sqrt((dx * dx) + (dz * dz));

                // A smooth bump rather than a disc: a hard-edged injection is a step function, and a
                // wave equation's response to a step is a ring of grid-aligned artefacts that reads
                // as a square splash.
                var falloff = 1f - WaterMath.SmoothStep(0f, reach, distance);

                if (falloff > 0f) {
                    velocity[(z * resolution) + x] += amount * falloff;
                }
            }
        }

        return true;
    }

    /// <summary>Advances the simulation by one fixed step.</summary>
    /// <param name="deltaTime">How long the step is, in seconds.</param>
    /// <exception cref="ArgumentException">The settings are not stable at that step.</exception>
    /// <remarks>
    ///     <para>
    ///         The two-dimensional wave equation, explicit: the acceleration of a texel is the
    ///         Laplacian of its neighbours times the speed squared, damped, and integrated
    ///         semi-implicitly so the step is the one a fixed-step physics engine takes.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The edge is damped to nothing across a stated band rather than clamped.</b> A
    ///         clamped boundary is a mirror: a wake reaching the edge reflects and comes back, so the
    ///         lake reads as having invisible walls exactly as far away as the window is wide.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The budget resets here and not in <see cref="Inject" />.</b> The injections of one
    ///         step are the ones between two steps, so resetting anywhere else would let a frame that
    ///         injected without stepping accumulate for ever.
    ///     </para>
    /// </remarks>
    public void Step(float deltaTime) {
        if (Settings.Validate(deltaTime) is { } why) {
            throw new ArgumentException(why, nameof(deltaTime));
        }

        var resolution = Settings.Resolution;
        var spacing = Settings.MetresPerTexel;
        var speed = Settings.Speed;

        var coefficient = speed * speed / (spacing * spacing);
        var damping = MathF.Max(Settings.Damping, 0f);

        for (var z = 0; z < resolution; z++) {
            for (var x = 0; x < resolution; x++) {
                var index = (z * resolution) + x;

                // ⚠ Clamped at the border rather than wrapped. A wrapped Laplacian joins the two ends
                // of the window, so a wake leaving one side arrives at the other — which is the same
                // artefact as a mirror boundary and is harder to recognise.
                var left = height[(z * resolution) + Math.Max(x - 1, 0)];
                var right = height[(z * resolution) + Math.Min(x + 1, resolution - 1)];
                var down = height[(Math.Max(z - 1, 0) * resolution) + x];
                var up = height[(Math.Min(z + 1, resolution - 1) * resolution) + x];

                var laplacian = left + right + down + up - (4f * height[index]);

                var accelerated = velocity[index] + (coefficient * laplacian * deltaTime);

                scratch[index] = accelerated - (accelerated * Math.Clamp(damping * deltaTime, 0f, 1f));
            }
        }

        (velocity, scratch) = (scratch, velocity);

        for (var z = 0; z < resolution; z++) {
            for (var x = 0; x < resolution; x++) {
                var index = (z * resolution) + x;
                var fade = EdgeFade(x, z);

                height[index] = (height[index] + (velocity[index] * deltaTime)) * fade;
                velocity[index] *= fade;
            }
        }

        Injections = 0;
        Overflowed = 0;
        StepCount++;
    }

    /// <inheritdoc />
    /// <remarks>
    ///     Bilinear, and <see langword="false" /> outside the window entirely — which is honest: a
    ///     simulation is a window, and what is beyond it is not something it knows. A caller that gets
    ///     false has the closed-form surface and nothing else, which is exactly right.
    /// </remarks>
    public bool TryDisplacement(Vector2 position, out float displacement) {
        displacement = 0f;

        var resolution = Settings.Resolution;
        var step = Settings.MetresPerTexel;
        var local = (position - Origin) / step;

        if (local.X < 0f || local.Y < 0f || local.X > resolution - 1 || local.Y > resolution - 1) {
            return false;
        }

        var x0 = Math.Clamp((int)MathF.Floor(local.X), 0, resolution - 1);
        var z0 = Math.Clamp((int)MathF.Floor(local.Y), 0, resolution - 1);
        var x1 = Math.Min(x0 + 1, resolution - 1);
        var z1 = Math.Min(z0 + 1, resolution - 1);

        var fx = WaterMath.Saturate(local.X - x0);
        var fz = WaterMath.Saturate(local.Y - z0);

        var top = float.Lerp(height[(z0 * resolution) + x0], height[(z0 * resolution) + x1], fx);
        var bottom = float.Lerp(height[(z1 * resolution) + x0], height[(z1 * resolution) + x1], fx);

        displacement = float.Lerp(top, bottom, fz);

        return true;
    }

    /// <summary>What the field holds at one texel, unfiltered.</summary>
    /// <param name="x">Its X index.</param>
    /// <param name="z">Its Z index.</param>
    /// <returns>The displacement, in metres.</returns>
    /// <remarks>What an upload walks, and what a test asserts against a hand-computed expectation.</remarks>
    public float At(int x, int z) =>
        (uint)x >= (uint)Settings.Resolution || (uint)z >= (uint)Settings.Resolution
            ? 0f
            : height[(z * Settings.Resolution) + x];

    /// <summary>The largest displacement anywhere in the field, in metres.</summary>
    /// <remarks>
    ///     The reading that says the simulation is stable. An explicit wave equation past its Courant
    ///     limit does not look wrong, it grows — so a number that keeps rising with no injections is
    ///     the one symptom worth watching for, and <see cref="WaterRippleSettings.Validate" /> is what
    ///     stops it happening.
    /// </remarks>
    public float Peak {
        get {
            var peak = 0f;

            foreach (var value in height) {
                peak = MathF.Max(peak, MathF.Abs(value));
            }

            return peak;
        }
    }

    /// <summary>How much of a texel's state survives, by how close to the edge it is.</summary>
    float EdgeFade(int x, int z) {
        var band = Settings.EdgeFade;

        if (!(band > 0f)) {
            return 1f;
        }

        var step = Settings.MetresPerTexel;
        var last = Settings.Resolution - 1;

        var nearest = MathF.Min(
            MathF.Min(x, last - x),
            MathF.Min(z, last - z)
        ) * step;

        return WaterMath.SmoothStep(0f, band, nearest);
    }

    /// <summary>Slides the whole field by whole texels, filling what scrolls in with stillness.</summary>
    void Shift(int shiftX, int shiftZ) {
        var resolution = Settings.Resolution;

        if (Math.Abs(shiftX) >= resolution || Math.Abs(shiftZ) >= resolution) {
            Clear();

            return;
        }

        Array.Clear(scratch);
        Move(height);

        Array.Clear(scratch);
        Move(velocity);

        void Move(float[] channel) {
            for (var z = 0; z < resolution; z++) {
                var source = z + shiftZ;

                if ((uint)source >= (uint)resolution) {
                    continue;
                }

                for (var x = 0; x < resolution; x++) {
                    var from = x + shiftX;

                    if ((uint)from < (uint)resolution) {
                        scratch[(z * resolution) + x] = channel[(source * resolution) + from];
                    }
                }
            }

            scratch.AsSpan().CopyTo(channel);
        }
    }
}

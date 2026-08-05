// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Threading;
using Vixen.Ecs.Systems;

namespace Vixen.Rendering.Water;

/// <summary>Advances the one water clock, before anything in the frame reads it.</summary>
/// <remarks>
///     <para>
///         <b>[35 § D2](../../docs/plan/35-water.md#d2-one-evaluator-two-hosts-and-the-seam-is-a-test)'s
///         first consequence, given a phase.</b> There is exactly one water time in a running game:
///         the vertex stage draws at it, the underwater shape tests at it, and a buoyancy solver
///         integrates at it. This is the one thing that writes it.
///     </para>
///     <para>
///         ⚠ <b>In <see cref="SystemPhase.EarlyUpdate" />, and the phase is the whole point of the
///         system existing.</b> <see cref="WaterZoneSystem" /> used to advance the clock in its own
///         <see cref="SystemPhase.PreRender" /> update — it has to fold there, because a body is
///         rasterised where <c>TransformSystem</c> has just put it — and
///         <see cref="SystemPhase.FixedUpdate" /> runs <em>earlier in the same frame</em>. So a
///         buoyancy solver read last frame's water time while the vertex stage drew this frame's, and
///         a boat sat exactly one frame of swell behind the water underneath it: constant, small, and
///         invisible until the frame rate changed. Which is the drift § D2's whole seam exists to
///         prevent, arriving through the back door of a phase order.
///     </para>
///     <para>
///         ⚠ <b><c>GameTime.Total</c>, not a delta this accumulates.</b> A system summing its own
///         deltas drifts from the physics step by exactly the rounding and keeps running while the
///         game is paused. The fixed step's own <c>GameTime</c> carries the frame's total unchanged —
///         see <c>FixedStepAccumulator.StepTime</c> — so a solver and a renderer reading it in
///         different phases of one frame read the same number, which is the property this relies on.
///     </para>
///     <para>
///         <b>A host that forgets it gets a still sea rather than a subtly wrong one.</b> That is
///         deliberate: the alternative is a fallback advance inside the zone system, which is a second
///         writer, and a second writer is what the rule is against.
///     </para>
/// </remarks>
/// <param name="zones">Whose clock this advances.</param>
[UpdateInGroup(SystemPhase.EarlyUpdate)]
public sealed class WaterClockSystem(WaterZoneSystem zones) : SystemBase {
    readonly WaterZoneSystem zones = zones ?? throw new ArgumentNullException(nameof(zones));

    /// <summary>How fast water time runs against game time. One is real time.</summary>
    /// <remarks>
    ///     ⚠ <b>A multiplier on the clock and not on the wave speed.</b> Halving this is a sea in
    ///     slow motion — every wave keeps its own dispersion relation, so a long swell still outruns
    ///     the chop by the same ratio. Scaling the amplitudes or the wavelengths instead would be a
    ///     different sea rather than the same one seen slowly.
    ///
    ///     ⚠ It multiplies <c>GameTime.Total</c>, which already carries the game's own time scale —
    ///     so a paused game has a still sea without this being touched.
    /// </remarks>
    public float Rate { get; set; } = 1f;

    /// <summary>The last value it wrote.</summary>
    public float WaterTime { get; private set; }

    /// <inheritdoc />
    public override JobHandle Update(in SystemContext context, JobHandle dependency) {
        WaterTime = (float)context.Time.TotalSeconds * Rate;
        zones.WaterTime = WaterTime;

        return dependency;
    }
}

// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;
using Vixen.Core.Threading;
using Vixen.Ecs;
using Vixen.Ecs.Systems;
using Vixen.Engine.Transforms;
using Vixen.Physics.Characters;
using Vixen.Physics.Ecs;

namespace Vixen.Water.Physics;

/// <summary>
///     Tells a character how much of it is under water, which is the one number swimming is made of.
/// </summary>
/// <remarks>
///     <para>
///         <b>[35 § D11](../../docs/plan/35-water.md#d11-swimming-is-a-fourth-move-mode-and-immersion-is-the-only-new-number)'s
///         only new number, written.</b> <c>CharacterMoveMode.Swimming</c> exists,
///         <c>CharacterMotion</c> implements wading, the swim restoring force, the drag, the speed
///         scale and the two-threshold hysteresis, and <c>CharacterMovement.Default</c> ships tuned
///         numbers for all of it — and <c>CharacterState.Immersion</c> was written by <em>nothing</em>
///         in the tree until this existed. Its own doc comment says "written by whatever knows where
///         the water is"; this is that writer.
///     </para>
///     <para>
///         <b>It is here for <see cref="BuoyancySystem" />'s reason and it is the same join.</b>
///         <c>Vixen.Physics</c> may not reference the water stack and the water kernel may not link
///         Jolt — § D1 makes the physics join a separate assembly, and this is that assembly. A
///         swimming character and a floating crate are the same seam with a different force: one
///         turns immersion into a mode, the other turns it into a lift.
///     </para>
///     <para>
///         ⚠ <b>It finds the water through <see cref="IWaterSurface" /> and never through
///         <c>WaterZoneSystem</c>.</b> The zone fold lives in <c>Vixen.Rendering.Water</c>, and a
///         reference to it from here would drag a graphics device into the path a dedicated server
///         runs — the one line § D1 exists to forbid. <c>WaterZoneSystem</c> implements the kernel
///         interface, so a game passes the fold it already has and a headless build passes its own.
///     </para>
///     <para>
///         <b>In <see cref="SystemPhase.FixedUpdate" />, before the character steps.</b>
///         <c>CharacterMovementSystem</c> runs after <c>PhysicsStepSystem</c> and reads this number to
///         decide the mode; written any later it is one step stale, which at a shoreline is a
///         character that swims a step after it should have and wades a step after it should have —
///         the drift § D2's whole seam exists to prevent, arriving through a phase order.
///     </para>
///     <para>
///         ⚠ <b>The clock is the surface's and not the frame's.</b> There is one water time and
///         <c>WaterClockSystem</c> is its only writer; reading <c>GameTime</c> here would be a second
///         definition of "when", and a swimmer bobbing on a different swell from the one drawn under
///         them is exactly the disagreement the one-clock rule is against.
///     </para>
///     <para>
///         <b>Sampled at the capsule, not at the entity.</b> A character's origin is at its feet and
///         <c>CharacterMovement.ShapeOffset</c> lifts the capsule off it, so the submerged fraction is
///         measured from the entity's Y over the standing capsule's full height —
///         <see cref="WaterQuery.Immersion" /> takes exactly that pair and is the one definition of
///         the answer.
///     </para>
/// </remarks>
/// <param name="surface">Where the water is, and what time it is there.</param>
[UpdateInGroup(SystemPhase.FixedUpdate)]
[UpdateBefore(typeof(CharacterMovementSystem))]
public sealed class WaterImmersionSystem(IWaterSurface surface) : SystemBase, IDeclaredAccess {
    readonly QueryDescription characters =
        new QueryDescription().WithAll<CharacterMovement, CharacterState, LocalTransform>();

    /// <summary>Where the water is, and the clock it is at.</summary>
    /// <remarks>
    ///     ⚠ <b>Settable for the reason <see cref="BuoyancySystem.Surface" /> is</b>: on a client this
    ///     is <c>WaterZoneSystem</c>; on a headless build it is whatever folded the zones there. The
    ///     interface is the kernel's for exactly that reason — see <see cref="IWaterSurface" />.
    /// </remarks>
    public IWaterSurface Surface { get; set; } = surface ?? throw new ArgumentNullException(nameof(surface));

    /// <inheritdoc />
    public SystemAccess Access { get; } = SystemAccess.Declare()
        .Read<CharacterMovement>()
        .Read<LocalTransform>()
        .Write<CharacterState>()
        .Build();

    /// <summary>How many characters were in water deep enough to swim in, last step.</summary>
    /// <remarks>
    ///     ⚠ <b>The number that says this ran at all.</b> A zone whose spline never resolved answers
    ///     every query with dry land, and a character walking into a lake that is drawn perfectly and
    ///     has no field behind it simply walks along the bed — no error, no mode change, and a
    ///     screenshot that looks like the water is a decal.
    /// </remarks>
    public int Swimming { get; private set; }

    /// <inheritdoc />
    public override JobHandle Update(in SystemContext context, JobHandle dependency) {
        // Writing CharacterState is a write to a column the character step reads on the very next
        // system, and nothing scheduled may still be reading it.
        dependency.Complete();

        Step(context.World);

        return dependency;
    }

    /// <summary>Measures one step's worth of immersion.</summary>
    /// <param name="world">The world.</param>
    /// <exception cref="ArgumentNullException"><paramref name="world" /> is null.</exception>
    /// <remarks>Public so a test can step without standing up a runner.</remarks>
    public void Step(World world) {
        ArgumentNullException.ThrowIfNull(world);

        var swimming = 0;
        var waterTime = Surface.WaterTime;

        foreach (var chunk in world.Chunks(characters)) {
            var movements = chunk.ReadValues<CharacterMovement>();
            var transforms = chunk.ReadValues<LocalTransform>();
            var states = chunk.Values<CharacterState>();

            for (var index = 0; index < chunk.Count; index++) {
                var feet = transforms[index].Position;
                var ground = new Vector2(feet.X, feet.Z);

                // ⚠ Null is "no zone claims this position", which is dry — and not the same as zero
                // immersion left over from last step. A character that walks out of a zone keeps its
                // old immersion for ever otherwise, and swims across the car park.
                if (Surface.QueryAt(ground) is not { } water) {
                    states[index].Immersion = 0f;
                    continue;
                }

                // Twice the shape offset is the capsule's full height: the offset lifts its centre off
                // the feet, so the crown is the same distance again above the centre.
                states[index].Immersion = water.Immersion(
                    ground,
                    feet.Y,
                    MathF.Max(movements[index].ShapeOffset.Y * 2f, 0.1f),
                    waterTime
                );

                if (states[index].Immersion >= movements[index].SwimThreshold) {
                    swimming++;
                }
            }
        }

        Swimming = swimming;
    }
}

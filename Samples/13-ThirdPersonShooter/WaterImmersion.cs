// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;
using Vixen.Core.Threading;
using Vixen.Ecs;
using Vixen.Ecs.Systems;
using Vixen.Engine.Transforms;
using Vixen.Physics.Characters;
using Vixen.Physics.Ecs;
using Vixen.Rendering.Water;

namespace Vixen.Samples.ThirdPersonShooter;

/// <summary>
///     Tells a character how much of it is under water, which is the one number swimming is made of.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>Another finished consumer nothing fed.</b> <c>CharacterMoveMode.Swimming</c> exists,
///         <c>CharacterMotion</c> implements wading, the swim restoring force, the drag, the speed
///         scale and the two-threshold hysteresis, and <c>CharacterMovement.Default</c> ships tuned
///         numbers for all of it — and <c>CharacterState.Immersion</c> is written by <em>nothing</em>
///         in the tree. Its own doc comment says "written by whatever knows where the water is", and
///         in a running game nothing did, so the fourth move mode could not be entered from any
///         scene. This is that writer.
///     </para>
///     <para>
///         ⚠ <b>It belongs in the engine and it is here, for <c>TerrainGroundSystem</c>'s reason.</b>
///         <c>Vixen.Physics</c> may not reference <c>Vixen.Rendering.Water</c> and the water kernel
///         may not reference Jolt — doc 35 § D1 is explicit that the physics join is a separate
///         assembly, and <c>Vixen.Water.Physics</c> is that assembly for buoyancy. A swimming
///         character is the same join with a different force, and it has no home yet.
///     </para>
///     <para>
///         <b>In <see cref="SystemPhase.FixedUpdate" />, before the character steps.</b>
///         <c>CharacterMovementSystem</c> runs after <c>PhysicsStepSystem</c> and reads this number to
///         decide the mode; written any later it is one step stale, which at a shoreline is a
///         character that swims a step after it should have and wades a step after it should have —
///         the drift § D2's whole seam exists to prevent, arriving through a phase order.
///     </para>
///     <para>
///         ⚠ <b>The clock is the zone system's and not the frame's.</b> There is one water time and
///         <c>WaterClockSystem</c> is its only writer; reading <c>GameTime</c> here would be a second
///         definition of "when", and a swimmer bobbing on a different swell from the one drawn under
///         them is exactly the disagreement the one-clock rule is against.
///     </para>
///     <para>
///         <b>Sampled at the capsule, not at the entity.</b> A character's origin is at its feet and
///         <c>CharacterMovement.ShapeOffset</c> lifts the capsule off it, so the submerged fraction is
///         measured from the entity's Y over the standing capsule's full height —
///         <c>WaterQuery.Immersion</c> takes exactly that pair and is the one definition of the answer.
///     </para>
/// </remarks>
/// <param name="zones">The fold that knows where the water is, and what time it is.</param>
[UpdateInGroup(SystemPhase.FixedUpdate)]
[UpdateBefore(typeof(CharacterMovementSystem))]
public sealed class WaterImmersionSystem(WaterZoneSystem zones) : SystemBase, IDeclaredAccess {
    readonly WaterZoneSystem zones = zones ?? throw new ArgumentNullException(nameof(zones));

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
    ///     screenshot that looks like the water is a decal. This is what
    ///     <c>SampleLog.WaterFolded</c> reports beside the zone counts.
    /// </remarks>
    public int Swimming { get; private set; }

    /// <inheritdoc />
    public override JobHandle Update(in SystemContext context, JobHandle dependency) {
        dependency.Complete();

        var query = new QueryDescription().WithAll<CharacterMovement, CharacterState, LocalTransform>();
        var swimming = 0;

        foreach (var chunk in context.World.Chunks(query)) {
            var movements = chunk.ReadValues<CharacterMovement>();
            var transforms = chunk.ReadValues<LocalTransform>();
            var states = chunk.Values<CharacterState>();

            for (var index = 0; index < chunk.Count; index++) {
                var feet = transforms[index].Position;
                var ground = new Vector2(feet.X, feet.Z);

                // ⚠ Null is "no zone claims this position", which is dry — and not the same as zero
                // immersion left over from last step. A character that walks out of a zone keeps its
                // old immersion for ever otherwise, and swims across the car park.
                if (zones.QueryAt(ground) is not { } water) {
                    states[index].Immersion = 0f;
                    continue;
                }

                // Twice the shape offset is the capsule's full height: the offset lifts its centre off
                // the feet, so the crown is the same distance again above the centre.
                states[index].Immersion = water.Immersion(
                    ground,
                    feet.Y,
                    MathF.Max(movements[index].ShapeOffset.Y * 2f, 0.1f),
                    zones.WaterTime
                );

                if (states[index].Immersion >= movements[index].SwimThreshold) {
                    swimming++;
                }
            }
        }

        Swimming = swimming;

        return default;
    }
}

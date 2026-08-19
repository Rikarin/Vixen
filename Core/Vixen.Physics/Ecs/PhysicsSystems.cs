// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;
using Vixen.Core.Threading;
using Vixen.Ecs;
using Vixen.Ecs.Systems;
using Vixen.Engine.Frames;
using Vixen.Engine.Transforms;

namespace Vixen.Physics.Ecs;

/// <summary>
///     Makes the simulation match the components, then drives kinematic bodies and pushes authored
///     velocities in.
/// </summary>
/// <remarks>
///     First of the three fixed-step passes. Creating a body is structural — it adds a
///     <see cref="PhysicsBody" /> — so this runs its collection and its mutation in two halves rather
///     than through a command buffer: the buffer is played back at the <i>end</i> of the phase, which
///     is after the step that needed the body.
/// </remarks>
[UpdateInGroup(SystemPhase.FixedUpdate)]
public sealed class PhysicsSyncSystem(PhysicsScene scene) : SystemBase, IDeclaredAccess {
    /// <inheritdoc />
    /// <remarks>
    ///     Declared at construction rather than with attributes, for the reason
    ///     <c>TransformSystem</c> gives: naming a component type in a generic call is what assigns it
    ///     an id, and an attribute can only look one up.
    /// </remarks>
    public SystemAccess Access { get; } = SystemAccess.Declare()
        .Read<Collider>()
        .Read<RigidBody>()
        .Read<LocalTransform>()
        .Write<PhysicsBody>()
        .Write<LinearVelocity>()
        .Write<AngularVelocity>()
        .Write<PhysicsInterpolation>()
        .Write<PhysicsTeleport>()
        .Build();

    /// <inheritdoc />
    public override JobHandle Update(in SystemContext context, JobHandle dependency) {
        scene.Synchronize((float)context.Time.Elapsed.TotalSeconds);
        return dependency;
    }
}

/// <summary>Advances the simulation by one fixed step.</summary>
/// <remarks>
///     Declares no component access at all, which the runner reads as "conflicts with everything" —
///     and that is exactly right. The step reads and writes every body in the world through native
///     memory the ECS knows nothing about, so nothing may run beside it.
/// </remarks>
[UpdateInGroup(SystemPhase.FixedUpdate)]
[UpdateAfter(typeof(PhysicsSyncSystem))]
public sealed class PhysicsStepSystem(PhysicsScene scene) : SystemBase {
    /// <summary>What the last step ran into.</summary>
    public PhysicsStepResult LastResult { get; private set; }

    /// <inheritdoc />
    public override JobHandle Update(in SystemContext context, JobHandle dependency) {
        // Everything scheduled so far must be done: the step is about to move bodies the rest of the
        // phase has been reading.
        dependency.Complete();

        var elapsed = (float)context.Time.Elapsed.TotalSeconds;

        if (elapsed > 0f) {
            LastResult = scene.Step(elapsed);
        }

        return default;
    }
}

/// <summary>Copies the simulation's results back into transforms and velocity components.</summary>
[UpdateInGroup(SystemPhase.FixedUpdate)]
[UpdateAfter(typeof(PhysicsStepSystem))]
public sealed class PhysicsWritebackSystem(PhysicsScene scene) : SystemBase, IDeclaredAccess {
    /// <inheritdoc />
    public SystemAccess Access { get; } = SystemAccess.Declare()
        .Read<PhysicsBody>()
        .Write<LocalTransform>()
        .Write<LinearVelocity>()
        .Write<AngularVelocity>()
        .Write<PhysicsInterpolation>()
        .Build();

    /// <inheritdoc />
    public override JobHandle Update(in SystemContext context, JobHandle dependency) {
        scene.Writeback();
        return dependency;
    }
}

/// <summary>
///     Draws every body between where it was at the last step and where it is at this one, so a 60 Hz
///     simulation looks smooth at any frame rate.
/// </summary>
/// <remarks>
///     <para>
///         Writes <c>LocalTransform</c>, so what the renderer sees is the interpolated pose and what
///         the simulation sees is the stepped one. The next fixed step overwrites it from the body,
///         which is why interpolating into the same component is safe rather than cumulative.
///     </para>
///     <para>
///         ⚠ <b>In <see cref="SystemPhase.LateUpdate" /> and not <see cref="SystemPhase.PreRender" />,
///         because a camera that follows a body is what everybody actually looks at.</b> That phase's
///         own summary is "after everything has moved — cameras that follow, constraints that
///         correct", and a camera solving there against a pose this system had not smoothed yet is a
///         camera that steps at the fixed rate however smooth the body is. The symptom is the
///         opposite of the obvious one: the character sits perfectly still relative to the frame and
///         the entire world judders around it, which reads as a jittery camera and sends you looking
///         at the camera.
///     </para>
///     <para>
///         Still before <c>TransformSystem</c>, which is what matters for the renderer, and still
///         after every fixed step this frame ran.
///     </para>
///     <para>
///         Only entities that have asked for it, by carrying a <see cref="PhysicsInterpolation" />
///         component. A body whose motion is being watched by gameplay code — a projectile, a hit
///         test — should not have one, because the smoothed pose is deliberately a step behind.
///     </para>
///     <para>
///         ⚠ <b>A teleport is excluded, and it is excluded upstream of here.</b> This pass draws
///         between two poses and has no way to know that the segment between them is a jump rather
///         than a very fast metre — a distance test here would un-smooth every projectile in the game.
///         So the bridge collapses both poses onto the destination when it acts on a
///         <see cref="PhysicsTeleport" />, and when a character's <c>Adopt</c> takes a written
///         transform; this pass then lerps between two identical poses and draws the body where it
///         was put.
///     </para>
///     <para>
///         ⚠ A zeroed <see cref="PhysicsInterpolation" /> holds two poses at the world origin, and
///         this lerps between them and writes the result — so an entity handed a <c>default</c> one
///         is dragged to <c>(0, 0, 0)</c> until a step has filled both. Seed it with the pose the
///         entity is being created at.
///     </para>
///     <para>
///         ⚠ <b>It writes what it drew back into <see cref="PhysicsInterpolation.DrawnPosition" />,
///         and that is not bookkeeping.</b> On a character, <c>LocalTransform</c> is an input as well
///         as an output — <c>PhysicsScene.Adopt</c> takes a transform that disagrees with the
///         controller as a teleport — so a smoothed pose left here was undone on the next step, and a
///         character carrying this component walked at exactly half its <c>WalkSpeed</c>. The bridge
///         compares against the recorded value to tell this pass's own pose from one a game, a
///         respawn or a rollback wrote.
///     </para>
/// </remarks>
[UpdateInGroup(SystemPhase.LateUpdate)]
[UpdateBefore(typeof(TransformSystem))]
[UpdateBefore(typeof(Engine.Cameras.VirtualCameraSystem))]
public sealed class PhysicsInterpolationSystem(FixedStepAccumulator accumulator) : SystemBase, IDeclaredAccess {
    static readonly QueryDescription Interpolated = new QueryDescription()
        .WithAll<PhysicsInterpolation, LocalTransform>();

    /// <inheritdoc />
    /// <remarks>
    ///     <c>Write</c> and not <c>Read</c> on the interpolation, because the pose drawn is recorded
    ///     back into it. The alternative — a second component holding one vector — is a second
    ///     archetype and a second thing to seed, for a fact that belongs to the smoothing that
    ///     produced it.
    /// </remarks>
    public SystemAccess Access { get; } = SystemAccess.Declare()
        .Write<PhysicsInterpolation>()
        .Write<LocalTransform>()
        .Build();

    /// <inheritdoc />
    public override JobHandle Update(in SystemContext context, JobHandle dependency) {
        // Clamped, because a frame that ran long can bank more than a step's worth before the
        // accumulator's own clamp discards it, and extrapolating past the current step would show
        // objects ahead of where the simulation has actually put them.
        var alpha = Math.Clamp(accumulator.Alpha, 0f, 1f);

        foreach (var chunk in context.World.Chunks(Interpolated)) {
            var states = chunk.Values<PhysicsInterpolation>();
            var transforms = chunk.Values<LocalTransform>();

            for (var index = 0; index < chunk.Count; index++) {
                var state = states[index];
                var drawn = Vector3.Lerp(state.PreviousPosition, state.CurrentPosition, alpha);

                transforms[index].Position = drawn;

                // Slerp and not lerp: a normalised lerp between two rotations more than a few degrees
                // apart moves at a visibly uneven rate, which on a spinning body reads as a wobble.
                transforms[index].Rotation = Quaternion.Slerp(state.PreviousRotation, state.CurrentRotation, alpha);

                // ⚠ The receipt. Exactly what was written, so the character bridge can recognise its
                // own smoothing rather than infer it — see the field's own remarks for the half-speed
                // walk this closes and for why inferring it from the segment is worse.
                states[index].DrawnPosition = drawn;
            }
        }

        return dependency;
    }
}

/// <summary>Registers the physics passes with a loop or a runner, in the right order.</summary>
/// <remarks>
///     One call rather than five, because the order between them is not a preference — it is the
///     difference between a simulation that is driven by this frame's input and one that is driven by
///     last frame's.
/// </remarks>
public static class PhysicsSystems {
    /// <summary>Adds the four fixed-step passes to a runner.</summary>
    /// <param name="runner">The runner.</param>
    /// <param name="scene">The bridge they drive.</param>
    /// <returns>The runner, for chaining.</returns>
    /// <remarks>
    ///     <see cref="CharacterMovementSystem" /> is included whether or not a game has characters. It
    ///     costs one query that matches nothing, and the alternative — a second registration call a
    ///     game has to know to make — is a player that does not move for a reason nobody can see.
    /// </remarks>
    public static SystemRunner AddPhysics(this SystemRunner runner, PhysicsScene scene) {
        ArgumentNullException.ThrowIfNull(runner);
        ArgumentNullException.ThrowIfNull(scene);

        return runner
            .Add(new PhysicsSyncSystem(scene))
            .Add(new PhysicsStepSystem(scene))
            .Add(new CharacterMovementSystem(scene))
            .Add(new PhysicsWritebackSystem(scene));
    }

    /// <summary>Adds the four fixed-step passes and the render-time interpolation to a loop.</summary>
    /// <param name="loop">The loop.</param>
    /// <param name="scene">The bridge they drive.</param>
    /// <returns>The loop, for chaining.</returns>
    public static EngineLoop AddPhysics(this EngineLoop loop, PhysicsScene scene) {
        ArgumentNullException.ThrowIfNull(loop);
        ArgumentNullException.ThrowIfNull(scene);

        loop.Systems.AddPhysics(scene);
        loop.Add(new PhysicsInterpolationSystem(loop.FixedStep));
        return loop;
    }
}

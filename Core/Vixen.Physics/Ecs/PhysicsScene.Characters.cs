// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core;
using Vixen.Core.Mathematics;
using Vixen.Ecs;
using Vixen.Engine.Players;
using Vixen.Engine.Transforms;
using Vixen.Physics.Characters;

namespace Vixen.Physics.Ecs;

public sealed partial class PhysicsScene {
    static readonly QueryDescription NeedCharacter = new QueryDescription()
        .WithAll<CharacterMovement, LocalTransform>()
        .WithNone<CharacterBody>();

    static readonly QueryDescription HaveCharacter = new QueryDescription()
        .WithAll<CharacterBody, CharacterMovement, CharacterState, LocalTransform>();

    static readonly QueryDescription UnstagedCharacter = new QueryDescription()
        .WithAll<CharacterMovement>()
        .WithNone<CharacterState>();

    static readonly QueryDescription OrphanedCharacter = new QueryDescription()
        .WithAll<CharacterBody>()
        .WithNone<CharacterMovement>();

    readonly Dictionary<int, CharacterController> charactersByHandle = [];
    readonly List<Entity> pendingCharacterCreate = [];
    readonly List<Entity> pendingCharacterDestroy = [];
    readonly List<Entity> pendingCharacterStage = [];
    readonly List<Entity> pendingCharacterUntag = [];

    int nextCharacterHandle = 1;

    /// <summary>How many entities have a character controller.</summary>
    public int CharacterCount => charactersByHandle.Count;

    /// <summary>How many times a character's controller has been moved to a written transform.</summary>
    /// <remarks>
    ///     ⚠ <b>A character being adopted on nearly every step is two writers fighting over one
    ///     component, and this is the number that says so.</b> A teleport, a respawn and a
    ///     reconciliation each add one; a walking character adds none. That distinction was invisible
    ///     for as long as it existed — an interpolated character was adopted sixty times a second and
    ///     the only symptom was that it covered half the ground its <c>WalkSpeed</c> asked for, which
    ///     no still capture and no unit test could see. <c>Vixen.Physics</c> has no logger to warn
    ///     through, so this is a counter a run summary, a test or an overlay can read — the same shape
    ///     as <c>FixedStepAccumulator.DroppedSteps</c> and <c>ClientPrediction.MispredictionCount</c>.
    /// </remarks>
    public long CharacterAdoptionCount { get; private set; }

    /// <summary>The controller an entity was given, if it has one.</summary>
    /// <param name="entity">The entity.</param>
    /// <param name="controller">Its controller.</param>
    /// <returns>Whether it has one.</returns>
    /// <remarks>
    ///     For the cases the components do not cover — a custom query from the character's own
    ///     position, a shape swap a game drives itself. Everything the movement rules need is already
    ///     in <see cref="CharacterState" />, so reaching for this should be rare.
    /// </remarks>
    public bool TryGetCharacter(Entity entity, out CharacterController? controller) {
        ObjectDisposedException.ThrowIf(IsDisposed, this);

        controller = null;

        return Entities.TryGet<CharacterBody>(entity, out var body)
            && charactersByHandle.TryGetValue(body.Handle, out controller);
    }

    /// <summary>
    ///     Creates and destroys character controllers, then moves every one of them by one step.
    /// </summary>
    /// <param name="deltaTime">How long the step is, in seconds.</param>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>Called after <see cref="Step" />, never before.</b> A character is swept against
    ///         the world as it is <i>now</i>, so sweeping first would test it against the previous
    ///         step's platform positions — the ordering
    ///         <see cref="CharacterController.Update" />'s own remarks require, made structural here
    ///         so that no caller has to remember it.
    ///     </para>
    ///     <para>
    ///         Creating a controller is structural — it adds a <see cref="CharacterBody" /> — so the
    ///         collection and the mutation are two halves rather than a command buffer, exactly as
    ///         <see cref="Synchronize" /> does it: the buffer plays back at the <i>end</i> of the
    ///         phase, which is after the step that needed the controller.
    ///     </para>
    /// </remarks>
    public void StepCharacters(float deltaTime) {
        ObjectDisposedException.ThrowIf(IsDisposed, this);

        SynchronizeCharacters();

        if (deltaTime <= 0f) {
            return;
        }

        pendingCharacterUntag.Clear();

        foreach (var chunk in Entities.Chunks(HaveCharacter)) {
            var bodies = chunk.Values<CharacterBody>();
            var settings = chunk.ReadValues<CharacterMovement>();
            var states = chunk.Values<CharacterState>();
            var transforms = chunk.Values<LocalTransform>();
            var entities = chunk.Entities;

            for (var index = 0; index < chunk.Count; index++) {
                if (!charactersByHandle.TryGetValue(bodies[index].Handle, out var controller)) {
                    continue;
                }

                var entity = entities[index];
                var intent = Entities.TryGet<MoveIntent>(entity, out var value) ? value : default;

                // The pose the smoothing last drew, if this character is smoothed at all. Read here
                // rather than inside Adopt so that the one component lookup serves both ends of the
                // step — the same lookup WriteCharacterBack would make anyway.
                var drawn = Entities.TryGet<PhysicsInterpolation>(entity, out var interpolation)
                    ? interpolation.DrawnPosition
                    : (Vector3?)null;

                // ⚠ The tag was inert here and therefore stuck for ever: only PushAuthoredState reads
                // it, and that walks PhysicsBody, which a character does not have. Nothing crashed —
                // the character was adopted anyway, because writing its transform *is* the teleport —
                // but the tag stayed on, and anything downstream that treats it as an event rather
                // than a state then fires every tick: NetworkRigidBodyCaptureSystem re-adds
                // NetworkTeleport, the bridge bumps TeleportCount, and every receiver refuses to
                // interpolate that entity again. Consumed here, so the idiom is one idiom.
                var told = Entities.Has<PhysicsTeleport>(entity);

                if (told) {
                    // Structural, so recorded and done after the walk — the reason CommandBuffer
                    // exists, and the same shape PushAuthoredState's pendingUntag has.
                    pendingCharacterUntag.Add(entity);
                }

                // The caller saying so outranks the receipt: Adopt refuses a transform still exactly
                // where the smoothing left it, and a teleport *back to* the drawn pose — a rollback
                // that lands on its own guess, a respawn at the spot you died — is that case exactly.
                // `drawn` itself is kept, because the collapse below still needs to know the entity
                // is smoothed at all.
                var receipt = told ? null : drawn;

                if (Adopt(controller, in bodies[index], in transforms[index], in receipt)) {
                    CharacterAdoptionCount++;

                    // ⚠ The adopt *is* the teleport, so the smoothing has to be told about it here —
                    // a character needs no tag to be moved, which is why one is not required and why
                    // one that is given only removes Adopt's doubt. Without this the pass draws
                    // between the pose the character left and the pose it was put at, so a respawn
                    // slides back across the level it just left and on a frame one fixed step long is
                    // drawn at the old spot outright.
                    //
                    // Position only: rotation is never adopted — the controller owns it, which is why
                    // Adopt does not read it — so its two poses are already continuous and collapsing
                    // them would snap a turn that never jumped.
                    //
                    // Current and not Previous, because WriteCharacterBack rolls Previous ← Current a
                    // few lines below: what is left is the destination, then one step of real walking
                    // out of it. Exactly the shape PhysicsScene.Arrive leaves a rigid body in.
                    if (drawn is not null) {
                        Entities.Get<PhysicsInterpolation>(entity).CurrentPosition = transforms[index].Position;
                    }
                }

                Drive(controller, in settings[index], ref states[index], ref bodies[index], in intent, deltaTime);
                WriteCharacterBack(controller, in bodies[index], ref transforms[index], entity);
            }
        }

        foreach (var entity in pendingCharacterUntag) {
            Entities.Remove<PhysicsTeleport>(entity);
        }

        pendingCharacterUntag.Clear();
    }

    /// <summary>How far a written transform must be from the controller before it counts as a move.</summary>
    /// <remarks>
    ///     A tenth of a millimetre — far above the rounding of the offset's round trip through
    ///     <see cref="WriteCharacterBack" /> (a couple of ULPs) and far below any correction a game or
    ///     a rollback would make. A test for exact equality would snap every step on the floating-point
    ///     noise of <c>(p − o) + o</c>; a larger threshold would silently ignore small corrections,
    ///     which is exactly the size a mispredicted step produces.
    /// </remarks>
    const float TeleportThreshold = 1e-4f;

    /// <summary>Takes a transform somebody else wrote as the character's new position.</summary>
    /// <returns>Whether the controller was moved.</returns>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>Without this, writing a character's <c>LocalTransform</c> does nothing at all.</b>
    ///         A <see cref="CharacterController" />'s position lives in Jolt and the bridge only ever
    ///         wrote <i>out</i> of it, so a teleport, a checkpoint load and a spawn point were all
    ///         silently ignored — the transform was overwritten from the controller on the same step.
    ///         The equivalent for a rigid body is <c>PhysicsTeleport</c>, which exists because the
    ///         bridge writes that component every step and so cannot tell who changed it.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>This file used to claim a character needs no such tag, "because nothing else
    ///         writes its transform between two steps". That was never true, and it cost 50% of every
    ///         character's speed.</b> <c>PhysicsInterpolationSystem</c> writes it, on the same entity,
    ///         every frame — and <see cref="WriteCharacterBack" /> is what puts a character in that
    ///         pass's query in the first place. With <c>alpha</c> at zero, which is every frame whose
    ///         delta is one fixed step, the pose it leaves is the <i>previous</i> step's; this read it
    ///         as a teleport and pulled the controller back, so every other step was undone and a
    ///         4.5 m/s walk covered 2.25. Sample 13 measured 1.819 m of a scripted first second
    ///         against 3.821 m with the component taken off.
    ///     </para>
    ///     <para>
    ///         So the question is not "does the transform disagree with the controller" — the bridge
    ///         is no longer the only thing that writes it — but "did anything <i>other than the
    ///         engine's own smoothing</i> write it". <see cref="PhysicsInterpolation.DrawnPosition" />
    ///         is the receipt that answers it exactly, rather than by guessing from geometry: the
    ///         guess, that an adopted pose lying between the two interpolated ones is the smoothing's,
    ///         swallows the commonest correction a predicted client ever gets, because a client that
    ///         mispredicts along its own path is corrected along that same segment.
    ///     </para>
    ///     <para>
    ///         It is also what makes a character predictable. A rollback restores the entity's
    ///         transform from the server's snapshot and replays; if the controller kept the
    ///         mispredicted position, every replay would start from the guess it was correcting and
    ///         the correction would never converge. A restored snapshot is not the drawn pose, so it
    ///         is still adopted — <c>PredictedPlayerMovement</c> and its tests are what pin that.
    ///     </para>
    ///     <para>
    ///         <see cref="CharacterController.RefreshContacts" /> after the move, so
    ///         <see cref="CharacterController.Ground" /> answers about where the character is now
    ///         rather than where it was — its own remarks name teleporting as the case.
    ///     </para>
    /// </remarks>
    static bool Adopt(
        CharacterController controller,
        in CharacterBody body,
        in LocalTransform transform,
        in Vector3? drawn
    ) {
        var written = transform.Position + body.BuiltOffset;

        if ((written - controller.Position).LengthSquared() <= TeleportThreshold * TeleportThreshold) {
            return false;
        }

        // The engine's own smoothing, still exactly where it left it. Nobody has written this
        // transform since, so there is nothing to adopt — and adopting it would undo the step that
        // produced the pose it was drawn from.
        if (drawn is { } pose
            && (transform.Position - pose).LengthSquared() <= TeleportThreshold * TeleportThreshold) {
            return false;
        }

        controller.Position = written;
        controller.RefreshContacts();
        return true;
    }

    /// <summary>Runs one character through the rules and moves it.</summary>
    static void Drive(
        CharacterController controller,
        in CharacterMovement settings,
        ref CharacterState state,
        ref CharacterBody body,
        in MoveIntent intent,
        float deltaTime
    ) {
        // The ground the character was left standing on by the previous step, which is what the rules
        // decide the mode from. Reading it here rather than trusting the stored copy means a character
        // that was teleported onto a ledge is walking on the first step rather than the second.
        CharacterMotion.Step(settings, ref state, intent, controller.Ground, deltaTime);

        Reshape(controller, in settings, ref state, ref body);

        // Relative on the way in, absolute on the way out. A character standing still on a lift has a
        // zero velocity and is carried by it, which is what CharacterController.GroundVelocity's own
        // remarks prescribe — applied once here rather than in every game that has a lift.
        var carried = state.Velocity + controller.GroundVelocity;

        // Without this a character walking into a slope too steep to climb keeps pushing, the sweep
        // keeps refusing, and it hovers on the face instead of sliding back down.
        var asked = controller.CancelVelocityTowardsSteepSlopes(carried);
        controller.Velocity = asked;

        if (settings.TurnWithAim) {
            controller.Rotation = Quaternion.FromAxisAngle(Vector3.UnitY, intent.Yaw);
        }

        var before = controller.Position;
        controller.Update(deltaTime);

        // ⚠ What the sweep achieved, measured from the displacement rather than read back from the
        // controller. CharacterVirtual leaves LinearVelocity as it was given — CharacterSceneTests
        // pins that by walking into a wall — so trusting it would leave a character jumping into a
        // ceiling holding its full upward speed and hanging there until gravity ate it.
        var achieved = (controller.Position - before) * (1f / deltaTime);

        state.Velocity = Blocked(asked, achieved) - controller.GroundVelocity;
        state.Ground = controller.Ground;
    }

    /// <summary>What a sweep left of the velocity it was given.</summary>
    /// <remarks>
    ///     <b>A sweep can only ever take velocity away.</b> So each component keeps its asked-for sign
    ///     and takes the smaller of the two magnitudes, which is one rule covering every case that
    ///     matters: a wall zeroes the horizontal, a ceiling zeroes the rise, and a stair step-up —
    ///     which displaces a character upward at tens of metres a second for one step — is discarded
    ///     rather than launching it. Taking the displacement wholesale gets the first two right and the
    ///     third catastrophically wrong.
    /// </remarks>
    static Vector3 Blocked(Vector3 asked, Vector3 achieved) => new(
        Blocked(asked.X, achieved.X),
        Blocked(asked.Y, achieved.Y),
        Blocked(asked.Z, achieved.Z)
    );

    static float Blocked(float asked, float achieved) {
        // Opposite signs means the sweep pushed the character back — penetration recovery, or a slide
        // along a surface. Nothing of the asked-for motion survived.
        if (asked == 0f || (asked > 0f) != (achieved > 0f)) {
            return 0f;
        }

        return MathF.Abs(achieved) < MathF.Abs(asked) ? achieved : asked;
    }

    /// <summary>Swaps the collision volume when the crouch state disagrees with it.</summary>
    /// <remarks>
    ///     ⚠ <b>A refused stand leaves the character crouched, and that is the answer rather than a
    ///     failure.</b> <see cref="CharacterController.TrySetShape" /> returns false under a low
    ///     ceiling, so putting <see cref="CharacterState.IsCrouching" /> back is all that is needed —
    ///     no call site anywhere has to know a ceiling exists, and the character keeps crouch speed
    ///     because <c>CharacterMotion</c> reads the same flag.
    /// </remarks>
    static void Reshape(
        CharacterController controller,
        in CharacterMovement settings,
        ref CharacterState state,
        ref CharacterBody body
    ) {
        var crouched = state.IsCrouching && !settings.CrouchShape.IsNone;
        var wanted = crouched ? settings.CrouchShape : settings.Shape;
        var offset = crouched ? settings.CrouchShapeOffset : settings.ShapeOffset;

        // BuiltShape and not controller.Settings.Shape: the settings record is what the controller was
        // *created* with and a swap does not rewrite it, so comparing against it would try the same
        // swap every step for ever. It is the same field PhysicsBody keeps and for the same reason —
        // telling a component edit from a no-op without a native call per character per step.
        if (wanted.IsNone || wanted == body.BuiltShape) {
            return;
        }

        // The feet stay where they are and the centre moves. Trying the taller shape about the
        // crouched centre puts its bottom through the floor, which is refused — so a character on
        // flat ground with nothing above it could never stand up, and it would look like the ceiling
        // check misfiring rather than like a coordinate mistake.
        var previous = controller.Position;
        controller.Position = previous + offset - body.BuiltOffset;

        if (controller.TrySetShape(wanted)) {
            body.BuiltShape = wanted;
            body.BuiltOffset = offset;
            return;
        }

        controller.Position = previous;
        state.IsCrouching = !crouched;
    }

    void WriteCharacterBack(
        CharacterController controller,
        in CharacterBody body,
        ref LocalTransform transform,
        Entity entity
    ) {
        // The controller's position is its shape's centre — which its own doc comment used to call
        // the bottom, and CharacterControllerTests proves otherwise by asserting a capsule settles at
        // halfHeight + radius above the floor. The offset is what puts the entity's origin back at the
        // character's feet.
        // The live offset, not the standing one: a crouched character's capsule is centred lower, and
        // reading the standing offset here would make it appear to sink as it crouched.
        var position = controller.Position - body.BuiltOffset;
        transform.Position = position;
        transform.Rotation = controller.Rotation;

        // Filled for the same reason a body's is: PhysicsInterpolationSystem draws between the last
        // two steps, so a character stepping at 60 Hz is smooth at any frame rate. A character with
        // no PhysicsInterpolation simply is not smoothed, which is what a camera bolted to it wants.
        //
        // ⚠ And this line is why a character is in that pass's query at all, which is the other half
        // of the half-speed defect Adopt's remarks describe: the bridge hands the smoothing an entity
        // whose transform the bridge then reads back as an instruction.
        if (!Entities.Has<PhysicsInterpolation>(entity)) {
            return;
        }

        ref var interpolation = ref Entities.Get<PhysicsInterpolation>(entity);
        interpolation.PreviousPosition = interpolation.CurrentPosition;
        interpolation.PreviousRotation = interpolation.CurrentRotation;
        interpolation.CurrentPosition = position;
        interpolation.CurrentRotation = transform.Rotation;

        // The receipt is the transform as it stands, because that is what was just written to it.
        // Kept in step here and not only in the smoothing pass, so a build that carries the component
        // without running that pass — a headless server, a prediction replay — holds a pose the
        // engine really wrote rather than a zeroed one. Otherwise a game teleporting a character to
        // the world origin would find the teleport quietly ignored.
        interpolation.DrawnPosition = position;
    }

    void SynchronizeCharacters() {
        pendingCharacterCreate.Clear();
        pendingCharacterDestroy.Clear();
        pendingCharacterStage.Clear();

        Collect(NeedCharacter, pendingCharacterCreate);
        Collect(OrphanedCharacter, pendingCharacterDestroy);
        Collect(UnstagedCharacter, pendingCharacterStage);

        foreach (var entity in pendingCharacterDestroy) {
            RemoveCharacter(entity);
            Entities.Remove<CharacterBody>(entity);
        }

        // Derived state, attached by the engine, so a character placed by a scene or created by hand
        // works without its creator having remembered to — the same courtesy VirtualCameraSystem does
        // a shot.
        foreach (var entity in pendingCharacterStage) {
            Entities.Add(entity, default(CharacterState));
        }

        foreach (var entity in pendingCharacterCreate) {
            if (CreateCharacter(entity) is { } body) {
                Entities.Add(entity, body);
            }
        }
    }

    void Collect(QueryDescription description, List<Entity> into) {
        foreach (var chunk in Entities.Chunks(description)) {
            into.AddRange(chunk.Entities);
        }
    }

    CharacterBody? CreateCharacter(Entity entity) {
        var settings = Entities.Read<CharacterMovement>(entity);

        // A shape names a volume only a live PhysicsShapes can issue, so CharacterMovement.Default
        // cannot supply one and a component that reached here without one is a configuration error
        // rather than a state to simulate. Skipped rather than thrown: the entity is retried every
        // step, so filling the shape in later works and nothing has to be recreated.
        if (settings.Shape.IsNone) {
            return null;
        }

        var transform = Entities.Read<LocalTransform>(entity);

        var controller = World.CreateCharacter(
            new() {
                Shape = settings.Shape,
                Layer = settings.Layer,
                Position = transform.Position + settings.ShapeOffset,
                Rotation = transform.Rotation
            }
        );

        var handle = nextCharacterHandle++;
        charactersByHandle[handle] = controller;

        return new() { Handle = handle, BuiltShape = settings.Shape, BuiltOffset = settings.ShapeOffset };
    }

    void RemoveCharacter(Entity entity) {
        if (!Entities.TryGet<CharacterBody>(entity, out var body)
            || !charactersByHandle.Remove(body.Handle, out var controller)) {
            return;
        }

        controller.Dispose();
    }

    void DisposeCharacters() {
        foreach (var controller in charactersByHandle.Values) {
            controller.Dispose();
        }

        charactersByHandle.Clear();
    }
}

// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core;
using Vixen.Core.Threading;
using Vixen.Ecs;
using Vixen.Ecs.Systems;
using Vixen.Engine.Cameras;

namespace Vixen.Engine.Players;

/// <summary>
///     Keeps possession honest: forwards each player's intent to the pawn they are driving, points
///     their camera at it, and lets go of pawns that no longer exist.
/// </summary>
/// <remarks>
///     <para>
///         <b>It runs in <see cref="SystemPhase.Input" />, after <see cref="PlayerInputSystem" />,
///         and that is not a shortcut.</b> The thing that reacts to a player's intent is
///         <c>CharacterMovementSystem</c> in <see cref="SystemPhase.FixedUpdate" /> — the next phase.
///         A phase boundary is a hard sync where command buffers play back, so a pawn that gains its
///         <see cref="MoveIntent" /> here has it before the first fixed step of the same frame.
///         Putting this in <see cref="SystemPhase.EarlyUpdate" /> — the tempting place, because the
///         structural work belongs there — would forward <i>last</i> frame's intent, and the symptom
///         is one frame of input latency that no profiler attributes to a phase choice.
///     </para>
///     <para>
///         <b>The pawn's intent is a copy, and it is rewritten every frame.</b> Following the
///         <see cref="PossessedBy" /> edge from inside a movement sweep would turn a sequential walk
///         over a column into two random accesses per entity, to save four copies at the top of the
///         frame. The cost is that a system writing a possessed pawn's intent directly is silently
///         overwritten — which is why the pawn's copy is documented as derived, the way
///         <c>WorldTransform</c> is, and why a pawn nothing possesses is not visited at all.
///     </para>
///     <para>
///         <b>The camera needs no machinery.</b> A player's <see cref="ViewTarget" /> shot has its
///         <c>CameraTargets</c> written to whatever the player is driving, unconditionally, every
///         frame. [26](../../../docs/plan/26-virtual-cameras.md)'s director then blends because the
///         answer changed — there is no <c>SetViewTarget</c>, no blend curve to pass, and the write
///         self-heals if anything else clobbers it. A shot carrying a <c>PovAim</c> additionally
///         takes the player's aim, which is the whole of a first-person rig.
///     </para>
/// </remarks>
[UpdateInGroup(SystemPhase.Input)]
[UpdateAfter(typeof(PlayerInputSystem))]
public sealed class PossessionSystem : SystemBase, IDeclaredAccess {
    readonly QueryDescription driving = new QueryDescription().WithAll<PlayerController, Possessing>();

    readonly QueryDescription forwarding = new QueryDescription()
        .WithAll<PlayerController, Possessing, MoveIntent>();

    readonly List<Entity> stale = [];
    readonly List<(Entity Controller, Entity Pawn, MoveIntent Intent)> pending = [];

    /// <inheritdoc />
    /// <remarks>
    ///     Declared rather than attributed, for the reason <c>TransformSystem</c> gives: naming a
    ///     component type in a generic call is what assigns it an id, and an attribute can only look
    ///     one up.
    /// </remarks>
    public SystemAccess Access { get; } = SystemAccess.Declare()
        .Read<PlayerController>()
        .Read<Possessing>()
        .Read<ControlRotation>()
        .Read<ViewTarget>()
        .Write<MoveIntent>()
        .Write<PossessedBy>()
        .Write<CameraTargets>()
        .Write<PovAim>()
        .Write<OrbitBody>()
        .Build();

    /// <summary>Pawns that were let go this frame because they no longer exist.</summary>
    /// <remarks>
    ///     A counter and not a log line, for the reason <c>MispredictionCount</c> is one: a game
    ///     where this climbs every frame is destroying pawns without unpossessing, which is legal and
    ///     is also usually a mistake.
    /// </remarks>
    public long ReleasedCount { get; private set; }

    /// <inheritdoc />
    public override JobHandle Update(in SystemContext context, JobHandle dependency) {
        Apply(context.World, context.Commands);
        return dependency;
    }

    /// <summary>Runs one pass over every player.</summary>
    /// <param name="world">The world.</param>
    /// <param name="commands">
    ///     Where a pawn's first <see cref="MoveIntent" /> is attached, or <see langword="null" /> to
    ///     attach it at once.
    /// </param>
    /// <remarks>
    ///     Public so a test or a tool can settle possession without standing up a runner — the same
    ///     reason <c>VirtualCameraSystem.Evaluate</c> is.
    /// </remarks>
    public void Apply(World world, CommandBuffer? commands = null) {
        ArgumentNullException.ThrowIfNull(world);

        Reap(world);
        Forward(world, commands);
    }

    /// <summary>Lets go of pawns that have been destroyed.</summary>
    /// <remarks>
    ///     <b>The controller outliving its pawn is the ordinary case, not an error.</b> A player who
    ///     dies keeps their aim, their slot and their camera channel, and the only thing that has to
    ///     happen is that the edge stops naming a slot somebody else will eventually be given. Doing
    ///     it here rather than in <c>World.Destroy</c> is what keeps the ECS from knowing what a
    ///     player is.
    /// </remarks>
    void Reap(World world) {
        stale.Clear();

        foreach (var chunk in world.Chunks(driving)) {
            var possessing = chunk.ReadValues<Possessing>();
            var entities = chunk.Entities;

            for (var index = 0; index < chunk.Count; index++) {
                if (!world.IsAlive(possessing[index].Pawn)) {
                    stale.Add(entities[index]);
                }
            }
        }

        foreach (var controller in stale) {
            // Removed directly rather than through a command buffer: this is a structural change
            // made from a system, and Player.Unpossess is the one call that knows both halves of
            // the edge. The query above has already been fully walked into `stale`, so mutating the
            // archetypes now cannot invalidate an enumerator.
            Player.Unpossess(world, controller);
            ReleasedCount++;
        }
    }

    /// <summary>Copies each driver's intent onto what it is driving, and aims the camera.</summary>
    /// <remarks>
    ///     ⚠ <b>Collected first, applied after.</b> Attaching a <see cref="MoveIntent" /> or a
    ///     <c>CameraTargets</c> moves the receiving entity to another archetype, and nothing forbids
    ///     a pawn or a shot from also being a controller — a debug camera that drives itself is the
    ///     obvious case. Mutating from inside the chunk walk would then invalidate the walk. The
    ///     collection is four entries in a split-screen game, so this costs nothing worth measuring.
    /// </remarks>
    void Forward(World world, CommandBuffer? commands) {
        pending.Clear();

        foreach (var chunk in world.Chunks(forwarding)) {
            var possessing = chunk.ReadValues<Possessing>();
            var intents = chunk.ReadValues<MoveIntent>();
            var entities = chunk.Entities;

            for (var index = 0; index < chunk.Count; index++) {
                pending.Add((entities[index], possessing[index].Pawn, intents[index]));
            }
        }

        foreach (var (controller, pawn, intent) in pending) {
            if (world.Has<MoveIntent>(pawn)) {
                world.Set(pawn, intent);
            } else if (commands is null) {
                world.Add(pawn, intent);
            } else {
                // Played back at the phase boundary, which is before the fixed step that reads it —
                // so a pawn possessed this frame moves this frame.
                commands.Add(pawn, intent);
            }

            Aim(world, controller, pawn, commands);
        }
    }

    static void Aim(World world, Entity controller, Entity pawn, CommandBuffer? commands) {
        if (!world.TryGet<ViewTarget>(controller, out var view) || !world.IsAlive(view.Shot)) {
            return;
        }

        var targets = CameraTargets.Both(pawn);

        if (world.Has<CameraTargets>(view.Shot)) {
            world.Set(view.Shot, targets);
        } else if (commands is null) {
            world.Add(view.Shot, targets);
        } else {
            commands.Add(view.Shot, targets);
        }

        if (!world.TryGet<ControlRotation>(controller, out var rotation)) {
            return;
        }

        // A shot aiming by POV is a first-person camera, and the player's aim is what it is for. The
        // shot keeps its own pitch clamps: they may be tighter than the controller's for a scripted
        // moment, and a camera that showed less than the player could aim at is a deliberate effect
        // rather than a disagreement.
        if (world.Has<PovAim>(view.Shot)) {
            ref var aim = ref world.Get<PovAim>(view.Shot);
            aim.Yaw = rotation.Yaw;
            aim.Pitch = rotation.Pitch;
        }

        // And an orbit is a third-person one. OrbitBody's own remarks say it reads no device and
        // expects gameplay to write its two angles — this is that write, and it is what makes
        // OrbitBody a player's camera rather than FollowBody, which swings round as the *target*
        // turns and so cannot be steered.
        if (world.Has<OrbitBody>(view.Shot)) {
            ref var orbit = ref world.Get<OrbitBody>(view.Shot);
            orbit.Heading = rotation.Yaw;

            // ⚠ Negated, and it is not a sign slip. ControlRotation's pitch is positive looking *up*;
            // OrbitBody's is positive riding *above* the target and looking down. A player raising
            // their aim drops the camera and looks up past the character's shoulder, which is what
            // every third-person game does and what copying the sign across would exactly invert.
            orbit.Pitch = -rotation.Pitch;
        }
    }
}

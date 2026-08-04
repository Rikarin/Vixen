// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Vixen.Ai.Nodes.Ecs;
using Vixen.Core;
using Vixen.Core.Mathematics;
using Vixen.Ecs;
using Vixen.Engine.Transforms;
using Vixen.Navigation.Agents;
using Vixen.Navigation.Ecs;

namespace Vixen.Ai.Nodes;

/// <summary>What a movement task remembers between ticks.</summary>
/// <remarks>
///     ⚠ <b>The issued destination is kept so that a moving target is re-planned and a stationary one
///     is not.</b> Writing <c>NavigationDestination</c> every tick would bump its version every tick,
///     which is a full path search per agent per frame — the exact cost <c>NavPathQueue</c>'s budget
///     exists to bound, paid unconditionally.
/// </remarks>
[StructLayout(LayoutKind.Sequential)]
struct MoveState {
    public Vector3 Issued;
    public int Started;
}

/// <summary>Walks to a key's position or entity, over the navmesh.</summary>
/// <param name="key">The key holding a <c>Vector3</c> or an <c>Entity</c>.</param>
/// <param name="acceptance">How close counts as arrived, in metres, measured horizontally.</param>
/// <param name="repath">How far the target may move before the path is planned again, in metres.</param>
/// <remarks>
///     <para>
///         doc 37 § Part 3's <c>MoveTo</c>. It writes a <c>NavigationDestination</c> and reads a
///         <c>NavigationState</c>; the walking is <c>NavigationSystem</c>'s and the crowd's. That
///         split is what lets the task be twenty lines and what lets local avoidance, path slicing
///         and off-mesh links keep working underneath it without the tree knowing.
///     </para>
///     <para>
///         ⚠ <b>An entity target is re-planned when it moves and a <c>Vector3</c> one never is.</b>
///         Chasing something means the destination follows it, and <paramref name="repath" /> is what
///         keeps that from being a path search a frame: the target has to have actually gone
///         somewhere before the search is worth repeating.
///     </para>
///     <para>
///         ⚠ <b>Aborting stops the agent where it stands.</b> An abort is a higher-priority branch
///         taking over, and an agent that kept walking to a destination its tree has forgotten about
///         is the classic behaviour-tree bug — a guard that chases you while playing its idle.
///     </para>
/// </remarks>
public sealed class MoveToTask(BlackboardKey key, float acceptance = 1.5f, float repath = 1f) : IAgentAction {
    /// <summary>How many bytes it needs.</summary>
    public static int StateSize => Unsafe.SizeOf<MoveState>();

    /// <inheritdoc />
    public void Start(in AgentContext context, Span<byte> state) { }

    /// <inheritdoc />
    public ActionStatus Tick(in AgentContext context, Span<byte> state, float delta) {
        var world = context.World;
        var entity = context.Entity;

        if (!AgentTarget.TryResolve(in context, key, out var goal, out _)) {
            return ActionStatus.Failed;
        }

        if (!world.Has<NavigationAgent>(entity) || !world.Has<NavigationDestination>(entity)) {
            return ActionStatus.Failed;
        }

        ref var move = ref MemoryMarshal.AsRef<MoveState>(state);
        ref var destination = ref world.Get<NavigationDestination>(entity);

        if (move.Started == 0 || AgentTarget.FlatDistance(move.Issued, goal) > repath) {
            destination.Value = goal;
            destination.Version++;
            move.Issued = goal;
            move.Started = 1;
        }

        var here = world.Has<NavigationState>(entity)
            ? world.Get<NavigationState>(entity).Position
            : world.Get<LocalTransform>(entity).Position;

        if (AgentTarget.FlatDistance(here, goal) <= acceptance) {
            return ActionStatus.Succeeded;
        }

        // Failed rather than Running, so a branch whose destination is off the mesh or walled off
        // gives way to the next one instead of standing still until something else aborts it.
        if (world.Has<NavigationState>(entity) && world.Get<NavigationState>(entity).Target == CrowdTargetState.Failed) {
            return ActionStatus.Failed;
        }

        return ActionStatus.Running;
    }

    /// <inheritdoc />
    public void Abort(in AgentContext context, Span<byte> state) => Stop(in context);

    /// <summary>Tells the crowd to stop where the agent is.</summary>
    internal static void Stop(in AgentContext context) {
        var world = context.World;
        var entity = context.Entity;

        if (!world.IsAlive(entity) || !world.Has<NavigationDestination>(entity) || !world.Has<LocalTransform>(entity)) {
            return;
        }

        ref var destination = ref world.Get<NavigationDestination>(entity);

        destination.Value = world.Get<LocalTransform>(entity).Position;
        destination.Version++;
    }
}

/// <summary>Walks toward a key in a straight line, ignoring navigation entirely.</summary>
/// <param name="key">The key holding a <c>Vector3</c> or an <c>Entity</c>.</param>
/// <param name="speed">How fast, in metres a second.</param>
/// <param name="acceptance">How close counts as arrived, in metres.</param>
/// <remarks>
///     doc 37 § Part 3's <c>MoveDirectlyToward</c>, and it is not a worse <c>MoveTo</c>. A flier, a
///     swimmer, a possessed suit of armour walking through a wall on purpose, a boss in a room with
///     no mesh baked in it, and every prototype before the level has a navmesh — all of those want a
///     position updated toward a point and nothing else.
///
///     ⚠ It writes <c>LocalTransform</c> directly, so an entity that also has a
///     <c>NavigationAgent</c> will have this fought over by the crowd. Use one or the other.
/// </remarks>
public sealed class MoveDirectlyTowardTask(BlackboardKey key, float speed = 3f, float acceptance = 0.5f)
    : IAgentAction {
    /// <inheritdoc />
    public void Start(in AgentContext context, Span<byte> state) { }

    /// <inheritdoc />
    public ActionStatus Tick(in AgentContext context, Span<byte> state, float delta) {
        var world = context.World;
        var entity = context.Entity;

        if (!AgentTarget.TryResolve(in context, key, out var goal, out _) || !world.Has<LocalTransform>(entity)) {
            return ActionStatus.Failed;
        }

        ref var transform = ref world.Get<LocalTransform>(entity);
        var offset = goal - transform.Position;
        var distance = offset.Length();

        if (distance <= acceptance) {
            return ActionStatus.Succeeded;
        }

        var step = speed * delta;

        // Clamped to the remaining distance, so a governed agent handed a large delta arrives rather
        // than overshooting and oscillating around the target for ever.
        transform.Position += offset / distance * MathF.Min(step, distance);

        return AgentTarget.FlatDistance(transform.Position, goal) <= acceptance
            ? ActionStatus.Succeeded
            : ActionStatus.Running;
    }

    /// <inheritdoc />
    public void Abort(in AgentContext context, Span<byte> state) { }
}

/// <summary>What a patrol remembers.</summary>
[StructLayout(LayoutKind.Sequential)]
struct PatrolState {
    public int Index;
    public int Step;
    public int Started;
}

/// <summary>Walks a route from the entity's own <see cref="PatrolRoute" />.</summary>
/// <param name="acceptance">How close to a point counts as reaching it, in metres.</param>
/// <remarks>
///     <para>
///         doc 37 § Part 3's <c>Patrol</c>. The route is the level's data and the task is the asset's:
///         one tree runs every guard in the game and each of them carries the corridor it walks, so
///         there is no tree per route.
///     </para>
///     <para>
///         ⚠ <b>Only <see cref="PatrolMode.Forward" /> ever succeeds.</b> A loop and a ping-pong have
///         no end, so they stay <c>Running</c> for ever and are meant to be interrupted — by a
///         decorator observing a perception key, which is the whole shape of a patrolling guard.
///     </para>
/// </remarks>
public sealed class PatrolTask(float acceptance = 1.5f) : IAgentAction {
    /// <summary>How many bytes it needs.</summary>
    public static int StateSize => Unsafe.SizeOf<PatrolState>();

    /// <inheritdoc />
    public void Start(in AgentContext context, Span<byte> state) { }

    /// <inheritdoc />
    public ActionStatus Tick(in AgentContext context, Span<byte> state, float delta) {
        var world = context.World;
        var entity = context.Entity;

        if (!world.Has<PatrolRoute>(entity) || !world.Has<NavigationDestination>(entity)) {
            return ActionStatus.Failed;
        }

        var route = world.Get<PatrolRoute>(entity);

        if (route.Points is not { Length: > 1 } points) {
            return ActionStatus.Failed;
        }

        ref var patrol = ref MemoryMarshal.AsRef<PatrolState>(state);

        if (patrol.Started == 0) {
            patrol.Started = 1;
            patrol.Step = 1;
            patrol.Index = Nearest(world, entity, points);
            Issue(world, entity, points[patrol.Index]);
        }

        var here = world.Has<NavigationState>(entity)
            ? world.Get<NavigationState>(entity).Position
            : world.Get<LocalTransform>(entity).Position;

        if (AgentTarget.FlatDistance(here, points[patrol.Index]) > acceptance) {
            return ActionStatus.Running;
        }

        if (!Advance(ref patrol, route.Mode, points.Length)) {
            return ActionStatus.Succeeded;
        }

        Issue(world, entity, points[patrol.Index]);

        return ActionStatus.Running;
    }

    /// <inheritdoc />
    public void Abort(in AgentContext context, Span<byte> state) => MoveToTask.Stop(in context);

    /// <summary>Moves to the next point, or says the route is over.</summary>
    static bool Advance(ref PatrolState patrol, PatrolMode mode, int count) {
        var next = patrol.Index + patrol.Step;

        if (next >= 0 && next < count) {
            patrol.Index = next;

            return true;
        }

        switch (mode) {
            case PatrolMode.Loop:
                patrol.Index = next < 0 ? count - 1 : 0;

                return true;

            case PatrolMode.PingPong:
                // Turned round rather than wrapped, and the index steps *twice* off the end would be
                // the point next to the end — so it is reflected instead of reset, which is what
                // stops a two-point route standing still at one of them.
                patrol.Step = -patrol.Step;
                patrol.Index = Math.Clamp(patrol.Index + patrol.Step, 0, count - 1);

                return true;

            default:
                return false;
        }
    }

    /// <summary>
    ///     Which point to head for first. ⚠ The nearest, not the first: a guard that respawns
    ///     mid-route otherwise walks back to the start of it through whatever is in the way.
    /// </summary>
    static int Nearest(World world, Entity entity, ReadOnlySpan<Vector3> points) {
        if (!world.Has<LocalTransform>(entity)) {
            return 0;
        }

        var here = world.Get<LocalTransform>(entity).Position;
        var best = 0;
        var distance = float.MaxValue;

        for (var index = 0; index < points.Length; index++) {
            var candidate = AgentTarget.FlatDistance(here, points[index]);

            if (candidate >= distance) {
                continue;
            }

            distance = candidate;
            best = index;
        }

        return best;
    }

    static void Issue(World world, Entity entity, Vector3 point) {
        ref var destination = ref world.Get<NavigationDestination>(entity);

        destination.Value = point;
        destination.Version++;
    }
}

/// <summary>Turns to face a key, or the agent's focus, at a rate.</summary>
/// <param name="key">The key to face, or an invalid one to use <see cref="AiFocus" />.</param>
/// <param name="degreesPerSecond">How fast it turns.</param>
/// <param name="tolerance">How close counts as facing it, in degrees.</param>
/// <remarks>
///     doc 37 § Part 3's <c>RotateToward</c>. Yaw only, and deliberately: an agent that pitched to
///     look at something at its feet would lie down, and the vertical half of "looking at" belongs to
///     an aim offset or a head-look constraint rather than to the character's own transform.
/// </remarks>
public sealed class RotateTowardTask(BlackboardKey key, float degreesPerSecond = 360f, float tolerance = 5f)
    : IAgentAction {
    /// <inheritdoc />
    public void Start(in AgentContext context, Span<byte> state) { }

    /// <inheritdoc />
    public ActionStatus Tick(in AgentContext context, Span<byte> state, float delta) {
        var world = context.World;
        var entity = context.Entity;

        if (!AgentTarget.TryResolveOrFocus(in context, key, out var goal) || !world.Has<LocalTransform>(entity)) {
            return ActionStatus.Failed;
        }

        ref var transform = ref world.Get<LocalTransform>(entity);
        var offset = goal - transform.Position;

        if (AgentTarget.FlatDistance(transform.Position, goal) < 1e-3f) {
            // Standing on it. There is no direction to face, and turning to an arbitrary one would
            // read as a spin the moment a target walked into the agent.
            return ActionStatus.Succeeded;
        }

        var wanted = MathF.Atan2(-offset.X, -offset.Z);
        var current = Yaw(transform.Rotation);
        var difference = Wrap(wanted - current);
        var step = float.DegreesToRadians(degreesPerSecond) * delta;

        // ⚠ Succeeds on the tick that *lands* on the target, not on the one after it. Testing the
        // difference before applying the step means a turn that finishes exactly reports Running for
        // one more tick — which a sequence reads as the turn still going, and which under a governor
        // is a whole extra interval of standing still facing the right way.
        if (MathF.Abs(difference) <= MathF.Max(step, float.DegreesToRadians(tolerance))) {
            transform.Rotation = Quaternion.FromAxisAngle(Vector3.Up, wanted);

            return ActionStatus.Succeeded;
        }

        transform.Rotation = Quaternion.FromAxisAngle(Vector3.Up, current + (MathF.Sign(difference) * step));

        return ActionStatus.Running;
    }

    /// <inheritdoc />
    public void Abort(in AgentContext context, Span<byte> state) { }

    /// <summary>The yaw of a rotation, taken from where it puts the forward axis.</summary>
    /// <remarks>
    ///     Read off the transformed axis rather than decomposed from the quaternion, because an
    ///     entity whose rotation also has pitch or roll in it still has a well-defined heading and a
    ///     Euler decomposition of one is three answers with a gimbal between them.
    /// </remarks>
    internal static float Yaw(Quaternion rotation) {
        var forward = rotation == default ? Vector3.Forward : Quaternion.Transform(Vector3.Forward, rotation);

        return MathF.Atan2(-forward.X, -forward.Z);
    }

    /// <summary>An angle brought into (-π, π], so a turn takes the short way round.</summary>
    internal static float Wrap(float radians) {
        var wrapped = MathF.IEEERemainder(radians, MathF.Tau);

        return wrapped <= -MathF.PI ? wrapped + MathF.Tau : wrapped;
    }
}

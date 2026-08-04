// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core;
using Vixen.Core.Mathematics;
using Vixen.Ecs;
using Vixen.Engine.Transforms;

namespace Vixen.Ai.Nodes.Ecs;

/// <summary>What an agent is paying attention to.</summary>
/// <remarks>
///     <para>
///         Unreal's focus, and its value is that <b>everything downstream reads one place</b>. A
///         rotation task, an aim offset, a head-look constraint and a dialogue camera all want "what
///         is this character looking at", and without a component for it each of them takes its own
///         blackboard key and they disagree the first time one branch sets a key another does not.
///     </para>
///     <para>
///         ⚠ <b>An entity <i>or</i> a point, and the entity wins.</b> A focus on an entity follows it
///         as it moves; a focus on a point does not. Two fields rather than a discriminated one
///         because <see cref="Entity.Null" /> already means "there is no entity", so the tag would be
///         a second copy of a fact the value carries.
///     </para>
/// </remarks>
[Component]
[DataContract]
public struct AiFocus {
    /// <summary>What it is looking at, or <see cref="Entity.Null" /> for <see cref="Point" />.</summary>
    public Entity Target;

    /// <summary>Where it is looking, when there is no target.</summary>
    public Vector3 Point;

    /// <summary>Whether it is looking at anything at all.</summary>
    public bool HasFocus;

    /// <summary>Where the focus is now, following the target if there is one.</summary>
    /// <param name="world">The world.</param>
    /// <param name="position">Where to put it.</param>
    /// <returns>Whether there is a focus to resolve.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="world" /> is null.</exception>
    public readonly bool TryResolve(World world, out Vector3 position) {
        ArgumentNullException.ThrowIfNull(world);

        position = Point;

        if (!HasFocus) {
            return false;
        }

        if (Target.IsNull) {
            return true;
        }

        if (!world.IsAlive(Target) || !world.Has<LocalTransform>(Target)) {
            return false;
        }

        position = world.Get<LocalTransform>(Target).Position;

        return true;
    }

    /// <summary>A focus on an entity.</summary>
    /// <param name="target">The entity.</param>
    /// <returns>The component.</returns>
    public static AiFocus On(Entity target) => new() { Target = target, HasFocus = true };

    /// <summary>A focus on a place.</summary>
    /// <param name="point">The place.</param>
    /// <returns>The component.</returns>
    public static AiFocus At(Vector3 point) => new() { Point = point, HasFocus = true };
}

/// <summary>How a patrol walks its route.</summary>
public enum PatrolMode : byte {
    /// <summary>To the end, and then the task succeeds.</summary>
    Forward,

    /// <summary>To the end, then back, for ever.</summary>
    PingPong,

    /// <summary>Round and round, for ever.</summary>
    Loop
}

/// <summary>The route a patrol walks.</summary>
/// <remarks>
///     <para>
///         ⚠ <b>A component and not a task setting, because a route is a level's data and a task is an
///         asset's.</b> One `.vxbt` runs every guard in the game; the corridor each of them walks is
///         placed in the level editor. Putting the points on the task would mean a behaviour tree per
///         patrol route, which is the thing an authored tree exists to avoid.
///     </para>
///     <para>
///         A managed component — the struct holds an array — which is the storage
///         <c>docs/plan/04</c> describes for exactly this: a handful of entities, a variable-length
///         value, and no per-frame iteration over it.
///     </para>
/// </remarks>
[Component]
[DataContract]
public struct PatrolRoute {
    /// <summary>Where it goes, in order. A route with fewer than two points is not one.</summary>
    public Vector3[]? Points;

    /// <summary>How it walks them.</summary>
    public PatrolMode Mode;

    /// <summary>A route from a list of places.</summary>
    /// <param name="mode">How to walk it.</param>
    /// <param name="points">Where it goes.</param>
    /// <returns>The component.</returns>
    public static PatrolRoute Of(PatrolMode mode, params Vector3[] points) => new() { Points = points, Mode = mode };
}

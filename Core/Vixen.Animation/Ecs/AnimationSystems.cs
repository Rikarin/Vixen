// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Animation.Constraints;
using Vixen.Ecs.Systems;
using Vixen.Engine.Diagnostics;
using Vixen.Engine.Frames;

namespace Vixen.Animation.Ecs;

/// <summary>The animation passes, as a set a game registers in one line.</summary>
/// <remarks>
///     <para>
///         <b>The same shape <c>PhysicsSystems.AddPhysics</c> takes, and it was missing for as long as
///         animation had systems.</b> <see cref="EngineLoop" /> registers a default set and cannot
///         include these: <c>Vixen.Animation</c> references <c>Vixen.Engine</c>, so the dependency
///         only runs one way and the engine has no name for an animator. The result was that every
///         game had to know the passes exist and what order they go in.
///     </para>
///     <para>
///         ⚠ <b>Both passes are added whether or not a game has skinned characters.</b> Each costs one
///         query that matches nothing, and the alternative — a second registration call somebody has
///         to know to make — is a character that does not move for a reason nobody can see. That is
///         the argument the physics registration already makes about its character pass.
///     </para>
/// </remarks>
public static class AnimationSystems {
    /// <summary>Adds the evaluation pass and the skinning pass to a runner.</summary>
    /// <param name="runner">The runner.</param>
    /// <returns>The runner, for chaining.</returns>
    public static SystemRunner AddAnimation(this SystemRunner runner) {
        ArgumentNullException.ThrowIfNull(runner);

        return runner.Add(new AnimationSystem()).Add(new SkinningSystem());
    }

    /// <summary>Adds the animation passes to a loop.</summary>
    /// <param name="loop">The loop.</param>
    /// <returns>The loop, for chaining.</returns>
    public static EngineLoop AddAnimation(this EngineLoop loop) {
        ArgumentNullException.ThrowIfNull(loop);

        loop.Systems.AddAnimation();
        return loop;
    }

    /// <summary>Adds the constraint gizmo pass, switched off.</summary>
    /// <param name="loop">The loop.</param>
    /// <param name="draw">Where the lines go.</param>
    /// <returns>The system, so the caller can switch it on and narrow it.</returns>
    /// <remarks>
    ///     ⚠ <b>Separate from <see cref="AddAnimation(EngineLoop)" /> and returning the system rather
    ///     than the loop</b>, because a gizmo pass is only ever useful once somebody has said which
    ///     character they are looking at — a scene of thirty constrained characters drawn at once is a
    ///     thousand lines and nothing legible. Registering it is not the interesting half; getting
    ///     hold of it to set <see cref="ConstraintGizmoSystem.Only" /> is.
    /// </remarks>
    public static ConstraintGizmoSystem AddConstraintGizmos(this EngineLoop loop, DebugDraw draw) {
        ArgumentNullException.ThrowIfNull(loop);
        ArgumentNullException.ThrowIfNull(draw);

        var system = new ConstraintGizmoSystem(draw);

        loop.Add(system);
        return system;
    }
}

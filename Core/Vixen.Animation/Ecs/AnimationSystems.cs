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
///         ⚠ <b>For as long as these systems existed, nobody made that call</b>
///         (<a href="https://github.com/Rikarin/Vixen/issues/1221">#1221</a>). The one caller of
///         <see cref="AddAnimation(Vixen.Engine.Frames.EngineLoop)" /> anywhere in the tree was
///         <c>GizmoTests</c>, so none of the three passes below was in any game's loop — and because
///         none of them carries <c>[GameSystem]</c>, the generated registry did not add them
///         either. <c>[UpdateInGroup]</c> orders a system that has already been added and does not
///         add one. The consequence was wider than skinning: an <c>AnimatorComponent</c> in a
///         shipping game was never evaluated, so clip playback and root motion were unreachable too.
///         <c>PhysicsSystems.AddPhysics</c>, whose argument the paragraph below borrows, <em>was</em>
///         called — by Sample 13 — and that asymmetry was the whole diagnosis. Two callers now:
///         <c>Arena.Register</c> in Sample 13, beside its <c>AddPhysics</c>, and
///         <c>Vixen.Editor.App</c>'s <c>PlayAnimation</c>, so every in-editor play session evaluates
///         a project's animators.
///     </para>
///     <para>
///         ⚠ <b>And <c>[GameSystem]</c> is deliberately not the alternative, which is the other half
///         of what #1221 asked to be decided.</b> <c>GameSystemAttribute</c>'s own remarks make it
///         opt-in and say the engine's own systems do not carry it: a declared system is one a
///         <em>project</em> owns and is built out of a service registry, and these three take no
///         service. Annotating them would put three engine passes into the declared set of every
///         project that transitively links this assembly, decided here rather than by the host that
///         is running the frame.
///     </para>
///     <para>
///         ⚠ <b>Three passes now, not two.</b> <see cref="BlendShapeAnimationSystem" /> is the third
///         and it needs no renderer — it writes a component the render side reads — so it is added
///         unconditionally like the other two. A game with no morphed meshes walks a query that
///         matches nothing.
///     </para>
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
    /// <summary>Adds the evaluation, skinning and blend-shape passes to a runner.</summary>
    /// <param name="runner">The runner.</param>
    /// <returns>The runner, for chaining.</returns>
    public static SystemRunner AddAnimation(this SystemRunner runner) {
        ArgumentNullException.ThrowIfNull(runner);

        return runner
            .Add(new AnimationSystem())
            .Add(new SkinningSystem())
            .Add(new BlendShapeAnimationSystem());
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

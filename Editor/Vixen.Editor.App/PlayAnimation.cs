// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Animation.Ecs;
using Vixen.Editor.SceneView;

namespace Vixen.Editor.App;

/// <summary>The animation passes an in-editor play session runs.</summary>
/// <remarks>
///     <para>
///         ⚠ <b><c>AnimationSystems.AddAnimation</c> had no production caller at all until this
///         file</b> (<a href="https://github.com/Rikarin/Vixen/issues/1221">#1221</a>). The one call
///         in the tree was <c>GizmoTests</c>, so <c>AnimationSystem</c>, <c>SkinningSystem</c> and
///         <c>BlendShapeAnimationSystem</c> were in nothing's loop and an <c>AnimatorComponent</c>
///         was never evaluated — clip playback and root motion unreachable, not merely unskinned.
///         The registration existed and the argument for it was written down; the call was never
///         made, which is this repository's commonest defect shape.
///     </para>
///     <para>
///         ⚠ <b><c>[GameSystem]</c> is deliberately <em>not</em> the answer, and that decision is
///         #1221's other half.</b> <c>GameSystemAttribute</c>'s own remarks say it is opt-in and that
///         "the engine's own systems do not carry it": a declared system is built out of a service
///         registry by the project that owns it, and these three take no service and belong to
///         whoever runs a frame. Annotating them would put three engine passes into every project's
///         declared set whether the project links animation or not, and would move the decision out
///         of the host — which is exactly the seam <c>IPlaySystems</c> is.
///     </para>
///     <para>
///         <b>Unconditional, which is <c>PlayPhysics</c>' shape without its lifetime problem.</b>
///         None of the three owns anything: there is no scene to create, nothing to dispose and
///         nothing to provide to a later contribution. A session with no animated entity walks three
///         queries that match nothing, which is the cost <c>AnimationSystems</c>' remarks already
///         argue for against the alternative — a character that does not move for a reason nobody
///         can see.
///     </para>
///     <para>
///         ⚠ <b>What this does <em>not</em> close is the skinning join.</b>
///         <c>SkinningSystem.Renderer</c> and <c>.Feature</c> are still set by nobody, so the pass
///         returns on its first line and a skinned character draws in its bind pose — see
///         <see href="https://github.com/Rikarin/Vixen/issues/451" />. The difference is that the
///         seam an overload would widen is now a method something calls.
///     </para>
/// </remarks>
sealed class PlayAnimation : IPlaySystems {
    /// <inheritdoc />
    public void Attach(PlaySession session) {
        ArgumentNullException.ThrowIfNull(session);

        session.Loop.AddAnimation();
        session.Runs("animation");
    }
}

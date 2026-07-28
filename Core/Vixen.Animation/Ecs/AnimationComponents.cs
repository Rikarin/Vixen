// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Rendering;

namespace Vixen.Animation.Ecs;

/// <summary>An entity's animation.</summary>
/// <remarks>
///     <para>
///         A managed component — the struct holds a reference, so the ECS keeps the object in the
///         world's store and the chunk holds a handle. That is the right storage for this and not a
///         compromise: an <see cref="Animator" /> owns several arrays, a list of layers and a graph,
///         and none of that is a thing to copy into a chunk. What the chunk gets is the handle,
///         which is what the query iterates.
///     </para>
///     <para>
///         It also means the animation pass reaches its animators one entity at a time rather than
///         through a span, which is fine: the work per entity is a graph evaluation over a hundred
///         joints, and the pointer chase to reach it is not what that frame is spending its time on.
///     </para>
/// </remarks>
public struct AnimatorComponent {
    /// <summary>The animator.</summary>
    public Animator Value;
}

/// <summary>Where the last frame's root motion is left for gameplay to read.</summary>
/// <remarks>
///     Present only on entities whose animator is in <see cref="RootMotionMode.Extract" /> — with
///     <see cref="RootMotionMode.Apply" /> the transform has already moved and this would be a
///     second copy of the same fact. A character controller queries for this alongside its own
///     velocity and decides what survives the collision pass.
/// </remarks>
public struct RootMotionResult {
    /// <summary>How far the root moved, in the character's own frame.</summary>
    public RootMotionDelta Delta;
}

/// <summary>Which renderable an animated entity's bone palette belongs to.</summary>
/// <remarks>
///     The join between the two halves of skinning. Animation produces a pose; the renderer holds a
///     dense per-object array indexed by <see cref="RenderObjectId" />, and something has to say
///     which entity is which object. This is that something, and it is a component rather than a
///     dictionary because the answer is per entity and never changes within a frame.
/// </remarks>
public struct SkinnedRenderer {
    /// <summary>The renderable whose palette this entity's pose fills.</summary>
    public RenderObjectId RenderObject;
}

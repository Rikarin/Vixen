// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Animation.Moves;
using Vixen.Core.Mathematics;

namespace Vixen.Animation.Constraints;

/// <summary>A place a goal is expressed in: where it is, which way it faces, and how big it is.</summary>
/// <param name="Transform">The place, in the character's model space.</param>
/// <remarks>
///     <b>Model space, for <c>TwoBoneIk</c>'s reason.</b> A pose has no opinion about where the
///     character is standing, so every frame is brought into the pose's own space once, at resolve
///     time, rather than the solver converting per joint. A frame that starts life in world space
///     costs one transform here and nothing afterwards.
/// </remarks>
public readonly record struct Frame(BoneTransform Transform) {
    /// <summary>The frame at the model-space origin.</summary>
    public static Frame Identity => new(BoneTransform.Identity);

    /// <summary>Where it is.</summary>
    public Vector3 Origin => Transform.Translation;

    /// <summary>Which way it faces.</summary>
    public Quaternion Rotation => Transform.Rotation;

    /// <summary>How big it is. What makes a region scale with the body it is resolved against.</summary>
    public Vector3 Scale => Transform.Scale;

    /// <summary>A point in the frame, in model space.</summary>
    /// <param name="local">The point, in the frame's own space.</param>
    /// <returns>The point, in model space.</returns>
    public Vector3 ToModel(Vector3 local) =>
        Transform.Translation + Quaternion.Transform(local * Transform.Scale, Transform.Rotation);

    /// <summary>A direction in the frame, in model space.</summary>
    /// <param name="local">The direction, in the frame's own space.</param>
    /// <returns>The direction, in model space.</returns>
    public Vector3 DirectionToModel(Vector3 local) => Quaternion.Transform(local, Transform.Rotation);

    /// <summary>A model-space point, in the frame's own space.</summary>
    /// <param name="model">The point, in model space.</param>
    /// <returns>The point, in the frame's space.</returns>
    public Vector3 ToFrame(Vector3 model) {
        var rotated = Quaternion.Transform(model - Transform.Translation, Quaternion.Conjugate(Transform.Rotation));

        return new(
            Transform.Scale.X == 0f ? 0f : rotated.X / Transform.Scale.X,
            Transform.Scale.Y == 0f ? 0f : rotated.Y / Transform.Scale.Y,
            Transform.Scale.Z == 0f ? 0f : rotated.Z / Transform.Scale.Z
        );
    }

    /// <summary>Part of the way from one frame to another.</summary>
    /// <param name="from">Where to start.</param>
    /// <param name="to">Where to end.</param>
    /// <param name="amount">How far, in <c>[0, 1]</c>.</param>
    /// <returns>The frame between them.</returns>
    public static Frame Lerp(in Frame from, in Frame to, float amount) =>
        new(BoneTransform.Lerp(from.Transform, to.Transform, amount));
}

/// <summary>What a frame has to look at to work out where it is.</summary>
/// <remarks>
///     A <c>ref struct</c> because it carries the pose it is resolved against. Nothing may hold one
///     past the resolve pass, which is exactly the guarantee wanted: a frame that cached the pose
///     would be resolving against last frame's body.
/// </remarks>
public readonly ref struct ConstraintContext {
    /// <summary>The skeleton being posed.</summary>
    public required Skeleton Skeleton { get; init; }

    /// <summary>The pose as it stands, in model space.</summary>
    public required ReadOnlySpan<BoneTransform> Model { get; init; }

    /// <summary>Who the other parties are.</summary>
    public required ConstraintBindings Bindings { get; init; }

    /// <summary>
    ///     The character's own place in the world, for bringing a world-space frame into model space.
    /// </summary>
    public BoneTransform WorldTransform { get; init; }

    /// <summary>A world-space transform, in model space.</summary>
    /// <param name="world">The transform.</param>
    /// <returns>The same place, expressed against the pose.</returns>
    public BoneTransform WorldToModel(in BoneTransform world) =>
        BoneTransform.Concatenate(world, BoneTransform.Inverse(WorldTransform));
}

/// <summary>Where a goal is. The seam every kind of "somewhere" goes through.</summary>
/// <remarks>
///     <para>
///         ⚠ <b><see cref="TryResolve" /> returning <see langword="false" /> is the important
///         case.</b> A frame naming a bound entity that no longer exists, or a shape that is not
///         loaded, fails cleanly, and the stage eases the constraint out instead of snapping the
///         limb. Resolution failure is expected, not exceptional, and an implementation that throws
///         instead is one that turns a despawned prop into a crash.
///     </para>
/// </remarks>
public interface IConstraintFrame {
    /// <summary>Works out where the frame is.</summary>
    /// <param name="context">The pose and the bindings to resolve against.</param>
    /// <param name="frame">Where it is, in model space.</param>
    /// <returns>Whether it could be resolved at all.</returns>
    bool TryResolve(in ConstraintContext context, out Frame frame);
}

/// <summary>A fixed place in the world.</summary>
/// <param name="Transform">Where, in world space.</param>
public sealed record WorldFrame(BoneTransform Transform) : IConstraintFrame {
    /// <summary>A fixed point in the world, unrotated.</summary>
    /// <param name="position">Where.</param>
    public WorldFrame(Vector3 position) : this(new BoneTransform(position, Quaternion.Identity, Vector3.One)) {
    }

    /// <inheritdoc />
    public bool TryResolve(in ConstraintContext context, out Frame frame) {
        frame = new(context.WorldToModel(Transform));
        return true;
    }
}

/// <summary>A joint on the character's own skeleton.</summary>
/// <param name="Joint">Which joint.</param>
/// <param name="Offset">Where, relative to it.</param>
/// <remarks>
///     Already in model space, which is what makes a hand-to-hand or a hand-to-hip goal the cheapest
///     kind there is: no world round trip and no binding to fail.
/// </remarks>
public sealed record JointFrame(int Joint, BoneTransform Offset) : IConstraintFrame {
    /// <summary>A joint, with no offset.</summary>
    /// <param name="joint">Which joint.</param>
    public JointFrame(int joint) : this(joint, BoneTransform.Identity) {
    }

    /// <inheritdoc />
    public bool TryResolve(in ConstraintContext context, out Frame frame) {
        if ((uint)Joint >= (uint)context.Model.Length) {
            frame = default;
            return false;
        }

        frame = new(BoneTransform.Concatenate(Offset, context.Model[Joint]));
        return true;
    }
}

/// <summary>Whatever is bound to a named slot.</summary>
/// <param name="Slot">The slot — <c>"held-item"</c>, <c>"look-target"</c>.</param>
/// <param name="Offset">Where, relative to it.</param>
/// <remarks>
///     A clip's constraints name slots, so the same clip works against whatever is bound. Same
///     pattern as a UI data context.
/// </remarks>
public sealed record EntityFrame(Symbol Slot, BoneTransform Offset) : IConstraintFrame {
    /// <summary>A slot by name.</summary>
    /// <param name="slot">The slot.</param>
    public EntityFrame(string slot) : this(Symbol.Intern(slot), BoneTransform.Identity) {
    }

    /// <inheritdoc />
    public bool TryResolve(in ConstraintContext context, out Frame frame) {
        if (!context.Bindings.TryResolve(Slot, Symbol.None, out var world)) {
            frame = default;
            return false;
        }

        frame = new(context.WorldToModel(BoneTransform.Concatenate(Offset, world)));
        return true;
    }
}

/// <summary>A named attachment point on whatever is bound to a slot.</summary>
/// <param name="Slot">The slot.</param>
/// <param name="Socket">The attachment point on it — <c>"grip"</c>, <c>"muzzle"</c>.</param>
/// <param name="Offset">Where, relative to it.</param>
/// <remarks>
///     ⚠ <b>Separate from <see cref="EntityFrame" /> because the socket is the part that fails.</b> A
///     bound entity either exists or does not; a socket on it may simply not be there — a rifle model
///     with no <c>grip-left</c> — and that has to come back as an unresolved frame rather than as the
///     entity's origin, which would put a hand in the middle of the gun.
/// </remarks>
public sealed record SocketFrame(Symbol Slot, Symbol Socket, BoneTransform Offset) : IConstraintFrame {
    /// <summary>A socket by name.</summary>
    /// <param name="slot">The slot.</param>
    /// <param name="socket">The attachment point.</param>
    public SocketFrame(string slot, string socket)
        : this(Symbol.Intern(slot), Symbol.Intern(socket), BoneTransform.Identity) {
    }

    /// <inheritdoc />
    public bool TryResolve(in ConstraintContext context, out Frame frame) {
        if (!context.Bindings.TryResolve(Slot, Socket, out var world)) {
            frame = default;
            return false;
        }

        frame = new(context.WorldToModel(BoneTransform.Concatenate(Offset, world)));
        return true;
    }
}

/// <summary>Whatever the game wrote this frame, by name.</summary>
/// <param name="Name">What it is called.</param>
/// <remarks>
///     <para>
///         The escape hatch, and it is deliberately the plainest one: a raycast result, a navmesh
///         point, a spline sample, anything a game computes itself. World space, written once per
///         frame and cleared with the frame, so a stale answer cannot outlive the tick that produced
///         it — a goal whose provider stopped writing eases out rather than holding a position from
///         four seconds ago.
///     </para>
///     <para>
///         Determinism through this frame is the game's responsibility. Nothing here can check it.
///     </para>
/// </remarks>
public sealed record ProvidedFrame(Symbol Name) : IConstraintFrame {
    /// <summary>A provided frame by name.</summary>
    /// <param name="name">What it is called.</param>
    public ProvidedFrame(string name) : this(Symbol.Intern(name)) {
    }

    /// <inheritdoc />
    public bool TryResolve(in ConstraintContext context, out Frame frame) {
        if (!context.Bindings.TryGetProvided(Name, out var world)) {
            frame = default;
            return false;
        }

        frame = new(context.WorldToModel(world));
        return true;
    }
}

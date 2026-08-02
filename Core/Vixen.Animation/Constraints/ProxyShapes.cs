// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Animation.Moves;
using Vixen.Core.Mathematics;

namespace Vixen.Animation.Constraints;

/// <summary>Where a proxy shape currently is, and how big it currently is.</summary>
/// <param name="Shape">Which shape.</param>
/// <param name="Transform">Where, in the character's model space.</param>
/// <param name="Dimensions">How big, after whatever the poser did to it.</param>
public readonly record struct ProxyShapePose(ProxyShape Shape, BoneTransform Transform, ShapeParams Dimensions) {
    /// <summary>A point in the shape's own space, in model space.</summary>
    /// <param name="local">The point.</param>
    /// <returns>The point, in model space.</returns>
    public Vector3 ToModel(Vector3 local) =>
        Transform.Translation + Quaternion.Transform(local * Transform.Scale, Transform.Rotation);

    /// <summary>A model-space point, in the shape's own space.</summary>
    /// <param name="model">The point.</param>
    /// <returns>The point, in the shape's space.</returns>
    public Vector3 ToShape(Vector3 model) {
        var rotated = Quaternion.Transform(model - Transform.Translation, Quaternion.Conjugate(Transform.Rotation));

        return new(
            Transform.Scale.X == 0f ? 0f : rotated.X / Transform.Scale.X,
            Transform.Scale.Y == 0f ? 0f : rotated.Y / Transform.Scale.Y,
            Transform.Scale.Z == 0f ? 0f : rotated.Z / Transform.Scale.Z
        );
    }
}

/// <summary>How a proxy shape is placed and sized for the pose it is in.</summary>
/// <remarks>
///     A seam because a shape's size is not always a function of its joint. A belly that swells with
///     a breathing morph, a muscle bulge driven by a corrective, a shape a simulation is inflating:
///     all of them place the same primitive somewhere the joint hierarchy does not say.
/// </remarks>
public interface IProxyShapePoser {
    /// <summary>Places a shape.</summary>
    /// <param name="shape">The shape.</param>
    /// <param name="model">The pose, in model space.</param>
    /// <param name="posed">Where it is and how big.</param>
    /// <returns>Whether it could be placed at all.</returns>
    bool TryPose(ProxyShape shape, ReadOnlySpan<BoneTransform> model, out ProxyShapePose posed);
}

/// <summary>The shipped poser: the shape hangs off its joint and nothing else touches it.</summary>
public sealed class JointProxyShapePoser : IProxyShapePoser {
    /// <summary>The one every set uses unless it is given another.</summary>
    public static JointProxyShapePoser Shared { get; } = new();

    /// <inheritdoc />
    public bool TryPose(ProxyShape shape, ReadOnlySpan<BoneTransform> model, out ProxyShapePose posed) {
        ArgumentNullException.ThrowIfNull(shape);

        if ((uint)shape.Joint >= (uint)model.Length) {
            posed = default;
            return false;
        }

        posed = new(shape, BoneTransform.Concatenate(shape.Offset, model[shape.Joint]), shape.Dimensions);
        return true;
    }
}

/// <summary>One character's proxy shapes, posed only when something asks for them.</summary>
/// <remarks>
///     <para>
///         ⚠ <b>Lazy, and that is the second of the three reasons proxy shapes are not physics
///         colliders.</b> A character may carry a hundred of these; a frame typically touches two to
///         six. Posing all of them to serve two goals is a hundred transform compositions per
///         character per frame for nothing, so the stage walks the active goals, collects the shapes
///         their frames name, and poses only those. <see cref="PosedLastFrame" /> is what makes that
///         claim checkable rather than asserted.
///     </para>
///     <para>
///         Per-animator, because where a shape <em>is</em> is per-character even though the set is
///         shared. Not thread-safe, for <see cref="PoseScratch" />'s reason: one of these belongs to
///         one animator.
///     </para>
/// </remarks>
public sealed class ProxyShapes {
    readonly Dictionary<Symbol, ProxyShapePose> posed = [];
    int count;

    /// <summary>Creates a per-character view of a set.</summary>
    /// <param name="set">The shapes.</param>
    /// <param name="poser">How they are placed, or <see langword="null" /> for the shipped one.</param>
    public ProxyShapes(ProxyShapeSet set, IProxyShapePoser? poser = null) {
        ArgumentNullException.ThrowIfNull(set);

        Set = set;
        Poser = poser ?? JointProxyShapePoser.Shared;
    }

    /// <summary>The shapes at full detail.</summary>
    public ProxyShapeSet Set { get; }

    /// <summary>The shapes at reduced detail, or <see langword="null" /> for none.</summary>
    /// <remarks>
    ///     D22's <b>detail</b> knob, which is the one of the three that lives here. Which set answers
    ///     a surface frame is a function of distance; how often the stage solves and which chains it
    ///     solves at all are the other two, and they belong to the stack and the governor.
    /// </remarks>
    public ProxyShapeSet? Coarse { get; set; }

    /// <summary>Which detail level is in force. Zero is the full set.</summary>
    public byte Detail { get; set; }

    /// <summary>How a shape is placed.</summary>
    public IProxyShapePoser Poser { get; }

    /// <summary>The set answering right now.</summary>
    public ProxyShapeSet Active => Detail == 0 || Coarse is null ? Set : Coarse;

    /// <summary>How many shapes were actually posed on the last frame.</summary>
    /// <remarks>
    ///     ⚠ <b>The measurement P4 exists to make</b>: this is the number that has to track the goal
    ///     count and not <see cref="ProxyShapeSet.Count" />. A regression here is invisible in a
    ///     screenshot and shows up as a frame budget nobody can account for.
    /// </remarks>
    public int PosedLastFrame { get; private set; }

    /// <summary>Starts a frame, and reports what the last one cost.</summary>
    /// <remarks>Called by the stack at the top of a solve, before anything resolves.</remarks>
    public void Frame() {
        PosedLastFrame = count;
        count = 0;
        posed.Clear();
    }

    /// <summary>Forgets where the shapes were, because the pose moved.</summary>
    /// <remarks>
    ///     Called after the chains have been solved and before anything asks again. A shape cached
    ///     from before a hand moved is a shape in the wrong place, and the socket pass runs after the
    ///     hand has moved by construction.
    /// </remarks>
    public void Invalidate() => posed.Clear();

    /// <summary>Where a named shape is, posing it if nobody has yet this frame.</summary>
    /// <param name="name">Its name.</param>
    /// <param name="model">The pose, in model space.</param>
    /// <param name="pose">Where it is.</param>
    /// <returns>Whether the set has it and it could be placed.</returns>
    public bool TryPose(Symbol name, ReadOnlySpan<BoneTransform> model, out ProxyShapePose pose) {
        if (posed.TryGetValue(name, out pose)) {
            return true;
        }

        var index = Active.IndexOf(name);
        return index >= 0 && Pose(index, model, out pose);
    }

    /// <summary>Where the first shape affording something is.</summary>
    /// <param name="tag">The tag.</param>
    /// <param name="model">The pose, in model space.</param>
    /// <param name="pose">Where it is.</param>
    /// <returns>Whether anything affords it and could be placed.</returns>
    public bool TryPose(Facet tag, ReadOnlySpan<BoneTransform> model, out ProxyShapePose pose) {
        var index = Active.FirstTagged(tag);

        if (index < 0) {
            pose = default;
            return false;
        }

        return posed.TryGetValue(Active[index].Name, out pose) || Pose(index, model, out pose);
    }

    bool Pose(int index, ReadOnlySpan<BoneTransform> model, out ProxyShapePose pose) {
        var shape = Active[index];

        if (!Poser.TryPose(shape, model, out pose)) {
            return false;
        }

        posed[shape.Name] = pose;
        count++;

        return true;
    }

    /// <summary>Builds a reduced set: one box per tag group, enclosing every member of it.</summary>
    /// <param name="set">The full set.</param>
    /// <param name="skeleton">The skeleton it was authored against.</param>
    /// <param name="key">Which facet key groups the shapes — <c>region</c>, say.</param>
    /// <param name="name">What the reduced set is called.</param>
    /// <returns>The reduced set.</returns>
    /// <remarks>
    ///     <para>
    ///         <b>A box, whatever the members were.</b> A group may hold a capsule, a sphere and two
    ///         tapered boxes, and the smallest primitive enclosing a mixed group is only exact and
    ///         cheap for one kind — so the generator picks that one and says so, rather than choosing
    ///         a capsule that is either wrong or expensive to fit.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Generated, with a manual override, and not the other way round.</b> A coarse set
    ///         somebody has to author is a coarse set that goes stale the first time a shape moves.
    ///         <see cref="ProxyShape.Coarse" /> is the override: a shape that declares itself coarse
    ///         survives into the reduced set unmerged, which is what a hand needs when the whole point
    ///         of dropping detail is to keep the grip and lose the ribs.
    ///     </para>
    ///     <para>
    ///         Shapes with no value for the key are carried through unchanged — a group of one is not
    ///         worth a box around it.
    ///     </para>
    /// </remarks>
    public static ProxyShapeSet Coarsen(ProxyShapeSet set, Skeleton skeleton, Symbol key, string? name = null) {
        ArgumentNullException.ThrowIfNull(set);
        ArgumentNullException.ThrowIfNull(skeleton);

        List<ProxyShape> built = [];
        Dictionary<Symbol, List<ProxyShape>> groups = [];

        foreach (var shape in set.Shapes) {
            if (shape.Coarse || !shape.Tags.TryGet(key, out var group)) {
                built.Add(shape);
                continue;
            }

            if (!groups.TryGetValue(group, out var members)) {
                groups[group] = members = [];
            }

            members.Add(shape);
        }

        foreach (var (group, members) in groups.OrderBy(entry => entry.Key.Id)) {
            built.Add(members.Count == 1 ? members[0] : Enclose(skeleton, group, members));
        }

        return ProxyShapeSet.Of(name ?? $"{set.Name} (coarse)", null, [.. built]);
    }

    /// <summary>One box around a group, in the space of the joint nearest their common ancestor.</summary>
    static ProxyShape Enclose(Skeleton skeleton, Symbol group, List<ProxyShape> members) {
        var anchor = members[0].Joint;

        foreach (var member in members) {
            while (anchor >= 0 && anchor != member.Joint && !skeleton.IsDescendantOf(member.Joint, anchor)) {
                anchor = skeleton.ParentOf(anchor);
            }
        }

        anchor = Math.Max(anchor, 0);

        var low = new Vector3(float.MaxValue);
        var high = new Vector3(float.MinValue);
        var tags = members[0].Tags;

        foreach (var member in members) {
            // Bind-pose relative, because a coarse set is baked once and a shape's offset from its
            // joint is the only part of its placement that does not depend on what is playing.
            var offset = Relative(skeleton, member.Joint, anchor, member.Offset);
            var reach = Reach(member);

            low = Vector3.Min(low, offset.Translation - reach);
            high = Vector3.Max(high, offset.Translation + reach);
        }

        var centre = (low + high) * 0.5f;

        return new() {
            Name = Symbol.Intern($"coarse-{group}"),
            Kind = ShapeKind.Box,
            Joint = anchor,
            Offset = new(centre, Quaternion.Identity, Vector3.One),
            Dimensions = ShapeParams.Box((high - low) * 0.5f),
            Tags = tags,
            Coarse = true
        };
    }

    /// <summary>How far a shape reaches from its own origin, on each axis, however it is turned.</summary>
    static Vector3 Reach(ProxyShape shape) {
        var extents = Vector3.Max(shape.Dimensions.Extents, shape.Dimensions.TopExtents);

        if (shape.Kind is ShapeKind.Capsule or ShapeKind.TaperedCapsule) {
            extents = new(extents.X, extents.Y + MathF.Max(shape.Dimensions.Radius, shape.Dimensions.TopRadius), extents.Z);
        }

        var corner = Quaternion.Transform(extents * shape.Offset.Scale, shape.Offset.Rotation);
        return new(MathF.Abs(corner.X), MathF.Abs(corner.Y), MathF.Abs(corner.Z));
    }

    static BoneTransform Relative(Skeleton skeleton, int joint, int anchor, in BoneTransform offset) {
        var composed = offset;

        for (var walk = joint; walk >= 0 && walk != anchor; walk = skeleton.ParentOf(walk)) {
            composed = BoneTransform.Concatenate(composed, skeleton.BindPose[walk]);
        }

        return composed;
    }
}

// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Animation.Moves;
using Vixen.Core;
using Vixen.Core.Mathematics;

namespace Vixen.Animation.Constraints;

/// <summary>Which kind of place a frame names.</summary>
/// <remarks>
///     A discriminator rather than a serialised type name, because the set is closed and a file that
///     named a type would let a project's own <see cref="IConstraintFrame" /> into a format that
///     cannot resolve it. A project-specific frame reaches a clip through the extension block.
/// </remarks>
public enum ConstraintFrameKind : byte {
    /// <summary>A fixed place in the world.</summary>
    World,

    /// <summary>A joint on the character's own skeleton.</summary>
    Joint,

    /// <summary>Whatever is bound to a named slot.</summary>
    Entity,

    /// <summary>A named attachment point on whatever is bound to a slot.</summary>
    Socket,

    /// <summary>Whatever the game wrote this frame, by name.</summary>
    Provided,

    /// <summary>A place on the surface of one of the character's own proxy shapes.</summary>
    Surface,

    /// <summary>One of the character's own attachment points, after it has been adapted.</summary>
    Attachment
}

/// <summary>Where a goal is, as a file holds it.</summary>
/// <remarks>
///     ⚠ <b>Joints are named and not indexed</b>, for <c>AnimationChannel</c>'s reason: an index is a
///     fact about the rig the clip was marked up against, and a clip that survives a joint being
///     inserted is worth more than one that loads a byte faster. Resolution happens once, at
///     <see cref="ConstraintTagRecord.Bake(Skeleton, PriorityLadder?)" />.
/// </remarks>
[DataContract("ConstraintFrameRecord")]
public sealed class ConstraintFrameRecord {
    /// <summary>Which kind of place.</summary>
    public ConstraintFrameKind Kind { get; set; }

    /// <summary>The binding slot, for an entity or a socket.</summary>
    public string Slot { get; set; } = string.Empty;

    /// <summary>The attachment point's name, for a socket or an attachment.</summary>
    public string Socket { get; set; } = string.Empty;

    /// <summary>The joint, for a joint frame.</summary>
    public string Joint { get; set; } = string.Empty;

    /// <summary>What the game calls it, for a provided frame.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Which proxy shape, for a surface frame.</summary>
    public string Shape { get; set; } = string.Empty;

    /// <summary>What the shape has to afford, when it is named by tag rather than by name.</summary>
    public string Tag { get; set; } = string.Empty;

    /// <summary>Where the frame sits, or a fixed world position.</summary>
    public Vector3 Position { get; set; }

    /// <summary>Which way it faces.</summary>
    public Quaternion Rotation { get; set; } = Quaternion.Identity;

    /// <summary>Where the origin comes from, for a surface frame.</summary>
    public OriginSource Origin { get; set; }

    /// <summary>Which patch of the surface, for a surface frame.</summary>
    public int Face { get; set; } = -1;

    /// <summary>Around, in <c>[0, 1)</c>.</summary>
    public float U { get; set; }

    /// <summary>Along, in <c>[0, 1]</c>.</summary>
    public float V { get; set; }

    /// <summary>The direction out of the shape's centre, for the axis form.</summary>
    public Vector3 Direction { get; set; }

    /// <summary>The joint at the top of the limb, for the limb form.</summary>
    public string LimbFrom { get; set; } = string.Empty;

    /// <summary>The joint at the end of the limb.</summary>
    public string LimbTo { get; set; } = string.Empty;

    /// <summary>How far along it, in <c>[0, 1]</c>.</summary>
    public float Along { get; set; }

    /// <summary>The gap held off the surface, in the surface's own frame.</summary>
    public Vector3 Residual { get; set; }

    /// <summary>Where the orientation comes from.</summary>
    public OrientationSource Orientation { get; set; }

    /// <summary>Where the scale comes from.</summary>
    public ScaleSource Scale { get; set; }

    /// <summary>Resolves the record into the frame a solve uses.</summary>
    /// <param name="skeleton">The rig, for the frames that name a joint.</param>
    /// <returns>The frame, or <see langword="null" /> when it names nothing this rig has.</returns>
    public IConstraintFrame? Bake(Skeleton skeleton) {
        ArgumentNullException.ThrowIfNull(skeleton);

        var offset = new BoneTransform(Position, Rotation, Vector3.One);

        return Kind switch {
            ConstraintFrameKind.World => new WorldFrame(offset),
            ConstraintFrameKind.Joint => OnJoint(skeleton, Joint, offset),
            ConstraintFrameKind.Entity => new EntityFrame(Symbol.Intern(Slot), offset),
            ConstraintFrameKind.Socket => new SocketFrame(Symbol.Intern(Slot), Symbol.Intern(Socket), offset),
            ConstraintFrameKind.Provided => new ProvidedFrame(Symbol.Intern(Name)),
            ConstraintFrameKind.Attachment => new AttachmentFrame(Symbol.Intern(Socket), offset),
            _ => Surface(skeleton)
        };
    }

    static JointFrame? OnJoint(Skeleton skeleton, string name, in BoneTransform offset) {
        var joint = skeleton.IndexOf(name);
        return joint < 0 ? null : new JointFrame(joint, offset);
    }

    SurfaceFrame? Surface(Skeleton skeleton) {
        var coordinate = new SurfaceCoordinate {
            Shape = Symbol.Intern(Shape),
            Tag = ShapeTags.Parse(Tag),
            Origin = Origin,
            Point = new(Face, U, V),
            Direction = Direction,
            Residual = Residual,
            Orientation = Orientation,
            Scale = Scale
        };

        if (Origin is not (OriginSource.Limb or OriginSource.Joint)) {
            return new SurfaceFrame(coordinate);
        }

        var from = skeleton.IndexOf(LimbFrom);
        var to = skeleton.IndexOf(LimbTo);

        return from < 0 || to < 0
            ? null
            : new SurfaceFrame(coordinate with { Limb = new(from, to, Along, Vector3.Zero) });
    }
}

/// <summary>One constraint a clip carries, as a file holds it.</summary>
/// <remarks>
///     <para>
///         ⚠ <b>One flat record with a kind discriminator, and the union of every kind's fields.</b> A
///         hierarchy would read better here and would make the <em>inspector</em> worse: the panel has
///         to show only the fields the selected kind has, and it derives that from
///         <c>GoalKindSchema</c> rather than from the shape of the type. One record means one binder
///         path, one diff, one template merge and one place a new field is added.
///     </para>
///     <para>
///         <b>Priority is a name, not the integer.</b> A raw integer has no meaning across a project —
///         two authors pick 70 and 700 for the same intent and the arbitration between their clips is
///         an accident — so what is stored is a name from a declared <see cref="PriorityLadder" />,
///         and the integer is looked up at bake.
///     </para>
/// </remarks>
[DataContract("ConstraintTagRecord")]
public sealed class ConstraintTagRecord {
    /// <summary>What an author calls it. Shown on the bar; means nothing to a solve.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>What it asks for.</summary>
    public GoalKind Kind { get; set; }

    /// <summary>Whether it says where to be, or how far to move.</summary>
    public GoalMode Mode { get; set; }

    /// <summary>The joint it is about, by name.</summary>
    public string Effector { get; set; } = string.Empty;

    /// <summary>The joint at the top of the chain it may move, by name.</summary>
    public string Chain { get; set; } = string.Empty;

    /// <summary>Where in the clip it starts, in <c>[0, 1]</c>.</summary>
    public float Begin { get; set; }

    /// <summary>Where in the clip it ends, in <c>[0, 1]</c>.</summary>
    public float End { get; set; } = 1f;

    /// <summary>How much of the clip it takes to fade in, as a fraction.</summary>
    public float EaseIn { get; set; }

    /// <summary>How much of the clip it takes to fade out, as a fraction.</summary>
    public float EaseOut { get; set; }

    /// <summary>The most of it that ever applies.</summary>
    public float MaxWeight { get; set; } = 1f;

    /// <summary>Which rung of the project's declared ladder, by name.</summary>
    public string Priority { get; set; } = string.Empty;

    /// <summary>What other systems know it as, for querying and suppression.</summary>
    public string Label { get; set; } = string.Empty;

    /// <summary>The nearest detail level it applies at.</summary>
    public byte LodMin { get; set; }

    /// <summary>The furthest detail level it applies at.</summary>
    public byte LodMax { get; set; } = byte.MaxValue;

    /// <summary>Where the goal is.</summary>
    public ConstraintFrameRecord Goal { get; set; } = new();

    /// <summary>What an additive offset was measured against, or <see langword="null" />.</summary>
    public ConstraintFrameRecord? Reference { get; set; }

    /// <summary>Where in the goal's frame the point is, for a position goal.</summary>
    public Vector3 Offset { get; set; }

    /// <summary>Where on the effector the point being placed is.</summary>
    public Vector3 EffectorOffset { get; set; }

    /// <summary>Half-extents of the volume it may be anywhere inside.</summary>
    public Vector3 Region { get; set; }

    /// <summary>Where the middle joint bends towards. Zero keeps the current bend.</summary>
    public Vector3 Pole { get; set; }

    /// <summary>Which rotation, for an orientation goal.</summary>
    public Quaternion Rotation { get; set; } = Quaternion.Identity;

    /// <summary>How far off it may be and still count, in radians.</summary>
    public float Tolerance { get; set; }

    /// <summary>Which way the joint faces in its own space, for an aim goal.</summary>
    public Vector3 Axis { get; set; } = Vector3.Forward;

    /// <summary>Where on the joint the aim starts.</summary>
    public Vector3 Origin { get; set; }

    /// <summary>How far off the origin-to-frame vector the aim was authored.</summary>
    public Quaternion Deviation { get; set; } = Quaternion.Identity;

    /// <summary>How far away the thing being aimed at was when it was authored, in metres.</summary>
    public float AuthoredDistance { get; set; }

    /// <summary>The other joint, for a distance goal.</summary>
    public string Other { get; set; } = string.Empty;

    /// <summary>The closest they may be, in metres.</summary>
    public float Min { get; set; }

    /// <summary>The furthest they may be, in metres.</summary>
    public float Max { get; set; } = float.PositiveInfinity;

    /// <summary>The template this tag came from, or empty if somebody placed it by hand.</summary>
    /// <remarks>
    ///     What makes a re-apply possible: a template that changes can find the tags it produced, and
    ///     leave the hand-placed ones alone.
    /// </remarks>
    public string Template { get; set; } = string.Empty;

    /// <summary>Which version of that template, so a re-apply knows what it is upgrading from.</summary>
    public int TemplateVersion { get; set; }

    /// <summary>Resolves the tag into what a stack solves.</summary>
    /// <param name="skeleton">The rig it is played on.</param>
    /// <param name="ladder">The project's priority names, or <see langword="null" /> for zero.</param>
    /// <returns>The tag, or <see langword="null" /> when it names a joint this rig does not have.</returns>
    /// <remarks>
    ///     ⚠ <b>A tag naming a joint the rig does not have resolves to nothing rather than to joint
    ///     zero.</b> Joint zero is the character's root, and a contact resolving there is a hand in
    ///     somebody's pelvis — much harder to diagnose than a contact that does nothing.
    /// </remarks>
    public ConstraintTag? Bake(Skeleton skeleton, PriorityLadder? ladder = null) {
        ArgumentNullException.ThrowIfNull(skeleton);

        var effector = skeleton.IndexOf(Effector);

        if (effector < 0) {
            return null;
        }

        var first = Chain.Length == 0 ? effector : skeleton.IndexOf(Chain);

        if (first < 0) {
            return null;
        }

        var goal = Goal.Bake(skeleton);
        var reference = Reference?.Bake(skeleton);

        if (goal is null && Goal.Kind is not ConstraintFrameKind.World) {
            return null;
        }

        var priority = ladder?.Value(Priority) ?? 0;

        ConstraintGoal? built = Kind switch {
            GoalKind.Position => new PositionGoal {
                Effector = effector,
                Chain = new(first, effector),
                Goal = goal,
                Reference = reference,
                Mode = Mode,
                Priority = priority,
                Label = Symbol.Intern(Label),
                Lods = new(LodMin, LodMax),
                MaxWeight = MaxWeight,
                Pole = Pole,
                Offset = Offset,
                EffectorOffset = EffectorOffset,
                Region = Region
            },
            GoalKind.Orientation => new OrientationGoal {
                Effector = effector,
                Chain = new(first, effector),
                Goal = goal,
                Reference = reference,
                Mode = Mode,
                Priority = priority,
                Label = Symbol.Intern(Label),
                Lods = new(LodMin, LodMax),
                MaxWeight = MaxWeight,
                Rotation = Rotation,
                Region = Tolerance
            },
            GoalKind.Aim => new AimGoal {
                Effector = effector,
                Chain = new(first, effector),
                Goal = goal,
                Reference = reference,
                Mode = Mode,
                Priority = priority,
                Label = Symbol.Intern(Label),
                Lods = new(LodMin, LodMax),
                MaxWeight = MaxWeight,
                Axis = Axis,
                Origin = Origin,
                Deviation = Deviation,
                AuthoredDistance = AuthoredDistance,
                Region = Tolerance
            },
            _ => Distance(skeleton, effector, first, goal, reference, priority)
        };

        return built is null
            ? null
            : new ConstraintTag {
                Goal = built,
                Begin = Begin,
                End = End,
                EaseIn = EaseIn,
                EaseOut = EaseOut,
                MaxWeight = MaxWeight
            };
    }

    DistanceGoal? Distance(
        Skeleton skeleton,
        int effector,
        int first,
        IConstraintFrame? goal,
        IConstraintFrame? reference,
        int priority
    ) {
        var other = skeleton.IndexOf(Other);

        return other < 0
            ? null
            : new DistanceGoal {
                Effector = effector,
                Other = other,
                Chain = new(first, effector),
                Goal = goal,
                Reference = reference,
                Mode = Mode,
                Priority = priority,
                Label = Symbol.Intern(Label),
                Lods = new(LodMin, LodMax),
                MaxWeight = MaxWeight,
                Min = Min,
                Max = Max
            };
    }

    /// <summary>Resolves a whole track, skipping what this rig cannot carry.</summary>
    /// <param name="records">The tags.</param>
    /// <param name="skeleton">The rig.</param>
    /// <param name="ladder">The project's priority names.</param>
    /// <param name="unresolved">Where the names of the tags that were skipped go.</param>
    /// <returns>The track, or <see langword="null" /> when nothing resolved.</returns>
    public static ConstraintTrack? Bake(
        IReadOnlyList<ConstraintTagRecord> records,
        Skeleton skeleton,
        PriorityLadder? ladder = null,
        ICollection<string>? unresolved = null
    ) {
        ArgumentNullException.ThrowIfNull(records);

        if (records.Count == 0) {
            return null;
        }

        List<ConstraintTag> built = [];

        foreach (var record in records) {
            if (record.Bake(skeleton, ladder) is { } tag) {
                built.Add(tag);
                continue;
            }

            unresolved?.Add(record.Name.Length > 0 ? record.Name : record.Effector);
        }

        return built.Count == 0 ? null : new ConstraintTrack([.. built]);
    }
}

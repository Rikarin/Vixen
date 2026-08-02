// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

namespace Vixen.Animation.Constraints;

/// <summary>What sort of control a field wants.</summary>
public enum GoalFieldKind : byte {
    /// <summary>A number.</summary>
    Number,

    /// <summary>A fraction of the clip, in <c>[0, 1]</c>.</summary>
    Phase,

    /// <summary>An angle, shown in degrees and stored in radians.</summary>
    Angle,

    /// <summary>A distance, in metres.</summary>
    Distance,

    /// <summary>Three numbers.</summary>
    Vector,

    /// <summary>A rotation.</summary>
    Rotation,

    /// <summary>A joint, picked from the rig.</summary>
    Joint,

    /// <summary>A place — the frame picker.</summary>
    Frame,

    /// <summary>A rung of the project's priority ladder.</summary>
    Priority,

    /// <summary>A free word other systems match on.</summary>
    Label,

    /// <summary>One of a fixed set.</summary>
    Choice,

    /// <summary>A detail range.</summary>
    Lod
}

/// <summary>One field of a constraint tag, as a panel needs to know about it.</summary>
/// <param name="Property">
///     The name of the property on <see cref="ConstraintTagRecord" /> it reads and writes.
/// </param>
/// <param name="Label">What to call it on screen.</param>
/// <param name="Kind">What sort of control it wants.</param>
/// <param name="Help">What it does, in a sentence.</param>
/// <param name="Advanced">Whether it belongs behind a disclosure rather than in the first screenful.</param>
public readonly record struct GoalField(
    string Property,
    string Label,
    GoalFieldKind Kind,
    string Help,
    bool Advanced = false
);

/// <summary>Which fields each kind of goal has, and what to call them.</summary>
/// <remarks>
///     <para>
///         ⚠ <b>The inspector is generated from this, and that is the difference between a markup tool
///         people use and one they avoid.</b> A position goal has no aim axis; a panel that showed one
///         is a panel where every field is suspect, and an author who cannot tell which fields matter
///         stops trusting all of them. Hand-writing a panel per kind gets the first version right and
///         drifts on the second, because a field added to the record has no reason to appear in four
///         separate panels.
///     </para>
///     <para>
///         ⚠ <b>Every property of <see cref="ConstraintTagRecord" /> appears here or is deliberately
///         hidden, and a test asserts it.</b> That is the mechanism that stops the drift: adding a
///         field to the record and forgetting the schema fails the build's tests rather than shipping a
///         field nobody can edit.
///     </para>
/// </remarks>
public static class GoalKindSchema {
    /// <summary>The fields every kind has: when it runs and how much it matters.</summary>
    public static ReadOnlySpan<GoalField> Common => Shared;

    static readonly GoalField[] Shared = [
        new("Name", "Name", GoalFieldKind.Label, "What to call this on the track. Means nothing to a solve."),
        new("Effector", "Effector", GoalFieldKind.Joint, "The joint this is about."),
        new("Chain", "Chain from", GoalFieldKind.Joint, "The joint at the top of what may move to satisfy it."),
        new("Goal", "Goal", GoalFieldKind.Frame, "Where it is."),
        new("Begin", "Begin", GoalFieldKind.Phase, "Where in the clip it starts."),
        new("End", "End", GoalFieldKind.Phase, "Where in the clip it ends. Before Begin means it straddles the loop."),
        new("EaseIn", "Ease in", GoalFieldKind.Phase, "How much of the clip it takes to fade in."),
        new("EaseOut", "Ease out", GoalFieldKind.Phase, "How much of the clip it takes to fade out."),
        new("MaxWeight", "Weight", GoalFieldKind.Number, "The most of it that ever applies."),
        new("Priority", "Priority", GoalFieldKind.Priority, "Which rung of the project's ladder."),
        new("Label", "Label", GoalFieldKind.Label, "What other systems know it as, for suppression."),
        new("Mode", "Mode", GoalFieldKind.Choice, "Whether it says where to be, or how far to move from there."),
        new("Reference", "Measured against", GoalFieldKind.Frame, "What an additive offset was captured against.", true),
        new("LodMin", "From detail", GoalFieldKind.Lod, "The nearest detail level it applies at.", true),
        new("LodMax", "To detail", GoalFieldKind.Lod, "The furthest detail level it applies at.", true)
    ];

    static readonly GoalField[] PositionFields = [
        new("Offset", "Offset", GoalFieldKind.Vector, "Where in the goal's frame the point is."),
        new("Region", "Region", GoalFieldKind.Vector, "Half-extents of a volume it may be anywhere inside. Zero is a point."),
        new("EffectorOffset", "On the effector", GoalFieldKind.Vector, "Where on the joint the point being placed is."),
        new("Pole", "Pole", GoalFieldKind.Vector, "Where the middle joint bends towards. Zero keeps the current bend.", true)
    ];

    static readonly GoalField[] OrientationFields = [
        new("Rotation", "Rotation", GoalFieldKind.Rotation, "Which way, in the goal's frame."),
        new("Tolerance", "Tolerance", GoalFieldKind.Angle, "How far off it may be and still count.")
    ];

    static readonly GoalField[] AimFields = [
        new("Axis", "Axis", GoalFieldKind.Vector, "Which way the joint faces in its own space."),
        new("Origin", "Origin", GoalFieldKind.Vector, "Where on the joint the aim starts."),
        new("Deviation", "Deviation", GoalFieldKind.Rotation, "How far off the origin-to-target vector it was authored."),
        new(
            "AuthoredDistance",
            "Authored at",
            GoalFieldKind.Distance,
            "How far away the target was when this was authored. What makes the aim retarget."
        ),
        new("Tolerance", "Tolerance", GoalFieldKind.Angle, "How far off it may be and still count.")
    ];

    static readonly GoalField[] DistanceFields = [
        new("Other", "Other joint", GoalFieldKind.Joint, "The joint the separation is measured to."),
        new("Min", "At least", GoalFieldKind.Distance, "The closest they may be."),
        new("Max", "At most", GoalFieldKind.Distance, "The furthest they may be.")
    ];

    /// <summary>The fields one kind of goal has, on top of <see cref="Common" />.</summary>
    /// <param name="kind">The kind.</param>
    /// <returns>The fields.</returns>
    public static ReadOnlySpan<GoalField> For(GoalKind kind) =>
        kind switch {
            GoalKind.Position => PositionFields,
            GoalKind.Orientation => OrientationFields,
            GoalKind.Aim => AimFields,
            _ => DistanceFields
        };

    /// <summary>Whether a field belongs on the panel for a kind.</summary>
    /// <param name="kind">The kind.</param>
    /// <param name="property">The property's name.</param>
    /// <returns>Whether it does.</returns>
    public static bool Shows(GoalKind kind, string property) {
        foreach (var field in Common) {
            if (string.Equals(field.Property, property, StringComparison.Ordinal)) {
                return true;
            }
        }

        foreach (var field in For(kind)) {
            if (string.Equals(field.Property, property, StringComparison.Ordinal)) {
                return true;
            }
        }

        return false;
    }

    /// <summary>The properties of a tag no panel shows, and why.</summary>
    /// <remarks>
    ///     ⚠ <b>Listed rather than inferred, so that forgetting a field is a failure and hiding one is
    ///     a decision.</b> A property missing from both this and the schema fails the test that walks
    ///     the record — which is the only thing that keeps the two from drifting.
    /// </remarks>
    public static IReadOnlyDictionary<string, string> Hidden { get; } = new Dictionary<string, string>(StringComparer.Ordinal) {
        ["Kind"] = "Chosen when the tag is created; changing it would mean a different set of fields.",
        ["Template"] = "Written by the template that produced the tag, and read by a re-apply.",
        ["TemplateVersion"] = "The same."
    };
}

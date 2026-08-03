// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;
using Vixen.Editor.Core;

namespace Vixen.Editor.SceneView;

/// <summary>What the gizmo changes.</summary>
public enum GizmoMode {
    /// <summary>Nothing. Clicks select and drags rubber-band.</summary>
    None,

    /// <summary>Position.</summary>
    Translate,

    /// <summary>Rotation.</summary>
    Rotate,

    /// <summary>Scale.</summary>
    Scale,

    /// <summary>All three at once, as one set of handles.</summary>
    Transform
}

/// <summary>Which axes the handles point along.</summary>
public enum GizmoSpace {
    /// <summary>The world's axes.</summary>
    World,

    /// <summary>The object's own.</summary>
    Local,

    /// <summary>Its parent's, which is the space its stored transform is in.</summary>
    /// <remarks>
    ///     The one people ask for once they have a rotated parent: dragging "along X" then moves the
    ///     object along the axis its own <c>LocalTransform.Position</c> is expressed in, so the number
    ///     in the inspector changes by the amount that was dragged.
    /// </remarks>
    Parent,

    /// <summary>The camera's, so up is up on screen.</summary>
    Screen
}

/// <summary>Where the handles sit when several things are selected.</summary>
public enum PivotMode {
    /// <summary>On the last-clicked object's own origin.</summary>
    Pivot,

    /// <summary>At the middle of everything selected.</summary>
    Center
}

/// <summary>One grabbable part of the gizmo.</summary>
public enum GizmoHandle {
    /// <summary>Nothing under the pointer.</summary>
    None,

    /// <summary>The X arm.</summary>
    AxisX,

    /// <summary>The Y arm.</summary>
    AxisY,

    /// <summary>The Z arm.</summary>
    AxisZ,

    /// <summary>The quad between Y and Z.</summary>
    PlaneYZ,

    /// <summary>The quad between Z and X.</summary>
    PlaneZX,

    /// <summary>The quad between X and Y.</summary>
    PlaneXY,

    /// <summary>The ring or square facing the camera.</summary>
    Screen,

    /// <summary>The middle box, which scales everything at once.</summary>
    Uniform
}

/// <summary>A drag that has finished, handed to the targets so they can say what it was.</summary>
/// <param name="Targets">Everything the gizmo was holding, in the order it took hold of them.</param>
/// <param name="Mode">Which handles were on.</param>
/// <param name="Handle">Which one was grabbed.</param>
/// <param name="Captured">What each target held at mouse-down, one per target.</param>
/// <param name="Document">The viewport's document, which is where a target with no opinion records.</param>
/// <remarks>
///     ⚠ <b>The drag has already been applied when this is built.</b> The gizmo owns the live
///     manipulation and writes the targets every frame; what is left at mouse-up is the history, and
///     <paramref name="Captured" /> is the only place the before state still exists.
/// </remarks>
public readonly record struct GizmoDrag(
    IReadOnlyList<IGizmoTarget> Targets,
    GizmoMode Mode,
    GizmoHandle Handle,
    IReadOnlyList<(Vector3 Position, Quaternion Rotation, Vector3 Scale)> Captured,
    EditorDocument? Document
) {
    /// <summary>What the drag did, for the undo entry's name.</summary>
    /// <remarks>
    ///     ⚠ <b>What the drag did, not what the tool is called.</b> The combined gizmo's middle box is
    ///     the uniform scale handle, so a drag of it in <see cref="GizmoMode.Transform" /> is a scale
    ///     and the history has to say so.
    /// </remarks>
    public string Verb => Mode switch {
        GizmoMode.Rotate => "Rotate",
        GizmoMode.Scale => "Scale",
        GizmoMode.Transform when Handle == GizmoHandle.Uniform => "Scale",
        _ => "Move"
    };
}

/// <summary>What a finished drag turned into: the entry that undoes it, and the history it belongs on.</summary>
/// <param name="Command">The entry.</param>
/// <param name="Stack">Where it goes, or <see langword="null" /> when nothing is recording.</param>
/// <remarks>
///     ⚠ <b>The stack comes from the target, which is the whole point.</b> A proxy shape belongs to a
///     shape set and its edit belongs on that set's history, not on whichever scene happened to be
///     showing it; an entity belongs to the viewport's document. A null stack is the case where
///     something is being previewed rather than edited — the drag still happened, and there is
///     nothing to take it back with.
/// </remarks>
public readonly record struct GizmoEdit(IEditorCommand Command, CommandStack? Stack);

/// <summary>Something the gizmo can move: a transform it can read and write.</summary>
/// <remarks>
///     <para>
///         An interface rather than an entity, so that the gizmo's whole of its arithmetic is testable
///         with no world, no renderer and no device — which is what makes "does dragging X by fifteen
///         pixels move it the right distance" a unit test rather than a screenshot somebody looks at.
///     </para>
///     <para>
///         ⚠ <b>Whoever supplies the targets owns the undo entry, and <see cref="Record" /> is where
///         that is written down.</b> It used to be a hook on the viewport plus a type test beside it,
///         which meant the viewport held a list of the exceptions to its own rule and a third kind of
///         target was a third exception. A target knows what document it came out of and what its
///         edit is called; nothing else does.
///     </para>
/// </remarks>
public interface IGizmoTarget {
    /// <summary>Where it is, in world space.</summary>
    Vector3 Position { get; set; }

    /// <summary>How it is turned, in world space.</summary>
    Quaternion Rotation { get; set; }

    /// <summary>How big it is, in its parent's space.</summary>
    Vector3 Scale { get; set; }

    /// <summary>Its parent's local-to-world matrix, or the identity for a root.</summary>
    Matrix4x4 ParentToWorld { get; }

    /// <summary>Turns a finished drag into the entry that undoes it.</summary>
    /// <param name="drag">The drag, already applied.</param>
    /// <returns>The entry and where it goes, or <see langword="null" /> if nothing moved.</returns>
    /// <remarks>
    ///     ⚠ <b>Asked of the first target only, and it answers for the whole group.</b> A drag is over
    ///     one kind of target — the gizmo takes hold of what the selection produced — and a rotate
    ///     about a group's centre moves every object as well as turning it, so one entry over all of
    ///     them is the only one that undoes coherently. <paramref name="drag" /> carries the rest.
    /// </remarks>
    GizmoEdit? Record(in GizmoDrag drag);
}

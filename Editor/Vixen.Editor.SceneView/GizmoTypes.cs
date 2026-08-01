// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;

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

/// <summary>Something the gizmo can move: a transform it can read and write.</summary>
/// <remarks>
///     An interface rather than an entity, so that the gizmo's whole of its arithmetic is testable
///     with no world, no renderer and no device — which is what makes "does dragging X by fifteen
///     pixels move it the right distance" a unit test rather than a screenshot somebody looks at.
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
}

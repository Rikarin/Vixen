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

/// <summary>What a drag rounds to.</summary>
/// <remarks>
///     <para>
///         <b>Snapping rounds the <i>total</i> of a drag, not each step of it.</b> Rounding each
///         frame's delta accumulates the rounding error, so a slow drag across ten grid squares lands
///         somewhere between two of them and a fast one lands on a line. Every implementation gets
///         this wrong once.
///     </para>
///     <para>
///         The steps are separate numbers because they are separate decisions: a level built on a
///         quarter-metre grid still wants fifteen-degree rotations.
///     </para>
/// </remarks>
public sealed class SnapSettings {
    /// <summary>Whether a translate rounds to <see cref="GridStep" />.</summary>
    public bool SnapPosition { get; set; }

    /// <summary>Whether a rotate rounds to <see cref="AngleStep" />.</summary>
    public bool SnapRotation { get; set; }

    /// <summary>Whether a scale rounds to <see cref="ScaleStep" />.</summary>
    public bool SnapScale { get; set; }

    /// <summary>How far apart the grid lines are, in world units.</summary>
    public float GridStep { get; set; } = 1f;

    /// <summary>How far one rotation step is, in degrees.</summary>
    public float AngleStep { get; set; } = 15f;

    /// <summary>How far one scale step is, as a factor.</summary>
    public float ScaleStep { get; set; } = 0.1f;

    /// <summary>Whether a drop lands on the surface under the pointer.</summary>
    public bool SnapToSurface { get; set; }

    /// <summary>Whether a drag lands on the nearest vertex of what is under the pointer.</summary>
    public bool SnapToVertex { get; set; }

    /// <summary>How near a vertex has to be to be snapped to, in render pixels.</summary>
    public float VertexRadius { get; set; } = 12f;

    /// <summary>Rounds a distance to the grid, if position snapping is on.</summary>
    /// <param name="value">The distance.</param>
    /// <returns>The rounded distance.</returns>
    public float Position(float value) => Round(value, SnapPosition ? GridStep : 0f);

    /// <summary>Rounds an offset to the grid, component by component.</summary>
    /// <param name="value">The offset.</param>
    /// <returns>The rounded offset.</returns>
    public Vector3 Position(Vector3 value) =>
        SnapPosition ? new(Position(value.X), Position(value.Y), Position(value.Z)) : value;

    /// <summary>Rounds an angle in radians to the angle step, if rotation snapping is on.</summary>
    /// <param name="radians">The angle.</param>
    /// <returns>The rounded angle.</returns>
    public float Rotation(float radians) =>
        SnapRotation
            ? MathUtil.DegreesToRadians(Round(MathUtil.RadiansToDegrees(radians), AngleStep))
            : radians;

    /// <summary>Rounds a scale factor, if scale snapping is on.</summary>
    /// <param name="factor">The factor.</param>
    /// <returns>The rounded factor.</returns>
    public float Scale(float factor) => Round(factor, SnapScale ? ScaleStep : 0f);

    static float Round(float value, float step) =>
        step <= 0f ? value : MathF.Round(value / step) * step;
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

// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;

namespace Vixen.Editor.SceneView;

/// <summary>What a position may be rounded or moved onto.</summary>
/// <remarks>
///     ⚠ <b>A set rather than a choice, because they compose and they compose in one direction.</b>
///     Holding vertex and surface at once is strictly better than either: a vertex snap only answers
///     when there is a corner within reach, so falling through to the surface when there is not is
///     what makes the pair a better drag rather than a mode switch. The order they are tried in is
///     <see cref="Vertex" />, <see cref="EdgeCentre" />, <see cref="Edge" />, <see cref="Face" /> —
///     smallest first, which is the same innermost-wins rule <see cref="SubObjectPicker" /> uses and
///     for the same reason.
/// </remarks>
[Flags]
public enum SnapElements : byte {
    /// <summary>Nothing. A drag goes where the pointer says.</summary>
    None = 0,

    /// <summary>Whole steps of the grid, measured from where the drag began.</summary>
    Increment = 1,

    /// <summary>The world lattice itself, so everything dragged ends up on the same one.</summary>
    Grid = 2,

    /// <summary>A shared position of the geometry under the pointer.</summary>
    Vertex = 4,

    /// <summary>Anywhere along an edge of it.</summary>
    Edge = 8,

    /// <summary>The midpoint of an edge, which is what a wall is centred on.</summary>
    EdgeCentre = 16,

    /// <summary>Wherever the view ray meets the surface.</summary>
    Face = 32,

    /// <summary>The three that need geometry rather than arithmetic.</summary>
    Geometry = Vertex | Edge | EdgeCentre | Face
}

/// <summary>Which part of what is being dragged is put on the snap point.</summary>
/// <remarks>
///     ⚠ <b>The half everybody omits, and doc 24's D4 says it is the half that matters.</b> Snapping
///     the <i>centre</i> of what you dragged to a vertex is almost never what you meant; you meant the
///     corner you grabbed. A snap with no base concept can only offer the first, which is why a great
///     many editors' vertex snapping is a feature people try once.
/// </remarks>
public enum SnapBase : byte {
    /// <summary>The gizmo's own origin — wherever the handles are drawn.</summary>
    Origin,

    /// <summary>The middle of everything selected, whatever the pivot mode says.</summary>
    Centre,

    /// <summary>The active element's own origin, which is the first of the selection.</summary>
    Active,

    /// <summary>Where the pointer met the geometry when the drag began.</summary>
    /// <remarks>The corner you grabbed. This is the one the other three exist to be compared against.</remarks>
    Pointer
}

/// <summary>The three orthogonal switches over a snap, rather than three more elements.</summary>
[Flags]
public enum SnapModifiers : byte {
    /// <summary>None of them.</summary>
    None = 0,

    /// <summary>Turn what is being dragged to stand on the surface it landed on.</summary>
    /// <remarks>
    ///     ⚠ <b>Only a <see cref="SnapElements.Face" /> snap has a normal to align to.</b> A vertex is
    ///     a point and an edge is a line; neither says which way anything faces, so a drag that landed
    ///     on one is moved and not turned. That is not a gap to be filled by averaging the faces round
    ///     a corner — a corner of a cube would then stand things up diagonally.
    /// </remarks>
    AlignToTarget = 1,

    /// <summary>Look for the snap under the pointer rather than nearest what is being dragged.</summary>
    /// <remarks>
    ///     <para>
    ///         <b>On, which is the default, the search is in screen space</b>: the nearest element to
    ///         the pointer within <see cref="SnapContext.VertexRadius" /> pixels. The gesture is "put
    ///         it on that corner", and which corner is meant is decided by where the pointer is.
    ///     </para>
    ///     <para>
    ///         <b>Off, the search is in the world</b>: the nearest element to
    ///         <see cref="SnapContext.Base" />, within the same radius converted to metres at that
    ///         point. That is what you want when the target is behind something, or when the pointer
    ///         is a long way from the object because the handle being dragged is.
    ///     </para>
    /// </remarks>
    ProjectFromView = 2,

    /// <summary>Take what is being dragged out of the answer.</summary>
    /// <remarks>
    ///     ⚠ <b>On by default and almost never worth turning off.</b> The pointer is over the object
    ///     being moved for the whole of every drag, so an object that could snap to its own surface is
    ///     one that never moves, and one that could snap to its own vertices jumps between its own
    ///     corners.
    /// </remarks>
    IgnoreSelf = 4
}

/// <summary>Where a snap landed, and which way the thing it landed on faces.</summary>
/// <param name="Point">Where, in world space.</param>
/// <param name="Normal">Which way the surface faces, or <see langword="null" /> for a point or a line.</param>
/// <param name="Element">Which kind of element answered.</param>
public readonly record struct SnapHit(Vector3 Point, Vector3? Normal, SnapElements Element);

/// <summary>What every transform in the editor rounds to, as one thing they all consult.</summary>
/// <remarks>
///     <para>
///         <b>Doc 24's D4, and it is a <i>context</i> rather than a settings bag for one reason.</b>
///         Snapping attached to the gizmo is a vertex snap that works when you drag an object and not
///         when you extrude a face, which reads as the feature being broken rather than as two
///         features. <c>TransformGizmo</c>, <c>ScenePlacement</c> and every blockout tool take the
///         same instance, so there is one answer to "am I snapping" and one place it is written.
///     </para>
///     <para>
///         Three orthogonal parts, which is Blender's arrangement and the reason its snapping is the
///         one people miss: <see cref="Elements" /> is what you land on, <see cref="Base" /> is what
///         of yours lands on it, and <see cref="Modifiers" /> is everything that is true of a snap
///         without being either.
///     </para>
///     <para>
///         ⚠ <b>The four booleans and three steps that were here before are still here, and still
///         mean what they meant.</b> They are views over <see cref="Elements" /> rather than separate
///         state — two writers for one fact is how a toolbar toggle and a settings panel end up
///         disagreeing — so everything that reads <see cref="SnapPosition" /> or
///         <see cref="SnapToVertex" /> is unchanged, and everything that writes them moves the set.
///     </para>
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
public sealed class SnapContext {
    /// <summary>What a position may land on.</summary>
    public SnapElements Elements { get; set; }

    /// <summary>Which part of what is being dragged is put on it.</summary>
    public SnapBase Base { get; set; } = SnapBase.Origin;

    /// <summary>The orthogonal switches.</summary>
    /// <remarks>
    ///     ⚠ <b><see cref="SnapModifiers.AlignToTarget" /> is on by default, which is not Blender's
    ///     default and is deliberate.</b> A drop onto a surface has stood the dropped thing up since
    ///     before this type existed, and the whole argument for one context is that a drop and a drag
    ///     onto the same ramp cannot disagree. Nothing is reachable without turning
    ///     <see cref="SnapElements.Face" /> on, which is off.
    /// </remarks>
    public SnapModifiers Modifiers { get; set; } =
        SnapModifiers.AlignToTarget | SnapModifiers.ProjectFromView | SnapModifiers.IgnoreSelf;

    /// <summary>Whether a translate moves by whole steps of <see cref="GridStep" />.</summary>
    public bool SnapPosition {
        get => Has(SnapElements.Increment);
        set => Toggle(SnapElements.Increment, value);
    }

    /// <summary>Whether a translate lands on the world grid rather than moving by whole steps of it.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>Two different things are called snapping and only one of them puts objects on the
    ///         grid.</b> Off — the default, and what Blender, Unity and Unreal all do — a drag moves
    ///         by a whole number of steps, so something at 0.3 dragged one step lands at 1.3. That is
    ///         the right answer for nudging a thing that is already where it should be, and it is
    ///         useless for lining several things up. On, the drag rounds the resulting <i>position</i>,
    ///         so everything dragged ends up on the same lattice however it started.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>It only means anything for a world-aligned drag.</b> A local-space arm points
    ///         somewhere the world grid has no lines along, so "the grid" has no answer; the gizmo
    ///         rounds along the arm from the gizmo's own origin, which is the nearest thing to one.
    ///     </para>
    /// </remarks>
    public bool AbsoluteGrid {
        get => Has(SnapElements.Grid);
        set => Toggle(SnapElements.Grid, value);
    }

    /// <summary>Whether a drag lands on the nearest vertex of what is under the pointer.</summary>
    public bool SnapToVertex {
        get => Has(SnapElements.Vertex);
        set => Toggle(SnapElements.Vertex, value);
    }

    /// <summary>Whether a drop or a drag lands on the surface under the pointer.</summary>
    public bool SnapToSurface {
        get => Has(SnapElements.Face);
        set => Toggle(SnapElements.Face, value);
    }

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

    /// <summary>How near an element has to be to be snapped to, in render pixels.</summary>
    /// <remarks>
    ///     Converted to metres at the base point when <see cref="SnapModifiers.ProjectFromView" /> is
    ///     off, so that turning the modifier off changes <i>where</i> the search happens and not how
    ///     far it reaches.
    /// </remarks>
    public float VertexRadius { get; set; } = 12f;

    /// <summary>Whether any element needing geometry under the pointer is on.</summary>
    public bool SnapsToGeometry => (Elements & SnapElements.Geometry) != 0;

    /// <summary>Whether an element is on.</summary>
    /// <param name="element">The element, or several.</param>
    /// <returns>Whether any of them is.</returns>
    public bool Has(SnapElements element) => (Elements & element) != 0;

    /// <summary>Whether a modifier is on.</summary>
    /// <param name="modifier">The modifier.</param>
    /// <returns>Whether it is.</returns>
    public bool Is(SnapModifiers modifier) => (Modifiers & modifier) != 0;

    /// <summary>Turns an element on or off.</summary>
    /// <param name="element">The element.</param>
    /// <param name="on">Whether it should be on.</param>
    public void Toggle(SnapElements element, bool on) =>
        Elements = on ? Elements | element : Elements & ~element;

    /// <summary>Turns a modifier on or off.</summary>
    /// <param name="modifier">The modifier.</param>
    /// <param name="on">Whether it should be on.</param>
    public void Toggle(SnapModifiers modifier, bool on) =>
        Modifiers = on ? Modifiers | modifier : Modifiers & ~modifier;

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

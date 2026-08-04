// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Animation.Moves;
using Vixen.Core;
using Vixen.Core.Mathematics;

namespace Vixen.Animation.Constraints;

/// <summary>Where a coordinate's origin comes from.</summary>
public enum OriginSource : byte {
    /// <summary>A place on a proxy shape's surface. The portable contact.</summary>
    Surface,

    /// <summary>
    ///     A direction from the shape's centre, resolved to wherever it exits the surface.
    /// </summary>
    /// <remarks>
    ///     For a point that should track the shape's <em>proportions</em> rather than a fixed patch of
    ///     it — and which stays meaningful across shape kinds, because the same axis works on a box
    ///     and on a sphere.
    /// </remarks>
    Axis,

    /// <summary>A fraction along a limb's extended length, with no shape needed at all.</summary>
    /// <remarks>
    ///     "Halfway down the forearm", which stays halfway down on an arm of any length. The form for
    ///     a body part nobody bothered to give a proxy shape.
    /// </remarks>
    Limb,

    /// <summary>A joint, plus an offset. The escape hatch, and the least portable.</summary>
    Joint
}

/// <summary>Where a coordinate's orientation comes from.</summary>
public enum OrientationSource : byte {
    /// <summary>The surface frame at the origin: <c>+Y</c> outward, <c>+X</c> along <c>U</c>.</summary>
    Surface,

    /// <summary>The joint the shape hangs off, unrotated by the surface.</summary>
    Joint,

    /// <summary>The character's model space. For an orientation that should not turn with the body.</summary>
    Model
}

/// <summary>Where a coordinate's scale comes from.</summary>
public enum ScaleSource : byte {
    /// <summary>The shape's own size, so an offset in the frame grows with the body.</summary>
    Shape,

    /// <summary>The joint's scale.</summary>
    Joint,

    /// <summary>One. For an offset in metres that must be the same on every body.</summary>
    Model
}

/// <summary>A fraction along the line between two joints.</summary>
/// <param name="From">The joint at the top.</param>
/// <param name="To">The joint at the end.</param>
/// <param name="Along">How far, in <c>[0, 1]</c>.</param>
/// <param name="Offset">
///     How far off that line, in the limb's own frame — <c>+Y</c> along the limb.
/// </param>
public readonly record struct LimbSpan(int From, int To, float Along, Vector3 Offset);

/// <summary>Where on a body a goal is, expressed so it means the same thing on another body.</summary>
/// <remarks>
///     <para>
///         ⚠ <b>Three sources, chosen independently, and that is the decision rather than a
///         convenience.</b> Collapsing origin, orientation and scale into one choice is what forces an
///         author to pick the least-wrong single frame and then hand-correct the result — because the
///         three genuinely want different sources more often than not. Project the origin onto a
///         surface, take the orientation from a bone, take the scale from the world so a
///         one-centimetre gap stays one centimetre. It costs almost nothing: the resolve is three
///         steps either way, and this lets each name its own source.
///     </para>
///     <para>
///         The surface, axis and limb forms are what you get by leaving the other two alone; they are
///         special cases of this and not separate types.
///     </para>
/// </remarks>
public readonly record struct SurfaceCoordinate {
    /// <summary>Which proxy shape, by name.</summary>
    public Symbol Shape { get; init; }

    /// <summary>Which proxy shape, by what it affords. Used when <see cref="Shape" /> names nothing.</summary>
    /// <remarks>
    ///     What makes one authored sitting clip work against a chair, a bench and a crate: the clip
    ///     names <c>affords=seat</c> and whichever shape carries that tag answers.
    /// </remarks>
    public Facet Tag { get; init; }

    /// <summary>Where the origin comes from.</summary>
    public OriginSource Origin { get; init; }

    /// <summary>The place on the surface, for <see cref="OriginSource.Surface" />.</summary>
    public SurfacePoint Point { get; init; }

    /// <summary>
    ///     The direction out of the shape's centre, for <see cref="OriginSource.Axis" />, in the
    ///     shape's own space.
    /// </summary>
    public Vector3 Direction { get; init; }

    /// <summary>The limb, for <see cref="OriginSource.Limb" />.</summary>
    public LimbSpan Limb { get; init; }

    /// <summary>
    ///     What was left over when the authored point was projected onto the surface, in the surface's
    ///     own frame.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>Applied at the scale <see cref="Scale" /> names, which for a gap is
    ///     <see cref="ScaleSource.Model" />.</b> A deliberate one-centimetre clearance authored on a
    ///     slim character is one centimetre on a heavy one; a residual that scaled with the shape
    ///     would be two.
    /// </remarks>
    public Vector3 Residual { get; init; }

    /// <summary>Where the orientation comes from.</summary>
    public OrientationSource Orientation { get; init; }

    /// <summary>Where the scale comes from.</summary>
    public ScaleSource Scale { get; init; }

    /// <summary>A place on a named shape's surface.</summary>
    /// <param name="shape">The shape.</param>
    /// <param name="point">Where on it.</param>
    /// <returns>The coordinate.</returns>
    public static SurfaceCoordinate On(string shape, SurfacePoint point) =>
        new() { Shape = Symbol.Intern(shape), Origin = OriginSource.Surface, Point = point };

    /// <summary>A place on whichever shape affords something.</summary>
    /// <param name="key">The tag's key — <c>affords</c>.</param>
    /// <param name="value">Its value — <c>seat</c>.</param>
    /// <param name="point">Where on it.</param>
    /// <returns>The coordinate.</returns>
    public static SurfaceCoordinate Affording(string key, string value, SurfacePoint point) =>
        new() { Tag = Facet.Of(key, value), Origin = OriginSource.Surface, Point = point };

    /// <summary>Wherever a direction out of a shape's centre exits its surface.</summary>
    /// <param name="shape">The shape.</param>
    /// <param name="direction">The direction, in the shape's own space.</param>
    /// <returns>The coordinate.</returns>
    public static SurfaceCoordinate Along(string shape, Vector3 direction) =>
        new() { Shape = Symbol.Intern(shape), Origin = OriginSource.Axis, Direction = direction };

    /// <summary>A fraction of the way down a limb.</summary>
    /// <param name="from">The joint at the top.</param>
    /// <param name="to">The joint at the end.</param>
    /// <param name="along">How far, in <c>[0, 1]</c>.</param>
    /// <returns>The coordinate.</returns>
    public static SurfaceCoordinate OnLimb(int from, int to, float along) =>
        new() { Origin = OriginSource.Limb, Limb = new(from, to, along, Vector3.Zero) };

    /// <summary>A joint, with no shape involved.</summary>
    /// <param name="joint">The joint.</param>
    /// <returns>The coordinate.</returns>
    public static SurfaceCoordinate AtJoint(int joint) =>
        new() {
            Origin = OriginSource.Joint,
            Limb = new(joint, joint, 0f, Vector3.Zero),
            Orientation = OrientationSource.Joint,
            Scale = ScaleSource.Joint
        };

    /// <summary>The same coordinate, taking its orientation and scale from somewhere else.</summary>
    /// <param name="orientation">Where the orientation comes from.</param>
    /// <param name="scale">Where the scale comes from.</param>
    /// <returns>The coordinate.</returns>
    public SurfaceCoordinate From(OrientationSource orientation, ScaleSource scale) =>
        this with { Orientation = orientation, Scale = scale };

    /// <summary>The same coordinate, with a gap held off the surface.</summary>
    /// <param name="residual">The gap, in the surface's own frame.</param>
    /// <param name="unscaled">
    ///     Whether the gap is in metres rather than in multiples of the shape. Usually yes.
    /// </param>
    /// <returns>The coordinate.</returns>
    public SurfaceCoordinate Offset(Vector3 residual, bool unscaled = true) =>
        this with { Residual = residual, Scale = unscaled ? ScaleSource.Model : Scale };
}

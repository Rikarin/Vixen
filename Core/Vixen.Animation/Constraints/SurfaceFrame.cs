// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;

namespace Vixen.Animation.Constraints;

/// <summary>A point on the surface of a proxy shape, resolved for whichever body is wearing it.</summary>
/// <param name="Coordinate">Where.</param>
/// <remarks>
///     <para>
///         <b>The frame the whole document is for.</b> Every other kind of frame names a place; this
///         one names a place <em>on a body</em>, and resolves to a different world point on every body
///         that means the same thing. One authored clip, three bodies of visibly different
///         proportions, hand contact correct on all three — that claim is this type.
///     </para>
///     <para>
///         ⚠ <b>Resolution needs proxy shapes, and a stack that has none fails cleanly.</b> Not an
///         exception: a character whose shape set has not loaded yet, or which has been dropped to a
///         detail level that no longer carries the named shape, is exactly the case
///         <see cref="IConstraintFrame.TryResolve" />'s failure path and D18's ease-out exist for.
///     </para>
/// </remarks>
public sealed record SurfaceFrame(SurfaceCoordinate Coordinate) : IConstraintFrame {
    /// <summary>A point on a named shape's surface.</summary>
    /// <param name="shape">The shape.</param>
    /// <param name="point">Where on it.</param>
    public SurfaceFrame(string shape, SurfacePoint point) : this(SurfaceCoordinate.On(shape, point)) {
    }

    /// <inheritdoc />
    public bool TryResolve(in ConstraintContext context, out Frame frame) {
        frame = default;

        if (Coordinate.Origin is OriginSource.Limb or OriginSource.Joint) {
            return TryResolveOnRig(context, out frame);
        }

        if (context.Shapes is not { } shapes) {
            return false;
        }

        var found = Coordinate.Shape.IsSome
            ? shapes.TryPose(Coordinate.Shape, context.Model, out var posed)
            : shapes.TryPose(Coordinate.Tag, context.Model, out posed);

        if (!found) {
            return false;
        }

        var sample = Coordinate.Origin is OriginSource.Axis
            ? Exit(posed, Coordinate.Direction)
            : ShapeGeometry.Evaluate(posed.Shape.Kind, posed.Dimensions, Coordinate.Point);

        var origin = posed.ToModel(sample.Position);
        var surface = Quaternion.Concatenate(sample.Rotation(), posed.Transform.Rotation);

        var rotation = Coordinate.Orientation switch {
            OrientationSource.Joint => context.Model[posed.Shape.Joint].Rotation,
            OrientationSource.Model => Quaternion.Identity,
            _ => surface
        };

        var scale = Coordinate.Scale switch {
            ScaleSource.Joint => context.Model[posed.Shape.Joint].Scale,
            ScaleSource.Model => Vector3.One,
            _ => posed.Transform.Scale * posed.Dimensions.Extents
        };

        // ⚠ The residual is applied in the *surface's* frame and at the scale the coordinate names,
        // which is not the frame's own rotation when the orientation came from somewhere else. A gap
        // authored 1 cm off the skin is 1 cm off the skin however the goal is turned.
        frame = new(
            new BoneTransform(
                origin + Quaternion.Transform(Coordinate.Residual * Gap(Coordinate.Scale, posed), surface),
                rotation,
                scale
            )
        );

        return true;
    }

    /// <summary>The limb and joint forms, which need no shape at all.</summary>
    bool TryResolveOnRig(in ConstraintContext context, out Frame frame) {
        var limb = Coordinate.Limb;

        frame = default;

        if ((uint)limb.From >= (uint)context.Model.Length || (uint)limb.To >= (uint)context.Model.Length) {
            return false;
        }

        var from = context.Model[limb.From];
        var to = context.Model[limb.To];
        var origin = Vector3.Lerp(from.Translation, to.Translation, MathUtil.Saturate(limb.Along));
        var along = to.Translation - from.Translation;

        // +Y along the limb, matching the surface frame's convention, so an offset authored against a
        // shape and one authored against a bare limb read the same way.
        var rotation = along.LengthSquared() > 1e-8f && Coordinate.Orientation is OrientationSource.Surface
            ? Quaternion.Concatenate(from.Rotation, Quaternion.FromToRotation(Quaternion.Transform(Vector3.Up, from.Rotation), Vector3.Normalize(along)))
            : Coordinate.Orientation is OrientationSource.Model ? Quaternion.Identity : from.Rotation;

        var scale = Coordinate.Scale switch {
            ScaleSource.Model => Vector3.One,
            _ => from.Scale
        };

        frame = new(
            new BoneTransform(
                origin + Quaternion.Transform(limb.Offset + Coordinate.Residual, rotation),
                rotation,
                scale
            )
        );

        return true;
    }

    /// <summary>Where a direction out of the shape's centre leaves its surface.</summary>
    /// <remarks>
    ///     Found by projecting a point far out along the direction, which is the same answer as
    ///     intersecting the ray for every primitive here and needs no per-kind ray code. The distance
    ///     is a multiple of the shape's own size, so it is outside whatever the shape currently is.
    /// </remarks>
    static SurfaceSample Exit(in ProxyShapePose posed, Vector3 direction) {
        var reach = MathF.Max(
            MathF.Max(posed.Dimensions.Extents.X, posed.Dimensions.Extents.Y),
            MathF.Max(posed.Dimensions.Extents.Z, posed.Dimensions.TopExtents.X)
        );

        var out1 = direction.LengthSquared() > 1e-8f
            ? Vector3.Normalize(direction) * (reach * 4f)
            : new Vector3(0f, reach * 4f, 0f);

        var point = ShapeGeometry.Project(posed.Shape.Kind, posed.Dimensions, out1, out _);
        return ShapeGeometry.Evaluate(posed.Shape.Kind, posed.Dimensions, point);
    }

    static Vector3 Gap(ScaleSource scale, in ProxyShapePose posed) =>
        scale is ScaleSource.Model ? Vector3.One : posed.Transform.Scale;
}

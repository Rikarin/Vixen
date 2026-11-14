// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;

namespace Vixen.Texturing.Painting;

public sealed partial class PaintMeshSurface {
    /// <summary>
    ///     The plane that touches the surface under a stamp's middle, with the camera's right and up laid on it, which a
    ///     stamp of <see cref="PaintStampAlignment.Tangent" /> is weighed on.
    /// </summary>
    /// <param name="Origin">Where the stamp's middle meets the surface.</param>
    /// <param name="Normal">The surface's unit normal there.</param>
    /// <param name="Across">The camera's right laid on the plane, a unit length.</param>
    /// <param name="Up">The camera's up laid on the plane, a unit length square to <paramref name="Across" />.</param>
    /// <param name="PerPixel">How far one unit of the picture reaches at the stamp's depth.</param>
    /// <remarks>
    ///     ⚠ <b>Right and up are each laid on the plane, never crossed from the normal.</b> A mirrored camera is
    ///     left-handed on purpose (see <see cref="PaintView" />), and a cross product would turn its stamps over.
    /// </remarks>
    readonly record struct StampPlane(Vector3 Origin, Vector3 Normal, Vector3 Across, Vector3 Up, float PerPixel) {
        /// <summary>Shorter than this, a direction laid on the plane is taken as lying along the normal.</summary>
        const float Degenerate = 1e-4f;

        /// <summary>The plane under a stamp, from the camera it is seen through.</summary>
        /// <param name="camera">The camera.</param>
        /// <param name="origin">Where the stamp's middle meets the surface.</param>
        /// <param name="normal">The surface's normal there, of any length and either side.</param>
        /// <param name="perPixel">The size of a picture unit at that point's depth.</param>
        /// <returns>The plane.</returns>
        public static StampPlane At(in PaintView camera, Vector3 origin, Vector3 normal, float perPixel) {
            var unit = normal.LengthSquared() > 0f ? Vector3.Normalize(normal) : -camera.Forward;
            var right = camera.Right - unit * Vector3.Dot(camera.Right, unit);
            var up = camera.Up - unit * Vector3.Dot(camera.Up, unit);

            // A face seen edge on along one of the camera's axes loses that axis; the other one, turned a quarter on the
            // plane, stands in for it, on the side the camera's own axis was.
            if (right.Length() < Degenerate) {
                up = Vector3.Normalize(up);
                right = Vector3.Cross(up, unit);
                right = Vector3.Dot(right, camera.Right) < 0f ? -right : right;
            } else {
                right = Vector3.Normalize(right);
                up -= right * Vector3.Dot(up, right);

                if (up.Length() < Degenerate) {
                    up = Vector3.Cross(unit, right);
                    up = Vector3.Dot(up, camera.Up) < 0f ? -up : up;
                }

                up = Vector3.Normalize(up);
            }

            return new(origin, unit, right, up, perPixel);
        }

        /// <summary>Where a point lies on the plane, in the picture's units about the stamp's middle.</summary>
        /// <param name="point">The point, which need not be on the plane.</param>
        /// <param name="centre">The stamp's middle on the picture.</param>
        /// <returns>The position the brush is weighed at.</returns>
        public Vector2 Laid(Vector3 point, Vector2 centre) {
            var offset = point - Origin;

            // A row grows down the picture, so up on the plane is a smaller row.
            return centre + new Vector2(Vector3.Dot(offset, Across), -Vector3.Dot(offset, Up)) / PerPixel;
        }

        /// <summary>Half the side of the square on the picture that holds everything the stamp can reach.</summary>
        /// <param name="camera">The camera.</param>
        /// <param name="radius">How far the stamp reaches on the plane, in the mesh's units.</param>
        /// <param name="depth">How far off the plane it may still weigh a point.</param>
        /// <returns>The half side, in the picture's units; the whole picture when the box reaches behind the eye.</returns>
        /// <remarks>
        ///     A box's picture is inside the picture of its corners, as long as every corner is in front of the eye. A
        ///     plane seen nearly edge on reaches far further on the picture than a disc on it does on a face seen square.
        /// </remarks>
        public float Square(in PaintView camera, float radius, float depth) {
            var middle = camera.Project(Origin);
            var nearest = camera.Depth(Origin) * 1e-3f;
            var whole = (float)Math.Max(camera.Width, camera.Height);
            var half = 0f;

            for (var corner = 0; corner < 8; corner++) {
                var point = Origin
                    + Across * ((corner & 1) == 0 ? -radius : radius)
                    + Up * ((corner & 2) == 0 ? -radius : radius)
                    + Normal * ((corner & 4) == 0 ? -depth : depth);

                if (!(camera.Depth(point) > nearest)) {
                    return whole;
                }

                var offset = Vector2.Abs(camera.Project(point) - middle);

                half = MathF.Max(half, MathF.Max(offset.X, offset.Y));
            }

            return MathF.Min(half + 1f, whole);
        }
    }
}

// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;

namespace Vixen.Rendering.DistanceFields.Tests;

/// <summary>Fields with no sampling error, so a tracer can be measured on its own.</summary>
/// <remarks>
///     Marching a baked field and comparing against arithmetic measures the tracer and the
///     interpolation together, and when the two disagree there is nothing to say which was wrong.
///     These have no interpolation in them at all: every disagreement is the tracer's.
/// </remarks>
static class AnalyticFields {
    /// <summary>A sphere.</summary>
    /// <param name="centre">Where it is.</param>
    /// <param name="radius">How big.</param>
    public sealed class Sphere(Vector3 centre, float radius) : IDistanceField {
        public float Sample(Vector3 position) => Vector3.Distance(position, centre) - radius;

        public Vector3 SampleGradient(Vector3 position) => Vector3.Normalize(position - centre);
    }

    /// <summary>A half-space: everything on the far side of a plane is solid.</summary>
    /// <param name="normal">Which way is out. Must be normalised.</param>
    /// <param name="offset">How far along it the plane sits.</param>
    public sealed class HalfSpace(Vector3 normal, float offset) : IDistanceField {
        public float Sample(Vector3 position) => Vector3.Dot(position, normal) - offset;

        public Vector3 SampleGradient(Vector3 position) => normal;
    }

    /// <summary>Several fields at once, which for distance is the nearer of them.</summary>
    /// <param name="fields">What to combine.</param>
    /// <remarks>
    ///     A union of distance fields is exactly their minimum, and it is exact rather than
    ///     conservative — which is what makes a corner built from two half-spaces a usable oracle.
    /// </remarks>
    public sealed class Union(params IDistanceField[] fields) : IDistanceField {
        public float Sample(Vector3 position) {
            var nearest = float.PositiveInfinity;

            foreach (var field in fields) {
                nearest = MathF.Min(nearest, field.Sample(position));
            }

            return nearest;
        }

        public Vector3 SampleGradient(Vector3 position) {
            var nearest = float.PositiveInfinity;
            var gradient = Vector3.Zero;

            foreach (var field in fields) {
                var distance = field.Sample(position);

                if (distance >= nearest) {
                    continue;
                }

                nearest = distance;
                gradient = field.SampleGradient(position);
            }

            return gradient;
        }
    }

    /// <summary>Nothing, anywhere, as far as anyone can see.</summary>
    public sealed class Empty : IDistanceField {
        public float Sample(Vector3 position) => 1e6f;

        public Vector3 SampleGradient(Vector3 position) => Vector3.Zero;
    }
}

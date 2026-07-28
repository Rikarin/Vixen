// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Runtime.InteropServices;
using Vixen.Core.Mathematics;

namespace Vixen.Samples.PbrShowcase;

/// <summary>One vertex of the sample's geometry: a position and a normal.</summary>
/// <remarks>
///     Sequential layout and a stated size, because this struct's bytes go straight into a vertex
///     buffer whose stride the pipeline declares. A layout the runtime is free to reorder would put
///     the normal wherever it liked, and the resulting picture — lit from an angle that drifts with
///     the JIT — is not one anybody would attribute to a missing attribute.
/// </remarks>
[StructLayout(LayoutKind.Sequential, Pack = 4)]
readonly record struct Vertex(Vector3 Position, Vector3 Normal) {
    /// <summary>How many bytes one vertex occupies.</summary>
    public const int Stride = 24;
}

/// <summary>A UV sphere, generated rather than loaded.</summary>
/// <remarks>
///     <para>
///         Generated because the asset pipeline is not what this sample is about, and because a
///         sphere is the shape that shows a BRDF best: it presents every angle between the normal
///         and the view at once, so a roughness change is visible as a shape rather than as a
///         brightness.
///     </para>
///     <para>
///         A UV sphere rather than an icosphere, despite the pole pinching, for one reason: its
///         normals are exactly analytic — the normal <em>is</em> the normalised position — so a
///         lighting mistake here cannot be blamed on the mesh.
///     </para>
/// </remarks>
static class Sphere {
    /// <summary>Builds a unit sphere at the origin.</summary>
    /// <param name="segments">How many divisions around the equator.</param>
    /// <param name="rings">How many divisions from pole to pole.</param>
    public static (Vertex[] Vertices, ushort[] Indices) Build(int segments = 32, int rings = 16) {
        ArgumentOutOfRangeException.ThrowIfLessThan(segments, 3);
        ArgumentOutOfRangeException.ThrowIfLessThan(rings, 2);

        var vertices = new Vertex[(segments + 1) * (rings + 1)];
        var index = 0;

        for (var ring = 0; ring <= rings; ring++) {
            var phi = MathF.PI * ring / rings;
            var (sinPhi, cosPhi) = MathF.SinCos(phi);

            for (var segment = 0; segment <= segments; segment++) {
                var theta = MathF.Tau * segment / segments;
                var (sinTheta, cosTheta) = MathF.SinCos(theta);

                // The seam column is duplicated — segment runs to `segments` inclusive — because a
                // sphere that shared it would need one vertex to carry two texture coordinates. No
                // texture here, but the duplication is what makes adding one later a one-line
                // change rather than a re-topology.
                var position = new Vector3(sinPhi * cosTheta, cosPhi, sinPhi * sinTheta);
                vertices[index++] = new(position, position);
            }
        }

        var indices = new List<ushort>(segments * rings * 6);

        for (var ring = 0; ring < rings; ring++) {
            for (var segment = 0; segment < segments; segment++) {
                var current = (ring * (segments + 1)) + segment;
                var next = current + segments + 1;

                // Counter-clockwise when seen from outside, which is what the engine calls front
                // (Core/Vixen.Core.Mathematics/Conventions.md). Wound the other way, every sphere
                // in the grid is inside-out and lit from behind — which reads as a lighting bug.
                indices.Add((ushort)current);
                indices.Add((ushort)next);
                indices.Add((ushort)(current + 1));

                indices.Add((ushort)(current + 1));
                indices.Add((ushort)next);
                indices.Add((ushort)(next + 1));
            }
        }

        return (vertices, indices.ToArray());
    }
}

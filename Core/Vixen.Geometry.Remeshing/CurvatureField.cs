// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;

namespace Vixen.Geometry.Remeshing;

/// <summary>Principal curvatures, principal directions and the angle defect, per vertex.</summary>
/// <remarks>
///     <para>
///         <b>What docs/plan/41 § D5's soft alignment and § D6's third correction both read.</b> The
///         second fundamental form is fitted in each vertex's tangent frame by least squares over its
///         one-ring: the normal curvature along an edge is
///         <c>2·(n · (p_j − p_i)) / |p_j − p_i|²</c>, and three of them determine the quadratic form
///         <c>κ(x, y) = a x² + 2b x y + c y²</c> whose eigenvalues are κ₁ and κ₂.
///     </para>
///     <para>
///         ⚠ <b>The number the field is weighted by is |κ₁ − κ₂|·diagonal and never |κ|, and § D5
///         says that is the whole of Adaptive Size on the direction side.</b> On a sphere the two
///         principal curvatures are equal at every point, the anisotropy is zero everywhere, and the
///         field is left free to be smooth — which is the right answer, because a sphere has no
///         preferred direction and any cross field on one is as good as another. A naive alignment
///         weighted by curvature <i>magnitude</i> gets a sphere maximally wrong: the weight is large
///         and uniform, the principal directions are the numerical noise of an ill-conditioned fit,
///         and the field chases them.
///     </para>
///     <para>
///         ⚠ <b>Multiplying by the diagonal is what makes it a number rather than a length.</b>
///         Curvature is one over a length, so |κ₁ − κ₂| on the same shape at a thousandth of the size
///         is a thousand times larger. The product with the bounding-box diagonal is dimensionless and
///         is the same at every scale, which is what <c>FieldScaleInvarianceTests</c> asserts.
///     </para>
/// </remarks>
sealed class CurvatureField {
    readonly float[] maximum;
    readonly float[] minimum;
    readonly Vector3[] directions;
    readonly float[] defects;
    readonly float diagonal;

    CurvatureField(float[] maximum, float[] minimum, Vector3[] directions, float[] defects, float diagonal) {
        this.maximum = maximum;
        this.minimum = minimum;
        this.directions = directions;
        this.defects = defects;
        this.diagonal = diagonal;
    }

    /// <summary>The larger principal curvature, κ₁.</summary>
    /// <param name="vertex">Its index.</param>
    /// <returns>The curvature, in reciprocal world units.</returns>
    public float Maximum(int vertex) => maximum[vertex];

    /// <summary>The smaller principal curvature, κ₂.</summary>
    /// <param name="vertex">Its index.</param>
    /// <returns>The curvature, in reciprocal world units.</returns>
    public float Minimum(int vertex) => minimum[vertex];

    /// <summary>The direction of κ₁, in the vertex's tangent plane.</summary>
    /// <param name="vertex">Its index.</param>
    /// <returns>A unit direction, or zero where the fit had nothing to say.</returns>
    /// <remarks>
    ///     ⚠ <b>Which of the two principal directions this is does not matter, and that is a property
    ///     of 4-RoSy rather than an accident.</b> They are ninety degrees apart and a cross stands for
    ///     four directions ninety degrees apart, so aligning to κ₁ and aligning to κ₂ produce the same
    ///     cross. It is why a cylinder's field comes out along its axis without anyone deciding
    ///     whether the axis or the circumference is the one to follow.
    /// </remarks>
    public Vector3 Direction(int vertex) => directions[vertex];

    /// <summary>How anisotropic the surface is at a vertex, as a number rather than a length.</summary>
    /// <param name="vertex">Its index.</param>
    /// <returns><c>|κ₁ − κ₂|</c> times the bounding-box diagonal. Zero on a sphere and on a plane.</returns>
    public float Anisotropy(int vertex) => MathF.Abs(maximum[vertex] - minimum[vertex]) * diagonal;

    /// <summary>How curved the surface is at a vertex, without regard to direction.</summary>
    /// <param name="vertex">Its index.</param>
    /// <returns>The larger absolute principal curvature, times the diagonal.</returns>
    /// <remarks>What <see cref="DensityField" /> uses, and deliberately <i>not</i> what the field
    ///     alignment uses — the density wants small quads wherever the surface bends, and a sphere's
    ///     poles want small quads even though its anisotropy is nothing.</remarks>
    public float Magnitude(int vertex) =>
        MathF.Max(MathF.Abs(maximum[vertex]), MathF.Abs(minimum[vertex])) * diagonal;

    /// <summary>The angle defect at a vertex: <c>2π</c> less the angles round it, or <c>π</c> less on a rim.</summary>
    /// <param name="vertex">Its index.</param>
    /// <returns>The defect, in radians. Positive on a bump, negative on a saddle, zero on anything developable.</returns>
    /// <remarks>
    ///     <para>
    ///         <b>docs/plan/41 § D6's third correction is stated in terms of this and not of κ.</b>
    ///         "The right somewhere is where the surface genuinely is not developable: the tip of a
    ///         finger, the corner of a box, the pole of a sphere." A cylinder is curved and perfectly
    ///         developable, and its angle defect is zero everywhere — which is exactly why a
    ///         singularity does not belong on one.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The sum over a closed surface is <c>2π·χ</c> exactly, which makes this the
    ///         discrete Gauss–Bonnet and the reason the attraction can never move the total.</b> § D6
    ///         says the same thing about the singularity index sum; they are the same conservation law
    ///         seen twice, and it is why the third correction is a placement and not a removal.
    ///     </para>
    /// </remarks>
    public float AngleDefect(int vertex) => defects[vertex];

    /// <summary>Fits the second fundamental form at every vertex.</summary>
    /// <param name="mesh">The conditioned view.</param>
    /// <returns>The field.</returns>
    public static CurvatureField Build(ManifoldMesh mesh) {
        ArgumentNullException.ThrowIfNull(mesh);

        var count = mesh.VertexCount;
        var maximum = new float[count];
        var minimum = new float[count];
        var directions = new Vector3[count];

        for (var vertex = 0; vertex < count; vertex++) {
            Fit(mesh, vertex, out maximum[vertex], out minimum[vertex], out directions[vertex]);
        }

        return new(maximum, minimum, directions, Defects(mesh), mesh.Diagonal);
    }

    static void Fit(ManifoldMesh mesh, int vertex, out float high, out float low, out Vector3 direction) {
        high = 0f;
        low = 0f;
        direction = Vector3.Zero;

        var frame = mesh.Frame(vertex);

        if (frame.Normal.LengthSquared() <= 0f) {
            return;
        }

        var ring = mesh.Ring(vertex);

        if (ring.Length < 3) {
            return;
        }

        var here = mesh.Positions[vertex];

        // The normal equations of `κ = a x² + 2b x y + c y²` over the basis (x², 2xy, y²), summed in
        // ring order — which is a fixed order, so the sum is the same on every machine.
        Span<float> m = stackalloc float[6];
        Span<float> r = stackalloc float[3];

        m.Clear();
        r.Clear();

        foreach (var neighbour in ring) {
            var span = mesh.Positions[neighbour] - here;
            var lengthSquared = span.LengthSquared();

            if (lengthSquared <= 0f) {
                continue;
            }

            var flat = ScaleSafe.Flatten(span, frame.Normal);

            if (flat.LengthSquared() <= 0f) {
                continue;
            }

            var x = Vector3.Dot(flat, frame.Tangent);
            var y = Vector3.Dot(flat, frame.Bitangent);
            var curvature = 2f * Vector3.Dot(frame.Normal, span) / lengthSquared;

            var u0 = x * x;
            var u1 = 2f * x * y;
            var u2 = y * y;

            m[0] += u0 * u0;
            m[1] += u0 * u1;
            m[2] += u0 * u2;
            m[3] += u1 * u1;
            m[4] += u1 * u2;
            m[5] += u2 * u2;

            r[0] += curvature * u0;
            r[1] += curvature * u1;
            r[2] += curvature * u2;
        }

        if (!Solve(m, r, out var a, out var b, out var c)) {
            return;
        }

        // Eigenvalues of the symmetric [[a, b], [b, c]]. The half-difference is the anisotropy, and
        // it is what the field alignment is weighted by.
        var mean = (a + c) * 0.5f;
        var spread = MathF.Sqrt((((a - c) * 0.5f) * ((a - c) * 0.5f)) + (b * b));

        high = mean + spread;
        low = mean - spread;

        var vx = b;
        var vy = high - a;

        if ((vx * vx) + (vy * vy) <= 0f) {
            vx = high - c;
            vy = b;
        }

        if ((vx * vx) + (vy * vy) <= 0f) {
            vx = 1f;
            vy = 0f;
        }

        direction = ScaleSafe.Unit((frame.Tangent * vx) + (frame.Bitangent * vy));
    }

    /// <summary>A 3×3 symmetric solve by Cramer, with a relative singularity test.</summary>
    /// <remarks>
    ///     ⚠ The entries are sums of products of <i>unit</i> direction components, so they are of
    ///     order one whatever size the model is — which is what lets the determinant be compared
    ///     against the matrix's own norm rather than against an absolute epsilon. Only the right-hand
    ///     side carries the reciprocal length, and it does not enter the test.
    /// </remarks>
    static bool Solve(ReadOnlySpan<float> m, ReadOnlySpan<float> r, out float a, out float b, out float c) {
        a = 0f;
        b = 0f;
        c = 0f;

        // [ m0 m1 m2 ]
        // [ m1 m3 m4 ]
        // [ m2 m4 m5 ]
        var cofactor0 = (m[3] * m[5]) - (m[4] * m[4]);
        var cofactor1 = (m[2] * m[4]) - (m[1] * m[5]);
        var cofactor2 = (m[1] * m[4]) - (m[2] * m[3]);
        var determinant = (m[0] * cofactor0) + (m[1] * cofactor1) + (m[2] * cofactor2);

        var norm = 0f;

        foreach (var entry in m) {
            norm = MathF.Max(norm, MathF.Abs(entry));
        }

        if (norm <= 0f || MathF.Abs(determinant) <= 1e-7f * norm * norm * norm) {
            return false;
        }

        var inverse = 1f / determinant;

        a = ((cofactor0 * r[0]) + (cofactor1 * r[1]) + (cofactor2 * r[2])) * inverse;

        b = ((cofactor1 * r[0]) + (((m[0] * m[5]) - (m[2] * m[2])) * r[1]) + (((m[1] * m[2]) - (m[0] * m[4])) * r[2]))
            * inverse;

        c = ((cofactor2 * r[0]) + (((m[1] * m[2]) - (m[0] * m[4])) * r[1]) + (((m[0] * m[3]) - (m[1] * m[1])) * r[2]))
            * inverse;

        return true;
    }

    /// <summary>The angle defect at every vertex, accumulated triangle by triangle in index order.</summary>
    static float[] Defects(ManifoldMesh mesh) {
        var defects = new float[mesh.VertexCount];

        for (var vertex = 0; vertex < defects.Length; vertex++) {
            // ⚠ A rim vertex has a fan rather than a ring, so the turn that would close it is not
            // there and the full turn is π rather than 2π. Using 2π everywhere makes every boundary
            // vertex read as a sharp cone, which puts § D6's curvature attraction round the rim of
            // every open mesh.
            defects[vertex] = mesh.IsBoundary(vertex) ? MathF.PI : MathF.Tau;
        }

        for (var triangle = 0; triangle < mesh.TriangleCount; triangle++) {
            var corners = mesh.Corners(triangle);

            for (var corner = 0; corner < 3; corner++) {
                var here = mesh.Positions[corners[corner]];
                var one = mesh.Positions[corners[(corner + 1) % 3]] - here;
                var two = mesh.Positions[corners[(corner + 2) % 3]] - here;

                if (one.LengthSquared() <= 0f || two.LengthSquared() <= 0f) {
                    continue;
                }

                var cosine = Vector3.Dot(ScaleSafe.Unit(one), ScaleSafe.Unit(two));

                defects[corners[corner]] -= MathF.Acos(Math.Clamp(cosine, -1f, 1f));
            }
        }

        return defects;
    }
}

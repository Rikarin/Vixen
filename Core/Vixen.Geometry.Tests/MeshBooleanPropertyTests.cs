// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using CsCheck;
using Vixen.Core.Mathematics;
using Xunit;

namespace Vixen.Geometry.Tests;

/// <summary>Doc 24's P6 exit criterion: ten thousand randomised operand pairs, and no hole in any of them.</summary>
/// <remarks>
///     <para>
///         <b>"The exit criterion is not 'it works on the demo'."</b> Doc 24 says to read ProBuilder's
///         seven-year "experimental" label as data rather than as an anecdote, and this is the
///         difference: a boolean is not finished when it produces the right picture for two boxes, it
///         is finished when ten thousand pairs of them produce no hole and no inconsistency.
///     </para>
///     <para>
///         ⚠ <b>The generators are biased towards the degenerate cases, because here they are the
///         normal ones.</b> Operands snapped to a coarse grid meet flush constantly, share edges
///         constantly and are occasionally identical — which is what a block-out looks like and is
///         exactly where a point-based classification fails. Sampling positions from a continuous
///         range would mostly test the easy case and would call it thorough.
///     </para>
///     <para>
///         ⚠ <b>The gate is <c>IsClosed</c> and <c>IsConsistent</c>, not "no exception".</b> A boolean
///         that throws is a bug somebody can see. One that gives back a surface with a gap in it is a
///         level that renders wrongly three weeks later, in a room nobody remembers making.
///     </para>
/// </remarks>
public class MeshBooleanPropertyTests {
    /// <summary>How many operand pairs the exit criterion asks for.</summary>
    const int Pairs = 10_000;

    /// <summary>A box of a random size, snapped to a grid coarse enough that operands meet flush.</summary>
    /// <remarks>
    ///     ⚠ <b>Half-unit steps, and that is the whole design of this generator.</b> On a fine grid two
    ///     random boxes essentially never share a plane, so every classification is comfortably away
    ///     from zero and the suite tests the case that was never going to fail. On a coarse one they
    ///     share planes, edges and corners constantly.
    /// </remarks>
    static Gen<(Vector3 Size, Vector3 At)> Operand(int span) =>
        Gen.Select(
            Gen.Int[1, 6],
            Gen.Int[1, 6],
            Gen.Int[1, 6],
            Gen.Int[-span, span],
            Gen.Int[-span, span],
            Gen.Int[-span, span]
        ).Select(
            values => (
                new Vector3(values.Item1 * 0.5f, values.Item2 * 0.5f, values.Item3 * 0.5f),
                new Vector3(values.Item4 * 0.5f, values.Item5 * 0.5f, values.Item6 * 0.5f)
            )
        );

    static EditMesh Box(Vector3 size, Vector3 at) {
        var mesh = MeshShapes.Create(new ShapeParameters { Kind = ShapeKind.Box, Size = size });

        for (var position = 0; position < mesh.PositionCount; position++) {
            mesh.MovePosition(position, mesh.Positions[position] + at);
        }

        return mesh;
    }

    /// <summary>The one assertion: whatever came back is a surface with no gap in it.</summary>
    static void AssertSound(EditMesh? made, string what) {
        if (made is null) {
            // Nothing left is a legitimate answer — subtracting a solid from one it contains — and it
            // is not a hole.
            return;
        }

        var report = made.Validate();

        // ⚠ No boundary edge and no reversed face — which is doc 24's gate said exactly: no hole and
        // no self-intersection. A *non-manifold* edge is deliberately allowed, and the suite found the
        // reason within ten thousand pairs: two boxes that touch along an edge and nowhere else have a
        // union with a non-manifold edge in it, and that is the true answer rather than a defect.
        // Refusing it would be refusing the geometry, which is the same argument D2 makes about the
        // edge table reporting these rather than rejecting them.
        Assert.Empty(report.Boundary);
        Assert.True(report.IsConsistent, what + ": " + (report.Describe() ?? "consistent"));
        Assert.Empty(report.Degenerate);
    }

    [Fact]
    public void Ten_thousand_randomised_operand_pairs_produce_no_hole() {
        var operation = 0;

        Gen.Select(Operand(4), Operand(4))
            .Sample(
                pair => {
                    var ((leftSize, leftAt), (rightSize, rightAt)) = pair;

                    var left = Box(leftSize, leftAt);
                    var right = Box(rightSize, rightAt);

                    // ⚠ All three operations on every pair rather than one chosen at random. They fail
                    // in different ways — a union loses a coplanar face, an intersection keeps one
                    // twice — and a suite that sampled among them would need three times the runs to
                    // say the same thing.
                    var what = $"{leftSize}@{leftAt} vs {rightSize}@{rightAt}";

                    AssertSound(MeshBoolean.Apply(left, right, BooleanOperation.Union), "union " + what);
                    AssertSound(MeshBoolean.Apply(left, right, BooleanOperation.Difference), "difference " + what);
                    AssertSound(MeshBoolean.Apply(left, right, BooleanOperation.Intersection), "intersection " + what);

                    operation++;
                },
                iter: Pairs
            );

        // ⚠ A floor rather than the exact figure. CsCheck decides how many distinct samples it draws
        // for a given iteration count, and on a grid this coarse the sample space is small enough that
        // it draws a few less than it was asked for. What this catches is the failure that would
        // otherwise be invisible: a suite that ran nothing at all and passed.
        Assert.True(operation > Pairs / 2, $"only {operation} pairs were tried");
    }

    [Fact]
    public void A_union_never_loses_volume_and_an_intersection_never_gains_any() {
        Gen.Select(Operand(3), Operand(3))
            .Sample(
                pair => {
                    var ((leftSize, leftAt), (rightSize, rightAt)) = pair;

                    var left = Box(leftSize, leftAt);
                    var right = Box(rightSize, rightAt);

                    var one = leftSize.X * leftSize.Y * leftSize.Z;
                    var other = rightSize.X * rightSize.Y * rightSize.Z;

                    var united = MeshBoolean.Apply(left, right, BooleanOperation.Union);
                    var shared = MeshBoolean.Apply(left, right, BooleanOperation.Intersection);

                    // ⚠ Bounds rather than an exact figure, because the exact one is the thing being
                    // tested. A union is at least as big as its larger operand and no bigger than both
                    // together; an intersection is no bigger than its smaller one. A boolean that
                    // double-counted a coplanar face or dropped one falls outside both.
                    if (united is not null) {
                        var volume = Volume(united);

                        Assert.True(volume >= MathF.Max(one, other) - Tolerance, $"union shrank: {volume}");
                        Assert.True(volume <= one + other + Tolerance, $"union grew: {volume}");
                    }

                    if (shared is not null) {
                        var volume = Volume(shared);

                        Assert.True(volume >= -Tolerance, $"intersection is inside out: {volume}");
                        Assert.True(volume <= MathF.Min(one, other) + Tolerance, $"intersection grew: {volume}");
                    }
                },
                iter: 2_000
            );
    }

    [Fact]
    public void A_difference_and_an_intersection_together_are_the_original() {
        Gen.Select(Operand(3), Operand(3))
            .Sample(
                pair => {
                    var ((leftSize, leftAt), (rightSize, rightAt)) = pair;

                    var left = Box(leftSize, leftAt);
                    var right = Box(rightSize, rightAt);

                    var without = MeshBoolean.Apply(left, right, BooleanOperation.Difference);
                    var shared = MeshBoolean.Apply(left, right, BooleanOperation.Intersection);

                    // ⚠ The identity that catches everything a volume bound does not: A = (A − B) ∪
                    // (A ∩ B), exactly, for every pair. A classification that sent one sliver to the
                    // wrong side puts it in both halves or in neither, and either shows up here as a
                    // number that does not add up.
                    var total = (without is null ? 0f : Volume(without)) + (shared is null ? 0f : Volume(shared));

                    Assert.Equal(leftSize.X * leftSize.Y * leftSize.Z, total, Tolerance);
                },
                iter: 2_000
            );
    }

    [Fact]
    public void Cutting_a_solid_in_two_and_weighing_both_halves_gives_the_whole() {
        Gen.Select(Operand(1), Gen.Int[-4, 4], Gen.Int[0, 2])
            .Sample(
                values => {
                    var ((size, at), offset, axis) = values;

                    var normal = axis switch {
                        0 => Vector3.UnitX,
                        1 => Vector3.UnitY,
                        _ => Vector3.UnitZ
                    };

                    var plane = new Plane(normal, -offset * 0.5f);

                    var behind = MeshBoolean.PlaneCut(Box(size, at), plane);
                    var ahead = MeshBoolean.PlaneCut(Box(size, at), plane, keepFront: true);

                    AssertSound(behind, "back half");
                    AssertSound(ahead, "front half");

                    // ⚠ The cap is what this really tests. A cap wound the wrong way makes a half whose
                    // signed volume is negative, and a cap that was not built at all makes one that is
                    // not closed — and the two halves adding up to the whole is the one number that
                    // catches both at once.
                    var total = (behind is null ? 0f : Volume(behind)) + (ahead is null ? 0f : Volume(ahead));

                    Assert.Equal(size.X * size.Y * size.Z, total, Tolerance);
                },
                iter: 2_000
            );
    }

    const float Tolerance = 1e-3f;

    static float Volume(EditMesh mesh) {
        var triangles = mesh.Triangulate();
        var total = 0f;

        for (var index = 0; index + 2 < triangles.Length; index += 3) {
            var a = mesh.Positions[triangles[index]];
            var b = mesh.Positions[triangles[index + 1]];
            var c = mesh.Positions[triangles[index + 2]];

            total += Vector3.Dot(a, Vector3.Cross(b, c)) / 6f;
        }

        return total;
    }
}

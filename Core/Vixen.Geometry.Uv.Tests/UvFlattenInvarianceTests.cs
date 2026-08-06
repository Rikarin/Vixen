// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;
using Xunit;

namespace Vixen.Geometry.Uv.Tests;

/// <summary>Two things the answer must not depend on: how big the model is, and how it was numbered.</summary>
/// <remarks>
///     <para>
///         ⚠ <b>An absolute tolerance is a claim about how big the model is, and this engine has been
///         bitten by one twice.</b> <c>EditMesh</c>'s weld tolerance is relative to the bounds for that
///         reason, and docs/plan/24 § P1 records an absolute epsilon declaring sixty-four real
///         triangles at a capsule's pole degenerate. A flattener inherits the mesh's units squared
///         through the cotangent Laplacian, so the claim would be worse here rather than better.
///     </para>
///     <para>
///         ⚠ <b>And a vertex numbering is not a property of the surface.</b> An importer, a weld and a
///         boolean all renumber a mesh routinely; a pin chosen by "whichever index came first" passes
///         every test written on the mesh it was developed against and moves the whole map on the next
///         import of the same asset.
///     </para>
/// </remarks>
public class UvFlattenInvarianceTests {
    public static TheoryData<string> Shapes =>
        ["sphere-cut-open", "hemisphere", "saddle", "torus-slit", "cylinder-slit", "strip"];

    /// <summary>A power of two scales every coordinate by exactly that, bit for bit.</summary>
    /// <remarks>
    ///     ⚠ <b>Exactly, and it is worth saying why that is a reasonable thing to ask.</b> Scaling a
    ///     <see langword="float" /> by a power of two changes only its exponent, and every expression
    ///     in the ladder is homogeneous in the mesh's units — so each one picks up the same exact
    ///     factor, every ratio the solver forms is unchanged, and the iteration takes the same steps in
    ///     the same order. Anything less than bit equality here is an expression that mixed units with
    ///     a constant.
    /// </remarks>
    [Theory]
    [MemberData(nameof(Shapes))]
    public void APowerOfTwoScalesTheAnswerByExactlyThat(string shape) {
        const float Down = 1f / 1024f;
        const float Up = 1024f;

        var reference = Flatten(shape, 1f, out var referenceReport);
        var down = Flatten(shape, Down, out var downReport);
        var up = Flatten(shape, Up, out var upReport);

        Assert.Equal(referenceReport.Distortion, downReport.Distortion);
        Assert.Equal(referenceReport.Distortion, upReport.Distortion);

        foreach (var (corner, coordinate) in reference) {
            SameBits(coordinate * Down, down[corner], shape, corner, Down);
            SameBits(coordinate * Up, up[corner], shape, corner, Up);
        }
    }

    /// <summary>And a scale that is not a power of two is proportional to within the arithmetic.</summary>
    [Theory]
    [MemberData(nameof(Shapes))]
    public void TheSameChartAtAThousandthAndAThousandTimesIsTheSameMap(string shape) {
        var reference = Flatten(shape, 1f, out var referenceReport);
        var small = Flatten(shape, 1e-3f, out var smallReport);
        var large = Flatten(shape, 1e+3f, out var largeReport);

        // The measures carry no units at all, so these are not "close" but equal to the precision a
        // float holds after the whole ladder has run.
        Near(referenceReport.Distortion, smallReport.Distortion, shape, "1e-3");
        Near(referenceReport.Distortion, largeReport.Distortion, shape, "1e+3");

        var extent = Extent(reference);

        foreach (var (corner, coordinate) in reference) {
            Proportional(coordinate, small[corner], 1e-3f, extent, shape, corner);
            Proportional(coordinate, large[corner], 1e+3f, extent, shape, corner);
        }
    }

    /// <summary>Renumbering the vertices leaves the mapping where it was.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>The pins are chosen by <i>position</i> and not by index, which makes this stronger
    ///         than the similarity-invariance docs/plan/42 § D5 asks for.</b> An index tie-break would
    ///         still give the same conformal map — LSCM's answer is unique up to a similarity, and
    ///         moving the pins only moves the gauge — so the honest test of an index tie-break is a
    ///         Procrustes fit with a tolerance, which is precisely the test that hides how far the
    ///         gauge moved. Picking the pins from the geometry instead makes the two solves the same
    ///         system with its rows permuted, and then the coordinates themselves can be compared.
    ///     </para>
    ///     <para>
    ///         What is left is the summation order: the same sums assembled in a different order differ
    ///         in the last bits, which is why this is a tolerance rather than a bit comparison.
    ///     </para>
    /// </remarks>
    [Theory]
    [MemberData(nameof(Shapes))]
    public void RenumberingTheVerticesDoesNotMoveTheMap(string shape) {
        var mesh = FlattenFixtures.Build(shape);
        var moved = ShapeCorpus.Renumber(mesh, 0x51F3u);

        Assert.NotEqual(
            Enumerable.Range(0, mesh.PositionCount).Select(index => mesh.Positions[index]).ToArray(),
            Enumerable.Range(0, moved.PositionCount).Select(index => moved.Positions[index]).ToArray()
        );

        var reference = FlattenFixtures.ByCorner(UvUnwrap.Flatten(mesh, ShapeCorpus.OneChart(mesh), new(), out var before));
        var renumbered = FlattenFixtures.ByCorner(
            UvUnwrap.Flatten(moved, ShapeCorpus.OneChart(moved), new(), out var after)
        );

        Near(before.Distortion, after.Distortion, shape, "renumbered");

        var extent = Extent(reference);

        foreach (var (corner, coordinate) in reference) {
            var moved2 = renumbered[corner];
            var slip = (coordinate - moved2).Length() / extent;

            Assert.True(
                slip < 1e-4f,
                $"{shape}: corner {corner} moved {slip:0.000e+0} of the island's extent when the mesh was "
                + "renumbered. docs/plan/42 § D5 — the pin is chosen from the geometry so that it cannot."
            );
        }
    }

    static Dictionary<int, Vector2> Flatten(string shape, float scale, out UvReport report) {
        var mesh = FlattenFixtures.Build(shape, scale);

        return FlattenFixtures.ByCorner(UvUnwrap.Flatten(mesh, ShapeCorpus.OneChart(mesh), new(), out report));
    }

    static float Extent(Dictionary<int, Vector2> coordinates) {
        var lowest = new Vector2(float.MaxValue, float.MaxValue);
        var highest = new Vector2(float.MinValue, float.MinValue);

        foreach (var coordinate in coordinates.Values) {
            lowest = Vector2.Min(lowest, coordinate);
            highest = Vector2.Max(highest, coordinate);
        }

        return (highest - lowest).Length();
    }

    static void SameBits(Vector2 expected, Vector2 actual, string shape, int corner, float scale) {
        Assert.True(
            BitConverter.SingleToInt32Bits(expected.X) == BitConverter.SingleToInt32Bits(actual.X)
            && BitConverter.SingleToInt32Bits(expected.Y) == BitConverter.SingleToInt32Bits(actual.Y),
            $"{shape} at {scale}×: corner {corner} is {actual} against {expected}. A power of two is an "
            + "exponent, so every homogeneous expression in the ladder picks up exactly it."
        );
    }

    static void Proportional(Vector2 unit, Vector2 scaled, float scale, float extent, string shape, int corner) {
        var slip = ((unit * scale) - scaled).Length() / (extent * MathF.Abs(scale));

        Assert.True(
            slip < 1e-4f,
            $"{shape} at {scale}×: corner {corner} is {scaled} where {unit * scale} was expected, which is "
            + $"{slip:0.000e+0} of the island's extent."
        );
    }

    static void Near(UvDistortion expected, UvDistortion actual, string shape, string what) {
        Assert.Equal(expected.Flipped, actual.Flipped);
        Assert.Equal(expected.Angular, actual.Angular, 3);
        Assert.Equal(expected.Area, actual.Area, 3);
        Assert.Equal(expected.StretchL2, actual.StretchL2, 3);

        Assert.True(
            MathF.Abs(expected.StretchLInf - actual.StretchLInf) < 1e-3f * MathF.Max(1f, expected.StretchLInf),
            $"{shape} ({what}): L^∞ went from {expected.StretchLInf} to {actual.StretchLInf}."
        );
    }
}

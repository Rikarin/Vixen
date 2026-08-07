// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using System.Text.RegularExpressions;
using Vixen.Core.Mathematics;
using Xunit;

namespace Vixen.Geometry.Remeshing.Tests;

/// <summary>The budget warning is about the mesh that came out, not about the one that was planned.</summary>
/// <remarks>
///     <para>
///         <b>docs/plan/41 § Part 4's claim is that a remesh which went wrong says so.</b> The budget
///         warning was decided from <c>Quantizer.QuadCount(layout, counts)</c> — the quantization's
///         intent — while <see cref="RemeshReport.QuadCount" /> is counted off the extraction, and the
///         two diverge by every patch dropped between them. A result over the tolerance with no line
///         mentioning the budget is indistinguishable, to a build script, from one that met it.
///     </para>
///     <para>
///         ⚠ <b>Asserted against the returned mesh rather than against the report's own field.</b> Both
///         are supposed to say the same thing and the whole defect is that two numbers claiming to be
///         the same thing were not, so a test that compared the report with itself would pass either
///         way.
///     </para>
/// </remarks>
public class BudgetWarningTests {
    /// <summary>Whenever the mesh is over the tolerance there is a warning, and it quotes the mesh.</summary>
    [Theory]
    [InlineData("box", 96)]
    [InlineData("cylinder", 96)]
    [InlineData("stairs", 96)]
    [InlineData("union", 96)]
    [InlineData("sphere", 400)]
    [InlineData("plate", 400)]
    public void TheWarningAgreesWithTheMeshItShipped(string name, int budget) {
        var output = Remesher.Remesh(RemesherTests.Fixture(name), new() { TargetQuads = budget }, out var report);
        var faces = output.FaceCount;
        var over = faces > budget * Remesher.BudgetTolerance;
        var said = Quoted(report);

        if (!over) {
            Assert.Null(said);

            return;
        }

        Assert.True(
            said is not null,
            $"{name}: the mesh has {faces} faces against a budget of {budget}, which is "
            + $"{(float) faces / budget:F2}× a tolerance of {Remesher.BudgetTolerance:F2}, and not one of "
            + $"the {report.Warnings.Count} warnings mentions the budget."
        );

        Assert.Equal(faces, said);
    }

    /// <summary>A mirrored remesh warns about the whole mesh, not about the half it planned.</summary>
    /// <remarks>
    ///     ⚠ <b>The case that makes the defect unarguable, because the two numbers differ by exactly
    ///     two.</b> The quantization runs on half the surface and the mirror doubles it afterwards, so a
    ///     warning decided from the layout quotes half of what it shipped — the report said "169665
    ///     against 96" for a mesh carrying 339330 faces. A factor of two is not a rounding disagreement.
    /// </remarks>
    [Fact]
    public void AMirroredRemeshWarnsAboutTheWholeMesh() {
        var output = Remesher.Remesh(
            RemesherTests.Fixture("cylinder"),
            new() { TargetQuads = 96, Symmetry = new Plane(Vector3.UnitX, 0f) },
            out var report
        );

        Assert.True(output.FaceCount > 0, string.Join(" · ", report.Warnings));

        if (output.FaceCount <= 96 * Remesher.BudgetTolerance) {
            return;
        }

        var said = Quoted(report);

        Assert.True(said is not null, $"no budget warning for {output.FaceCount} faces against 96.");
        Assert.Equal(output.FaceCount, said);
    }

    /// <summary>The face count a budget warning quotes, or null when there is no such warning.</summary>
    static int? Quoted(RemeshReport report) {
        foreach (var warning in report.Warnings) {
            var found = Regex.Match(warning, @"The budget was not met: (\d+) quads", RegexOptions.None, TimeSpan.FromSeconds(5));

            if (found.Success) {
                return int.Parse(found.Groups[1].Value, CultureInfo.InvariantCulture);
            }
        }

        return null;
    }
}

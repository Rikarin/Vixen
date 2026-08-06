// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Xunit;

namespace Vixen.Geometry.Remeshing.Tests;

/// <summary>The shape of the worst quad, which nothing was measuring.</summary>
/// <remarks>
///     <para>
///         <b>docs/plan/41 § Part 4 lists <see cref="RemeshReport.MinScaledJacobian" /> among the nine
///         fields the report exists to carry, and says what it means in one line: "the worst quad's
///         shape quality. A negative one is an inverted quad."</b> R3 filled the field in and no test
///         read it, so the number went into the report and out of the build without anybody having to
///         look at it — which is precisely the failure the report was written to prevent.
///     </para>
///     <para>
///         ⚠ <b>These are characterisation tests and the numbers in them are a defect, not a
///         target.</b> A scaled Jacobian of zero means a quad with no area; a negative one means a
///         quad folded over itself. Both exist in today's output on every fixture. They are pinned
///         here so that the defect is visible in the suite rather than in the report, and so that the
///         phase that fixes it has something that fails when it does.
///     </para>
///     <para>
///         The all-quad guarantee is orthogonal to this and is genuinely met — every face has four
///         sides on every fixture. ⚠ <b>Four sides is not the same as four <i>usable</i> sides</b>,
///         and reading `IsAllQuad` as a quality statement is the mistake this file exists to make
///         hard.
///     </para>
/// </remarks>
public class QuadQualityTests {
    /// <summary>
    ///     Every fixture's worst quad, recorded. ⚠ The bound is loose on purpose — it is where the
    ///     implementation is today and not where it should be, and tightening it is the work.
    /// </summary>
    [Theory]
    [InlineData("box")]
    [InlineData("cylinder")]
    [InlineData("stairs")]
    [InlineData("plate")]
    [InlineData("union")]
    [InlineData("difference")]
    [InlineData("sphere")]
    public void TheWorstQuadIsRecordedEvenWhereItIsDegenerate(string name) {
        Remesher.Remesh(RemesherTests.Fixture(name), new() { TargetQuads = 400 }, out var report);

        Assert.True(report.QuadCount > 0, string.Join(" · ", report.Warnings));

        // A scaled Jacobian is a cosine-like ratio, so it cannot leave [-1, 1] whatever the quad
        // looks like. A value outside it is an arithmetic bug rather than a bad quad.
        Assert.InRange(report.MinScaledJacobian, -1f, 1f);

        Assert.True(
            report.MinScaledJacobian > -0.5f,
            $"{name}: the worst quad's scaled Jacobian is {report.MinScaledJacobian:F3}. Below -0.5 is not "
            + "a sliver, it is a quad folded most of the way back on itself, and the extractor should "
            + "have refused its patch."
        );
    }

    /// <summary>
    ///     ⚠ <b>The guard that keeps this file honest: at least one fixture must still be bad.</b> A
    ///     characterisation test whose subject has quietly been fixed reads as a passing test about a
    ///     defect that no longer exists, and the next person tightens nothing because nothing is
    ///     failing. When this fails, the bound above is what to tighten — and this fact is what to
    ///     delete.
    /// </summary>
    [Fact]
    public void SomeFixtureStillHasADegenerateQuad() {
        string[] fixtures = ["box", "cylinder", "stairs", "plate", "union", "difference", "sphere"];
        var worst = 1f;
        var offender = string.Empty;

        foreach (var name in fixtures) {
            Remesher.Remesh(RemesherTests.Fixture(name), new() { TargetQuads = 400 }, out var report);

            if (report.QuadCount > 0 && report.MinScaledJacobian < worst) {
                worst = report.MinScaledJacobian;
                offender = name;
            }
        }

        Assert.True(
            worst <= 0f,
            $"Every fixture now has a positive scaled Jacobian — the worst is {offender} at {worst:F3}. "
            + "The degenerate-quad defect this file characterises is fixed: tighten "
            + $"{nameof(TheWorstQuadIsRecordedEvenWhereItIsDegenerate)}'s bound and delete this fact."
        );
    }
}

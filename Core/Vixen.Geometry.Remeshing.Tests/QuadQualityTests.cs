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
    ///     Every fixture's worst quad, recorded per fixture. ⚠ The bounds are loose on purpose — they
    ///     are where the implementation is today and not where it should be, and tightening them is the
    ///     work.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>Four of these bounds got much worse without the output changing at all, because
    ///         they were reading a metric that stopped scanning at the first degenerate face.</b>
    ///         <see cref="RemeshMetrics.ScaledJacobian" /> returned <c>0f</c> from the whole function on
    ///         a collapsed corner, so box, cylinder, plate and union reported <c>0.000</c> — "there is a
    ///         flat quad somewhere" — and every face after that one went unmeasured. Their true worst
    ///         corners are <c>−0.965</c>, <c>−0.997</c>, <c>−0.840</c> and <c>−0.991</c>: quads folded
    ///         almost completely back on themselves, on fixtures that had been recorded as merely
    ///         degenerate. The bounds below are the measured values and the defect they characterise is
    ///         bigger than it was written down as, not smaller.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The bounds are loose on purpose — they are where the implementation is today and not
    ///         where it should be, and tightening them is the work.</b> Only the sphere is anywhere near
    ///         usable, at <c>−0.079</c> over 4 bad faces of 372; the rest run 11 to 62 inverted faces
    ///         out of 513 to 687. docs/plan/41 § D8 names the cause and it is unchanged by this
    ///         measurement: a Coons blend of four curved boundary chains is not injective, so the patch
    ///         interior folds, and the fix is a real per-patch parameterization.
    ///     </para>
    /// </remarks>
    [Theory]
    [InlineData("box", -0.97f)]
    [InlineData("cylinder", -1f)]
    [InlineData("stairs", -1f)]
    [InlineData("plate", -0.85f)]
    [InlineData("union", -1f)]
    [InlineData("difference", -1f)]
    [InlineData("sphere", -0.5f)]
    public void TheWorstQuadIsRecordedEvenWhereItIsDegenerate(string name, float bound) {
        Remesher.Remesh(RemesherTests.Fixture(name), new() { TargetQuads = 400 }, out var report);

        Assert.True(report.QuadCount > 0, string.Join(" · ", report.Warnings));

        // A scaled Jacobian is a cosine-like ratio, so it cannot leave [-1, 1] whatever the quad
        // looks like. A value outside it is an arithmetic bug rather than a bad quad.
        Assert.InRange(report.MinScaledJacobian, -1f, 1f);

        Assert.True(
            report.MinScaledJacobian >= bound,
            $"{name}: the worst quad's scaled Jacobian is {report.MinScaledJacobian:F3} against a recorded "
            + $"{bound:F3}. Below -0.5 is not a sliver, it is a quad folded most of the way back on itself, "
            + "and the extractor should have refused its patch."
        );
    }

    /// <summary>A degenerate face does not end the scan, so a worse face behind it is still found.</summary>
    /// <remarks>
    ///     <para>
    ///         <b>The bug that made four fixtures read <c>0.000</c> while holding inverted quads.</b>
    ///         <see cref="RemeshMetrics.ScaledJacobian" /> is documented as "the minimum over every
    ///         corner of every face" and returned <c>0f</c> from the function itself on a face with a
    ///         collapsed corner — so the value was a sentinel meaning "a degenerate face exists", and
    ///         everything after the first one was never looked at.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Two faces and the order matters.</b> The degenerate one is built first, so the old
    ///         code returns before it reaches the folded one; anything that reports the fold has kept
    ///         scanning. A fixture cannot make this point — it would only pin today's numbers, and the
    ///         numbers are what the bug was hiding.
    ///     </para>
    /// </remarks>
    [Fact]
    public void ADegenerateFaceDoesNotHideAWorseOneBehindIt() {
        var mesh = new EditMesh();

        // A quad with two corners on one position: a collapsed corner, whose angle is undefined.
        var flat = new[] {
            mesh.AddPosition(new(0f, 0f, 0f)),
            mesh.AddPosition(new(1f, 0f, 0f)),
            mesh.AddPosition(new(1f, 0f, 0f)),
            mesh.AddPosition(new(0f, 1f, 0f))
        };

        mesh.AddFace(flat);

        // And a dart behind it, whose third corner is reflex — the quad folds back through itself.
        // ⚠ Not a symmetric bow-tie: that one's Newell sum cancels to exactly zero, so it reads as a
        // face with no normal and scores zero rather than negative, which would not tell the two
        // behaviours apart.
        var folded = new[] {
            mesh.AddPosition(new(0f, 0f, 1f)),
            mesh.AddPosition(new(4f, 0f, 1f)),
            mesh.AddPosition(new(1f, 1f, 1f)),
            mesh.AddPosition(new(0f, 4f, 1f))
        };

        mesh.AddFace(folded);

        var worst = RemeshMetrics.ScaledJacobian(mesh);

        Assert.True(
            worst < 0f,
            $"the scan reported {worst:F3}. A zero means it stopped at the collapsed corner and never "
            + "reached the folded quad behind it, which is the whole defect."
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

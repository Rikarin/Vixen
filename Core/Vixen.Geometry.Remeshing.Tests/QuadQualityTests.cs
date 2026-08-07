// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;
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
    ///     <para>
    ///         ⚠ <b>The crease attribution was wrong and it was measured to be wrong, so these bounds are
    ///         § D8's work after all.</b> Counted over the conditioned mesh, the number of feature edges
    ///         whose two triangles land in the same patch is <b>0</b> on every one of the seven
    ///         fixtures: no patch spans a crease and § D4's promise is kept. What the folds were is the
    ///         transfinite blend, and what sent patches to it was
    ///         <see cref="PatchParameterization" />'s own verification — a triangle with all three
    ///         corners on one straight side of the unit square is collinear <i>by construction</i>, and
    ///         both the embedding test and the rim test read that as a failure. The correlation with a
    ///         40° output edge is real and runs the other way: patch boundaries lie on creases, so a
    ///         quad in a grid's boundary row touches one.
    ///     </para>
    ///     <para>
    ///         <b>Where they are now, all seven improved on both numbers at once</b> — box −0.874 over
    ///         21 faces, cylinder −0.985 over 35, stairs −0.901 over 22, plate −0.418 over 6, union
    ///         −0.804 over 22, difference −0.981 over 15, sphere −0.044 over 1; 210 inverted faces in
    ///         total down to 122. ⚠ <b>They are still a defect and not a target.</b> What is left is a
    ///         fold no single-vertex smoothing step can improve — <c>PatchExtractor.Relax</c> refuses
    ///         any move that folds and has stopped moving by its budget — so closing the rest is an
    ///         untangler's job.
    ///     </para>
    /// </remarks>
    [Theory]
    [InlineData("box", -0.89f)]
    [InlineData("cylinder", -0.99f)]
    [InlineData("stairs", -0.92f)]
    [InlineData("plate", -0.45f)]
    [InlineData("union", -0.82f)]
    [InlineData("difference", -0.99f)]
    [InlineData("sphere", -0.06f)]
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

    /// <summary>Where the quads that are still inverted actually are: on the creases.</summary>
    /// <remarks>
    ///     <para>
    ///         <b>The attribution for what <see cref="PatchParameterization" /> did not close, measured
    ///         off the output mesh and nothing else.</b> An output edge whose two quads meet at
    ///         <see cref="Sharp" /> or more is a crease; a quad that has one is on a crease. On every
    ///         hard-surface fixture an inverted quad is several times more likely to be one of those:
    ///         box 9.5% against 0.3% — a factor of 29 — cylinder 23.3% against 3.0%, stairs 10.4%
    ///         against 2.0%, plate 2.9% against 0.4%, union 10.1% against 1.8%, difference 7.1%
    ///         against 3.3%.
    ///     </para>
    ///     <para>
    ///         ⚠⚠ <b>This correlation is real and the cause it was first read as is not, which is worth
    ///         keeping written down because the reading was the obvious one.</b> It was taken as
    ///         evidence that a patch's interior crosses a feature polyline — docs/plan/41 § D4's
    ///         promise not being kept, and so § D7's layout at fault. <b>Measured directly, that is
    ///         false</b>: counted over the conditioned mesh, the number of feature edges whose two
    ///         triangles land in the same patch is <b>0</b> on all seven fixtures. Every crease is a
    ///         partition boundary.
    ///     </para>
    ///     <para>
    ///         <b>The arrow runs the other way.</b> A crease <i>is</i> a patch boundary, so a quad in a
    ///         grid's boundary row has a creased edge — and the boundary row is where the folds are,
    ///         because it is the row built against the rim. The sphere is still the control and still
    ///         makes the point, but the point it makes is that a fixture with no crease has no
    ///         boundary-row bucket to be worse in, not that a crease causes a fold.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The bound is a ratio and not a count, on purpose.</b> Asserting a number of inverted
    ///         faces pins today's output and breaks on any change that moves a patch boundary. What it
    ///         states now is that the inversions are still concentrated on the boundary row — which is
    ///         true, is measured, and stops being true when the grids stop folding against their rims.
    ///     </para>
    /// </remarks>
    [Theory]
    [InlineData("box")]
    [InlineData("cylinder")]
    [InlineData("stairs")]
    [InlineData("plate")]
    [InlineData("union")]
    [InlineData("difference")]
    public void InvertedQuadsClusterOnTheCreases(string name) {
        var quads = Remesher.Remesh(RemesherTests.Fixture(name), new() { TargetQuads = 400 }, out var report);

        Assert.True(report.QuadCount > 0, string.Join(" · ", report.Warnings));

        var creased = Creases(quads);
        var on = (Bad: 0, All: 0);
        var off = (Bad: 0, All: 0);

        for (var face = 0; face < quads.FaceCount; face++) {
            var loop = quads.CornersOf(face);
            var normal = ScaleSafe.Unit(quads.Normal(face));

            if (normal.LengthSquared() <= 0f) {
                continue;
            }

            var touches = false;
            var folded = false;

            for (var at = 0; at < loop.Length; at++) {
                var here = quads.Positions[loop[at]];
                var next = loop[(at + 1) % loop.Length];
                var previous = loop[(at + loop.Length - 1) % loop.Length];

                touches |= creased.Contains(Edge(loop[at], next));

                var ahead = ScaleSafe.Unit(quads.Positions[next] - here);
                var behind = ScaleSafe.Unit(quads.Positions[previous] - here);

                folded |= ahead.LengthSquared() > 0f
                    && behind.LengthSquared() > 0f
                    && Vector3.Dot(Vector3.Cross(ahead, behind), normal) < 0f;
            }

            if (touches) {
                on = (on.Bad + (folded ? 1 : 0), on.All + 1);
            } else {
                off = (off.Bad + (folded ? 1 : 0), off.All + 1);
            }
        }

        Assert.True(on.All > 0 && off.All > 0, $"{name}: the fixture has no crease to compare against.");

        var onCrease = (float) on.Bad / on.All;
        var away = (float) off.Bad / off.All;

        Assert.True(
            onCrease > away * 2f,
            $"{name}: {onCrease:P2} of the quads on a crease are inverted against {away:P2} of the ones "
            + "away from any. The inversions are no longer concentrated on the boundary row a crease "
            + "marks out, so what docs/plan/41's criterion 8 is left holding has changed. Re-attribute "
            + "it before loosening this — and note that the crease is the boundary rather than the "
            + "cause: no patch has ever been measured to span one."
        );
    }

    /// <summary>How sharp an output edge has to be before it counts as a crease, in degrees.</summary>
    /// <remarks>
    ///     ⚠ Well above the wobble a relaxed quad grid has on a smooth surface and well below a box's
    ///     90°, so nothing about which fixture is which depends on where exactly it sits.
    /// </remarks>
    public const float Sharp = 40f;

    /// <summary>Every output edge whose two quads meet sharply, plus every open rim.</summary>
    static HashSet<(int, int)> Creases(EditMesh quads) {
        var faces = new Dictionary<(int, int), List<int>>();

        for (var face = 0; face < quads.FaceCount; face++) {
            var loop = quads.CornersOf(face);

            for (var at = 0; at < loop.Length; at++) {
                var edge = Edge(loop[at], loop[(at + 1) % loop.Length]);

                if (!faces.TryGetValue(edge, out var found)) {
                    faces[edge] = found = [];
                }

                found.Add(face);
            }
        }

        var creased = new HashSet<(int, int)>();
        var limit = MathF.Cos(Sharp * MathF.PI / 180f);

        foreach (var (edge, found) in faces) {
            if (found.Count != 2) {
                creased.Add(edge);

                continue;
            }

            var one = ScaleSafe.Unit(quads.Normal(found[0]));
            var two = ScaleSafe.Unit(quads.Normal(found[1]));

            if (one.LengthSquared() > 0f && two.LengthSquared() > 0f && Vector3.Dot(one, two) < limit) {
                creased.Add(edge);
            }
        }

        return creased;
    }

    /// <summary>An edge, with its two positions in ascending order.</summary>
    static (int, int) Edge(int one, int two) => one < two ? (one, two) : (two, one);

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

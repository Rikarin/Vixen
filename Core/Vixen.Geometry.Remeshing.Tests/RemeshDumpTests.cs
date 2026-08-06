// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;
using Vixen.Core.Threading;
using Xunit;

namespace Vixen.Geometry.Remeshing.Tests;

/// <summary>docs/plan/41 § D1: every stage can emit its artefact, and § R4 is where they land.</summary>
/// <remarks>
///     <para>
///         <b>When a remesh looks wrong, <i>which stage</i> is the first question.</b>
///         <see cref="RemeshReport" /> says which one was slow and which one dropped something;
///         <see cref="RemeshDump" /> says what each one produced. These are the assertions that the
///         artefacts are the stages' own rather than something adjacent — a line set with the right
///         number of segments and the wrong endpoints looks exactly like a correct one until somebody
///         renders it.
///     </para>
///     <para>
///         ⚠ <b>Nothing here writes a file, and the type cannot.</b> <c>Core/</c> is under the
///         virtual-path rule — no <c>System.IO.Path</c>, no <c>File</c> — so every artefact is a plain
///         array a caller turns into whatever its viewer reads.
///     </para>
/// </remarks>
public class RemeshDumpTests {
    /// <summary>Every stage's artefact comes back, and each one is the size its stage produced.</summary>
    [Theory]
    [InlineData("box")]
    [InlineData("sphere")]
    [InlineData("plate")]
    public void Every_stage_emits_its_artefact(string name) {
        var source = RemesherTests.Fixture(name);
        var dump = RemeshDump.Capture(source, new() { TargetQuads = 400 });

        Remesher.Remesh(source, new() { TargetQuads = 400 }, out var report);

        // ① The conditioned triangles are the ones the report counted.
        Assert.Equal(report.Conditioning.Triangles, dump.Conditioned.FaceCount);
        Assert.True(dump.Conditioned.PositionCount > 0);

        // ③ Two crossed segments per vertex that has a tangent plane, so the count is even and it is
        // bounded by twice the vertex count.
        Assert.NotEmpty(dump.Field);
        Assert.Equal(0, dump.Field.Count % 2);
        Assert.InRange(dump.Field.Count, 2, 2 * dump.Conditioned.PositionCount);

        // ③ And the singularities are the ones the field stage counted.
        Assert.Equal(report.Stages.Single(stage => stage.Stage == RemeshStage.Field).Elements, dump.Singularities.Count);

        // ④ One region per conditioned triangle, in triangle order, and at least one claimed.
        Assert.Equal(dump.Conditioned.FaceCount, dump.Layout.Count);

        for (var triangle = 0; triangle < dump.Layout.Count; triangle++) {
            Assert.Equal(triangle, dump.Layout[triangle].Triangle);
            Assert.InRange(dump.Layout[triangle].Patch, -1, report.Stages.Single(stage => stage.Stage == RemeshStage.Layout).Elements - 1);
        }

        Assert.Contains(dump.Layout, region => region.Patch >= 0);

        // ⑤ One label per arc, and the arcs are the ones the quantize stage counted.
        Assert.Equal(report.Stages.Single(stage => stage.Stage == RemeshStage.Quantize).Elements, dump.Quantization.Count);

        for (var arc = 0; arc < dump.Quantization.Count; arc++) {
            Assert.Equal(arc, dump.Quantization[arc].Arc);
            Assert.True(dump.Quantization[arc].Quads >= 0);
            Assert.True(dump.Quantization[arc].Target >= 0f);
        }
    }

    /// <summary>The field's crosses are crossed, and they scale with the model rather than being a length.</summary>
    /// <remarks>
    ///     ⚠ <b>The failure this repository has been bitten by three times.</b> An arm length that was
    ///     a fixed number would draw nothing on a millimetre-wide part and a hairball on a
    ///     kilometre-wide one — <see cref="RemeshDump.CrossArm" /> is a fraction of the bounding box's
    ///     diagonal for the same reason every number in <see cref="RemeshReport" /> is.
    ///
    ///     ⚠ <b>The claim is per capture and not between two, because the two are not comparable and
    ///     R1 recorded why.</b> <c>ScaleInvarianceTests</c> measured that the pre-remesh does not agree
    ///     exactly at a thousandth and a thousand times — <c>0.001f</c> is not a binary fraction, so a
    ///     mesh of equal edge lengths breaks its ties differently — which reaches here as a different
    ///     vertex count and therefore a different number of crosses. What must hold is that each
    ///     capture's arms are the same fraction of <i>its own</i> diagonal, which is the property an
    ///     absolute length would not have.
    /// </remarks>
    /// <param name="factor">What the fixture is scaled by before it is captured.</param>
    [Theory]
    [InlineData(1e-3f)]
    [InlineData(1f)]
    [InlineData(1e+3f)]
    public void The_field_lines_are_a_fraction_of_the_diagonal(float factor) {
        var source = Scaled(RemesherTests.Fixture("sphere"), factor);
        var dump = RemeshDump.Capture(source, new() { TargetQuads = 400 });

        Assert.NotEmpty(dump.Field);

        var lowest = dump.Conditioned.Positions[0];
        var highest = lowest;

        for (var position = 1; position < dump.Conditioned.PositionCount; position++) {
            lowest = Vector3.Min(lowest, dump.Conditioned.Positions[position]);
            highest = Vector3.Max(highest, dump.Conditioned.Positions[position]);
        }

        var arm = 2f * RemeshDump.CrossArm * (highest - lowest).Length();

        Assert.True(arm > 0f, "A cross with no length is not a line set.");

        foreach (var segment in dump.Field) {
            var length = (segment.To - segment.From).Length();

            Assert.True(
                MathF.Abs(length - arm) <= 1e-3f * arm,
                $"×{factor}: a cross arm is {length:E4} against {arm:E4} of the diagonal."
            );
        }

        // A cross is two segments about one point, so consecutive pairs share a midpoint.
        for (var pair = 0; pair + 1 < dump.Field.Count; pair += 2) {
            var first = 0.5f * (dump.Field[pair].From + dump.Field[pair].To);
            var second = 0.5f * (dump.Field[pair + 1].From + dump.Field[pair + 1].To);

            Assert.True((first - second).Length() <= 1e-3f * arm, $"Segments {pair} and {pair + 1} are not one cross.");
        }
    }

    /// <summary>A feature line is a run of conditioned edges, which is what makes § D4 structural.</summary>
    [Fact]
    public void A_feature_line_runs_along_the_surface() {
        var dump = RemeshDump.Capture(RemesherTests.Fixture("box"), new() { TargetQuads = 400 });

        Assert.NotEmpty(dump.Features);

        var positions = new HashSet<Vector3>();

        for (var position = 0; position < dump.Conditioned.PositionCount; position++) {
            positions.Add(dump.Conditioned.Positions[position]);
        }

        foreach (var segment in dump.Features) {
            Assert.Contains(segment.From, positions);
            Assert.Contains(segment.To, positions);
            Assert.NotEqual(segment.From, segment.To);
        }
    }

    /// <summary>The dump is the remesh's own artefacts, which is only true because both are deterministic.</summary>
    /// <remarks>
    ///     ⚠ <b>Re-running the stages is legitimate exactly to the extent that § D14 holds.</b> The
    ///     alternative — a capture hook threaded through <see cref="Remesher" /> — would put a
    ///     debugging concern in the middle of a pipeline every caller pays for. What makes the cheaper
    ///     choice honest is that two captures of one input are the same capture, at any worker count.
    /// </remarks>
    [Fact]
    public void Two_captures_of_one_input_are_one_capture() {
        var source = RemesherTests.Fixture("sphere");
        var settings = new RemeshSettings { TargetQuads = 400 };
        var first = RemeshDump.Capture(source, settings);

        using var scheduler = new JobScheduler(4);

        foreach (var again in new[] { RemeshDump.Capture(source, settings), RemeshDump.Capture(source, settings, scheduler) }) {
            Assert.Equal(first.Field, again.Field);
            Assert.Equal(first.Features, again.Features);
            Assert.Equal(first.Singularities, again.Singularities);
            Assert.Equal(first.Layout, again.Layout);
            Assert.Equal(first.Quantization, again.Quantization);
            Assert.Equal(first.Warnings, again.Warnings);
        }
    }

    /// <summary>A mesh nothing can be made of refuses with empty artefacts, and never throws.</summary>
    [Fact]
    public void A_refusal_is_empty_artefacts_and_a_reason() {
        var dump = RemeshDump.Capture(new EditMesh(), new() { TargetQuads = 400 });

        Assert.Equal(0, dump.Conditioned.FaceCount);
        Assert.Empty(dump.Field);
        Assert.Empty(dump.Features);
        Assert.Empty(dump.Layout);
        Assert.Empty(dump.Quantization);
        Assert.NotEmpty(dump.Warnings);
    }

    /// <summary>Nothing in the corpus of deliberately broken meshes makes it throw.</summary>
    /// <remarks>
    ///     docs/plan/41's seventh exit criterion applies to a debugging facility more than to anything
    ///     else: a dump that throws on the input somebody is debugging is unavailable exactly when it
    ///     is wanted.
    /// </remarks>
    [Fact]
    public void A_broken_mesh_dumps_or_refuses_and_never_throws() {
        foreach (var (name, source) in BrokenMeshes.Corpus()) {
            var dump = RemeshDump.Capture(source, new() { TargetQuads = 200 });

            Assert.Equal(dump.Conditioned.FaceCount, dump.Layout.Count);
            Assert.NotNull(dump.Warnings);
            _ = name;
        }
    }

    static EditMesh Scaled(EditMesh source, float factor) {
        var scaled = new EditMesh(source);

        for (var position = 0; position < scaled.PositionCount; position++) {
            scaled.MovePosition(position, scaled.Positions[position] * factor);
        }

        return scaled;
    }
}

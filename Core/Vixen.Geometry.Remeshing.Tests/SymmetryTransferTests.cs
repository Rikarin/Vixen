// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;
using Xunit;

namespace Vixen.Geometry.Remeshing.Tests;

/// <summary>Two features that each work and do not yet compose.</summary>
/// <remarks>
///     <para>
///         <b>§ D11's exact mirror was written against the overload that has no attributes, and
///         stage seven arrived on a different branch.</b> Merging the two left
///         <see cref="Remesher.Remesh(EditMesh, SourceAttributes, RemeshSettings, out RemeshReport, out TransferResult, Vixen.Core.Threading.JobScheduler?)" />
///         taking the mirror's early return before it reaches the transfer — so a symmetric remesh
///         comes back with an empty <see cref="TransferResult" /> rather than the source's colours
///         and skin weights.
///     </para>
///     <para>
///         ⚠ <b>This is a characterisation test and the behaviour it pins is wrong.</b> It exists
///         because the alternative was a comment in <c>Remesher.cs</c> claiming a test held the gap
///         open when none did — and a gap that only a comment records is a gap the next person
///         closes by deleting the comment. Composing them means teaching the mirror pass to reflect
///         an attribute set as well as a mesh, which is real work rather than a line of glue: a
///         mirrored vertex's skin weights belong to the *mirrored* bone, and nothing in
///         <see cref="SkinBinding" /> knows which bone that is.
///     </para>
///     <para>
///         When this fails, the gap is closed: delete it and assert the transfer instead.
///     </para>
/// </remarks>
public class SymmetryTransferTests {
    [Fact]
    public void ASymmetricRemeshStillLosesItsAttributesAndSaysSoHere() {
        var source = RemesherTests.Fixture("box");

        var colors = new Vector4[source.CornerCount];

        for (var corner = 0; corner < colors.Length; corner++) {
            colors[corner] = new(1f, 0.5f, 0.25f, 1f);
        }

        Remesher.Remesh(
            source,
            new SourceAttributes { Colors = colors },
            new RemeshSettings { TargetQuads = 200, Symmetry = new Plane(Vector3.UnitX, 0f) },
            out var report,
            out var transferred
        );

        Assert.True(report.QuadCount > 0, string.Join(" · ", report.Warnings));

        Assert.True(
            transferred.Colors.Count == 0,
            $"A symmetric remesh now carries {transferred.Colors.Count} colours across. The mirror pass "
            + "and stage seven compose — delete this test and assert the transfer, and remove the ⚠ "
            + "paragraph in Remesher.Remesh that points at it."
        );
    }

    /// <summary>
    ///     ⚠ The control, and the reason the fact above is about composition rather than about
    ///     transfer being broken: the same source and the same attributes, with symmetry off, do
    ///     carry across. Without this, a transfer that had stopped working entirely would read as
    ///     the known gap.
    /// </summary>
    [Fact]
    public void TheSameSourceWithoutSymmetryDoesCarryItsColours() {
        var source = RemesherTests.Fixture("box");

        var colors = new Vector4[source.CornerCount];

        for (var corner = 0; corner < colors.Length; corner++) {
            colors[corner] = new(1f, 0.5f, 0.25f, 1f);
        }

        Remesher.Remesh(
            source,
            new SourceAttributes { Colors = colors },
            new RemeshSettings { TargetQuads = 200 },
            out var report,
            out var transferred
        );

        Assert.True(report.QuadCount > 0, string.Join(" · ", report.Warnings));
        Assert.NotEmpty(transferred.Colors);
    }
}

// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;
using Xunit;

namespace Vixen.Geometry.Uv.Tests;

/// <summary>docs/plan/42 § D3's unconditional partition, and the mesh it must not fire on.</summary>
/// <remarks>
///     <para>
///         ⚠ <b>§ D3 partitions on <i>material</i> boundaries, and a face group is only sometimes
///         one.</b> A mesh built by <see cref="EditMesh.FromTriangles" /> carries the coplanarity guess
///         <see cref="EditMesh.Regroup" /> made, and on a faceted surface that is close to one group per
///         triangle. Partitioning on it decides the chart count before a single distortion measurement
///         is taken, so every later stage — the recursion, the merge-back pass, the whole inversion § D3
///         is built on — is working inside one triangle and can do nothing at all.
///     </para>
///     <para>
///         <b>Measured on sixteen image-to-3D GLBs</b> of 13 165 to 25 439 triangles: between 13 165 and
///         24 197 charts, one per triangle to within a rounding, in a document whose own Part 6 puts
///         xatlas at 51.6 charts and MeshTailor at 10.4.
///     </para>
/// </remarks>
public class UvFacetedGroupTests {
    /// <summary>A faceted surface charts by distortion, not by its coplanarity groups.</summary>
    /// <remarks>
    ///     ⚠ <b>The first two assertions are the defect and the third is the fix.</b> The fixture really
    ///     does have a group per triangle — that is not something the charter may quietly rely on being
    ///     false — and the answer is to notice that they are a guess, not to stop reading groups.
    /// </remarks>
    [Fact]
    public void A_faceted_surface_charts_by_distortion_and_not_by_its_coplanarity_groups() {
        var mesh = Faceted(16, 16);

        Assert.Equal(MeshGroupSource.Coplanarity, mesh.GroupSource);

        Assert.True(
            Groups(mesh) * 3 >= mesh.FaceCount * 2,
            $"The fixture wants a group per triangle and has {Groups(mesh)} over {mesh.FaceCount}."
        );

        UvUnwrap.Charts(mesh, new(), out var report);

        Assert.True(
            report.ChartCount < mesh.FaceCount / 10,
            $"{report.ChartCount} charts over {mesh.FaceCount} faces is the group count, not a chart count."
        );
    }

    /// <summary>The same ids, called an assignment, partition exactly as § D3 says they must.</summary>
    /// <remarks>
    ///     ⚠ <b>The pair is the point.</b> One mesh, one set of group ids, two readings: the rule is not
    ///     weakened for a mesh whose groups mean something, and is not applied to one whose groups are a
    ///     guess. Turning <see cref="UvSettings.KeepGroups" /> off would have made both cases behave like
    ///     the first, which is why it is not the fix.
    /// </remarks>
    [Fact]
    public void The_same_groups_called_an_assignment_partition_first_and_unconditionally() {
        var mesh = Faceted(16, 16);
        var guessed = UvUnwrap.Charts(mesh, new(), out var loose);

        mesh.GroupSource = MeshGroupSource.Assigned;

        var assigned = UvUnwrap.Charts(mesh, new(), out var strict);

        Assert.True(
            strict.ChartCount > loose.ChartCount * 10,
            $"An assignment has to partition: {strict.ChartCount} charts against {loose.ChartCount}."
        );

        // And every one of those charts is inside a single group, which is what "unconditionally" means.
        for (var face = 0; face < mesh.FaceCount; face++) {
            for (var other = 0; other < mesh.FaceCount; other++) {
                if (mesh.Faces[face].Group != mesh.Faces[other].Group) {
                    Assert.NotEqual(assigned[face], assigned[other]);
                }
            }
        }

        Assert.Equal(mesh.FaceCount, guessed.Count);
    }

    /// <summary>How many distinct groups a mesh's faces are in.</summary>
    static int Groups(EditMesh mesh) {
        var seen = new HashSet<int>();

        for (var face = 0; face < mesh.FaceCount; face++) {
            seen.Add(mesh.Faces[face].Group);
        }

        return seen.Count;
    }

    /// <summary>A dome as a triangle soup, roughened so that hardly any two neighbours are coplanar.</summary>
    /// <remarks>
    ///     ⚠ Built through <see cref="EditMesh.FromTriangles" /> rather than face by face, because the
    ///     defect <i>is</i> what <c>FromTriangles</c> does on the way in. The roughness is a fixed hash
    ///     of the grid indices: a fixture that differs between runs is a test that reports a different
    ///     thing every time, and a perfectly regular sphere pairs each quad's two triangles into one
    ///     group and hides half of it.
    /// </remarks>
    internal static EditMesh Faceted(int around, int up) {
        var positions = new List<Vector3>();
        var indices = new List<int>();

        for (var ring = 0; ring <= up; ring++) {
            var phi = MathF.PI * 0.5f * ring / up;

            for (var step = 0; step < around; step++) {
                var theta = MathF.Tau * step / around;
                var noise = ((ring * 73856093) ^ (step * 19349663) ^ ((ring + step) * 83492791)) & 0xFFFF;
                var rough = 1f + (0.02f * noise / 0xFFFF);

                positions.Add(
                    rough
                    * new Vector3(
                        MathF.Cos(theta) * MathF.Cos(phi),
                        MathF.Sin(phi),
                        MathF.Sin(theta) * MathF.Cos(phi)
                    )
                );
            }
        }

        for (var ring = 0; ring < up; ring++) {
            for (var step = 0; step < around; step++) {
                var next = (step + 1) % around;
                var low = ring * around;
                var high = (ring + 1) * around;

                indices.AddRange([low + step, high + step, high + next]);
                indices.AddRange([low + step, high + next, low + next]);
            }
        }

        return EditMesh.FromTriangles([.. positions], [.. indices]);
    }
}

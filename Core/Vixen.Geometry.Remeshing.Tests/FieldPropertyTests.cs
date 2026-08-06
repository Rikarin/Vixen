// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using CsCheck;
using Vixen.Geometry.Testing;
using Xunit;

namespace Vixen.Geometry.Remeshing.Tests;

/// <summary>Poincaré–Hopf over a space of closed surfaces rather than over four hand-built ones.</summary>
/// <remarks>
///     <para>
///         <b>docs/plan/41 § D5's extraction is pinned by an identity that is true of every closed
///         orientable surface</b> — the singularity indices sum to four times the Euler characteristic
///         — and <see cref="SingularityTests.The_index_sum_is_four_times_the_euler_characteristic" />
///         asserts it on four fixtures: a sphere at <c>+8</c>, a torus at <c>0</c>, a one-holed plate
///         at <c>0</c> and a two-holed one at <c>−8</c>. Poincaré–Hopf makes it a property of the
///         <i>surface</i> and not of the field, so it holds for a good field and a bad one alike, which
///         is what makes it a test of the extraction rather than of the solver. Four surfaces is four
///         points; this is a range of tessellations of three of them.
///     </para>
///     <para>
///         ⚠ <b>The precondition is that the mesh resolves the field, and it is a real restriction
///         rather than a formality.</b> The index is read off period jumps — which of the four
///         rotations of a neighbour was meant — and a quarter turn is ninety degrees, so an edge across
///         which the field genuinely turns by more than forty-five has no right answer. The field is
///         therefore solved with the features off, exactly as the fixed test does: a hard constraint
///         can force a turn of more than forty-five degrees across one edge, because that is what
///         "hard" means.
///     </para>
///     <para>
///         ⚠ <b>Which surfaces satisfy the precondition was measured rather than guessed, and the
///         answer is narrower than the fixed test suggests.</b> Over three hundred sampled
///         tessellations: sphere 62 of 62, capsule 45 of 45, torus 55 of 55 — and cylinder 44 of 45,
///         pipe 46 of 49, a rounded slab 36 of 44. Every failure was off by exactly one quarter turn,
///         and every failing family has a hard rim or crease the smoothing did not remove. Smoothing
///         the slab harder makes it <i>worse</i>, not better — at sixty rounds it holds 37 of 108 and
///         at a hundred and twenty, 20 of 88 — because over-smoothing collapses the slab toward
///         something with no shape rather than toward something round. So the generator is the three
///         families that are genuinely smooth, and the higher genus stays on
///         <see cref="SingularityTests" />' two hand-tuned plates, where it is the fixture that has
///         been made to satisfy the precondition.
///     </para>
///     <para>
///         ⚠ <b>Which is a real limit and worth stating plainly: this covers <c>χ = 2</c> and
///         <c>χ = 0</c> over a range of tessellations, and no negative characteristic at all.</b> A
///         surface of genus two that a routine can build, that is closed, and that is smooth enough to
///         resolve a cross field is not something <see cref="FieldFixtures" /> currently has — a
///         punched slab rounded by an umbrella pass was the answer for the fixed test and it does not
///         generalise. The sign of the identity is therefore still pinned by
///         <see cref="SingularityTests" /> and not here.
///     </para>
/// </remarks>
public class FieldPropertyTests {
    /// <summary>A closed surface: which family, and how finely it is divided.</summary>
    /// <param name="Kind">Sphere and capsule are <c>χ = 2</c>; a torus is <c>χ = 0</c>.</param>
    /// <param name="Sides">Divisions round it.</param>
    /// <param name="Steps">Divisions along it.</param>
    public readonly record struct SurfaceRecipe(ShapeKind Kind, int Sides, int Steps) {
        /// <inheritdoc />
        public override string ToString() =>
            string.Create(CultureInfo.InvariantCulture, $"{Kind} with {Sides} sides and {Steps} steps");
    }

    /// <summary>A sphere or a capsule at any tessellation these two ranges reach.</summary>
    static readonly Gen<SurfaceRecipe> Round = Gen.Select(
        Gen.OneOfConst(ShapeKind.Sphere, ShapeKind.Capsule),
        Gen.Int[8, 24],
        Gen.Int[3, 14],
        (kind, sides, steps) => new SurfaceRecipe(kind, sides, steps)
    );

    /// <summary>A torus, whose tube needs eight segments before it is round rather than a prism.</summary>
    /// <remarks>
    ///     ⚠ <b>The floor is measured, and it is the precondition rather than a taste.</b> Below it the
    ///     identity fails: over four hundred tessellations with the tube's segment count drawn from
    ///     three upward, 38 disagreed, every one of them at three, four or five segments — a tube of
    ///     three segments is a triangular prism, and a hundred-and-twenty-degree crease is one no cross
    ///     field can turn through in less than a quarter turn. From eight segments up, none of six
    ///     hundred disagreed. Sphere and capsule need no floor: none of six hundred each disagreed at
    ///     any tessellation in the range above, because neither has a crease anywhere.
    /// </remarks>
    static readonly Gen<SurfaceRecipe> Ring = Gen.Select(
        Gen.Const(ShapeKind.Torus),
        Gen.Int[10, 24],
        Gen.Int[8, 16],
        (kind, sides, steps) => new SurfaceRecipe(kind, sides, steps)
    );

    static readonly Gen<SurfaceRecipe> Surface = Gen.OneOf(Round, Ring);

    /// <summary>∀ closed orientable mesh: Σ singularity index = 4χ.</summary>
    /// <remarks>
    ///     ⚠ <b>The characteristic is measured off the mesh with <see cref="FieldFixtures.Euler" />
    ///     rather than assumed from the family.</b> A tessellation that came out open, pinched or
    ///     welded shut is not the genus its recipe claims, and a test that assumed otherwise would fail
    ///     somewhere confusing later instead of here. The claim asserted is between two numbers taken
    ///     from the same mesh.
    /// </remarks>
    [Fact]
    public void The_index_sum_is_four_times_the_euler_characteristic() {
        var genera = new HashSet<int>();

        Surface.Sample(
            recipe => {
                var mesh = RunawayGuard.Run($"conditioning {recipe}", () => Build(recipe));

                if (mesh.TriangleCount == 0) {
                    return;
                }

                var euler = FieldFixtures.Euler(mesh);

                genera.Add(euler);

                var settings = new RemeshSettings { Adaptivity = 0f, FeatureAngle = 180f };
                var field = RunawayGuard.Run($"solving on {recipe}", () => CrossFieldTests.Solve(mesh, settings));
                var found = RunawayGuard.Run($"extracting on {recipe}", () => SingularityPass.Extract(mesh, field));

                Assert.True(
                    found.Sum(single => single.Index) == 4 * euler,
                    $"{recipe}: {mesh.VertexCount} vertices, χ = {euler}, and the indices sum to "
                    + $"{found.Sum(single => single.Index)} against {4 * euler}."
                );

                // Every one of them is a whole quarter turn and every triangle appears once, which is
                // what makes the sum above a sum of indices rather than of anything else.
                Assert.Equal(found.Count, found.Select(single => single.Triangle).Distinct().Count());

                foreach (var single in found) {
                    Assert.InRange(single.Index, -1, 1);
                    Assert.NotEqual(0, single.Index);
                }
            },
            iter: 120,
            threads: 1
        );

        // ⚠ The guard on the guard. An identity between two numbers taken from the same mesh is
        // satisfied by every mesh of one genus for the wrong reason, so a run that only ever saw
        // spheres would prove nothing about the sign of anything.
        Assert.True(
            genera.Contains(2) && genera.Contains(0),
            $"The sample only reached characteristics {string.Join(", ", genera.Order())}."
        );
    }

    /// <summary>⚠ And the guard under that: the identity is worth asserting only where there is turning.</summary>
    /// <remarks>
    ///     A field with no singularities at all sums to zero, which is the right answer for a torus and
    ///     the wrong one for a sphere. Measured here so a solver that quietly returned nothing — the
    ///     failure <see cref="ScaleSafe" /> exists for, where every normal came back as the zero vector
    ///     at a thousandth scale — cannot pass the identity by finding nothing on a genus-one surface.
    /// </remarks>
    [Fact]
    public void A_genus_zero_surface_always_has_turning_to_find() {
        Surface.Sample(
            recipe => {
                if (recipe.Kind == ShapeKind.Torus) {
                    return;
                }

                var mesh = Build(recipe);

                if (mesh.TriangleCount == 0 || FieldFixtures.Euler(mesh) != 2) {
                    return;
                }

                var settings = new RemeshSettings { Adaptivity = 0f, FeatureAngle = 180f };
                var found = SingularityPass.Extract(mesh, CrossFieldTests.Solve(mesh, settings));

                Assert.True(found.Count >= 4, $"{recipe}: {found.Count} singularities on a sphere.");
            },
            iter: 60,
            threads: 1
        );
    }

    static ManifoldMesh Build(SurfaceRecipe recipe) =>
        FieldFixtures.Condition(
            MeshShapes.Create(
                ShapeParameters.Default(recipe.Kind) with { Sides = recipe.Sides, Steps = recipe.Steps }
            )
        );
}

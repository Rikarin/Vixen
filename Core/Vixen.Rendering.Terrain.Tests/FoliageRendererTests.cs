// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;
using Vixen.Foliage;
using Xunit;

namespace Vixen.Rendering.Terrain.Tests;

/// <summary>Cells culled as objects, instances culled within them — [docs/plan/31 § D9] and § T5.</summary>
public sealed class FoliageRendererTests {
    static FoliageType Tree =>
        FoliageType.Of("Tree") with {
            Mesh = "Meshes/pine",
            Radius = 2f,
            StartCullDistance = 180f,
            EndCullDistance = 200f
        };

    static FoliageDraw Draws(int type, params float[] distances) =>
        new(
            type,
            [.. Enumerable.Range(0, distances.Length + 1)
                .Select(level => new DrawCommand { IndexCount = (uint)(600 >> level) })],
            distances
        );

    /// <summary>A camera looking down −Z from the origin, seeing a long way.</summary>
    static BoundingFrustum Looking(float far = 1000f) =>
        new(
            Matrix4x4.LookAt(Vector3.Zero, -Vector3.UnitZ, Vector3.UnitY)
            * Matrix4x4.PerspectiveFieldOfView(MathF.PI / 2f, 1f, 0.1f, far)
        );

    static (FoliageVolume Volume, int Type) Built(FoliageType? type = null) {
        var volume = new FoliageVolume(new(32f));

        return (volume, volume.AddType(type ?? Tree));
    }

    static void Fill(FoliageVolume volume, int type, int count, float z, float spread = 30f) {
        for (var index = 0; index < count; index++) {
            volume.Add(
                type,
                new(new(((index % 16) - 8) * spread / 16f, 0f, z), Quaternion.Identity, 1f)
            );
        }
    }

    // --- Stage one: the cell ------------------------------------------------

    [Fact]
    public void A_cell_behind_the_camera_is_never_looked_inside() {
        var (volume, type) = Built();
        var renderer = new FoliageRenderer();

        Fill(volume, type, 200, z: 300f);

        renderer.Cull(volume, [Draws(type)], Looking(), Vector3.Zero);

        Assert.True(renderer.CellsConsidered > 0);
        Assert.Equal(0, renderer.CellsDrawn);
        Assert.Equal(0, renderer.InstancesConsidered);
        Assert.Equal(0, renderer.InstancesDrawn);
    }

    [Fact]
    public void A_cell_in_front_of_it_has_its_instances_tested() {
        var (volume, type) = Built();
        var renderer = new FoliageRenderer();

        Fill(volume, type, 100, z: -50f);

        Assert.True(renderer.Cull(volume, [Draws(type)], Looking(), Vector3.Zero) > 0);
        Assert.True(renderer.CellsDrawn > 0);
        Assert.Equal(100, renderer.InstancesConsidered);
    }

    [Fact]
    public void A_type_with_no_draw_template_is_skipped_entirely() {
        var (volume, type) = Built();
        var renderer = new FoliageRenderer();

        Fill(volume, type, 50, z: -50f);

        Assert.Equal(0, renderer.Cull(volume, [], Looking(), Vector3.Zero));
        Assert.Equal(0, renderer.CellsConsidered);
    }

    // --- Stage two: the instance --------------------------------------------

    /// <summary>Instances past the cull distance go, even though their cell survived.</summary>
    /// <remarks>
    ///     ⚠ <b>This is the whole reason for the second stage.</b> A cell is either entirely drawn or
    ///     entirely absent, so a 32 m cell of grass whose far half is behind a hill draws all of it.
    /// </remarks>
    [Fact]
    public void Instances_past_the_cull_distance_go_even_though_their_cell_survived() {
        var (volume, type) = Built(Tree with { StartCullDistance = 40f, EndCullDistance = 50f });
        var renderer = new FoliageRenderer();

        // One cell straddling the cull distance.
        for (var z = -30f; z > -60f; z -= 2f) {
            volume.Add(type, new(new(0f, 0f, z), Quaternion.Identity, 1f));
        }

        var drawn = renderer.Cull(volume, [Draws(type)], Looking(), Vector3.Zero);

        Assert.True(drawn > 0);
        Assert.True(drawn < renderer.InstancesConsidered, "nothing was culled inside the cell.");

        foreach (var transform in renderer.Transforms) {
            Assert.True(MathF.Abs(transform.M43) <= 50f, $"one at z = {transform.M43} survived.");
        }
    }

    /// <summary>Instances bin into their own level, which a per-object LOD cannot express.</summary>
    /// <remarks>
    ///     ⚠ <b>The divergence from <c>LodRenderFeature</c>, stated as a test.</b> That feature's
    ///     level is per render object; here four thousand trees in one cell are at level 1 and six
    ///     hundred are at level 2, and one cell draws three times.
    /// </remarks>
    [Fact]
    public void One_cells_instances_bin_into_several_levels() {
        var (volume, type) = Built(Tree with { StartCullDistance = 500f, EndCullDistance = 600f });
        var renderer = new FoliageRenderer();

        for (var z = -5f; z > -32f; z -= 1f) {
            volume.Add(type, new(new(0f, 0f, z), Quaternion.Identity, 1f));
        }

        renderer.Cull(volume, [Draws(type, 12f, 24f)], Looking(), Vector3.Zero);

        var batch = Assert.Single(renderer.Batches);

        Assert.Equal(3, batch.Commands.Length);
        Assert.All(batch.Commands, command => Assert.True(command.InstanceCount > 0));

        // Every level's run is inside this cell's slice of the frame buffer, and they tile it.
        var total = batch.Commands.Sum(command => (int)command.InstanceCount);

        Assert.Equal(batch.Count, total);
    }

    [Fact]
    public void A_level_with_no_survivors_still_gets_a_command() {
        var (volume, type) = Built(Tree with { StartCullDistance = 500f, EndCullDistance = 600f });
        var renderer = new FoliageRenderer();

        // All of them near, so the far level is empty.
        for (var z = -2f; z > -8f; z -= 1f) {
            volume.Add(type, new(new(0f, 0f, z), Quaternion.Identity, 1f));
        }

        renderer.Cull(volume, [Draws(type, 100f, 200f)], Looking(), Vector3.Zero);

        var batch = Assert.Single(renderer.Batches);

        Assert.Equal(3, batch.Commands.Length);
        Assert.True(batch.Commands[0].InstanceCount > 0);
        Assert.Equal(0u, batch.Commands[1].InstanceCount);
        Assert.Equal(0u, batch.Commands[2].InstanceCount);
    }

    /// <summary>Each level draws its own mesh, which is what the templates are for.</summary>
    [Fact]
    public void A_levels_command_carries_that_levels_mesh() {
        var (volume, type) = Built(Tree with { StartCullDistance = 500f, EndCullDistance = 600f });
        var renderer = new FoliageRenderer();

        for (var z = -5f; z > -32f; z -= 1f) {
            volume.Add(type, new(new(0f, 0f, z), Quaternion.Identity, 1f));
        }

        renderer.Cull(volume, [Draws(type, 12f, 24f)], Looking(), Vector3.Zero);

        var batch = Assert.Single(renderer.Batches);

        Assert.Equal(600u, batch.Commands[0].IndexCount);
        Assert.Equal(300u, batch.Commands[1].IndexCount);
        Assert.Equal(150u, batch.Commands[2].IndexCount);
    }

    // --- What the frame is handed -------------------------------------------

    /// <summary>Every batch's run is inside the frame's one buffer, and they do not overlap.</summary>
    [Fact]
    public void The_batches_tile_the_frames_transform_buffer() {
        var (volume, type) = Built();
        var renderer = new FoliageRenderer();

        for (var cell = 0; cell < 6; cell++) {
            Fill(volume, type, 40, z: -40f - (cell * 32f));
        }

        var drawn = renderer.Cull(volume, [Draws(type)], Looking(), Vector3.Zero);

        Assert.Equal(drawn, renderer.Transforms.Length);
        Assert.Equal(drawn, renderer.Parameters.Length);

        var at = 0;

        foreach (var batch in renderer.Batches.OrderBy(batch => batch.First)) {
            Assert.Equal(at, batch.First);
            at += batch.Count;
        }

        Assert.Equal(drawn, at);
    }

    /// <summary>The cross-fade weight rides in the per-instance parameters.</summary>
    /// <remarks>
    ///     ⚠ <b>[§ B3]'s <c>float4</c>, read by the existing dithered discard unchanged.</b> Deciding
    ///     the fade anywhere else would be measuring the distance a second time — and the two would
    ///     disagree by a frame, which pops.
    /// </remarks>
    [Fact]
    public void An_instance_near_the_cull_distance_fades_rather_than_vanishing() {
        var (volume, type) = Built(Tree with { StartCullDistance = 40f, EndCullDistance = 60f });
        var renderer = new FoliageRenderer();

        volume.Add(type, new(new(0f, 0f, -10f), Quaternion.Identity, 1f));
        volume.Add(type, new(new(0f, 0f, -50f), Quaternion.Identity, 1f));

        renderer.Cull(volume, [Draws(type)], Looking(), Vector3.Zero);

        var fades = renderer.Parameters.ToArray().Select(parameters => parameters.Fade).OrderBy(fade => fade).ToArray();

        Assert.Equal(2, fades.Length);
        Assert.InRange(fades[0], 0.1f, 0.9f);
        Assert.Equal(1f, fades[1], 3);
    }

    /// <summary>A density scalar drops the same instances every frame.</summary>
    /// <remarks>
    ///     ⚠ A density slider that reshuffled would make a forest shimmer. The choice is a hash of the
    ///     position, so it is stable under any traversal order.
    /// </remarks>
    [Fact]
    public void A_density_scalar_drops_the_same_instances_each_time() {
        var (volume, type) = Built();
        var renderer = new FoliageRenderer();

        Fill(volume, type, 200, z: -50f);

        var first = renderer.Cull(volume, [Draws(type)], Looking(), Vector3.Zero, densityScale: 0.5f);
        var kept = renderer.Transforms.ToArray();

        var second = renderer.Cull(volume, [Draws(type)], Looking(), Vector3.Zero, densityScale: 0.5f);

        Assert.Equal(first, second);
        Assert.Equal(kept, renderer.Transforms.ToArray());
        Assert.True(first < 200, "the density scalar dropped nothing.");
    }

    [Fact]
    public void Nothing_in_the_volume_is_no_draws_rather_than_an_empty_one() {
        var (volume, type) = Built();
        var renderer = new FoliageRenderer();

        Assert.Equal(0, renderer.Cull(volume, [Draws(type)], Looking(), Vector3.Zero));
        Assert.Empty(renderer.Batches);
        Assert.Equal(0, renderer.Draws);
    }

    /// <summary>Fifty thousand trees, culled per instance and LOD-binned.</summary>
    /// <remarks>
    ///     § T5's exit criterion, in the part that is arithmetic. What it asserts is that the two
    ///     stages both did something: most cells never had their instances looked at, and of the ones
    ///     that did, only some instances survived.
    /// </remarks>
    [Fact]
    public void Fifty_thousand_trees_are_culled_per_instance_and_binned_per_level() {
        var (volume, type) = Built(Tree with { Radius = 1f, StartCullDistance = 280f, EndCullDistance = 300f });
        var renderer = new FoliageRenderer();
        var random = new Random(0x5EED);

        for (var index = 0; index < 50_000; index++) {
            volume.Add(
                type,
                new(
                    new(
                        ((float)random.NextDouble() - 0.5f) * 2_000f,
                        0f,
                        ((float)random.NextDouble() - 0.5f) * 2_000f
                    ),
                    Quaternion.Identity,
                    1f
                )
            );
        }

        Assert.Equal(50_000, volume.InstanceCount);

        var drawn = renderer.Cull(volume, [Draws(type, 60f, 150f)], Looking(), Vector3.Zero);

        // Stage one did most of the work: a 2 km square seen from the middle looking one way.
        Assert.True(
            renderer.CellsDrawn < renderer.CellsConsidered / 2,
            $"{renderer.CellsDrawn} of {renderer.CellsConsidered} cells survived the frustum."
        );

        // Stage two did the rest: the cells that survived hold far more than the 300 m range keeps.
        Assert.True(
            drawn < renderer.InstancesConsidered,
            $"{drawn} of {renderer.InstancesConsidered} instances survived, which is all of them."
        );

        Assert.True(drawn > 0);
        Assert.Equal(drawn, renderer.Transforms.Length);

        // A cell draws three times rather than once, one command per level.
        Assert.Equal(renderer.Batches.Count * 3, renderer.Draws);

        // ⚠ And *fewer* cells draw than survived the frustum, which is a third rejection worth
        // naming: a cell can be in view and still have nothing left after the per-instance cull —
        // every one of its trees past the cull distance. Issuing three empty commands for it would
        // be the cost of the batch for none of the benefit.
        Assert.True(
            renderer.Batches.Count < renderer.CellsDrawn,
            $"{renderer.Batches.Count} of {renderer.CellsDrawn} visible cells drew, which is all of them."
        );
    }
}

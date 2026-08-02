// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;
using Vixen.Foliage;
using Xunit;

namespace Vixen.Rendering.Terrain.Tests;

/// <summary>Cells scattered as they come into range, blades culled every frame — [docs/plan/31 § T6].</summary>
public sealed class GrassRendererTests {
    /// <summary>A surface that answers for everything, with whatever weight a test needs.</summary>
    sealed class Meadow(Func<Vector2, float>? weight = null) : IFoliageSurface {
        public int Samples { get; private set; }

        public FoliageSurface SampleAt(Vector2 position, string layer) {
            Samples++;

            return new(
                new(position.X, 0f, position.Y),
                Vector3.UnitY,
                string.IsNullOrEmpty(layer) ? 1f : weight?.Invoke(position) ?? 1f,
                true
            );
        }
    }

    static GrassType Grass =>
        GrassType.Of("Meadow") with {
            Mesh = "Meshes/grass",
            Layer = "Grass",
            Density = 1f,
            StartCullDistance = 30f,
            EndCullDistance = 40f
        };

    static GrassDraw Draw(GrassType type, params float[] distances) =>
        new(
            type,
            [.. Enumerable.Range(0, distances.Length + 1)
                .Select(level => new DrawCommand { IndexCount = (uint)(24 >> level) })],
            distances
        );

    /// <summary>A box a thousand metres on a side, centred on the origin: a view that sees it all.</summary>
    /// <remarks>
    ///     ⚠ <b>Not <c>Matrix4x4.Identity</c>, which is a frustum one metre wide.</b> The clip cube is
    ///     ±1, so an identity view-projection keeps only what is within a metre of the origin — a test
    ///     written with it passes for the wrong reason and asserts nothing about the cull.
    /// </remarks>
    static BoundingFrustum Everything(float reach = 500f) =>
        new(Matrix4x4.Compose(new(1f / reach), Quaternion.Identity, Vector3.Zero));

    /// <summary>A camera at the origin looking down −Z.</summary>
    static BoundingFrustum Looking(float far = 500f) =>
        new(
            Matrix4x4.LookAt(Vector3.Zero, -Vector3.UnitZ, Vector3.UnitY)
            * Matrix4x4.PerspectiveFieldOfView(MathF.PI / 2f, 1f, 0.1f, far)
        );

    [Fact]
    public void ACellComingIntoRangeIsScatteredOnce() {
        var renderer = new GrassRenderer(new(32f));
        var surface = new Meadow();
        var draws = new[] { Draw(Grass) };

        var first = renderer.Scatter(draws, surface, Vector3.Zero);

        Assert.True(first > 0);

        var probes = surface.Samples;
        var second = renderer.Scatter(draws, surface, Vector3.Zero);

        Assert.Equal(first, second);
        Assert.Equal(probes, surface.Samples);
    }

    /// <summary>The scatter is on entry and the cull is per frame, and the two are not the same pass.</summary>
    /// <remarks>
    ///     ⚠ <b>The failure this catches is scattering inside <c>Cull</c>.</b> That draws correctly
    ///     and probes the surface for every blade of every cell every frame — the exact cost the ring
    ///     exists to pay once, moved back into the frame.
    /// </remarks>
    [Fact]
    public void CullingProbesNothing() {
        var renderer = new GrassRenderer(new(32f));
        var surface = new Meadow();
        var draws = new[] { Draw(Grass) };

        renderer.Scatter(draws, surface, Vector3.Zero);

        var probes = surface.Samples;

        renderer.Cull(draws, Looking(), Vector3.Zero);
        renderer.Cull(draws, Looking(), Vector3.Zero);

        Assert.Equal(probes, surface.Samples);
        Assert.True(renderer.BladesDrawn > 0);
    }

    /// <summary>Grass follows the layer it is bound to, through the renderer as well.</summary>
    [Fact]
    public void AFieldGrowsOnlyWhereItsLayerIsPainted() {
        var renderer = new GrassRenderer(new(32f));
        var surface = new Meadow(at => at.X > 0f ? 1f : 0f);

        renderer.Scatter([Draw(Grass)], surface, Vector3.Zero);
        renderer.Cull([Draw(Grass)], Everything(), Vector3.Zero);

        Assert.True(renderer.BladesResident > 0);

        foreach (var transform in renderer.Transforms) {
            Assert.True(transform.M41 > 0f, "a blade grew where the layer is not painted.");
        }
    }

    /// <summary>Appearing and disappearing with range, with a fade rather than a pop.</summary>
    /// <remarks>
    ///     [docs/plan/31 § T6]'s second exit criterion. A blade at the far edge of the band is
    ///     drawn at a fade near zero, so the frame in which the cull drops it removes something that
    ///     was already invisible.
    /// </remarks>
    [Fact]
    public void BladesFadeOutBeforeTheyAreCulled() {
        var renderer = new GrassRenderer(new(32f));

        renderer.Scatter([Draw(Grass)], new Meadow(), Vector3.Zero);
        renderer.Cull([Draw(Grass)], Everything(), Vector3.Zero);

        Assert.True(renderer.BladesDrawn > 0);

        var fades = renderer.Parameters.ToArray().Select(parameter => parameter.Fade).ToArray();

        Assert.All(fades, fade => Assert.InRange(fade, 0f, 1f));
        Assert.Contains(fades, fade => fade < 0.2f);
        Assert.Contains(fades, fade => fade > 0.9f);
    }

    [Fact]
    public void ABladesTintAndPhaseSurviveTheCull() {
        var renderer = new GrassRenderer(new(32f));

        renderer.Scatter([Draw(Grass)], new Meadow(), Vector3.Zero);
        renderer.Cull([Draw(Grass)], Everything(), Vector3.Zero);

        var parameters = renderer.Parameters.ToArray();

        Assert.All(parameters, parameter => Assert.InRange(parameter.WindPhase, 0f, MathF.Tau));
        Assert.True(parameters.Select(parameter => parameter.WindPhase).Distinct().Count() > 8);
    }

    [Fact]
    public void ACellLeavingRangeGivesItsBladesBack() {
        var renderer = new GrassRenderer(new(32f));
        var draws = new[] { Draw(Grass) };

        renderer.Scatter(draws, new Meadow(), Vector3.Zero);

        Assert.True(renderer.BladesResident > 0);

        var held = renderer.BladesResident;

        renderer.Scatter(draws, new Meadow(), new(4000f, 0f, 4000f));

        Assert.True(renderer.BladesDropped > 0, "moving four kilometres dropped nothing.");
        Assert.False(renderer.Residency.TryGetSlot(new(0, 0), out _));
        Assert.Equal(held - renderer.BladesDropped + renderer.BladesScattered, renderer.BladesResident);
    }

    /// <summary>And coming back produces the identical field.</summary>
    [Fact]
    public void ACellComingBackScattersTheGrassItHad() {
        var renderer = new GrassRenderer(new(32f));
        var draws = new[] { Draw(Grass) };

        renderer.Scatter(draws, new Meadow(), Vector3.Zero);
        renderer.Cull(draws, Everything(), Vector3.Zero);

        var before = renderer.Transforms.ToArray();

        renderer.Scatter(draws, new Meadow(), new(4000f, 0f, 4000f));
        renderer.Scatter(draws, new Meadow(), Vector3.Zero);
        renderer.Cull(draws, Everything(), Vector3.Zero);

        var after = renderer.Transforms.ToArray();

        Assert.Equal(before.Length, after.Length);
        Assert.Equal([.. before.OrderBy(m => m.M41).ThenBy(m => m.M43)], [.. after.OrderBy(m => m.M41).ThenBy(m => m.M43)]);
    }

    /// <summary>A near field does not pay for a far one's range.</summary>
    /// <remarks>
    ///     ⚠ <b>The ring is held open to the largest field's cull distance</b>, so a short-range field
    ///     scattering into every resident cell would place blades it is about to cull — the long
    ///     field's cost charged to the short one.
    /// </remarks>
    [Fact]
    public void AShortFieldDoesNotScatterIntoTheLongFieldsCells() {
        var renderer = new GrassRenderer(new(32f));

        var near = Grass with { Name = "Near", StartCullDistance = 8f, EndCullDistance = 12f };
        var far = Grass with { Name = "Far", StartCullDistance = 100f, EndCullDistance = 120f };

        renderer.Scatter([Draw(near), Draw(far)], new Meadow(), Vector3.Zero);
        renderer.Cull([Draw(near), Draw(far)], Everything(), Vector3.Zero);

        var nearCells = renderer.Batches.Count(batch => batch.Field == 0);
        var farCells = renderer.Batches.Count(batch => batch.Field == 1);

        Assert.True(nearCells > 0);
        Assert.True(farCells > nearCells, $"the near field occupied {nearCells} cells and the far one {farCells}.");
    }

    /// <summary>The density scalar thins the drawn field and does not move it.</summary>
    [Fact]
    public void TheDensityScalarThinsTheField() {
        var renderer = new GrassRenderer(new(32f));
        var draws = new[] { Draw(Grass) };

        renderer.Scatter(draws, new Meadow(), Vector3.Zero);
        renderer.Cull(draws, Everything(), Vector3.Zero);

        var full = renderer.BladesDrawn;

        renderer.Cull(draws, Everything(), Vector3.Zero, 0.5f);

        Assert.True(renderer.BladesDrawn < full);
        Assert.True(renderer.BladesDrawn > 0);
    }

    /// <summary>Levels are binned per blade, and a level with no survivors still gets a command.</summary>
    [Fact]
    public void EveryLevelGetsACommandWhetherOrNotItDrew() {
        var renderer = new GrassRenderer(new(32f));
        var draws = new[] { Draw(Grass, 10f, 20f) };

        renderer.Scatter(draws, new Meadow(), Vector3.Zero);
        renderer.Cull(draws, Everything(), Vector3.Zero);

        Assert.NotEmpty(renderer.Batches);
        Assert.All(renderer.Batches, batch => Assert.Equal(3, batch.Commands.Length));
        Assert.Equal(renderer.Batches.Count * 3, renderer.Draws);

        var counted = renderer.Batches.Sum(batch => batch.Commands.Sum(command => (int)command.InstanceCount));

        Assert.Equal(renderer.BladesDrawn, counted);
    }

    /// <summary>The frustum removes what is behind the camera.</summary>
    [Fact]
    public void CellsBehindTheCameraAreNotDrawn() {
        var renderer = new GrassRenderer(new(32f));
        var draws = new[] { Draw(Grass) };

        renderer.Scatter(draws, new Meadow(), Vector3.Zero);
        renderer.Cull(draws, Looking(), Vector3.Zero);

        Assert.True(renderer.CellsDrawn < renderer.CellsConsidered);
        Assert.True(renderer.BladesDrawn > 0);
        Assert.True(renderer.BladesDrawn < renderer.BladesConsidered);
    }

    [Fact]
    public void MoreFieldsThanTheRingHoldsIsRefusedRatherThanTruncated() {
        var renderer = new GrassRenderer(new(32f), fields: 1);

        var thrown = Assert.Throws<ArgumentException>(
            () => renderer.Scatter([Draw(Grass), Draw(Grass)], new Meadow(), Vector3.Zero)
        );

        Assert.Contains("room for 1 fields", thrown.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ResettingDropsEverything() {
        var renderer = new GrassRenderer(new(32f));

        renderer.Scatter([Draw(Grass)], new Meadow(), Vector3.Zero);
        renderer.Reset();

        Assert.Equal(0, renderer.BladesResident);
        Assert.Equal(0, renderer.Residency.Count);

        renderer.Cull([Draw(Grass)], Everything(), Vector3.Zero);

        Assert.Equal(0, renderer.BladesDrawn);
        Assert.Empty(renderer.Batches);
    }
}

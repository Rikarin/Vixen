// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;
using Vixen.Rendering;
using Vixen.Rendering.Features;
using Xunit;

namespace Vixen.Rendering.Tests;

/// <summary>
///     The per-instance cull — [docs/plan/31 § B2] and [§ D9].
/// </summary>
public sealed class InstanceCullingTests {
    /// <summary>A wide frustum looking down +Z from the origin.</summary>
    static BoundingFrustum Forward() {
        var view = Matrix4x4.LookAt(Vector3.Zero, new(0f, 0f, 1f), new(0f, 1f, 0f));
        var projection = Matrix4x4.PerspectiveFieldOfView(MathF.PI / 2f, 1f, 0.1f, 10_000f);
        return new(view * projection);
    }

    static InstanceCullSettings Settings(float end = float.MaxValue, float start = float.MaxValue) =>
        InstanceCullSettings.Everything(Forward(), Vector3.Zero) with {
            StartCullDistance = start, EndCullDistance = end
        };

    /// <summary>Instances marching away down +Z, one per metre from `first`.</summary>
    static InstanceBounds[] Line(int count, float first = 1f, float radius = 0.5f) =>
        [.. Enumerable.Range(0, count).Select(i => new InstanceBounds(new(0f, 0f, first + i), radius))];

    [Fact]
    public void EverythingInFrontSurvivesAWideOpenCull() {
        var culler = new InstanceCuller();
        var instances = Line(20);

        Assert.Equal(20, culler.Cull(instances, [], Settings(), []));
        Assert.Equal(1, culler.LevelCount);
        Assert.Equal([.. Enumerable.Range(0, 20).Select(i => (uint)i)], culler.Survivors.ToArray());
    }

    [Fact]
    public void InstancesBehindTheViewAreDropped() {
        var culler = new InstanceCuller();

        InstanceBounds[] instances = [
            new(new(0f, 0f, 10f), 0.5f),
            new(new(0f, 0f, -10f), 0.5f),
            new(new(0f, 0f, 20f), 0.5f)
        ];

        Assert.Equal(2, culler.Cull(instances, [], Settings(), []));
        Assert.Equal([0u, 2u], culler.Survivors.ToArray());
    }

    [Fact]
    public void TheEndCullDistanceIsMeasuredToTheNearEdgeNotTheCentre() {
        // An instance whose centre is just past the limit but whose canopy is not stays. Measuring
        // to the centre makes a tree blink out while half of it is still inside the range.
        var culler = new InstanceCuller();

        InstanceBounds[] instances = [
            new(new(0f, 0f, 99f), 0.5f),
            new(new(0f, 0f, 100.4f), 1f),
            new(new(0f, 0f, 120f), 1f)
        ];

        Assert.Equal(2, culler.Cull(instances, [], Settings(end: 100f), []));
        Assert.Equal([0u, 1u], culler.Survivors.ToArray());
    }

    [Fact]
    public void AZeroedSettingsCullsEverythingRatherThanNothing() {
        // The zero-value trap, asserted: a forgotten EndCullDistance must not read as "no limit".
        var culler = new InstanceCuller();
        Assert.Equal(0, culler.Cull(Line(20), [], default, []));
    }

    // --- Levels -------------------------------------------------------------

    [Fact]
    public void SurvivorsAreBinnedByLevelAndEachRunIsContiguousAndAscending() {
        var culler = new InstanceCuller();

        // Instances at 1…20 m; level 0 out to 5, level 1 out to 12, level 2 beyond.
        var instances = Line(20);

        Assert.Equal(20, culler.Cull(instances, [], Settings(), [5f, 12f]));
        Assert.Equal(3, culler.LevelCount);

        var runs = culler.Runs.ToArray();

        Assert.Equal(new(0, 4), runs[0]);
        Assert.Equal(new(4, 7), runs[1]);
        Assert.Equal(new(11, 9), runs[2]);

        var survivors = culler.Survivors.ToArray();

        Assert.Equal(20, survivors.Length);
        Assert.Equal(survivors.Length, survivors.Distinct().Count());

        foreach (var run in runs) {
            var slice = survivors[run.First..(run.First + run.Count)];
            Assert.Equal(slice.OrderBy(index => index), slice);
        }
    }

    [Fact]
    public void ALevelWithNoSurvivorsIsAnEmptyRunRatherThanAMissingOne() {
        // So the run at slot N is always level N's, which is what lets a caller bind level N's mesh
        // by index instead of reading back which levels happened to survive.
        var culler = new InstanceCuller();

        culler.Cull(Line(3, first: 1f), [], Settings(), [100f, 200f]);

        Assert.Equal(3, culler.LevelCount);
        Assert.Equal(3, culler.Runs[0].Count);
        Assert.Equal(0, culler.Runs[1].Count);
        Assert.Equal(0, culler.Runs[2].Count);
    }

    [Fact]
    public void ALevelBoundaryIsInclusiveOfTheFartherLevel() {
        var culler = new InstanceCuller();

        // Exactly on the boundary belongs to the coarser level, and the rule is stated so the two
        // sides of a seam test can agree about it.
        InstanceBounds[] instances = [new(new(0f, 0f, 10f), 0f)];

        culler.Cull(instances, [], Settings(), [10f]);

        Assert.Equal(0, culler.Runs[0].Count);
        Assert.Equal(1, culler.Runs[1].Count);
    }

    [Fact]
    public void DistancesThatDoNotAscendAreRefused() {
        var culler = new InstanceCuller();
        Assert.Throws<ArgumentException>(() => culler.Cull(Line(4), [], Settings(), [20f, 10f]));
    }

    [Fact]
    public void ParametersMustMatchTheInstancesOrBeAbsent() {
        var culler = new InstanceCuller();

        Assert.Throws<ArgumentException>(
            () => culler.Cull(Line(4), new InstanceParameters[3], Settings(), [])
        );
    }

    // --- Parameters and fading ----------------------------------------------

    [Fact]
    public void AuthoredParametersFollowTheirInstanceThroughCompaction() {
        var culler = new InstanceCuller();

        InstanceBounds[] instances = [
            new(new(0f, 0f, 10f), 0.5f),
            new(new(0f, 0f, -10f), 0.5f),
            new(new(0f, 0f, 20f), 0.5f)
        ];

        var authored = new InstanceParameters[3];

        for (var index = 0; index < 3; index++) {
            authored[index] = InstanceParameters.Neutral with { Tint = index, WindPhase = index * 10f };
        }

        culler.Cull(instances, authored, Settings(), []);

        // The middle instance was culled, so the survivors' parameters must be 0 and 2 — not 0 and 1.
        Assert.Equal([0f, 2f], culler.Parameters.ToArray().Select(p => p.Tint));
        Assert.Equal([0f, 20f], culler.Parameters.ToArray().Select(p => p.WindPhase));
    }

    [Fact]
    public void WithNoAuthoredParametersEverySurvivorGetsNeutralOnes() {
        var culler = new InstanceCuller();

        culler.Cull(Line(5), [], Settings(), []);

        Assert.All(
            culler.Parameters.ToArray(),
            parameter => {
                Assert.Equal(1f, parameter.Scale);
                Assert.Equal(1f, parameter.Fade);
            }
        );
    }

    [Fact]
    public void FadingRampsFromOneAtTheStartDistanceToZeroAtTheEnd() {
        var culler = new InstanceCuller();

        InstanceBounds[] instances = [
            new(new(0f, 0f, 50f), 0f),
            new(new(0f, 0f, 80f), 0f),
            new(new(0f, 0f, 90f), 0f)
        ];

        var settings = Settings(end: 100f, start: 80f) with { Fade = true };

        culler.Cull(instances, [], settings, []);

        var fades = culler.Parameters.ToArray().Select(p => p.Fade).ToArray();

        Assert.Equal(1f, fades[0], 4);
        Assert.Equal(1f, fades[1], 4);
        Assert.Equal(0.5f, fades[2], 4);
    }

    [Fact]
    public void WithoutFadingEverySurvivorIsFullyPresent() {
        var culler = new InstanceCuller();

        culler.Cull(Line(5, first: 90f), [], Settings(end: 100f, start: 80f), []);

        Assert.All(culler.Parameters.ToArray(), parameter => Assert.Equal(1f, parameter.Fade));
    }

    // --- Density ------------------------------------------------------------

    [Fact]
    public void DensityThinsTheFieldRoughlyInProportion() {
        var culler = new InstanceCuller();

        var instances = Enumerable.Range(0, 4000)
            .Select(i => new InstanceBounds(new(i % 40 * 0.5f, 0f, 10f + (i / 40 * 0.5f)), 0.1f))
            .ToArray();

        var full = culler.Cull(instances, [], Settings(), []);
        var half = culler.Cull(instances, [], Settings() with { DensityScale = 0.5f }, []);
        var none = culler.Cull(instances, [], Settings() with { DensityScale = 0f }, []);

        Assert.Equal(0, none);
        Assert.InRange(half, (int)(full * 0.45f), (int)(full * 0.55f));
    }

    /// <summary>
    ///     Lowering the density thins the field rather than rearranging it.
    /// </summary>
    /// <remarks>
    ///     The property that stops a quality slider from looking like a different level: every
    ///     instance a lower scale keeps must also be kept by a higher one. A prefix or a stride would
    ///     satisfy the count assertion above and fail this one.
    /// </remarks>
    [Fact]
    public void ASmallerDensityKeepsASubsetOfWhatALargerOneKept() {
        var culler = new InstanceCuller();

        var instances = Enumerable.Range(0, 2000)
            .Select(i => new InstanceBounds(new(i % 50 * 0.4f, 0f, 10f + (i / 50 * 0.4f)), 0.1f))
            .ToArray();

        uint[] At(float density) {
            culler.Cull(instances, [], Settings() with { DensityScale = density }, []);
            return culler.Survivors.ToArray();
        }

        var sparse = At(0.25f);
        var medium = At(0.5f).ToHashSet();
        var dense = At(0.9f).ToHashSet();

        Assert.NotEmpty(sparse);
        Assert.All(sparse, index => Assert.Contains(index, medium));
        Assert.All(medium, index => Assert.Contains(index, dense));
    }

    [Fact]
    public void DensityIsHashedFromPositionSoAReorderedCellThinsIdentically() {
        // Streaming a cell back in, or re-scattering it, must not change which instances survive.
        var culler = new InstanceCuller();

        var instances = Enumerable.Range(0, 500)
            .Select(i => new InstanceBounds(new(i % 25 * 0.7f, 0f, 10f + (i / 25 * 0.7f)), 0.1f))
            .ToArray();

        var settings = Settings() with { DensityScale = 0.4f };

        culler.Cull(instances, [], settings, []);
        var forwards = culler.Survivors.ToArray().Select(index => instances[index].Centre).ToHashSet();

        var reversed = instances.Reverse().ToArray();
        culler.Cull(reversed, [], settings, []);
        var backwards = culler.Survivors.ToArray().Select(index => reversed[index].Centre).ToHashSet();

        Assert.Equal(forwards, backwards);
    }

    // --- Draw commands ------------------------------------------------------

    [Fact]
    public void EachLevelGetsACommandWithItsOwnRunAndTheBatchsBaseInstance() {
        var culler = new InstanceCuller();

        culler.Cull(Line(20), [], Settings(), [5f, 12f]);

        DrawCommand[] templates = [
            new() { IndexCount = 900, FirstIndex = 0, VertexOffset = 0 },
            new() { IndexCount = 300, FirstIndex = 900, VertexOffset = 0 },
            new() { IndexCount = 60, FirstIndex = 1200, VertexOffset = 0 }
        ];

        var commands = new DrawCommand[3];
        culler.FillCommands(templates, 5000u, commands);

        Assert.Equal(900u, commands[0].IndexCount);
        Assert.Equal(4u, commands[0].InstanceCount);
        Assert.Equal(5000u, commands[0].FirstInstance);

        Assert.Equal(7u, commands[1].InstanceCount);
        Assert.Equal(5004u, commands[1].FirstInstance);

        Assert.Equal(60u, commands[2].IndexCount);
        Assert.Equal(9u, commands[2].InstanceCount);
        Assert.Equal(5011u, commands[2].FirstInstance);
    }

    [Fact]
    public void AnEmptyLevelGetsAZeroInstanceCommandWhichDrawsNothing() {
        var culler = new InstanceCuller();

        culler.Cull(Line(3), [], Settings(), [100f]);

        var commands = new DrawCommand[2];
        culler.FillCommands([new() { IndexCount = 900 }, new() { IndexCount = 300 }], 0u, commands);

        Assert.Equal(3u, commands[0].InstanceCount);
        Assert.Equal(0u, commands[1].InstanceCount);
        Assert.Equal(300u, commands[1].IndexCount);
    }

    [Fact]
    public void TooFewTemplatesOrTooLittleRoomIsRefused() {
        var culler = new InstanceCuller();
        culler.Cull(Line(3), [], Settings(), [10f]);

        Assert.Throws<ArgumentException>(() => culler.FillCommands([default], 0u, new DrawCommand[2]));
        Assert.Throws<ArgumentException>(() => culler.FillCommands([default, default], 0u, new DrawCommand[1]));
    }

    // --- Reuse --------------------------------------------------------------

    [Fact]
    public void ACullerIsReusableAndDoesNotLeakTheLastFramesAnswer() {
        var culler = new InstanceCuller();

        culler.Cull(Line(50), [], Settings(), [5f, 10f]);
        Assert.Equal(50, culler.SurvivorCount);

        culler.Cull(Line(3), [], Settings(), []);

        Assert.Equal(3, culler.SurvivorCount);
        Assert.Equal(1, culler.LevelCount);
        Assert.Equal(3, culler.Survivors.Length);
        Assert.Equal(3, culler.Parameters.Length);
        Assert.Single(culler.Runs.ToArray());

        culler.Cull([], [], Settings(), []);
        Assert.Equal(0, culler.SurvivorCount);
        Assert.Empty(culler.Survivors.ToArray());
    }

    [Fact]
    public void TheRunsAlwaysPartitionTheSurvivorsExactly() {
        var culler = new InstanceCuller();

        foreach (var count in new[] { 0, 1, 7, 64, 513 }) {
            culler.Cull(Line(count), [], Settings(end: 300f), [5f, 12f, 40f]);

            var total = 0;
            var expected = 0;

            foreach (var run in culler.Runs) {
                Assert.Equal(expected, run.First);
                total += run.Count;
                expected += run.Count;
            }

            Assert.Equal(culler.SurvivorCount, total);
        }
    }
}

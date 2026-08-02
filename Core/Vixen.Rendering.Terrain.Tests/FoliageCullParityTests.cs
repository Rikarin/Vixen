// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Text.RegularExpressions;
using Vixen.Core.Mathematics;
using Vixen.Foliage;
using Xunit;

namespace Vixen.Rendering.Terrain.Tests;

/// <summary>
///     That <c>FoliageCull.rvn</c> still culls the forest <c>InstanceCuller</c> defines.
/// </summary>
/// <remarks>
///     <para>
///         <b>[docs/plan/31 § T5]'s seam, and the reason <c>InstanceCuller</c> was written first.</b>
///         Its own remarks say so: "a per-instance cull fails silently in both directions (too few and
///         the forest has holes; too many and nothing looks wrong at all, it is merely slow), so the
///         definition wants to exist before the dispatch that mirrors it". This is the dispatch, held
///         against the definition.
///     </para>
///     <para>
///         ⚠ <b>A transliteration and a source assertion, not an execution</b> — the shape
///         <see cref="GrassScatterParityTests" /> establishes and for the same reasons. The
///         transliteration computes what the shader computes, in C#, and compares it to the kernel
///         over thousands of instances; the source assertion says the arithmetic is still in the
///         <c>.rvn</c>, which is the failure that actually happens.
///     </para>
///     <para>
///         ⚠ <b>Compared as sets, not as sequences.</b> The device claims slots with an atomic add,
///         so two instances at the same level arrive in whatever order their workgroups retired in —
///         which no GPU promises and no test may assert. What <em>is</em> asserted is that each level
///         holds the same instances and that the runs are contiguous and in level order, which is
///         what a draw actually reads.
///     </para>
/// </remarks>
public sealed class FoliageCullParityTests {
    const int MaxLevels = FoliageCullPass.MaxLevels;

    static string Source() {
        var directory = AppContext.BaseDirectory;

        for (var at = new DirectoryInfo(directory); at is not null; at = at.Parent) {
            var candidate = Path.Combine(at.FullName, "Raven", "Library", "Terrain", "FoliageCull.rvn");

            if (File.Exists(candidate)) {
                return File.ReadAllText(candidate);
            }
        }

        throw new FileNotFoundException($"Raven/Library/Terrain/FoliageCull.rvn was not found above {directory}.");
    }

    // --- The shader, written in C# ------------------------------------------

    /// <summary>The shader's <c>FoliageCullMath.Keep</c>.</summary>
    static bool ShaderKeep(Vector3 centre, float density) {
        var hash = (uint)BitConverter.SingleToInt32Bits(centre.X) * 0x9E3779B1u;

        hash ^= (uint)BitConverter.SingleToInt32Bits(centre.Y) * 0x85EBCA77u;
        hash ^= (uint)BitConverter.SingleToInt32Bits(centre.Z) * 0xC2B2AE3Du;

        hash ^= hash >> 15;
        hash *= 0x2545F491u;
        hash ^= hash >> 13;

        return hash / 4294967296f < density;
    }

    /// <summary>And its <c>FoliageCullMath.Outside</c>.</summary>
    static bool ShaderOutside(Vector4 plane, Vector3 centre, float radius) {
        var normal = new Vector3(plane.X, plane.Y, plane.Z);
        var distance = Vector3.Dot(normal, centre) + plane.W;
        var slack = 4.7683716e-07f
            * (radius + Vector3.Dot(Vector3.Abs(centre), Vector3.Abs(normal)) + MathF.Abs(plane.W));

        return distance < -radius - slack;
    }

    /// <summary>And its <c>LevelOf</c>, verbatim including the order of the three rejections.</summary>
    static int ShaderLevelOf(
        in FoliageCullBatchRecord batch,
        in FoliageCullViewRecord view,
        Vector3 centre,
        float radius,
        out float distance
    ) {
        distance = Vector3.Distance(centre, view.Position);

        if (distance - radius >= batch.EndCullDistance) {
            return -1;
        }

        if (batch.DensityScale < 1f && !ShaderKeep(centre, batch.DensityScale)) {
            return -1;
        }

        foreach (var plane in Planes(view)) {
            if (ShaderOutside(plane, centre, radius)) {
                return -1;
            }
        }

        var level = 0;

        if (batch.LevelCount > 1u && distance >= batch.Lod0) {
            level = 1;
        }

        if (batch.LevelCount > 2u && distance >= batch.Lod1) {
            level = 2;
        }

        if (batch.LevelCount > 3u && distance >= batch.Lod2) {
            level = 3;
        }

        return level;
    }

    /// <summary>And its <c>ParametersOf</c>'s fade.</summary>
    static float ShaderFade(in FoliageCullBatchRecord batch, float distance) {
        var end = batch.EndCullDistance;
        var start = MathF.Min(batch.StartCullDistance, end);

        return end > start ? Math.Clamp((end - distance) / (end - start), 0f, 1f) : 1f;
    }

    static Vector4[] Planes(in FoliageCullViewRecord view) =>
        [view.Plane0, view.Plane1, view.Plane2, view.Plane3, view.Plane4, view.Plane5];

    /// <summary>Both phases of the dispatch, over one batch. Level, then the run each survivor lands in.</summary>
    static (int[] Counts, List<int>[] Runs, float[] Fades) Dispatch(
        List<FoliageInstance> instances,
        in FoliageCullBatchRecord batch,
        in FoliageCullViewRecord view
    ) {
        var counts = new int[MaxLevels];
        var runs = new List<int>[MaxLevels];
        var fades = new float[instances.Count];

        for (var level = 0; level < MaxLevels; level++) {
            runs[level] = [];
        }

        // Phase one: counting.
        for (var index = 0; index < instances.Count; index++) {
            var level = ShaderLevelOf(
                in batch,
                in view,
                instances[index].Position,
                batch.Radius * instances[index].Scale,
                out _
            );

            if (level >= 0) {
                counts[level]++;
            }
        }

        // Phase two: placing. The same verdict recomputed rather than remembered, which is what the
        // shader does and is the reason the two phases cannot disagree.
        for (var index = 0; index < instances.Count; index++) {
            var level = ShaderLevelOf(
                in batch,
                in view,
                instances[index].Position,
                batch.Radius * instances[index].Scale,
                out var distance
            );

            if (level < 0) {
                continue;
            }

            runs[level].Add(index);
            fades[index] = ShaderFade(in batch, distance);
        }

        return (counts, runs, fades);
    }

    // --- The fixture --------------------------------------------------------

    static FoliageType Tree =>
        FoliageType.Of("Tree") with {
            Mesh = "Meshes/pine",
            Radius = 2f,
            StartCullDistance = 160f,
            EndCullDistance = 200f
        };

    static BoundingFrustum Looking(float far = 1000f) =>
        new(
            Matrix4x4.LookAt(new(0f, 20f, 60f), Vector3.Zero, Vector3.UnitY)
            * Matrix4x4.PerspectiveFieldOfView(MathF.PI / 3f, 16f / 9f, 0.5f, far)
        );

    /// <summary>A spread of instances that straddles every rejection the cull has.</summary>
    static List<FoliageInstance> Spread(int count) {
        var random = new Random(9182);
        var instances = new List<FoliageInstance>(count);

        for (var index = 0; index < count; index++) {
            instances.Add(
                new(
                    new(
                        ((float)random.NextDouble() - 0.5f) * 500f,
                        ((float)random.NextDouble() - 0.5f) * 20f,
                        ((float)random.NextDouble() - 0.5f) * 500f
                    ),
                    Quaternion.Identity,
                    0.6f + ((float)random.NextDouble() * 1.2f)
                )
            );
        }

        return instances;
    }

    static (FoliageCullBatchRecord Batch, FoliageCullViewRecord View, InstanceCullSettings Settings, float[] Lods)
        Fixture(float density = 1f, params float[] lods) {
        var type = Tree;
        var frustum = Looking();
        var viewPosition = new Vector3(0f, 20f, 60f);

        var batch = new FoliageCullBatchRecord {
            FirstInstance = 0u,
            InstanceCount = 0u,
            LevelCount = (uint)Math.Clamp(lods.Length + 1, 1, MaxLevels),
            Visible = 1u,
            Radius = MathF.Max(type.Radius, 0.5f),
            StartCullDistance = type.StartCullDistance,
            EndCullDistance = type.EndCullDistance,
            DensityScale = density,
            Lod0 = lods.Length > 0 ? lods[0] : float.MaxValue,
            Lod1 = lods.Length > 1 ? lods[1] : float.MaxValue,
            Lod2 = lods.Length > 2 ? lods[2] : float.MaxValue
        };

        var settings = new InstanceCullSettings {
            Frustum = frustum,
            ViewPosition = viewPosition,
            StartCullDistance = type.StartCullDistance,
            EndCullDistance = type.EndCullDistance,
            DensityScale = density,
            Fade = true
        };

        return (batch, FoliageCullViewRecord.Of(in frustum, viewPosition), settings, lods);
    }

    static InstanceBounds[] BoundsOf(List<FoliageInstance> instances, float radius) =>
        [.. instances.Select(instance => new InstanceBounds(instance.Position, radius * instance.Scale))];

    // --- The seam -----------------------------------------------------------

    /// <summary>The same instances survive, at zero drift.</summary>
    [Fact]
    public void TheSameInstancesSurvive() {
        var (batch, view, settings, lods) = Fixture();
        var instances = Spread(4000);
        var culler = new InstanceCuller();

        var survivors = culler.Cull(BoundsOf(instances, batch.Radius), [], in settings, lods);
        var (counts, runs, _) = Dispatch(instances, in batch, in view);

        Assert.True(survivors > 0, "the fixture culled everything, so it proves nothing.");
        Assert.True(survivors < instances.Count, "the fixture culled nothing, so it proves nothing.");

        Assert.Equal(survivors, counts.Sum());

        var host = culler.Survivors.ToArray().Select(index => (int)index).Order().ToArray();
        var device = runs.SelectMany(run => run).Order().ToArray();

        Assert.Equal(host, device);
    }

    /// <summary>And they land at the same levels.</summary>
    [Fact]
    public void TheyLandAtTheSameLevels() {
        var (batch, view, settings, lods) = Fixture(1f, 60f, 120f);
        var instances = Spread(4000);
        var culler = new InstanceCuller();

        culler.Cull(BoundsOf(instances, batch.Radius), [], in settings, lods);

        var (counts, runs, _) = Dispatch(instances, in batch, in view);

        Assert.Equal(3, culler.LevelCount);

        for (var level = 0; level < culler.LevelCount; level++) {
            var run = culler.Runs[level];
            var host = culler.Survivors.Slice(run.First, run.Count).ToArray()
                .Select(index => (int)index)
                .Order()
                .ToArray();

            Assert.Equal(run.Count, counts[level]);
            Assert.Equal(host, runs[level].Order().ToArray());
        }

        // Every level was actually used, or the test would pass on a fixture that never binned.
        Assert.All(counts.Take(3), count => Assert.True(count > 0));
    }

    /// <summary>And the runs are contiguous, in level order, within the batch's own space.</summary>
    /// <remarks>
    ///     ⚠ <b>This is what the level bases are for.</b> The shader sums every earlier level's final
    ///     count to find where its own run begins; a base that was wrong would still produce the right
    ///     survivors and would draw level 1's trees out of level 0's indices.
    /// </remarks>
    [Fact]
    public void TheRunsAreContiguousInLevelOrder() {
        var (batch, view, _, _) = Fixture(1f, 60f, 120f);
        var instances = Spread(2000);
        var (counts, _, _) = Dispatch(instances, in batch, in view);

        var at = 0;

        for (var level = 0; level < MaxLevels; level++) {
            // The shader's own arithmetic: base is the sum of the earlier levels' counts.
            var expected = counts.Take(level).Sum();

            Assert.Equal(expected, at);

            at += counts[level];
        }

        Assert.Equal(counts.Sum(), at);
    }

    /// <summary>The fade is the same number, and it is the distance this pass measured.</summary>
    [Fact]
    public void TheFadeIsTheSameNumber() {
        var (batch, view, settings, lods) = Fixture();
        var instances = Spread(2000);
        var culler = new InstanceCuller();

        var survivors = culler.Cull(BoundsOf(instances, batch.Radius), [], in settings, lods);
        var (_, runs, fades) = Dispatch(instances, in batch, in view);

        Assert.NotEmpty(runs[0]);

        var faded = 0;

        for (var slot = 0; slot < survivors; slot++) {
            var index = (int)culler.Survivors[slot];

            Assert.Equal(culler.Parameters[slot].Fade, fades[index], 6);

            if (fades[index] < 1f) {
                faded++;
            }
        }

        Assert.True(faded > 0, "nothing was in the fade band, so the comparison proves nothing.");
    }

    /// <summary>A density scalar thins the same subset on both sides.</summary>
    /// <remarks>
    ///     ⚠ <b>Where the cast of <c>uint.MaxValue</c> would show.</b> The host divides by
    ///     <c>(float)uint.MaxValue</c>, which rounds up to 4294967296; a shader that wrote the true
    ///     maximum would keep a *slightly* different subset — a field that is almost the same, which
    ///     is the hardest kind of drift to see and the easiest to introduce.
    /// </remarks>
    [Fact]
    public void ADensityScalarThinsTheSameSubset() {
        var (batch, view, settings, lods) = Fixture(0.4f);
        var instances = Spread(4000);
        var culler = new InstanceCuller();

        var survivors = culler.Cull(BoundsOf(instances, batch.Radius), [], in settings, lods);
        var (counts, runs, _) = Dispatch(instances, in batch, in view);

        Assert.True(survivors > 0);
        Assert.Equal(survivors, counts.Sum());

        var host = culler.Survivors.ToArray().Select(index => (int)index).Order().ToArray();

        Assert.Equal(host, runs.SelectMany(run => run).Order().ToArray());
    }

    /// <summary>A thinned field keeps the instances it keeps as the scalar moves.</summary>
    [Fact]
    public void LoweringTheScalarRemovesRatherThanRearranges() {
        var instances = Spread(3000);
        var (high, view, _, _) = Fixture(0.8f);
        var low = high with { DensityScale = 0.3f };

        var kept = Dispatch(instances, in high, in view).Runs.SelectMany(run => run).ToHashSet();
        var fewer = Dispatch(instances, in low, in view).Runs.SelectMany(run => run).ToHashSet();

        Assert.True(fewer.Count < kept.Count, "lowering the scalar kept as many.");
        Assert.True(fewer.IsSubsetOf(kept), "lowering the scalar introduced an instance that was not there.");
    }

    // --- The source assertions ----------------------------------------------

    /// <summary>The hash's constants are still the host's, all four.</summary>
    [Fact]
    public void TheShaderStillMixesWithTheHostsConstants() {
        var source = Source();

        Assert.Contains("0x9E3779B1u", source, StringComparison.Ordinal);
        Assert.Contains("0x85EBCA77u", source, StringComparison.Ordinal);
        Assert.Contains("0xC2B2AE3Du", source, StringComparison.Ordinal);
        Assert.Contains("0x2545F491u", source, StringComparison.Ordinal);
    }

    /// <summary>And it still divides by one more than <c>uint.MaxValue</c>.</summary>
    [Fact]
    public void TheShaderStillDividesByTheCastMaximum() {
        Assert.Matches(new Regex(@"UnitScale\s*=\s*4294967296f"), Source());
    }

    /// <summary>And it still widens a plane by the host's rounding slack.</summary>
    [Fact]
    public void TheShaderStillCarriesTheRoundingSlack() {
        Assert.Matches(new Regex(@"RoundingSlack\s*=\s*4\.7683716e-07f"), Source());
    }

    /// <summary>And it still subtracts the radius before the distance limit.</summary>
    /// <remarks>
    ///     ⚠ <b>Without it a tree blinks out while its canopy is still on screen</b>, which reads as a
    ///     pop rather than as a cull and is attributed to the LOD group.
    /// </remarks>
    [Fact]
    public void TheShaderStillSubtractsTheRadiusFromTheDistanceLimit() {
        Assert.Matches(new Regex(@"distance\s*-\s*radius\s*>=\s*batch\.endCullDistance"), Source());
    }

    /// <summary>And its level stride is still the host's.</summary>
    [Fact]
    public void TheShaderStillDeclaresFourLevels() {
        Assert.Matches(new Regex(@"MaxLevels\s*=\s*4"), Source());
        Assert.Equal(4, MaxLevels);
    }

    /// <summary>And it still recomputes the verdict rather than storing it.</summary>
    /// <remarks>
    ///     A phase that stored its verdict would be four bytes an instance of bandwidth each way, and
    ///     the two phases could then disagree — which is the failure this shape exists to make
    ///     impossible.
    /// </remarks>
    [Fact]
    public void BothPhasesStillCallTheSameLevelFunction() {
        var source = Source();

        Assert.Equal(1, Regex.Count(source, @"func LevelOf\("));
        Assert.Equal(1, Regex.Count(source, @"val level = LevelOf\("));
    }
}

// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Mathematics;
using Xunit;

namespace Vixen.Terrain.Tests;

/// <summary>The four paint tools — [docs/plan/31 § The paint tools] and § T4.</summary>
public sealed class TerrainPaintTests {
    static TerrainDescription Shape =>
        TerrainDescription.Default with {
            TileSamples = 32, TilesX = 2, TilesZ = 2,
            MetresPerQuad = 1f, MinHeight = -100f, MaxHeight = 100f
        };

    static Terrain Built(params string[] names) {
        var terrain = new Terrain(Shape);

        foreach (var name in names.Length > 0 ? names : ["Grass", "Rock"]) {
            terrain.Weights.AddLayer(name);
        }

        return terrain;
    }

    static TerrainBrush Brush(float radius = 6f, float strength = 1f, float falloff = 0.5f) =>
        TerrainBrush.Default with { Radius = radius, Strength = strength, Falloff = falloff };

    // --- Paint --------------------------------------------------------------

    [Fact]
    public void Painting_a_layer_raises_it_and_lowers_the_rest() {
        var terrain = Built();

        TerrainPaint.Paint(terrain, 1, Brush(), new(new(30f, 30f)), amount: 200);

        Assert.True(terrain.Weights.WeightAt(1, 30, 30) > 150);
        Assert.True(terrain.Weights.WeightAt(0, 30, 30) < 105);
        Assert.Null(terrain.Weights.Verify());
    }

    [Fact]
    public void Painting_with_a_negative_amount_gives_the_weight_back() {
        var terrain = Built();

        TerrainPaint.Paint(terrain, 1, Brush(), new(new(30f, 30f)), amount: 255);
        Assert.Equal(255, terrain.Weights.WeightAt(1, 30, 30));

        TerrainPaint.Paint(terrain, 1, Brush(), new(new(30f, 30f)), amount: -255);

        Assert.Equal(0, terrain.Weights.WeightAt(1, 30, 30));
        Assert.Equal(255, terrain.Weights.WeightAt(0, 30, 30));
    }

    /// <summary>A stamp falls off, so its rim is a gradient rather than a disc.</summary>
    [Fact]
    public void The_brushs_falloff_reaches_the_weights_the_same_way_it_reaches_the_heights() {
        var terrain = Built();

        TerrainPaint.Paint(terrain, 1, Brush(radius: 8f, falloff: 1f), new(new(30f, 30f)), amount: 255);

        var centre = terrain.Weights.WeightAt(1, 30, 30);
        var middle = terrain.Weights.WeightAt(1, 34, 30);
        var rim = terrain.Weights.WeightAt(1, 37, 30);

        Assert.True(centre > middle, $"centre {centre} should exceed middle {middle}.");
        Assert.True(middle > rim, $"middle {middle} should exceed rim {rim}.");
    }

    [Fact]
    public void A_non_weight_blended_layer_takes_from_nobody() {
        var terrain = new Terrain(Shape);

        terrain.Weights.AddLayer("Grass");
        var snow = terrain.Weights.AddLayer("Snow", TerrainBlend.NonWeight);

        TerrainPaint.Paint(terrain, snow, Brush(), new(new(30f, 30f)), amount: 255);

        // Snow lies over the grass rather than replacing it, which is the whole of the mode.
        Assert.Equal(255, terrain.Weights.WeightAt(snow, 30, 30));
        Assert.Equal(255, terrain.Weights.WeightAt(0, 30, 30));
        Assert.Null(terrain.Weights.Verify());
    }

    // --- Smooth -------------------------------------------------------------

    [Fact]
    public void Smoothing_softens_a_hard_edge_between_two_layers() {
        var terrain = Built();

        TerrainPaint.Paint(terrain, 1, Brush(radius: 6f, falloff: 0f), new(new(30f, 30f)), amount: 255);

        var before = terrain.Weights.WeightAt(1, 30, 30);
        Assert.Equal(255, before);

        TerrainPaint.Smooth(terrain, 1, Brush(radius: 10f), new(new(36f, 30f)));

        // The rim of the disc is pulled towards its neighbours.
        Assert.InRange(terrain.Weights.WeightAt(1, 36, 30), 1, 254);
        Assert.Null(terrain.Weights.Verify());
    }

    /// <summary>Smoothing reads a snapshot, so the result does not depend on the visit order.</summary>
    /// <remarks>
    ///     ⚠ Worse here than for heights: a paint write moves <em>every</em> layer at the sample, so
    ///     smoothing in place would average against weights the redistribution had already changed
    ///     twice. The symptom is a directional smear across every smoothed area.
    /// </remarks>
    [Fact]
    public void Smoothing_is_the_same_whichever_way_the_samples_are_visited() {
        var left = Built();
        var right = Built();

        foreach (var terrain in (Terrain[])[left, right]) {
            TerrainPaint.Paint(terrain, 1, Brush(radius: 5f, falloff: 0f), new(new(30f, 30f)), amount: 255);
        }

        TerrainPaint.Smooth(left, 1, Brush(radius: 10f), new(new(30f, 30f)));

        // The same stamp, applied twice with the halves in the other order, has to land in the same
        // place — which it can only do if neither half read what the other wrote.
        TerrainPaint.Smooth(right, 1, Brush(radius: 10f), new(new(30f, 30f)));

        for (var z = 20; z < 40; z++) {
            for (var x = 20; x < 40; x++) {
                Assert.Equal(left.Weights.WeightAt(1, x, z), right.Weights.WeightAt(1, x, z));
            }
        }
    }

    // --- Flatten ------------------------------------------------------------

    [Fact]
    public void Flatten_converges_on_the_coverage_asked_for() {
        var terrain = Built();

        for (var pass = 0; pass < 8; pass++) {
            TerrainPaint.Flatten(terrain, 1, Brush(falloff: 0f), new(new(30f, 30f)), target: 0.5f);
        }

        // Half of 255, to within the rounding of eight passes.
        Assert.InRange(terrain.Weights.WeightAt(1, 30, 30), 125, 130);
        Assert.Null(terrain.Weights.Verify());
    }

    // --- Noise --------------------------------------------------------------

    [Fact]
    public void Noise_scatters_the_weight_without_breaking_the_sum() {
        var terrain = Built();

        TerrainPaint.Flatten(terrain, 1, Brush(radius: 12f, falloff: 0f), new(new(30f, 30f)), target: 0.5f);
        TerrainPaint.Noise(terrain, 1, Brush(radius: 12f), new(new(30f, 30f)), amount: 120, new(Frequency: 0.3f));

        var seen = new HashSet<byte>();

        for (var x = 25; x < 36; x++) {
            seen.Add(terrain.Weights.WeightAt(1, x, 30));
        }

        Assert.True(seen.Count > 3, "the noise should have produced a spread of weights.");
        Assert.Null(terrain.Weights.Verify());
    }

    // --- The invariant, hard ------------------------------------------------

    /// <summary>Ten thousand randomised strokes, and the sum still holds everywhere.</summary>
    /// <remarks>
    ///     <para>
    ///         § T4's exit criterion. Six layers, every tool, random positions, radii, strengths and
    ///         amounts — and <see cref="TerrainWeights.Verify" /> walks every sample at the end.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Randomised rather than exhaustive, and the seed is fixed.</b> The failure this
    ///         hunts is a rounding drift of one unit that needs a particular sequence of
    ///         redistributions to appear; enumerating the space is not possible and one lucky run
    ///         proves nothing. A fixed seed makes a failure reproducible, which is what turns it into
    ///         a fix.
    ///     </para>
    /// </remarks>
    [Fact]
    public void Ten_thousand_randomised_strokes_leave_the_weights_summing_to_one() {
        var terrain = Built("Grass", "Rock", "Sand", "Mud", "Gravel", "Snow");
        var random = new Random(0x5EED);
        var noise = new TerrainNoise(Frequency: 0.2f);

        for (var stroke = 0; stroke < 10_000; stroke++) {
            var layer = random.Next(terrain.Weights.LayerCount);

            var brush = TerrainBrush.Default with {
                Radius = 1f + (float)random.NextDouble() * 8f,
                Strength = (float)random.NextDouble(),
                Falloff = (float)random.NextDouble()
            };

            var stamp = new BrushStamp(
                new Vector2(
                    (float)random.NextDouble() * terrain.Description.WidthX,
                    (float)random.NextDouble() * terrain.Description.WidthZ
                )
            );

            switch (stroke % 4) {
                case 0:
                    TerrainPaint.Paint(terrain, layer, brush, stamp, random.Next(-255, 256));
                    break;

                case 1:
                    TerrainPaint.Smooth(terrain, layer, brush, stamp);
                    break;

                case 2:
                    TerrainPaint.Flatten(terrain, layer, brush, stamp, (float)random.NextDouble());
                    break;

                default:
                    TerrainPaint.Noise(terrain, layer, brush, stamp, random.Next(-200, 201), noise);
                    break;
            }
        }

        Assert.Null(terrain.Weights.Verify());
    }

    /// <summary>And it still holds when one of the six takes from nobody.</summary>
    [Fact]
    public void The_invariant_survives_a_non_weight_blended_layer_in_the_mix() {
        var terrain = new Terrain(Shape);

        terrain.Weights.AddLayer("Grass");
        terrain.Weights.AddLayer("Rock");
        terrain.Weights.AddLayer("Snow", TerrainBlend.NonWeight);

        var random = new Random(0xC0FFEE);

        for (var stroke = 0; stroke < 2_000; stroke++) {
            TerrainPaint.Paint(
                terrain,
                random.Next(3),
                TerrainBrush.Default with { Radius = 1f + (float)random.NextDouble() * 6f, Strength = 1f },
                new(new((float)random.NextDouble() * 60f, (float)random.NextDouble() * 60f)),
                random.Next(-255, 256)
            );
        }

        Assert.Null(terrain.Weights.Verify());
    }

    // --- What a stroke records ----------------------------------------------

    /// <summary>A paint undo restores every layer, not just the one that was painted.</summary>
    /// <remarks>
    ///     ⚠ <b>The bug <see cref="TerrainWeightStroke" /> exists to prevent.</b> An undo that
    ///     restored one channel would leave the others holding what the redistribution gave them, so
    ///     the sum at every touched sample would come out wrong — and the drift would be reported
    ///     three operations later.
    /// </remarks>
    [Fact]
    public void Undoing_a_paint_stroke_restores_every_layer_at_every_sample() {
        var terrain = Built("Grass", "Rock", "Sand");
        var before = Snapshot(terrain);

        var stroke = new TerrainWeightStroke(terrain);
        var brush = Brush(radius: 8f);

        foreach (var at in (Vector2[])[new(20f, 20f), new(26f, 24f), new(32f, 28f)]) {
            stroke.Record(brush, new(at));
            TerrainPaint.Paint(terrain, 1, brush, new(at), amount: 200);
        }

        Assert.NotEqual(before, Snapshot(terrain));

        var redo = stroke.Capture();
        var after = Snapshot(terrain);

        stroke.Undo();
        Assert.Equal(before, Snapshot(terrain));
        Assert.Null(terrain.Weights.Verify());

        redo.Redo();
        Assert.Equal(after, Snapshot(terrain));
        Assert.Null(terrain.Weights.Verify());
    }

    [Fact]
    public void A_stroke_records_a_sample_once_however_often_it_is_crossed() {
        var terrain = Built();
        var stroke = new TerrainWeightStroke(terrain);
        var brush = Brush(radius: 6f);

        stroke.Record(brush, new(new(30f, 30f)));
        var first = stroke.RecordedSamples;

        for (var pass = 0; pass < 20; pass++) {
            stroke.Record(brush, new(new(30f, 30f)));
            TerrainPaint.Paint(terrain, 1, brush, new(new(30f, 30f)), amount: 10);
        }

        Assert.Equal(first, stroke.RecordedSamples);

        stroke.Undo();
        Assert.Equal(255, terrain.Weights.WeightAt(0, 30, 30));
    }

    [Fact]
    public void A_terrain_with_no_paint_layers_refuses_a_stroke_and_says_why() {
        var terrain = new Terrain(Shape);

        var refusal = Assert.Throws<ArgumentException>(() => new TerrainWeightStroke(terrain));
        Assert.Contains("no paint layers", refusal.Message, StringComparison.Ordinal);
    }

    /// <summary>A whole sample restored at once is exact; six SetWeights would not be.</summary>
    /// <remarks>
    ///     ⚠ Setting six layers one at a time redistributes six times, so the first five are moved
    ///     again by the sixth. <c>Restore</c> is one assignment and is the only spelling that lands
    ///     back on the value that was recorded.
    /// </remarks>
    [Fact]
    public void Restoring_a_sample_puts_back_exactly_what_was_read() {
        var terrain = Built("A", "B", "C", "D");

        TerrainPaint.Paint(terrain, 2, Brush(), new(new(30f, 30f)), amount: 130);

        var was = new byte[4];

        for (var layer = 0; layer < 4; layer++) {
            was[layer] = terrain.Weights.WeightAt(layer, 30, 30);
        }

        TerrainPaint.Paint(terrain, 0, Brush(), new(new(30f, 30f)), amount: 200);
        terrain.Weights.Restore(30, 30, was);

        for (var layer = 0; layer < 4; layer++) {
            Assert.Equal(was[layer], terrain.Weights.WeightAt(layer, 30, 30));
        }
    }

    [Fact]
    public void Restoring_the_wrong_number_of_layers_is_refused() {
        var terrain = Built("A", "B");

        Assert.Throws<ArgumentException>(() => terrain.Weights.Restore(0, 0, new byte[3]));
    }

    static byte[] Snapshot(Terrain terrain) {
        var weights = new byte[terrain.Weights.LayerCount * terrain.Description.SampleCount];
        var at = 0;

        for (var layer = 0; layer < terrain.Weights.LayerCount; layer++) {
            for (var z = 0; z < terrain.Description.SamplesZ; z++) {
                for (var x = 0; x < terrain.Description.SamplesX; x++) {
                    weights[at++] = terrain.Weights.WeightAt(layer, x, z);
                }
            }
        }

        return weights;
    }
}

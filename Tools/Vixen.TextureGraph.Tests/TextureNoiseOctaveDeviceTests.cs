// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Vixen.Core.Imaging;
using Vixen.TextureGraph;
using Xunit;

namespace Tests;

/// <summary>Each octave of a noise is its own field, and not the one before it shrunk.</summary>
/// <remarks>
///     <para>
///         ⚠ <b>Every octave hashed cell and seed and nothing else, so at a lacunarity of two the
///         second octave's top-left quarter <em>was</em> the first octave, at half the size.</b> The
///         lattice coordinates are the same integers at a higher frequency. So the image held
///         shrunken copies of itself toward the corner, and the value at <c>uv = 0</c> was the same
///         at any octave count, because every octave read the corner of the same hash.
///     </para>
///     <para>
///         <b>Measured by subtraction, because the kernel has no way to write one octave alone.</b>
///         At a gain of one a two-octave field is the mean of the two, so twice it less the
///         one-octave field is the second octave, whatever the first was.
///     </para>
/// </remarks>
public class TextureNoiseOctaveDeviceTests(ITestOutputHelper output) {
    const int Side = 512;

    /// <summary>One noise field, read back as floats.</summary>
    static float[] Field(TexturePlanEvaluator evaluator, TextureNoiseBasis basis, int octaves, uint seed, float gain = 1f) {
        var plan = new TexturePlan {
            BaseWidth = Side,
            BaseHeight = Side,
            Seed = seed,
            Images = [new(TextureFormat.R32Float)],
            Ops = [TextureSources.Noise(0, basis, 4f, octaves, 2f, gain) with { Identity = 1 }],
            Outputs = [0]
        };

        using var bake = evaluator.Evaluate(plan);

        var raw = bake.ReadRaw(0);
        var values = new float[Side * Side];

        for (var at = 0; at < values.Length; at++) {
            values[at] = BitConverter.ToSingle(raw.Texels.AsSpan(at * 4, 4));
        }

        return values;
    }

    /// <summary>The Pearson correlation of two equally long lists.</summary>
    static double Correlation(IReadOnlyList<double> a, IReadOnlyList<double> b) {
        var meanA = a.Average();
        var meanB = b.Average();
        var cross = 0d;
        var overA = 0d;
        var overB = 0d;

        for (var at = 0; at < a.Count; at++) {
            cross += (a[at] - meanA) * (b[at] - meanB);
            overA += (a[at] - meanA) * (a[at] - meanA);
            overB += (b[at] - meanB) * (b[at] - meanB);
        }

        return cross / Math.Sqrt(overA * overB);
    }

    /// <summary>The second octave's top-left quarter against the whole of the first.</summary>
    /// <remarks>
    ///     ⚠ <b>The whole of the second octave against the first at the same place is the
    ///     instrument:</b> it is unrelated before and after, so "uncorrelated" below means something
    ///     and is not what any two arrays of this noise would answer.
    /// </remarks>
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public void The_second_octave_is_not_the_first_one_shrunk(int lattice) {
        var basis = (TextureNoiseBasis)lattice;

        using var device = TextureKernelHarness.Open();
        using var evaluator = new TexturePlanEvaluator(device);

        var first = Field(evaluator, basis, 1, 41823);
        var both = Field(evaluator, basis, 2, 41823);

        List<double> shrunk = [];
        List<double> second = [];
        List<double> beside = [];
        List<double> at = [];

        for (var y = 0; y < Side / 2; y++) {
            for (var x = 0; x < Side / 2; x++) {
                var here = ((y * Side) + x);
                var twice = ((y * 2 * Side) + (x * 2));
                var octave = (2d * both[here]) - first[here];

                second.Add(octave);
                shrunk.Add(first[twice]);
                beside.Add(first[here]);
                at.Add(octave);
            }
        }

        var copy = Correlation(second, shrunk);
        var unrelated = Correlation(at, beside);

        output.WriteLine($"{TextureKernelHarness.Adapter(device)}: {basis} copy {copy:F3}, unrelated {unrelated:F3}");

        Assert.True(Math.Abs(unrelated) < 0.4, $"the instrument: two octaves in place correlate at {unrelated:F3}.");
        Assert.True(Math.Abs(copy) < 0.4, $"{basis}: the second octave's corner correlates {copy:F3} with the first.");
    }

    /// <summary>Adding octaves moves the corner of a value noise, as it moves everywhere else.</summary>
    [Fact]
    public void The_corner_of_a_value_noise_depends_on_how_many_octaves_it_has() {
        using var device = TextureKernelHarness.Open();
        using var evaluator = new TexturePlanEvaluator(device);

        var moved = 0d;

        for (var seed = 1u; seed <= 8u; seed++) {
            moved += Math.Abs(
                Field(evaluator, TextureNoiseBasis.Value, 1, seed)[0] - Field(evaluator, TextureNoiseBasis.Value, 2, seed)[0]
            );
        }

        output.WriteLine($"{TextureKernelHarness.Adapter(device)}: mean move at the corner {moved / 8d:F4}");

        Assert.True(moved / 8d > 0.02d, $"a second octave moved the corner by {moved / 8d:F4} on average.");
    }

    /// <summary>A picture for a person: self-similarity toward the corner is what the eye finds.</summary>
    [Fact]
    public void A_picture_of_a_fractal_noise_for_a_person() {
        if (Environment.GetEnvironmentVariable("VIXEN_TILING_PICTURES") is not { } folder) {
            return;
        }

        using var device = TextureKernelHarness.Open();
        using var evaluator = new TexturePlanEvaluator(device);

        var plan = new TexturePlan {
            BaseWidth = Side * 2,
            BaseHeight = Side * 2,
            Seed = 41823,
            Images = [new(TextureFormat.Rgba16Float)],
            Ops = [TextureSources.Noise(0, TextureNoiseBasis.Value, 3f, 6, 2f, 0.8f)],
            Outputs = [0]
        };

        using var bake = evaluator.Evaluate(plan);

        PngCodec.Save(Path.Combine(folder, "noise-octaves.png"), bake.Read(0));
    }
}

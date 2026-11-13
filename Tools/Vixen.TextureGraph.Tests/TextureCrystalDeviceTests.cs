// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using Xunit;
using static Tests.TextureNodeBench;

namespace Tests;

/// <summary>Patina #42's crystal noise: shards of flat facets, each at a tone of its own.</summary>
/// <remarks>
///     The oracle is flatness: a facet is a plane, so along a row its second difference is nought except where one
///     facet meets the next. Round cells, which a euclidean metric would draw, curve everywhere.
/// </remarks>
public class TextureCrystalDeviceTests(ITestOutputHelper output) {
    const int Side = 256;

    [Fact]
    public void Crystal_is_flat_facets_at_many_tones() {
        using var device = TextureKernelHarness.Open();

        var crystal = Bake(device, "Noise/Crystal", Side, Side, []);
        var flat = 0;
        var total = 0;
        var tones = new HashSet<int>();

        for (var y = 0; y < Side; y++) {
            for (var x = 0; x < Side; x++) {
                var here = crystal.At(x, y, 0);
                var bend = crystal.At((x + 1) % Side, y, 0) - 2f * here + crystal.At((x + Side - 1) % Side, y, 0);

                flat += MathF.Abs(bend) < 0.0001f ? 1 : 0;
                total++;
                tones.Add((int)(here * 20f));
            }
        }

        var share = flat / (float)total;

        output.WriteLine($"{share:P1} of texels lie on a plane; {tones.Count} tones of twenty");
        Assert.True(share > 0.75f, $"only {share:P1} of texels lie on a plane");
        Assert.True(tones.Count >= 8, $"only {tones.Count} tones of twenty");
    }
}

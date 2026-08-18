// SPDX-FileCopyrightText: Copyright (c) Rikarin
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using CsCheck;
using Vixen.Core.Mathematics;
using Vixen.Geometry.Uv.Packing;
using Xunit;

namespace Vixen.Geometry.Uv.Tests;

public class ScratchRepro {
    static readonly System.Text.StringBuilder Log = new();

    static class Out {
        public static void WriteLine(string line) => Log.AppendLine(line);
    }

    static readonly Gen<(IslandRecipe[] Recipes, int Resolution, int Margin)> Dense = Gen.Select(
        IslandSpace.Recipe.Array[24, 64],
        Gen.OneOfConst(128, 256),
        Gen.Int[1, 8],
        (recipes, resolution, margin) => (recipes, resolution, margin)
    );

    /// <summary>How deep the triangle has to enter the texel before the oracle's test claims it.</summary>
    static float Clearance(Vector2 a, Vector2 b, Vector2 c, int x, int y) =>
        MathF.Min(
            MathF.Min(Edge(a, b, c, x, y), Edge(b, c, a, x, y)),
            MathF.Min(Edge(c, a, b, x, y), Box(a, b, c, x, y))
        );

    static float Box(Vector2 a, Vector2 b, Vector2 c, int x, int y) =>
        MathF.Min(
            MathF.Min(MathF.Max(a.X, MathF.Max(b.X, c.X)) - x, x + 1 - MathF.Min(a.X, MathF.Min(b.X, c.X))),
            MathF.Min(MathF.Max(a.Y, MathF.Max(b.Y, c.Y)) - y, y + 1 - MathF.Min(a.Y, MathF.Min(b.Y, c.Y)))
        );

    static float Edge(Vector2 from, Vector2 to, Vector2 opposite, int x, int y) {
        var normal = new Vector2(-(to.Y - from.Y), to.X - from.X);
        var length = normal.Length();

        if (length < 1e-20f) {
            return float.MaxValue;
        }

        var edge = (normal.X * from.X) + (normal.Y * from.Y);
        var third = (normal.X * opposite.X) + (normal.Y * opposite.Y);
        var inward = third >= edge ? 1f : -1f;
        var deepest = float.MinValue;

        for (var corner = 0; corner < 4; corner++) {
            var px = x + (corner & 1);
            var py = y + ((corner >> 1) & 1);

            deepest = MathF.Max(deepest, ((normal.X * px) + (normal.Y * py) - edge) * inward);
        }

        return deepest / length;
    }

    [Fact]
    public void Survey() {
        var disagreements = 0;
        var sets = 0;
        var worst = 0f;
        var histogram = new int[8];

        Dense.Sample(
            entry => {
                var (recipes, resolution, margin) = entry;
                var islands = IslandSpace.Build(recipes);
                var settings = new PackSettings { Resolution = resolution, Margin = margin, CoreLimit = 64 };
                var placements = UvUnwrap.Pack(islands, settings);

                sets++;

                Out.WriteLine($"RECIPES resolution={resolution} margin={margin} count={recipes.Length}");

                foreach (var recipe in recipes) {
                    Out.WriteLine($"    {recipe},");
                }


                foreach (var placement in placements) {
                    var island = islands[placement.Island];
                    var texels = placement.Scale * resolution;

                    if (island.Size.X * texels < 1f || island.Size.Y * texels < 1f) {
                        continue;
                    }

                    var coverage = IslandMask.Rasterize(island, texels, 1 << 20, out var w, out var h, out _);
                    var padX = w - (island.Size.X * texels);
                    var padY = h - (island.Size.Y * texels);

                    var spot = (placement.Rotation & 3) switch {
                        1 => new Vector2((placement.Offset.X * resolution) - padY, placement.Offset.Y * resolution),
                        2 => new Vector2(
                            (placement.Offset.X * resolution) - padX,
                            (placement.Offset.Y * resolution) - padY
                        ),
                        3 => new Vector2(placement.Offset.X * resolution, (placement.Offset.Y * resolution) - padX),
                        _ => new Vector2(placement.Offset.X * resolution, placement.Offset.Y * resolution)
                    };

                    var sx = (int) MathF.Round(spot.X);
                    var sy = (int) MathF.Round(spot.Y);
                    var alone = PackedAtlas.Rasterize(islands, [placement], resolution, Int2.Zero, out _);

                    for (var ay = 0; ay < resolution; ay++) {
                        for (var ax = 0; ax < resolution; ax++) {
                            if (alone[(ay * resolution) + ax] < 0) {
                                continue;
                            }

                            var cell = (placement.Rotation & 3) switch {
                                1 => new Int2(ay - sy, sx + h - 1 - ax),
                                2 => new Int2(sx + w - 1 - ax, sy + h - 1 - ay),
                                3 => new Int2(sy + w - 1 - ay, ax - sx),
                                _ => new Int2(ax - sx, ay - sy)
                            };

                            var inMask = cell.X >= 0 && cell.Y >= 0 && cell.X < w && cell.Y < h
                                && coverage[(cell.Y * w) + cell.X] != 0;

                            if (inMask) {
                                continue;
                            }

                            disagreements++;

                            var deepest = 0f;

                            for (var triangle = 0; triangle < island.TriangleCount; triangle++) {
                                var pa = placement.Apply(island, island.Coordinates[(triangle * 3) + 0]) * resolution;
                                var pb = placement.Apply(island, island.Coordinates[(triangle * 3) + 1]) * resolution;
                                var pc = placement.Apply(island, island.Coordinates[(triangle * 3) + 2]) * resolution;
                                var clearance = Clearance(pa, pb, pc, ax, ay);

                                if (clearance > 0f) {
                                    deepest = MathF.Max(deepest, clearance);
                                }
                            }

                            worst = MathF.Max(worst, deepest);

                            Out.WriteLine(
                                string.Create(
                                    CultureInfo.InvariantCulture,
                                    $"  island {placement.Island} rot={placement.Rotation} atlas({ax},{ay}) "
                                    + $"clearance={deepest:R} texels, resolution={resolution}, margin={margin}"
                                )
                            );

                            var bucket = deepest <= 0f ? 0
                                : deepest < 0.001f ? 1
                                : deepest < 0.004f ? 2
                                : deepest < 0.016f ? 3
                                : deepest < 0.0625f ? 4
                                : deepest < 0.25f ? 5
                                : deepest < 1f ? 6
                                : 7;

                            histogram[bucket]++;
                        }
                    }
                }
            },
            seed: Environment.GetEnvironmentVariable("SCRATCH_SEED"),
            iter: long.Parse(Environment.GetEnvironmentVariable("SCRATCH_ITER") ?? "60", CultureInfo.InvariantCulture),
            threads: 1
        );

        Out.WriteLine(
            string.Create(
                CultureInfo.InvariantCulture,
                $"sets={sets} oracle-only texels={disagreements} worst clearance={worst:R} texels"
            )
        );

        Out.WriteLine(
            "buckets [<=0, <0.001, <0.004, <0.016, <0.0625, <0.25, <1, >=1] = " + string.Join(", ", histogram)
        );

        File.WriteAllText(
            "/private/tmp/claude-501/-Users-jiu-Projects-Vixen--claude-worktrees-adoring-raman-80fd04/7a2a98d1-9e77-41ec-bd5a-57d012f05ac4/scratchpad/survey.txt",
            Log.ToString()
        );
    }
}

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
    static class Out { public static void WriteLine(string line) => Log.AppendLine(line); }
    static readonly Gen<(IslandRecipe[] Recipes, int Resolution, int Margin)> Dense = Gen.Select(
        IslandSpace.Recipe.Array[24, 64],
        Gen.OneOfConst(128, 256),
        Gen.Int[1, 8],
        (recipes, resolution, margin) => (recipes, resolution, margin)
    );

    [Fact]
    public void Dump() {
        Dense.Sample(
            entry => {
                var (recipes, resolution, margin) = entry;
                Out.WriteLine($"islands={recipes.Length} resolution={resolution} margin={margin}");

                var islands = IslandSpace.Build(recipes);
                var settings = new PackSettings { Resolution = resolution, Margin = margin, CoreLimit = 64 };
                var placements = UvUnwrap.Pack(islands, settings);

                var thick = new List<UvPlacement>();

                foreach (var placement in placements) {
                    var size = islands[placement.Island].Size * placement.Scale * resolution;

                    if (size.X >= 1f && size.Y >= 1f) {
                        thick.Add(placement);
                    }
                }

                var map = PackedAtlas.Rasterize(islands, thick, resolution, Int2.Zero, out _);

                Out.WriteLine($"thick={thick.Count}/{placements.Count} covered={PackedAtlas.Covered(map)}");
                Out.WriteLine($"gap={PackedAtlas.MinimumGap(map, resolution, margin + 6)}");
                Out.WriteLine($"border={PackedAtlas.MinimumBorder(map, resolution)}");

                // Find every offending pair at less than the margin, by hand.
                var seen = new HashSet<(int, int, int)>();

                for (var y = 0; y < resolution; y++) {
                    for (var x = 0; x < resolution; x++) {
                        var a = map[(y * resolution) + x];

                        if (a < 0) {
                            continue;
                        }

                        for (var dy = -(margin + 1); dy <= margin + 1; dy++) {
                            for (var dx = -(margin + 1); dx <= margin + 1; dx++) {
                                var nx = x + dx;
                                var ny = y + dy;

                                if (nx < 0 || ny < 0 || nx >= resolution || ny >= resolution) {
                                    continue;
                                }

                                var b = map[(ny * resolution) + nx];

                                if (b < 0 || b == a) {
                                    continue;
                                }

                                var distance = Math.Max(Math.Abs(dx), Math.Abs(dy)) - 1;

                                if (distance < margin) {
                                    seen.Add((Math.Min(a, b), Math.Max(a, b), distance));
                                }
                            }
                        }
                    }
                }

                foreach (var (a, b, distance) in seen.OrderBy(entry => entry.Item3)) {
                    Out.WriteLine($"--- pair {a}/{b} at gap {distance} (margin {margin})");
                    Report(a);
                    Report(b);

                    // Every texel where the two are adjacent.
                    for (var y = 1; y < resolution - 1; y++) {
                        for (var x = 1; x < resolution - 1; x++) {
                            if (map[(y * resolution) + x] != a) {
                                continue;
                            }

                            for (var dy = -1; dy <= 1; dy++) {
                                for (var dx = -1; dx <= 1; dx++) {
                                    if (map[((y + dy) * resolution) + x + dx] == b) {
                                        Out.WriteLine($"    touch: {a}@({x},{y}) next to {b}@({x + dx},{y + dy})");
                                    }
                                }
                            }
                        }
                    }
                }

                Out.WriteLine("--- whole-island agreement between the two rasterizers");

                foreach (var placement in thick) {
                    var island = islands[placement.Island];
                    var texels = placement.Scale * resolution;
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

                    var x = (int) MathF.Round(spot.X);
                    var y = (int) MathF.Round(spot.Y);
                    var alone = PackedAtlas.Rasterize(islands, [placement], resolution, Int2.Zero, out _);
                    var oracleOnly = 0;
                    var maskOnly = 0;
                    var both = 0;

                    for (var ay = 0; ay < resolution; ay++) {
                        for (var ax = 0; ax < resolution; ax++) {
                            var cell = (placement.Rotation & 3) switch {
                                1 => new Int2(ay - y, x + h - 1 - ax),
                                2 => new Int2(x + w - 1 - ax, y + h - 1 - ay),
                                3 => new Int2(y + w - 1 - ay, ax - x),
                                _ => new Int2(ax - x, ay - y)
                            };

                            var inMask = cell.X >= 0 && cell.Y >= 0 && cell.X < w && cell.Y < h
                                && coverage[(cell.Y * w) + cell.X] != 0;

                            var inOracle = alone[(ay * resolution) + ax] >= 0;

                            if (inMask && inOracle) {
                                both++;
                            } else if (inMask) {
                                maskOnly++;
                            } else if (inOracle) {
                                oracleOnly++;
                            }
                        }
                    }

                    if (oracleOnly > 0 || maskOnly > 0) {
                        Out.WriteLine(
                            $"  island {placement.Island} rot={placement.Rotation}: both={both} "
                            + $"oracleOnly={oracleOnly} maskOnly={maskOnly}"
                        );
                    }
                }

                Out.WriteLine("--- the packer's own coverage at the same cells");

                Mask(26, 108, 152);
                Mask(32, 107, 151);

                void Mask(int index, int ax, int ay) {
                    var island = islands[index];
                    var placement = placements.First(candidate => candidate.Island == index);
                    var texels = placement.Scale * resolution;
                    var coverage = IslandMask.Rasterize(island, texels, 1 << 20, out var w, out var h, out _);
                    var padX = w - (island.Size.X * texels);
                    var padY = h - (island.Size.Y * texels);

                    var spot = (placement.Rotation & 3) switch {
                        1 => new Vector2((placement.Offset.X * resolution) - padY, placement.Offset.Y * resolution),
                        2 => new Vector2((placement.Offset.X * resolution) - padX, (placement.Offset.Y * resolution) - padY),
                        3 => new Vector2(placement.Offset.X * resolution, (placement.Offset.Y * resolution) - padX),
                        _ => new Vector2(placement.Offset.X * resolution, placement.Offset.Y * resolution)
                    };

                    var x = (int) MathF.Round(spot.X);
                    var y = (int) MathF.Round(spot.Y);

                    var cell = (placement.Rotation & 3) switch {
                        1 => new Int2(ay - y, x + h - 1 - ax),
                        2 => new Int2(x + w - 1 - ax, y + h - 1 - ay),
                        3 => new Int2(y + w - 1 - ay, ax - x),
                        _ => new Int2(ax - x, ay - y)
                    };

                    var i = cell.X;
                    var j = cell.Y;

                    var set = i >= 0 && j >= 0 && i < w && j < h && coverage[(j * w) + i] != 0;

                    Out.WriteLine(
                        string.Create(
                            CultureInfo.InvariantCulture,
                            $"  island {index} rot={placement.Rotation} mask {w}x{h} spot=({spot.X:F4},{spot.Y:F4})->({x},{y}) "
                            + $"atlas({ax},{ay}) -> cell({i},{j}) coverage={set}"
                        )
                    );
                }

                Out.WriteLine("--- which triangle claims each touching texel");

                Claimer(26, 108, 152);
                Claimer(32, 107, 151);

                void Claimer(int index, int tx, int ty) {
                    var island = islands[index];
                    var placement = placements.First(candidate => candidate.Island == index);

                    for (var triangle = 0; triangle < island.TriangleCount; triangle++) {
                        var a = placement.Apply(island, island.Coordinates[(triangle * 3) + 0]) * resolution;
                        var b = placement.Apply(island, island.Coordinates[(triangle * 3) + 1]) * resolution;
                        var c = placement.Apply(island, island.Coordinates[(triangle * 3) + 2]) * resolution;
                        var alone = new int[resolution * resolution];

                        Array.Fill(alone, -1);

                        var probe = PackedAtlas.Rasterize(
                            [new UvIsland([
                                island.Coordinates[(triangle * 3) + 0],
                                island.Coordinates[(triangle * 3) + 1],
                                island.Coordinates[(triangle * 3) + 2]
                            ], [0, 1, 2], island.Minimum, island.Maximum, island.Scale)],
                            [placement with { Island = 0 }],
                            resolution,
                            Int2.Zero,
                            out _
                        );

                        if (probe[(ty * resolution) + tx] < 0) {
                            continue;
                        }

                        Out.WriteLine(
                            string.Create(
                                CultureInfo.InvariantCulture,
                                $"  island {index} texel ({tx},{ty}) claimed by triangle {triangle}: "
                                + $"({a.X:R},{a.Y:R}) ({b.X:R},{b.Y:R}) ({c.X:R},{c.Y:R})"
                            )
                        );
                    }
                }

                Out.WriteLine("--- coverage of the touching texels, by supersampling");

                Coverage(26, 108, 152);
                Coverage(32, 107, 151);

                void Coverage(int index, int tx, int ty) {
                    var island = islands[index];
                    var placement = placements.First(candidate => candidate.Island == index);
                    var inside = 0;
                    const int Samples = 256;

                    for (var sy = 0; sy < Samples; sy++) {
                        for (var sx = 0; sx < Samples; sx++) {
                            var point = new Vector2(
                                tx + ((sx + 0.5f) / Samples),
                                ty + ((sy + 0.5f) / Samples)
                            );

                            for (var triangle = 0; triangle < island.TriangleCount; triangle++) {
                                var a = placement.Apply(island, island.Coordinates[(triangle * 3) + 0]) * resolution;
                                var b = placement.Apply(island, island.Coordinates[(triangle * 3) + 1]) * resolution;
                                var c = placement.Apply(island, island.Coordinates[(triangle * 3) + 2]) * resolution;

                                if (Inside(point, a, b, c)) {
                                    inside++;

                                    break;
                                }
                            }
                        }
                    }

                    Out.WriteLine(
                        string.Create(
                            CultureInfo.InvariantCulture,
                            $"  island {index} covers {inside * 100.0 / (Samples * Samples):F4}% of texel ({tx},{ty})"
                        )
                    );
                }

                static bool Inside(Vector2 point, Vector2 a, Vector2 b, Vector2 c) {
                    var d1 = Cross(point - a, b - a);
                    var d2 = Cross(point - b, c - b);
                    var d3 = Cross(point - c, a - c);
                    var negative = d1 < 0 || d2 < 0 || d3 < 0;
                    var positive = d1 > 0 || d2 > 0 || d3 > 0;

                    return !(negative && positive);
                }

                static float Cross(Vector2 u, Vector2 v) => (u.X * v.Y) - (u.Y * v.X);

                Out.WriteLine("--- true continuous distance between the offending pairs");

                foreach (var (a, b, _) in seen) {
                    Out.WriteLine(
                        string.Create(
                            CultureInfo.InvariantCulture,
                            $"  {a}/{b}: {Distance(a, b):F6} texels"
                        )
                    );
                }

                float Distance(int a, int b) {
                    var best = float.MaxValue;

                    foreach (var (pa, pb) in Edges(a).SelectMany(_ => Edges(b), (x, y) => (x, y))) {
                        best = MathF.Min(best, Segments(pa.Item1, pa.Item2, pb.Item1, pb.Item2));
                    }

                    return best;
                }

                List<(Vector2, Vector2)> Edges(int index) {
                    var island = islands[index];
                    var placement = placements.First(candidate => candidate.Island == index);
                    var edges = new List<(Vector2, Vector2)>();

                    for (var triangle = 0; triangle < island.TriangleCount; triangle++) {
                        var pa = placement.Apply(island, island.Coordinates[(triangle * 3) + 0]) * resolution;
                        var pb = placement.Apply(island, island.Coordinates[(triangle * 3) + 1]) * resolution;
                        var pc = placement.Apply(island, island.Coordinates[(triangle * 3) + 2]) * resolution;

                        edges.Add((pa, pb));
                        edges.Add((pb, pc));
                        edges.Add((pc, pa));
                    }

                    return edges;
                }

                static float Segments(Vector2 p, Vector2 q, Vector2 r, Vector2 s) =>
                    MathF.Min(
                        MathF.Min(PointToSegment(p, r, s), PointToSegment(q, r, s)),
                        MathF.Min(PointToSegment(r, p, q), PointToSegment(s, p, q))
                    );

                static float PointToSegment(Vector2 point, Vector2 a, Vector2 b) {
                    var direction = b - a;
                    var lengthSquared = direction.LengthSquared();

                    if (lengthSquared < 1e-12f) {
                        return (point - a).Length();
                    }

                    var t = Math.Clamp(Vector2.Dot(point - a, direction) / lengthSquared, 0f, 1f);

                    return (point - a - (direction * t)).Length();
                }

                Out.WriteLine("--- offsets in texels");

                foreach (var placement in thick.OrderBy(entry => entry.Island)) {
                    var ox = placement.Offset.X * resolution;
                    var oy = placement.Offset.Y * resolution;
                    var integral = Math.Abs(ox - MathF.Round(ox)) < 1e-4f && Math.Abs(oy - MathF.Round(oy)) < 1e-4f;

                    Out.WriteLine(
                        string.Create(
                            CultureInfo.InvariantCulture,
                            $"  [{placement.Island}] offset {ox:F4},{oy:F4} integral={integral} rot={placement.Rotation}"
                        )
                    );
                }

                void Report(int index) {
                    var recipe = recipes[index];
                    var island = islands[index];
                    var placement = placements.First(candidate => candidate.Island == index);
                    var texels = island.Size * placement.Scale * resolution;

                    // How many texels this island actually covers on its own.
                    var alone = PackedAtlas.Rasterize(islands, [placement], resolution, Int2.Zero, out _);

                    Out.WriteLine(
                        string.Create(
                            CultureInfo.InvariantCulture,
                            $"  [{index}] {recipe} bboxTexels={texels.X:F3}x{texels.Y:F3} "
                            + $"scale={placement.Scale:R} rot={placement.Rotation} "
                            + $"offset={placement.Offset.X:R}/{placement.Offset.Y:R} "
                            + $"covered={PackedAtlas.Covered(alone)}"
                        )
                    );
                }
            },
            seed: "1XF0jvSmVt13",
            iter: 1,
            threads: 1
        );

        File.WriteAllText("/private/tmp/claude-501/-Users-jiu-Projects-Vixen--claude-worktrees-adoring-raman-80fd04/7a2a98d1-9e77-41ec-bd5a-57d012f05ac4/scratchpad/dump.txt", Log.ToString());
    }
}

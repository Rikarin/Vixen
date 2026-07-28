# Vixen.Benchmarks.Navigation

What a bake costs, what one agent's worth of thinking costs, and — the number the frame loop actually
depends on — how much a crowd allocates per update.

```bash
./build.sh Benchmark --configuration Release
```

or, for one of them:

```bash
dotnet run -c Release --project Benchmarks/Vixen.Benchmarks.Navigation -- --filter '*Crowd*'
```

The level is a square floor with a grid of pillars eight metres apart, so a path across it turns
several times. A benchmark over an empty floor would measure a single polygon and a function call.

## What it measures, and why each one is here

| | |
|---|---|
| `BakeBenchmarks` | The content-build cost, at two cell sizes and two level sizes, single-tile against tiled. This is the number that decides whether rebaking a tile is something an editor can do while somebody drags a crate about. |
| `QueryBenchmarks` | Nearest-polygon, A\*, the funnel over its result, and a raycast across the level. One agent changing its mind costs a `FindPath`; one agent walking costs a `FindStraightPath` over a corridor it already has. |
| `CrowdBenchmarks` | A whole frame for 16, 64 and 256 agents, with avoidance on and off — because avoidance is the term that scales with density rather than with population. |

**Allocated is the column to read first.** Every query and crowd case is expected to be **0 B**;
`NavigationAllocationTests` is the gate that fails the build if it is not, and this is where the same
paths get a time beside the zero. Baking is exempt on purpose: it is a tool operation, and buying a
slower bake to avoid a collection nobody is present for is the wrong trade.

## What it measured

Apple M-series, .NET 10, medians. Medians rather than means because several cases have a long tail —
a background collection lands inside an iteration — and the mean follows it while the median does not.

**One agent thinking**, on a floor with pillars eight metres apart:

| | 40 m level | 80 m level | Allocated |
|---|---|---|---|
| `FindNearestPoly` | 315 ns | 903 ns | **0 B** |
| `FindPath` | 2.78 µs | 12.8 µs | **0 B** |
| `FindStraightPath` *(includes the search)* | 3.01 µs | 14.1 µs | **0 B** |
| `Raycast` across the level | 94 ns | 93 ns | **0 B** |

**A whole crowd frame**, on the 80 m level:

| Agents | Avoidance off | Avoidance on | Allocated |
|---|---|---|---|
| 16 | 12.7 µs | 33 µs | **0 B** |
| 64 | 44 µs | 313 µs | **0 B** |
| 256 | 255 µs | 1.59 ms | **0 B** |

**A bake**, which is a build step rather than a frame:

| Level | Cell size | One tile | Tiled | Allocated (one tile) |
|---|---|---|---|---|
| 40 m | 0.3 | 2.59 ms | 3.49 ms | 1.37 MB |
| 40 m | 0.2 | 5.34 ms | 9.16 ms | 2.85 MB |
| 80 m | 0.3 | 11.4 ms | 13.7 ms | 5.46 MB |
| 80 m | 0.2 | 25.1 ms | 30.7 ms | 11.4 MB |

## What the numbers say

**Zero bytes, in every query and every crowd frame.** That is the frame-loop non-negotiable, measured
at 256 agents rather than at the scale a unit test reaches.

**Avoidance is the whole cost of a crowd, and it grows with density rather than with population.**
Turning it off is 6.6× faster at 256 agents and only 2.6× at 16, because the sampler scores each
candidate velocity against every neighbour and a denser crowd has more of them. 256 agents at 1.6 ms
is a frame's worth of budget for a crowd nobody has yet asked for; the obvious lever, when somebody
does, is a neighbour cap rather than fewer samples.

**Halving the cell size roughly doubles the bake and doubles its garbage**, as the voxel count says it
should. A tile bake costs 20–40 % more than the same level in one piece, which is the margin each tile
voxelises outside itself — and what it buys is that a rebuild after somebody moves a crate touches one
tile instead of the level.

**A raycast does not care how big the level is.** It walks polygons along a line, and the line in this
test crosses about the same number of them either way; the search beside it is 4.5× slower on the
bigger level because it expands an area rather than a line. That is the argument for
`PathCorridor.Optimize`, which spends a raycast to avoid a search.

## Reading the numbers

Timings here are for comparison against one another and against the next run on the same machine.
Absolute values move with the level, the cell size and whatever else the machine is doing — the run
recorded below was taken with the repository's own build and test loop idle, because an earlier one
taken while the solution was compiling produced confidence intervals wider than its means.

A separate trap, and the reason the first numbers taken here were wrong by an order of magnitude: a
hand-rolled loop of a hundred iterations measures **tier-0** code. The funnel appeared to cost 20 µs
per call that way and costs 2.6 µs once it has been tiered up. BenchmarkDotNet handles this; the quick
`Stopwatch` in a test does not.

Licensed under Apache-2.0.

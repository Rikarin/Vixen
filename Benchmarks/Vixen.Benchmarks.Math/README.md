# Vixen.Benchmarks.Math

```bash
dotnet run -c Release --project Benchmarks/Vixen.Benchmarks.Math -- --filter '*'
```

## What this found

The SIMD paths in `Vixen.Core.Mathematics` were written on the assumption that they were faster.
The first run of this project said otherwise:

| | Before | After | |
|---|---|---|---|
| `Multiply` vectorised vs scalar | **1.68× slower** | 2.83× faster | |
| `TransformVector4` vectorised vs scalar | **2.43× slower** | 1.27× faster | |

The cause was reading matrix rows through the `Row1`…`Row4` properties: four field reads assembled
into a `Vector4` and then reinterpreted as a `Vector128<float>`. That reads like a free
reinterpretation and compiles to a gather, so the "vectorised" path was doing all the scalar loads
*plus* the shuffles. Loading sixteen bytes directly out of the matrix, and broadcasting lanes with
`Vector128.Shuffle` rather than `Vector128.Create(row.X)`, is what the code does now.

This is the entire argument for the benchmark existing. The code was correct throughout — the tests
were green before and after — it was merely slower than the fallback it was written to replace, and
nothing but a measurement was ever going to say so.

## Measured on an Apple M-series, .NET 10, `--job short`

| Benchmark | Mean | vs scalar |
|---|---|---|
| `Multiply` — scalar | 10.89 ns | 1.00 |
| `Multiply` — `Vector128` | 3.86 ns | **0.36** |
| `Multiply` — `System.Numerics` | 2.97 ns | 0.27 |
| `TransformVector4` — scalar | 2.15 ns | 1.00 |
| `TransformVector4` — `Vector128` | 1.69 ns | **0.79** |
| `Invert` | 13.78 ns | |
| `Decompose` | 12.84 ns | |
| Bulk transform, 16 384 points — one at a time | 88.9 µs | 1.00 |
| Bulk transform, 16 384 points — `TransformPositions` | 17.3 µs | **0.19** |

Everything allocates zero bytes.

Two things worth reading off that table. **`System.Numerics` is still 30% faster at multiply**, which
is a fair result against code hand-tuned by people who do this full time, and is the number to beat
if anyone comes back to this. And **the bulk transform earns its separate entry point** — 5.1× at
16 384 points, which is the size a cull actually sees.

Numbers from one machine are a sanity check, not a gate. The Nuke `Benchmark` target exports JSON for
a baseline comparison; per [doc 12](../../docs/plan/12-build-ci-and-testing.md) the timing gate runs
nightly rather than per-PR, because shared CI runners are too noisy to fail a build on.

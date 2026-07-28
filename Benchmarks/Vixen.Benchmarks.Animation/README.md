# Vixen.Benchmarks.Animation

```bash
dotnet run -c Release --project Benchmarks/Vixen.Benchmarks.Animation
```

Three questions, one class each.

**`SamplingBenchmarks`** — what a clip costs to sample, against how many keys its tracks carry. The
key index (`AnimationClip`) claims the cost is flat in the key count; this is where that claim is
either true or is not. `Random` seeks rather than plays forwards, which is the case a per-instance
cursor would be worst at and this design is indifferent to.

**`CrowdBenchmarks`** — a world of characters through `AnimationSystem`, inline against the job
scheduler. The parallel path is worth having above some crowd size and not below it; sixteen
characters is in the benchmark specifically to find where that crossover is.

**`CompressionBenchmarks`** — what key reduction removes from a clip an exporter emitted a key a
frame for, and what it costs to run. It is a build-time pass, so the interesting column is the ratio
rather than the time.

The rigs are in `Rigs.cs`: sixty-four joints with branching, a key per frame at thirty hertz. A
three-joint chain would measure the loop and nothing else.

Licensed under Apache-2.0.

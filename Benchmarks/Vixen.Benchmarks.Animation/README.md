# Vixen.Benchmarks.Animation

```bash
dotnet run -c Release --project Benchmarks/Vixen.Benchmarks.Animation
```

Five questions, one class each.

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

**`MoveSelectionBenchmarks`** — what it costs to pick a move out of a set, against how big the set is
and whether the query filters. `docs/plan/34` puts a five-microsecond bar on five hundred entries;
this is where that is met or missed, and at the time of writing the unfiltered case misses it.

**`ConstraintStageBenchmarks`** — what the constraint stage costs a game that is not using it.
`Empty` has to be indistinguishable from `Bare`, because the frame gained a pass before evaluation
that ships doing nothing and the only defensible answer to "what does the hook cost" is *nothing you
can measure*. `Solving` is beside them so the difference between the hook and the feature is a
number: on an M1 Max, a hundred characters run 501 µs bare, 484 µs with the empty stage, and 891 µs
with two solved goals each. `SolvingWithFewShapes` and `SolvingWithManyShapes` are a pair to
read together — 8 proxy shapes against 120, same goals — because shapes are posed lazily and this is
where that is either true or is not. `Ungoverned` and `Governed` are the other pair: four hundred
goals a frame, and the same four hundred put through a budget of a hundred and fifty. A budget nobody
measures is a budget nobody has to meet.

The rigs are in `Rigs.cs`: sixty-four joints with branching, a key per frame at thirty hertz. A
three-joint chain would measure the loop and nothing else.

Licensed under Apache-2.0.

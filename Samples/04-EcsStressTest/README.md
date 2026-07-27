# 04 — ECS stress test

The whole of Phase 2 at once, with the exit criteria measured rather than asserted.

```bash
dotnet run -c Release --project Samples/04-EcsStressTest -- --roots 2000 --frames 10000
```

A console program with no window, because Phase 2 renders nothing — the picture is Phase 4's. What
it shows is the shape of a frame at scale.

## What it does

`--roots N` entities on a circle, each with four children, so a `--roots 20000` run is 100 000
entities across 8 archetypes with a two-level hierarchy. An `OrbitSystem` in `Update` moves every
root; `TransformSystem` in `PreRender` picks up exactly the chunks it wrote and propagates to the
children. A `SceneManager` owns all of it, so the last thing the run does is unload the scene and
check that nothing is left.

## What it measured

Apple M-series, .NET 10, Release.

| | 10 000 entities | 100 000 entities |
|---|---|---|
| Build | 37 ms | 265 ms |
| Frame, mean | 514 µs | 5.7 ms |
| Allocated per frame | 161 B | 165 B |
| **Gen0 collections** | **0 over 10 000 frames** | **0 over 2 000 frames** |

The Phase 2 exit criterion — *a 10 k-entity scene with a transform hierarchy at zero Gen0
collections over 10 000 frames* — is the third row of the first column.

The build cost is ~3.7 µs per entity against the ~70 ns a bare `Create` measures, and the difference
is the hierarchy: `SetParent` is two archetype moves and a depth walk, and `Adopt` is another. That
is the honest cost of building a tree one node at a time, and it is exactly what `Prefab` exists to
avoid — which is why a prefab instantiates by archetype rather than by entity.

## What is not here

Nothing is drawn. `DebugDraw` and the diagnostic overlays from
[docs/plan/13](../../docs/plan/13-diagnostics.md) are owed, and they need a renderer to draw
through.

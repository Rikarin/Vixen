# Vixen.Benchmarks.Ecs

The operations [docs/plan/04](../../docs/plan/04-ecs-and-scripting.md) § Tests names — create,
destroy, get, set, iterate, and an archetype move — at the scale the Phase 2 exit criterion sets.

```bash
./build.sh Benchmark --configuration Release
```

or, for one of them:

```bash
dotnet run -c Release --project Benchmarks/Vixen.Benchmarks.Ecs -- --filter '*Iterate*'
```

## What it measured, and what it changed

Apple M-series, .NET 10, `--job short`. Numbers are for **100 000 entities** of
`Position + Velocity + Health`.

| | ns / entity | note |
|---|---|---|
| `IterateChunksBySpan` | 0.50 | spans, loop bounded by `span.Length` |
| `IterateVisitor` | 0.52 | generated struct-visitor dispatch |
| `IterateDelegate` | 0.52 | generated delegate dispatch |
| `IterateChunksByCount` | 0.67 | spans, loop bounded by `chunk.Count` |
| `Get` / `Set` | 6.6 / 7.1 | by entity handle — the indirection the chunk forms avoid |
| `Create` | 70 | includes allocating the chunks |
| `AddThenRemove` | 123 | two archetype moves |

Two findings, both of which changed code rather than being written down and left:

**The obvious chunk loop is the slow one.** `for (i = 0; i < chunk.Count; i++)` bounds the loop by a
number the JIT cannot connect to either span, so both indexers keep their bounds check — 34% slower
than the generated per-entity forms, which is the opposite of what the design claims. Bounding by
`positions.Length` and slicing the other span to match removes both checks and makes it the fastest
form by 4%. Both are benchmarked side by side, because the difference is invisible in the source and
users will write the first one.

**Create was building a `ComponentSignature` per entity** — allocate, sort, de-duplicate, hash — for
a set that was fixed when the call site was compiled. 129 ns per entity, most of it that. The
archetype is now remembered per combination of type parameters, and create is **46% faster** (70 ns)
and allocates 30% less.

Both were found by running this, which is the argument for it existing.

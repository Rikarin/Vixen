# Vixen.Net.Generators

The build half of networking: the code that would otherwise have to be written by hand for every
replicated component and kept in step with it.

Spec: [docs/plan/16-networking.md](../../docs/plan/16-networking.md) § The IL-weaving problem.

## Why this is generated rather than reflected

Two reasons, and either would be enough.

**It cannot be reflected.** iOS is NativeAOT, so there is no run-time code generation to fall back on,
and reflecting over a struct's fields is precisely what trimming removes. Everything about a
component's layout has to be decided at build time.

**It cannot be woven.** ADR-002 bans IL post-processing, which is how the reference implementation
does the equivalent job. That constraint pushed the RPC design somewhere better — a visible
`Rpc.Method(...)` call site instead of a method that silently becomes a packet — and it pushes
replication here.

What is left is a generator, and a generator is a good place for it: the emitted replicator is
ordinary C# on disk, steppable in a debugger, and readable by whoever is trying to work out why a
field did not arrive.

## What it emits

For every `[Replicated]` struct, an `IComponentReplicator`:

```csharp
[Replicated(Channel = Channel.Unreliable, Priority = 10)]
public struct Position {
    [Quantize(-1000f, 1000f, 16)] public float X;
    [Quantize(-1000f, 1000f, 16)] public float Y;
    public bool Grounded;
}
```

becomes a class with the stable wire id (FNV-1a of the full type name), the change-filtered query the
replication loop uses, and `Write`/`Apply` that pack the fields — 16 bits, 16 bits, 1 bit, rather than
three whole values. Plus one `ReplicatedComponents.RegisterAll(registry)` per assembly, which is the
closed set nothing outside can add to.

The claim that it "emits what a careful person would have written" is a test rather than a slogan:
`Vixen.Net.Generators.Tests` declares a component, hand-writes the replicator for it, and asserts the
two produce **the same bits** from the same values.

## Diagnostics

| Code | Meaning |
|---|---|
| `VXNET1001` | A replicated field has a type that cannot be put on the wire. |
| `VXNET1002` | `[Quantize]` is on a field that is not a `float`. |
| `VXNET1003` | `[Quantize]` declares a width outside 1–32, or a range that does not go upwards. |
| `VXNET1004` | A replicated component has no public fields, so every snapshot of it is empty. Warning. |

An error emits nothing for that component. A page of errors inside generated code the author cannot
see buries the one line that is actually wrong — the same rule the VXML generator follows, for the
same reason.

## Incrementality

The per-component step produces a finished `string` of source, so the cache compares text: editing an
unrelated file re-runs nothing, and editing one component re-emits that component alone. Only the
registration file depends on the *set* of components, and only on the set — adding a field to one does
not invalidate it. `EditingSomethingElseReRunsNothing` asserts it against the reasons Roslyn records,
because a generator that is incremental in name only looks exactly like one that is not.

## Owed

- **RPC senders and the manifest.** The `Rpc.Method(...)` accessor pattern, the dispatch table and the
  hashed RPC ids need an RPC runtime — attributes, a dispatcher, ownership checks against
  `NetworkRules` — which is not built yet. The replicator half is here because replication is.
- **Packaging.** Today a project takes this generator through a `ProjectReference` with
  `OutputItemType="Analyzer"`. Travelling inside the `Vixen.Net` package, the way `Vixen.Ui` carries
  its generators through a `build/*.targets`, is the arrangement to copy and is not done.

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

Two things. For every `[Replicated]` struct, an `IComponentReplicator`:

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

It also emits the **wire layout** — the widths of those fields and which of them arithmetic means
something for — which is all the runtime's `DeltaCodec` needs to send a value as a difference from the
last one the far end had. Deliberately no generated delta code: differencing is a transform between
bit streams, so there is one implementation of it that every component shares and one property test
over random layouts, rather than a `WriteDelta` per component and the hope that they all agree. The
layout and `Write` come from the same field list in the same order, and a test asserts they add up to
the same number of bits — the server checks the same two numbers at run time and falls back to whole
records if they ever disagree, so getting it wrong costs bandwidth rather than correctness.

The claim that it "emits what a careful person would have written" is a test rather than a slogan:
`Vixen.Net.Generators.Tests` declares a component, hand-writes the replicator for it, and asserts the
two produce **the same bits** from the same values.

And for every type declaring `[ServerRpc]`/`[ClientRpc]` handlers, a nested `Rpc` accessor with one
sender per handler, the dispatch table behind `IRpcInvoker`, and the manifest entry:

```csharp
[ServerRpc(RequireOwnership = true)]
void TakeDamage(int amount) => _health -= amount;
```

gets a `Rpc.TakeDamage(int)` beside it that encodes the arguments and hands them to the router. The
table is sorted by hashed id at build time, so two builds number the calls the same without having to
agree on declaration order — and a peer that has not been rebuilt fails the manifest hash at the
handshake rather than routing an old index to a new handler.

A handler may take an `in RpcContext` as its first parameter. It is not read from the wire; the router
fills it in from the connection the bytes arrived on, which is the difference between knowing who
called and asking them.

## Diagnostics

| Code | Meaning |
|---|---|
| `VXNET1001` | A replicated field has a type that cannot be put on the wire. |
| `VXNET1002` | `[Quantize]` is on a field that is not a `float`. |
| `VXNET1003` | `[Quantize]` declares a width outside 1–32, or a range that does not go upwards. |
| `VXNET1004` | A replicated component has no public fields, so every snapshot of it is empty. Warning. |
| `VXNET2001` | A remote call takes an argument of a type that cannot be sent. |
| `VXNET2002` | A type declaring remote calls is not `partial`. |
| `VXNET2003` | A type declaring remote calls does not implement `IRpcObject`. |
| `VXNET2004` | A remote call *handler* returns something. A handler is one way; a request/response is `RpcRouter.CallAsync<T>`, which is built and is not this. |
| `VXNET2005` | A handler is marked as both a `ServerRpc` and a `ClientRpc`. |
| `VXNET2006` | A type declaring remote calls is nested, generic, or not a class. |
| `VXNET2007` | `[Quantize]` is on an argument that is not a `float`. |

An error emits nothing for that component. A page of errors inside generated code the author cannot
see buries the one line that is actually wrong — the same rule the VXML generator follows, for the
same reason.

⚠ **`VXNET2004` is not a gap, and its message used to read as one.** It said awaitable calls were
"designed for and not built"; `RpcRouter.CallAsync<T>` has since been built, and the diagnostic is
still right. A `[ServerRpc]` handler that returns a value is a different thing from a call that
awaits an answer: the handler is invoked by the router with no caller to return to, and the answer
travels back as its own correlated message. So the rule stands and the sentence explaining it does
not.

## Incrementality

The per-component step produces a finished `string` of source, so the cache compares text: editing an
unrelated file re-runs nothing, and editing one component re-emits that component alone. Only the
registration file depends on the *set* of components, and only on the set — adding a field to one does
not invalidate it. `EditingSomethingElseReRunsNothing` asserts it against the reasons Roslyn records,
because a generator that is incremental in name only looks exactly like one that is not.

## The wire corpus

`Vixen.Net.Generators.Tests/Wire` holds what the generator's output encodes to, as committed bytes —
`generated-records`, `generated-registry`, `generated-snapshot` and `generated-rpc`. It lives here
rather than beside `Vixen.Net.Tests/Wire` because this is the only test project the generator runs
in: it is referenced as an `Analyzer` as well as a reference, so the code being pinned is emitted
while the assembly compiles. `UPDATE_GOLDEN=1` regenerates the listings, and every line of the diff
is a wire format change.

⚠ **A differential is blind to anything that moves both halves at once.**
`TheGeneratedReplicatorWritesExactlyWhatAHandWrittenOneWrites` compares the generated replicator with
a hand-written one compiled from the same tree in the same build — so a change underneath *both*
changes the wire and leaves the comparison green. And the hand-written half only ever existed for one
of the three components; what held the other two, and every RPC in the assembly, was a length. Two
sabotages measured the gap rather than assumed it: swapping the type and method varints in
`RpcRouter.BeginCall` **and** in its receive path — a silent, symmetric break that mis-dispatches
every call against a peer built a day earlier — left all 348 tests in `Vixen.Net.Tests` and 41 of the
42 here green; folding `ReplicationRegistry.ManifestHash`'s bytes the other way round did the same.
Both are the corpus's alone to catch, because the manifest hashes a handshake refuses a peer over
were asserted only to be non-zero.

## Owed

Nothing outstanding. ⚠ **Packaging is done and this section used to say it was not**: the generator
travelled only through a `ProjectReference` with `OutputItemType="Analyzer"`, so every in-tree
consumer named it itself and was green while a game restoring the *package* got no
`ReplicatedComponents` and no `RpcMethods` at all, with no error. It is packed by
`Vixen.Net.csproj`'s `PackNetGenerators` target now and asserted over the `.nupkg` bytes by
`Vixen.Net.Tests.PackagedGeneratorTests`.

⚠ **And that assertion did not run on its own until 2026-09-03, which is the second instrument bug
in the same two tests.** `PackNetGenerators` asks this project for `GetTargetPath` and `Vixen.Net`
deliberately does not `ProjectReference` it — so nothing in `Vixen.Net.Tests`' build graph built the
generator, and `dotnet pack -c Debug` failed `NU5019: File not found: Vixen.Net.Generators.dll` on
any tree where something else had not already built it. The suite therefore read green in a
whole-solution run and red on its own, and the message read as the packaging target being broken
rather than as a missing build step. `Vixen.Net.Tests.csproj` now carries a
`ReferenceOutputAssembly="false"` edge to this project — build order and nothing else, because
`OutputItemType="Analyzer"` would run the generator over the assembly that hand-writes the
specification it is checked against. (The first was `Directory.GetFiles("Vixen.Net.[0-9]*.nupkg")`,
which matches no file because that method understands `*` and `?` and nothing else.)

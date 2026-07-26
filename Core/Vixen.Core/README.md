# Vixen.Core

The engine's root assembly. It depends on nothing but the BCL, and everything else in Vixen
depends on it, so what goes in here is only what genuinely has no better home: the vocabulary
subsystems need to talk to each other.

## What is here

| | |
|---|---|
| **Annotations** | `[DataContract]`, `[DataMember]`, `[DataMemberIgnore]`, `[DataAlias]`, `[Component]`, `[HotPath]`, `[EditorVisible]`, `[Category]`, `[Range]`, `[Tooltip]` — marker types that source generators read at compile time. They carry no runtime behaviour. |
| **Identity** | `AssetId`, `ObjectId`, `EntityId`, `ComponentTypeId` — value types with span formatting and parsing, so writing an id never allocates a string. |
| `GameTime` | What a frame is told about the clock: scaled and unscaled deltas, the running total, the frame number. |
| `ServiceRegistry` | A flat, typed lookup for the handful of genuinely global services. Not a DI container — see below. |
| **Pooling** | `ObjectPool<T>`, `PooledArray<T>`, `PooledList<T>`, `PooledDictionary<K,V>` — the rentals that keep the frame loop from allocating. |
| `DisposeBag` | Reverse-order teardown of everything a subsystem owns, with failures aggregated rather than swallowed. |
| `LeakTracker` | Debug-build tracking of resources the GC cannot see, with allocation stacks. Compiled out of release builds. |

## Two things that are deliberate

**`ServiceRegistry` is not a container.** No constructor injection, no lifetime scopes, no
auto-wiring, no reflection. Subsystems take their dependencies as constructor parameters and the
bootstrapper wires them by hand. The registry exists for the few services that would otherwise
thread through every signature. This is what Stride and Unity's DOTS both settled on, and it is
the only shape that works under NativeAOT.

**`ObjectId` has no hash function.** It is 128 bits of identity and nothing else. The algorithm
that produces those bits (XxHash128, over a serialised chunk) lives with the object database in
`Vixen.Core.Serialization`, which is what keeps this assembly free of package references.

## Namespaces

The root vocabulary — annotations, ids, time, services, disposal — is in `Vixen.Core` itself, so a
consumer needs one `using` to write a component. Coherent sub-areas get a sub-namespace:
`Vixen.Core.Pooling`.

Licensed under Apache-2.0.

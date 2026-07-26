# 03 — Core Foundation

Everything above this layer is only as good as this layer. This is where the "zero allocation in the
frame loop" promise is either kept or lost.

## `Vixen.Core`

The smallest possible root. No dependencies beyond BCL.

- **Attributes/annotations.** `[DataContract]`, `[DataMember]`, `[DataMemberIgnore]`,
  `[DataAlias]` (Stride's model — it works and it survives refactors), plus `[Component]`,
  `[HotPath]`, `[EditorVisible]`, `[Category]`, `[Range]`, `[Tooltip]`. These are marker types read
  by source generators; they carry no runtime cost.
- **`ServiceRegistry`.** A flat, typed, non-hierarchical container: `Add<T>(T)`, `Get<T>()`,
  `TryGet<T>()`, backed by a generated switch or a `Dictionary<Type, object>` populated at boot.
  Explicitly **not** a DI container — no constructor injection, no lifetime scopes, no reflection.
  Subsystems take their dependencies as constructor parameters and the bootstrapper wires them by
  hand. This is the choice Stride made and the choice Unity's DOTS made; it is boring and it works
  under AOT.
- **Identity types.** `readonly record struct AssetId(Guid)`, `ObjectId` (128-bit content hash from
  XxHash128), `EntityId`, `ComponentTypeId` — all with `IUtf8SpanFormattable`/`ISpanParsable` so
  serialisation never allocates strings.
- **`GameTime`.** `readonly record struct` with `Total`, `Elapsed`, `FrameCount`, `UnscaledElapsed`,
  `TimeScale`. Fixed-step accumulator lives in `Vixen.Engine`, not here.
- **Pooling.** `ObjectPool<T>` (thread-local free list + shared overflow), `ArrayPool<T>` façade with
  clearing policy, `PooledList<T>`/`PooledDictionary<K,V>` as `ref struct`-friendly rentals with
  `using` disposal.
- **Disposal.** `IDisposable` plus `IAsyncDisposable`; a `DisposeBag` for subsystem teardown; a
  debug-build leak tracker that captures allocation stacks for undisposed GPU resources.

> ✅ **Built.** `Core/Vixen.Core/` and `Core/Vixen.Core.Tests/` (86 tests) are live. Four things
> came out differently from the paragraphs above, each for a reason worth keeping:
>
> - **`ObjectId` carries no hash function.** It is 128 bits of identity, formatting, parsing and
>   ordering — nothing else. XxHash128 lives in `System.IO.Hashing`, a NuGet package, and taking it
>   would break "no dependencies beyond BCL" for a type that does not need the algorithm: the code
>   that hashes has the *content* in front of it, and that code is the object database in
>   `Vixen.Core.Serialization`. Bytes are big-endian so the hex text and `WriteTo` agree, which is
>   what makes ids comparable across machines.
> - **`ObjectPool<T>` is a lock-free fast slot in front of a fixed slot array**, which is Roslyn's
>   design, rather than the thread-local free list with shared overflow described above. A genuine
>   per-pool thread-local needs either a `ThreadLocal<T>` — allocating per thread *per pool*, and the
>   engine will have many pools — or a `[ThreadStatic]` field, which is per *type* and so cannot
>   serve two pools of the same `T`. The shape here has the same property that mattered (uncontended
>   fast path, bounded retention) and costs one field.
> - **`ComponentTypeId` is assigned from 1, not 0**, so a zeroed struct is a detectably invalid
>   handle instead of a silent alias for whichever component type registered first. Bit 0 of an
>   archetype mask goes unused; that is cheaper than the class of bug it removes.
> - **The `ArrayPool<T>` façade is `PooledArray`/`PooledArray<T>`**, and the clearing policy it
>   applies is: clear on return iff the element type contains references. Not tidiness — an uncleared
>   `Entity[]` sitting in a pool roots everything it last held, while clearing `int[]` every frame is
>   pure cost.
>
> One C# detail that shaped the pooled collections, since it decides whether they can be structs at
> all: a `using` declaration makes its variable read-only, but calling a mutating *method* on it is
> still allowed and mutates in place — no defensive copy. Direct member assignment
> (`map[k] = v` on a `using var`) is CS1654, a hard error rather than a silent copy. So
> `using var list = new PooledList<T>(64); list.Add(x);` is correct, and the failure mode is caught
> by the compiler.

## `Vixen.Core.Mathematics`

Per ADR-003. Implementation notes that matter:

- **Layout.** `[StructLayout(LayoutKind.Sequential)]`, `readonly record struct`, fields not
  properties (`public readonly float X;`) so `ref` returns and `Unsafe.As` reinterpretation are legal
  and free. `record struct` gives value equality, `Deconstruct`, and `with` for free — but override
  `Equals`/`GetHashCode` by hand for float types to control epsilon semantics (the compiler-generated
  ones use `EqualityComparer<float>` which is bitwise; that is the correct default for hashing but we
  need `NearEqual` as a separate explicit method, never as `==`).
- **SIMD.** `Vector4`/`Matrix4x4` operations go through `Vector128<float>`/`Vector256<float>` with
  `if (Vector128.IsHardwareAccelerated)` fast paths and scalar fallbacks. Matrix multiply, transform,
  normalize, dot, cross, frustum-vs-AABB, and the bulk `TransformMany` helpers are the ones that
  matter; benchmark each.
- **Bulk operations are first-class.** `static void Transform(ReadOnlySpan<Vector3> src, in Matrix4x4 m, Span<Vector3> dst)`
  exists alongside the scalar version, because culling and skinning call it a million times a frame.
- **Conventions doc.** A single `Conventions.md` in the project stating handedness, matrix storage,
  multiplication order, depth range (reverse-Z, 1→0), UV origin (top-left), and NDC. Every
  disagreement about a sign flip gets settled by pointing at it. The shader half is already settled
  and pinned by tests — including why row-major host storage and a `ColMajor`-decorated shader matrix
  are the same bytes and compose to `mul(v, M)`, which is the one that looks wrong every time somebody
  meets it: [07 § E](07-raven-shader-pipeline.md#e-conventions-raven-must-bake-in). `Conventions.md`
  should link there rather than restate it.
- **Interop.** `implicit operator System.Numerics.Vector3(Vector3)` and back;
  `Silk.NET.Maths.Vector3D<float>` conversions in a separate `Vixen.Graphics` internal extension so
  the math library does not reference Silk.NET.

Tests: CsCheck property tests (`(a*b)*c ≈ a*(b*c)`, `Inverse(m)*m ≈ I`, quaternion↔matrix round-trip,
frustum classification against a brute-force reference), plus golden values captured from HLSL for
anything Raven-facing.

## `Vixen.Core.Memory`

- **`NativeArray<T> where T : unmanaged`** — `NativeMemory.AlignedAlloc`-backed, `Span<T>` accessor,
  optional bounds checking under `DEBUG`, disposal-tracked. This is the storage primitive for ECS
  chunks, vertex staging, and layout node arrays.
- **`ArenaAllocator`** — bump allocator with frame reset. Two instances live for the whole process:
  `FrameArena` (reset every frame, used for render command payloads, culling results, layout scratch)
  and `TempArena` (scope-based, `using var scope = TempArena.Push()`). Both are thread-local with a
  per-thread block chain.
- **`GpuUploadRing`** — persistent-mapped ring buffer with frame-fenced regions, the single path for
  per-frame constant/instance data. Sized from a config; overflow logs and grows once, then errors.
- **Sub-allocator** for device memory: buddy allocator over 256 MB heaps, with dedicated allocations
  above a threshold, `VK_EXT_memory_budget`-aware. Reused by D3D12 placed resources.
- **Leak/lifetime tracking.** `DEBUG`/`VIXEN_MEMORY_DEBUG` builds record an allocation ID + captured
  stack per native allocation and report on shutdown. Release builds compile it out entirely.

## `Vixen.Core.Collections`

Purpose-built, all `struct`-friendly, all with `Span<T>` access:

| Type | Used for |
|---|---|
| `SparseSet<T>` | entity→component index, dirty sets, selection sets |
| `ChunkedArray<T>` | ECS chunk storage, stable references under growth |
| `SmallList<T, TBuffer>` (`InlineArray` buffer) | descriptor slots, child lists ≤ N, avoids heap for the common case |
| `BitSet` / `FixedBitSet<N>` | archetype masks, render group masks, layout dirty flags |
| `RobinHoodDictionary<K,V>` | hot lookups (asset URL → content, style key → computed style) |
| `PriorityQueue<T>` (indexed, decrease-key) | job graph, animation events, timeline |
| `RingBuffer<T>` | log ring, profiler samples, input event queue |
| `FreeList<T>` | handle allocation with generation counters |
| `Handle<T>` / `HandlePool<T>` | typed, generation-checked resource handles — the RHI's public currency |

`Handle<T>` deserves emphasis: the RHI never exposes reference types for GPU resources. A
`BufferHandle` is `readonly record struct BufferHandle(uint Index, uint Generation)`. Use-after-free
becomes a detected generation mismatch instead of a native crash, and resource tables stay contiguous
and cache-friendly.

## `Vixen.Core.Threading` — the job system

This is the load-bearing concurrency decision. Stride uses `MicroThreading` (cooperative coroutines
over a scheduler); Unity uses a job system with dependency handles and a safety system. **Vixen uses
a job system**, because it composes with `Span<T>`, works without a custom scheduler in the language,
and is what the renderer and ECS actually need.

**Design.**

- `N-1` persistent worker threads (N = `Environment.ProcessorCount`), pinned where the OS allows,
  with per-thread Chase–Lev work-stealing deques. One thread is the main thread and participates.
- `JobHandle` is a `readonly record struct` over an index into a generation-checked job table.
  `Combine(params ReadOnlySpan<JobHandle>)`, `Complete()`, `IsCompleted`.
- Jobs are `struct`s implementing `IJob { void Execute(); }` or
  `IJobParallelFor { void Execute(int index); }`, dispatched through a generic
  `Schedule<TJob>(in TJob job, JobHandle dependsOn)` — generic specialisation means **no boxing, no
  delegate, no closure**. This is Unity's design and it is correct; the generator-free version is
  possible in modern C# where it was not when Unity built theirs.
- `ParallelFor` with automatic batch sizing and work stealing at the batch level.
- **A dependency graph, not a barrier soup.** `JobHandle` edges form a DAG resolved by atomic
  dependency counters; completing a job decrements successors and pushes ready ones.
- **Main-thread affinity queue.** Some work must run on the main thread (GL context calls, platform
  window APIs, .NET Hot Reload apply). `MainThreadDispatcher.Post(...)` drains at defined frame
  points.
- **Debug-mode race detection.** Under `VIXEN_JOB_SAFETY`, jobs declare read/write access to
  `NativeArray` handles and the scheduler asserts no two concurrent jobs write the same region. This
  is Unity's safety system, and it catches the class of bug that otherwise costs weeks. Compiled out
  in release.
- **Profiler integration.** Every job emits begin/end samples with its type name (from
  `typeof(TJob).Name`, resolved once into a cached interned string), so the frame graph in the editor
  shows real names without runtime reflection cost.

**Explicitly not used:** `Task`/`ThreadPool` for frame work (the .NET thread pool's hill-climbing
heuristics fight a frame deadline), `Parallel.For` (delegate allocation, no dependency model),
`async`/`await` in the loop.

> ✅ **Built.** `Core/Vixen.Core.Threading/` and its 45 tests are live, and
> `Benchmarks/Vixen.Benchmarks.Jobs` measures the claims: one `Schedule`+`Complete` round trip is
> 6.3× cheaper than `Task.Run`+`Wait` and allocates nothing against its 160 bytes, and
> `ScheduleParallel` beats `Parallel.For` by 2.2–2.6× above about ten thousand elements — and loses
> to a plain serial loop below about a thousand, which is written down next to the rest.
> Four things differ from the paragraphs above:
>
> - **A lock per job slot, not a lock-free continuation list.** Adding a graph edge to a job that is
>   completing at that instant is the whole difficulty here, and the lock-free form needs a CAS loop,
>   an ABA guard, and a heap-allocated link node per edge. An uncontended lock — the scheduling thread
>   against one completing worker — makes the graph's correctness readable instead of arguable, and a
>   frame's few hundred edges are not where the time goes.
> - **Failures outlive their slot.** A slot returns to the free list the moment its job finishes, so
>   it can no longer answer "did that throw" — and that answer must not depend on how promptly the
>   caller asked. The last 64 failures move to a side table, which is what both `Complete` and an
>   edge added after the fact read. A job whose dependency threw inherits the failure and is skipped
>   rather than run against inputs that were never produced.
> - **Workers are not pinned.** `Thread` has no portable affinity API, the per-OS ones differ in kind
>   rather than in spelling, and pinning is a pessimisation on a machine running anything else. It
>   waits for `Vixen.Platform`, where the per-OS calls will already live.
> - **The safety system is deferred to Phase 2, with `Vixen.Ecs`.** The check described above is only
>   as good as the access declarations, and in Unity's design those come from the ECS. Building the
>   declaration API before its only consumer exists would be guessing at its shape. What *is*
>   compiled in under `DEBUG` or `VIXEN_JOB_SAFETY` is the check that needs nothing else: a job that
>   completes its own handle is caught and told so, instead of waiting forever for the work item that
>   is doing the waiting.

## `Vixen.Core.IO` — the virtual file system

Modelled on Stride's VFS because the problem is unchanged: six platforms with six different notions
of "where files are", plus content that lives inside bundles, plus editor-time content that lives on
disk.

- **Virtual paths** are `/`-separated, case-sensitive, with mount points:
  - `/app/` — read-only application content (APK assets, iOS bundle, wwwroot)
  - `/data/` — read-write app data (per-platform correct location)
  - `/cache/` — evictable
  - `/temp/`
  - `/project/` — editor only: the open project's folder
  - `/db/` — the object database (content-addressed)
- **`IFileProvider`** implementations: physical FS, Android `AAssetManager`, iOS bundle, browser
  `fetch` + IndexedDB cache, in-memory (tests), bundle-backed (read-only, from the ODB).
- **Async-first API** returning `ValueTask<Stream>`; sync overloads exist for editor code and are
  banned in runtime hot paths by analyzer.
- **Memory-mapped reads** where the platform supports it, with a `ReadOnlyMemory<byte>` façade so the
  serializer can read without copying.
- **File watching** (`Vixen.Core.IO.Watch`) with per-platform backends (`FSEvents`, `inotify`,
  `ReadDirectoryChangesW`), debounced, coalesced, and delivered on the main thread. Hot reload for
  both assets and UI markup depends on this being *reliable*, which means: handle atomic-save
  rename-over patterns, handle editors that write-truncate-write, and never fire on our own writes.

> ✅ **Built.** `Core/Vixen.Core.IO/` and its 123 tests are live. Four things differ from the
> paragraphs above:
>
> - **No per-platform watch backends.** `FileSystemWatcher` is already FSEvents, inotify and
>   `ReadDirectoryChangesW` behind one type, maintained by people who have to keep it working on OS
>   versions that do not exist yet. What the BCL does *not* do is the part that makes watching
>   usable, so `FileChangeCoalescer` is where the work went: debouncing that extends rather than
>   expires, atomic-save renames folded into one change to the destination, created-then-deleted
>   cancelled out, and the program's own writes suppressed. Time is a parameter rather than a clock,
>   so every one of those is tested at exact timestamps instead of with sleeps.
> - **Case-sensitivity is enforced by the provider, not only by CI.** [Doc 10](10-platforms.md)
>   assigns this to a Linux CI check. That is a backstop measured in hours; `PhysicalFileProvider`
>   makes it a backstop measured in milliseconds by refusing to serve a file whose real name on disk
>   differs in case. The volume is probed once at construction so the check is off where the kernel
>   already does it.
> - **Enumeration is synchronous and ordered.** Async was dropped because every provider that exists
>   or is planned answers enumeration from something local — a directory, a dictionary, a bundle
>   catalog — so the state machine would have had no caller. Ordering was added because a content
>   build that hashes a directory listing must not get a different answer on ext4 than on APFS.
> - **Memory-mapped reads decline rather than throw.** A file above two gigabytes has no
>   `ReadOnlyMemory<byte>`, and a file inside a compressed APK entry has no mapping at all, so
>   `TryMap` returning false is an ordinary answer and callers fall back to a stream.
>
> **Deferred:** the Android, iOS, browser and bundle providers, each of which arrives with the thing
> it reads from; and the analyzer banning `System.IO.Path` and synchronous IO outside their permitted
> layers, which needs an analyzer project that does not exist yet.

## `Vixen.Core.Serialization`

- **Generated binary serializers.** `Vixen.Core.Serialization.Generators` walks `[DataContract]`
  types and emits `Serialize(ref SerializationStream, ref T)` methods. No reflection, no
  `Reflection.Emit`, no Cecil. Versioning via `SerializedVersion` on the contract plus `[DataAlias]`
  for renamed members.
- **`SerializationStream`** is a `ref struct` over `Span<byte>` for in-memory and a chunked writer
  for streams; primitives, `Span<T>` bulk copies for blittable arrays, LEB128 for lengths.
- **Content references.** `ContentReference<T>`/`UrlReference<T>` serialise as a URL + type and
  resolve through `Vixen.Assets`. The `AttachedReference` trick from Stride (an asset loaded at
  edit-time carrying its identity so it can re-serialise as a reference) is worth reproducing —
  it is what makes editor round-tripping of asset graphs work.
- **Chunked, content-addressed storage.** An `ObjectDatabase` maps `ObjectId` → blob, with a
  `FileOdbBackend` (loose files, editor) and a `BundleOdbBackend` (packed `.bundle`, runtime), exactly
  Stride's split. Chunks carry a header with the serializer type ID and references list, so loading
  is: read header → load referenced chunks → deserialise.
- **Compression.** LZ4 for local bundles (decode speed), Zstd for downloadable bundles (size), raw for
  already-compressed payloads (BCn/ASTC textures, Ogg). Per-chunk choice recorded in the header.

Tests: round-trip property tests over generated types, schema-evolution tests (v1 writes, v2 reads,
and vice versa with `[DataAlias]`), and a cross-platform determinism test asserting byte-identical
output on Windows/Linux/macOS runners.

> ✅ **Built.** `Core/Vixen.Core.Serialization/`,
> its generator, and 28 tests are live. What differs from the paragraphs above:
>
> - **The reader is span-only; there is no chunked stream writer.** That is the deliberate pair with
>   `Vixen.Core.IO`'s memory mapping: a bundle is mapped rather than read, so "the whole file in a
>   span" costs no copy and the pages nobody asked for are never faulted in. Writing does grow, via
>   `IBufferWriter<byte>`.
> - **Evolution is a member count, not a tagged format.** Every object writes two varints — contract
>   version and member count — and that is the whole mechanism. Appending a member is free in both
>   directions and needs no version bump; removing or reordering one is refused with a message naming
>   the numbers. A version bump means "the layout changed incompatibly" and sends the reader to a
>   `TryMigrate` hook. Two bytes an object, against a name tag per member.
> - **`[DataAlias]` on a *member* does not apply to this format.** Positional is smaller and faster,
>   and the count already covers the case that matters, so member names are not in the stream. Member
>   aliases are for the YAML serializer, where names *are* the format.
> - **No `partial` is required.** Serializers are emitted as standalone classes in their own
>   namespace rather than into the contract type, which costs reaching only public members and buys
>   not having an opinion about how every type in the engine is declared.
> - **Polymorphism is detected and refused rather than silently truncating.** Writing a derived
>   instance through a base serializer throws. Doing it properly needs a type-name table, and that is
>   the same map `Vixen.Core.Reflection` has to build anyway.
>
> - **Chunk compression is outside the hashed region.** The id names the chunk — header plus payload
>   — and the compression framing wraps it afterwards, so two builds that disagree about whether to
>   LZ4 a mesh still agree about what it is called. Without that, changing a compression setting
>   would invalidate every artefact in the project and every incremental update would ship
>   everything. Compression that would grow a chunk is not used, which is what every already-
>   compressed texture payload does.
>
> **Deferred:** `ContentReference<T>`/`UrlReference<T>`, which need `Vixen.Assets` in Phase 3; and the
> catalog and bundle packing *policy* — which chunks go in which bundle — which is the content build's
> job in [08](08-asset-pipeline-and-addressables.md). The bundle *format* is built and tested.

## `Vixen.Core.Reflection`

The AOT-safe replacement for `Type`-driven discovery.

- A source generator emits, per assembly, a `VixenTypeRegistry` partial class listing every
  `[DataContract]`/`[Component]`/`[Behavior]`/`[EditorVisible]` type with its ID, members, attribute
  values, factory delegate, and serializer.
- At boot, each assembly's generated `Module.Initialize()` (via `[ModuleInitializer]`) registers into
  a global registry. `[ModuleInitializer]` replaces Stride's Cecil-injected module initialisers
  cleanly.
- Editor code that genuinely needs open-ended reflection (plugin loading, third-party assemblies) uses
  `System.Reflection` freely — it is editor-only and JIT-hosted.

## `Vixen.Core.Syntax`

Extracted from Raven (see [02](02-repository-layout.md)). Provides:

- `GreenNode` (immutable, width-cached, shared across trees), red `SyntaxNode` (lazy parent-linked
  wrapper), `SyntaxToken`, `SyntaxTrivia`, `SyntaxList<T>`, `SeparatedSyntaxList<T>`.
- A `Syntax.xml` → generated-node-classes source generator (Raven's `SyntaxGenerator`, generalised).
- `TextSpan`/`TextLine`/`SourceText` with incremental change tracking (`SourceText.WithChanges`)
  and a `TextChangeRange`-driven incremental reparse entry point.
- `Diagnostic`/`DiagnosticDescriptor`/`DiagnosticBag` with severity, location, and message args —
  one diagnostics model for Raven, VXML, and VCSS, so the editor's error list has one implementation.

Incremental reparse is the reason this is shared: hot-reloading a 2 000-line `.vxml` in under 200 ms
means reparsing only the changed subtree, and that machinery is identical across the three languages.

## `Vixen.Core.Imaging`

Runtime-safe image handling, distinct from ImageSharp (which is import-time only).

- `Image` / `ImageDescription` / `PixelBuffer` — the engine's texture representation, mip chains,
  array slices, cube faces, matching `Vixen.Graphics` formats 1:1.
- **Decoders needed at runtime**: KTX2 (the shipping texture container), DDS (legacy interop), and
  nothing else. Runtime never decodes PNG/JPEG for game content.
- **Encoders needed at build time**: BC1–BC7 (desktop), ASTC (mobile), ETC2 (GLES fallback), with
  the encoder either bound natively (`ispc_texcomp`, `astcenc`) or managed. Native is the right call —
  ASTC encoding is measured in minutes/GB in managed code.
- Mip generation, sRGB-correct downsampling, alpha-weighted mips, normal-map renormalisation,
  cubemap prefiltering (GGX importance sampling for IBL) and SH irradiance projection. The last two
  are also needed at runtime for dynamic reflection probes, so they exist in both a CPU and a compute
  form.

## `Vixen.Core.Diagnostics`

See [13](13-diagnostics.md) for the full treatment. Structurally: `ILogger` plumbing, a
`ProfilerScope` ring recorder, `System.Diagnostics.Metrics` counters, and a trace exporter.

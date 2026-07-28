# Vixen.Shaders

The engine's side of the shader contract: **typed keys** for naming a shader's parameters, and
**constant-buffer writers** whose offsets were computed by the shader compiler rather than here.

Raven ([docs/plan/07](../../docs/plan/07-raven-shader-pipeline.md)) compiles a `.rvn` shader and
reports its reflection — descriptor sets, member offsets, permutation keys. `Vixen.Shaders.Generators`
reads that reflection at build time and emits a `…Keys` class and a `…Constants` struct per shader.
This project is what the generated code is written against.

## The shape

```csharp
// Generated from Lighting.rvn — no reflection, no name lookup, no layout arithmetic.
var constants = new LightingConstants {
    WorldViewProjection = camera.ViewProjection,
    Ambient             = new Vector3(0.1f, 0.1f, 0.12f),
    Exposure            = 1.4f,
    LightCount          = visible.Count,
};

constants.Write(buffer);                       // every offset baked at build time

for (var i = 0; i < visible.Count; i++) {
    new LightingLightsElement {
        Position  = visible[i].Position,
        Range     = visible[i].Range,
        Color     = visible[i].Color,
        Intensity = visible[i].Intensity,
    }.Write(buffer, i);
}
```

## Why the offsets are copied and never recomputed

The numbers in the generated writer come out of the same `ShaderLayout` pass that told the GLSL and
SPIR-V emitters where to put things. Computing them here would be a **second implementation of
std140**, and the two would eventually disagree — silently, because every byte still lands inside the
buffer. The symptom of that class of bug is "setting the ambient colour resets the exposure", three
subsystems from the cause.

Three cases where the CLR's layout and the shader's genuinely differ, and the writers exist to know
the difference:

| Shape | The trap |
|---|---|
| `float3` followed by `float` | std140 packs both into one 16-byte slot. A `Vector4`-shaped write clears the scalar |
| `bool` | four bytes in a block, not one. Writing one leaves three bytes of whatever was there, and a non-zero one of them is a `true` nobody set |
| `mat3` | three columns of 12 bytes in 16 of space. The only matrix that has to be taken apart |
| `float[4]` | 64 bytes, not 16 — std140 rounds an array's element stride up to 16 whatever the element is |

And one case where they deliberately do **not**: a `Matrix4x4` is a straight blit. The engine stores
row-major with the translation in `M41..M43`; the shader reads the same bytes as `ColMajor`, which
makes its matrix the host's transpose — which is exactly what `mul(v, M)` needs. Transposing here
would compute the wrong transform more expensively. See
[docs/plan/07 § E](../../docs/plan/07-raven-shader-pipeline.md).

## Keys

A `ParameterKey` is **interned by name**, so equal names are the same object and a key costs a
pointer compare as a dictionary key. Two assemblies that generated bindings from the same shader get
the same key rather than two that merely look alike.

`PermutationKey<T>` is a different type from `ParameterKey<T>` rather than a flag on it, because the
two are consumed by different machinery and confusing them is expensive in one direction: setting a
permutation at draw time does nothing until something recompiles, and making a value key a
permutation multiplies the shader cache for no gain. Raven reports which is which — a `[Permutation]`
field is not a uniform — so the generator never guesses.

Registering one name as two types, or as both a value and a permutation, throws at creation. The
alternative is writing four bytes of the wrong interpretation into a correctly sized slot: a value
that is wrong rather than absent, which is the harder kind to notice.

A key also carries its default **as bytes**, not only as a typed value. `ParameterCollection.Get<T>`
has always promised that a collection which never mentions a key yields what the shader author
declared; `DefaultBytes` is the same promise for the code that copies rather than reads. A buffer
writer filling only the keys somebody set gives `var exposure: float = 1f` the value zero, which is a
black frame produced by a parameter nobody touched.

That default is real all the way down. A uniform's initialiser never runs anywhere — the block
arrives already filled — so `= 1f` was only ever a statement about what a *host* should put there,
and until Raven carried it, lowering dropped it on the floor. It now reaches `ParameterInfo`, the
generator spells it as a literal, and the key holds its bytes. Break any link in that chain and the
symptom is a black frame that nothing reports.

## Parameters, effects and the cache

**Two ways to fill one uniform block, because two callers want different things.** Code that knows
the shader at compile time gets a `…Constants` struct: assign fields, call `Write(Span<byte>)`, no
lookups. Code that knows it only by *name* — a material read from an asset, a post-process node
configured by a compositor document — has no generated type to assign to, and gets a `ParameterKey`
per value in the block to set through a collection. Before those existed, the name-driven path
interned its keys from strings, which works and gives up every guarantee interning exists for.

`ParameterCollection` is what a material, a view or a draw holds: one packed byte buffer with an
offset per key, rather than a dictionary of boxed values. The thing it is asked thousands of times a
frame is "give me the bytes for these parameters", and a `Dictionary<key, object>` answers that with
a boxing allocation and a pointer chase per parameter.

**Values and permutations are kept apart**, because they are consumed at different times: a value is
written into a buffer every frame it changes, while a permutation decides *which shader exists* and
changing one is a recompile. Two version counters follow from that, and a material re-asserting its
settings each frame does not look like a shader change.

Neither counter moves when a key is set to what it already holds. That is what makes a version mean
"something changed" rather than "something was assigned" — and it is load-bearing for anything that
reconfigures itself every frame, such as a post-process chain, which would otherwise re-upload a
constant buffer in which nothing had moved.

`EffectKey` is the cache key — a shader name plus the permutations **the shader actually branched
on**. Raven's `UsedPermutationKeys` is what makes that possible, and it is the difference between a
tractable cache and 2ⁿ entries where a handful are distinct. Values are sorted by name so the same
settings in a different order are the same key; without that normal form the cache holds one entry
per insertion order and hits almost never — a miss that shows up as a frame-time cliff rather than a
wrong image.

A key carries a third thing, and it is not a permutation: the **`ShaderComposition`** — which shader
fills each of the pass's `compose` slots. A permutation decides which branch of one shader survives; a
composition decides which shaders the compilation contains at all, so two materials with the same name
and the same permutations but different features are different code. A key blind to that returns the
first one compiled for both, which is a metal-roughness object drawn with a specular-glossiness shader
and nothing logged anywhere. Same normal form as the values, and empty for the shaders that declare no
slots — every post effect and the depth-only pass — so their keys and their cache filenames are exactly
what they were before compositions existed.

`EffectSystem` resolves a key to an `Effect`, asking each `IEffectProvider` in turn and remembering
the answer. That interface is the seam that makes **"zero runtime shader compilation" structural
rather than aspirational**: a shipping build supplies a provider backed by the baked bundle and never
references the compiler, so it *cannot* compile a shader — not because a flag says so, but because
the code was never linked in. It is also what makes the remote compiler a provider rather than a
special case.

A key nothing can supply is recorded rather than hidden, so doc 06's "no runtime compilation in
shipping" can be a **test**: run a playthrough against the bundle alone and assert the miss list is
empty. `Requests` is the other half of that — every key a run asked for, hit or miss, which is what an
`EffectManifest` is dumped from and what the build is then told to produce.

## The tiers below the dictionary

`IEffectProvider` answers with an `Effect`, which is a thing on a device. `IEffectSource` answers with
an **`EffectData`**, which is a thing on a disk or a wire — and that distinction is what lets the
tiers compose: a disk cache that missed can ask the dev machine and *write down what came back*,
which it could not do if the answer were already a set of device handles. Sources stack; one
`EffectSourceProvider` at the top turns whatever the stack produced into an effect. A shipping
build's stack is one deep.

| | |
|---|---|
| `EffectStore` | Variants in memory, indexed by key. What an `EffectBundle` becomes when a shipping build loads it |
| `EffectDiskCache` | Read-through, write-back over a directory, keyed by (key, target), with the source hash checked rather than named — a runtime asking for a variant does not know what the shader hashed to |
| `RemoteEffectSource` | A dev machine on the other end of a socket. See `Tools/Vixen.ShaderCompilerService` |
| `EffectLoader` | The one step that needs a device: descriptor set layouts and a pipeline layout, with the layouts shared between effects that describe the same set |

`EffectData` exists because Raven's own `.rvnfx` cannot be read without the compiler:
`CompiledEffectReader` lives in `Vixen.Raven`, so a runtime that read one would link the parser, the
lowerer and both backends. Translating a `.rvnfx` into this happens once, on the build side, in
`Tools/Vixen.ShaderCompiler` — the only project allowed to know both.

The load-bearing agreement is the **parameter type**. A key is interned by name and carries a CLR
type; `Vixen.Shaders.Generators` picks that type from Raven's reflection at build time, and
`EffectLoader` picks it from a `ShaderValueKind` stored in a file. They agree or the interning table
throws naming both — which is the good failure. The bad one would be two keys for one offset, and a
value set through the generated one landing nowhere.

`Effect` carries bytecode and layout rather than a pipeline, because a pipeline also depends on the
vertex layout, the render pass and the blend state: one effect backs many pipelines, and keying
pipelines by effect alone is a cache that returns an object drawn with the wrong blend mode.

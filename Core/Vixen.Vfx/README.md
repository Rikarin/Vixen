# Vixen.Vfx

Particle systems as compiled graphs. Stride's particle runtime with Unity VFX Graph's authoring
model, which is what [doc 06](../../docs/plan/06-rendering-pipeline.md) § VFX pipeline asks for.

```csharp
var graph = VfxCompiledGraph.Compile(
    spawners:     [VfxSpawner.AtRate(60f)],
    initializers: [
        new(VfxOpcode.PositionInSphere,        new Vector4(0f, 0f, 0f, 0.2f)),
        new(VfxOpcode.VelocityRandomDirection, new Vector4(2f, 4f, 0f, 0f)),
        new(VfxOpcode.SetLifetime,             new Vector4(1f, 2f, 0f, 0f)),
    ],
    updaters: [
        new(VfxOpcode.Gravity, new Vector4(0f, -9.81f, 0f, 0f)),
        new(VfxOpcode.Integrate),
    ],
    capacity: 4096);

using var system = new VfxSystem(graph, seed: 12345);

system.Step(deltaTime);
```

## What is here

| | |
|---|---|
| `VfxAttribute` | The per-particle quantities, as bit positions so a declaration is a mask. |
| `ParticleBuffer` | Struct-of-arrays storage in native memory, one array per attribute the graph touches. |
| `VfxOperation`, `VfxOpcode` | One node of a compiled graph: an opcode and two `Vector4`s of parameters. |
| `VfxCompiledGraph` | The artefact both backends read. Derives storage, assigns salts, refuses graphs that read what nothing writes. |
| `VfxSimulation` | The CPU backend: one operation swept across every particle. |
| `VfxRandom` | Stateless integer-only hashing, so a compute shader can reproduce a value exactly. |
| `VfxSystem` | One running instance: its particles, its clock, its seed, its spawner state. |
| `VfxRenderer` | How particles are drawn — alignment, sorting — and which attributes that reads. |
| `VfxGeometryBuilder` | Particles into camera-facing quads, and the draw order. |
| `VfxShaderEmitter` | The same compiled graph as a Raven compute shader: the GPU backend's front half. |

## The compiled graph is data, and that is the whole design

Doc 06 says the dual-target compilation "needs to be designed in from the start rather than
retrofitted", so it was the first thing decided. The compiled form is **an array of fixed-size
operations** — an opcode, a salt, and two `Vector4`s. Not a delegate tree, which a shader cannot be
compiled from. Not shader source, which a CPU cannot run. Not an object graph, which neither can be
uploaded.

What that shape buys: the CPU backend is a loop over it, a GPU backend is a translation of it, it goes
into a constant buffer verbatim, it serialises without a visitor, and two graphs can be compared for
equality in a golden test. Nothing in a `VfxOperation` is a pointer, a delegate or an index into
anything else.

The cost is that the node set is closed — a genuinely novel node needs an opcode. That is the right
cost while the library is small enough to enumerate, and the alternative is lowering to
add/multiply/select over a register file, which means both backends need a register allocator.

**Two `Vector4`s and no more**, deliberately. An operation that needs more parameters than that is one
that should be two operations.

## One operation across every particle, not the other way round

`VfxSimulation` sweeps per *operation*: apply gravity to all of them, then integrate all of them. The
opcode is dispatched once per frame instead of once per particle, each sweep touches one or two
attribute arrays end to end, and the inner loop has no branch in it.

The other order — walk the particles, run the whole graph on each — reads better and is wrong. It puts
a `switch` inside the hot loop and pulls a whole particle into cache to change four bytes of it. It is
also the order that would make the GPU backend a redesign instead of a translation, because sweeping
per operation is what a compute dispatch already looks like from the inside.

## Storage is derived, and an unused attribute has no memory

`VfxCompiledGraph.Attributes` is the union of what the operations read and write, computed at compile
time from a table on the opcode. An author cannot forget to declare an attribute and cannot declare one
nothing uses, because there is nowhere to say either.

An attribute no operation touches is not a zeroed array and not a null check per access — it is
`NativeArray<T>.Empty`, allocated never. An effect that does not rotate its particles pays nothing for
rotation, and `BytesPerParticle` will tell you what it does pay.

**A graph that reads what nothing writes is refused at compile time.** An updater integrating velocity
in a graph whose initializers never set velocity would run over zeroed memory and produce an effect
that looks like it does nothing — the worst kind of failure, because it has no symptom to search for.
`Compile` throws instead, naming the attribute.

## Randomness a shader can reproduce

The exit criterion for Phase 7 is a VFX graph producing *identical* output on the CPU and GPU paths.
Identical, not close — so `VfxRandom` is built to be reproducible rather than merely random.

**Stateless.** A generator with state has to be carried per particle and advanced in visit order,
which on a GPU is no order at all. A value here is a pure function of what it is for: the particle's
identifier, the effect's seed, and a per-use salt. Any thread can compute any particle's value without
having computed anybody else's.

**Integer operations only.** Every step is a 32-bit multiply, xor or shift, all exact and identical
everywhere. A hash that reached for a float multiply or a sine would agree to within a tolerance, and a
tolerance is not what a golden image wants. The single conversion to float takes 24 bits over 2²⁴,
which is every float in [0, 1) with an exact representation and nothing else — dividing by
`uint.MaxValue` instead would ask the hardware to round, and rounding mode is the thing shader
compilers are allowed to differ on.

**Zero is not a fixed point.** A mixer built from xor-shifts and multiplies maps zero to zero at every
step, so particle zero of seed zero would draw zero for ever and the first particle of an effect would
be the one that looked wrong. An offset by the golden ratio's reciprocal before mixing removes it.
There is a test.

**The salt is what keeps two uses apart.** Without one, "random size" and "random lifetime" would be
the same number and every big particle would live longest — a correlation that looks like art direction
and is not. `Compile` assigns salts from each operation's position, four apart, because the widest
operation here draws three consecutive values.

**Randomness follows the identifier, never the slot.** A particle keeps its identifier for life; it
keeps its slot only until something ahead of it dies. Hashing the slot would silently re-roll a
particle's size and lifetime partway through its life, and a test spawns a hundred particles through a
recycling buffer to prove it does not.

Uniform *by volume* and *by solid angle* where that matters: a position in a sphere takes the cube root
of its radius fraction, or two thirds of the particles pile into the outer third and it reads as a
shell; a direction samples `z` uniformly rather than the polar angle, or a burst pinches at the poles.

## The other target

`VfxShaderEmitter.Emit(graph)` turns the same compiled graph into Raven source: a shader holding the
buffers and the helpers, and one compute entry point per pass.

```csharp
var shader = VfxShaderEmitter.Emit(graph, "Fountain");

File.WriteAllText("Fountain.rvn", shader.Source);   // then: raven compile --target spirv
```

**It is a translation, not a second implementation**, and that is the whole return on the compiled
graph's shape. Which attributes exist, what each operation reads and writes, and which salt it draws
on were all decided once in `Compile`; what is left here is spelling. The emitter is a `switch` that
writes a line of source per operation, and adding an opcode means adding a case to it and a case to
the CPU sweep — never a design.

**The order is inverted, and it is the one real difference.** `VfxSimulation` sweeps per operation
across every particle. A dispatch has no inner loop to keep an opcode out of: one invocation owns one
particle and runs the whole graph on it, so every intermediate stays in registers and the buffer is
touched once at each end. Sweeping per operation on the GPU would be one dispatch per operation with a
round trip through memory between each — the same arithmetic at several times the bandwidth. Both
orders are correct because no operation reads another particle, which is a property worth having said
out loud.

**The graph is unrolled, not interpreted.** The operation array could have been uploaded and stepped
through by one shader with a `switch` in it, which would need one shader for every graph rather than
one per graph. It would also put a branch on every instruction in the hot path of the processor that
least likes them. The graph is known when the effect compiles, so it is spelled out.

**A `float3` attribute is a `float4` in the buffer.** std430 aligns a `vec3` to sixteen bytes, so an
array of them has a stride of sixteen whatever it is declared as — and a host that uploaded a packed
`Vector3[]` would read every particle after the first from the wrong offset. Declaring `float4` costs
the bytes the layout was going to spend anyway and spends them somewhere a later attribute can use,
rather than in padding nothing can name. `Bindings` reports the stride so the host never has to work
this out twice.

**It binds what the kernels touch, not what the graph stores.** An attribute only the renderer reads
is a descriptor these kernels would bind and never look at, and the identifier buffer is read-only
because nothing writes it — one access decoration, and the difference between a driver that may hoist
a load out of a loop and one that may not.

**What agrees exactly and what agrees closely.** The hash is integer arithmetic throughout and is
exact on both sides; that is what `VfxRandom` is for, and it is the part that has to be exact, because
a random value differing by one bit puts a particle somewhere else entirely. Downstream of it is
ordinary floating point, and three things there are close rather than identical: the transcendentals
(a sine, a cube root, an exponential are each accurate to a fraction of an ulp without the two
libraries having to choose the same fraction); an interpolation, since `float.Lerp` is a *fused*
`a(1-t) + bt` and whether a target contracts its `mix` the same way is the implementation's to decide;
and anything the shader compiler is free to reassociate. Where the CPU does the arithmetic itself the
emitter spells the same arithmetic — `Range` is `a + (b - a) * t` on both sides, not a `lerp` — so the
gap is only where a library call is on one side of the line. **The agreement test is therefore exact on
the hash and a tolerance on positions**, which is the honest form of that claim and not a weakening of
it: a tolerance on a value fed by an exact hash is a real check, and a tolerance on a value fed by a
different random draw would not be.

The tests compile every emitted shader with the real compiler and hand both targets to their reference
tools, because a generated shader that reads perfectly can still be a module no driver would load.
That is not hypothetical: it is how a lowering bug was found in Raven itself, where a `RWBuffer`
inherited from a base shader arrived read-only. `spirv-val` accepted the contradiction and
`glslangValidator` did not, so the effect ran on Vulkan and would not build for GL — which reads as a
backend bug and was one line in the binding merge. Both tools run, for that reason.

**What is still the CPU's:** deciding how many particles to spawn and where, and reaping the dead.
Spawning is bookkeeping rather than arithmetic and there is one right place for it. Reaping is a
choice again now that Raven has `atomicAdd` — the GPU form is every survivor taking the next slot from
a shared counter, and the value the atomic hands back is the slot — but it changes the *order* the
survivors end up in, where the CPU's swap-removal changes it differently. Neither order is promised
and a particle's randomness follows its identifier rather than its slot, so both are correct; it is
written down because "the two backends disagree about slot order" is a thing somebody will otherwise
find in a diff and take for a bug.

## Geometry stops where the graphics stack starts

`VfxGeometryBuilder` turns particles into quads and nothing more. What happens to those vertices — the
pipeline, the descriptor set, the draw — is `Vixen.Rendering.Features.ParticleRenderFeature`, which is
why `Vixen.Vfx` references no graphics at all. Rendering depends on Vfx; Vfx depends on nothing that
draws. The payoff is that every decision the expansion makes is
checked against a number instead of a screenshot: where the four corners are, which way the quad faces,
what happens when a streak is seen end-on.

**Four vertices a particle, not six.** The two triangles share an edge, and the index pattern joining
them is the same for every particle in every effect ever — so it is a buffer built once by whoever
draws, not two repeated vertices per particle forever.

**Aligned billboards turn to face the particle-to-camera vector, not the camera's forward.** Under
perspective those differ by more than a little at the edges of the view, and using forward makes a wide
effect visibly lean. When the fixed axis points straight at the camera the cross product vanishes and
the quad falls back to the camera's own right — otherwise a spark coming towards the viewer becomes a
line of zero width, which is the sort of thing that only shows up in one shot out of fifty.

**The renderer declares its reads like any other stage**, so a velocity-aligned billboard is what makes
a graph allocate velocity even when nothing in the simulation would have. And a graph with **no**
renderer allocates neither colour nor size — a simulation used to drive something else pays for nothing
it is not drawn with, and asking to draw it throws rather than quietly producing zero-area quads in a
colour nobody chose.

**Sorting is a drawing decision**, so it lives on the renderer and defaults to the one that costs
nothing: additive blending does not care about order, and a key per particle plus a sort is not free.
`Order` is exposed because a caller uploading per-instance data instead of expanded quads needs the same
order, and recomputing it would be a second sort that could disagree with the first.

## Decisions worth knowing about

**The alive set is a prefix, not a mask.** Particles live in `[0, Count)` and a dead one is removed by
copying the last live particle over it. Every sweep is then a dense loop with no per-particle branch.
The cost is that order changes as particles die — which nothing promises, and only a depth sort would
care about, and a depth sort re-orders anyway.

**Update before spawn.** A particle born this step is not also aged this step. The other order gives
every particle one step less of life than it asked for.

**Ageing first, reaping last.** A particle is updated on the step it dies and not after it. The other
way round leaves one step of an effect drawn with a particle that should already have gone.

**A full buffer refuses rather than grows.** The capacity is the budget and it is the author's to set;
the alternative is a reallocation in the middle of a frame or an emitter that stops at a threshold
nobody chose. `LastRefused` reports it, because a system at capacity is normal for a dense effect and
an authoring mistake for a sparse one, and only the author can tell which.

**Bursts are counted, not tested for.** A burst spawner works out how many bursts *should* have
happened by now rather than asking whether one falls inside this step. A step longer than the interval
would otherwise emit one burst instead of the several it covers, which is how an effect loses its shape
the first time the frame rate drops.

**Rate spawners keep their fractional debt.** Two and a half particles a second at sixty hertz is
0.0417 a frame; dropping the fraction each frame emits nothing at all, for ever.

**Drag is exponential.** `v *= exp(-k dt)` rather than `v *= 1 - k dt`: the same to first order, and it
does not go negative at a large step, which would turn a strong drag into a particle that reverses.

## Determinism, and what it is for

Two systems with the same seed and the same steps are identical particle for particle — not similar.
Nothing in the simulation reads a clock, a thread identifier, or anything the caller did not hand it. A
test runs two instances of a fountain for two seconds and compares every position, velocity and
lifetime.

That is what makes an effect reproducible in a replay, comparable in a golden-image test, and — once
the GPU backend exists — checkable against it.

## Nothing allocates once it is running

`VfxSystemTests.SteppingAllocatesNothing` warms a fountain to its working population, then runs three
hundred frames and asserts the process allocated **zero** bytes. Particle storage is `NativeArray`, the
graph is read-only and shared between instances, and the only per-instance state besides the particles
is two small arrays of spawner bookkeeping.

## What is not here yet

- **The GPU backend's back half.** `VfxShaderEmitter` writes the shader and the reference tools accept
  it; nothing has yet uploaded a particle buffer, dispatched it, or read the result back. That needs a
  device, and so does Phase 7's exit criterion — the test that the two paths agree. Until there is one,
  what is claimed is that the translation compiles and is well typed, which is a weaker statement than
  the roadmap's and the true one.
- **Mesh, ribbon and light renderers.** Only billboards so far. A mesh renderer is an instance
  transform per particle rather than a quad; a ribbon needs particles linked into strips, which is the
  one that needs something the storage does not have yet.
- **A second view of the same effect.** `ParticleRenderFeature` expands once, against one view, so a
  reflection or a shadow pass draws quads facing the wrong camera. Expanding per view is the
  workaround; the GPU path is the fix, which is why the workaround is not in.
- **The node graph and its editor.** `Vixen.Editor.VfxGraph` authors what `Compile` consumes. A graph is
  written in code today.
- **Custom attributes.** The attribute set is closed. Opening it means a name-to-slot mapping the
  compiled graph carries and both backends agree on, which is a design rather than an addition.
- **Force fields, curl noise, collision, sub-emitters, trails.** Updaters doc 06 names and this does not
  have. Each is an opcode and a sweep; collision is the one that needs something outside the module.
- **Parallel simulation.** The sweeps are single-threaded. They are the right shape for
  `IJobParallelFor` — a range of a dense array with no cross-particle dependency — and it is not worth
  scheduling until there is a particle count that needs it.

Licensed under Apache-2.0.

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
| `VfxCompiledGraph` | The artefact both backends read. Derives storage, assigns salts and slots, refuses graphs that read what nothing writes. |
| `VfxCustomAttribute` | A per-particle quantity a graph declares by name and everything downstream reaches by slot. |
| `VfxSimulation` | The CPU backend: one operation swept across every particle. |
| `VfxRandom` | Stateless integer-only hashing, so a compute shader can reproduce a value exactly. |
| `VfxNoise` | Value noise over a lattice, and the curl of three — the field turbulence samples. |
| `VfxSystem` | One running instance: its particles, its clock, its seed, its spawner state. |
| `VfxSubEmitter` | Particles that emit particles — a burst on death, on birth, or a trail. |
| `VfxRenderer` | How particles are drawn — alignment, sorting — and which attributes that reads. |
| `VfxGeometryBuilder` | Particles into quads, instance transforms or ribbon strips, and the draw order. |
| `VfxShaderEmitter` | The same compiled graph as a Raven compute shader: the GPU backend's front half. |
| `VfxShaderUniforms` | The push-constant block that shader declares, as the host writes it. |
| `VfxShaderPacking` | One attribute between `ParticleBuffer` and the bytes a storage buffer holds. |

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

## Attributes a graph declares for itself

```csharp
var graph = VfxCompiledGraph.Compile(
    spawners:     [VfxSpawner.AtRate(60f)],
    initializers: [new(VfxOpcode.RandomCustom, new Vector4(0.5f, 0f, 0f, 0f)) { B = new(2f, 0f, 0f, 0f) }],
    updaters:     [],
    capacity:     4096,
    customs:      [new("mass", VfxAttributeType.Float)]);
```

**A name to the author, a slot to everything else.** Slots are assigned by declaration order; an
operation's `Slot` is an index into that list, the storage is allocated by index, and the emitted
shader declares its buffers in the same sequence. Nothing looks a name up at run time, and the two
backends cannot come to different conclusions because neither of them decides — `Compile` does, once.
Reordering the declarations is therefore a *different graph*, which is the same rule the salts follow
and for the same reason: a compiled artefact whose meaning depended on how it was written down would
not be comparable in a golden test.

**The name has to be an identifier**, because it names a binding in the emitted shader and a host
binds by it. A custom attribute called `"particle size"` would compile perfectly on the CPU and emit a
shader that does not parse — a failure a long way from its cause, so it is refused at the declaration.

**Float lanes only, one to four.** The one unsigned quantity here is the identifier and it belongs to
the runtime; a custom integer would need its own interpolation rule, and there is none. Four lanes is
the widest because a random draw takes one salt per lane and the stride between operations is four.

**What this does not buy is an arbitrary expression over a custom attribute.** That needs the node
graph and a lowering to add/multiply/select over a register file — the cost the closed opcode set was
chosen to avoid, and named as such at the top of this file. What it buys is the three operations that
make storage useful without one: write it at birth, draw it at random, animate it over a life. The
first real consumer is the ribbon renderer, which needs to know which strip a particle belongs to and
where in it — an ordering the built-in set has no place for.

## Force fields, and why the noise is the shape it is

Gravity is the same everywhere and needs to know nothing. `Attract`, `Vortex` and `Turbulence` each
read the *position*, which is what makes them fields — and what makes `Compile` refuse a graph whose
initializers never place its particles, since a field acting on a thousand particles all at the origin
accelerates every one of them identically.

**Falloff is linear-squared to a radius, not inverse-square.** A real attractor's strength goes to
infinity at its centre, so a particle that wanders close enough leaves the scene in one step. An effect
wants a *region of influence*, which an author can reason about and place; squaring the remaining
fraction eases the edge, because a linear falloff has a discontinuous derivative at the radius and a
stream crossing it visibly kinks.

**A vortex takes the axial component out before the cross product.** Crossing the axis with the whole
offset gives a swirl that grows with height above the centre — a vortex that leans, for no reason
anybody chose. There is a test with two particles at one radius and different heights.

**Value noise, not Perlin or simplex.** Both of those need a gradient table, and a table is the one
thing the GPU side would have to be *given* rather than compute: a uniform buffer, an upload, and a way
for the two backends to disagree about its contents. Value noise needs only the hash that is already
here — the corners of the unit cell hash to numbers and the point between them is an interpolation —
so the whole field transcribes into the emitted shader as ordinary code.

**Curl, because divergence-free is what makes it read as fluid.** Sampling noise straight into a
velocity gives a field with sources and sinks: particles pile up where it points inward and thin out
where it points out. No fluid does that and the eye knows. The curl of any vector field has zero
divergence identically, which costs six extra samples per octave and buys smoke that swirls instead of
smoke that clumps. There is a test that measures the divergence numerically rather than trusting the
algebra, because the algebra is exact and the finite differences are not.

**Octaves are what hide the lattice**, and the interpolant is smoothstep because a linear one leaves a
crease at every cell boundary — visible in the *motion* long before it is visible in the noise.

**The clock is handed in, never read.** A drifting field has to know when it is, and the moment the
simulation asked an ambient clock for that, two systems with the same seed and the same steps would
stop being identical.

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

Measured on a device rather than reasoned about, that shakes out finer than the paragraph above
implies. The values that are a multiply-add over the hash — a position in a box, a lifetime, a size —
land within **one or two ulps**, because a target may contract `min + (max - min) t` into a fused
instruction that rounds once instead of twice. The one born value that goes through a sine and a
cosine lands within about **twenty**. So the agreement test holds those two apart rather than sizing
one tolerance for trigonometry and letting a real regression in the arithmetic hide inside it. Even
the tightest of them is six orders of magnitude away from what a disagreeing hash would cost, which is
why a tolerance is still the test that proves the hash agrees.

### Running it

`Vixen.Rendering.VfxGpuSimulation` is the device-side half: it allocates the buffers the emitted
shader binds, builds the descriptor set that names them, and records the dispatches. It does *not*
compile anything — turning Raven source into a module is the shader compiler's job, and a runtime that
linked one would be a runtime that ships a compiler. The caller compiles and hands over a
`PipelineHandle`.

```csharp
using var simulation = new VfxGpuSimulation(device, shader, capacity);

simulation.Upload(list, particles, count);                        // seeding, once
simulation.Initialize(list, initializeKernel, 0, count, seed, 0f);
simulation.Update(list, updateKernel, count, dt, seed, time);
simulation.Download(list, count);                                 // comparing, or debugging
```

**The five per-dispatch scalars are push constants.** Twenty bytes written into the command buffer, so
an initialize and an update can be recorded into one list with different values — which a uniform
buffer cannot do without being two buffers. It also drops a descriptor: the set holds nothing but
particle storage. `VfxShaderUniforms` is the host-side struct whose field order *is* the shader's
declaration order, and a test compiles the emitted source and compares the reflected offsets to
`Marshal.OffsetOf` member by member, because a field inserted into the middle of either one compiles
perfectly and moves everything after it.

**Upload and download are stalls and are meant to be rare.** A GPU effect exists so that particle
state never leaves the device. The two transfer paths are there for seeding a system from a CPU spawn
and for reading the result back to compare it — neither belongs in a frame, and the dispatches touch
neither.

`Platform/Vixen.Vfx.Gpu.Tests` is where the three assemblies that no shipping build links together —
the runtime, the compiler and the driver — are put in one process so the question can be asked at all.
It skips where there is no Vulkan, and `VIXEN_REQUIRE_VULKAN=1` turns that skip into a failure on the
leg whose purpose is to run it.

The tests compile every emitted shader with the real compiler and hand both targets to their reference
tools, because a generated shader that reads perfectly can still be a module no driver would load.
That is not hypothetical: it is how a lowering bug was found in Raven itself, where a `RWBuffer`
inherited from a base shader arrived read-only. `spirv-val` accepted the contradiction and
`glslangValidator` did not, so the effect ran on Vulkan and would not build for GL — which reads as a
backend bug and was one line in the binding merge. Both tools run, for that reason.

**What is still the CPU's on the device path:** deciding how many particles to spawn and where, and
reaping the dead.
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

**Four renderers, one of which needs particles to know about each other and one of which draws
nothing.** A billboard is a quad. A mesh is an instance transform — three `float4` rows, because the
fourth row of an affine transform is always the same and uploading it is sixteen bytes an instance to
say so. A **ribbon** is the odd one: it joins particles into a strip, so it has to know which strip a
particle belongs to and where in it. A **light** produces no geometry at all.

That was the thing the storage had no place for, and it is a custom attribute — the first real
consumer of them. Ordering *within* a strip is age, which is a built-in the runtime already keeps, so
a ribbon needs exactly one declared attribute and not two. Drawing one is therefore what makes a graph
allocate age, the same way a velocity-aligned billboard is what makes it allocate velocity.

**A ribbon's indices are rebuilt every frame and a quad's are not.** Two triangles a quad never depend
on anything but the count; a strip's depend on where each ribbon *ends*, and a ribbon ends wherever a
particle died. So there is no pattern to build once, and the index buffer joins the vertex one in
being per-frame.

**A ribbon of one particle draws nothing.** A strip needs two points to have a direction, so a single
particle contributes its two vertices and no triangles — which is what makes a trail appear as its
second particle is born rather than as a degenerate sliver. And two strips are never joined: the
triangle spanning the gap between two trails is a bright sheet across the scene, and it has a test.

**One alignment convention across both kinds.** A velocity-aligned mesh points its local **+Y** along
the velocity, which is the axis a velocity-aligned billboard stretches along. One convention is worth
more than each being locally reasonable: a model authored for a streak works for an instanced spark,
and a model built the other way up is a rotation in the asset rather than a flag here.

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

### The renderer that draws nothing

`VfxRenderer.Light()` makes each particle a point light. `Vixen.Rendering.ParticleLights.Collect`
appends them to whatever is gathering lights this frame, and that lives in `Vixen.Rendering` because
`RenderLight` does — the runtime knows what a light-emitting particle *is* and nothing about how the
renderer represents one, which is the same split `VfxGpuSimulation` makes.

It is the one renderer an additive quad cannot fake: the quad brightens the sparks, not the wall behind
them. **Colour's alpha is the intensity and size is the range**, so a colour-over-life fade dims the
pool of light and a size-over-life curve shrinks it — the two curves an author has already written are
the two an author expects the light to follow.

**It takes a budget and reports what did not fit.** A light costs every fragment it reaches in every
pass that shades one, so a thousand of them is not an effect but an outage. A system meant to light a
scene has a capacity of a dozen; the budget is the author's to set, and being at it is normal for a
deliberate effect and a mistake for an accidental one — which is exactly why it is reported rather
than logged.

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

## Colliders are analytic, and that is what makes them opcodes

`CollidePlane` and `CollideSphere` keep particles on the right side of a surface. Both are ordinary
updaters — a read of position and velocity, a test, and a write — which is what lets them be a case in
the CPU sweep and a case in the emitter like everything else.

```csharp
new(VfxOpcode.CollidePlane, new Vector4(0f, 1f, 0f, 0f)) { B = new(0.6f, 0.2f, 0f, 0f) }
//                          normal        distance          bounce  friction
```

**Bounce and friction are separate because they mean different things.** The velocity is split into
the part along the normal and the part across it: bounce is how much of the approach comes back,
friction is how much of the slide is scrubbed off. Reflecting the whole vector and scaling it would
make a particle dropped straight down and one skimming along the floor lose the same fraction of their
speed, which is neither word.

**Only an approach is reflected.** A particle already moving away from the surface was pushed out last
step and is leaving; bouncing it again traps it against the surface, vibrating. That is the classic
way a collider makes a system buzz, and it is one comparison.

**The particle is moved as well as turned.** These run after the integration, so a particle that hit
something is already through it. Putting it back on the surface is what stops the one frame inside the
floor that a viewer notices.

An earlier draft of this file said collision "needs something outside the module — a depth buffer or a
physics query — and is therefore the one that cannot be an opcode". That is true of *screen-space*
collision and false of analytic primitives, which is what these are. Depth-buffer collision remains
outside; a plane and a sphere never were.

## Particles that emit particles

`VfxSubEmitter` connects one system's particles to another system, on one of three events.

```csharp
var burst = new VfxSubEmitter(shell, sparks, VfxEmitEvent.Death, count: 40);
shell.RecordDeaths = true;

shell.Step(dt);
sparks.Step(dt);
burst.Step(dt);      // after both, always
```

**Two systems rather than one system with two kinds of particle.** A shell and its sparks have
different lifetimes, forces, renderers and capacities; they are two effects that happen to be
connected. Folding them into one graph would put a test for "which kind is this" inside every
operation, which is the per-particle branch the storage design exists to avoid.

**The child's initializers are an offset, not a replacement.** A child is initialized by its own graph
and then moved to where its parent was, so "burst in a sphere" scatters around the parent rather than
around the origin. That is the only reading under which authoring the child graph separately is worth
anything.

**A trail needs no storage of its own.** The interval is counted off the parent's *age*: a particle
whose age and whose age-a-step-ago fall either side of a multiple of the interval is one that is due.
That is exact, costs nothing per particle, and stays right when a death reorders the buffer — which a
per-slot timer would not. It also sheds the first child at birth, so there is no gap between the
parent and the trail behind it.

**A death burst needs `RecordDeaths`.** By the time a step has finished, a dead particle's slot belongs
to a survivor, so the position has to be kept as it is reaped. It costs a `Vector3` per particle of
capacity and it is off by default — the same rule as every other attribute here. A sub-emitter on a
system nobody switched it on for emits nothing rather than guessing.

**Step it after both systems.** A child spawned here waits until the next step to be updated, which is
exactly what happens to a particle a spawner produces, because `Step` updates before it spawns.
Emitting between the two steps would age a child on the step it was born and leave the two ways a
particle can come into existence disagreeing about how old it is.

## Sweeps across threads, and the number that decides

`VfxSystem.Scheduler` is null by default. Given one, a step runs its updaters across the scheduler's
threads when the population reaches `ParallelThreshold`.

**One dispatch for the whole graph, not one per operation.** Scheduling per operation matches the
serial sweep's shape and is the wrong one: six updaters would pay six barriers, and gravity over ten
thousand particles is over before a barrier is. A batch runs the *whole updater list* over its own
range of particles — which is also the order the GPU backend runs in, and produces bit-identical
results because no operation reads another particle.

**The threshold is measured, and the measurement says a particle count cannot be right for every
graph.** `Benchmarks/Vixen.Benchmarks.Vfx` runs a cheap graph (gravity, integrate) and an expensive one
(attract, vortex, three octaves of curl noise, integrate, two curves) at five populations, serial and
parallel. What it shows, consistently across two runs:

- The expensive graph is several times faster on the scheduler from about a thousand particles up —
  five-fold at four thousand and above.
- The cheap graph is **never** faster, at any population measured up to 65,536. It is bandwidth-bound;
  four threads streaming the same arrays do not stream them faster.
- Neither path allocates a byte, at any size.

So there is no particle count at which parallelism is free for a cheap graph, and the default of 4096
is chosen for the case where a scheduler was handed over at all — someone with an expensive graph. It
is a public settable property because the right number is a property of the graph, and this is the
measurement that says so rather than an opinion.

One caution about those numbers: the sweep takes twenty minutes and this machine throttled during it,
so the second run's absolute times run up to three times the first run's at the largest populations and
the error bars reach fifty per cent. The *ratios within a population* held across both runs, and the
ratios are what the threshold rests on. Absolute throughput from this table would be worth nothing.

## What is not here yet

- **A second view of the same effect.** `ParticleRenderFeature` expands once, against one view, so a
  reflection or a shadow pass draws quads facing the wrong camera. Expanding per view is the
  workaround; the GPU path is the fix, which is why the workaround is not in.
- **The node graph and its editor.** `Vixen.Editor.VfxGraph` authors what `Compile` consumes. A graph is
  written in code today.
- **Arbitrary expressions over custom attributes.** The storage and its three operations are here; what
  is not is a node reading two attributes and writing a third. That is the node graph, and a lowering
  to add/multiply/select over a register file — a different design, and the one the closed opcode set
  exists to postpone.
- **Screen-space collision.** The analytic colliders are in; colliding against a depth buffer needs one,
  and that is a renderer's resource rather than a simulation's.
- **Sub-emitters on the GPU.** `VfxSubEmitter` is a CPU object walking CPU particles. The device form
  needs the atomic append that reaping needs, and the same dispatch that would drive it.

Licensed under Apache-2.0.

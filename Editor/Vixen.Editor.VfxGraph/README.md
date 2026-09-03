# Vixen.Editor.VfxGraph

Particle effects authored as a graph, compiled to both processors at once.

The node library and the lowering; the framework underneath is
[`Vixen.Editor.NodeGraph`](../Vixen.Editor.NodeGraph/README.md) and the runtime it produces is
[`Vixen.Vfx`](../../Core/Vixen.Vfx/README.md). No UI.

```csharp
var registry = new NodeTypeRegistry();
NodeTypes.Register(registry);

var result = new VfxGraphCompiler(registry).Compile(graph);

using var system = new VfxSystem(result.Value.Graph);   // the CPU simulation
File.WriteAllText("Fountain.rvn", result.Value.Shader.Source);   // and the GPU one
```

## The dual target is nearly free, and that is the whole point

[Doc 06](../../docs/plan/06-rendering-pipeline.md) asked for dual-target compilation to be designed in
rather than retrofitted. It was: `VfxCompiledGraph` is an array of fixed-size operations, and
`VfxShaderEmitter` was written against that array. So a node graph that produces the array produces
the shader too — **by calling one method.**

There is no second lowering, no second node library, and no way for the two to have understood the
graph differently. That is what the earlier decision bought, and this is where it gets spent.

## Blocks in a chain, not expressions in a tree

A shader graph's edges carry values. A VFX graph's carry *order*: a `Gravity` block does not hand
`Integrate` a number, it runs before it. That is `PortKind.Flow`, a port that carries nothing, and
giving it its own kind means the compiler can refuse a wire between a value and an ordering and an
unconnected one has no default to invent.

The framework's topological sort turns the chain into the list `VfxCompiledGraph.Compile` wants, and
its cycle refusal means an author cannot draw a loop of blocks.

**An unwired block still runs.** Order among blocks nothing connects is the order they were added,
because that is what the sort falls back to — so a graph built by dropping blocks in compiles to what
an author expects, and wiring them is how to say otherwise.

**A block's parameters are numbers, not text.** A `VfxOperation` holds two `Vector4`s, so a node
reads `Binding.Value(port)` rather than the expression a shader node would interpolate. The framework
hands over both forms for exactly this.

## The library

⚠ **This table was wrong in both directions and nothing could catch it**, because no test asserted
what the registry contains — only that whatever is in it compiles. It omitted seven shipped nodes
(Vortex, Collide Sphere, Mesh, Ribbon, Set Custom, Random Custom, Custom over Life) and the section
below undercounted the remaining opcodes by four. `VfxNodeLibraryTests` now pins the list to this one.

| Category | Nodes |
|---|---|
| Effect | Effect — the capacity, which is the one number an author has to choose |
| Spawn | Burst, Rate |
| Initialize | Position, Position in Box, Position in Sphere, Set Velocity, Random Velocity, Velocity in Cone, Lifetime, Size, Colour, Rotation, Angular Velocity, Set Custom, Random Custom |
| Update | Gravity, Drag, Integrate, Rotate, Attract, Vortex, Turbulence, Collide Plane, Collide Sphere, Size over Life, Colour over Life, Custom over Life |
| Output | Billboard, Mesh, Ribbon, Light |

Thirty-two, which is every opcode in `VfxOpcode` reachable from a graph.

## The runtime's refusals arrive as diagnostics

`VfxCompiledGraph.Compile` refuses a graph whose updaters read an attribute no initializer writes —
an integration over a velocity nothing set. That is exactly the mistake a node graph makes easy, so
it is caught and reported against the graph rather than let out as an exception from a compiler whose
whole job is to report problems. A graph with no spawner is refused here for the same reason: it
would produce no particles at all, and saying so is more useful than compiling silence.

## An attribute a graph keeps for itself

`Set Custom`, `Random Custom` and `Custom over Life` each hold an `Attribute` **setting** — a
`[Setting]`-declared `string`, edited in the panel beside the canvas — and a `Lanes` port. There is no
declaration node: the first block to name an attribute declares it, and its slot is where it landed,
which is the rule the built-in attributes already follow. `VfxGraphBuilder.Custom` is the find-or-add,
and it leaves a problem rather than throwing, because `Contribute` is handed a builder and not a
diagnostic sink.

⚠ **`Vfx/Output/Ribbon` names its attribute rather than numbering it**, and the name is resolved to a
slot in `Finish` — after every block has contributed, because a block nobody wired contributes in
insertion order and an output may sit above its writer. A name nothing writes is refused: unwritten
storage is zero for every particle, so every particle would be in one strip and there would be
nothing to search for.

## What is not here yet

- **Operator nodes.** A `Sine` feeding a gravity's strength, as a shader graph's operators feed a
  master. The compiled form's parameters are constants, so an operator would have to be constant
  folded — which is a real feature and a different one.
- ~~**Blocks for the opcodes that are not here.**~~ **Done, and ⚠ this line said one was left when
  five were.** `Vortex` and `CollideSphere` landed with the mesh output; `SetCustom`, `RandomCustom`
  and `CustomOverLife` landed once a node could hold a **name** — see `[Setting]` in
  `Vixen.Editor.NodeGraph`. The last five — `SetPosition`, `VelocityInCone`, `SetRotation`,
  `SetAngularVelocity` and `Rotate` — were each implemented in `VfxSimulation` **and** in
  `VfxShaderEmitter` with nothing able to author them, which is the built-and-never-fed shape rather
  than a hole. ⚠ `VelocityInCone` is the one that cost something visible: without it the library's
  only random emitter was a sphere, so every jet, fountain and muzzle flash was a sphere somebody had
  aimed with a Gravity block.
- **Sub-emitters and trails.** ⚠ **The runtime is done and tested** — `Core/Vixen.Vfx/VfxSubEmitter.cs`
  drives `VfxEmitEvent.Birth`, `.Death` and `.Trail`, and `Core/Vixen.Vfx.Tests/VfxSubEmitterTests.cs`
  exercises all three. What is missing is the other two thirds: `VfxEffectContent` has no row for one,
  so a `.vxvfx` cannot express it, and nothing here mentions `VfxSubEmitter` — so this is a node, a
  content field, and a host that steps the sub-emitter after both systems. Authoring one is authoring
  a relationship between two graphs, which the model can hold and the compiler has nothing to say
  about yet.
- ~~**A live preview.**~~ Closed by doc 20's E5, and in the other assembly. `VfxNodeLibrary.Create`
  is the one call a host needs from here; the document, the canvas and the preview are
  `Vixen.Editor.AssetEditors`' `Vfx/`, because this assembly deliberately knows nothing about a
  project, a document or a panel — which is what lets its tests compile a graph with no editor in the
  way. ⚠ The preview *simulates* with `VfxSystem` and *draws* by projecting the particle buffer:
  particles are drawn by a material, and the editor's viewport is a tool renderer until doc 14's
  Phase 7 wires one.

Licensed under Apache-2.0.

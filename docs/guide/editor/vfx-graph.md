---
title: The VFX graph
slug: editor/vfx-graph
kind: guide
area: Editor
summary: The node library a .vxvfx is authored against — spawners, initializers, updaters and outputs — and the compiler that turns one graph into both an effect the CPU runs and a shader a device runs.
api: [T:Vixen.Editor.VfxGraph.VfxGraphCompiler, T:Vixen.Editor.VfxGraph.VfxGraphArtefact, T:Vixen.Editor.VfxGraph.VfxGraphBuilder, T:Vixen.Editor.VfxGraph.VfxNode, T:Vixen.Editor.VfxGraph.VfxNodeLibrary, T:Vixen.Editor.VfxGraph.NodeTypes, T:Vixen.Editor.VfxGraph.Nodes.VfxBlockNode, T:Vixen.Editor.VfxGraph.Nodes.EffectNode, T:Vixen.Editor.VfxGraph.Nodes.BurstNode, T:Vixen.Editor.VfxGraph.Nodes.RateNode, T:Vixen.Editor.VfxGraph.Nodes.PositionNode, T:Vixen.Editor.VfxGraph.Nodes.PositionInBoxNode, T:Vixen.Editor.VfxGraph.Nodes.PositionInSphereNode, T:Vixen.Editor.VfxGraph.Nodes.RandomVelocityNode, T:Vixen.Editor.VfxGraph.Nodes.VelocityInConeNode, T:Vixen.Editor.VfxGraph.Nodes.SetVelocityNode, T:Vixen.Editor.VfxGraph.Nodes.LifetimeNode, T:Vixen.Editor.VfxGraph.Nodes.SizeNode, T:Vixen.Editor.VfxGraph.Nodes.ColourNode, T:Vixen.Editor.VfxGraph.Nodes.RotationNode, T:Vixen.Editor.VfxGraph.Nodes.AngularVelocityNode, T:Vixen.Editor.VfxGraph.Nodes.GravityNode, T:Vixen.Editor.VfxGraph.Nodes.DragNode, T:Vixen.Editor.VfxGraph.Nodes.IntegrateNode, T:Vixen.Editor.VfxGraph.Nodes.RotateNode, T:Vixen.Editor.VfxGraph.Nodes.AttractNode, T:Vixen.Editor.VfxGraph.Nodes.VortexNode, T:Vixen.Editor.VfxGraph.Nodes.TurbulenceNode, T:Vixen.Editor.VfxGraph.Nodes.CollidePlaneNode, T:Vixen.Editor.VfxGraph.Nodes.CollideSphereNode, T:Vixen.Editor.VfxGraph.Nodes.SizeOverLifeNode, T:Vixen.Editor.VfxGraph.Nodes.ColourOverLifeNode, T:Vixen.Editor.VfxGraph.Nodes.VfxCustomNode, T:Vixen.Editor.VfxGraph.Nodes.SetCustomNode, T:Vixen.Editor.VfxGraph.Nodes.RandomCustomNode, T:Vixen.Editor.VfxGraph.Nodes.CustomOverLifeNode, T:Vixen.Editor.VfxGraph.Nodes.BillboardOutputNode, T:Vixen.Editor.VfxGraph.Nodes.MeshOutputNode, T:Vixen.Editor.VfxGraph.Nodes.RibbonOutputNode, T:Vixen.Editor.VfxGraph.Nodes.LightOutputNode]
tags: [editor, vfx, particles, node-graph]
since: 0.1
status: preview
related: [rendering/particles, editor/modes]
---

## What it is

A `.vxvfx` is a node graph, and this is the library it is drawn against. Every node is a
`[Node("Vfx/…")]` class in `Vixen.Editor.VfxGraph.Nodes`, and every one of them does the same small
thing: `Contribute` appends to a `VfxGraphBuilder` — a spawner, an operation, or the renderer.

`VfxGraphCompiler` walks the graph in **wire order** and produces a `VfxGraphArtefact`: a
`VfxCompiledGraph` the CPU simulation runs, and a Raven shader source the GPU backend compiles. One
graph, two targets, one lowering — which is the property the whole design exists for, and why there
is no second node library for the device path.

⚠ **The wire is order, not data.** A VFX node has one `Flow` input and one `Flow` output and carries
no values between blocks. Two blocks connected means "this one runs after that one"; a block nobody
wired still contributes, at whatever position the graph gives it.

## What it is for

Authoring the *behaviour* of an effect: what makes particles, what each one starts as, what happens
to it every step, and what it is drawn as. Four categories, and the menu path is the category:

| Path | What the nodes there do |
|---|---|
| `Vfx/Effect` | the capacity and the renderer, as one node per graph |
| `Vfx/Spawn/…` | `Burst`, `Rate` — what makes particles |
| `Vfx/Initialize/…` | `Position`, `Position in Box`, `Position in Sphere`, `Random Velocity`, `Velocity in Cone`, `Set Velocity`, `Lifetime`, `Size`, `Colour`, `Rotation`, `Angular Velocity`, `Set Custom`, `Random Custom` |
| `Vfx/Update/…` | `Gravity`, `Drag`, `Integrate`, `Rotate`, `Attract`, `Vortex`, `Turbulence`, `Collide Plane`, `Collide Sphere`, `Size over Life`, `Colour over Life`, `Custom over Life` |
| `Vfx/Output/…` | `Billboard`, `Mesh`, `Ribbon`, `Light` — one of these decides the renderer |

It is **not** for saying which shader draws the particles or which texture they use. There is no
material node: the host chooses, once, through `WorldRenderer.ParticleMaterial` and
`WorldRenderer.MeshParticleMaterial`. See [Drawing particles](../rendering/particles.md).

## Using it

**The output node is the one that changes everything downstream.** It sets `VfxRenderer`, and the
renderer declares what the graph has to allocate — so choosing `Ribbon` is what makes a graph keep
each particle's age, and choosing a velocity-aligned `Billboard` is what makes it keep velocity even
if no updater would have.

| Output | Draws | Needs |
|---|---|---|
| `Billboard` | a camera-facing quad per particle | nothing but position, size and colour |
| `Mesh` | an instance of a mesh per particle | `VfxEmitter.Mesh` on the entity |
| `Ribbon` | a strip through the particles sharing a custom attribute | a block that writes the attribute it names |
| `Light` | a point light per particle, and no geometry at all | a host that collects them |

⚠ **Roll needs two blocks, exactly as movement does.** `Vfx/Initialize/Angular Velocity` sets the
spin rate and nothing turns until `Vfx/Update/Rotate` integrates it — the same pairing as
`Vfx/Initialize/Set Velocity` and `Vfx/Update/Integrate`, and the same first surprise. A graph with
the rate and no `Rotate` draws still billboards; one with `Rotate` and no rate advances every
particle by zero. `Vfx/Initialize/Rotation` is the starting angle, and its default range of nought to
2π is what stops a burst of sprites all facing the same way.

⚠ **A graph with two output nodes in it is not an error and the last one wins.** `Contribute`
assigns `VfxGraphBuilder.Renderer`, so a second output overwrites the first silently. One per graph.

⚠ **`Vfx/Output/Mesh` says the particles are geometry; it does not say which geometry.** The asset is
`VfxEmitter.Mesh`, on the entity, for the reason the material is the host's — the same debris effect
is worn by the rock, the crate and the glass.

⚠ **`Vfx/Update/Integrate` is what actually moves anything.** Gravity, drag and the fields all write
*velocity*; nothing moves until an integrator turns velocity into position, and a graph without one
is particles that accelerate in place. The compiler refuses the reverse mistake — an updater reading
an attribute no initializer writes comes back as diagnostic `VG0003` rather than as zeroed memory.

⚠ **A field reads where the particle is.** `Attract`, `Vortex` and `Turbulence` all declare a read of
position, so a graph whose initializers never place its particles is refused: a field acting on
particles that are all at the origin accelerates every one of them identically.

### Custom attributes

A graph may keep per-particle quantities of its own. `Set Custom`, `Random Custom` and
`Custom over Life` each hold an **`Attribute` setting** — a name, typed in the panel beside the
canvas, not a port — and a `Lanes` port saying whether it is a float, a float3 or a float4.

⚠ **An attribute exists because something writes it.** There is no declaration node and no list to
keep in step: the first block to name one declares it, and its slot is where it landed. That is the
rule the built-in attributes already follow — storage is derived from what the operations touch.

⚠ **One name is one slot, and one slot is one width.** Two blocks naming `glow` share it; two
naming it at different `Lanes` are refused rather than quietly widened, because promoting one would
change what every other operation on it reads.

⚠ **`Vfx/Output/Ribbon` names its attribute; it does not number it.** A slot is a *position* in the
declaration list and that position moves the moment a block is added above, so a number would have
silently pointed at something else. The name is resolved after every block has contributed, which is
also why an output dropped on the canvas before its writer still compiles. An attribute nothing
writes is refused — unwritten storage is zero for every particle, so every particle would be in one
strip.

⚠ **The name becomes an identifier in the emitted shader**, so it has to be one, has to be unique,
and cannot be something the emitter already declares — `seed`, `age`, `identifierOut`, `Noise` and
the rest are refused by name rather than as a parse error in generated source nobody wrote.

Everything a block finds wrong here comes back as `VG0004`, against the graph. A block is handed a
builder and not a diagnostic sink, so it leaves what it found for `Finish` to say — which is what
lets a graph with three mistakes in it report three.

⚠ **Two lanes is refused, not rounded.** `VfxAttributeType` has no `Float2`: the shader declares one
buffer element type per attribute, and a node that accepted two would store one lane of what was
typed and never say which.

⚠ **`Vfx/Output/Light`'s `Range` is per *unit* size**, so it is multiplied by each particle's own
size. Two-centimetre embers at the default of four reach four centimetres and light nothing at all.
Intensity is candela, like every other punctual light in the engine.

## Examples

A whirl that never converges — the thing `Attract` cannot do, because a pull towards a point ends
with every particle at the point:

```csharp no-compile="in a test or a tool that builds a graph without the editor open"
var graph = new NodeGraphModel { Name = "Whirl" };

graph.Add("Vfx/Spawn/Rate");
graph.Add("Vfx/Initialize/Position in Sphere");
graph.Add("Vfx/Initialize/Lifetime");

var vortex = graph.Add("Vfx/Update/Vortex");

vortex.SetValue("Centre", 0f, 0f, 0f);
vortex.SetValue("Axis", 0f, 1f, 0f);
vortex.SetValue("Strength", 7f);

graph.Add("Vfx/Update/Integrate");
graph.Add("Vfx/Output/Billboard");

var artefact = new VfxGraphCompiler(VfxNodeLibrary.Create()).Compile(graph);
```

Debris, as chunks of a mesh thrown outward and turned by their own velocity:

```csharp no-compile="the graph half; the mesh is VfxEmitter.Mesh on whatever entity emits it"
var graph = new NodeGraphModel { Name = "Debris" };

graph.Add("Vfx/Spawn/Burst");
graph.Add("Vfx/Initialize/Position in Sphere");
graph.Add("Vfx/Initialize/Random Velocity");
graph.Add("Vfx/Initialize/Lifetime");
graph.Add("Vfx/Update/Gravity");
graph.Add("Vfx/Update/Collide Sphere");
graph.Add("Vfx/Update/Integrate");

graph.Add("Vfx/Output/Mesh").SetValue("Align to Velocity", 1f);
```

⚠ `VfxNodeLibrary.Create()` rather than a fresh `NodeTypeRegistry` — one registry per host, not one
per document. A registry nobody registered against reports every node in a saved file as unknown,
which is a long way from the mistake.

**A trail of ribbons, keyed on an attribute the graph writes itself.**

```csharp no-compile="a library and a graph, as the compiler tests build one"
var graph = new NodeGraphModel { Name = "Trail" };

graph.Add("Vfx/Spawn/Rate");
graph.Add("Vfx/Initialize/Position in Sphere");
graph.Add("Vfx/Initialize/Lifetime");

// Four strips, chosen at birth and never changed. The name is a setting, not a port.
var strip = graph.Add("Vfx/Initialize/Random Custom");

strip.SetText("Attribute", "strip");
strip.SetValue("Maximum", 4f, 0f, 0f, 0f);

graph.Add("Vfx/Update/Integrate");
graph.Add("Vfx/Output/Ribbon").SetText("Attribute", "strip");
```

## See also

- [Editing a node's ports](node-port-editing.md) — `[Setting]`, and the panel the `Attribute` above
  is typed into.
- [Drawing particles](../rendering/particles.md) — the component, the bridge, the feature and the
  two materials the outputs above are drawn with.
- [Viewport modes](modes.md) — where an effect document sits among the editor's other surfaces.

# Vixen.Editor.ShaderGraph

Shaders authored as a graph, compiled to Raven source.

The node library, the emission, and the preview renderer; the framework underneath is
[`Vixen.Editor.NodeGraph`](../Vixen.Editor.NodeGraph/README.md). There is no UI here and none is
needed to check any of it — the panel that opens a `.vxshadergraph` is
[`Vixen.Editor.AssetEditors`](../Vixen.Editor.AssetEditors/README.md)'s `Shading/`, on the same split
the VFX graph makes: a compiler that knows nothing about a project is a compiler a test can run with
no editor in the way.

⚠ **A device is not a UI.** `ShaderGraphPreviewRenderer` takes an `IGraphicsDevice` and draws, which
is why this assembly references `Vixen.Graphics`, `Vixen.Shaders` and `Vixen.ShaderCompiler`. It
still knows nothing about a project, a document or a panel, and `ShaderGraphPreview` — the half that
compiles a node's sub-expression — needs none of the three.

```csharp
var registry = new NodeTypeRegistry();
NodeTypes.Register(registry);

var graph = new NodeGraphModel { Name = "Tinted" };
var uv = graph.Add("Input/UV");
var sample = graph.Add("Texture/Sample 2D");
var master = graph.Add("Master/Unlit");

graph.Connect(new(uv.Id, "UV"), new(sample.Id, "UV"));
graph.Connect(new(sample.Id, "RGBA"), new(master.Id, "Colour"));

var result = new ShaderGraphCompiler(registry).Compile(graph);
File.WriteAllText("Tinted.rvn", result.Value.Source);
```

## Source, not IR

[Doc 07](../../docs/plan/07-raven-shader-pipeline.md) settled this. The generated shader is
inspectable through "show generated code", it is type-checked by the same compiler a hand-written
shader goes through, and its diagnostics come back with spans. A graph that lowered straight to IR
would need its own type checker and would produce shaders nobody could read.

The tests are built on that: **every one of them puts the emitted source through the real compiler.**
A generated shader that reads beautifully and does not compile is the only failure mode this stage
really has, and a golden-text assertion is exactly the test that cannot see it. There is a golden
test too — doc 11's table asks for one, and it is what notices a change nobody meant to make — but it
is the second line of defence.

## The library

| Category | Nodes |
|---|---|
| Input | UV, World Position, World Normal, Vertex Colour, Time, Constant, Colour Property, Float Property |
| Math | Add, Subtract, Multiply, Divide, Lerp, Saturate, One Minus, Power, Absolute, Fraction, Sine, Smoothstep, Dot, Normalize |
| Vector | Combine, Split, Tiling and Offset, Rotate UV, Flipbook |
| Procedural | Noise, Fractal Noise, Checker |
| Texture | Sample 2D |
| Master | Unlit, Sprite, PBR, **Surface** |

⚠ **The Master row said three for as long as there were four.** `Master/Surface` landed in
`5a5e6332` and is documented forty lines below, and this table was not updated with it.

**The procedural and UV nodes add no shader code.** Each is a call into
`Raven/Library/Material/ComputeColor.rvn`, whose procedural and UV sections were written as "the
shader-graph node vocabulary" and ⚠ had no caller of any kind. They are the first nodes whose Raven
is not self-contained, which is why `RavenEmitter.Import` exists — see *Two shapes, two preambles*
below — and why they declare no preview.

### Two shapes, two preambles

A surface graph emits the four `Vixen.Shaders.*` imports unconditionally, because it is composed into
a pass that has them. ⚠ **A standalone graph emitted none at all**, which is right for what compiles
one — the node preview, which binds one uniform block and refuses a variant whose reflection asks for
more. So an import is *asked for*, by `RavenEmitter.Import`, rather than written into both preambles:
a graph that never calls into the library pays nothing, and the surface shape drops a request that
duplicates one of its four, because Raven refuses a repeated import.

**Most maths nodes are two dynamic inputs and a dynamic output**, so one `Add` works on floats, on
colours and on positions. That is what `DynamicVector` is for and the reason there is no `AddFloat3`
next to it.

**A node writes statements, not a shader.** It has no idea what stage it is in, what the entry point
is called or what the master did; it emits lines that assign to its own outputs and stops. Everything
structural is the compiler's, which is what lets a node be twelve lines and a plugin's node be twelve
lines too.

**Declarations are requests.** A node that needs a uniform or an interpolated value asks and gets the
name back. Two texture nodes sampling one property declare one binding; a graph that never reads a
normal interpolates no normal, which is a real cost on a dense mesh and a varying slot on every mesh.
What each compile asked for comes back on `ShaderGraphSource.Properties`, which is what a panel shows
as "what this graph needs from outside" — and it cannot yet say which of them a *material* supplies
rather than the engine, because the emitter asks for both the same way.

**A property's name is authored, and it lives on the graph.** `Texture/Sample 2D`, `Colour Property`
and `Float Property` read theirs from `GraphNode.Texts` under `ShaderProperties.Key`. It used to be a
C# field on the node, which is scaffolding the compiler builds and throws away — so nothing wrote it,
nothing saved it, and every texture in every graph was `albedo`. A name that cannot be changed is one
binding for every node that wants one, which is the shape of bug that makes a node library unusable
in a real project rather than in a test.

## The shape of what comes out

The vertex stage is fixed and the fragment stage is the graph. A shader graph is about what a surface
looks like; the transform and the interpolators are the same in every one.

```
shader Tinted {
    var worldViewProjection: mat4
    var world: mat4
    var albedo: Texture2D
    var albedoSampler: Sampler

    stream var uv: float2

    [VertexShader] [Semantic("SV_Position")]
    func Vertex(position: float3, texcoord: float2): float4 { … }

    [FragmentShader] [Semantic("SV_Target")]
    func Fragment(): float4 {
        val n1_UV = uv
        val n3_RGBA = albedo.Sample(albedoSampler, n1_UV)
        val surface = float4(n3_RGBA.xyz, 1f)
        return surface
    }
}
```

**Exactly one master.** A graph with none produces nothing to write; one with two would produce two
shaders under one name. Both are reported against the graph rather than guessed at.

**A texture and its sampler are one property.** A graph where an author can wire a sampler is a graph
where they can wire the wrong one, and every real material wants the sampler belonging to the
texture. The declaration is `{name}` and `{name}Sampler`, which is the convention the hand-written
library shaders already use.

**Widening pads and narrowing swizzles.** A `float3` read as a `float4` becomes `float4(v.x, v.y,
v.z, 1f)` — the homogeneous convention — and a `float4` read as a `float2` becomes `.xy`. A scalar
splats. Those are the rules every shader language already has, and an author who wants something else
has a `Combine` node.

## Preview thumbnails

`ShaderGraphPreview.Compile(graph, node, registry)` compiles **one node's sub-expression** — that
node, everything upstream of it, and a `Master/Unlit` hung on its output — as an ordinary graph
through the ordinary compiler. Nothing about emission, typing, conversion or diagnostics is
duplicated, so a preview is by construction compiled the way the shader is.

The vertex stage needs no special case either. Every graph emits
`worldViewProjection * float4(position, 1f)` and exactly the varyings it asked for, so a preview is
that stage over a quad with identity transforms: `ShaderGraphPreviewRenderer` supplies clip-space
corners as `position` and the unit square as `texcoord`, and the shader is untouched.

**Unlit, into a non-sRGB target, and neither is incidental.** The fragment writes the node's value
straight out — no lighting, no exposure, no tone map — so what a preview shows is the number the node
computed. A preview shaded like the scene would answer a different question, in different units.

**Clip `y = +1` is the top**, so the corner at `+1` takes `texcoord.y = 0` and the target's first row
is the top of the picture. A preview drawn the other way up is perfectly plausible to look at, which
is why the device test asserts a corner as well as a histogram.

**Two tiers and three gates.** Emitting Raven is string work over a handful of nodes; compiling it to
SPIR-V and building a pipeline is tens of milliseconds. A frame in which the graph's revision has not
moved emits nothing; a graph that changed but whose emitted text did not compiles nothing; and
`RebuildsPerUpdate` rations what is left. The renderer owns one target per node, redraws into it
rather than replacing it, and `Created`/`Destroyed`/`Live` are public so the absence of a leak can be
measured. See [the guide page](../../docs/guide/editor/shader-graph-previews.md).

## Two shapes, and only one of them draws

A master decides which — `ShaderGraphKind`, the one structural decision a node makes.

**Standalone** is a whole shader: its own `worldViewProjection`, its own vertex stage, a `float4`
return. `Master/Unlit`, `Master/Sprite` and `Master/PBR` make one. It is readable, it draws a preview
thumbnail, an author can hand it to `raven compile` — and **nothing in this engine can put it on a
mesh**, because a draw binds a transform record, a light cluster, a shadow atlas and a bindless table
by names it does not declare.

**Surface** is a material feature: `shader N : IMaterialSurface`, no stages, no entry point, no
`return`. `Master/Surface` makes one. `MaterialCompiler` composes it into `CompositeSurface` beside
the hand-written features, so a graph-authored material is transformed, lit and shadowed by the same
path every other material takes. The whole of the engine's material model turns out to have been
written for exactly this shape, which is why the drawing half needed no new render feature and no new
pass.

⚠ **A feature is composed into a pass it has never seen, so it may only read `MaterialData`.** `uv`
becomes `d.uv` and a world normal becomes `d.tangentFrame.normal`; a **world position** and a
**vertex colour** are not there at all, and `SG0004` refuses the graph rather than substituting the
origin or white — both of which compile, draw, and produce a surface lit as though the graph said
something it did not.

⚠ **A texture is a slot, not a binding.** A standalone graph declares `albedo: Texture2D` and
`albedoSampler: Sampler` and owns them. A surface declares `albedoIndex: uint` and indexes
`MaterialTextures`'s shared table — the mechanism doc 06 named as the reason a feature could not
sample at all until there was one. The array is indexed directly rather than through `SampleSurface`,
because that helper samples at `d.uv` unconditionally and would silently ignore a `Tiling and Offset`
node. `ShaderGraphSource.Maps` carries the `albedo` ⇄ `albedoIndex` pairing a host feeds
`MaterialRenderFeature.TextureIndices`.

`ShaderGraphMaterial.Feature` turns a compiled surface into the `GraphSurfaceFeature` a `.vxmat`
composes — the only `IMaterialFeature` whose `ShaderName` is data rather than a constant.

## How the emitted Raven reaches a compilation

`Vixen.Editor.Assets`'s `ShaderGraphSources` compiles every `.vxshadergraph` under `Assets/` and
hands the text over; `EditorEffects` and `ShaderBuildRunner` both enumerate it beside their `*.rvn`.

⚠ **Nothing is written to disk.** A generated `.rvn` beside its graph would acquire a `.meta`, an
address and a place in the browser, be committed by somebody, edited by somebody else, and silently
overwritten by the next import. `RavenEffectCompiler.FromSources` has taken in-memory sources since
the previews needed them.

⚠ **A graph that will not compile contributes nothing rather than its text**, because
`RavenEffectCompiler`'s constructor throws on a source that will not parse — so one unfinished graph
would refuse every material in the project under a message about the library.

## The PBR master is honest about being half a job

It emits a self-contained Lambert plus GGX with one directional light from uniforms. A master that
called into `Vixen.Shaders.Shading` would be the right long-term arrangement and would mean a graph's
output could not be compiled without the library on the include path — which is exactly what the
golden tests compile it without. So it is inline: enough to be a real shader, checkable on its own,
and a change to one method when it is time to wire it into the engine's clustered lighting.

## What is not here yet

- ~~**Procedural nodes.**~~ **Value noise, fractal noise and a checker are in**, over
  `ComputeColor.rvn`'s own functions rather than a second transcription of them. ⚠ What is left is
  Perlin, simplex and voronoi, and each of those is a change to a published `.rvn` — a regeneration
  and a `CheckShaders` run — rather than a node. A gradient is `Math/Lerp`.
- ⚠ **A preview of a node that calls the library.** `ShaderGraphPreviewRenderer` compiles the emitted
  preview through `RavenEffectCompiler.FromSources` with exactly one source, so nothing in the shipped
  library is in scope and the procedural nodes cannot declare `Preview`. It is a property of the
  preview's compilation rather than of those nodes — the same graph compiles as a material, because
  `EditorEffects` and the shader build both hand Raven the library's import closure.
- **A custom-code node**, which doc 11 lists. It is a `[Input]`-less node holding a string of Raven,
  and the interesting part is not the node but what happens to a diagnostic inside it.
- **Post and UI masters.** ⚠ **Four masters are in, not three** — Unlit, Sprite, PBR and Surface —
  and ⚠ **doc 11 is one table cell**: `master (PBR/unlit/sprite/UI/post)` at
  `docs/plan/11-editor.md:486`, with no prose anywhere and nothing at all about what shape either
  emits. `Master/Surface` emits `shader N : IMaterialSurface`; a post pass is a full-screen shader
  with a source texture and `UiQuad.rvn` has its own vertex contract, so both are most likely a
  *third* and *fourth* `ShaderGraphKind` rather than variations of the standalone preamble. That
  decision is the work, and it is unmade.
- ~~**Diagnostics mapped back to ports.**~~ **Done.** `RavenEmitter` counts the lines it writes, so
  this compiler records a `ShaderGraphSpan` for every node's statements and for every uniform
  declaration a node asked for; `ShaderGraphSource.NodeAt` turns a line of the emitted text back into
  the node that wrote it, and `ShaderGraphDocument.SourceNodeDiagnostics` is what a panel shows.
  ⚠ **The node a span names is put through `NodeGraphCompiler.Inlining` before it is written down**,
  so a line that came out of a sub-graph names the sub-graph node in the author's own graph rather
  than the copy the flattener made. A line the compiler wrote for itself — the preamble, the vertex
  stage, the master's `return` — belongs to nobody, and is reported as a line number rather than
  blamed on whichever node is nearest.
- **Previews of a node that needs a resource.** A preview binds one uniform block — the two
  transforms every graph declares — and nothing else, so `Texture/Sample 2D` is refused rather than
  drawn against an unbound descriptor. Binding a *material's* textures means knowing which material,
  which is doc 08's material compiler.

Licensed under Apache-2.0.

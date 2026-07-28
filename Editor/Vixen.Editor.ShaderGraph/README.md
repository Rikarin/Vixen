# Vixen.Editor.ShaderGraph

Shaders authored as a graph, compiled to Raven source.

The node library and the emission; the framework underneath is
[`Vixen.Editor.NodeGraph`](../Vixen.Editor.NodeGraph/README.md). There is no UI here and none is
needed to check any of it.

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
| Vector | Combine, Split, Tiling and Offset |
| Texture | Sample 2D |
| Master | Unlit, Sprite, PBR |

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

## The shape of what comes out

The vertex stage is fixed and the pixel stage is the graph. A shader graph is about what a surface
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

    [PixelShader] [Semantic("SV_Target")]
    func Pixel(): float4 {
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

## The PBR master is honest about being half a job

It emits a self-contained Lambert plus GGX with one directional light from uniforms. A master that
called into `Vixen.Shaders.Shading` would be the right long-term arrangement and would mean a graph's
output could not be compiled without the library on the include path — which is exactly what the
golden tests compile it without. So it is inline: enough to be a real shader, checkable on its own,
and a change to one method when it is time to wire it into the engine's clustered lighting.

## What is not here yet

- **Procedural nodes.** Noise, gradients, shapes. The `VfxNoise` transcription in `Vixen.Vfx` is the
  obvious source for a value-noise node, and it is not wired up.
- **A custom-code node**, which doc 11 lists. It is a `[Input]`-less node holding a string of Raven,
  and the interesting part is not the node but what happens to a diagnostic inside it.
- **Post and UI masters.** Unlit, Sprite and PBR are in; doc 11 names five.
- **Diagnostics mapped back to ports.** Half of it is here — every diagnostic this compiler raises
  names a node and a port. The other half needs the emitter to record which node wrote which span, so
  that Raven's own complaints can be mapped back.
- **Preview thumbnails.** `[Node(Preview = true)]` is recorded on the types that would want one; what
  draws it is a view.

Licensed under Apache-2.0.

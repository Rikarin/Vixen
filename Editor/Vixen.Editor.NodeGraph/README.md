# Vixen.Editor.NodeGraph

The node-graph framework the shader graph, the VFX graph and — when it exists — the animation graph
are all built on.

Building three node editors is three times the work of building one well-factored one, so
[doc 11](../../docs/plan/11-editor.md) says to build one. This is that one: the document model, the
generated node registry, and the compiler that walks a graph and hands each node its resolved ports.
What each graph's nodes *mean* is that graph's; everything above is here.

```csharp
var registry = new NodeTypeRegistry();
MyLibrary.NodeTypes.Register(registry);          // generated, one per assembly

var graph = new NodeGraphModel { Name = "Tinted" };
var uv = graph.Add("Input/UV");
var sample = graph.Add("Texture/Sample 2D");

graph.Connect(new(uv.Id, "UV"), new(sample.Id, "UV"));
```

## What is here

| | |
|---|---|
| `NodeGraphModel` | Nodes, edges, groups and comments. Refuses cycles, topologically orders. |
| `NodeGraphAsset`, `NodeGraphDocument` | The file shape, and the checked conversion between it and the model. |
| `NodeAttribute`, `InputAttribute`, `OutputAttribute` | What a node type declaration looks like. |
| `PortKind`, `PortKinds` | What a port carries, and the rules — including `DynamicVector`'s. |
| `Scalar`, `Float2`…`Float4`, `DynamicVector`, `Bool`, `Int`, `Texture`, `Sampler`, `Flow` | Port field types. The declared type *is* the port's kind. |
| `NodeTypeRegistry`, `NodeTypeDefinition` | The node library, filled by generated code. |
| `Node`, `NodeBinding` | The base a node derives from, and what its ports carry this time round. |
| `NodeGraphCompiler<T>` | Graph to artefact: ordering, typing, binding, diagnostics. |
| `NodeDiagnostic` | Something to say, about a node and a port an author can see. |

## A node is a class, and the generator reads it once

```csharp
[Node("Math/Lerp", Preview = true)]
public sealed partial class LerpNode : ShaderNode {
    [Input] public DynamicVector A;
    [Input] public DynamicVector B;
    [Input] public Scalar T = 0.5f;
    [Output] public DynamicVector Result;

    protected internal override void Emit(RavenEmitter e) => e.Emit($"{Result} = lerp({A}, {B}, {T})");
}
```

That is doc 11's example, and it compiles. A plugin adds nodes by adding classes: there is no table
to register in and no switch to extend.

**The ports and the metadata come from one declaration.** They could have been two — ports declared
in the attribute, fields written to match — and they would then be two lists that have to agree, with
a renamed port being an edge that silently stops arriving. Reading both off the same text is the only
arrangement where that cannot happen.

**Generated rather than reflected.** Scanning assemblies for the attribute is shorter code and costs
a reflection pass at startup, an AOT hazard, and a node library whose contents depend on what
happened to be loaded. It is also what lets `Create` be a `new` rather than an `Activator` call.

**Registration is per assembly and a host picks.** `NodeTypes.Register(registry)` adds that
assembly's types. Nothing is global, which is why a shader graph's create menu contains no spawner
nodes and a test can have a registry with three types in it.

## `DynamicVector` is the interesting part of the type system

A `Lerp` works on floats, on colours and on positions. Authoring three of it is what a graph without
this looks like, so a port may be *dynamically* typed and resolved by what it is connected to.

**The widest wins and everything narrower is promoted.** A `Lerp` with a `float3` and a `float` is a
`float3` lerp with the scalar splatted — what an author means, and what every shader language already
does for `float3 * float`. Refusing the mixture would turn the common case into an error and a manual
splat node.

**A node with nothing connected is a float.** It has to be something, and the narrowest is the one
that promotes into anything later. An unconnected scalar default splats to whatever the node turned
out to be, so a `0.25` typed into a port that became a colour is a grey rather than a red.

**Resolution travels.** A dynamic *output* is as wide as its own node resolved to, and because the
walk is in dependency order that node was resolved first. A chain of dynamic nodes fed by one colour
is a chain of `float4`s.

**A texture is not a width.** It arrives at a dynamic port as a type error reported against the port,
not as something to widen: there is no width a texture and a float agree on.

## The model refuses what it cannot represent

**A cycle is refused as it is made.** A graph that cannot contain one is a graph the compiler can
walk without a visited set and a view can lay out without one. Allowing it and reporting later means
every consumer has to be robust against a structure the model already knows is wrong, and an author
finds out about a mistake at a different time from making it.

**An input takes one edge.** Connecting a second replaces the first, because that is what dragging a
wire onto an occupied port means every time. An output takes any number.

**An identity is never reused.** That is what makes undo cheap: a re-added node comes back under the
identity it had, so the edges the same command is about to restore still name it. Renumbering would
mean rewriting every edge, in an order the two lists would have to agree on.

## The file is a separate shape

`NodeGraphAsset` is what was written down — including whatever a hand edit or a bad merge left.
`NodeGraphModel` is the thing with invariants. `NodeGraphDocument.Load` is where the checking happens
and where a broken file produces a diagnostic rather than a half-built model.

**It repairs and says what it repaired.** An edge naming a node that is not in the file is dropped; a
duplicated identity is dropped; an edge that would close a cycle is dropped. Every one of those is
what a bad merge produces, and an editor that will not open the file is an editor that cannot be used
to fix it. A file from a *later version* is refused, because there it cannot tell what the bytes mean.

**Identities survive the round trip**, so a save, load and save cycle is a no-op in the diff.
Positions are two floats rather than a nested vector, for the same reason: this is a file people read
and merge.

**No file format is chosen here.** The document types are `[DataContract]` records described by the
reflection generator; a caller writes them as YAML or bakes them with the binary serializer. Nothing
that only wants to compile a graph links a parser.

## The compiler does the parts that are the same everywhere

Walking in dependency order, resolving dynamic ports, naming the variable an output writes,
converting a value that arrives at a port of a different width, and reporting all of it against the
node it came from — none of that differs between a shader graph and a VFX graph. Three things do, and
they are the three abstract members: how a constant is spelled, how a conversion is spelled, and what
a bound node *does*.

**Names come from identity, not a counter.** An output's variable is `n{id}_{port}`, so compiling the
same graph twice produces the same source — which a golden test needs, and which makes a diff between
two saved versions mean something.

**It keeps going after an error.** A graph with a missing node type *and* a badly typed edge reports
both, because an author fixing one at a time is an author compiling five times.

**Both forms of a value are handed over.** A shader graph reads the expression and interpolates it
into a line of source; a VFX graph is building an array of operations whose parameters are numbers,
and parsing them back out of a literal it had just formatted would be absurd. So the resolution
happens once and the binding carries both.

## What is not here yet

- **The view.** `NodeGraphView` — pan, zoom, marquee, wire routing, minimap, search-to-create — is
  the other half of doc 11's sketch and needs an editor shell that does not exist. Everything here is
  testable without one, which is deliberate: the model, the typing and the emission are checked
  against numbers and text rather than against a screenshot.
- **Sub-graphs.** The model holds nodes, edges, groups and comments; a node that *is* another graph
  needs entry and exit nodes whose ports come from the sub-graph's own, and inlining at compile time.
  It is the one bullet of doc 11's model list that is not in.
- **Undo commands.** The model is shaped for them — `Remove` hands back the edges it detached, and
  `Restore` puts a node back under its identity — but the `IEditorCommand` implementations that use
  those live with the editor shell.
- **Diagnostics mapped back from Raven.** A `NodeDiagnostic` carries a node and a port, which is half
  of doc 07's requirement. Mapping a *generated shader's* span back to the node that emitted it needs
  the emitter to record spans as it writes, and nothing does yet.

Licensed under Apache-2.0.

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
| `NodeGraphModel` | Nodes, edges, groups, comments and a sub-graph interface. Refuses cycles, topologically orders. |
| `NodeGraphAsset`, `NodeGraphDocument` | The file shape, and the checked conversion between it and the model. |
| `NodeAttribute`, `InputAttribute`, `OutputAttribute` | What a node type declaration looks like. |
| `PortKind`, `PortKinds` | What a port carries, and the rules — including `DynamicVector`'s. |
| `Scalar`, `Float2`…`Float4`, `DynamicVector`, `Bool`, `Int`, `Texture`, `Sampler`, `Flow` | Port field types. The declared type *is* the port's kind. |
| `NodeTypeRegistry`, `NodeTypeDefinition` | The node library, filled by generated code. |
| `Node`, `NodeBinding` | The base a node derives from, and what its ports carry this time round. |
| `NodeGraphCompiler<T>` | Graph to artefact: ordering, typing, binding, diagnostics. |
| `NodeDiagnostic` | Something to say, about a node and a port an author can see. |
| `SubGraphs`, `SubGraphLibrary` | A graph inside a graph: the boundary nodes, inlining, and extraction. |
| `NodeGraphView`, `NodeCommentView` | The model on a `NodeCanvas`, with every gesture arriving as a command. |
| `NodeInspector` | The selected node's values as rows beside the canvas, for what will not fit on it. |
| `NodeSearchPopup`, `NodeSearch`, `PortFilter` | Search-to-create, and the ranking behind it. |
| `NodePreviewLayer`, `INodePreviewSource` | The swatch under a node that asked for one. |
| `NodeGraphLayout`, `NodeLayoutOptions` | Laying a graph out left to right, in columns. |
| `NodeGraphClipboard` | Copy and paste, as a fragment that is itself a graph. |
| `AddNodeCommand`, `RemoveNodesCommand`, … | Every edit, reversibly. See below. |
| `NodeGraphTheme` | The stylesheet the four elements this assembly adds come with. The CSS is `NodeGraphTheme.vcss` beside it — a file, not a `const string` — embedded by the `**/*.vcss` glob in `Vixen.Ui.targets`. |

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

**A port's inline value is lanes of `float` or a string, never both.** `GraphNode.Values` is what a
shader graph and a VFX graph are made of; `GraphNode.Texts` arrived for the graphics compositor,
which is made of **names** — a pass names its targets, a full-screen node names its shader — and
there is no float encoding of a name that is not an index into a table somebody has to keep.
`SetPortTextCommand` is `SetPortValueCommand`'s twin, down to keeping whether there *was* a text and
merging so that typing a resource name is one undo entry.

⚠ **A key no port claimed still reaches the binding.** A node type may hold a setting keyed like a
port and declared as none — nothing connects to it, and giving it a socket would put a wire nobody
can attach on the canvas. Those keys are added to `NodeBinding` *after* the ports are bound and only
where nothing claimed them, so a **connected** port still answers "no inline value" — otherwise a
VFX node would start reading the number an author typed before wiring something up.

**No file format is chosen here.** The document types are `[DataContract]` records described by the
reflection generator; a caller writes them as YAML or bakes them with the binary serializer. Nothing
that only wants to compile a graph links a parser.

## An unconnected input is typed into on the node

A graph is read by looking at it, so the number a `Multiply` is multiplying by is on the
`Multiply` — a box of digits in the port's own row, drawn for every input that has no wire in it.
`NodeInspector` is still there and still shows the same values, for a node whose ports do not fit
across 240 units of canvas and for reading one node's settings without hunting across the graph.

**Fields, not lanes.** `PortKinds.Lanes` answers "how wide is this value in the emitted source",
which is *zero* for a boolean, an integer and an unresolved dynamic — none of them is a float
vector. `PortKinds.Fields` answers "how many boxes", which is one for all three. Asking the first
question is what left every maths node in the shader graph — whose inputs are all `DynamicVector` —
showing the word "Dynamic" where its numbers should have been.

**A dynamic port takes one number however wide it turned out to be.** The compiler pads a short
constant with its last lane, so a `0.25` typed into a port that resolved to a colour compiles as a
grey. Drawing four boxes because the node resolved to a `float4` would mean the same graph offered a
different editor depending on what was wired to a *different* port.

**The row is the same height whether or not it has a value in it.** A wire's endpoint is arithmetic
over the header height and the port pitch — see `NodeCanvas.AnchorOf` — and a port that made its own
row taller would be a port every wire on the node missed. So the boxes go *beside* the name rather
than under it, the name shrinks and is clipped before the number is, and a node is 240 units wide
because three lanes and a port called "Base Colour" is the widest a row gets.

**⚠ The canvas hides a connected port's editor; the projection does not remove it.** The canvas is
the thing that knows a wire was dropped, a frame before anything above it has recorded it. It is also
what makes pulling a wire off bring the old number back rather than a zero.

**⚠ Two gestures had to be taken back off the canvas.** A press inside a value box would otherwise
start a wire drag from the port the box belongs to, because the box is inside the port's element; and
Backspace would delete the selected node rather than a digit. Both the canvas and the view check
whether the keyboard is talking to a `TextField` before claiming a key, and both have to, because the
view claims its keys on the capture leg and the canvas on the bubble leg.

**⚠ A value edit does not reproject, which is the second exception after a drag.** Dragging across a
number field raises one command per frame, and rebuilding the projection per frame is a cost that
grows with the graph rather than with the screen. There is nothing to correct: the field wrote the
value into the canvas's own copy before telling anybody. `NodeInspector` follows via
`NodeGraphView.GraphChanged`, re-reading the rows it has rather than rebuilding them — in a framework
where removal is final, a rebuild per frame is elements that accumulate for as long as the drag. It
*does* rebuild when the node's wiring changed, which it tells apart by comparing a signature of the
selected node's ports and which of them a wire arrives at: connecting something turns a row from a
field into "from a connection", and no amount of re-reading numbers will do that.

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

## A sub-graph is inlined, not called

Every target these graphs compile to is a straight-line program over values. Raven source has no
function to call for this and a VFX operation array has no stack to put one on, so a sub-graph is a
macro: `SubGraphs.Flatten` turns a graph containing sub-graph nodes into an equivalent graph
containing none, and the compiler that walks the result has no idea sub-graphs exist. Set
`NodeGraphCompiler.SubGraphSource` and it happens before the walk; leave it unset and a sub-graph node
is a node type nobody registered, which is already a diagnostic naming the node.

**The interface is declared, not derived.** `NodeGraphModel.Interface` is the list of ports a
containing graph wires against. Deriving it from the entry and exit nodes instead would mean deleting
one node silently changed the signature every containing graph depends on.

**⚠ The entry node's ports face the other way from the interface's.** A port the interface calls an
*input* — a value fed in from outside — is an *output* of the entry node, because inside the graph
that is where the value comes from. Getting this backwards produces a sub-graph whose wires all refuse
to connect and no clue as to why.

**The top-level identities survive inlining and the inlined ones do not.** A diagnostic about the
author's own graph names a node they can select. What comes out of a sub-graph is new, so a complaint
about one names something they cannot click on — a real gap, whose fix is a map from the synthetic
identity back to the sub-graph node, which nothing yet reads.

**An unconnected sub-graph input becomes an inline value on whatever it fed.** The entry node
disappears, so there is nothing left to carry a default; pushing it down is what keeps a sub-graph
dropped in and not wired up doing what the graph it stands for does.

## The view is a projection, and it is one direction

`NodeGraphView` puts a `NodeGraphModel` on a `NodeCanvas`. Two graph types are involved and the view
is the only thing that knows both: the model is the document — identities, port names, saved and
diffed — and `Vixen.Ui.Controls.Advanced.NodeGraph` is boxes with sockets on and no idea what a node
type is. Keeping them apart is what lets the model be tested against numbers and the canvas against a
fixture with three nodes called "a", "b" and "c".

**Every structural change reprojects the whole graph.** A cheaper incremental update is possible and
was not written: the canvas culls to the viewport, so the expensive part is bounded by the screen
rather than by the graph, and a projection that is rebuilt cannot drift from the model. A drag is the
one thing that does *not* reproject — it writes positions in place — because that is the path that
runs every frame.

**⚠ The canvas edits its own copy optimistically, and that is on purpose.** Dragging a wire connects
it in the picture before anything is recorded, which is what makes the gesture feel live. What follows
is either a command — and the reprojection agrees with it — or a reprojection alone, which puts the
picture back. A canvas that had to ask permission before drawing would have a frame of latency in
every gesture.

**⚠ Two of the canvas's behaviours are intercepted rather than configured.** Delete is claimed by a
capture-phase key handler, because the canvas would otherwise remove nodes from its own copy and tell
nobody. And a wire *picked up* off a connected input — the canvas's reroute gesture, which it performs
by disconnecting its own graph with no event for it — is found by comparing the model's edges against
the picture's wires. Both needed nothing added to `Vixen.Ui.Controls.Advanced`.

**No stack means read-only.** Every edit goes through `Stack`, so a view without one shows a graph and
refuses to change it — which is what a preview of a sub-graph should do.

## Every edit is a command

```csharp
view.EditedDocument = document;          // takes the undo stack from it
view.Registry = registry;
view.Graph = graph;
```

`AddNodeCommand`, `RemoveNodesCommand`, `ConnectCommand`, `DisconnectCommand`, `MoveNodesCommand`,
`SetPortValueCommand`, `AddGroupCommand`, `RemoveGroupCommand`, `RenameGroupCommand`,
`AddCommentCommand`, `SetCommentCommand`, `RemoveCommentCommand`, `PasteCommand`, `LayoutCommand` and
`ExtractSubGraphCommand`. All of them work against a bare `NodeGraphModel`, so all of them are
testable without a command stack.

**Undo depends on identities never being reused**, which is the reason `NodeGraphModel.Restore`
exists. A deletion hands back the edges it detached and the group positions it emptied, and puts all
three back on undo.

**Merging is what makes a drag one entry.** Two moves of the same nodes become one, and the merged
command keeps the earlier one's starting positions — so one undo goes back to before the drag rather
than to one mouse-move ago. `CommandStack.Seal` on the pointer release is what ends the run. A value
edit merges the same way, and keeps *whether there was a value at all*: a port that never had one is
left without one rather than pinned to whatever it read on the first frame.

**⚠ `TryMergeWith` is declared on `NodeGraphCommand` and overridden**, not left to the interface's
default. Interface mapping is fixed at the type that lists the interface, so a derived class that
merely declared a matching method would never be called through `IEditorCommand` and its merging would
silently do nothing.

## Search-to-create, and drag-from-port

Dropping a wire on empty canvas opens `NodeSearchPopup` filtered to the node types with a port that
could take it, with each row saying which port it would land on — so the gesture is one drag and one
Enter rather than a drag, a menu, a guess and a second drag. Choosing one is a single undo entry
covering both the node and the wire.

**Ranked rather than filtered.** A menu that hid everything not matching makes an author who typed
`lerp` and meant `Mix` find nothing. The score is a small integer with a stated ladder — exact title,
prefix, word prefix, substring, category, summary — rather than a fuzzy distance, because a subsequence
matcher puts `Sample Texture 2D` above `Sample` for the query `st`, which reads as a shuffle. The
command palette does use one, because a command is looked up by a phrase.

**⚠ A dynamic port is not a wildcard.** It takes any *vector* and only a vector: there is no width a
texture and a float agree on, which is what the compiler reports as a type error — so offering the
node would offer a wire that is refused the moment it compiles.

## Auto-layout is layered, because a data-flow graph already is

Every wire runs from a node's right to another's left, so "how far along is this node" is a well-defined
number — the longest chain feeding it — and putting every node at its own number means no wire ever
runs backwards. A force-directed layout would be prettier in the abstract and would put a texture
sample to the right of the thing that reads it.

Longest path rather than shortest, so a node feeding the master sits beside the other things feeding it.
Crossings are reduced by the median heuristic, both directions, a fixed four times: minimising them
exactly is NP-hard, and a fixed pass count rather than "until it stops improving" is what makes the
result a function of the graph, which a golden test needs. Columns are centred on the tallest, so a
chain comes out as a line of centres.

## What is not here yet

- **Nothing here renders a thumbnail, though the layer draws one.** `NodePreview` carries either a
  colour or a render-target handle, and `NodePreviewLayer` draws both — the image case is the same
  command and the same flip question as `Viewport`. What does not exist is a *shader-graph* preview
  renderer: compiling one node's sub-expression, running it over a quad, and keeping the target alive
  across edits. That is `Vixen.Editor.ShaderGraph`'s to write, and it is why the framework's own
  fixture answers with a swatch. A swatch is not a placeholder for it either — a constant, a colour,
  a mask and a channel split all reduce to one colour, and a render target per node to say so would
  be a lot of targets to answer a question a rectangle answers.
- **A node in two groups is drawn in one of them.** The canvas's group membership is a back-pointer on
  the node, so it holds one; the model does not, because a document should not lose an author's
  grouping to a drawing limitation.
- **Sticky notes are shown, not edited.** `SetCommentCommand` is the edit; nothing in the view puts a
  caret in one yet.
- **Wires are not selectable.** `NodeCanvas` has no notion of a selected wire, so deleting a connection
  is pulling it off and dropping it on nothing.
- **Diagnostics mapped back from Raven.** A `NodeDiagnostic` carries a node and a port, which is half
  of doc 07's requirement. Mapping a *generated shader's* span back to the node that emitted it needs
  the emitter to record spans as it writes, and nothing does yet.

Licensed under Apache-2.0.

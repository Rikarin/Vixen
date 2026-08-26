---
title: Graph diagnostics that name a node
slug: editor/graph-diagnostics
kind: guide
area: Editor
summary: How a complaint about a line of generated shader source becomes a complaint about a node the author can select, across sub-graph inlining.
api: [T:Vixen.Editor.NodeGraph.NodeSpan, T:Vixen.Editor.NodeGraph.NodeOrigin, T:Vixen.Editor.NodeGraph.NodeGraphInlining, T:Vixen.Editor.ShaderGraph.ShaderGraphSpan]
tags: [editor, node-graph, shader-graph, diagnostics, raven, sub-graphs]
since: 0.1
status: preview
related: [editor/shader-graph-previews, editor/node-port-editing]
---

## What it is

A shader graph is compiled twice. `ShaderGraphCompiler` walks the graph and complains about the
graph; the Raven front end then reads the text that came out and complains about the text. The two
speak different languages — one names a node and a port, the other names a line and a column of a
file nobody wrote — and this is the pair of records that translates between them.

`ShaderGraphSpan` says **which node wrote which lines**. `ShaderGraphSource.Spans` is a list of them,
and `ShaderGraphSource.NodeAt` looks a line up in it. `NodeSpan` is the line range itself: lines and
not characters, because `RavenEmitter.Emit` writes one statement per line and nothing wraps, so a
line is the finest resolution the emitter can honestly claim.

`NodeGraphInlining` is the other half, and it is the one that makes the first half true across
sub-graphs. `SubGraphs.Flatten` replaces a sub-graph node with the contents of the graph it stands
for, giving each copy a fresh identity — because the author's own graph already owns the ones it has.
A `NodeOrigin` records where one of those copies came from: the sub-graph node in the author's graph,
the node-type path of the sub-graph it was written in, and the identity it had there.

## What it is for

Sending an author to something they can click on.

A diagnostic that says `line 23` is a diagnostic about a file that does not exist as far as the
author is concerned: it is regenerated on every compile, it is read-only in the panel, and editing it
would be editing the output of the thing they are actually authoring. A diagnostic that names a
synthetic node identity is worse — nothing in any document has that identity, so there is no node to
select, frame or put a badge on.

Both are the same failure wearing different clothes, and both are closed the same way: everything the
compiler records about *where* is recorded against a node of the graph the author has open.

## Using it

`ShaderGraphDocument` does the join. `SourceNodeDiagnostics` holds one entry per entry of
`SourceDiagnostics`, in the same order, each addressed to the node that wrote the line:

```csharp no-compile="a fragment against a document the editor already has open"
document.Compile();

foreach (var complaint in document.SourceNodeDiagnostics) {
    if (complaint.Node.IsValid) {
        Console.WriteLine($"{complaint.Node}: {complaint.Message}");
    } else {
        Console.WriteLine($"{complaint.Span}: {complaint.Message}");
    }
}
```

⚠ **`NodeId.None` means no node wrote that line**, which the preamble, the declarations of the two
transforms every graph has, the vertex stage and the master's `return` all are. Reporting the nearest
node instead would send an author to a node that is fine, so a caller says "line 14" for those and
lets them read it in the pane.

A compiler outside the shader graph reads the map through `NodeGraphCompiler.Inlining`:

```csharp no-compile="inside a NodeGraphCompiler subclass, during the walk"
var selectable = Inlining.Resolve(node.Id);
```

⚠ **`Report` already does this for every diagnostic**, so a subclass never has to. What a subclass
does have to do is call `Resolve` on any identity it records for *later* use — a span, a cache key, a
badge — because those escape the walk and outlive the flattened graph.

## Examples

**A property name with a space in it, inside a sub-graph.** The graph is well-formed and the graph
compiler has nothing to say. Raven refuses the emitted text twice — once where the uniform is
declared and once where it is read — and both complaints name the sub-graph node in the author's own
graph:

```
line 12: RVN2006: 'my' needs either a type annotation or an initializer (Tint #3)
line 23: RVN2010: The name 'tint' does not exist in the current context (Tint #3)
line 24: RVN2033: 'float4' takes 4 argument(s), but 2 were given (Unlit #2)
```

The third is the master's own line and naming the master is right: Raven keeps going after a syntax
error, so the statement after a broken one is left holding half an expression. The map reports where
each complaint *is*, not where the author's mistake was — which is the same bargain a text compiler
makes.

**A uniform's declaration belongs to the node that asked for it**, though it is the compiler that
writes the line. `RavenEmitter.Uniform` declares a name once however many nodes ask for it, so the
first asker owns the declaration; that is where a bad property name is first refused, and it is the
more useful of the two lines to be sent to.

**Nesting does not change the answer.** A node inlined out of a sub-graph inside a sub-graph still
resolves to the outermost sub-graph node, because that is the only one the open document has.
`NodeOrigin.Type` is the innermost sub-graph's path, so the sentence appended to the diagnostic still
says which graph the node was actually written in.

## See also

- [Shader-graph preview thumbnails](shader-graph-previews.md) — the other consumer of the rule that
  a node's identity is what names its variables.
- [Editing a node's ports](node-port-editing.md) — the panel a selected node's values are edited in,
  which is where a diagnostic sends the author.

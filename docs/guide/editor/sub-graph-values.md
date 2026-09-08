---
title: Computing a sub-graph's port values
slug: editor/sub-graph-values
kind: guide
area: Editor
summary: A published graph is inlined rather than called, so what its unfed ports are worth is decided during the walk — this is the seam that lets a front end compute one instead of only reading a number.
api: [T:Vixen.Editor.NodeGraph.ISubGraphValues, T:Vixen.Editor.NodeGraph.SubGraphScope]
tags: [editor, node-graph, sub-graph, texture-graph, material-authoring]
since: 0.1
status: preview
related: [editor/texture-graph-compiling, editor/graph-diagnostics, editor/texture-graph-generators]
---

## What it is

A sub-graph is a **macro**. `SubGraphs.Flatten` replaces every node standing for a published graph
with that graph's contents, and the compiler that walks the result has no idea sub-graphs exist. So a
port of a sub-graph node has to be given a value *during* that replacement: the node carrying it is
gone by the time anything is compiled.

`ISubGraphValues` is who the flattening asks. It is handed a `SubGraphScope` — the graph the sub-graph
node was **written** in, that graph's expansion number, that expansion's settings, and the node a
complaint should name — and the keys it said it claims, and answers with a value per port.

## What it is for

One front end wants to write arithmetic on a published graph's port: `Radius` is not `8`, it is
`Amount * 0.5`. In the texture graph that arithmetic is Raven, folded against the containing graph's
parameters, and the numbers it reads are not knowable until the walk reaches that node.

You do not want it for a value that is simply a number. `GraphNode.Values` already carries those and
the flattening reads them without asking anybody.

## Using it

```csharp no-compile="a fragment; the resolver is normally a compiler, not a standalone class"
sealed class Resolver : ISubGraphValues {
    // Which keys on a sub-graph node are this front end's. The graph model is never told the
    // spelling, so a second front end can adopt the seam without agreeing about the character.
    public bool Claims(PortDefinition port, string key) =>
        key.StartsWith('=') && key[1..] == port.Name && port.Kind == PortKind.Float;

    public IReadOnlyDictionary<string, float[]> Resolve(
        SubGraphScope scope,
        IReadOnlyDictionary<string, string> claimed
    ) {
        // `scope.Type` is "" for the author's own graph and a node-type path for a compound; the
        // parameters an expression may read are that graph's, and `scope.Settings` is what this
        // instance of it was given.
        var values = ValuesFor(scope);

        return claimed.ToDictionary(entry => entry.Key, entry => new[] { Fold(entry.Value, values) });
    }
}

var flattened = SubGraphs.Flatten(graph, library, new Resolver(), out var diagnostics, out var inlining);
```

`NodeGraphCompiler` does the wiring for a compiler: override `SubGraphValues` to return the resolver,
and override `Prepare` for anything it has to read off the author's own graph — `Begin` sees the
flattened graph and runs *after* the resolver has already been asked.

## How it works

The flattening walks a graph, and every time it meets a node whose type the sub-graph source knows it
descends into that graph. On the way in it works out where each of the child's interface inputs is fed
from. An input a wire answers is done. Only the ones left over are offered to the resolver, one call
per node, and what comes back beats the number typed on the port; a port the resolver leaves out keeps
that number, or the port's declared default.

The scope handed over is the **enclosing** one, not the child being expanded. That is the whole
subtlety: a sub-graph node nested inside a compound is written against *that* compound's parameters
with *that* instance's overrides, and only the recursion knows which those are.

## Things to know

⚠ **A wire wins, silently.** A connected port is never resolved. That is the same rule that makes a
wire beat a number typed into a port, and a diagnostic here would be a rule sub-graph nodes alone had.
It is also the cost story: a resolver that compiles something is never asked to compile a value nothing
would read.

⚠ **One call per node, not per port.** A node with four claimed fields is one `Resolve`. A front end
that compiles a source to fold an expression should compile one source for the four, which is what the
batching is for.

⚠ **The blame is the outermost node.** `SubGraphScope.Blame` is the sub-graph node on the author's own
canvas, not the one two graphs down — a complaint naming a node nobody can select is a complaint
nobody can act on.

⚠ **What comes back is copied.** The flattening copies each `float[]` on the way in, so a resolver may
reuse its buffers.

## Examples

A resolver that answers one port with a computed number, and declines the rest:

```csharp no-compile="illustrative — a real front end folds through its own expression compiler"
sealed class Doubler : ISubGraphValues {
    public bool Claims(PortDefinition port, string key) =>
        key == "x2:" + port.Name && port.Kind is PortKind.Float;

    public bool Resolve(SubGraphScope scope, PortDefinition port, string key, out float value) {
        value = port.Default.Length > 0 ? port.Default[0] * 2f : 0f;

        return true;
    }
}
```

⚠ **`Claims` is asked before `Resolve`, and it is what keeps a front end's convention out of the
graph model.** The flattener knows nothing about `x2:` or about `=`; it asks whether this resolver
owns the key it found, and offers the port only if the answer is yes.

⚠ **Only scalar kinds are worth claiming.** A resolver answers *one number*, and the flattener writes
that as the port's whole value — so claiming a `Float4` would splat one lane across four. The texture
graph's own resolver names `Float`, `Int` and `Bool` positively for that reason.

## See also

- [Compiling a texture graph](texture-graph-compiling.md) — the front end that implements this, and
  the `=` convention it keeps to itself
- [Graph diagnostics](graph-diagnostics.md) — what a refusal about an inlined node looks like

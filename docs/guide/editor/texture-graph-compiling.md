---
title: Compiling a texture graph
slug: editor/texture-graph-compiling
kind: guide
area: Editor
summary: The compiler that turns a node graph into a texture plan, and the four things it hands back besides the plan — the outputs by usage, the pictures a host still owes it, the parameters an author exposed, and the per-node images a preview draws.
api: [T:Vixen.Editor.TextureGraph.TextureGraphCompiler, T:Vixen.Editor.TextureGraph.TextureGraphOutput, T:Vixen.Editor.TextureGraph.TextureGraphExternal, T:Vixen.Editor.TextureGraph.TextureGraphExternals, T:Vixen.Editor.TextureGraph.TextureProjectImages, T:Vixen.Editor.TextureGraph.TextureGraphKernel, T:Vixen.Editor.TextureGraph.TextureGraphNodeImage, T:Vixen.Editor.TextureGraph.TextureGraphParameter, T:Vixen.Editor.TextureGraph.TextureGraphParameters, T:Vixen.Editor.TextureGraph.TextureGraphParameterKind, T:Vixen.Editor.TextureGraph.TextureGraphSettings]
tags: [editor, texture-graph, material-authoring, node-graph, compiler]
since: 0.1
status: preview
related: [editor/texture-graph-evaluation, editor/texture-graph-plugin, editor/graph-diagnostics]
---

## What it is

`TextureGraphCompiler` is a `NodeGraphCompiler<TexturePlan>`: it walks a `NodeGraphModel` and produces
a [texture plan](texture-graph-evaluation.md), which is the only artefact anything downstream reads.
A graph and a layer stack both compile through this one type, which is what stops them acquiring two
opinions about what a node means.

The plan is the answer. Everything else on the compiler is a **side table** the plan has no room for,
because a plan is a list of images and dispatches and nothing else:

| Side table | What it answers |
| --- | --- |
| `Outputs` — `TextureGraphOutput` | Which image is the base colour, which is the roughness. A plan numbers its images and does not name them. |
| `Externals` — `TextureGraphExternal` | Which images the graph did not produce, and what file each one wants. |
| `Parameters` — `TextureGraphParameter` | The knobs an author exposed, with their ranges. |
| `NodeImages` — `TextureGraphNodeImage` | Which image each node's port wrote, so a preview can draw one node. |
| `Kernels` — `TextureGraphKernel` | Which Raven source each dispatched kernel came from. |

## What it is for

⚠ **A plan cannot say what an output is *for*.** `TexturePlan.Outputs` is a list of image indices, so
a baker holding one has no way to know which index becomes `basecolor.png`. `TextureGraphOutput` is
the pairing — `(Usage, Image, Node)` — and it is a side table rather than a field on the plan because
the plan is also what a layer stack compiles to, and a layer stack's usages come from its channels
rather than from a node.

The same reasoning covers the rest. A preview that draws one node needs to know which image that node
wrote, and the plan has thrown the node away by then.

## Using it

```csharp no-compile="illustrative — a real caller resolves its registry from the plugin host"
TextureGraphCompiler compiler = new(registry) {
    BaseWidth = 1024,
    BaseHeight = 1024,
    Seed = 5501u
};

var compilation = compiler.Compile(graph);

if (compilation.Artefact is not { } plan) {
    // Every refusal is a NodeDiagnostic on the compilation, addressed to a node.
    return;
}

foreach (var output in compilation.Outputs) {
    // output.Usage is "baseColor"; output.Image indexes plan.Images.
}
```

## The externals a host still owes

A `Source/Bitmap` or a `Source/Mesh Map` names a file the compiler cannot open — it has no project and
no device. So it allocates the image, marks it external, and records what it wants in a
`TextureGraphExternal`: the image index, the node to blame, the asset reference, and the size and
texels **if it already has them**.

`TextureGraphExternals.Upload` puts the ones that carry texels onto a device and hands back the ones
that do not, so a host can resolve those itself:

```csharp no-compile="illustrative — `uploads` and `plan` come from a live evaluation"
var owed = TextureGraphExternals.Upload(uploads, plan, compilation.Externals);

foreach (var entry in owed) {
    // entry.Asset is a project path this graph could not read for itself.
}
```

⚠ **A caller that ignores `owed` bakes a black image rather than failing.** An external nothing filled
is an image with no texels, and a kernel sampling it reads zero.

### Filling one from a project

`TextureProjectImages` is the second half, for a host that has an `EditorProject`. It resolves an
asset reference through the project's own asset database, refuses a picture the plan's slot cannot
hold, and uploads the rest — one sentence per entry it could not fill, in the order the plan names
them:

```csharp no-compile="illustrative — `uploads`, `plan` and `project` come from a live host"
var unresolved = TextureProjectImages.Fill(project, uploads, plan, owed, Decoded);
```

⚠ **The read is a callback and not something this does for you**, which is the one thing to know
before calling it. Choosing a decoder for a file extension belongs to the assemblies that own image
formats, and reaching them from here would put the whole runtime behind a bake — so a host hands in a
`Func<string, (TextureData? Picture, string? Unreadable)>` that opens the file it is given. That is
also where a cache goes: the editor answers out of the store holding the session's live pixels, so a
preview re-evaluating on every edit does not re-decode a 4K PNG, and a build script simply opens the
file.

⚠ **A reference carrying a scheme is refused rather than looked up as a path.** `meshmap:curvature`
and `vxpaint:…` name something a live editor session supplies; a host that can fill one claims it
*before* calling this, and one that cannot gets a sentence saying so rather than a missing-file
message about a file nobody named.

## Parameters, and the two directions they travel

`TextureGraphParameter` is a knob an author exposed: a name, a `TextureGraphParameterKind`
(`Scalar`, `Integer` or `Boolean`), a default, a range, and a group.

They travel both ways, which is what `TextureGraphParameters` is for:

* `Definition` and `Settings` turn declared parameters into the `SettingDefinition`s a node inspector
  draws, so a published sub-graph's knobs appear on the node that uses it.
* `Declared` reads them back out of a definition, so a graph that was published and reopened still
  knows what it exposed.
* `Kind` maps a parameter kind to the node graph's own `SettingKind`.

`Check` is the refusal: a duplicate name, an empty one, or a default outside its own range.

## Settings on the graph itself

`TextureGraphSettings` reads the three numbers that belong to the whole graph rather than to a node —
the base width, the base height and the seed — out of the model's own settings, with `Declare` writing
them and `Extent` and `SeedOf` reading them back with a refusal rather than an exception.

⚠ **The extent is the *authoring* size, not the bake size.** A bake at another resolution moves
`BakeLevelOffset`, and every length in the graph stays in texels at the base — which is
[the resolution rule](texture-graph-evaluation.md) the whole plan rests on.

## Examples

Compiling a graph and finding the image a usage names:

```csharp no-compile="illustrative — `compilation` is the result of a Compile call"
static int? ImageFor(TextureGraphCompiler compiler, string usage) {
    foreach (var output in compiler.Outputs) {
        if (string.Equals(output.Usage, usage, StringComparison.OrdinalIgnoreCase)) {
            return output.Image;
        }
    }

    return null;
}
```

## See also

* [Evaluating a texture plan](texture-graph-evaluation.md) — what happens to the plan this produces.
* [The texture graph plugin](texture-graph-plugin.md) — the editor half that calls this.
* [Graph diagnostics](graph-diagnostics.md) — how a refusal reaches the node an author can click.

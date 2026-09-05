---
title: The texture graph plugin
slug: editor/texture-graph-plugin
kind: guide
area: Editor
summary: The .vxtexgraph document, the panel that edits one, and the module that registers both through the plugin contract — plus the three things a plugin still cannot do, each named with the change that would close it.
api: [T:Vixen.Editor.Texturing.TexturingModule, T:Vixen.Editor.Texturing.TextureGraphDocument]
tags: [editor, plugin, texture-graph, material-authoring, node-graph]
since: 0.1
status: preview
related: [editor/writing-a-plugin, editor/texture-graph-evaluation, editor/shader-graph-previews]
---

## What it is

`Vixen.Editor.Texturing` is the texture graph's editor half, and it is a **plugin** rather than part
of the application. `TexturingModule` implements `IEditorPlugin`; it registers a Create ▸ entry for
`.vxtexgraph`, a panel that edits one, and the command that opens the selected graph into that panel.
It asks the host for the project and the contribution registry through `PluginServices.Require`, and
it does not reference `Vixen.Editor.App` at all.

`TextureGraphDocument` is the document: a `NodeGraphAsset` on disk, exactly as a `.vxshadergraph` is,
holding nodes, edges, positions and the numbers an author typed — not the images and not the plan.

## What it is for

Authoring the graph that `TexturePlan` evaluates. The nodes come from the evaluator's own generated
registration list, so every kernel that assembly declares is in the canvas's search popup with no
edit here.

It is also the measurement doc 48 § D14 asks for. An extension API whose own authors bypass it is a
guess; this feature is the one that had to go through the front door, and what it found on the way is
below.

## Using it

A host activates it the way it activates any built-in module:

```csharp no-compile="a fragment of a composition root, against a host's own shell and services"
plugins.Activate(TexturingModule.ModuleId, TexturingModule.ModuleName, new TexturingModule());
```

Nothing else is needed. The module refuses a host that publishes no `EditorProject` or no
`IEditorRegistry`, with a diagnostic naming the missing service, rather than throwing from inside its
own `Activate`.

An author then makes a graph from **Create ▸ Texture Graph**, selects it in the Project panel, and
runs **Tools ▸ Open Texture Graph**. The panel is the node canvas with the whole texture node library
in it, and an `ImageView` beside it showing what the bake would write.

## Examples

Opening a graph without the editor, which is what a test does:

```csharp no-compile="a fragment; the project, the asset id and the path are the caller's"
var document = new TextureGraphDocument(project, asset, path);

// An empty file opens as the smallest graph that produces a map: one Source/Uniform wired into one
// Output/Output. A graph with no Output node produces no images at all, which would read as a broken
// evaluator rather than as an unfinished graph.
document.Graph.Add("Filters/Blur", new(240f, 80f));
document.Save();
```

⚠ **A file this build cannot read opens anyway**, with the reason in `LoadDiagnostics` — the panel
that could show the problem is only reachable if the document opens.

## Three things a plugin cannot do, and what would close each

Doc 48 § D14 predicted two of these and said finding out was the point. All three are confirmed, and
none is worked around: a panel that worked by cheating would make them invisible.

**No plugin can get a graphics device.** `EditorApplication.PluginPoints` publishes the project, the
scene, the drawers, the importers, the contribution registry, the editing state, the work plane, the
mesh services, the shown scene, the shown view, the deploy target, the asset-editor registry, the
reload host and the plugin host — and no `IGraphicsDevice`. There is no other route: the contract's
only channel is `PluginServices`. So doc 48's sentence stands as written — *either a device is
published through `PluginServices` or a third party cannot write anything that draws.* One
`.Add(device)` line in `PluginPoints` closes it, under the interface rather than the implementation.

**`TextureGraphCompiler` is `internal`.** `Vixen.Editor.TextureGraph`'s `InternalsVisibleTo` names
only its own test project, so the generated `NodeTypes.Register` crosses the boundary and the thing
that turns a graph into a `TexturePlan` does not. ⚠ This one survives the first fix: a device alone
would still leave the panel unable to compile what an author wires.

**An asset-editor registration cannot be undone.** `AssetEditorRegistry` has `Add` and no `Remove`,
so claiming `.vxtexgraph` from a plugin would be a registration with no matching `OnUnload` — and a
factory the editor still holds is a reference into the plugin's assembly, which pins it for the
session with no error anywhere. That is why the Create ▸ entry is `Opens: false` and why the way in
is a command. Returning an `IDisposable` from `AssetEditorRegistry.Add`, the way `IEditorRegistry`
already does, closes it.

`AddPreview` and `AddSettingsPage` — doc 36 § D4's last two rows — are still unbuilt, so a
`.vxtexgraph` has no thumbnail. That is downstream of the first item rather than beside it: a
thumbnail registry with nothing able to render a thumbnail is half a feature.

## What the panel shows, and what it does not

The canvas is real and complete: the graph, the document's own `CommandStack` behind every gesture,
and the node library in the search popup.

The preview pane carries the graph's extent and **no texture handle**, so it draws `ImageView`'s
chequerboard at the resolution a bake would write, with the zoom, the fit and the pointer readout all
in texels — and a line under it naming which of the two obstacles above this host is stopped by. It
is not a picture and does not pretend to be one.

⚠ **The base resolution is held rather than saved.** `NodeGraphModel` carries a name, a node list and
an interface, with nowhere to put a number — the same gap `TextureGraphCompiler.BaseWidth` records —
so a `.vxtexgraph` does not round-trip its authoring size yet.

## See also

* [Writing a plugin](editor/writing-a-plugin) — the contract, the four rules that make unloading work,
  and what the host publishes.
* [Evaluating a texture plan](editor/texture-graph-evaluation) — what a compiled graph becomes, and the
  resolution rule.
* [Shader graph previews](editor/shader-graph-previews) — the other graph, and why the two stay apart.

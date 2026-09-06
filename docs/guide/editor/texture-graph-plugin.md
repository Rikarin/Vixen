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

## Three things a plugin could not do. Two of them it can now

Doc 48 § D14 predicted two of these and said finding out was the point. All three were confirmed, and
none of them was worked around — a panel that reached past the plugin contract would have made the
gap invisible, which is the one thing this module exists not to do. ⚠ **All three are now closed in
the editor; the third is not yet *used* here**, and the difference is the section below.

**A graphics device — closed.** `EditorApplication.PluginPoints` publishes `IEditorGraphics`: the
editor's device to allocate on and dispatch over, and an upload that turns pixels into the number an
`ImageView` draws. ⚠ **Its predicted one-line fix — `.Add(device)` — could not have worked**, which
is the useful half of the finding: `PluginPoints` runs from `EditorApplication`'s constructor and the
host sets `GraphicsDevice` afterwards, when the window can present, so a device added there would be
`null` for the life of the process and `PluginServices.Add` throws on a second publish. What a plugin
is handed is a live view of whether there is one.
[#737](https://github.com/Rikarin/Vixen/issues/737)

⚠ **The device is handed over whole, and the narrower contract was refused for a measured reason.**
`TexturePlanEvaluator` caches a compiled pipeline per kernel and output format across evaluations, so
lending the device for the duration of one call would recompile every kernel on every preview. What
is narrowed is the return path: `Upload` takes pixels rather than a texture view, because a plugin's
image is created for what it dispatches into and a view registered from a storage image is missing
`Sampled` and in the wrong layout — which MoltenVK forgives and a discrete card does not.

**`TextureGraphCompiler` was `internal` — the type is public now, and the panel has not caught up.**
For three batches `Vixen.Editor.TextureGraph`'s `InternalsVisibleTo` named only its own test project,
so the generated `NodeTypes.Register` crossed the plugin boundary and the thing that turns a graph
into a `TexturePlan` did not — the panel could draw the node library and not compile it.
[#738](https://github.com/Rikarin/Vixen/issues/738) made the type `public`.

⚠ **What the pane shows is still the graph's base layer**, and its status line gave the old reason
for a further batch until [#816](https://github.com/Rikarin/Vixen/issues/816) — it now names
[#792](https://github.com/Rikarin/Vixen/issues/792), the gap that is actually open. A visibility that
is fixed and a gap that is closed have come apart, which is worth reading as the more general lesson
here, because it is this repository's commonest defect wearing the clothes of a fix: the plugin does
compile a canvas through the public compiler in two places — `TextureGraphDocument.Compile` and the
layer stack's `LayerStackCompiler`, which bakes a real map — and the graph pane is simply the one
caller nobody wired.
[#792](https://github.com/Rikarin/Vixen/issues/792) is that, and the sentence under the preview now
names it rather than the visibility that was closed.

**An asset-editor registration could not be undone — closed.** `AssetEditorRegistry.Add` hands back
an `IDisposable` now, the way `IEditorRegistry.Add` already did, and it gives up the editor's name
*and* every extension it claimed. So `.vxtexgraph` has a double-click, registered inside the module's
scope and gone when the module unloads, and the Create ▸ entry's `Opens` is derived from whether the
host published a registry rather than declared.
[#739](https://github.com/Rikarin/Vixen/issues/739)

`AddPreview` and `AddSettingsPage` — doc 36 § D4's last two rows — are still unbuilt, so a
`.vxtexgraph` has no thumbnail.
[#400](https://github.com/Rikarin/Vixen/issues/400)

## What the panel shows, and what it does not

The canvas is real and complete: the graph, the document's own `CommandStack` behind every gesture,
and the node library in the search popup.

The preview pane carries a real picture in a host with a device: a one-op `TexturePlan` at the
document's resolution, dispatched by `TexturePlanEvaluator` and uploaded through
`IEditorGraphics.Upload`. The extent is the document's either way, so the zoom, the fit and the
pointer readout are in the texels an author is authoring — and the line under it says the picture is
the graph's **base layer** rather than the wired graph, or, in a host with no device, which of the
two reasons the pane is empty.

⚠ **Every route into the evaluation is outside the host's own frame.**
`TexturePlanEvaluator.Evaluate` drives `BeginFrame`, `EndFrame` and `WaitIdle` on the device itself,
so a call from inside `EditorHost.Present`'s pair would reset a command pool with work still
executing in it. A command handler and a panel build both run from `EditorApplication.Update`.

⚠ **The base resolution is held rather than saved.** `NodeGraphModel` carries a name, a node list and
an interface, with nowhere to put a number — the same gap `TextureGraphCompiler.BaseWidth` records —
so a `.vxtexgraph` does not round-trip its authoring size yet.

## See also

* [Writing a plugin](editor/writing-a-plugin) — the contract, the four rules that make unloading work,
  and what the host publishes.
* [Evaluating a texture plan](editor/texture-graph-evaluation) — what a compiled graph becomes, and the
  resolution rule.
* [Shader graph previews](editor/shader-graph-previews) — the other graph, and why the two stay apart.

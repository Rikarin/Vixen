---
title: The texture graph plugin
slug: editor/texture-graph-plugin
kind: guide
area: Editor
summary: The .vxtexgraph document, the panel that edits one, and the module that registers both through the plugin contract — plus the three things a plugin could not do when this module was written, each with the change that closed it.
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

## Three things a plugin could not do. All three it can now

Doc 48 § D14 predicted two of these and said finding out was the point. All three were confirmed, and
none of them was worked around — a panel that reached past the plugin contract would have made the
gap invisible, which is the one thing this module exists not to do. ⚠ **All three are closed, and
all three are used here** — the heading said "two of them" and the paragraph under it said the third
was closed but unused, which was two answers in four lines and both behind the tree.

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

⚠ **This section said "what the pane shows is still the graph's base layer" and named
[#792](https://github.com/Rikarin/Vixen/issues/792) as the open gap; both were stale by
2026-09-08.** `TextureGraphPreview.Evaluate` compiles the open document
(`TextureGraphDocument.Compile`), refuses before asking for a device when it does not compile, takes
the first `Output` by usage and evaluates *that* plan — `Base(width, height)` is the empty
document's picture, not the pane's answer. And #792 was never this gap: it was
"six places still say `TextureGraphCompiler` is internal", closed with
[#816](https://github.com/Rikarin/Vixen/issues/816)'s corrections.

⚠ **A citation is the part of a page that rots without reading wrong**, which is the general lesson
worth keeping from the paragraph this replaces: a number stays put while what it points at closes,
and a reader who trusts it inherits a gap that no longer exists. Resolve every issue number against
`gh issue view` before repeating it.

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

The preview pane carries a real picture in a host with a device: `TextureGraphPreview.Evaluate`
compiles the open document, refuses **before** asking for a device when it does not compile, takes
the first `Output` the compilation names, and dispatches that plan through `TexturePlanEvaluator`,
uploading through `IEditorGraphics.Upload`. The extent is the document's either way, so the zoom,
the fit and the pointer readout are in the texels an author is authoring. ⚠ **This paragraph said
the pane draws "a one-op `TexturePlan`" and that its status line says the picture is the graph's
base layer; both were behind the tree.** `TextureGraphPreview.Base` is the plan for a document with
nothing wired, and the statuses an empty pane shows are a compile refusal, a missing device, or a
graph with no `Output` node — each in its own sentence.

⚠ **Every route into the evaluation is outside the host's own frame.**
`TexturePlanEvaluator.Evaluate` drives `BeginFrame`, `EndFrame` and `WaitIdle` on the device itself,
so a call from inside `EditorHost.Present`'s pair would reset a command pool with work still
executing in it. A command handler and a panel build both run from `EditorApplication.Update`.

⚠ **This section said the base resolution is held rather than saved, because `NodeGraphModel` had
nowhere to put a number. It has one.** `TextureGraphSettings.Declare` writes `baseWidth`,
`baseHeight` and `seed` into `NodeGraphModel.Settings`, and every shipped compound carries the line
— `settings: { baseWidth: '1024', baseHeight: '1024', seed: '1' }`. What deliberately stays *out* of
the file is what the bake decides rather than what the graph declares: `BakeLevelOffset`, a
`.vxsmartmat`'s overrides and the panel's preview-every-node tick, each of which in the file would be
a bake somebody saved by accident.

## See also

* [Writing a plugin](editor/writing-a-plugin) — the contract, the four rules that make unloading work,
  and what the host publishes.
* [Evaluating a texture plan](editor/texture-graph-evaluation) — what a compiled graph becomes, and the
  resolution rule.
* [Shader graph previews](editor/shader-graph-previews) — the other graph, and why the two stay apart.

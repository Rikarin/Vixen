---
title: Coming from Unity
slug: engine/unity-migration
kind: concept
area: Engine
summary: The vocabulary map, and the four places the two models genuinely differ — an entity is a row rather than an object, the frame is an asset rather than a call order, an asset is imported and built rather than loaded, and the interface is a framework rather than a canvas.
api: [T:Vixen.Core.ComponentAttribute, T:Vixen.Core.DataContractAttribute, T:Vixen.App.Game]
tags: [unity, migration, ecs, comparison, vocabulary, getting-started]
since: 0.2
status: preview
related: [engine/getting-started, ecs/components, ecs/system-order, rendering/standard-frame, assets/content-in-a-game, ui/tutorial]
---

## What it is

A translation table, and then the four places where translating stops working because the two models
disagree about something rather than spelling it differently.

It assumes Unity and explains Vixen. Everything here is also in the guide on its own terms; this page
exists because a reader who knows one engine reads a reference for a second one through the first,
and the words that look the same are exactly where that goes wrong.

## What it is for

The first hour, for the reader this engine is most likely to get — and for the specific failure of
that hour, which is not confusion. It is writing something that compiles, runs, and is quietly the
wrong shape: a component that is really a script, an ordering attribute that does nothing, a frame
edited at a call site.

## Using it

### The words

| Unity | Vixen | Not quite the same because |
|---|---|---|
| `GameObject` | An **entity** — a row number in a world | It owns nothing. Its components are what the columns at that row hold |
| `MonoBehaviour` | A `[Component]` **struct** *plus* a system | Data and behaviour are separated; see the first difference below |
| `MonoBehaviour.Update` | `SystemBase.Update` | Runs once for a whole query, not once per object |
| `[SerializeField]` on a behaviour | `[DataContract]` beside `[Component]` | `[Component]` makes the ECS store it; `[DataContract]` is what makes it *authored* — placeable in a scene, in the Add Component menu, drawn by the inspector |
| `.unity` scene | `.vxscene` | Text, authored, and built into content |
| Prefab | `.vxprefab`, with overrides | The nearest thing on the list to a straight rename |
| `ScriptableObject` | An authored asset with a `.meta` sidecar | An asset is a file the importer owns, not a class the loader instantiates |
| `.meta` files | `.meta` files | Genuinely the same idea: the settings live beside the file and the file is not modified |
| `Library/` | `Library/` | Derived, not checked in — the templates' `.gitignore` says so |
| `Instantiate` / `Destroy` | Structural changes on the world | Batched and ordered rather than immediate; see [structural changes](../ecs/structural-changes.md) |
| `Time.deltaTime` | `GameTime` | Handed to the frame rather than read off a static |
| Input Manager / Input System | Input **actions** | Closest to the new Input System, and it is the only input path |
| URP/HDRP pipeline asset | `.vxcompositor`, with `.vxpreset` tiers and `.vxlook` profiles | The second difference below |
| ShaderLab / HLSL | **Raven**, `.rvn` | A language with a compiler in this repository, not a wrapper over a vendor's |
| uGUI / UI Toolkit | `Vixen.Ui`, `.vxml` + `.vcss` | The fourth difference below |
| Editor scripts, `[CustomEditor]` | Editor plugins and markup inspectors | The editor is an application written in the same framework you write yours in |
| `.asmdef` | A `.csproj` | It is .NET all the way down; NuGet is the package manager |
| Play mode | Play mode | Same idea, and systems declare whether they run in it |

### 1. An entity is a row, not an object

This is the difference every other ECS page is downstream of, and the one that makes a
`MonoBehaviour` untranslatable as a unit.

A component is a plain struct marked `[Component]`. The ECS stores every component of one type in a
column, so a hundred thousand positions are one contiguous array — which is what makes iterating
them fast and is the entire reason for the arrangement. There is nowhere in that picture for a
method that belongs to one object.

So a `MonoBehaviour` becomes two things: the fields become a component, and `Update` becomes a
system that runs a query.

```csharp compile
using Vixen.Core;

// The fields of what would have been a MonoBehaviour.
[Component]
[DataContract]
public struct Spin {
    public float Radians;
}
```

⚠ **Do not translate a singleton into a component.** A camera setting, the frame's clock, "the
player" — a column of length one costs an archetype for nothing. That is a service or a singleton,
and Unity's habit of putting one on an empty GameObject has no counterpart here.

⚠ **Ordering is per *phase*, and an attribute that names a system the graph does not have is dropped
without a word.** `[UpdateBefore]` pointing at a renamed system, or one nobody registered, reads as
though it works and does nothing — which is a different failure from Unity's script execution order
list, where the entry at least stays visible. `SystemPlan.Unsatisfied` is what reports both, and
`vixen doctor systems` prints it.

### 2. The frame is data

There is no render pipeline to subclass and no `OnRenderImage`. A `StandardFrame` asset expands into
a compositor document, which builds a render graph of passes; quality tiers (`.vxpreset`) and look
profiles (`.vxlook`) layer over it. **Changing what draws is usually an edit to a document, not to a
call site.**

The `vixen-game` template ships one, and its `OnConfigure` names it:

```csharp no-compile="a fragment of the template's Game.OnConfigure; config is the AppConfig"
config.Graphics.Compositor = "Assets/Frame.vxcompositor";
```

⚠ **Extraction is the half a frame cannot decide**, and it is the commonest way a migrated project
gets a picture that is subtly wrong rather than obviously broken: turning shadows on in the document
also needs every mesh drawn into the `Shadow` stage, and temporal antialiasing needs the velocity
pass fed through `Motion`. Both are `config.Graphics.CasterStages` lines, and a frame configured for
something nothing is extracted into simply renders without it.

### 3. An asset is imported and built, not loaded

The flow is importer → asset database (with `.meta` sidecars) → content build → catalog and bundles.
A file dropped into `Assets/` is imported by whichever importer claims it, and the settings that
governed the import live in the `.meta` beside it — which is the one part of this a Unity reader
already knows by heart.

What is different is the far end. A shipping game reads **built content**, not source assets: the
content build produces a catalog and bundles, and what a running game asks for is an entry in that
catalog. So "it works in the editor and not in the build" has a first question here — is it in the
content build — with a place to look up the answer.

### 4. The interface is a framework, not a canvas

`Vixen.Ui` is not a game overlay with a scene-graph canvas. It is the framework the **editor** is
written in: a document, a cascade, a reactive graph, `.vxml` for the tree and `.vcss` for the style.
There is no `Canvas`, no `RectTransform` and no `EventSystem`, and the layout vocabulary is flexbox
and grid rather than anchors.

The nearest Unity thing is UI Toolkit — UXML/USS is the same idea — and the differences worth
knowing on day one are that the reactive model is signals rather than data binding by path, that a
`.vxml` compiles to a C# partial class rather than being loaded at runtime, and that the utility
class vocabulary is Tailwind's.

⚠ **`Vixen.Ui` never references `Vixen.Engine`, and that is enforced by the build.** An interface
does not get to reach into the world; a game hands it what it needs. That constraint is the whole
application-framework claim, so it is the one rule on this page that is not a preference.

## Examples

**A spinning object, both ways.** In Unity it is a behaviour with a field and an `Update`. Here it is
the struct above plus a system that runs over every entity that has one — one query, one pass, no
per-object dispatch:

```csharp no-compile="the shape rather than a compiled fragment; see the ECS guide for the real query API"
public sealed class SpinSystem : SystemBase {
    public override JobHandle Update(in SystemContext context, JobHandle dependency) {
        // one query over the Spin column, not one call per object
        return dependency;
    }
}
```

**Where the per-object habit is still right.** An interface. A panel *is* a tree of objects with
behaviour attached, and `Vixen.Ui` is that model deliberately — so the instinct a Unity reader
brings, which is wrong for the world, is right the moment they open a `.vxml`. See the
[UI tutorial](../ui/tutorial.md).

**What has no counterpart.** Coroutines. There is no `StartCoroutine`: per-frame work is a system,
and waiting on I/O is `async`/`await` on the thread pool — importers block on I/O for exactly this
reason and deliberately do not use the job scheduler, which cannot replace a blocked worker.

## See also

- [Getting started](getting-started.md) — the SDK, the feed, the six templates and the first run.
- [Components](../ecs/components.md) — what `[Component]` and `[DataContract]` each mean, and why
  the pair is the authored/internal distinction.
- [Reading a frame's order without running it](../ecs/system-order.md) — phases, the ordering
  attributes, and the two ways they silently do nothing.
- [The standard frame](../rendering/standard-frame.md) — the document a `.vxcompositor` expands
  into.
- [Content in a game](../assets/content-in-a-game.md) — the catalog and bundles a built game reads.
- [Building an interface, end to end](../ui/tutorial.md) — the fourth difference, as a panel.

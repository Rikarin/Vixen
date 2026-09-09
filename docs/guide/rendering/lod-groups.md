---
title: LOD groups
slug: rendering/lod-groups
kind: concept
area: Rendering
summary: A discrete level-of-detail chain authored as a parent carrying screen-height thresholds and children carrying their level numbers, chosen per view after culling and hidden through the visibility bitset — the LOD story for meshes with no cluster hierarchy, and for the levels a cluster DAG cannot express because they change material.
api: [T:Vixen.Rendering.Features.LodRenderFeature, T:Vixen.Rendering.Features.LodGroup, T:Vixen.Rendering.Features.LodMembership, T:Vixen.Rendering.Ecs.LodGroupComponent, T:Vixen.Rendering.Ecs.LodLevel, T:Vixen.Engine.Renderer.LodExtractionSystem]
tags: [rendering, meshes, performance, culling]
since: 0.2
status: preview
related: [rendering/mesh-and-material, rendering/foliage-rendering, rendering/instance-culling]
---

## What it is

A **LOD group** is several render objects and a rule for which of them a view sees. The near level is
the full mesh; the far one is a cheaper mesh; the last one is often not a mesh of the same kind at all
— a two-triangle impostor with a baked-out material.

Three types carry it, and they divide cleanly:

| | |
|---|---|
| `LodGroupComponent` | On the **parent**, carrying the thresholds — the fraction of the viewport's height below which each level gives way to the next, descending. Four levels take three thresholds. |
| `LodLevel` | On each **child** that is a level, carrying the number. Zero is the most detailed. |
| `LodRenderFeature` | The renderer's half: it chooses one level per group per view and clears the visibility bit of every other one. |

`LodExtractionSystem` joins the two halves — it registers each parent's thresholds with the feature
and gives each extracted child its membership.

## What it is for

**A level is a render object, not a mesh swap.** One object that changed its mesh would have to pick
one sort key for meshes that resolve to different pipelines, which is the same argument that makes a
three-material mesh three render objects. It is also what lets the last level be an *impostor*: a
different mesh with a different material is just another object.

**The choice is per view, because screen size is.** The same tree is level 0 to the camera and level 3
to a distant reflection probe, so the decision is a bit cleared in one view's set rather than a field
on the object.

**Selection happens after culling and before sorting.** It cannot happen earlier — an object outside
the frustum has no screen size to measure, and asking for one means measuring every object in the
scene rather than every visible one. It cannot happen later, because sorting is what builds the list a
hidden level would have to be absent from.

This is not the same job as either of its two neighbours, and the difference is worth stating so a
project reaches for the right one:

- **Virtualized geometry** — a mesh with a cluster hierarchy is drawn by
  `VirtualGeometryRenderFeature`, whose DAG picks a cut per view per frame and subsumes a discrete
  chain *for that mesh*. A LOD group is for the meshes that have no hierarchy, and for the case a DAG
  structurally cannot express: a level with a **different material**.
- **Instance LOD** — `InstanceCuller.Cull(…, ReadOnlySpan<float> lodDistances)` picks a level per
  *instance* of one mesh, which is what a grass field needs and what a group of distinct render
  objects cannot be. `FoliageRenderer` says the same thing from the other side.

## Using it

Author a group as a parent and its levels as children:

```csharp no-compile="a fragment; `world`, `High`, `Medium` and `Low` are a game's own"
var group = world.Create();

world.Add(group, new WorldTransform { Value = Matrix4x4.Identity });
world.Add(group, new LodGroupComponent { Thresholds = [0.25f, 0.06f] });

foreach (var (mesh, level) in (ReadOnlySpan<(AssetReference, int)>)[(High, 0), (Medium, 1), (Low, 2)]) {
    var child = world.Create();

    world.Add(child, new WorldTransform { Value = Matrix4x4.Identity });
    world.Add(child, MeshRenderables.Default(mesh));
    world.Add(child, new Parent { Value = group });
    world.Add(child, new LodLevel { Level = level });
}
```

Both components are `[Component] [DataContract]`, so a scene serialises them and the inspector shows
them; nothing here has to be built in code.

**Which children are levels.** A child with no `LodLevel` is not one — a light, a collider or a socket
hanging off the same parent is drawn whatever the group is showing. A child with a `LodLevel` whose
parent carries no `LodGroupComponent` is in no group and is drawn.

**Thresholds descend.** They are a fraction of the viewport's height —
`radius × RenderView.ScreenHeightScale / distance` — so one number means the same thing at every
resolution and every field of view.

⚠ `LodRenderFeature.Add` throws on a list that does not descend, and `LodExtractionSystem` checks
before calling it rather than letting that out of a frame: an inspector edits these one keystroke at a
time, so a list is ascending for as long as it takes to finish the second box. A group whose
thresholds ascend is left **unregistered** — every one of its levels draws, which is the picture the
scene had before it was authored — and counted in `LodExtractionSystem.Malformed`, which is what stops
that being indistinguishable from working.

⚠ **A view whose `ScreenHeightScale` is zero sees every level.** That is a shadow cascade, a
reflection-probe face and an orthographic camera, and it is deliberate: a shadow drawn from a
different mesh than its caster stops matching it.

**Hysteresis** — `LodRenderFeature.Hysteresis`, a tenth of the threshold by default — is what keeps an
object drifting across a boundary from changing mesh every frame. A level change is a different
silhouette, so the flicker is far more visible than the detail the switch was protecting.

**Cross-fade** is off (`CrossFadeDuration` is zero). During a fade *both* levels are drawn and each is
pushed a weight a material turns into a dithered discard, so a fade doubles the draws for the objects
crossing a threshold. A host that wants one sets the duration and feeds `DeltaTime` each frame.

## Examples

**Which level is showing.** The feature answers per group per view, which is what a test or an overlay
asks:

```csharp no-compile="a fragment; `renderer` is a WorldRenderer and `camera` its RenderView"
var level = renderer.Lods.LevelOf(group, camera.Index);
var fading = renderer.Lods.FadingFrom(group, camera.Index);
```

**Whether the producer is running.** Two counters, and the second is the one that reads zero on the
frame nothing is hidden:

```csharp no-compile="a fragment; `renderer` is a WorldRenderer that has been Registered"
var groups = renderer.LodExtraction!.GroupCount;   // parents seen
var levels = renderer.LodExtraction.Assigned;      // children given a membership
```

A scene whose LOD parents are all registered and whose children are all still waiting on their meshes
reads the first healthy and the second zero.

**Both renderers.** `WorldRenderer.Register` adds the system for a game; `EditorWorldRenderer`
constructs one by hand and runs it after its extraction, because the editor assembles its extraction
itself and `Register` never runs there. A feature wired into one and not the other silently does
nothing in half the product.

## What is not built yet

- **No importer produces a chain.** `MeshData` has no LOD field and `ModelImportSettings` has no
  `generateLods` — the settings' own remarks put LOD generation in "the compiler that sees the whole
  model". A chain today is three imported meshes an author wires up, not something a `.fbx` brings.
- **No editor gesture builds a group.** The components serialise and inspect; there is no
  Create ▸ LOD Group and no handle that shows where a threshold falls.
- **`DeltaTime` is not fed by either renderer**, so a cross-fade needs a host that sets it. With the
  fade off — the default — nothing reads it.

⚠ **Until 2026-09-09 there was no producer at all.** `LodRenderFeature` was complete, tested and named
by neither renderer, so nothing ever called `Add`, no group was ever registered, and a scene authored
with a three-level rock drew all three of them on top of each other at every distance — with every
counter reading healthy, because the counters belonged to a feature that had nothing in it.

## See also

- [Meshes and materials, type by type](mesh-and-material.md) — where a render object and its draw come
  from.
- [Instance culling](instance-culling.md) — the per-instance LOD a grass field wants, and why it is a
  different mechanism.
- [Foliage rendering](foliage-rendering.md) — the divergence from this feature, stated as a test.
- `docs/plan/06-rendering-pipeline.md` § Geometry and materials — the frame structure that puts LOD
  selection between culling and sorting.

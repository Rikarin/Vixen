---
title: Prefab overrides
slug: editor/prefab-overrides
kind: guide
area: Editor
summary: How a scene records that an entity came from a prefab and which of its members it has changed, and what happens when the prefab changes underneath it.
api: [T:Vixen.Editor.Core.Scenes.PrefabOverrides, T:Vixen.Editor.Core.Scenes.PrefabReport, T:Vixen.Editor.Core.Scenes.PrefabReportKind]
tags: [editor, scenes, prefabs, overrides, asset-pipeline]
since: 0.1
status: preview
related: [editor/index, editor/editing-pipeline, editor/scene-menus]
---

## What it is

A prefab instance in a `.vxscene` is an ordinary entity subtree that also says where it came from and
which of its members it has changed. Three keys carry that: `prefab` names the prefab asset, `source`
names the entity inside it this one was stamped from, and `overrides` lists the members this instance
owns rather than inherits.

`PrefabOverrides` is the pure logic over those keys — read and write a member by path, mark and clear
an override, and bring a scene back in step with a prefab that has changed underneath it. It works on
the file, not on a world: no `SceneDocument`, no ECS, no project on disk.

`PrefabReport` and `PrefabReportKind` are what a reconcile could not resolve and therefore left alone.

## What it is for

A prefab exists so that a change made once reaches every place it is used. Everything an instance did
*not* change should follow the prefab; everything it did change is the author's and must survive.
That is the whole job, and the two ways to get it wrong are both silent:

- Treating "overridden" as "differs from the template" means an author who turns a lamp's intensity
  down to `0` has said something the file cannot represent, and the next reconcile turns it back on.
  **Presence in `overrides` is the override.** The value is whatever it is, including zero, including
  a value identical to the template's.
- Deleting what you cannot explain. A prefab that has been renamed, deleted or edited under a level
  leaves entities and override entries pointing at nothing. Every one of those is **kept and
  reported**; nothing removes an entity, a key or an override entry.

The design, the two models that were rejected and what is still owed are in
[plan/47](https://github.com/Rikarin/Vixen/blob/master/docs/plan/47-prefab-overrides-and-nested-prefabs.md).

## Using it

A member path is `Member` for one of the entity's own — `Name`, `Position`, `Rotation`, `Scale` — or
`Alias.Member` for one inside `components`, where the alias is the component's `[DataContract]` name.
Matching is case-insensitive; `Mark` writes the canonical spelling and keeps the list sorted.

```yaml
- id: 7f3a1c9b0e2d4a5b6c7d8e9f0a1b2c3d
  name: Turret
  position: 4 0 2
  prefab: vx:9c2e4f1a8b7d6e5f0a1b2c3d4e5f6071
  source: 1a2b3c4d5e6f70819a0b1c2d3e4f5061
  overrides: [Position, Light.Intensity]
  components:
    - !Light
      intensity: 0
```

That instance has been moved and its light turned off. A reconcile against the prefab rewrites its
name, rotation, scale, colour and range from the template, and leaves the position and the intensity
exactly as they are.

`Reconcile` takes the scene, the prefab's reference text and the prefab file, and returns how many
members took the template's value:

```csharp no-compile="a fragment; the scene and the template are whatever loaded them"
List<PrefabReport> reports = [];
var written = PrefabOverrides.Reconcile(scene, entity.Prefab, template, reports);
```

Reconciliation is an **editor-side pass, run when a scene is opened**. It never runs in the content
build and never at run time — an importer is handed an `AssetId` and no way to resolve one to a path,
so `SceneCompiler` could not open the prefab an instance names even if it wanted to. That constraint
is why the file carries every value in full rather than only the overrides, and it is why a scene
whose prefab is missing still loads with all of its content.

## Examples

Marking an edit, so that a later reconcile leaves it alone:

```csharp no-compile="a fragment; nameof over the format type this guide assumes in scope"
entity.Position = new(4f, 0f, 2f);
PrefabOverrides.Mark(entity, nameof(SceneEntityData.Position));
```

Reverting one — "give this member back to the prefab". `Clear` forgets the claim; the value returns
on the next reconcile, which is the only place that has the template to read it from:

```csharp no-compile="a fragment; the template is the caller's to load"
PrefabOverrides.Clear(entity, "Light.Intensity");
PrefabOverrides.Reconcile(scene, entity.Prefab, template);
```

Reading a member by path without knowing its type:

```csharp no-compile="a fragment; the member's type is on the descriptor, not here"
if (PrefabOverrides.TryRead(entity, "Light.Intensity", out var value)) {
    // value is boxed; the member's type is on the descriptor
}
```

Asking whether an entity is an instance at all — both halves of the link, because either alone is a
half-written file:

```csharp no-compile="a fragment; the entity comes from a scene file"
if (PrefabOverrides.IsInstance(entity)) { … }
```

## See also

- [plan/47 — Prefab Overrides and Nested Prefabs](https://github.com/Rikarin/Vixen/blob/master/docs/plan/47-prefab-overrides-and-nested-prefabs.md)
  — the format decision, the models rejected, and what an added or removed child still needs.
- [plan/08 — Asset Pipeline and Addressables](https://github.com/Rikarin/Vixen/blob/master/docs/plan/08-asset-pipeline-and-addressables.md) — why
  a reference in a scene is `vx:` text rather than a bare id.
- [plan/15 § R7](https://github.com/Rikarin/Vixen/blob/master/docs/plan/15-risks-and-open-questions.md) — the restriction this ships under:
  single-level nesting, no prefab variants.

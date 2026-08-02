---
title: Foliage mode
slug: editor/foliage-mode
kind: guide
area: Editor
summary: The mode that requires nothing — six tools, a palette, surface filters, and instance selection through the transform gizmo.
api: [T:Vixen.Editor.Terrain.FoliageMode, T:Vixen.Editor.Terrain.FoliageEdit, T:Vixen.Editor.Terrain.FoliageTool, T:Vixen.Editor.Terrain.FoliageSettings, T:Vixen.Editor.Terrain.FoliageReapply, T:Vixen.Editor.Terrain.FoliageFilters, T:Vixen.Editor.Terrain.FoliageStrokeCommand, T:Vixen.Editor.Terrain.FoliageMoveCommand]
tags: [editor, foliage, mode, painting, undo]
since: 0.1
status: preview
related: [editor/modes, editor/terrain-mode, engine/foliage, rendering/foliage-rendering]
---

## What it is

`FoliageMode` is the fourth viewport mode. It claims the `foliage` command context, puts six tools on
`1`–`6`, and hands every pointer gesture to `FoliageEdit` — which owns the volume, the palette
selection and the drag in flight.

## What it is for

Painting trees, rocks and props onto whatever is there. You want it whenever the things being placed
have identity; a rule-driven carpet of grass is the other tool, and it changes a *rule* rather than
instances.

⚠ **It requires nothing, and that is what makes it a separate mode from terrain's.** Sculpt and paint
need a terrain and act on its texels; foliage paints onto any surface, and its filter set is the
feature. One mode that did both would answer "what is the target surface" twice with different
answers.

## Using it

```csharp no-compile="a fragment; the surface and the document are the application's"
shell.Modes.Add(new FoliageMode { Document = scene, Editing = { Volume = volume, Surface = probe } });
```

| | |
|---|---|
| `1`…`6` | paint, single, fill, erase, reapply, select |
| `[` `]` | the brush radius |
| `Shift`+drag | erases, on the paint tool |
| `Escape` | abandons a stroke, or deselects |
| `Delete` | removes the selected instances |

⚠ **The digits are slots, not named tools** — `TerrainMode`'s rule, for the same reason. Blockout
claims `1`–`4`, terrain `1`–`8` and foliage `1`–`6`, all in different contexts, and view-bookmark
recall keeps all nine everywhere none of them has the focus.

⚠ **An empty palette shows the palette, not an empty strip.** The two tools that act on what is
already there — select and erase — stay reachable; the four that place things are greyed, and
`FoliageEdit.Refusal` is the sentence the panel shows instead.

⚠ **Every surface filter off is refused rather than silently landing nowhere.** A brush that does
nothing and says nothing is the version reported as broken.

## Reapply

⚠ **The tool to get right, and it is Unreal's.** It is what turns foliage from place-and-regret into
an editable thing: changing a type's scale range afterwards should re-roll the scale of existing trees
**without moving them**, and re-rolling everything is not the same operation. `FoliageReapply` is a
checkbox per property, which is exactly how Unreal does it.

⚠ **The position is never re-rolled.** That is what would move a forest somebody had already thinned
by hand — so it is not one of the flags.

⚠ **The filter pass is its own flag, because it *removes* things.** An artist tightening a slope range
expects to be asked before a third of their forest disappears.

## One stroke, one entry

⚠ **A foliage stroke's record is instances, not a rectangle.** Sculpt and paint both write a grid, so
their records are a rect of values; this writes a list, so what it holds is what it added and what it
took away.

⚠ **Redo re-adds rather than re-scattering.** The scatter is deterministic from its seed, so
re-running it would produce the same trees — but only if nothing else changed in between, and an undo
stack does not promise that. Somebody who erased a clearing, undid it, then undid the stroke before it
has changed what the spacing rejection sees.

⚠ **Addresses do not survive the round trip, and the command does not pretend they do.** Undoing
removes what the stroke added, which shifts every index after it. The command works in instances and
the editor re-resolves its selection after any edit.

## Selection and the gizmo

⚠ **A move across a cell boundary re-cells the instance, so its address changes** — and a gizmo still
holding the old one would move a different tree on the next drag, which reads as the gizmo drifting.
`FoliageMoveCommand` hands the new addresses back after each apply.

⚠ **The selection survives leaving the mode; a half-painted stroke does not.** A stroke belongs to a
gesture that is over; a selection is a statement about which trees somebody is working on, and losing
it on a trip to the outliner is the version people complain about.

## Examples

A stroke driven from world points, which is what a test does and what the pane does with a ray:

```csharp no-compile="a fragment; the ground points come from the surface probe"
edit.Choose(pine);
edit.Begin(start);
edit.Extend(next);

if (edit.Commit() is { } command) {
    document.Stack.Execute(command);
}
```

Re-rolling the scale of a wood after widening its range:

```csharp no-compile="a fragment"
edit.Settings.Tool = FoliageTool.Reapply;
edit.Settings.Reapply = FoliageReapply.Scale;

edit.Begin(over);
edit.Commit();
```

Picking one out and lifting it:

```csharp no-compile="a fragment"
edit.Select(where);

if (edit.MoveSelection(new(0f, 2f, 0f)) is { } moved) {
    document.Stack.Execute(moved);
}
```

## What is owed

Removing a palette entry is registered as **unavailable with the reason** rather than left absent: it
renumbers every index above it in the chunks, the selection and every undo entry on the stack. The
`.vxfoliage` importer is the same owed item `.vxlayer` has — this is the content and the form, and
turning either into a file belongs with `Vixen.Editor.Assets`.

## See also

- [Editor modes](modes.md) — the seam this is the fourth consumer of.
- [Sculpt and paint mode](terrain-mode.md) — the other one, and why they are two.
- [Foliage instances](../engine/foliage.md) — the kernel behind every tool here.
- [docs/plan/31 § T5](https://github.com/Rikarin/Vixen/blob/master/docs/plan/31-terrain-grass-and-trees.md) —
  the phase this is, and its exit criterion.

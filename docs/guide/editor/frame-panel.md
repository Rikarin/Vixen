---
title: The frame panel
slug: editor/frame-panel
kind: guide
area: Editor
summary: Editing a .vxcompositor as the Standard Frame's knobs, with the resolved quality waterfall and the per-camera volume stack shown beside them, and Explode as the one-way door out.
api: [T:Vixen.Editor.AssetEditors.Frame.StandardFrameDocument, T:Vixen.Editor.AssetEditors.Frame.StandardFrameView, T:Vixen.Editor.AssetEditors.Frame.StandardFrameEditorFactory, T:Vixen.Editor.AssetEditors.Frame.StandardFrameSettings, T:Vixen.Editor.AssetEditors.Frame.FrameQualityChoice, T:Vixen.Editor.AssetEditors.Frame.LookSettings, T:Vixen.Editor.AssetEditors.Frame.ResolvedQualityTable, T:Vixen.Editor.AssetEditors.Frame.ResolvedQualityKnob, T:Vixen.Editor.AssetEditors.Frame.QualityLayer, T:Vixen.Editor.AssetEditors.Frame.ResolvedVolumes, T:Vixen.Editor.AssetEditors.Frame.ResolvedVolumeReport, T:Vixen.Editor.AssetEditors.Frame.ResolvedVolumeParameter, T:Vixen.Editor.SceneView.IActiveView]
tags: [editor, rendering, standard-frame, quality, volumes, inspector]
since: 0.1
status: preview
related: [rendering/standard-frame, rendering/render-quality, rendering/post-process-volumes, editor/inspectors-in-markup, editor/editing-pipeline]
---

## What it is

Double-clicking a `.vxcompositor` opens the frame panel. A document whose `game:` is a
`!StandardFrame` opens as its eight semantic knobs and its look profile, both as ordinary inspector
forms; a hand-authored document opens read-only. Under the forms are four readouts:

* **What it expands to** — the stages, targets and buffers the node becomes, rebuilt on every edit,
  with the expansion's own guardrail refusals shown where they happen.
* **Resolved quality** — every knob of the quality table, its value, and **which of the three layers
  decided it**: the engine's own defaults, the project's `RenderQuality.vxpreset`, or the document's
  inline `preset:`.
* **Volumes reaching the camera** — the fold `PostProcessVolumeSystem` performs, per camera:
  *N* of *M* volumes contributing, and per parameter the value, the weight it won at, and the layer
  that had the last word.
* **Explode to a full document** — doc 39's escape hatch, as a button.

`StandardFrameEditorFactory` claims the extension; `StandardFrameDocument` is the model and
`StandardFrameView` the panel over it.

## What it is for

The frame document is the one file every project ships and, until this panel, the one file nothing
opened. More importantly, every number it decides comes out of a fold: the quality waterfall folds
per parameter across three files, and the volume stack folds across four layers. A panel that showed
only the final number would answer "what is it" while the question anybody actually arrives with is
"why is it *that*" — and would send them to edit the wrong file. So both tables show provenance
beside the value, and the ones that came from somewhere other than the engine's own table are the
ones that are coloured.

```yaml no-compile="the whole of a project's frame — the panel is a form over exactly this"
version: 2
game: !StandardFrame
  quality: High
  shadows: Cascades
  gi: Ambient
  antialiasing: Taa
  exposure: Automatic
```

## Using it

**The knobs are live.** Every row writes through to the node and re-runs the expansion, so turning
`shadows` from `Cascades` to `Off` takes the caster stage and its atlases out of the counts under
the form as you watch — no save, no restart. Nothing is written to disk until you save; what is live
is the expansion the panel is showing you.

**The look profile is optional per parameter.** Each row is a tick and a value: ticked is an
opinion, unticked says nothing and lets whatever is under it stand. That distinction is the whole of
doc 32's overlay model, and a look that sets `fogDensity: 0` has cleared the fog where one that says
nothing about fog has not.

**"Only what is overridden"** cuts the resolved table from sixty-odd rows to the handful your own
files have claimed. A row is *overridden* when a layer above the engine **states** it — not when it
changes it, because a preset that pins the engine's own number has still taken ownership of it and
is still where you would go to change it.

**The volume stack folds from the focused viewport's camera**, published as `IActiveView`. Flying
into a volume and watching the count move is the check to make when a volume "is not working": the
gap between *N* and *M* is a volume that is placed and not reaching, which looks exactly like one
that is not wired up at all.

**Explode is one-way and says so.** It replaces the node with the graph it stood for — comments
included — writes the authored file beside it as `<name>.vxcompositor.authored`, and re-reads the
document. The panel then says the file is hand-authored, hides the knobs and stops writing it,
because a form left showing knobs over a file that no longer has them is a form whose every write is
discarded.

## Examples

Reading the resolved quality table outside the panel — a build report, a test, a settings screen:

```csharp no-compile="Vixen.Editor.AssetEditors.Frame"
foreach (var knob in ResolvedQualityTable.Resolve(QualityTier.High, project, overlay)) {
    if (knob.Overridden) {
        Console.WriteLine($"{knob.Path} = {knob.Value} ({knob.Layer})");
    }
}
```

Folding a scene's volumes for one camera position:

```csharp no-compile="Vixen.Editor.AssetEditors.Frame"
var stack = new ResolvedVolumes { Look = look.Settings, Camera = view.Position };
var report = stack.Fold(world);

Console.WriteLine(report.Summary);

foreach (var parameter in report.Parameters.Where(entry => entry.IsContested)) {
    Console.WriteLine($"{parameter.Parameter}: {parameter.Value} from {parameter.Winner}");
}
```

## See also

* [The Standard Frame](../rendering/standard-frame.md) — the node the knobs are members of
* [Render quality presets](../rendering/render-quality.md) — the waterfall the table is a view of
* [Post-process volumes](../rendering/post-process-volumes.md) — the fold the stack panel reads
* [Inspectors in markup](inspectors-in-markup.md) — how the two forms are written
* [The editing pipeline](editing-pipeline.md) — what a row is, underneath

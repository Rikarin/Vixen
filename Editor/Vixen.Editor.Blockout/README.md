# Vixen.Editor.Blockout

The editor half of [docs/plan/24](../../docs/plan/24-blockout-tools.md): the viewport mode the
grey-boxing tools live in.

**Today it is the mode and nothing else, and that is the phase.** Doc 24's P0 ships "a Blockout mode
that so far only owns its keys" — because what could not be retrofitted is the arbitration, not the
tools. The mesh kernel the tools will edit is `Core/Vixen.Geometry` and is P1; the gestures are P2's;
the fifteen verbs are P3's.

```csharp
shell.Modes.Add(new SelectMode());
shell.Modes.Add(new BlockoutMode());
```

That is the whole of the registration. The mode bar, the palette entries, the radio state and the
keymap all follow from it — see the [editor modes guide](../../docs/guide/editor/modes.md).

## What it owns

| | |
|---|---|
| `1` `2` `3` `4` | object, vertex, edge and face — `BlockoutElement`, in the `blockout` context |
| `Tab` | in and out of the mesh, returning to the element mode it left |
| The mode bar's second strip | the four element modes as one segmented control |

⚠ **The keys are the point of this assembly existing yet.** [Doc 20 § B2](../../docs/plan/20-editor-parity.md)
gives `1..9` to view-bookmark recall and every modelling tool ever written gives `1`/`2`/`3` to
vertex, edge and face. Both claims are right, and the resolution is that the blockout commands
declare `Context = BlockoutMode.BlockoutContext` while the bookmarks declare none — so `KeyMap` files
the two under different contexts, `2` is vertex mode while the mode has the focus, and it is View 2
everywhere else. Neither command moved and neither gave up the key.

## What it does not reference

`Vixen.Editor.Ui`, and nothing else. In particular **not** `Vixen.Editor.SceneView`: a mode is
written against the shell's vocabulary — commands, contexts, toolbars — and the pane is joined to it
by `Vixen.Editor.App` through the pane's own `IViewportInput`. The two interfaces exist separately
because `Vixen.Editor.SceneView` does not reference the shell, deliberately, so that a viewport is
constructible in a test with no chrome around it.

⚠ **And not `Vixen.Rendering`, when the kernel arrives.** [Doc 24 § D1](../../docs/plan/24-blockout-tools.md)
puts `EditMesh` in `Core/Vixen.Geometry` under the profile that is AOT-compatible, trimmable and
API-checked, referencing `Vixen.Core.Mathematics` and nothing else — the six-line copy into
`MeshData` belongs here, beside the code that uploads it. `Vixen.Navigation` makes exactly this
choice and it has cost it nothing.

## The demotion, which is not built yet

[D6](../../docs/plan/24-blockout-tools.md) is worth reading before the shape tool is written: a
primitive keeps live parameters until a face is edited, at which point it becomes a plain mesh and
the parameters are gone. That is a one-way door and it has to be presented as one. The alternative —
a parametric history that survives editing — is a node-based modeller, which is out of scope for the
reason everything else in that column is: it is authoring for its own sake rather than something a
level designer reaches for between two playtests.

## Tests

`Vixen.Editor.Blockout.Tests` builds a real `EditorShell` headless, adds both modes, and asserts the
arbitration rather than the picture: that the element verbs are registered and disabled before the
mode is entered, that entering it moves `2` from the bookmark to vertex mode, that `Tab` comes back
into the element mode it left, and that unregistering the mode takes its commands with it.

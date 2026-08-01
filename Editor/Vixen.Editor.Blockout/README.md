# Vixen.Editor.Blockout

The editor half of [docs/plan/24](../../docs/plan/24-blockout-tools.md): the viewport mode the
grey-boxing tools live in.

**The mode, its element selection, its geometry verbs, the things that make geometry and the things
that dress it.** Doc 24's P0 shipped "a Blockout mode that so far only owns its keys", because what
could not be retrofitted was the arbitration rather than the tools; P2 gave it the element modes and
the selection gestures, P3 the verb table, P4 the shape tool and the cube grid, and P5 the surfaces.
The arithmetic under all of it is `Core/Vixen.Geometry` — this assembly is what turns a key press into
a command, a command into an operation, and an operation into one entry in the undo history.

```csharp
shell.Modes.Add(new SelectMode());
shell.Modes.Add(new BlockoutMode { Editing = editing });
```

That is the whole of the registration. The mode bar, the palette entries, the radio state and the
keymap all follow from it — see the [editor modes guide](../../docs/guide/editor/modes.md).

## What it owns

| | |
|---|---|
| `1` `2` `3` `4` | object, vertex, edge and face — `BlockoutElement`, in the `blockout` context |
| `Tab` | in and out of the mesh, returning to the element mode it left |
| `L`, `Ctrl+R` | the edge loop and the edge ring through the edge chosen last |
| `Ctrl+↑` `Ctrl+↓` | grow and shrink; `Ctrl+A`, `Alt+A`, `Ctrl+I` for all, none and invert |
| `E`, `I`, `Ctrl+B` | extrude, inset and bevel — `Alt` for the per-face versions of the first two |
| `Ctrl+Shift+R`, `Ctrl+E`, `F` | loop cut, bridge, fill hole |
| `M`, `X`, `Ctrl+X`, `P` | weld, delete, dissolve, detach |
| `Ctrl`+drag the gizmo | extrude, and then drag what it made — doc 24's second binding for it |
| `Shift+A` | arms the shape tool: drag a footprint on the work plane, then drag the height |
| `G`, `Alt+]`, `Alt+[` | a box on the cell lattice, and pushing its far side out and in by whole cells |
| `Ctrl+D`, `Ctrl+M` | duplicate; mirror a copy across the work plane |
| menu | array and radial array, poly shape, and the twelve shape kinds as a radio group |
| menu | project UVs (world / object), fit, smooth, harden, auto-smooth, new face group |
| The mode bar's second strip | the element modes as one segmented control, then four verbs |

⚠ **The keys are the point of this assembly existing yet.** [Doc 20 § B2](../../docs/plan/20-editor-parity.md)
gives `1..9` to view-bookmark recall and every modelling tool ever written gives `1`/`2`/`3` to
vertex, edge and face. Both claims are right, and the resolution is that the blockout commands
declare `Context = BlockoutMode.BlockoutContext` while the bookmarks declare none — so `KeyMap` files
the two under different contexts, `2` is vertex mode while the mode has the focus, and it is View 2
everywhere else. Neither command moved and neither gave up the key.

## Both interfaces, and why that is not a layering change

`BlockoutMode` implements `IEditorMode` **and** `IViewportInput`. A mode with only keys needed the
shell's vocabulary and nothing else; a mode that selects a face has to know which viewport the pointer
is in — which camera, which render size, which mesh — and none of that is on a `PointerEvent`. So the
assembly gained a reference to `Vixen.Editor.SceneView` and the mode answers the pane-aware overload.

⚠ **The two interfaces still live where they did and their assemblies still do not reference each
other.** `IEditorMode` is the shell's because a mode has a title, an icon and a claim on the keymap;
`IViewportInput` is the pane's because a viewport is constructible in a test with no chrome around it.
What changed is that one type implements both — and `ModeInput` prefers the pane-aware overload when a
mode has one, so a gesture is never written twice.

⚠ **The mode takes hover and the `Ctrl`+drag extrude, and leaves the rubber-band alone.** A press in
an element mode still starts the pane's band, because `SceneViewport.EndSelect` already resolves one
against elements — doc 20's E2 asks for the region resolve to be built once, and taking the press here
would be building it twice and having the two disagree about what counts as a drag.

⚠ **And still not `Vixen.Rendering`.** [Doc 24 § D1](../../docs/plan/24-blockout-tools.md) puts
`EditMesh` in `Core/Vixen.Geometry` under the profile that is AOT-compatible, trimmable and
API-checked, referencing `Vixen.Core.Mathematics` and nothing else — the copies into `MeshData` and
into the scene file live in `Vixen.Editor.SceneView`, beside the code that uploads and writes them.
`Vixen.Navigation` makes exactly this choice and it has cost it nothing.

## The demotion, which happens at the first edit

[D6](../../docs/plan/24-blockout-tools.md): a shape keeps live parameters until a face of it is
edited, at which point it becomes a plain mesh and the parameters are gone. `MeshEdit.Demote` is where
that happens, it is undoable, and it asks first.

⚠ **At the first edit rather than on entering a mode, and P4 is what made that possible.** P2 put it
on entering an element mode with an argument that was true at the time: a parametric entity had no
geometry of its own, so pressing `3` on one and seeing nothing happen read as the mode being broken. A
shape built by `MeshShapes` has a real mesh in the document from the moment it is created — so the
cage is there, every element of it selects, and the parameters survive until something actually edits
it.

⚠ **The confirmation is asked once a session and never again.** A designer who has understood what the
door is does not need to be told on the second wall, and a dialog that appears every time is one
people learn to dismiss without reading. What tells them afterwards is the badge, and the badge is
derived: `SceneDocument.IsPlainMesh` is "has a mesh and no parameters", which needs nothing saved,
migrated or kept true through an undo — and puts the same badge on a mesh that arrived from an import,
which is in exactly the same position.

⚠ **Assigning a material is the one surface verb that does not demote.** The assignment lives on the
document beside the mesh rather than inside it, so regenerating a corridor's geometry from its
parameters leaves it dressed. A UV projection writes into the mesh's own corner layer and therefore
does.

The alternative — a parametric history that survives editing — is a node-based
modeller, which is out of scope for the reason everything else in that column is: it is authoring for its own sake rather than something a
level designer reaches for between two playtests.

## Tests

`Vixen.Editor.Blockout.Tests` builds a real `EditorShell` headless, adds both modes, and asserts the
arbitration rather than the picture: that the element verbs are registered and disabled before the
mode is entered, that entering it moves `2` from the bookmark to vertex mode, that `Tab` comes back
into the element mode it left, and that unregistering the mode takes its commands with it.

Beside that, a real `SceneDocument` over a real world: every selection verb, every geometry verb,
every creation verb and every surface verb run against a cylinder or a cube, each asserting what it
made, that it is one entry in the history, and that the mesh's tables still agree afterwards. Two of
them are doc 24's exit criteria as tests — a room with a doorway, a window and a chamfered edge built
from a cube (P3), and a two-storey building with stairs between the floors that reopens byte-identical
with five of its seven entities still parametric (P4).

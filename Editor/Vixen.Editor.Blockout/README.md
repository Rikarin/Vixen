# Vixen.Editor.Blockout

The editor half of [docs/plan/24](../../docs/plan/24-blockout-tools.md): the viewport mode the
grey-boxing tools live in.

**The mode, its element selection and its geometry verbs.** Doc 24's P0 shipped "a Blockout mode that
so far only owns its keys", because what could not be retrofitted was the arbitration rather than the
tools; P2 gave it the element modes and the selection gestures, and P3 gave it the verb table. The
arithmetic under all of it is `Core/Vixen.Geometry` — this assembly is what turns a key press into a
command, a command into an operation, and an operation into one entry in the undo history.

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

## The demotion, which happens on entering an element mode

[D6](../../docs/plan/24-blockout-tools.md): a primitive keeps live parameters until it is edited, at
which point it becomes a plain mesh and the parameters are gone. `MeshEdit.Enter` is where that
happens, and it is undoable.

⚠ **On entering the mode rather than on the first edit, and D6 is still satisfied.** A designer who
presses `3` and sees nothing change — because the entity is still parametric and there is no cage to
draw — concludes the mode is broken. The door is one-way because it throws away *parameters*, and a
`PrimitiveShape` has none yet: a kind and a material, both of which survive. The confirmation D6 asks
for arrives with P4's shape tool, which is what creates the parameters it protects.

The alternative — a parametric history that survives editing — is a node-based
modeller, which is out of scope for the reason everything else in that column is: it is authoring for its own sake rather than something a
level designer reaches for between two playtests.

## Tests

`Vixen.Editor.Blockout.Tests` builds a real `EditorShell` headless, adds both modes, and asserts the
arbitration rather than the picture: that the element verbs are registered and disabled before the
mode is entered, that entering it moves `2` from the bookmark to vertex mode, that `Tab` comes back
into the element mode it left, and that unregistering the mode takes its commands with it.

Beside that, a real `SceneDocument` over a real world: every selection verb and every geometry verb
run against a cylinder or a cube, each asserting what it made, that it is one entry in the history,
and that the mesh's tables still agree afterwards. The last of them is doc 24's P3 exit criterion as a
test — a room with a doorway, a window and a chamfered edge, built from a cube, round-tripping through
the scene record unchanged.

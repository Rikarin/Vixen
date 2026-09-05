# Vixen.Editor.Texturing

Doc 48's texture graph, as a plugin — and the plugin is the point.

`Vixen.Editor.TextureGraph` is a plan, an evaluator and forty-five compute kernels. Until this
assembly existed **none of it was reachable from the editor**: nothing registered a document, nothing
registered a panel, nothing registered a command. Doc 48 § D14 says the whole document exists to
prove one claim —

> None of it is compiled into the editor. It is a plugin, loaded from a folder, through the door doc
> 36 built for a third party.

— and the way that claim is made true rather than asserted is a reference set. This project
references `Vixen.Editor.Plugin`, `Vixen.Editor.Ui`, `Vixen.Editor.Core`, `Vixen.Editor.NodeGraph`
and `Vixen.Editor.TextureGraph`, and it **does not reference `Vixen.Editor.App`**.
`ModuleReferenceTests` asserts that, and says what its own instrument cannot see.

## What is here

| | |
|---|---|
| `TexturingModule` | `IEditorPlugin`. The command, the panel, the Create ▸ entry, and the Tools entry. |
| `TextureGraphDocument` | A `.vxtexgraph`: a `NodeGraphAsset`, exactly as a `.vxshadergraph` is. |
| `TextureGraphView` | The panel: `NodeGraphView` over the graph, `ImageView` beside it. |
| `TextureNodeLibrary` | One line over the generated `NodeTypes.Register`. |
| `TexturePreview` | Why the preview pane is empty, as a value a test can assert. |

## The three things a plugin cannot do today

Doc 48 § D14 predicted two, "and finding out is the point". Both are confirmed, and there is a third
it did not name. **None of them is worked around here**, because a panel that worked by cheating
would make all three invisible.

### 1. No plugin can get a graphics device ⛔ [#737](https://github.com/Rikarin/Vixen/issues/737)

`EditorApplication.PluginPoints` publishes `EditorProject`, `SceneDocument`, `DrawerRegistry`,
`ImporterContributions`, `IEditorRegistry`, the editing state, the work plane, `IMeshBaker`,
`IMeshMapBaker`, `IMeshSource`, `IActiveScene`, `IActiveView`, `IDeviceDeploy`,
`AssetEditorRegistry`, `HotReloadHost` and the `PluginHost` itself. There is **no `IGraphicsDevice`**,
and there is no other route: `Vixen.Editor.Ui` has none, `Vixen.Editor.Core` has none, and the
contract's only channel is `PluginServices`.

So doc 48's own sentence stands as written: *either a device is published through `PluginServices`
or a third party cannot write anything that draws.* **The smallest honest fix is one line** —
`.Add(device)` in `PluginPoints`, under the interface rather than the implementation, for the reason
the `IMeshSource` line beside it already states. What it costs is a decision this slice cannot make
for the editor: a plugin holding a device can destroy resources the frame is using, and whether that
is a `IGraphicsDevice` or a narrower "make me an image" contract is a design question, not an
omission.

### 2. `TextureGraphCompiler` is `internal` ⛔ *not predicted* — [#738](https://github.com/Rikarin/Vixen/issues/738)

`TextureGraphCompiler`, `TextureNode` and all eight `[Node]` classes are `internal`, and
`Vixen.Editor.TextureGraph`'s `InternalsVisibleTo` names only `Vixen.Editor.TextureGraph.Tests`. The
generated `NodeTypes.Register` is `public` — the generator emits it that way — so the node *library*
crosses the boundary and the thing that turns a graph into a `TexturePlan` does not.

⚠ **This is the more interesting of the two, because it survives the first fix.** Publishing a device
would still leave this panel unable to compile what an author wires. Making
`TextureGraphCompiler` public is the change; this slice does not own that file.

### 3. An asset-editor registration cannot be undone ⛔ *not predicted* — [#739](https://github.com/Rikarin/Vixen/issues/739)

`AssetEditorRegistry` has `Add` and **no `Remove`**. Registering an `IAssetEditorFactory` from a
plugin is therefore a registration with no matching `OnUnload`, which is rule 2 of [the four that
make unloading work](../Vixen.Editor.Plugin/README.md#the-four-rules-that-make-unloading-work): the
factory is a reference from the editor into the plugin's assembly, and one left behind leaks the
whole assembly permanently with no error anywhere.

So a `.vxtexgraph` **cannot get a double-click** from a plugin today, and this module does not
pretend otherwise:

* the Create ▸ entry is `Opens: false`, because a kind that opens needs an editor claiming the
  extension;
* the way into the panel is a command, `texturing.open-graph`, which opens whatever `.vxtexgraph` is
  selected in the Project panel.

Adding `AssetEditorRegistry.Remove` — returning an `IDisposable` from `Add`, the way
`IEditorRegistry` already does — is the fix, and it is a change to `Vixen.Editor.AssetEditors`.

### And `AddPreview` still does not exist

Doc 36 § D4's last two rows are `AddSettingsPage` and `AddPreview`, and doc 48 predicts *"this plugin
is the consumer that makes them worth building"*. Confirmed absent: `AddPreview`, `AddSettingsPage`
and `AssetPreview` appear nowhere in the tree outside plan documents. It is not the blocker here,
though — a thumbnail registry with nothing able to render a thumbnail would be the second half of a
feature whose first half is § 1 above. [#400](https://github.com/Rikarin/Vixen/issues/400).

## What the panel does show

The canvas is real: `NodeGraphView` over the document's graph and the document's `CommandStack`, with
the whole node library in the search popup, so authoring a graph and saving it works end to end.

The preview pane is an `ImageView` — **its first production caller**; batch 1 built it for this panel
and nothing in the editor had constructed one — carrying the graph's extent and no texture handle.
That draws the chequerboard at the resolution a bake would write, with the zoom, the fit and the
pointer readout all in texels, and a line underneath naming which of § 1 and § 2 the host is stopped
by. It is not a picture and does not pretend to be one.

## What is not here

* **No bake.** Doc 48 § D4's output — a folder of PNGs and a `.vxmat` — is the material bake in
  `Vixen.Editor.Assets/Materials` and the CLI's `texture` verb, not this.
* **No layer stack.** § D10's `.vxlayers` is a second document over the same `TexturePlan`, and it is
  M7.
* **No `.vxml`.** Doc 36 § P4 makes markup the authoring path and this panel is three elements; it is
  worth porting when it grows a form, not before.
* **No base resolution in the file.** `NodeGraphModel` has nowhere to put one —
  [#719](https://github.com/Rikarin/Vixen/issues/719) — so `TextureGraphDocument.BaseWidth` is held,
  shown and not saved. A sidecar to hold it would be a second file that disagrees with the one #719
  is going to add.

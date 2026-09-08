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
| `TexturePreview` | Whether the preview pane can show anything, as a value a test can assert. |
| `TextureGraphPreview` | Evaluates a plan on the host's device and hands the pane a picture. |
| `TextureGraphEditorFactory` | Claims `.vxtexgraph`, inside the module's registration scope. |
| `MaterialBakeRoute` | The open `.vxtexgraph` **or** `.vxlayers` to a `.vxmat`, behind two verbs. |
| `Layers/SmartMaterial` | A `.vxsmartmat`: one texture set's layers, with the mesh binding taken out. |

⚠ **This README said "No bake" until 2026-09-08, and it was the reason nobody looked**
([#1009](https://github.com/Rikarin/Vixen/issues/1009)). The bake *itself* is still
`Vixen.Editor.Assets/Materials`' — what lives here is the route to it: compile, refuse before asking
for a device, fill the externals, dispatch through the evaluator both panes already share, read
**every** output rather than the first, and hand `ProjectMaterialBaker` the pictures. Of the three narrower routes that
were owed beside it, two landed on 2026-09-08 and are below; the CLI's `--graph`
([#1020](https://github.com/Rikarin/Vixen/issues/1020)) is the one that stays owed.

### A stack bakes too, one material per texture set

[#1029](https://github.com/Rikarin/Vixen/issues/1029). *Bake Material from Layers* takes the open
`.vxlayers` to a `.vxmat` per texture set. ⚠ **Half of that issue's framing turned out not to be
owed**: it reads "nothing walks the stack's outputs … teach the compiler to emit them together", and
`LayerStackGraph.Build` has emitted one `Output/Output` node per channel of the set since M7 —
`LayerStackCompilation.Outputs` already carried every one of them. What was missing was a *reader*.
`LayerStackPreview` searches that list for one usage and shows it, and nothing read the rest, which
is this repository's commonest defect wearing a compiler question.

The set question ([#927](https://github.com/Rikarin/Vixen/issues/927)) is answered here rather than
deferred: a stack's sets are the material slots of one model, so a bake that took `Sets[0]` would
leave every other slot with no material and say nothing. One set takes the stack's name; two or more
suffix each with the set's, because `Hull_Default` for the ordinary one-slot case is a name nobody
would have chosen.

### And a force control, which is one verb rather than two

[#1019](https://github.com/Rikarin/Vixen/issues/1019). § D4's digest refuses to overwrite a map
somebody painted over, `force` is how a person says they meant it, and a command handler takes no
argument — so every editor bake was called with the default. ⚠ **The obvious shape is a forced twin
of each bake verb, and it was rejected**: that is four verbs, two of which are a control that
overwrites an artist's paint whenever somebody reaches for it out of order. *Bake Material (Force)*
instead **repeats the bake that was refused** — same document, same name, same folder. It is armed
only by `MaterialBakeOutcome.Painted`, which is the one refusal force answers, and it disarms by
running. So it cannot touch anything the artist has not just been told about.

## `.vxsmartmat` — a stack without its meshes

[#575](https://github.com/Rikarin/Vixen/issues/575), doc 48 § M10. Until 2026-09-08 the extension
appeared in the plan, in `docs/overview.md` and in five `.cs` **comments**, and in no type, no
constant, no reader and no verb — which is exactly the shape that reads as a feature to anybody who
greps for the word.

⚠ **The file *is* a `.vxlayers`, byte for byte.** `LayerStackYaml` reads and writes it, so there is
no second serialiser to drift and no second set of refusals for a blend mode this build does not
know. What makes it a smart material is three invariants `Extract` establishes and `Prepare`
re-establishes: **no model, no mesh, nothing painted.**

**What travels and what does not** is decided by one question — does the thing survive a change of
model? A mask driven by a `curvature` bake does: `Source/Mesh Map` names no image at all, so the same
mask reads the next mesh's own bakes with no rewiring, which is § D10's claim and the whole reason a
generator is worth authoring. A mask driven by a hand-painted canvas does not: a `.vxpaint` is texels
in *this* model's atlas. So bakes, generators, anchors, imported textures and graph fills all come;
paint does not.

**A paint layer is dropped and named, not refused.** Both answers are defensible and one had to be
chosen out loud. Refusing the whole save was rejected because M9 makes a paint layer ordinary rather
than exotic — a rule that a stack containing one can never become a smart material would make the
feature unreachable for most real stacks, and the artist's workaround would be to delete the layer by
hand, which is the same drop with no record of what went. Every dropped thing is one sentence in
`SmartMaterialExtract.Dropped` and in the notification.

⚠ **A dropped paint *mask* becomes a constant zero and never `LayerMaskSource.None`.** `None` does
not mean "no coverage", it means *no mask* — the layer then writes everywhere. So the obvious
spelling of "remove the mask I cannot carry" turns a layer that painted a rust patch into one that
covers the whole model, and dropping coverage would have **increased** it. A mask *entry* is switched
off rather than zeroed, because an entry composites with its own operator and zero is neutral under
`Add` and total under `Copy`.

The shelf is `Assets/SmartMaterials/`, which is `TextureNodeLibrary.CompoundFolder`'s convention one
kind along: a named folder somebody can read, rather than a walk that would offer every stack
fragment anybody ever saved. *Save as Smart Material* writes onto it and *Apply Smart Material* puts
the selected one on top of the open stack's chosen set, as **one** undo entry — ids re-minted only
where they would collide, and the material's own anchors rewritten to follow them, because applying
the same smart material twice into one stack is the ordinary case.

⚠ **What is deliberately not here**: no *Create ▸* entry, because an empty `.vxsmartmat` is a file
the apply verb refuses — a smart material is *produced* from a stack, not authored blank; and
`LayerStackEditorFactory` does not claim the extension, so a shelf entry cannot yet be opened and
edited in the layers panel.

## The three things a plugin could not do. Two of them it can now

Doc 48 § D14 predicted two, "and finding out is the point". Both were confirmed, and there was a
third it did not name. Two are closed, in the editor rather than here; the third is not, and **it is
not worked around**, because a panel that worked by cheating would make it invisible.

### 1. A graphics device ✅ [#737](https://github.com/Rikarin/Vixen/issues/737)

`EditorApplication.PluginPoints` now publishes `IEditorGraphics`: the editor's device to allocate on
and dispatch over, and an upload that turns pixels into the number an `ImageView` draws. The preview
pane runs a kernel on it.

⚠ **#737's "smallest honest fix is one line" was wrong, and finding out is the useful half.**
`.Add(device)` in `PluginPoints` cannot work: that method runs from `EditorApplication`'s
constructor, the host sets `GraphicsDevice` afterwards — when the window can present — and sets it
back to `null` on the way down, and `PluginServices.Add` throws on a second publish of a type. So
there is no moment at which a device could be added. What a plugin can be handed is a **live view**,
which is the shape `IActiveScene` and `IActiveView` beside it already take.

⚠ **And a narrower "lend me the device for one call" was the intended answer and is refuted by the
evaluator.** `TexturePlanEvaluator` caches one compiled pipeline per kernel and output format across
evaluations; a borrow-per-call would recompile every kernel a plan touches on every preview. A plugin
that dispatches its own work needs a device it can *hold*. What is narrowed instead is the way back
to the screen: `Upload` takes **pixels**, not a texture view, because a plugin's image is created for
what it dispatches into — `Storage` — and a view registered from one is missing `Sampled` and is in
the wrong layout, which MoltenVK forgives and a discrete card does not.

⚠ **This module also had the claim wrong.** It read the answer once, at activation, "because a host
does not start publishing a device halfway through a session". The editor does exactly that. The
question is now asked on every show.

### 2. `TextureGraphCompiler` was `internal` ✅ *not predicted* — [#738](https://github.com/Rikarin/Vixen/issues/738)

`TextureGraphCompiler`, `TextureNode` and all eight `[Node]` classes were `internal`, and
`Vixen.Editor.TextureGraph`'s `InternalsVisibleTo` named only `Vixen.Editor.TextureGraph.Tests`. The
generated `NodeTypes.Register` is `public` — the generator emits it that way — so the node *library*
crossed the boundary and the thing that turns a graph into a `TexturePlan` did not.

⚠ **#738 closed and this section did not, which is [#816](https://github.com/Rikarin/Vixen/issues/816).**
The compiler is public, and two things in this assembly compile a canvas through it:
`TextureGraphDocument.Compile` and `LayerStackCompiler`. `ModuleReferenceTests` holds the visibility
so it cannot quietly go back.

⚠ **Closing a visibility is not the same as closing a gap, and this one stayed open for three more
batches.** The graph panel went on evaluating `TextureGraphPreview.Base` — a fixed checkerboard at the
document's own resolution — because nothing ever wired `Evaluate` to the document's plan, while the
**layer stack** pane beside it compiled and baked through the same public compiler. That was
[#792](https://github.com/Rikarin/Vixen/issues/792), and it is closed: `Evaluate` compiles the
document, resolves its external images through `TextureExternalImages` — the loop
`LayerStackPreview` had, now shared rather than copied — and says which node refused when the graph
does not compile.

### 3. An asset-editor registration could not be undone ✅ *not predicted* — [#739](https://github.com/Rikarin/Vixen/issues/739)

`AssetEditorRegistry.Add` hands back an `IDisposable` now, the way `IEditorRegistry.Add` already did.
So `TextureGraphEditorFactory` claims `.vxtexgraph` inside this module's registration scope and gives
it back — the name **and** the extension, together — when the module unloads.

Before that, registering an `IAssetEditorFactory` from a plugin was a registration with no matching
`OnUnload`, which is rule 2 of [the four that make unloading
work](../Vixen.Editor.Plugin/README.md#the-four-rules-that-make-unloading-work): the factory is a
reference from the editor into the plugin's assembly, and one left behind leaks the whole assembly
permanently with no error anywhere.

The Create ▸ entry's `Opens` is now **derived**: true exactly when the host published a registry to
claim the extension in. The command, `texturing.open-graph`, stays — it is what a host with no
asset-editor registry offers.

### And `AddPreview` still does not exist

Doc 36 § D4's last two rows are `AddSettingsPage` and `AddPreview`, and doc 48 predicts *"this plugin
is the consumer that makes them worth building"*. Confirmed absent: `AddPreview`, `AddSettingsPage`
and `AssetPreview` appear nowhere in the tree outside plan documents.
[#400](https://github.com/Rikarin/Vixen/issues/400).

## What the panel does show

The canvas is real: `NodeGraphView` over the document's graph and the document's `CommandStack`, with
the whole node library in the search popup, so authoring a graph and saving it works end to end.

The preview pane is an `ImageView` — **its first production caller**; batch 1 built it for this panel
and nothing in the editor had constructed one. In a host with a device it carries a real picture:
`TextureGraphPreview` builds a one-op `TexturePlan` at the document's resolution, `TexturePlanEvaluator`
dispatches it, and the pixels go back through `IEditorGraphics.Upload`. The extent is the document's
either way, so the zoom, the fit and the pointer readout are in the texels an author is authoring.

⚠ **What it is not is the wired graph** — see § 2 — and the line under the pane says so rather than
letting a picture imply it. In a host with no device the pane is empty and the same line says which
of the two reasons it is.

⚠ **Every route into the evaluation is outside the host's own frame**, and that is a constraint
rather than an accident: `TexturePlanEvaluator.Evaluate` drives `BeginFrame`, `EndFrame` and
`WaitIdle` on the device itself, so a call from inside `EditorHost.Present`'s pair would reset a
command pool with work still executing in it. A command handler and a panel build both run from
`EditorApplication.Update`, which is where `ThumbnailCache.Pump` runs and for the same reason.

## Painting, and the three things a surface owes

`Painting/` is doc 48 § M9. The brush, the stroke, the spacing, the jitter, the seam dilation, the
cached composite, the one-undo-entry-per-drag and the `.vxpaint` were all built before anything could
reach them; `PaintUvView` is § D13's **2D UV view**, and it is the first thing in this tree that turns
a pointer position into a texel. `TexturingModule` registers it as `texturing.paint`, and
`texturing.toggle-paint` opens it.

`PaintSession`'s remarks name what a surface has to do. What a 2D view's answers turn out to be:

1. **Pointer to texels** is `ImageView.ToImage`, which already existed — the control doc 48 § B6
   asked for carries the pan, the zoom and the inverse.
2. ⚠ **Screen radius to texels is the identity, which is not the obvious reading.**
   `PaintBrush.Radius` is authored in *texels of the atlas*, so a 2D view has nothing to convert on
   the way in — and, it turns out, nothing on the way out: `ShowCursor` draws the ring in texels and
   `ImageView`'s pan and zoom put it on the screen at the size of the stamp that would land. The hit
   triangle's texel density belongs to the 3D path, where a screen radius really is what the artist
   is holding. ⚠ A `ScreenRadius` property said this in arithmetic and nothing ever called it
   ([#928](https://github.com/Rikarin/Vixen/issues/928)); it is gone, and the claim now lives beside
   the ring.
3. ⚠ **There are no mirrors here, and that is a refusal.** Planar symmetry mirrors a point in
   *object* space and the mirrored point lands on a different triangle in a different island. Only a
   surface holding the mesh can supply one.

⚠ **The pointer handler is on the capture leg and the ordinary registration could not have worked.**
`UiElement.AddHandler` defaults to `handledEventsToo: false`, and `ImageView` marks every pointer
event handled on its way to panning — so a `Bubble` handler is registered, reads correctly, compiles,
and never once runs. Capture is also the only leg on which a paint drag can win a gesture the pan
wants: in Select mode nothing is swallowed and the pane pans as it always did.

**What the brush is aimed at is chosen in the layers panel.** A row's *Select* button writes
`LayerStackView.Selected` and mirrors it into `PaintTool.LayerId`, which the paint pane reads at every
refresh — [#910](https://github.com/Rikarin/Vixen/issues/910). ⚠ Selecting a layer that is not a
`Paint` layer is allowed and the brush then refuses it **by name**; silently painting into some other
layer is the defect the issue is about. Clicking the selected row again clears the selection, which
puts the brush back on "the first paint layer in composite order" — a state with its own meaning that
has to stay reachable.

**And which texture *set* it is aimed at is chosen there too, by the same panel's set picker** —
[#927](https://github.com/Rikarin/Vixen/issues/927). The picker writes `LayerStackDocument.PaintSet`
and `PaintSurface.Open` resolves it through `LayerStackEdit.SetFor`, the one function the panel and
the preview already resolve through, so the set on the screen is the set a stroke lands in. ⚠ Before
this every path took `Sets[0]` while every refusal named `set.Name`, so a multi-set stack painted into
the first set and said a sentence that read as though a set had been chosen. ⚠ **The choice is on the
document rather than on `PaintTool`**, which is what the code here used to predict: a set name means
something only inside one stack, and the tool outlives documents by design — two stacks made from
`LayerStackDocument.Starter` carry the same set names, so a name on the tool would be stale in the
silent direction. ⚠ **What still takes `Sets[0]` is `TexturingModule.Mesh`**, so a chosen set that
narrows to a different mesh is given the first set's coverage map; that is a finding rather than a
decision, and the file belongs to another slice.

**And what the atlas is *of* is chosen there too.** The mesh picker binds
`LayerStackAsset.Model` — [#920](https://github.com/Rikarin/Vixen/issues/920) — and that one binding
is what makes three things possible at once: `PaintUvView.ShowIslands` has a caller, a stroke is
refused outside an island instead of being allowed everywhere, and the seam dilation has a seam.
⚠ **The measured cost moved with it**: over `PaintCoverage.Everywhere` the dilation breaks out of its
round loop immediately and runs *once*, so at radius 48 and gutter 4 a stamp scanned 10 816 texels
past its footprint; over real islands all four rounds run and it scans 49 564 — 4.6× — which is what
`PaintCostTests`' bound always allowed and had never measured. `PaintIslandCostTests` derives both
from the same run rather than writing either down.

Two seams are stated rather than papered over, and the line under the pane is what says you are
looking at one of them:

* **The pane shows the layer, not the stack.** `PaintComposite`'s two halves come from an
  `IPaintStack`, and the module supplies `PaintStackImages.Empty` — so the composite of the layer
  between two transparent halves *is* the layer. Making them the plan's is
  [#849](https://github.com/Rikarin/Vixen/issues/849); ⚠ what that needs is **not** the read-back
  that issue names (`TextureBake.Read` already exists and `LayerStackPreview` already calls it) but
  a seam that evaluates an arbitrary sliced `TextureSetAsset`.

* **The pane composites with one of the bake's sixteen operators, and the other fifteen diverge.**
  `PaintComposite.Resolve` is straight-alpha source-over, which is exactly `LayerBlendMode.Copy` at
  full opacity; a compiled stack joins through `Colour/Blend`. So a painted layer whose blend mode is
  anything but `Copy` — or which carries a mask, or per-channel enables — bakes to something other
  than what was under the brush. ⚠ **This is written down and pinned rather than fixed**, and the two
  lists that say which is which are `PaintComposite.Reproduces` and `PaintComposite.Diverges`;
  `PaintCompositeTests` asserts they partition `LayerBlendMode`, so a *seventeenth* operator appended
  to the bake cannot widen the gap silently.

  ⚠ **Why implementing the fifteen is not the fix, measured rather than argued.** Over two
  `PaintStackImages.Empty` halves every separable operator degenerates to the foreground, so the
  fifteen would move **zero texels** for any stack anybody can open today — there is a test for that
  too. ⚠ **And a second one for the supply, which is the half the first cannot see**: the degeneracy
  case builds its own empty halves, so `PaintSurfaceTests`
  `.The_surface_supplies_two_blank_halves_which_is_what_makes_the_degeneracy_a_claim` reads
  `PaintSurface.Target`'s instead. That is what goes red the day the halves stop being blank, and
  without it this whole paragraph would have stayed true-sounding after it stopped being true.
  What would make them observable is real halves, and real halves force the seed and the resolved
  rectangles to come from the same join, which is `PaintComposite.ResolveAll`: **2712 ms at 4096² in
  Debug**, measured on two machines and corroborated by [#853](https://github.com/Rikarin/Vixen/issues/853)'s
  1878 ms on a third — and it would land on the pointer-down path *and* on an opacity slider's
  per-frame path. #849's own design (the painted layer as an external image the stroke re-uploads per
  dirty rectangle, so the plan does the join) is the one that closes this, and it does not begin with
  a C# operator table.

⚠ **A pointer move uploads its own rectangle** ([#912](https://github.com/Rikarin/Vixen/issues/912),
closed). `IEditorGraphics.Update` takes a rectangle and the host defers the copy to the frame that
draws next, behind a barrier out of `ShaderRead` — which orders it behind the frame that may still be
sampling the texture, so no second texture and no wait. `PaintUvView` raises `Painted` once per
*stamp* rather than once for their union, so what is uploaded is exactly what `PaintComposite.Resolve`
recomputed. ⚠ That is not uniformly fewer bytes and the union is smaller for a slow drag; what the
union cannot bound is a diagonal jump between two frames or a mirrored pair on opposite sides of the
atlas. The caller keeps a whole-picture fallback, because `Update` refuses an image made before the
atlas changed size and refuses everything in a host with no surface.

**The canvas is an object the editor holds open, not a file it re-reads.** `PaintCanvasStore` is
where the open canvases live, and the paint session, the preview and the pane all reach the same one
— which is why the store had to be *the* answer to both
[#885](https://github.com/Rikarin/Vixen/issues/885) and
[#948](https://github.com/Rikarin/Vixen/issues/948) rather than a cache owned by either. ⚠ A cache
the preview owned would have served a **stale** canvas the moment a live session was wired to it,
because a session writes texels in memory and does not touch the file until save.

It is still written at pointer-up, and since format version 2 it is Deflated per channel at `Fastest`
— a stroked 4K channel is 4.09 MB rather than 64 MiB, for the same wall clock, because the raw write
it replaces is I/O-bound ([#850](https://github.com/Rikarin/Vixen/issues/850)).

## What is not here

* **No layer stack.** § D10's `.vxlayers` is a second document over the same `TexturePlan`, and it is
  M7.
* **No 3D projection painting.** Doc 48 § D13's *first* front end — a ray to the surface, the hit's
  UV, a stamp in the atlas footprint the screen brush covers — is the half of M9 that is still owed
  ([#574](https://github.com/Rikarin/Vixen/issues/574)). ⚠ **What it needed and did not have is now
  here**: a `.vxlayers` names a model (`LayerStackAsset.Model`), `LayerStackMesh` resolves it to UV
  triangles, and the coverage map a stroke dilates across is that mesh's rather than
  `PaintCoverage.Everywhere` — [#920](https://github.com/Rikarin/Vixen/issues/920). What #574 still
  owes on its own is the raycast, the screen-radius-to-texels conversion through the hit triangle's
  density, and the mirrors, none of which an atlas can supply.
* **⚠ No refresh on an undo taken elsewhere.** Nothing here subscribes to `EditorDocument.Stack`, so
  an undo made through the editor's own verb leaves every control in the layers panel showing the
  value it had — the blend mode and the opacity as much as the mesh picker. An edit made *in* a row
  refreshes, which is why this is invisible from inside the panel.
* **No `.vxml` yet, and no longer any inline styling either.** Doc 36 § P4 makes markup the authoring
  path, and [#881](https://github.com/Rikarin/Vixen/issues/881) is the debt: `LayerStackView` builds
  its tree in C#. ⚠ **Half of that has landed.** The layout is `TexturingTheme.vcss` — the flex boxes
  that were fifty-two `SetStyle` calls are nineteen, and every one of the nineteen is either a
  runtime toggle (`display` written from `Show`) or a computed length (`depth × 12px`), neither of
  which a stylesheet can express. ⚠ **A plugin installs its own sheet**, which was worth checking
  before assuming otherwise: `EditorApplication` names the editor's five sheets one by one and
  `PluginContext` has no stylesheet seam, but `UiElement.Document` reaches the document a panel is
  in, so `LayerStackView` loads it — guarded, because a panel's factory re-runs on every workspace
  relayout and `UiDocument.Load` appends. `LayerStackThemeTests` asserts the sheet reaches a real
  panel, by geometry rather than by declaration.

  ⚠ **What is still owed is the harder half**: the tree as markup with a reactive row model. It is
  not a syntax translation — `Show` rebuilds rows only when a *shape signature* changes, precisely so
  a slider survives a refresh mid-drag, and a naive `@for` over the layer list re-runs every row body
  on every evaluation. The `@for` key rule applies: key rows on the layer's `Id`, stable across
  reorders by construction. ⚠ And the day a `.vxml` appears in this project, its `.csproj` needs
  `<VixenUi>true</VixenUi>` — the `.vcss` glob it now imports also globs `**/*.vxml`, and the
  generator is what VX4002 and VX4003 are there to say is missing.
* **No base resolution in the file.** `NodeGraphModel` has nowhere to put one —
  [#719](https://github.com/Rikarin/Vixen/issues/719) — so `TextureGraphDocument.BaseWidth` is held,
  shown and not saved. A sidecar to hold it would be a second file that disagrees with the one #719
  is going to add.

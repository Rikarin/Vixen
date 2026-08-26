# Vixen.Editor.AssetEditors

Doc 11's table of per-asset editors, built: a document and a view for each kind of thing a project
holds, and one registry that says which of them claims a file.

Spec: [docs/plan/11](../../docs/plan/11-editor.md) § "Editor-specific asset editors".

```csharp
var editors = StandardEditors.CreateDefault(_ => world, _ => new World("Prefab"));

if (editors.TryOpen(project, asset, out var document)) {
    editor.CreateView(document, panel);   // whichever factory claimed the file
}
```

| Asset | Document | View |
|---|---|---|
| Texture | `TextureImportDocument` | two tabs: settings, the mip ladder, the channel selection and the override matrix — and the sprite editor |
| Model | `ModelImportDocument` | settings, the part list, the override matrix |
| Material | `MaterialDocument` | header, parameters, a preview request, the shader-graph link |
| Scene | `SceneDocument` (`.SceneView`'s) | `SceneHierarchyView` beside the viewport and the inspector |
| Prefab | `SceneDocument` in a world of its own | `PrefabView`: the banner, and the same tree |
| Shader (`.rvn`) | `ShaderDocument` | `CodeEditorView` with Raven's diagnostics in the gutter |
| UI (`.vxml`/`.vcss`) | `MarkupDocument`, `StyleSheetDocument` | `PreviewCodeEditorView`: the editor and a preview pane |
| Addressable groups | `AddressableGroupDocument` | the group list, the policy, and the build's own analysis |
| Graphics compositor | `CompositorDocument` | a node graph, the selected node's settings, and what compiling says |
| Shader graph | `ShaderGraphDocument` | a node graph, the Raven it emits, and what both compilers said |

## The sprite editor is a tab, not a document

`SpriteSheetView` cuts a texture into sprites: a grid by cell size, a grid by cell count, or one
sprite per island of opaque texels, then the rects drawn over the picture with the nine-slice guides
inside the selected one and a name/rect/pivot/border panel beside it.

**It edits `TextureImportDocument`.** A slice is rects written into the texture's own import
settings — the same `.meta` the compression settings live in — so the panel is a second view over
that document and shares its undo stack, its dirty flag and its save. A second document over one file
would be two undo histories over one set of bytes, which is the rule stated below about opening an
asset twice.

**The cutting is not here.** `SpriteSlicer` lives in `Vixen.Editor.Assets`, beside the importer that
consumes what it produces, and is a pure function of pixels and options — which is what lets all three
modes be checked against images built in a test rather than against a screenshot. What is here is the
toolbar, the overlay and the selection.

⚠ **Slicing is a suggestion.** The sidecar records the rects, not the options that produced them. An
automatic slice depends on the pixels, so re-cutting at import time would renumber a sheet whose
artist nudged one frame between exports and repoint every reference into it.

⚠ **The overlay is positioned inline, in texels times the zoom.** No stylesheet can say "this box is
at texel 96 of that picture", and computing it in the view rather than reading it back out of the
layout is what lets the overlay be asserted without a frame having been drawn.

## Told, never discovered — and no fallback

`AssetEditorRegistry` is `ImporterRegistry`'s rule restated: registration is a line of code, two
editors claiming one extension is an error naming both, and an assembly scan would make "which
editors does this build have" a question with a different answer in a trimmed publish.

**Where it differs is the fallback, and deliberately.** An importer has one because "this format has
no importer yet" should be a shrug; a double-click that opened an unknown file in a text editor would
be the editor guessing that a `.fbx` is text. A file nothing claims stays selected in the browser,
where its import settings are still inspectable.

⚠ **An asset already open comes back, it does not open again.** Two documents over one file are two
undo histories over one set of bytes and whichever saves last wins — which is how an afternoon
disappears by double-clicking a scene twice.

## Import settings edit the node tree, not a bound object

`ImportSettingsDocument` reads the `.meta` as YAML nodes and writes the same tree back. Binding it to
an `AssetMeta` and re-emitting would be shorter and would silently throw away two things:

- the per-target `overrides` block, which `TargetOverrides` resolves at import time and which no
  settings type has a member for, and
- any key a newer editor wrote that this one does not understand — reported through `UnknownKeys` and
  left exactly as it was found.

A settings editor that deleted either would make *opening* a file an edit.

### The mirrors, and the test that keeps them honest

The settings records are `init`-only, which is right: a record that can be mutated after it has been
hashed into a cache key is a footgun in a pipeline. The cost is a second, mutable declaration per
settings type for the inspector to edit — `TextureImportEdits`, `ModelImportEdits`,
`AddressableGroupEdits` — and the risk is that the two drift.

`ImportSettingsMirrorTests` compares them by reflection, member for member and type for type, so a
setting added to an importer and not to its mirror is a red test rather than a knob nobody can turn.
`Version` is the one deliberate omission: it is the importer's own schema version, written by the
pipeline, and a field an author could type into is a way to invalidate every artefact in a project
by accident.

### The override matrix

A row is a setting, a column is a build target, and the leftmost column is the base. That
orientation answers the question people actually have — "which platforms disagree about compression"
is read across a row.

- **The cells are the inspector's own drawers.** A cell is an `InspectorField` over one target's
  settings object and whatever `DrawerRegistry` resolves, so a setting added to an importer appears
  with the right editor and a plugin's custom drawer works here without knowing this exists.
- ⚠ **An unticked cell is live and shows the base's value**, which is what the target will build
  with. A column of blanks whose meaning is "look left" would be worse; typing in one writes the
  row's own object and changes nothing until the box is ticked.
- ⚠ **Sparse by which members are marked, not by which are null.** Doc 08's block is a mapping merge,
  so "override this to null" and "do not override this" have to be different things.
- ⚠ **Rebuilt when the rows change and not when a value does.** A grid that rebuilt on every
  keystroke would take the focus out of the field being typed into.

## The three previews nothing in this assembly draws

A texture's pixels, a material's sphere and a scene's viewport all need a graphics device, and this
assembly has none. So each view owns the *request* — the channels, the mip level, the preview shape —
and raises an event; the application renders and puts a number on the `Image`. It is exactly the
split `ScenePresenter` already has with the scene panel.

⚠ **What a texture editor decodes is the source, not the artefact.** An author editing import
settings wants to see what they are about to compress. The consequence is that the preview never
shows compression artefacts; comparing those needs the artefact store and a second image.

⚠ **The mip inspector is arithmetic.** How many levels, how big each is, what the chain costs — all
of it follows from the extent, the size limit and the format, so opening a texture does not cost a
block-compression pass. `TextureLadder.Resolve` restates the importer's `Automatic` table and a test
compares the two, because a preview that guessed differently would show a cost the build does not
produce.

## A material is not a `MaterialDescriptor`

`MaterialAsset` is what a `.vxmat` holds and what git diffs; the runtime's material is a feature tree
with a compiled pipeline, and turning one into the other is the `MaterialCompiler` doc 08 names and
nothing has written. `NativeFormatImporter` carries the document forward unchanged for that reason,
so the file this editor writes is the file the pipeline reads.

**Five parameter kinds rather than one with a type field.** `- !Colour`, `- !Scalar`, `- !Texture` —
the contract name is the YAML tag, which is how the rest of the engine does polymorphism in a file.
One type carrying a `Vector4`, an `AssetId` and a discriminator would put four dead fields on every
line and give the inspector one editor to draw for five different things.

**The shading model is a name.** An `IMaterialShading` is a runtime object with a `Compile` method,
and a document that named the type could not be read without loading the renderer.

## A prefab is a scene with one root, edited in a world of its own

**Isolation is the world, and that is the whole of it.** A prefab opened into the level's world would
be a subtree in the level, dragged by the level's gizmo and saved by the level's Ctrl+S. Its own
world means the hierarchy shows the prefab and nothing else. The two world suppliers are separate
arguments precisely so that a host cannot make them one by accident.

⚠ **One root, refused at the save.** Refusing the *edit* that made a second root would mean an author
cannot create an entity before parenting it, so the banner complains and `PrefabFileWriter` throws —
which is the moment work would otherwise be lost. `SceneCompiler` refuses the same file for the same
reason.

⚠ **An instance does not adopt the template's identities.** `SceneSerializer.Instantiate` takes a map
instead, so two instances of one prefab in one scene do not both claim the file's ids — and the map
is also exactly what an override comparison needs.

`PrefabSource` is `IPrefabSource` over pairs of *objects* rather than entities: the inspector edits
whatever the shell decided an entity's row of editors is, and all it needs answered is whether this
object's member differs from the one it was made from.

⚠⚠ **Nothing feeds it, and feeding it as it stands would be wrong twice.** It has no caller outside its
own tests — the inspector's revert button is dead for every real inspection — and doc 47's row 6 is
what would wire it. It is not a wiring job:

- `IsOverridden` **compares values**, which is the implicit model doc 47 § 3 rejected. It cannot see an
  override *to zero* and cannot see an override to a value equal to the template's, and
  `SceneDocument.Prefabs` now has the right answer written down. What the inspector wants is a source
  backed by the list, not a pairing.
- `SceneEntity.Position`/`Rotation` are **world space**; `SceneEntityData`'s are **relative to the
  parent**. The two objects a pairing would join do not mean the same thing by "position", so a naive
  pairing marks every child of a moved instance as overridden and a revert writes a local value into a
  world-space setter.

### Placing one

`Prefab.TryPlace` is the verb: it turns a prefab's GUID into a file through `AssetDatabase`, refuses
what it cannot open, and goes through `SceneDocument.Place` so that one Ctrl+Z takes the instance
back. The gesture is a drop — a `.vxprefab` released over the viewport or the outliner places an
instance, where every other asset kind makes one entity holding an `AssetInstance`. That is the only
asset kind for which those two differ, and it is what gives the format's link keys anything to hold.

⚠ **A prefab that cannot be opened is a report and not an exception.** A renamed, unbuilt or
not-yet-imported asset is an ordinary state of a project, and the same refusal set a reconcile uses
(`PrefabReconcile.TryOpen`) answers here — including the one-root rule, which `Instantiate` would
otherwise throw for, out of a gesture somebody made with a mouse.

⚠ **A link the prefab file already carried is not overwritten**, and that is what makes a nested
prefab nested. A `.vxprefab` may hold an instance of another one; its nodes arrive already carrying
the inner link, and recording the outer one over the top would flatten a level of nesting on every
placement — silently, with the subtree still there and answering to the wrong template.

⚠ **`Vixen.Engine.Scenes.Prefab` and `Vixen.Editor.AssetEditors.Prefabs.Prefab` are two unrelated
things with one name.** A file that names both gets CS0104; `EditorApplication` aliases the editor's.
Doc 47 § 1 opens by saying that conflating them is the first way to get prefabs wrong here, and the
compiler says the same thing at the call site.

Links **are** written to the `.vxscene` now — `prefab`, `source`, `overrides` and `removed`, read and
written by `SceneSerializer` off `SceneDocument.Prefabs`, with a reconcile at open time.
⚠ **Migration**: the keys are additive and the scene version does not move, but `OmitDefaults` is off
for this format, so the first save of any existing scene gains all four keys on every entity.

## The compiled tab is the other half of a scene

A scene has two forms and an author could only ever see one. The `.vxscene` nests its entities and
spells its numbers out; the `SceneAsset` a build produces is flat, positional and archetype-major.
So "why does my entity arrive in the player without its `Health`" had no answer short of building,
shipping and noticing — which is the shape of defect a second tab makes visible while somebody can
still act on it.

**It compiles the open document rather than reading the artefact the last import wrote.** What it
shows is what this scene *would* compile to, so it cannot show a stale artefact and cannot be wrong
about an unsaved edit. Reading the store answers a different and also useful question, and needs an
import to have run and a staleness story of its own; this is the trade the shader graph's *show
generated code* makes, and for the same reason — during authoring the actionable question is what
the thing in front of you produces.

⚠ **The diagnostics matter more than the tables.** `SceneCompiler.Compile` reports every problem and
then fails once, so a hand-merged scene with four duplicate ids says all four rather than making an
author find them one build at a time. A pane showing only the happy result would throw that away.

⚠ **A prefab is compiled as a prefab**, so its one-root rule is checked here exactly as a build
checks it — otherwise this would be the one pane where a two-rooted prefab looked fine. And the
prefab banner stays *outside* the tabs, because a warning that vanished when the author switched
pane would be absent from the pane doing the checking.

⚠ **`tabs.document-tabs` is load-bearing.** `tabs` carries no `flex-grow` in `ControlTheme` and is
right not to — a tab set inside a form is as tall as its content — so a bare one dropped into a
document panel collapses and the tree inside it resolves to no height. The rows are still there and
still clickable in the tree's own terms, which is what made the failure read as "selection is
broken" rather than as a layout fault. It cost one test in `SelectionTests` to find.

## The code editors, and what "live" means for each

**Raven**: lex, parse and bind, and stop there. That is where the diagnostics an author can act on
come from; lowering and a backend produce artefacts, and an artefact is not what a keystroke is
asking for. Doc 07's compiler service runs the rest, out of process.

⚠ **One file, no references.** A shader that imports another names a symbol the binder cannot
resolve. Cross-file *errors* are reported; cross-file successes need the project's shader set and a
reference graph, which is the compiler service's model rather than a panel's.

**VCSS**: genuinely live. `StyleEngine.Replace` swaps a sheet's text and restyles, so the pane shows
the real cascade over a sample tree. It is the cheapest thing in doc 11's table and the one that pays
back most often.

⚠ **VXML is structure only.** A `.vxml` becomes a C# partial class, so a truly live preview means
compiling and loading the generated type — the hot-reload pipeline doc 11 wants this pane to sit on.
What is here is one step short: the element tree with its literal attributes and its text, and a
placeholder where an expression would go. Layout and styling are right in that picture; state and
bindings are not there at all, and the pane says so. Every tag becomes a plain element with that tag
name rather than a control of that type, because the binder has no tag-to-type table and cannot: a
component may use types from any assembly the generated code will reference.

⚠ **A stylesheet reports no syntax errors.** `StyleSheetLoader` follows CSS's own recovery rules and
tells a caller nothing, which is right for a browser and unhelpful in an editor. A
diagnostic-producing loader belongs in `Vixen.Ui.Styling`.

⚠ **The preview shares the editor's `UiDocument`.** A rule at author origin is scoped under the
pane's own class, which is the containment available today; a `!important` at user-agent origin still
escapes it. The fix is a second document rendered into a texture.

### Text edits are commands on the document's stack

A `TextEditCommand` holds the whole text before and after. A structural edit is the right shape when
the undo stack belongs to the *editor*; this one belongs to the document, alongside the command that
renamed the asset, so an entry has to be replayable against a buffer something else may have touched.
The cost is the file's size per entry, which is why merging is where the entry count is controlled.

⚠ **Merging is bounded by newlines, in both directions.** An edit that crossed a line boundary merges
with nothing — refusing only to merge *into* it would let the newline's entry absorb the next line's
typing, so Enter-then-type would undo back past the Enter.

## The addressable analysis is the real planner

`ContentPipeline.Analyse` is the call `Build` makes, minus the packing. A panel that reimplemented
the planner's rules would be a second set of rules, and the drift shows up as a panel calling a
project clean and the build refusing it. The view takes a delegate; the application supplies one that
runs against a `ProjectWorkspace` of its own — never the editor's `AssetDatabase`, because `Scan`
clears and repopulates its dictionaries.

⚠ **The list is the project's `.vxgroup` files, not the groups a build invented.** The `Default` group
the planner reports has no file and so is not in the list; it appears in the analysis, which is where
it exists.

## The compositor is a chain, and a container is a branch off it

Every other graph on `Vixen.Editor.NodeGraph` is data flow — a node hands the next one a value — and
a frame is not that. A frame is a *sequence*, and a render pass is a sequence nested in one. So a
compositor node has one `Flow` in and one `Flow` out, the chain of them is the order, and a node that
contains others has a second flow output that starts an inner chain.

⚠ **Order comes from the edges, not from where the nodes sit.** Laying a graph out by eye and having
that decide the frame would make dragging a node for legibility a change to the rendering.

**A declaration is a node with no flow ports.** A resource, a buffer and a stage are things a frame
*has* rather than things it does, so they sit on the canvas wherever they read best and are collected
from anywhere. The alternative — a side panel — would have been a second editor, a second undo path
and a second thing to serialise.

### Two things this needed from the framework

- **`GraphNode.Texts`.** A compositor is made of *names*: a pass names its targets, a full-screen node
  names its shader. `Values` is lanes of `float` because that is what a shader graph and a VFX graph
  are made of, and there is no float encoding of a name that is not an index into a table somebody
  has to keep. A port carries one or the other and never both.
- **Unclaimed keys reach the binding.** A setting keyed like a port and not declared as one — nothing
  connects to it — would otherwise round-trip through the file and never reach the node that reads
  it. Only the keys no port claimed, and only after the ports are bound, so a *connected* port still
  answers "no inline value" and a VFX node does not start reading a number the author typed before
  wiring something up.

`CompositorField` is declared on each node type rather than emitted by the generator, for the same
reason: teaching the generator a port kind that cannot be wired would put a socket on the canvas that
refuses every wire.

## The stylesheet is not optional

**The sheet is `AssetEditorTheme.vcss`, a file beside the loader**, embedded by the `**/*.vcss` glob
in `Vixen.Ui.targets` and read back by `AssetEditorTheme.Css`. It was 794 lines of CSS in a
`const string` until it was moved out byte for byte. `AssetEditorTheme.Utilities` is a different
thing and stays a constant: a build step generates it, so there is no file to edit.

`AssetEditorTheme` is a fifth user-agent sheet, after `ControlTheme`, `AdvancedTheme`, `EditorTheme`
and `InspectorTheme`, and a host has to load it — for `InspectorTheme`'s reason, which is worth
restating because it is the one that catches people: CSS's initial `flex-direction` is `row`, so an
element nothing styles lays its children out side by side. Without the sheet a settings panel is
every section beside the one before it.

## Known gaps

- **No LOD preview and no model viewport.** Doc 11 asks for both. Drawing a mesh needs a device, and
  that is now the whole of it: `ModelCompiler` writes a `Meshlets` sub-asset holding the cluster
  hierarchy — every level at once rather than a chain — which the part list already shows. What is
  missing is somewhere to draw a cut through it.
- **Nothing imports a `.vxcomp` or a `.vxshadergraph`.** `NativeFormatImporter` carries a document
  forward, which is right for a material and wrong for a graph — what a build needs is the compiled
  frame. A compositor wants
  an importer that runs `CompositorDocument.Compile`, the shape `SceneImporter` has, and a shader
  graph wants the same thing one step further along: the emitted Raven, compiled.
- ~~**No animation-clip, VFX, input-action or font editor.**~~ Closed by doc 20's E5, along with two
  surfaces doc 11's table has no row for. What is here now, and the one decision each is worth:
  - **VFX** (`Vfx/`) — the node library and the compiler stay in `Vixen.Editor.VfxGraph`, which knows
    nothing about a project or a panel; the document, the view and the factory are here, the same
    split the compositor makes from the other side. The preview is the *real* `VfxSystem` and a
    projection this assembly draws, because particles are drawn by a material and the editor's
    viewport is a tool renderer.
  - **Animation clip** (`Animation/`) — `.vxanim` is ten scalar curves per target rather than three
    vector tracks, because a curve editor edits one number and a vector track cannot say "X has a key
    here and Y does not". `ToClipData` bakes back to the import's shape at the union of each group's
    key times, not at a frame rate. An eleventh property, `Weight`, drives a blend shape;
    ⚠ **it is the one that needs a `Shape` beside it, and the pair — not the property — is what
    identifies a curve**, because a morphed mesh's node carries one weight curve per shape. So
    `AnimationRow` is three parts, `Curve`/`SetCurve`/`AddKey`/`Evaluate` all take the shape, and the
    dope sheet's row is `Head · Weight · jawOpen` rather than twenty rows called `Weight` that
    compare equal.
  - **Animation graph** (`Animation/`) — document, view and factory over
    `Vixen.Editor.AnimationGraph`'s model. The state map draws its arrows and puts *elements* over the
    boxes, because `DrawContext` deliberately has no text: text in this framework is an element.
  - **Sequencer** (`Sequencing/`) — `.vxseq`. Scrubbing and playing are one pure function of the time,
    so dragging left is exactly as correct as dragging right; events are the exception and take the
    previous time, because an event is a moment rather than a state. What it moves, it restores.
  - **Audio mixer** (`Audio/`) — a panel over `Vixen.Audio`'s own `MixerAsset`, validated by running
    the real `MixerBuilder` against a real `AudioMixer` rather than by a second set of rules.
    ⚠ **The panel `change:` and `refs` were built for**: a strip's fader handler reads *its own*
    mute and its mute handler reads *its own* fader, which is one member and many rows for a `ref`
    (`VXML2010`). The port is held to a whole-tree rectangle dump that is byte-identical to the C#
    it replaced — see the [panel
    ledger](../Vixen.Editor.Ui/README.md#the-panel-ledger--what-is-markup-what-is-next-and-what-never-will-be).
  - **Variation harness** (`Animation/VariationHarnessView.vxml`) — the second `.vxml` here, and the
    one that shows `refs` is not only for reaching a sibling control. ⚠ **A `harness-cell` is a
    plain element and plain elements raise no click**, so a grid of seventy cells is deliberately not
    seventy controls and a press is turned into a selection by asking each cell whether it contains
    the point. `ElementRefs` refuses to enumerate on purpose — iterate the model and look each row
    up — which is the shape the hand-written hit test already had. Byte-identical in five states.
  - **Input actions** (`Input/`) — over `Vixen.Input`'s reader and writer, so the file this editor
    writes and the file the source generator reads are the same file by construction.
  - **Font** (`Fonts/`) — `.vxfont`, a document beside the `.ttf` because a fallback chain is a
    property of *this use* of a face rather than of the file.
- ~~**No shader graph editor.**~~ `Shading/` — `.vxshadergraph`, on the same split as the VFX graph:
  the node library and the Raven emission stay in `Vixen.Editor.ShaderGraph`. Three things are worth
  pulling out of it:
  - **Compiling runs two compilers, and the panel says which one spoke.** The graph compiler's
    complaints name a node and a port; Raven's front end then reads the emitted text and names a
    line. A graph can be well-formed and emit a shader that does not type-check, which is exactly
    what a panel listing only the first kind would report as success.
  - **The generated source is a read-only `CodeEditor`, hidden until asked for.** Doc 07's "show
    generated code", with the same Raven tokenizer the `.rvn` editor uses and the same gutter. It is
    read-only because the next compile overwrites it: a pane you could type into is one that throws
    work away.
  - **A property's name is a graph text, and that was a bug fix.** `Texture/Sample 2D` and the two
    property nodes used to carry their name as a C# field on the node — which nothing writes and
    nothing saves — so every texture in every graph was `albedo` and two colour properties were one
    binding. It is `GraphNode.Texts` now, edited beside the node inspector, and the emitted
    declaration follows it.
- **The scene editor's view here is the hierarchy only.** The viewport is `Vixen.Editor.SceneView`'s
  and the inspector is `Vixen.Editor.Inspector`'s; arranging the three is the shell's job, and
  `Vixen.Editor.App` does it for the one scene it opens rather than per document.
- **Selection travels out of the hierarchy and not into it.** Something else selecting an entity does
  not move the tree's highlight. The fix is an `Effect` over the signal, which needs a reactive
  scheduler the editor's loop does not flush.
- **Labels and name lists are comma-separated text.** A list drawer is a real gap in
  `Vixen.Editor.Inspector`, and a bespoke one per editor would be the second answer to a question the
  inspector should own.

Licensed under Apache-2.0.

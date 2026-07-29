# 11 — Editor

The editor is a Vixen application. It uses `Vixen.Ui` for its interface, `Vixen.Rendering` for its
viewports, `Vixen.Ecs` for its scene representation, and `Vixen.Assets` for its content. There is no
WPF, no Avalonia, no ImGui in the shipping editor.

This is the plan's biggest bet, and it is deliberate: an engine whose editor is written in a different
UI toolkit has no incentive to make its own UI toolkit good. Stride's editor is WPF, which is exactly
why `Stride.UI` remains a thin, game-HUD-grade layer fifteen years in.

## Bootstrap problem, and the answer

The editor needs the UI framework; the UI framework is easier to build with an editor. Resolution:

1. **Phase 1–2**: an ImGui debug overlay (`Silk.NET.OpenGL.Extensions.ImGui`, behind
   `VIXEN_DEBUG_IMGUI`) provides inspection while `Vixen.Ui` is being built. It is a scaffold with a
   scheduled demolition date recorded in the roadmap, referenced by no shipping code, and it never gains
   a feature the real editor needs.
2. **Phase 4**: `Samples/02-HelloUi` proves the UI framework standalone.
3. **Phase 5**: the editor shell (docking + project browser + inspector + viewport) is built in
   `Vixen.Ui`. From this point the editor is the UI framework's primary consumer and its bug-finding
   mechanism.
4. **Phase 6+**: ImGui is deleted. Its removal is a roadmap exit criterion, not a "someday".

## Architecture

```
Vixen.Editor.App                          the host: window, shell, layout persistence, plugin loading
 ├── Vixen.Editor.Ui                      docking, command system, menus, dialogs, theming, palette
 ├── Vixen.Editor.Inspector               property drawers, attribute-driven editing
 ├── Vixen.Editor.SceneView               viewport, gizmos, picking, camera, grid, overlays
 ├── Vixen.Editor.NodeGraph               reusable graph framework
 │    ├── Vixen.Editor.ShaderGraph
 │    ├── Vixen.Editor.VfxGraph
 │    └── Vixen.Editor.AnimationGraph
 ├── Vixen.Editor.Profiler                frame graph, timeline, memory, GPU counters
 ├── Vixen.Editor.Debugger                remote inspector client
 ├── Vixen.Editor.Assets                  importers + compilers (shared with the CLI)
 ├── Vixen.Editor.Plugin                  third-party extensibility contract
 └── Vixen.Editor.Core                    project model, asset database, undo/redo, selection, settings
```

### `Vixen.Editor.Core` — the document model

Everything the editor edits goes through one mutation vocabulary, so undo/redo, dirty tracking,
multi-user awareness, and the remote inspector all work uniformly.

```csharp
public interface IEditorCommand
{
    string Name { get; }                     // shown in the undo history
    void Do(EditorContext ctx);
    void Undo(EditorContext ctx);
    bool TryMergeWith(IEditorCommand previous, out IEditorCommand merged);  // drag-scrub coalescing
}
```

- **Undo/redo is a command stack, not a snapshot diff.** Snapshots are simpler but cannot represent
  "renamed this asset, which updated 400 references" as one step, and cannot merge a 300-event drag into
  one entry. `TryMergeWith` is what makes a slider drag one undo step.
- **Per-document stacks**, plus a global stack for cross-document operations (asset rename), with a
  documented interaction: a global operation clears the affected documents' redo stacks.
- **The object model is signal-backed.** An `EditorProperty<T>` is a `Signal<T>`; the inspector binds to
  it directly; changing it from a gizmo updates the inspector with no wiring. This is where the signal
  investment pays off outside the UI framework.
- **Asset database** per [08](08-asset-pipeline-and-addressables.md): GUID index, reverse-reference
  index, import orchestration, watch-driven re-import.
- **Project settings** as YAML assets under `ProjectSettings/`, editable through the same inspector
  machinery as any other asset.

### `Vixen.Editor.Ui` — the shell

- **Docking** (`DockingHost` from `Vixen.Ui.Controls.Advanced`): split, tab, float, undock to a separate
  OS window, drag-to-dock with a preview overlay, per-layout serialisation, named layout presets
  (Default / Scene / Shading / Animation / Debug), and reset. This is the single most-used feature in the
  application and deserves a dedicated iteration.
- **Command system**: every action is a named `Command` with an id, keybinding, enablement predicate,
  and icon. Menus, toolbars, context menus, and the command palette are all *views over the command
  registry*, so a new action appears everywhere at once. Keybindings are user-remappable and stored per
  user, with conflict detection.
- **Command palette** (`Ctrl/Cmd+P`): fuzzy search over commands, assets, scene objects, and settings.
  Cheap to build on the command registry, and it is the feature power users judge tooling by.
- **Theming**: light and dark, driven entirely by design tokens in `vixen.ui.yaml`
  ([09](09-ui-framework.md)). A user-editable theme file is a natural consequence rather than a feature.
- **Notifications, progress, and long operations**: a background-task manager showing import/build
  progress with cancellation. Never a modal progress dialog.
- **Localisation** from the start: all editor strings through a `Strings.Resource` generator, so
  retrofitting is not needed. (Stride's `Stride.Core.Translation` exists precisely because this was
  retrofitted.)

> **As built** (see [`Editor/Vixen.Editor.Ui/README.md`](../../Editor/Vixen.Editor.Ui/README.md) and
> [`Editor/Vixen.Editor.App/README.md`](../../Editor/Vixen.Editor.App/README.md)). All of the above
> is in, with three corrections and two gaps.
>
> The corrections. **`Vixen.Editor.Ui` does not reference `Vixen.Editor.Core`**, contrary to what the
> tree above implies by ordering: a command is an id and a delegate and a panel is an id and a
> factory, so the shell knows nothing about projects, documents or undo stacks — which is what makes
> the whole of the editor's chrome testable against a bare `UiDocument`, and it is what doc 11's own
> "headless editor host" line asks for. `Vixen.Editor.App` joins the two. ~~**Undock to a separate OS
> window is still half-built**~~ — it is built: `IUiWindowHost` is the seam this assembly declares
> and `Vixen.Platform.Ui` fills, the arrangement records where a promoted group was, and a saved
> position that lands on no current display is dropped rather than restoring a window nobody can
> reach.
> **`Strings.Resource` is not generated yet** — `EditorStrings` is hand-written in the shape the
> generator will emit, so no call site changes when it lands, but an id used nowhere is not yet a
> build error.
>
> **The project browser is `ProjectBrowser` in `Vixen.Editor.App`**, not a shell panel, and for the
> same reason as the first correction: it needs the asset database, and the shell may not see one.
> Its shape is `AssetTree` in `Vixen.Editor.Core` — a flat index in, an immutable tree out — so
> the ordering, the folder synthesis and the search are tested without a document. `Ctrl+R` rescans.
> It does not watch the file system, and says so rather than pretending.
>
> ~~The remaining gap is a keybinding editor~~ — `KeyBindingsView` is that panel, and the model
> underneath it gained the third layer it needed for presets: shipped defaults, a preset, and the
> user's own overrides, with only the last saved. `Vixen`, `Unity` and `Unreal` ship. Plugin loading
> has since landed — see [`Vixen.Editor.Plugin`](#vixeneditorplugin) below — and so has a panel over
> it with enable, disable and reload.

### `Vixen.Editor.Inspector`

Attribute-driven, generated, not reflective:

```csharp
public sealed partial class WaterMaterial : Behavior
{
    [Inspector, Range(0, 1)]              public float Roughness = 0.2f;
    [Inspector, ColorUsage(hdr: true)]    public Color4 Tint = Color4.White;
    [Inspector, AssetPicker<Texture>]     public Texture? NormalMap;
    [Inspector, Header("Waves")]
    [Inspector, Curve]                    public AnimationCurve Amplitude = default;
    [Inspector, ShowIf(nameof(UseFoam))]  public float FoamWidth = 0.1f;
    [Inspector]                           public bool UseFoam = true;
}
```

- A source generator emits an `IPropertyDrawer` descriptor per type: member list, types, attribute
  metadata, get/set accessors as delegates over `ref` access. Zero reflection, AOT-safe, and it works
  for `struct` members without boxing.
- **Custom drawers** register per type or per attribute; the fallback is a generated composite drawer.
- **Multi-object editing** with mixed-value indication and "apply to all". Frequently omitted and
  always missed.
- **Reset-to-default**, **copy/paste property**, **right-click → revert to prefab**, **search within
  inspector** — the small affordances that make an inspector feel finished.
- Every edit produces an `IEditorCommand`, so undo works without per-drawer effort.

> **As built** (see [`Editor/Vixen.Editor.Inspector/README.md`](../../Editor/Vixen.Editor.Inspector/README.md)).
> All of the above is in, with one correction and two gaps.
>
> The correction is what the generator is *for*. `Vixen.Core.Reflection` already emits a descriptor
> per type and `Vixen.Ui.Controls.Advanced`'s `PropertyGrid` is built on it, so a second generator
> needed a reason. It has one, and it is the sentence above about `ref` access: the reflection
> layer's accessors pass values as `object`, which is why `PropertyGrid` documents that a struct
> member of a struct member cannot be written back. `InspectorMember<TOwner, TValue>` takes a
> `ref` to a *field* — `static (Foo o) => ref o.Tint` — and falls back to accessors for a
> *property*, because a property has no reference to take. Attributes that both layers care about
> (`[Range]`, `[Tooltip]`, `[Category]`) are `Vixen.Core`'s and are read by simple name, so the
> vocabulary did not fork; the editor-only ones live in the inspector assembly.
>
> One thing this section does not say and should. **Refreshing a mixed row will destroy the values
> it is showing** unless something stops it: putting a value into a control raises the control's
> changed event, a mixed field has no value to re-write, and the neutral position the control was
> parked at gets written to every selected object. `InspectorField.Refreshing()` is the guard, held
> by the view rather than by each drawer so a third-party drawer gets it too. Every inspector has
> this bug once.
>
> The gaps are a drawer for a nested described type — the field binding supports it, the row
> grouping does not — and the asset picker's browser, which is a shell panel this raises an event
> for.

### `Vixen.Editor.SceneView`

- Viewport hosting a `RenderView` into a render target displayed by a `Viewport` control. Multiple
  simultaneous viewports (four-pane layout) with independent cameras and render modes.
- **Gizmos**: translate/rotate/scale/transform with local/world/parent/screen space, snapping (grid,
  angle, vertex, surface), and pivot modes. Plus light/camera/collider/audio-source/particle-emitter
  visualisation gizmos.
- **Picking**: a dedicated `RenderStage` writing object ids to an offscreen R32_UInt target, read back
  asynchronously. Robust for any geometry, including skinned and instanced, in a way raycasting is not.
  (Stride uses a picking render stage for the same reason.)
- **Selection outline** post effect; **wireframe/unlit/albedo/normal/roughness/overdraw/lightcomplexity
  debug views** driven by swapping the `GraphicsCompositor` — a direct payoff of making the compositor
  data.
- **Camera navigation**: orbit/pan/zoom/fly/WASD, focus-on-selection, frame-all, view bookmarks,
  Blender-style numpad views.
- **Drag-and-drop** from the project browser into the scene with a placement preview and surface
  snapping.
- **Play mode, two topologies** ([17](17-app-heads-and-shipping.md)):
  - **In-process** (default): the game runs in the viewport with a world snapshot taken on entry and
    restored on exit. Requires the ECS world to be cheaply clonable, which the archetype/chunk layout
    makes true (bulk-copy chunks) — a design constraint on [04](04-ecs-and-scripting.md), not an
    afterthought. Its hazard is static/unmanaged state leaking between sessions, so a play-stop that
    leaks fails via the `DisposeBag` leak tracker ([03](03-core-foundation.md)) rather than degrading
    silently.
  - **Out-of-process**: the editor launches N standalone player processes against the same content and
    attaches the remote inspector to each. **Required by networking** — testing a server-authoritative
    game needs a server plus several clients ([16](16-networking.md)) — and doubles as the way to verify
    release-config behaviour and to isolate a game that hangs. The remote inspector already exists
    ([13](13-diagnostics.md)), so the incremental cost is process launch and a session panel.

> **As built** (see [`Editor/Vixen.Editor.SceneView/README.md`](../../Editor/Vixen.Editor.SceneView/README.md)).
> The camera, the gizmos, the picking stage, the view modes, the grid, drag-and-drop placement and
> both play topologies are in. Everything that decides *where* is here; everything that puts
> triangles on screen is not, and that division is the reason the whole assembly is tested with no
> device.
>
> Three notes on how it came out.
>
> - **A gizmo drag is recomputed from mouse-down every frame, never accumulated.** That one choice
>   is what makes snapping land *on* the grid however slowly the drag was made, makes a drag that
>   goes out and comes back end exactly where it began, and makes drift impossible rather than
>   small. The obvious implementation — add this frame's delta — has all three bugs and none of them
>   reproduce.
> - **A plane handle seen edge-on is not offered, and that had to affect the hit test and not only
>   the drawing.** Its quad projects to a sliver lying along the third arm and takes that arm's
>   clicks. A handle that is hidden and still grabbable is worse than one that is neither.
> - **The picking pass could not declare its own draw pass.** `CompositorFrame.Context` is internal
>   to `Vixen.Rendering`, rightly — a draw context is the renderer's plumbing — so `PickingRenderer`
>   owns a `RenderPassRenderer` and drives it through the `BuildChild` seam, and adds only the
>   transfer pass that copies one pixel out. That seam was widened for exactly this case and this is
>   the first thing outside `Vixen.Rendering` to use it.
>
> Two things this section asks for that are only half here. **Vertex snapping** is in the settings
> model and is not honoured: it needs the mesh under the pointer, which is the readback picking
> already does but for a position rather than an id. **Rubber-band selection** likewise — the
> picking stage answers one pixel, and a marquee is a different copy and a different resolve.
>
> One correction to the play-mode paragraph. It says a snapshot is taken and restored, and leaves
> out that **every entity gets a new handle**, so the editor's selection — which is outside the
> world — has to be translated. `WorldSnapshot.Restore` returns the table rather than being a
> `void`, because a selection that was not translated names whatever landed in those slots, which
> presents as a rendering fault.

### `Vixen.Editor.NodeGraph` — one framework, three graphs

Building three node editors is three times the work of building one well-factored one. So:

```
NodeGraphModel      nodes, ports, edges, groups, comments, sub-graphs; serialisable; undoable
NodeGraphView       NodeCanvas-based: pan/zoom, marquee, box-select, wire routing, minimap,
                    auto-layout, search-to-create, drag-from-port-to-create, copy/paste,
                    collapse/expand, error badges, live preview thumbnails
NodeTypeRegistry    generated from [Node]-attributed C# types: title, category, ports, defaults, docs
NodeGraphCompiler   abstract: graph → target artefact; per-graph implementation
```

The UX target is Unity's Shader Graph / VFX Graph, which is the current best-in-class: searchable node
creation from a dragged wire, inline previews on nodes, group boxes, sticky notes, sub-graph extraction.

| Graph | Nodes | Compiles to |
|---|---|---|
| **ShaderGraph** | math, texture sample, UV, vertex data, time, noise, procedural, custom-code, master (PBR/unlit/sprite/UI/post) | **Raven source** ([07](07-raven-shader-pipeline.md)) — inspectable via "show generated code", typechecked by Raven, diagnostics mapped back to node ports |
| **VfxGraph** | spawners, initializers, updaters, renderers, operators, events, sub-graphs | A `VfxGraphAsset` compiled to **both** a C# job body (CPU sim) and a Raven compute shader (GPU sim) — the dual-target requirement from [06](06-rendering-pipeline.md) |
| **AnimationGraph** | states, transitions, blend trees (1D/2D), layers, masks, IK, parameters, events | An `AnimationGraphAsset` interpreted by the animation runtime |

Node definitions are ordinary C# with a generator:

```csharp
[Node("Math/Lerp", Preview = true)]
public sealed partial class LerpNode : ShaderNode
{
    [Input] public DynamicVector A;
    [Input] public DynamicVector B;
    [Input] public Scalar T = 0.5f;
    [Output] public DynamicVector Result;

    protected override void Emit(RavenEmitter e) => e.Emit($"{Result} = mix({A}, {B}, {T});");
}
```

So a third-party plugin adds nodes by adding classes. `DynamicVector` (a port type resolved by
connection, as Unity's shader graph does) is the interesting type-system requirement and belongs in the
port model from the start.

> **As built** (see [`Vixen.Editor.NodeGraph`](../../Editor/Vixen.Editor.NodeGraph/README.md),
> [`.ShaderGraph`](../../Editor/Vixen.Editor.ShaderGraph/README.md) and
> [`.VfxGraph`](../../Editor/Vixen.Editor.VfxGraph/README.md)). All four boxes are in, and so are two
> of the three graphs. The example in this section compiles as written.
>
> Three notes on how it came out:
>
> - **`DynamicVector` resolution is "the widest connected input wins, and everything narrower is
>   promoted".** A node with nothing connected is a `float`, and a scalar default splats to whatever
>   the node turned out to be. A texture arriving at a dynamic port is a type error rather than
>   something to widen.
> - **A VFX graph's edges carry `Flow`, not values.** Its blocks are a chain, and the topological sort
>   the framework already does is what turns the chain into the operation list. That needed one more
>   port kind and nothing else, which is the return on having built one framework rather than three.
> - **The VFX graph's dual target cost one method call.** `VfxCompiledGraph` was made an array of
>   fixed-size operations for [06](06-rendering-pipeline.md)'s sake and `VfxShaderEmitter` was written
>   against it, so a graph that produces the array produces the Raven too. There is no second lowering
>   and no way for the two halves to have understood the graph differently.
>
> Four more from building the view and sub-graphs on top:
>
> - **A sub-graph is inlined, not called.** Every target here is a straight-line program over values —
>   Raven source with no function to call, a VFX operation array with no stack to put one on — so
>   `SubGraphs.Flatten` turns a graph containing sub-graph nodes into one containing none, and the
>   compiler that walks the result has no idea sub-graphs exist. It cost the compiler one property and
>   four lines. The interface is *declared* on the model rather than derived from the entry and exit
>   nodes, because a signature that came and went with a node would change under every containing
>   graph when somebody deleted one.
> - **The view is a projection and it is one direction.** The model is the document and the canvas's
>   own `NodeGraph` is boxes with sockets on; `NodeGraphView` rebuilds the picture from the model on
>   every structural change rather than editing it incrementally, because the canvas already culls to
>   the viewport — so the cost is bounded by the screen — and a projection that is rebuilt cannot
>   drift. A drag is the exception, and writes positions in place.
> - **Two of the canvas's behaviours had to be intercepted rather than configured, and neither needed
>   a change to `Vixen.Ui.Controls.Advanced`.** Delete is claimed by a capture-phase key handler,
>   because the canvas would otherwise remove nodes from its own copy and tell nobody. And the canvas's
>   reroute gesture — picking a wire up off a connected input, which it performs by disconnecting its
>   own graph with no event for it — is found by comparing the model's edges against the picture's
>   wires. Both are recorded in the view's README, because both are the kind of thing that reads as a
>   bug until you know why.
> - **Two things the section asks for are half here.** A preview is drawn either as a colour or as a
>   render target — the same image command and the same flip question as `Viewport` — but nothing yet
>   *renders* one: compiling a single node's sub-expression, running it over a quad and keeping the
>   target alive across edits belongs to `.ShaderGraph`, so the framework's own fixture answers with a
>   swatch. And a node the model has in two groups is drawn in one of them, because the canvas's group
>   membership is a back-pointer on the node; the model keeps both, since a document should not lose
>   an author's grouping to a drawing limitation.
>
> Not in: the animation graph, selectable wires, editing a sticky note in place, and mapping a
> *generated shader's* diagnostics back to the node that emitted the line — every diagnostic the graph
> compilers raise names a node and a port, but Raven's own complaints about the generated text are not
> yet mapped, which needs the emitters to record spans as they write. A diagnostic about a node that
> came *out of* a sub-graph names a synthetic identity the author cannot select, for the same
> want of a source map.

### `Vixen.Editor.Profiler` and `.Debugger`

Covered in [13](13-diagnostics.md). Editor-side: a frame-graph flame chart over job-system samples, a
GPU timeline from timestamp queries, a frame debugger stepping draw calls with render-target
inspection, a memory view (managed heap, native allocators, GPU heaps, asset residency), and a remote
inspector that attaches to a running build on a device to browse and mutate live entities.

> **As built** (see [`Editor/Vixen.Editor.Profiler/README.md`](../../Editor/Vixen.Editor.Profiler/README.md)
> and [`.Debugger`](../../Editor/Vixen.Editor.Debugger/README.md)). Both projects exist and both are
> above the shell rather than beside the model — a diagnostics panel shows a *reading* rather than
> something anybody edits, so neither references `Vixen.Editor.Core` and both are testable against a
> bare `UiDocument`. Four gaps, each named where it is:
>
> - **The GPU timeline needed an RHI change first**, which doc 20's E4 called the one item that could
>   not start with the panel. `Vixen.Graphics` now has query pools, `WriteTimestamp` and a
>   non-blocking resolve; Vulkan implements it, the Null backend records it, OpenGL and WebGPU report
>   the capability absent with a reason the panel shows.
> - **Render-target inspection is not built.** Stepping to draw N replays the *state*, which a
>   recorded command stream has; presenting what the frame had drawn by then needs a device that
>   executed the calls, and `Vixen.Graphics.Null` is the only recording path there is.
> - **The remote inspector's runtime half is not written** — it is doc 13's — and neither is device
>   discovery. The editor's half is complete over any `ITransport`, and the tests drive it against a
>   `FakeBuild` written only to the protocol.
> - **GPU heaps are absent from the memory view**, because reporting them needs
>   `VK_EXT_memory_budget` and the Vulkan backend does not query it. The arena is missing rather than
>   zero, which is the difference between "not measured" and "nothing allocated".

### `Vixen.Editor.Plugin`

- A plugin is a NuGet package or a folder with an assembly + a manifest, discovered at startup.
- Extension points: commands, menu items, panels, inspectors/drawers, importers, node types, gizmos,
  build steps, project templates.
- Plugin assemblies load into a `AssemblyLoadContext` for unloadability (so plugin dev iterates without
  restarting). This is the one place in the codebase where runtime reflection is not merely allowed but
  required, and it is the reason `Vixen.Editor.App` is not NativeAOT by default.
- API stability: `Vixen.Editor.Plugin` has its own `PublicAPI.Shipped.txt` and a stricter compatibility
  policy than the rest of the editor.

> **As built** (see [`Editor/Vixen.Editor.Plugin/README.md`](../../Editor/Vixen.Editor.Plugin/README.md)).
> All of the above is in, with one correction, one addition and two gaps.
>
> **The correction is what the assembly references.** This section reads as though the contract were
> the whole editor's; it is `Vixen.Editor.Ui` and nothing else under `Editor/`. Five of the eight
> extension points — commands, menu items, panels, and the layouts and keybindings that come with
> them — are the shell's vocabulary, and the shell does not reference `Vixen.Editor.Core`, so the
> contract stays chrome-level. Drawers, importers, node types and gizmos are reached through
> `PluginServices`, a lookup the host publishes into: a plugin that only adds a menu item would
> otherwise build against `Vixen.Editor.Assets`, which carries Assimp and a model importer for two
> dozen authoring formats. A plugin that does write an importer references that assembly itself.
>
> **The addition is that unloading had to become undoing.** A collectible context only collects once
> nothing outside it refers to anything inside it, and a command whose `Run` is a lambda over the
> plugin's state is exactly such a reference — held by the editor's own registry. So a plugin that
> registered five things and was unloaded without them being removed does not leak five entries, it
> leaks its whole assembly, permanently, with nothing reporting it. Every `PluginContext.Add…`
> records its own undo; `DockingWorkspace.Unregister`, `EditorShell.UnregisterPanel`,
> `MenuModel.Remove` and `DrawerRegistry.Remove` are the four methods that had to exist for it, and
> `PluginHost.WaitForCollection` is what turns the runtime's silence about a context that did not
> collect into a warning the user sees.
>
> ⚠ **Importers and build steps are listed above and are not reachable.** `ContentPipeline` builds
> its `ImporterRegistry` per run, deliberately, so the editor, the CLI and the compiler workers
> cannot disagree about the set — which means there is no registry for a plugin to add to and giving
> it one here would be the editor building a set the workers have not got. That is a change to
> `Vixen.Editor.Assets`, not to this. Project templates are `Tools/Vixen.Templates`, which does not
> exist yet either.
>
> ⚠ **A rebuilt dependency still needs a restart.** The plugin's own assembly is read into memory
> rather than mapped, so a `dotnet build` over the folder the editor is watching can rewrite it and
> `Reload Plugins` picks the new one up; the libraries beside it are mapped and stay open. Shadow-
> copying the folder is the fix and is a feature of its own.
>
> There is also no plugin-management panel — installed plugins, enable, disable, reload — which is a
> view over `PluginHost.Plugins` and nothing more.

## Editor-specific asset editors

| Asset | Editor |
|---|---|
| Scene | scene view + hierarchy + inspector |
| Prefab | isolated prefab-editing mode with override indicators |
| Material | inspector + live sphere preview + shader graph link |
| Texture | import settings + channel viewer + mip inspector + platform-override matrix, and a **sprite editor** beside them: ✅ `SpriteSheetView` — three ways to cut (grid by cell size, grid by cell count, and one sprite per island of opaque texels), the rects drawn over the picture with the nine-slice guides inside the selected one, a name/rect/pivot/border panel, and a list. It is a second **tab over the same document**, never a second document: a slice is rects written into the texture's own import settings, so it shares that undo stack — two documents over one `.meta` would be two undo histories over one set of bytes. The cutting itself is `SpriteSlicer` in `Vixen.Editor.Assets`, a pure function of pixels and options, so all three modes are checked against images built in a test. ⚠ **The rects are recorded, not the slice that produced them**: an automatic slice depends on the pixels, so re-cutting at import time would renumber a sheet whose artist nudged one frame and quietly repoint every reference into it |
| Model | import settings + mesh/skeleton/animation-clip list + LOD preview |
| Animation clip | timeline + curve editor + event track |
| Shader (`.rvn`) | `CodeEditor` with Raven syntax highlighting, diagnostics, and live recompile |
| UI (`.vxml`/`.vcss`) | `CodeEditor` + **live preview pane** with the hot-reload pipeline |
| VFX | node graph + live preview viewport |
| Addressable groups | group list, per-group policy, the analysis view from [08](08-asset-pipeline-and-addressables.md) |
| Graphics compositor | node graph over `SceneRenderer`s — the render pipeline is authored, not coded |
| Input actions | action-map editor ([see below](#input-system)) |
| Font | glyph coverage, atlas preview, fallback chain |

The `.vxml` live-preview pane is worth calling out: it is the editor feature that makes the UI framework
pleasant, and it is nearly free once hot reload works, because the preview pane is just another host for
the same component tree.

> **Nine of those thirteen are built**, in `Vixen.Editor.AssetEditors` — one assembly, because they
> are one shape: a document with an undo stack, a control over it, and a registry saying which one
> claims a file. What each of them cost is [in its
> README](../../Editor/Vixen.Editor.AssetEditors/README.md); five things are worth pulling up here.
>
> - **Import settings edit the sidecar's node tree, not a bound `AssetMeta`.** Binding and re-emitting
>   would silently delete two things a file carries and the schema does not: the per-target
>   `overrides` block, which no settings type has a member for, and any key a newer editor wrote.
>   An editor that dropped either would make *opening* a file an edit.
> - **The override matrix's cells are the inspector's own drawers.** A cell is an `InspectorField`
>   over one target's settings object, so a setting added to an importer appears here with the right
>   editor and a plugin's drawer works without knowing the matrix exists. What is sparse is the set
>   of *marked* members rather than the set of non-null ones, because "override this to null" and "do
>   not override this" have to stay different things.
> - **Three previews are a request rather than a renderer.** A texture's pixels, a material's sphere
>   and a scene's viewport all need a device this assembly does not have, so each view owns the
>   channels, the level or the shape and the application uploads. It is the split `ScenePresenter`
>   already had, and the mip inspector turned out to need no device at all — how many levels and what
>   they cost is arithmetic over the extent, the limit and the format.
> - **The compositor is a chain and a pass is a branch off it.** Every other graph on the node
>   framework is data flow; a frame is a *sequence*, so a node has one flow in and one flow out and
>   the chain is the order. It needed two things from `Vixen.Editor.NodeGraph`, both because a
>   compositor is made of **names** where the other graphs are made of numbers: `GraphNode.Texts`
>   beside `Values`, and a rule letting a key no port claimed reach the node that reads it.
> - **What "live" means differs per file type, and the pane says which it is.** A `.vcss` preview is
>   the real cascade — `StyleEngine.Replace` and a restyle. A `.vxml` preview is the *structure*: the
>   element tree with its literal attributes, and a placeholder where an expression would go, because
>   a truly live one means compiling the generated partial class. Layout and styling are right in
>   that picture; state and bindings are not there at all.
>
> Not in: the animation clip, VFX, input-action and font editors — four rows this table has and that
> assembly does not. The VFX graph's model and compiler already exist and want a document and a
> factory; the other three want their formats first. Also not in: a LOD preview, which needs the
> `ModelCompiler` [08](08-asset-pipeline-and-addressables.md) specifies, and an importer for a
> compositor graph, which is the one place a `.vxcomp` still has to be compiled by its host.

## Input system

Your brief left this open between Stride's and Unity's. **Take Unity's new Input System model, with
Stride's device abstraction underneath.**

Why: Stride's input layer (`sources/engine/Stride.Input`) is a good *device* abstraction — clean
`IInputDevice`/`IInputSource` interfaces, proper gamepad layout mapping, sensors, gesture recognisers,
virtual buttons. But its consumption API is largely direct polling (`Input.IsKeyDown(Keys.W)`), which
does not solve rebinding, multiple control schemes, local multiplayer, or device hot-swap.

Unity's new Input System solves exactly those with **actions and action maps**: an action ("Move") has a
type (button/value/pass-through), bindings (WASD composite, left stick, touch drag) grouped into control
schemes, and processors/interactions (deadzone, invert, hold, tap, multi-tap). It is the better
*consumption* model and it is what users expect in 2026.

So `Vixen.Input`:

- **Devices** (Stride-shaped): keyboard, mouse, pen/stylus, touch, gamepad with layout database, sensors,
  MIDI (P2), custom HID. `IInputSource` per platform.
- **Actions** (Unity-shaped): `.vxinput` action-asset with maps, actions, composite bindings, control
  schemes, and processors. Generated C# accessor class so `Input.Player.Move.ReadValue<Vector2>()` is
  typed and refactor-safe.
- **Event-driven and polled** APIs both available; the event path drives UI, the polled path drives
  gameplay.
- **Rebinding at runtime** with interactive "press a key" capture and conflict detection — this is what
  the action model exists for and it must be in the 1.0 API.
- **Editor**: the action-map editor, plus an input-debug panel showing live device state.

> **As built** (see [`Core/Vixen.Input/README.md`](../../Core/Vixen.Input/README.md)). Everything
> above is in except the two editor surfaces, which need an editor application shell that does not
> exist yet, and the sensor/pen/MIDI/HID devices, which need a `Vixen.Platform` contract before they
> can have an action-side one.
>
> One correction to this section and to [10](10-platforms.md)'s "`IInputSource` … feeds
> `Vixen.Input`": it cannot, directly. `Vixen.Input` is a `Core/` assembly by
> [02](02-repository-layout.md)'s layout, `Vixen.Platform` sits above it, and `CheckArchitecture`
> refuses the reference — correctly, because `Vixen.Ui` consumes the action system and must stay
> usable with no platform backend. So the device set is *fed* through a device-neutral submission
> API, and the translation from `PlatformEvent` lives in the host (`Vixen.App.PlatformInput`). The
> cost is that the key table exists twice, checked member by member by a test; the benefit is that
> the whole action system is testable with no platform, which is also what a determinism replay from
> a recorded input log needs.

## Editor testing

Testing a GUI application is where most plans go quiet. Concretely:

| Level | Mechanism |
|---|---|
| Document model | Unit tests on `IEditorCommand`: do/undo/redo/merge invariants; randomised command sequences leave the model equal to a reference; undo to empty restores the initial state exactly |
| Asset database | Per [08](08-asset-pipeline-and-addressables.md) |
| Inspector generation | Snapshot tests on generated drawer descriptors; a fixture type with every attribute combination |
| Node graphs | Graph → artefact golden tests (graph JSON → Raven source snapshot); round-trip serialisation; cycle detection; type-resolution tests for `DynamicVector` |
| Controls | Per [09](09-ui-framework.md) |
| **Editor UI automation** | A headless editor host driving synthetic input against the real element tree, asserting on the tree and on draw lists. Because `Vixen.Ui` is our own code, we can drive it directly rather than through an OS accessibility layer — a significant advantage over testing a WPF editor. Scenario tests: create project → import asset → drag into scene → edit property → undo → save → reopen → assert state. |
| Golden screenshots | Key editor layouts rendered headless on lavapipe, perceptual diff, light and dark themes. Catches layout and theming regressions. |
| Crash reporting | An out-of-process crash handler capturing a minidump plus the last N log lines and the undo history, with user consent (Stride has `Stride.Editor.CrashReport`; it earns its place) |
| Session recovery | Kill the editor mid-edit; on restart it recovers unsaved scene state from a journal. Tested by an automated kill-and-restore loop. |

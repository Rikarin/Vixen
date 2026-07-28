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

### `Vixen.Editor.Profiler` and `.Debugger`

Covered in [13](13-diagnostics.md). Editor-side: a frame-graph flame chart over job-system samples, a
GPU timeline from timestamp queries, a frame debugger stepping draw calls with render-target
inspection, a memory view (managed heap, native allocators, GPU heaps, asset residency), and a remote
inspector that attaches to a running build on a device to browse and mutate live entities.

### `Vixen.Editor.Plugin`

- A plugin is a NuGet package or a folder with an assembly + a manifest, discovered at startup.
- Extension points: commands, menu items, panels, inspectors/drawers, importers, node types, gizmos,
  build steps, project templates.
- Plugin assemblies load into a `AssemblyLoadContext` for unloadability (so plugin dev iterates without
  restarting). This is the one place in the codebase where runtime reflection is not merely allowed but
  required, and it is the reason `Vixen.Editor.App` is not NativeAOT by default.
- API stability: `Vixen.Editor.Plugin` has its own `PublicAPI.Shipped.txt` and a stricter compatibility
  policy than the rest of the editor.

## Editor-specific asset editors

| Asset | Editor |
|---|---|
| Scene | scene view + hierarchy + inspector |
| Prefab | isolated prefab-editing mode with override indicators |
| Material | inspector + live sphere preview + shader graph link |
| Texture | import settings + channel viewer + mip inspector + platform-override matrix |
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

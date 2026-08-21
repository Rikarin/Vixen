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
 │    └── Vixen.Editor.AnimationGraph    ⚠ built, and *not* on the framework — see its README
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

### Play mode runs a system graph

⚠ **Until 2026-08-21 it ran none, and the bullet above was describing a button that did nothing.**
This section is the correction, and it is here rather than in [20](20-editor-parity.md) because the
question is architectural: which loop drives the editor's frame, what a restore has to undo, and what
the editor's own per-frame work must be outside. Doc 20 enumerates the *surface* and says in its own
opening that it changes no architecture; this changes some.

#### What Play did, verified rather than inherited

`PlayModeController` had the whole state machine — play, pause, resume, step, stop, a `PendingSteps`
count, a leak comparison — and `ShouldTick`, *the method that decides whether the game loop advances
this frame*, **had no caller outside its own tests**. `EditorApplication.Update` never asked. Pressing
Play snapshotted the world, maximised the viewport, cleared the console and posted a notification;
nothing stepped. `EditorWorldRenderer` said so in as many words — "the editor runs no system graph at
all" — and `EditorApplication.ResolveTransforms` existed for exactly that reason, calling
`TransformSystem.Resolve` by hand because there was no scheduler to place it.

⚠ **`ShouldTick` was well tested, and that is the finding.** Five assertions covered pausing,
stepping, stepping-from-stopped and resuming, and every one of them passed for as long as the method
had no caller. A state machine tested in isolation proves the state machine. What was missing was a
test that asserts a *frame happened*, which is why `PlayGraphTests` is written the way it is.

#### What running one actually needs

There is no scheduler to build. `EngineLoop` (`Vixen.Engine.Frames`) is the loop a game head, a
determinism test and — the class's own remarks already said so — an editor's play mode are all
supposed to drive, and it takes a world rather than making one. It owns a `SystemRunner`, whose
`RunPhase` brackets each of the nine `SystemPhase`s with a version advance, the systems, a job
completion and a command-buffer playback. `Frame(elapsed, timeScale)` runs the nine in order with
`FixedUpdate` repeated by the accumulator.

So the four real questions are what it is given, what a restore must undo, what must not be inside
it, and what it does not contain.

**What a restore has to undo, beyond the world.** `WorldSnapshot` copies components and rewires the
hierarchy. Three things live beside the world and none of them were in it:

| Outside the world | What a restore must do | Why the obvious thing is wrong |
|---|---|---|
| **Behaviours** | Save each authored one as an alias and bytes, take it off *before* the capture, run the session over fresh copies in the loop's own store, and rebuild the authored ones on the restored handles | `BehaviorRef` is a managed component holding an array of live objects. A snapshot taken with it in place copies the *reference*, so the restore hands the scene back the very instances the session woke, started and mutated — on an `Entity` handle that no longer exists. `ISceneBehaviorBinder.Save`/`Restore` is the same gap-crossing `ProjectAssemblies` already does for a code reload |
| **The session's own lifecycle** | Destroy the session's behaviours and drain the callbacks *before* `Restore` clears the world | A teardown after `World.Clear` has no entity to walk, so nothing gets `OnDisable`/`OnDestroy` — and the leak comparison then reports every handle they were holding as a leak the controller itself caused |
| **The selection, names and ids** | Already handled — `Restore` returns the translation table and `SceneDocument.Remap` consumes it | See the correction above |

⚠ **`BehaviorStore.Destroy` does not check that the behaviour is its own, and `Remove` does.** A
teardown that walked the entity link and destroyed everything it found would queue a behaviour
belonging to the *document's* store, and the drain then indexes a bucket the session's store has
never had — a `KeyNotFoundException` out of the middle of Stop. `AllOn` reads the entity's link,
which is one component however many stores share the world, so "which store owns this" is not a
question the public API answers. The controller keeps the set it could *not* take over instead, and
everything else on the world is the session's by construction.

**What must not be inside the graph.** Three things in `EditorApplication.Update` would collide, and
one of them collides invisibly:

- ⚠ **`ResolveTransforms()`** — it *is* `TransformSystem`, and the graph runs one in `PreRender`. Two
  instances over one world keep separate "what have I already seen" versions, so each answers the
  other's writes with "nothing changed". The failure is not a doubled cost; it is a moved object that
  stops following its parent, on alternate frames, only while playing. The editor's pass is therefore
  **replaced** by the tick rather than run beside it.
- **`ExtractFrame()`** — `MeshExtractionSystem.Extract` and `LightExtractionSystem.Extract`, called
  out of band. Registering those two into the loop would run them twice a frame, and the mesh side
  writes `RenderHandle` structurally and claims geometry residency per entity. It stays where it is,
  after the tick.
- **The pane's `RenderView` write** — `SceneViewport.Update` aims the view from the `EditorCamera`,
  which is `CameraExtractionSystem`'s job in a game. You look through the editor's camera in the
  editor's viewport; "play through the game camera" is a separate decision and is not taken here.

Everything else in that method — the content and dialog pumps, the thumbnail uploads, the console
pull, the history poll, the outliner and inspector syncs, the file and stylesheet watchers, the
plugin modules' per-frame follow, the gizmo attach — is editor chrome and belongs exactly where it
is.

#### What it does not run, which is the part that must be loud

⚠ **A game's system set is imperative code in that game's own `Game.OnInitialise`, and there is no
declarative form of it.** `EngineLoop`'s constructor registers a fixed default — the behaviour
lifecycle, `Update` and `LateUpdate` passes, the four coroutine drains, and `TransformSystem` — and
every other system in the tree is added by hand against a host service:

| Registered by | Systems | Service it needs |
|---|---|---|
| `AppBuilder` | `InputUpdateSystem` | `InputService` |
| `AppGraphics` | `CameraExtractionSystem`, `WaterZoneSystem`, `WaterClockSystem`, `PostProcessVolumeSystem`, and `WorldRenderer.Register`'s four extractions | a `RenderView`, a device-backed `WorldRenderer`, stage masks that only exist after a compositor document is loaded |
| the game | `AddPhysics`'s five, `AudioSystem`, `TerrainColliderSystem`, `WaterImmersionSystem`, `BuoyancySystem`, `NavigationSystem`, the AI and virtual-camera sets | a `PhysicsScene`, an `AudioEngine`, a navmesh `Crowd`, a `DebugDraw` |

There is **no** `AddStandardSystems`. Until 2026-08-21 there was also nothing in a project that said
which of its systems a scene wants, so an in-editor session could not reproduce a game's frame by
scheduling harder; it would have had to run the game's boot path, which is what the out-of-process
topology already is. `[GameSystem]` is the declaration that closes that — see below — and it closes
the game's row of the table, not `AppBuilder`'s or `AppGraphics`'.

⚠ **The table has since been reached from both directions, and it is worth being exact about
which.** A system whose service the *editor* can own — a `PhysicsScene`, and on the same terms an
`AudioEngine` or a navmesh `Crowd` — is added by an `IPlaySystems` contribution, which is how the
shipped editor runs physics and terrain collision. A system the *project* owns is declared with
`[GameSystem]` and built by the editor out of whatever those contributions provided, which is owed
item 1 below. What is still hand-registered is `AppBuilder`'s row and `AppGraphics`', where the
editor already has a second, differently-aimed one of each and reproducing the game's would be wrong
rather than merely hard.

⚠ **Which makes the rule for this feature: run a whole graph of a named set, and name what is
missing.** A Play button that runs most of a frame and says nothing makes the missing part read as a
gameplay bug, and that is worse than a button that does nothing — the failure has moved from the
editor, where it is, into the user's game, where it is not. So entering play states the set it runs,
and lists by name both the `ISystem` types the project's own assembly declares (found by reflection
over the assembly `ProjectAssemblies` already builds and loads) and any behaviour the session could
not take over.

#### As built

Built: `PlayModeController` owns an `EngineLoop` over the world being edited, `Tick(delta)` is the
frame and the only caller `ShouldTick` needs, `EditorApplication.Update` calls it and skips its own
transform pass on a frame that ticked, the authored behaviours cross the snapshot as bytes and come
back untouched, the session's behaviours are destroyed before the restore clears the world, and
`EnterPlay` reports the gap. `PlayGraphTests` asserts a frame happened.

Built since (2026-08-21): `IPlaySystems` and `PlaySession`, so a module can add systems to a session
and have them taken away again; `EnterPlay`'s report reads the set out of `PlaySession.Running`
instead of a fixed sentence that would now be false; a contribution whose `Attach` throws is named in
`PlayModeController.Refused` rather than failing the session. `PlaySystemsTests` asserts the lifetime
— a system that runs inside a session and not before it, an owned resource disposed while the world
is still there, a second Play that attaches again.

Built since (2026-08-21, later the same day): `[GameSystem]`, so the *project* side of the same seam
is declarative too — `PlayModeController.Declared` holds what a project's own systems did, `Contribute`
resolves them last so a contribution's service is already provided, and `ProjectAssemblies.Unload`
evicts the declarations along with the four registries that already needed it. `PlayDeclaredSystemsTests`
asserts a project system that ran a frame, one whose service was absent and was named, and that the
session offers its loop and world as services.

Owed, in the order that unblocks the most:

1. ✅ **A project declares its frame** — closed 2026-08-21, and it went in as the attribute rather
   than the manifest. `[GameSystem]` on a concrete `ISystem` is collected by `GameSystemGenerator` in
   `Vixen.Engine.Generators`, which emits one `[ModuleInitializer]` per assembly calling
   `GameSystemRegistry.Declare` — the same shape `[Component]` and `[DataContract]` already use, and
   the reason a declaration is readable without running any of the project's code.

   ⚠ **A system names the service it needs with its constructor, and there is deliberately no second
   list.** `GameSystemDeclaration.Requires` *is* the parameter types, and the key is the static one —
   so `ColliderSystem(PhysicsScene, ITerrainScene)`, the hard case this section named, is not a
   special case at all. `ServiceRegistry.Add<T>`, `PlaySession.Provide<T>` and a constructor
   parameter already agree on that key without any of them being told about the others, which is why
   `PlaySession` only had to become an `IServiceProvider` for the two halves to meet.

   ⚠ **The factory is emitted, not reflected.** `ConstructorInfo.Invoke` would have made this a small
   DI container, which is the thing `ServiceRegistry`'s own remarks refuse on NativeAOT grounds. The
   generator knows every parameter's type, so it writes the `new` with the casts in it.

   ⚠ **An absent service is named, not skipped**, which is this section's rule applied one level in.
   `EngineLoop.AddDeclaredSystems` returns a `FrameActivation` — what ran, and a readable line per
   declared system that could not be built, including one whose constructor threw.
   `PlayModeController.Declared` is where the editor reads it, after every contribution has attached
   so that a system wanting physics is resolved against the `PhysicsScene` one of them provided.
   `EnterPlay`'s report subtracts what ran from the reflected list, so what is left is the set whose
   author has not opted in yet rather than the whole assembly.

   ⚠ **It is additive and nothing dedupes.** A project may go on constructing its systems by hand; a
   declared system and a hand-constructed one are the same thing to `SystemGraph`. Doing both for one
   system runs it twice. See `docs/guide/engine/declaring-a-frame.md`.

   Owed from it: the engine's own systems carry no attribute, by design — `AppBuilder`'s and
   `AppGraphics`' rows are still hand-registrations, and the editor's answer for those stays
   `IPlaySystems`. A system whose service is a *value* rather than a class — `IntruderSystem(Entity)`
   in `Samples/15` — is not declarable, because `ServiceRegistry` keys on reference types.
2. ✅ **A `PhysicsScene` in the editor** — [31 § D10](31-terrain-grass-and-trees.md)'s blocker,
   closed 2026-08-21. `Vixen.Editor.App` references `Vixen.Physics` and contributes a `PlayPhysics`
   that builds a scene over the world being edited on Play and disposes it on Stop; `AddPhysics` puts
   the four fixed-step passes and the interpolation into the session's loop.
   `Editor/Vixen.Editor.Terrain.Physics` is now a *module* as well as an adapter, publishing the
   `ITerrainColliders` the sculpt tools resolve and running `TerrainColliderSystem` over that scene.

   ⚠ **It went in as a general mechanism rather than as four lines in `EnterPlay`, and the reason is
   the layering rather than taste.** `Vixen.Editor.App` may not reference `Editor/Vixen.Editor.Terrain`,
   so it cannot publish an `ITerrainColliders`; the terrain toolset may not link physics. The join has
   to be a module, and a module can only reach a session's frame if there is a seam for it — which is
   `IPlaySystems`, an `IEditorRegistry` contribution read at every `Play`, and `PlaySession`, which
   owns the lifetime, carries the teardown, passes one contribution's service to the next and collects
   the names the entry report reads out. See `docs/guide/editor/play-mode-systems.md`.

   ⚠ **Physics belongs to play, not to editing**, and the session lifetime is what makes that true:
   the snapshot is captured before any contribution attaches, so the entities a collider system creates
   are inside what the restore clears rather than something that lands in a person's scene file. Pause
   and Step need nothing from a contribution — `Tick` is what decides whether `Frame` is called at all.
3. **Play through the game camera**, and with it the question of whether a session drives the
   viewport's `RenderView` or its own.
4. **Additive scenes.** The controller is given one `BehaviorStore` — the first document's — and a
   behaviour authored into a second, additively opened scene is named in `Unsupported` rather than
   run. Correct and visible, and not yet whole.

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
| **AnimationGraph** ⚠ | states, transitions, blend trees (1D/2D), layers, masks, parameters | An `AnimationGraphAsset` compiled to the `AnimationStateMachine` and `AnimationLayer`s the runtime runs. ⚠ Built, but **not on this framework** — see the note below |

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
> ⚠ **The third graph is built and is not on this framework, which is a correction to the tree
> above rather than a gap.** Doc 20's [E5](20-editor-parity.md#e5--authoring-surfaces-25-em) tried it
> here first. A shader graph's edge carries a *value* and a VFX graph's carries *order*; a state
> machine's carries *"may become"* — there is nothing on it, several leave one state and several
> arrive at another, and a graph with no cycle is a character that can never return to idle. Every
> rule this framework exists for would have to be switched off to hold one, so
> [`Vixen.Editor.AnimationGraph`](../../Editor/Vixen.Editor.AnimationGraph/README.md) is its own
> model with its own compiler. What it shares is the *shape of the editor* — a canvas, a panel of the
> selected thing's settings, a diagnostics list, a compile button — which is where sharing belongs.
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
> Five more from building the view and sub-graphs on top:
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
> - **An unconnected input is typed into on the node, and the question that got wrong was "how many
>   boxes".** The port model answers *how wide a value is in the emitted source*, which is zero lanes
>   for a boolean, an integer and an unresolved dynamic — so an editor that asked it gave every maths
>   node in the shader graph, whose inputs are all `DynamicVector`, no way to type a number into it at
>   all. `PortKinds.Fields` is the second question and the answer is one for all three; a dynamic port
>   takes one number however wide it later turns out to be, because the compiler pads a short constant
>   with its last lane. The boxes go *beside* the port's name rather than under it, because a wire's
>   endpoint is arithmetic over the port pitch and a row that grew for its value is a row every wire
>   on the node would miss. Two gestures had to be taken back off the canvas for it: a press inside a
>   value box (which would otherwise start a wire from the port the box sits in) and every keyboard
>   shortcut while the focus is in a field (or Backspace deletes the node instead of a digit).
> - **Two things the section asks for are half here.** A preview is drawn either as a colour or as a
>   render target — the same image command and the same flip question as `Viewport` — but nothing yet
>   *renders* one: compiling a single node's sub-expression, running it over a quad and keeping the
>   target alive across edits belongs to `.ShaderGraph`, so the framework's own fixture answers with a
>   swatch. And a node the model has in two groups is drawn in one of them, because the canvas's group
>   membership is a back-pointer on the node; the model keeps both, since a document should not lose
>   an author's grouping to a drawing limitation.
>
> And one from giving each graph a way in. **Each graph's document, panel and factory are
> `Vixen.Editor.AssetEditors`', not its own assembly's** — `.vxvfx`, `.vxcomp` and now
> `.vxshadergraph` — because a compiler that knows nothing about a project is a compiler a test runs
> with no editor in the way, and the document-with-an-undo-stack-and-a-view is one shape this table's
> other rows already have. The shader graph's panel is where "show generated code" lives: a read-only
> `CodeEditor` with Raven's own highlighting, and the emitted text put back through Raven's front end
> so that a graph which is well-formed and emits a shader that does not type-check is reported rather
> than called a success.
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
> **All thirteen are built now** — doc 20's [E5](20-editor-parity.md#e5--authoring-surfaces-25-em)
> closed the four this paragraph used to name, and three of the four turned out to be about the
> *format* rather than about the panel, exactly as it said. The VFX graph wanted a document, a
> factory and a preview; the animation clip and the font wanted formats that did not exist; the input
> asset's format was already `Vixen.Input`'s and shared with the source generator, so the editor
> writes the file the compiler reads by construction. Two authoring surfaces this table has no row
> for arrived beside them — a sequencer over a new `.vxseq`, and a mixer over the `MixerAsset`
> `Vixen.Audio` already had.
>
> Three of them are worth pulling up for the same reason the five above are.
>
> - **An animation clip is ten scalar curves per target, not three vector tracks.**
>   `AnimationChannel` — what an import writes — holds arrays of `Vector3` and `Quaternion`, which is
>   right for a file a DCC produced and wrong for an editor: a curve editor edits *one* number against
>   time and a dope sheet's row is one number's keys, and a vector track cannot express "X has a key
>   here and Y does not", which is most of what hand animation is. `ToClipData` bakes back to the
>   import's shape by sampling the union of each group's key times — not at a frame rate, which would
>   turn a two-key slide into sixty keys and still miss the moment between two frames.
> - **The VFX preview is a real simulation and an honest projection.** What steps is `VfxSystem` over
>   the `VfxCompiledGraph` the document just compiled — the class a game runs — so an author is
>   watching their graph rather than a mock of it. What *draws* is the panel, projecting the particle
>   buffer, because particles are drawn by a material and the editor's viewport is a tool renderer:
>   the half that would be dishonest to fake is the simulation, and it is not faked.
> - **A font asset is a document beside the `.ttf` rather than settings on it, and the fallback chain
>   is why.** A chain is a property of *this use* of a face — the same `NotoSans.ttf` is one font's
>   primary and another's CJK fallback — which import settings on the file could only express once.
>
> Also not in: a LOD preview, which needs the `ModelCompiler`
> [08](08-asset-pipeline-and-addressables.md) specifies, and an importer for a compositor graph,
> which is the one place a `.vxcomp` still has to be compiled by its host.

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

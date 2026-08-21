# Vixen.Editor.Water

Doc 35's authoring half: one viewport mode, the gesture that lays a body's curve on the ground, the
handles a river's profile is dragged by, and the zone panel that turns a resolution into metres.

Specified in [`docs/plan/35-water.md`](../../docs/plan/35-water.md) § Part 2 and § W9. The kernel it
edits is [`Core/Vixen.Water`](../../Core/Vixen.Water) and is deliberately not here.

## One mode, and most of the time not even that

Doc 31 needed a sculpt mode and a foliage mode because each owns the viewport and has an incompatible
idea of what a click means. Water needs **one**, and the reason it needs even that is short: placing a
lake is placing an *entity*, editing its shape is editing a *spline*, and the editor already does
both.

So `WaterMode` exists for the three things that are neither:

| Verb | What it is |
|---|---|
| **Draw** | Click points on the ground to lay a curve at the ground's own height, closed for a lake or an ocean and open for a river |
| **Profile** | Drag the width handles either side of the curve and the depth handle down — Unreal's three viewport visualisations, and the reason its river authoring is good |
| **Preview** | Toggle the reserved layer's contribution, so an author sees what the water did to the ground |

⚠ **Clicking the first point again is how a closed body is finished**, because the UI layer has no
double click: `PointerAction` is moves, presses and releases, and a click count is a fact about time
the event does not carry. Enter finishes as well, and is the only way to finish a river — an open
curve has no first point to come back to.

## The gesture ends at a curve, and the module is what makes it a body

`WaterEdit` and `WaterMode` have no device, no world and no document, so every gesture is testable
with synthetic input and nothing running — which is what [§ Part 4] asks for: "asserting the spline
and the profile rather than the pixels". `WaterMode.Drawn` hands a `Spline` over, and `WaterModule`
is what writes it beside the scene and creates the entity that names it.

⚠ **The curve is written as a `.vxspline` rather than held in memory.** A body names its curve by
*name* — [§ D6] — because a handle names a slot in a world that issued it and a scene file is read by
a world that has not run yet. A curve that existed only in the editor's memory is a lake that
disappears when the project is reopened, which is the worst kind of authoring bug: it looks like it
worked.

⚠ **And a scene that has never been saved refuses the draw rather than half-doing it.** There is
nowhere to put the sidecar, so no entity is made and the panel says why. An entity naming a spline
nothing can supply loads, resolves to nothing, counts into `WaterZoneSystem.UnresolvedBodies` and
draws no water — a diagnostic an author would have to go looking for.

## The handles were arithmetic with no caller, and the preview was a flag nobody read

`WaterEdit.Grab`, `Drag` and `HandlesOf` had tests and no user; `WaterMode.Pointer` answered `false`
for every tool but Draw; and `CarvePreview` was set by a command, ticked by a menu and read by nothing.
Two of doc 35's three verbs were names in a tool strip.

**Profile** is now the whole route. `WaterProfileHandles` draws the three crosses per control point
into the pane's overlay channel — `SceneViewport.Cursor`, which is the door `TerrainMode` hangs its
brush ring on — and hit-tests them in *render pixels*, because the same half-width is forty pixels
across on a canal and one on an ocean. A press within `WaterMode.HandlePixels` grabs; moves slide the
handle along its own axis and write the component as they go, so the surface follows the pointer; the
release pushes one `WaterProfileCommand`.

⚠ **Each tool is asked what it wants, and one of the three wants nothing.** Draw takes a press on the
ground. Profile takes a press *on a handle* and the moves and release that follow it — a press
anywhere else is a selection and is left alone, because a Profile tool that swallowed it would be one
you have to leave in order to choose what it works on. Preview takes nothing at all: it is a state,
and looking at what the water did to the ground means being able to fly around while the ground is
there.

⚠ **The mode needs an `IWaterScene` to find a single handle**, and the module hands it its own — the
same object the viewport draws the water from, so the crosses are on the surface the author is looking
at rather than on a second reading of the same file.

## Preview needs something to preview, so the carve had to be reachable first

`WaterCarve` was written and tested in the kernel; `WaterCarveCommand` wrapped it for undo; nothing
constructed either. So "toggle the reserved layer's contribution" was a toggle over an empty layer.

`WaterModuleCarve` is the other half: `water.carve` regenerates the reserved **Water** layer from every
body in the scene, over every terrain in it, as one undo entry — and `CarvePreview` now raises a change
that sets `TerrainEditLayer.IsVisible` and re-resolves.

⚠ **The terrain arrives through `ITerrainScene` and not through `Vixen.Editor.Terrain`.** The two
toolsets are independent plugins and either may be absent — the same reason `GroundAt` is a flat plane
— so what is referenced is the *contract* in `Core/Vixen.Rendering.Terrain`. A project with no terrain
plugin loaded gets an empty list, a greyed-out verb and a panel row saying so.

⚠ **`Resolve` as well as `Regenerate`.** The kernel invalidates the tiles it touched and stops there,
because dirtiness is a flag rather than a recompute — so a carve without a resolve is deltas in a layer
and a viewport still drawing the old ground.

⚠ **Leaving the mode puts the preview back on.** `IsVisible` is saved with the terrain, so a session
that ended with the carve hidden would leave a project whose riverbeds are on disk and invisible.

⚠ **Every body carves at the same strength**, the draw settings' `Carve`. `WaterBodyComponent` has no
carve fields on it and adding them is a component *layout* change — a scene-compatibility decision
rather than a wiring one, and so not this one.

## There is no CreateWaterBodyCommand

`SceneDocument.Create(name, local, parent, initialise)` already makes an entity undoably, with the
transform, the parenting and the outliner's name handled. A command of water's own would be a second
answer to "how does a tool add an entity", and the two would drift the first time the document's
version learnt something this one had not.

What `WaterCommands.cs` holds is only what is genuinely new: `WaterProfileCommand`, which is a whole
handle drag as one entry, and `WaterCarveCommand`, which regenerates the terrain's reserved layer.

⚠ **The carve's undo regenerates from the old body list rather than restoring a stored layer.** A
layer's deltas are sparse chunks over a whole terrain, and copying them per entry would put a
heightfield's worth of memory on the undo stack for every drag of a width handle. Regeneration is
deterministic — that is what "regenerated wholesale" means — so the cheap thing and the correct thing
are the same thing.

## The zone panel is the derived numbers

`WaterZoneSettings.Facts()` is the whole point of the panel: metres per texel, megabytes, vertices for
a full window, the height quantum, and the sea state's maximum amplitude. § D3 puts it plainly — "a
number an author types into `render_target_resolution` with no idea what it buys is how the reference
gets configured wrongly".

⚠ **The arithmetic is the kernel's, not the panel's.** `WaterZone.MetresPerTexel`, `Bytes` and
`HeightQuantum` are the same properties the renderer sizes its texture from, and `Validate` is the
same rule it refuses by — so the panel cannot be right about a configuration the renderer rejects.

⚠ **The maximum amplitude is beside them because it decides three other things**: the node error
metric, the far-mesh cut and the collision bounds. An author raising the wind from a breeze to a gale
should see what it costs before the frame time does.

## And the viewport has to draw it, which for a long time it did not

`ScenePresenter` contained no occurrence of the word "water". The gesture wrote a real `.vxspline`,
created a real `WaterBodyComponent`, and an author saw the same dry ground they had before — doc 31's
"built and not yet reachable" arriving through a door W9's exit criterion could not check, since a
session test can find the tools bound and say nothing about what is on screen.

`WaterModuleScene` is this module's half of the fix: an `IWaterScene` contributed through the same
registry `TerrainModule` contributes an `ITerrainScene` through, answering the three questions a fold
cannot answer for itself — what a spline name means, what a sea-state name means, and where the
ground is.

⚠ **It reads the files off disk rather than through the asset database.** The draw tool writes the
curve beside the scene with `File.WriteAllText` and an import is a scan away, so a source that asked
the database would show the lake on whichever frame the watcher caught up — which reads as the draw
tool being unreliable. Cached by name and re-read when the timestamp moves.

⚠ **`GroundAt` is a flat plane at zero, and that is a limit rather than a placeholder.** The runtime's
ground is the terrain's, and this module may not reference the terrain one — the two are independent
plugins and either may be absent. What it costs is a shoreline drawn where the body's own falloff puts
it rather than where the hill is; what it buys is a water toolset that works in a project with no
terrain in it.

## The six `water.show*` verbs are editor commands too

`WaterDebug` declares them as `[ConsoleCommand]`s, and the only thing that finds an attributed command
is `ConsoleCommands.RegisterFrom(Assembly)` — which nothing in the tree calls, because nothing in the
tree constructs a `ConsoleCommands` outside its own tests. `WaterModuleDebug` registers the same six in
the shell under **the same names**: the command palette is the editor's console, and a different id
there would mean the sentence in the guide matched neither.

⚠ **`water.showFlow` is the one of the six a pane draws today.** Tiles and LOD bands describe patches a
device selected and ripples a simulation only a game runs; a preview surface is a CPU grid with none of
the three. They are registered anyway, so the set an author sees does not depend on which host they are
in.

[§ D6]: ../../docs/plan/35-water.md#d6-a-water-body-is-a-spline-and-a-profile-and-there-is-no-new-spline
[§ Part 4]: ../../docs/plan/35-water.md#part-4--testing

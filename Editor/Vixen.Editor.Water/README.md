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

[§ D6]: ../../docs/plan/35-water.md#d6-a-water-body-is-a-spline-and-a-profile-and-there-is-no-new-spline
[§ Part 4]: ../../docs/plan/35-water.md#part-4--testing

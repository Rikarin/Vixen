<!--
SPDX-FileCopyrightText: Copyright (c) Rikarin
SPDX-License-Identifier: Apache-2.0
-->

# 32 — Post-process volumes

⚠️ **Extends [30](30-post-processing-parity.md).** Doc 30 is the audit: seventeen screen-space
effects, each a node a `.vxcompositor` can name. Every row of it is now built. What none of them
answers is **where** a look applies — the whole frame is graded one way, decided once, in a document
that cannot know the player has walked into a cellar.

**The claim this document has to earn.** A level designer makes a room look different by placing a
box in it, and writes no code and no second compositor. Walking in and out of that box crossfades,
and two overlapping boxes resolve by a rule somebody can predict. Nothing about the frame's *shape* —
which passes exist, what they read, in what order — changes, because that is the document's business
and a graph rebuilt per frame is not a design.

---

## The argument

Unreal's `APostProcessVolume` is the reference implementation, and stripped of its naming it is four
mechanics rather than the two hundred properties it looks like:

| Mechanic | What it is |
|---|---|
| **Optional fields** | `FPostProcessSettings` pairs every property with a `bOverride_` flag. A volume contributes only what it opted into; everything else falls through |
| **Priority** | Volumes sort, and the highest-priority one that has an opinion wins |
| **Blend radius** | A distance *outside* the bounds over which the volume fades in, so a doorway is a crossfade rather than a cut |
| **Unbound** | A volume that skips the containment test and is the base everything else blends over |

The two hundred properties are bulk. The four mechanics are the design, and the first is the one
everything else rests on: **without per-field optionality, a volume is a replacement rather than an
override**, and two volumes that each care about one thing cannot both apply.

### What is worth taking, and what is not

| Unreal does | We should | Why |
|---|---|---|
| Per-property override flags | ✅ `float?` and friends | C#'s nullable *is* `bOverride_`, and the DataContract generator already handles it |
| Priority + blend radius + unbound | ✅ exactly | The mechanics are right and the vocabulary is one a designer already knows |
| Settings on the camera as a base layer | ✅ | It is where "this character's look" belongs, and it is one more layer in the same fold |
| Blendables — post-process *materials* per volume, at four fixed insertion points | ❌ | A `.vxcompositor` already runs an arbitrary shader wherever the node is written. Four hard-coded insertion points is the general case made specific |
| Camera settings — f-stop, shutter, ISO, focal distance — inside the volume | ❌ **and this is the interesting one** | See below |

### ⚠ The lens does not belong to a volume

`FPostProcessSettings` carries `DepthOfFieldFstop`, `DepthOfFieldFocalDistance`,
`CameraShutterSpeed` and `CameraISO`. So in Unreal a *volume* can set your aperture — and a cine
camera's depth of field being silently overridden by a level's post volume is a well-known and
much-complained-about gotcha.

That is precisely the fault [30](30-post-processing-parity.md) removed when `PhysicalCamera` was
folded into `Camera`: one aperture, one answer, in one place. Re-introducing a second claimant a
document away would undo it.

**So the boundary is: a volume describes a _place_, and a lens belongs to the _camera_.** Volumes get
the grade, the bloom, the fog, the vignette, the local exposure and an exposure *compensation*. They
do not get the f-stop, the shutter, the ISO, the focal length or the sensor.

⚠ **Exposure compensation rather than exposure.** A volume saying "this cellar is two stops darker
than the meter thinks" is a statement about the place and composes correctly with whatever the camera
and the auto-exposure decided. A volume saying "this cellar is EV 9" is a second claimant on the
number the camera already owns, and two overlapping volumes would fight over an absolute rather than
adding two relative offsets.

---

## The constraint that shapes everything

**A volume blends node _parameters_. It cannot change which nodes exist.**

The compositor graph decides resource lifetimes, pass ordering, target formats and transient
aliasing, and it is built from a document. Rebuilding it as the player crosses a threshold would be a
per-frame graph recompile, and the transient aliaser would have to re-plan every frame.

The good news, which makes the whole feature cheap: **`compositor.Build` already runs every frame**
(`SceneRenderHost.Draw`). A node's properties are read at build time, so setting
`TonemapRenderer.Exposure` between frames takes effect on the next one with nothing rebuilt. There is
precedent — `TonemapRenderer.View`, `MotionBlurRenderer.View` and `AutoExposureRenderer.DeltaTime`
are already live per-frame reads.

⚠ **What this costs is one honest sentence in the guide.** Someone will place a volume expecting
"this room adds depth of field", and what it does is *set the focus distance on the depth-of-field
node the document already declared*. A document with no `!DepthOfField` in it gets nothing, and the
volume cannot say so at author time.

Unreal has exactly the same constraint — its uber-pass always contains the bloom code and intensity
zero is the off switch. The difference is that Unreal hides it inside a monolithic pass and we have
to say it out loud, because our passes are named in a file you can read. That is the price of the
compositor being a document, and it is worth paying.

---

## The shape

### Settings

A `[DataContract]` struct of nullable fields, in `Vixen.Rendering` so both the ECS side and the
effect set can see it.

```csharp
[DataContract]
public struct PostProcessSettings {
    public float? ExposureCompensation;
    public float? BloomIntensity;
    public float? BloomThreshold;
    // …
    public static PostProcessSettings Blend(in PostProcessSettings under, in PostProcessSettings over, float weight);
}
```

`Blend` is the whole of the semantics: for each field, an unset value in `over` leaves `under`'s
alone, and a set one lerps toward it by `weight`. Nothing else in the feature has an opinion about
how two looks combine.

⚠ **This signature is wrong and the built version has two types.** Left as written because the reason
is the useful part — see [the corrections](#two-corrections-this-document-earned-by-being-implemented)
at the end.

⚠ **Every field is a scalar or a colour, and none is a resource name.** A volume that could redirect
`source:` would be changing the graph, which is the constraint above. A volume that could name a
different LUT texture is a real want and is deferred: it needs the graph to declare both tables up
front, which is a second mechanism.

### Inline on the component, not an asset reference

**I argued for an asset reference first and changed my mind; the reason is worth recording.** A
`.vxpostfx` asset keeps the ECS column small and lets two volumes share a look. But it needs an
importer, an asset editor and a round trip, and the editor half is where the cost of this feature
actually is — so the asset version buys a theoretical saving and doubles the work.

The column is ~200 bytes for the surface below, and a level has tens of volumes rather than
thousands. Inline also matches the workflow Unreal actually gets used with, and the reflected
inspector renders a nested `[DataContract]` struct already.

A shared-preset asset is a later addition that costs nothing to add on top, because the settings type
is the same either way.

### The volume

```csharp
[Component]
[DataContract]
public struct PostProcessVolume {
    public Vector3 Extents;       // half-size, in the entity's own space
    public float BlendRadius;     // metres outside the bounds over which it fades in
    public float Weight;          // a master multiplier, 0..1
    public int Priority;          // higher wins
    public bool Unbound;          // ignore the bounds; this is the base layer
    public PostProcessSettings Settings;
}
```

Its transform is the entity's, so a rotated volume is a rotated box and the containment test runs in
the volume's own space.

### The fold

A `PostProcessVolumeSystem` in `SystemPhase.PreRender`, the same placement and for the same reason as
`CameraExtractionSystem` — it reads `WorldTransform` after the transforms are written, so a volume
that moved this frame is tested where it now is.

```
result = default                      // every field unset
for each volume, ascending by Priority:
    weight = Weight * Falloff(camera, volume)
    if weight > 0: result = Blend(result, volume.Settings, weight)
```

⚠ **Ascending, and the last write wins by weight rather than by replacement.** A high-priority volume
at weight 0.5 is *half* applied over what is under it, which is what makes a doorway a crossfade. A
sort that let priority win outright would make the blend radius decorative.

### Falloff

Inside the box, 1. Outside it, `1 - saturate(distance / BlendRadius)` where the distance is to the
box's surface in the volume's own space. A `BlendRadius` of zero is a hard edge, which is what a
skybox-changing volume wants and a lighting volume does not.

### Reaching the nodes

```csharp
public interface IPostProcessTarget {
    void Apply(in PostProcessSettings settings);
}
```

Implemented by the renderers in `Vixen.Rendering.PostFx`, which is downstream of the compositor — the
same direction `ISceneRendererFactory` already sends knowledge, and for the same reason: the builder
cannot know these types without a cycle.

⚠ **A node applies only the fields it owns, and applies them over its _authored_ value rather than
over last frame's.** A node that accumulated would drift: two frames inside the same volume would
apply the offset twice. Each node keeps what the document gave it and treats the blended settings as
an overlay computed fresh each frame.

`GraphicsCompositor` needs one thing it has not got: **a walk that finds nodes by name**.
`SceneRenderer.Children` is a public list and `Name` is init-only, so this is a tree walk and about
twenty lines — but it does not exist today and every consumer of this feature needs it.

---

## The editor

This is where the feature's value is, and where most of its cost is.

| Piece | What |
|---|---|
| **Gizmo** | The box drawn in the scene view, plus a second wireframe at `BlendRadius` so the falloff is visible rather than a number. A `SceneShow.Volumes` flag beside `Lights` |
| **Inspector** | The nested settings struct, where an **unset field reads as unset** and setting it is what turns the override on |
| **Create menu** | A volume with sensible extents at the cursor, on the `Initial(Type)` list beside `Light` and `Camera` — a zeroed volume has zero extents and zero weight, which is invisible and inert |

⚠ **The inspector is the part with a real design question in it.** A `float?` shown as a plain number
box cannot express "unset", and showing `0` for unset is the worst possible answer — it reads as an
override to black. Each optional field needs a checkbox beside it, exactly as Unreal has, and
clearing it has to write `null` rather than a default.

---

## Order of work

| Step | Item | Size | State |
|---|---|---|---|
| 1 | `PostProcessSettings`, and `PostProcessOverlay` beside it | S | ✅ two types, not one — see below |
| 2 | `PostProcessVolume` component and the falloff | S | ✅ |
| 3 | `PostProcessVolumeSystem` — gather, sort, fold | M | ✅ |
| 4 | `IPostProcessTarget` + a walk over the frame | M | ✅ no name matching — see below |
| 5 | The nodes implement it | M | ✅ tonemap, bloom, vignette, fog, local exposure, flare, defocus |
| 6 | Host wiring: the system's result reaches the compositor each frame | S | ✅ |
| 7 | Editor — gizmo, show flag, create-menu default | M | ✅ two boxes, and rings for an unbound one |
| 8 | Editor — optional-field inspector | M | ✅ and it needed the nested drawer lifted to structs first |
| 9 | Tests, guide page, a volume in the arena | M | ✅ |

### Two corrections this document earned by being implemented

**`Blend` on the settings type does not work, and the fold needs a second type.** This document
specified `PostProcessSettings.Blend(under, over, weight)`. Writing it exposed the flaw: a volume half
faded in wants half its offset — but half of the way from *what*? From the value the node was authored
with, which lives in the node and not in the volume. So the fold cannot finish the interpolation, and
a plain `float?` coming out of it either loses the weight or bakes it against a number the fold had to
invent. `PostProcessOverlay` carries a `(value, weight)` pair per field and the node finishes the lerp
against its own authored value. Different fields carry different weights, which also rules out one
weight for the whole struct.

**Node lookup by name was specified and is not what was built.** Matching a volume's opinion to a node
by the name a document happened to give it makes renaming a node in a compositor silently unwire every
volume in every level. `GraphicsCompositor.Apply` hands the overlay to every node implementing
`IPostProcessTarget` instead, and each takes the fields it owns. A frame with two `!Bloom` nodes has
both brightened, which is what "in this room the glow is stronger" means.

⚠ **The editor step had a prerequisite this document did not see.** `NestedDrawer` only claimed types
in the generated `InspectorRegistry`, and `VXI0103` refuses `[Inspector]` on a value type — so a
`[DataContract]` **struct** was drawn by the read-only last resort, which is exactly what
`PostProcessVolume.Settings` is. Lifting it needed the descriptor resolved through
`ReflectedDescriptor` and the working box written back through the outer member, both of which the
existing comment in that file had already named as the missing pieces.

### What is deliberately not in the first version

**Shapes other than a box.** A sphere volume is a second containment test and a second gizmo for a
case a box covers. Unreal supports any brush; nobody uses one.

**A LUT per volume.** It needs the graph to declare every table a level might blend to, up front,
because the graph cannot gain a resource mid-frame. That is a real mechanism and it is separate.

**Blendables.** `!FullScreen` already runs an arbitrary shader wherever the node is written, which is
the general case of Unreal's four insertion points. What a volume would add is *weighting* one, and
that is a node parameter like any other once the node exists.

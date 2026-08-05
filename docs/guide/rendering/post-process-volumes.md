---
title: Making a room look different
slug: rendering/post-process-volumes
kind: guide
area: Rendering
summary: A region a level designer places to say how the frame looks inside it, how two of them resolve where they overlap, and the one thing a volume cannot do.
api: [T:Vixen.Rendering.Ecs.PostProcessVolume, T:Vixen.Rendering.Ecs.PostProcessSettings, T:Vixen.Rendering.PostProcessOverlay, T:Vixen.Rendering.Blended, T:Vixen.Rendering.BlendedColour, T:Vixen.Rendering.Ecs.PostProcessVolumeSystem, T:Vixen.Rendering.Compositor.IPostProcessTarget, T:Vixen.Rendering.Ecs.IPostProcessShape, T:Vixen.Rendering.Ecs.IPostProcessShapeSource, T:Vixen.Rendering.Ecs.PostProcessShapeKind, T:Vixen.Rendering.Ecs.BoxPostProcessShape, T:Vixen.Rendering.Ecs.SpherePostProcessShape, T:Vixen.Editor.Inspector.Drawers.OptionalDrawer, T:Vixen.Editor.Inspector.Drawers.OptionalEditor]
tags: [rendering, post-processing, compositor, editor]
since: 0.1
status: stable
related: [rendering/post-processing, rendering/physical-lighting, rendering/look-profiles, editor/frame-panel]
---

## What it is

A component a scene places on an entity, saying how the frame looks where that entity is. The box is
the entity's own — rotated, scaled and moved with the transform gizmo — and everything inside it is
graded the way the volume says.

| Field | What it decides |
|---|---|
| `shape` | `Box`, `Sphere`, or `Custom` for one something else supplies |
| `extents` | Half the box's size, or the sphere's radii, in the entity's own space |
| `blendRadius` | How far **outside** the shape it fades in, in metres |
| `weight` | A master multiplier on everything it contributes |
| `priority` | Which volume wins where two overlap — higher is on top |
| `unbound` | Applies everywhere, ignoring the shape. The level's base look |
| `settings` | What it has an opinion about |

## What it is for

A compositor document names the frame's passes and grades the whole level one way. It cannot know the
player has walked into a cellar. This is how a level says so — placed by a designer, with no code and
no second document.

⚠ **Every field of `settings` is optional, and that is the whole design.** A volume contributes only
what it sets; everything else falls through to whatever is underneath. So a volume that only wants the
cellar darker does not also silently reset the grade, and two volumes that each care about one thing
can both apply.

`null` and `0` are different states. A bloom intensity of zero means "no bloom here", which is a real
thing to say; unset means "I have no opinion about bloom". The inspector draws a checkbox beside every
optional field for exactly this reason.

## Using it

```yaml
# The level's base look. `unbound` is Unreal's "Infinite Extent": it applies wherever the camera is,
# at the lowest priority, so everything else lays over it.
- name: BaseGrade
  components:
    - !PostProcessVolume
      unbound: true
      priority: -100
      weight: 1
      settings:
        saturation: 1.05

# And one corner of it, which is a different place.
- name: ColdCorner
  position: -20 3 -20
  components:
    - !PostProcessVolume
      extents: 10 6 10
      blendRadius: 6
      weight: 1
      priority: 0
      settings:
        exposureCompensation: -2
        saturation: 0.55
        temperature: 7800
        fogDensity: 0.05
```

⚠ **`blendRadius` is measured outside the box, not inside it.** Place the box around the region that
should be fully affected and the fade happens in the approach, so widening the falloff does not shrink
the room. Zero is a hard edge — right for something discrete, wrong for a lighting change.

⚠ **The falloff is measured to the shape's _surface_.** A long thin volume measured from its centre
would fade in from much further away at its ends than along its sides, which reads as a corridor whose
grade starts before the corridor does.

### The shape

A volume is a box unless it says otherwise, and `Box` is the enum's zero — so every volume authored
before shapes existed loads as the box it was.

| `shape` | What `extents` means | When |
|---|---|---|
| `Box` | Half the box's size | A room, a corridor, a region of a level |
| `Sphere` | The ellipsoid's radii; uniform radii are a sphere | A brazier's warmth, a light shaft, a pool of damp |
| `Custom` | Nothing — the shape comes from `IPostProcessShapeSource` | A water body, below its own surface |

```yaml
# A four-metre ball of warmth around a brazier. `extents` is read as radii here.
- name: BrazierWarmth
  position: 2 2.35 19
  components:
    - !PostProcessVolume
      shape: Sphere
      extents: 4 4 4
      blendRadius: 3
      priority: 10
      weight: 1
      settings:
        exposureCompensation: 0.35
        temperature: 8200
```

A box fading over a radius has corners, and a corner in a grade is visible as a straight edge crossing
a floor where nothing else is straight. That is what the sphere is for, and it is why it shipped
alongside the interface rather than after it: an extension point whose only non-default implementation
is one special case ends up shaped like that special case.

⚠ **The sphere's exterior distance is exact for uniform radii and a lower bound otherwise.** There is
no closed form for the distance to an ellipsoid's surface, so non-uniform radii use a bound that never
overstates it — a volume authored the common way gets the exact answer, and one authored as an
ellipsoid never fades out earlier than its blend radius says.

⚠ **A `Custom` volume with no source reaches nothing, not everything.** The alternative — falling back
to the box — grades a rectangle around the lake while the inspector looks correct, which is the
failure that costs an afternoon. Set `PostProcessVolumeSystem.Shapes` to whatever can answer:

```csharp no-compile="the shape, not a compiling source"
sealed class WaterVolumes : IPostProcessShapeSource {
    // ⚠ Asked once per custom volume per frame, from the frame's own fold — so the answer should be
    // an object that lives as long as the entity does, not one built per call.
    public IPostProcessShape? ShapeFor(Entity entity) =>
        bodies.TryGetValue(entity, out var body) ? body : null;
}
```

### How two volumes resolve

Volumes are sorted by priority and applied in ascending order, so the highest-priority one is applied
last. **"Applied last" is not the same as "wins outright"** — it wins *by its weight*, which is what
makes a doorway a crossfade:

| Situation | Result |
|---|---|
| One volume, fully inside | Its value |
| One volume, half faded in | Half way from the **document's** value to the volume's |
| A fully applied volume, then a half-faded one over it | Half way between the two |
| A field the volume does not set | The document's, untouched |

⚠ **Two volumes at the same priority resolve arbitrarily.** The order the world is walked in decides,
and that is deliberately undefined: two volumes both fully claiming one field at one priority is a
level-design mistake rather than a case worth inventing a tiebreak for.

**Below every volume sits the project's [look profile](look-profiles.md)** — one `.vxlook` of the
same per-parameter opinions, laid down first at full weight, so any volume's word beats it per
parameter whatever the volume's priority. A scene overrides the project by saying something; the
project covers everything the scenes leave unsaid.

### ⚠ What a volume cannot do

**It blends parameters. It cannot change which passes exist.** The frame's graph decides resource
lifetimes, pass ordering and transient aliasing, and rebuilding it as somebody crosses a threshold
would be a graph recompile every frame.

So `maximumDefocus` in a document with no `!DepthOfField` node does nothing at all, and cannot say so
at author time. Unreal has the same constraint and hides it inside one uber-pass; here the passes are
named in a file you can read, which is what makes it statable.

### ⚠ `temperature` runs the opposite way to the intuition

It is a **white balance**: the number names the light being corrected *for*, and correcting for a warm
light cools the picture.

| `temperature` | Looks |
|---|---|
| 3000 K | strongly cool |
| 4000 K | cool |
| 6500 K | neutral |
| 7800 K | warm |

Writing 7800 to mean "this room is cold" gives a warm room. The sample's `ColdCorner` uses 4000 and
its `WarmCorner` uses 7800 for exactly this reason.

⚠ **It is also the only setting whose unset value is a sentinel rather than a value.** Every other
field's zero means something — a bloom intensity of nothing, a neutral hue shift — so blending toward
it is well defined. There is no temperature that means "do not white balance", so the tonemap blends
the resulting *multiplier* and never the kelvin. Interpolating the kelvin passes through 7 K, which
clamps to 1667 K and whose correction is an enormous blue gain: it shipped that way once, and it read
as a hard flip to blue at the volume's edge that got *less* blue the further in you walked.

### ⚠ The lens is not in here

Unreal's `FPostProcessSettings` carries the f-stop, the shutter, the ISO and the focal distance — so
a level's volume can silently override a cine camera's depth of field, which is a well-known gotcha.

Those belong to [`Camera`](physical-lighting.md#two-conventions-and-both-are-named), which is the one
place they can be without two claimants on one number. A volume describes a **place**; a lens belongs
to the **camera**.

That is also why exposure arrives as a **compensation** rather than a value. "This cellar is two stops
under what the meter says" composes with whatever the camera and the auto-exposure decided, and two
overlapping volumes add two offsets. "This cellar is EV 9" would be a second claimant on a number
something else owns.

⚠ It is a separate uniform on the tonemap for a concrete reason: a metered frame ignores the authored
exposure entirely — the shader reads the buffer — so a volume that darkened a room by scaling that
number would do nothing in exactly the frames that have an auto-exposure.

## Examples

**In the editor.** Create the component from the add-component menu and it arrives as a five-metre
cube with a one-metre falloff — a zeroed one contains nothing and contributes nothing, which is a
component that appears not to work.

The scene view draws **two** boxes: the inner one is where the volume fully applies and the faded
outer one is its blend radius. That second box is the point — a volume that looks right and does
nothing is nearly always one whose falloff the camera never enters, and a number in an inspector
cannot show that. `Show ▸ Post-process Volumes` toggles them.

An **unbound** volume draws two rings instead of a box, because its extents mean nothing and drawing
a boundary that does not exist is worse than drawing none.

**Reading the fold at run time**, which is what answers "my volume is not doing anything":

```csharp no-compile="graphics is the host's AppGraphics"
// How many volumes are placed, and how many are actually reaching the camera.
var placed = graphics.Volumes.VolumeCount;
var reaching = graphics.Volumes.ContributingCount;
var quiet = graphics.Volumes.Overlay.IsEmpty;
```

A volume that is placed and not contributing has a zero weight, zero extents, no settings, or a camera
outside its blend radius — and all four look identical to one that is not wired up.

And the resolved stack itself, per camera — which layer said what, in application order:

```csharp no-compile="graphics is the host's AppGraphics"
var stack = new List<(string Layer, string Parameter)>();
graphics.Volumes.Contributions(stack);   // ("look", "ev100"), ("scene", "saturation"), …
```

**A node taking part.** Any `SceneRenderer` may implement `IPostProcessTarget`, including a project's
own:

```csharp no-compile="the shape, not a compiling node — see TonemapRenderer for a whole one"
public sealed class MyEffect : SceneRenderer, IPostProcessTarget {
    PostProcessOverlay applied;

    public float Strength { get; set; } = 1f;

    public void Apply(in PostProcessOverlay overlay) => applied = overlay;

    void Configure(ParameterCollection parameters) =>
        // ⚠ Over the *authored* value, every frame, from the same starting point. A node that wrote
        // into its own properties would lose what the document said the first time a volume reached
        // it, and walking back out would restore the volume's numbers rather than the document's.
        parameters.Set(MyKeys.Strength, applied.Saturation?.Over(Strength) ?? Strength);
}
```

## See also

- [The post-processing node kinds](post-processing.md) — the seventeen effects a volume grades.
- [Lighting a scene in lux and lumens](physical-lighting.md) — where the lens lives, and why not here.
- [A pass that reads the frame so far](reading-the-frame.md) — the other half of the same
  generalisation, for a pass rather than a place.
- `docs/plan/32-post-process-volumes.md` — the design, and what was deliberately left out of the
  first version.
- `docs/plan/35-water.md` § B2 — why the containment test stopped being an axis-aligned box.

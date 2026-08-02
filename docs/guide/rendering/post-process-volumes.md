---
title: Making a room look different
slug: rendering/post-process-volumes
kind: guide
area: Rendering
summary: A box a level designer places to say how the frame looks inside it, how two of them resolve where they overlap, and the one thing a volume cannot do.
api: [T:Vixen.Rendering.Ecs.PostProcessVolume, T:Vixen.Rendering.Ecs.PostProcessSettings, T:Vixen.Rendering.PostProcessOverlay, T:Vixen.Rendering.Blended, T:Vixen.Rendering.BlendedColour, T:Vixen.Rendering.Ecs.PostProcessVolumeSystem, T:Vixen.Rendering.Compositor.IPostProcessTarget, T:Vixen.Editor.Inspector.Drawers.OptionalDrawer, T:Vixen.Editor.Inspector.Drawers.OptionalEditor]
tags: [rendering, post-processing, compositor, editor]
since: 0.1
status: stable
related: [rendering/post-processing, rendering/physical-lighting]
---

## What it is

A component a scene places on an entity, saying how the frame looks where that entity is. The box is
the entity's own — rotated, scaled and moved with the transform gizmo — and everything inside it is
graded the way the volume says.

| Field | What it decides |
|---|---|
| `extents` | Half the box's size, in the entity's own space |
| `blendRadius` | How far **outside** the box it fades in, in metres |
| `weight` | A master multiplier on everything it contributes |
| `priority` | Which volume wins where two overlap — higher is on top |
| `unbound` | Applies everywhere, ignoring the box. The level's base look |
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

⚠ **The falloff is measured to the box's _surface_.** A long thin volume measured from its centre
would fade in from much further away at its ends than along its sides, which reads as a corridor whose
grade starts before the corridor does.

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

### ⚠ What a volume cannot do

**It blends parameters. It cannot change which passes exist.** The frame's graph decides resource
lifetimes, pass ordering and transient aliasing, and rebuilding it as somebody crosses a threshold
would be a graph recompile every frame.

So `maximumDefocus` in a document with no `!DepthOfField` node does nothing at all, and cannot say so
at author time. Unreal has the same constraint and hides it inside one uber-pass; here the passes are
named in a file you can read, which is what makes it statable.

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
- `docs/plan/32-post-process-volumes.md` — the design, and what was deliberately left out of the
  first version.

---
title: The variation harness
slug: animation/variation-harness
kind: guide
area: Animation
summary: Playing one interaction across a range of bodies, props and ground, measuring it five ways, and failing a build when it regresses.
api: [T:Vixen.Animation.Constraints.VariationHarness, T:Vixen.Animation.Constraints.HarnessPlan, T:Vixen.Animation.Constraints.VariationReport, T:Vixen.Animation.Constraints.HarnessCell, T:Vixen.Animation.Constraints.HarnessThresholds, T:Vixen.Animation.Constraints.IVariationSource, T:Vixen.Animation.Constraints.HarnessPlanContent]
tags: [animation, constraints, testing, ci]
since: 0.1
status: stable
related: [animation/pose-constraints, animation/move-sets]
---

## What it is

Point it at an interaction and a range of variation. It plays the interaction across the range and
reports a matrix: every configuration against every goal, with the worst cell selectable.

## What it is for

**Knowing when a clip is finished.** The honest cost of constraints is marking up a library of clips,
and what makes that cost bearable is not authoring faster — it is not having to re-check everything
by hand every time an artist changes a body. An author with a matrix knows which clips are done.

It is not a renderer and it is not a playtest. Nothing it measures involves looking at anything, which
is what lets the same run be a build step.

## The measurements

Five, because five different things go wrong:

| Measurement | What it catches |
|---|---|
| **Residual** | the goal missed — the hand did not get to the rail |
| **Penetration** | the contact sank into what it was supposed to rest on |
| **Jerk** | the effector changed velocity hard — a hand that snaps |
| **Reach** | the chain was straight and still short |
| **Limits** | a joint was sitting at the end of its range of motion |

A hand that snaps shows in the **jerk and nowhere else**: the residual is small on both sides of a
snap, which is exactly what makes a snap invisible in a residual plot.

⚠ **A goal is judged on how far it missed when it was being asked for in full.** Every tag eases in,
and a goal at half weight is *supposed* to be half satisfied — measuring either as error would flag
every clip ever marked up. The run warms up for one loop before anything is recorded, and only the
samples at or near a goal's own peak weight count towards its residual.

⚠ **Reach and limits are separate columns, because they are separate fixes.** A straight arm that is
still short means the contact is somewhere this body cannot get to — answered by moving the contact.
A joint sitting at the end of its range means the pose the solver wanted is one this body may not
adopt — answered by widening the limit or bending elsewhere. A rig that declares no limits reports
nothing in the second column, which is not the same as passing it.

## Using it

```csharp
var report = VariationHarness.Run(
    new() {
        Clip = clipContent,                     // the *authored* form, not a baked clip
        Skeleton = rig,
        Shapes = shapes,
        Samples = 32,
        Thresholds = new() { Residual = 0.02f, Penetration = 0.01f },
        Variations = [
            new BodyVariation(rig, 0.8f, 1f, 1.25f),
            new GroundVariation((0f, 0f), (12f, 0.1f)),
        ],
    }
);

var verdict = report.Judge(thresholds);

if (!verdict.Passed) {
    Console.Error.WriteLine(verdict.Summary);
}
```

The clip is the **authored** `AnimationClipContent` and not a baked `AnimationClip`, because a baked
clip's goals hold joint indices resolved against one skeleton — varying the body means baking again,
and a harness handed a baked clip would pose every body with the first one's joint numbering.

Axes multiply. Two axes are a grid and not two runs, because the failures this exists to find are at
the corners: a short body *and* a wide prop.

## Reading the matrix

Rows are configurations, columns are goals, and a cell carries the worst of each measurement and
**when** it happened. That last part is what makes the report a tool rather than a verdict — a number
saying a clip is wrong somewhere is not actionable; the same number with a configuration and a moment
attached drops you on the frame.

`VariationHarness.Rebuild` turns a row back into the body, the prop and the ground it was, so the
editor can put that on screen. The panel's **Worst Cell** button jumps straight there.

⚠ **A goal that never resolved is a third state, not a large number.** It is the most common way a
variation actually breaks — the hand cannot reach the shape on the small body, the binding resolves
to nothing — and it outranks any amount of error. An author who reads it as a number spends the
afternoon tuning a goal that is not running.

## As a build gate

Declare the run in the project so the thresholds are somewhere the person authoring the clip can see
them and change them. Numbers written into a test are numbers nobody believes.

```yaml
# reach.vxharness
name: reach the rail
clip: Assets/Anim/Reach.vxanim
rig: Assets/Bodies/Hero.gltf
shapes: Assets/Bodies/Hero.vxproxyshapes
samples: 32
bodies: [0.8, 1.0, 1.25]
ground:
  - { degrees: 0, height: 0 }
  - { degrees: 12, height: 0.1 }
thresholds:
  residual: 0.02
  penetration: 0.01
  reach: true
  limits: true
```

The importer refuses a plan with nothing to play or fewer than two samples, and warns about two
things a plan invites: thresholds all left at zero — a green build that means nothing — and a run of
more configurations than anybody meant to start.

A `.vxharness` opens in the editor too: the panel shows the declaration, **Run** fills the matrix,
and a run that cannot start says why rather than throwing — a plan whose clip has not been imported
yet is the ordinary state of one somebody just wrote.

⚠ **There is no `vixen animation check` verb.** `HarnessPlanContent.Resolve` turns the declaration
into a run given the clip, the rig and the shapes; wiring those from a project on the command line
additionally needs the rig resolved through the model import path, and that is not built. Today the
gate is three lines in a test project, which is enough to fail a build and is honest about what it is.

## Varying something else

`IVariationSource` is an axis: a name, some values, and a way to apply one to a subject.

```csharp
sealed class WeatherVariation : IVariationSource {
    public string Name => "surface";
    public int Count => 2;
    public string Label(int index) => index == 0 ? "dry" : "icy";

    public void Apply(int index, HarnessSubject subject) {
        subject.Slots[Symbol.Intern("ground")] = /* … */;
        subject.Describe(Label(index));
    }
}
```

Subjects are mutable and handed to every source in turn, so two axes compose without knowing about
each other.

## See also

- [Pose constraints](animation/pose-constraints) — what the harness is measuring
- [Move sets](animation/move-sets) — the other half of doc 34

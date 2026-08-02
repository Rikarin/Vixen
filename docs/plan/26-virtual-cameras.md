<!--
SPDX-FileCopyrightText: Copyright (c) Rikarin
SPDX-License-Identifier: Apache-2.0
-->

# Virtual cameras

A procedural camera system in the shape Cinemachine established: a scene holds many *shots*, each one
a point of view that knows how to place and aim itself, and a *director* beside the real camera picks
one of them every frame and blends when the pick changes.

This document extends [04 § Layer 3](04-ecs-and-scripting.md) — which introduced `Camera` as a
component and stopped there — and it is a separate file for the reason
[24](24-blockout-tools.md) is: the first half is an argument rather than a schedule.

---

## The argument

Doc 04 gives a game a camera component and a transform, which is everything it needs and nothing it
wants. What gets written on top of that, every time, is a `CameraController` behaviour: follow the
player at an offset, smooth it, look ahead a bit, don't clip the wall, shake on impact. It works, and
then the game acquires a second camera — a cutscene, a shop, a death cam, a boss intro — and the
controller grows a mode enum. By the fifth mode the transitions between modes outnumber the modes,
each one is written once and tested never, and the thing nobody can do is *look at a shot and see
what it does*, because what it does depends on which of thirty-one transitions arrived at it.

The insight Cinemachine is built on is that this is the wrong decomposition. **A camera is not a
thing with modes; it is a stage with many possible shots and one of them lit at a time.** Once shots
are separate objects, three properties fall out that the controller-with-modes cannot have:

1. **A shot is authored, not coded.** Its framing is data — an offset, a dead zone, a damping time —
   so it is set up by whoever is composing the picture, in the editor, against the running game.
2. **Transitions are a property of the pair, not of the code.** There is no `SwitchToShopCamera`.
   There is a shot with a higher priority, and a blend that describes how the camera gets there. Ten
   shots need ten definitions rather than ninety transitions.
3. **The procedural parts compose.** "Where it is" and "where it looks" are independent questions
   with four or five good answers each, and any pair of answers is a valid camera. A mode enum
   multiplies them; separate stages add them.

That is worth building because the third property is what a camera *is*. Almost every camera in
almost every game is one of: sits still, follows a subject, orbits a subject, is welded to a subject
— crossed with: looks at nothing, looks at a subject, looks where the player is pointing, looks the
way the subject is facing. Sixteen cameras from eight pieces, plus the framing rules that make each
one feel authored rather than computed.

⚠ **This does not make Vixen's camera a Cinemachine clone, and the resemblance is deliberate rather
than incidental.** The names are the ones a person coming from Unity already knows — body, aim,
composer, dead zone, damping time, priority, blend, impulse — because the concepts are the same and
inventing new names for them would cost a reader a translation table and buy nothing. Where the
behaviour differs, the difference is named in this document.

---

## The model

Three kinds of thing, and the whole system is what happens when a frame walks through them in order.

| | |
|---|---|
| **The shot** — `VirtualCamera` | A priority, an on/off switch, a lens, a channel. Every shot is evaluated every frame into a `CameraShot`: a position, a rotation, a lens, and a shake held separately from all three |
| **The stages** | Optional components beside the shot. A **body** (`FollowBody`, `FramingBody`, `OrbitBody`, `HardLockBody`) decides where it is; an **aim** (`ComposerAim`, `HardLookAim`, `PovAim`, `MatchTargetAim`) decides where it looks; **extensions** (`CameraConfiner`, `CameraOcclusion`) correct both; **shake** (`CameraNoise`, `CameraImpulseListener`) is added on top and never fed back |
| **The director** — `CameraDirector` | Beside the real `Camera`. Picks the enabled shot with the highest priority on its channel, blends when the pick changes, and writes the result onto the camera entity's transform and lens |

Two systems run it, both in `SystemPhase.LateUpdate`, in that order: `VirtualCameraSystem` evaluates
every shot, `CameraDirectorSystem` chooses and writes.

---

## Decisions

### Which stage a shot uses is an archetype question

Each stage is a chunk sweep over the shots that have that stage's component. The follow bodies are
one monomorphic pass over a contiguous column; the composers are another. A shot with no body at all
is matched by a query with `WithNone` over all four of them, and takes its position from its own
transform — which is how a hand-placed establishing shot works with nothing on it but the component
that says it is a shot.

The alternative is one fat component with an enum and a union of every stage's settings, branched on
per entity. It would be smaller to write and would put a switch in the hot loop, would make every
shot pay the memory of the stage it does not use, and would make adding a stage an edit to a type
every existing shot is stored as. This is the same shape `TransformSystem` uses for its roots, and
the same shape doc 04 argues for generally.

### The stages are passes inside one system, not systems of their own

Their order **is** the design: a body before an aim, because an aim needs somewhere to look from; the
confiner and the obstacle avoider between them, because a camera that has been moved must still look
at its subject from where it ended up; the shake last, because nothing downstream may damp against
it. Written as eleven systems with `UpdateAfter` attributes that would be eleven separate things to
get wrong — and every one of them writes `CameraShot`, so the scheduler would serialise them anyway.

### Every shot is evaluated every frame, live or not

Cinemachine makes this a per-camera setting (`StandbyUpdate`) and pays for it with shots that lurch
when they come on, because their damping resumes from wherever they were left. A shot is a few dozen
floating-point operations and a scene has tens of them. The setting is not worth the class of bug it
opens, and the measurement that matters is already made: a hundred shots with every stage between
them, stepped five hundred times, allocate zero bytes.

### `LateUpdate`, and targets resolved by walking the parent chain

[04](04-ecs-and-scripting.md)'s phase list puts "cameras that follow" in `LateUpdate` and resolves
transforms in `PreRender`, which means `WorldTransform` during `LateUpdate` holds *last frame's*
answer. A camera that followed that would render one frame behind its subject — visible as a subject
that slides about within the frame whenever anything accelerates, and one of the most-reported
Cinemachine-adjacent bugs in every engine that has this ordering.

So targets are read through `Hierarchy.ResolveWorldMatrix`, which composes the chain upward and is
therefore current. It costs one matrix multiply per level of depth for a handful of entities. The
director then writes the camera's *local* transform, so the ordinary `PreRender` pass resolves the
camera **and everything parented to it** in the same frame; a weapon model on a camera rig does not
lag the camera.

### A damping time is the time in which 99 % of the error is removed

One definition, everywhere, and it is Cinemachine's constant so that a number copied off a Unity
component means the same thing here. The implementation is exponential — `1 − exp(ln(0.01)·dt/T)` of
the remaining error per step — which composes *exactly*: the residual after a second is the same
whether the second arrived as one frame or a hundred. The usual `Lerp(current, target, 0.1f)` does
not have that property and means a different camera at 30 Hz than at 144.

The rotational form is exact too, which is less obvious: a slerp travels the geodesic at constant
angular speed, so the residual *angle* composes the way a residual distance does. An `Nlerp` in the
same place would not, which is why this is the one place in the engine that pays for a slerp per
frame.

### Framing is done in angles, not in screen units

A composer answers "the subject is outside the dead zone" by turning. The tempting arithmetic is
`atan(overshoot · tan(fov/2))`, and it is right near the middle of the frame and increasingly wrong
towards its edges, because a screen coordinate is proportional to the *tangent* of the angle off the
view axis. The correct correction is the difference of two arctangents, it costs one more `atan`, and
without it a subject entering from the side gets snatched past the dead zone towards the centre.

### The soft zone is a promise, and damping is not allowed to break it

Inside the dead zone the camera does not move. Between the dead zone and the soft zone it turns at
the rate the damping time allows. Outside the soft zone it turns however fast it must — because
damping alone can be outrun by anything faster than it converges, and a camera that falls behind a
sprinting subject and never catches up has lost the shot.

### What a shot follows is not something a scene can author

`CameraTargets` carries entity handles and is therefore `[Component]` without `[DataContract]` — the
line `PhysicsBody` is already on, and the reason doc 04 gives: a handle names a slot in the world
that issued it. A level places its shots, their priorities, their lenses and their framing; something
running in the world says what they point at. That is where most games put it anyway, because the
thing being followed is usually spawned.

⚠ **What it costs is the cutscene camera framing a prop the same scene placed**, and buying that back
needs entity references in the compiled scene format, which do not exist. It is recorded as owed
rather than worked around, because every workaround is a second way for a handle to get into a file.

### Ties go to the shot enabled most recently

Priority decides; equal priorities go to whichever `Enabled` most recently became true. That is what
a designer means when they wire two triggers to two cameras and give neither a number. Breaking the
tie by entity id would be equally deterministic and would make the second trigger appear to be
broken, for ever.

### An interrupted blend freezes rather than nesting

When the pick changes during a blend, the state the director produced *that frame* becomes the
outgoing side of the next one. One snapshot, no stack, no pop at the moment of the interruption.
Cinemachine keeps the whole chain alive and evaluates it recursively, which is smoother under a rapid
series of cuts and unbounded in cost. The visible cost of freezing is that a handheld shake on the
outgoing shot stops moving for the length of the second blend; a blend that is *not* interrupted goes
on evaluating both shots live, so the common case pays nothing.

### Shake is held apart from the damped state

Noise and impulse write `CameraShot.ShakePosition` / `ShakeRotation`, in camera space, and only the
composed output folds them in. A damped camera that could see its own shake would chase it — the
noise becomes an input to the smoothing that is supposed to be underneath it, and the result reads as
a loose mounting rather than a hand-held camera. It is a two-line mistake and an hour to diagnose.

### The noise is value noise, so an amplitude is a bound

`VfxNoise` chose value noise over Perlin because a gradient table is the one thing a GPU would have
to be handed rather than compute. The camera's reason is different and stronger: the range of value
noise is exactly the range of its lattice values, so a shake declared as five centimetres never
exceeds five centimetres. A gradient-noise peak is a number you look up and hope for, and "the shake
is 5 cm, except occasionally" is not something a designer framing a shot near geometry can work with.

### An impulse is an initial velocity, not a displacement

A shell landing gives the camera a shove in metres per second, and what is seen is the ring-down: a
decaying oscillation of amplitude `|v| / 2πf`. Doubling the frequency halves the visible kick from
the same number, which is what makes a high-frequency rattle and a low-frequency lurch different
events rather than the same one played at different speeds. The envelope is `(1 − u)²` rather than an
exponential, so the signal and its slope are both exactly zero at the end and the impulse can be
dropped instead of ringing below the noise floor for the rest of the level.

### Obstacle avoidance asks a question the engine cannot answer

`Vixen.Engine` references no physics, and that direction is what keeps `Vixen.Physics` an optional
subsystem rather than a dependency of the frame loop. So `CameraOcclusion` asks through
`ICameraOcclusion`, which a host implements over whatever it has. The cast goes *outward from the
subject*, not inward from the camera: a camera that has already ended up inside a wall is behind the
surface, and an inward cast from there reports the wall's far side or nothing at all.

---

## What is deliberately not built

| Not built | Why, and what it would take |
|---|---|
| **Dolly track** — a camera on a spline, with auto-dolly | Wants a spline *asset*: authored, serialised, editable in the viewport, shared with whatever else needs a path. Inventing one here would make it the second spline in the engine the moment anything else needs one. It is the largest owed item and the one most worth doing. ⚠ **That moment arrived**: landscape splines want the same asset, so it is built once in [31 § T8](31-terrain-grass-and-trees.md#t8--splines--15-em) — control points and tangents, arc-length parameterisation, serialisation, and viewport editing on the gizmo and `SnapContext` that already exist. What is left here afterwards is the camera stage that reads it, which is the small thing this row always said it was |
| **Target groups** — framing several subjects at once | A bounding sphere over N targets, fed to a body as a virtual target, with the lens widening to hold it. Genuinely useful for a two-player brawler and genuinely small; owed rather than cut |
| **Recentring** — an orbit that drifts back behind the subject after an idle | `OrbitBody` reads two angles that gameplay writes, and a recentre is a tween on those two numbers. Whether it belongs in the engine or in the ten lines of game code that already own the stick is not settled |
| **Blend curves as an asset** | Five styles cover the cases; a curve asset is a second thing to load, serialise and get wrong, and the difference is not visible over the half-second a cut takes. `CameraBlendTable` covers the per-pair exceptions, in memory |
| **Blending around an arc** | Both shots would have to agree about the point being orbited, and produce a wild move whenever they do not. A straight line through geometry is the known cost, and a blend between two shots on opposite sides of a wall should be a cut |
| **A composer on an orthographic lens** | Where a point lands in an orthographic frame barely depends on which way the camera is turned, so a stage that answers framing errors by turning cannot converge. `FramingBody` answers the same error by moving, which is what works there |
| **A polygon confiner** | A box clamps in three components and cannot fail. A hull or a polygon needs a containment test, a projection, and an answer for a camera that starts outside — which belongs with a level's collision data |
| **Solo / preview in the editor** | An editor feature rather than a runtime one, and it lands with the inspector work in [20](20-editor-parity.md) |

---

## What it costs

Built: ~2 900 lines across `Core/Vixen.Engine/Cameras`, plus ~1 000 of tests. The owed items above are
roughly a week for the target group and the recentring together, and two to three for the dolly track
— nearly all of which is the spline asset and its editing, not the camera stage that reads it.

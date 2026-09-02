---
title: What a play session runs
slug: editor/play-mode-systems
kind: guide
area: Editor
summary: How a module adds systems to the frame the editor's Play button steps — the `IPlaySystems` contribution, the `PlaySession` that owns their lifetime, and the physics world the editor now stands up on Play and takes away on Stop.
api: [T:Vixen.Editor.SceneView.IPlaySystems, T:Vixen.Editor.SceneView.PlaySession, T:Vixen.Editor.SceneView.PlaySystemOrder, T:Vixen.Editor.SceneView.ProvidesAttribute, T:Vixen.Editor.SceneView.RunsAfterAttribute, T:Vixen.Editor.Terrain.Physics.TerrainPhysicsModule]
tags: [editor, play-mode, systems, physics, terrain, collision]
since: 0.1
status: preview
related: [editor/modes, editor/writing-a-plugin, editor/terrain-mode, editor/terrain-sculpt-collision, engine/terrain-collision, engine/declaring-a-frame]
---

## What it is

Pressing Play in the editor steps an `EngineLoop` over the world being edited. That loop's default
registration is a fixed, small set — the behaviour lifecycle, the four coroutine drains, and
`TransformSystem` — because every other system a game runs is added by that game's own
`OnInitialise` against a service the loop cannot invent.

`IPlaySystems` is how something that *owns* such a service adds the systems that need it. A
contribution is registered into `IEditorRegistry`, the same typed multimap a plugin's gizmos and
inspectors go into, and `PlayModeController` reads them when Play is pressed:

```csharp no-compile="a fragment; `context` is the module's PluginContext"
context.Owns(context.Services.Require<IEditorRegistry>().Add<IPlaySystems>(new PlayAudio()));
```

`PlaySession` is what `Attach` is handed: the `Loop` to add systems to, the `World` they run over,
somewhere to put the teardown, and a small typed bag so one contribution can hand a service to the
next.

| Member | What it is for |
|---|---|
| `Loop`, `World` | What to add systems to, and what they run over |
| `Owns(resource)` | Disposed when the session ends, newest first |
| `OnStop(action)` | The same, for what is not an `IDisposable` |
| `Provide<T>` / `TryGet<T>` | One contribution's service, found by a later one |
| `Runs(name)` | What to tell the person this session is running |

`TerrainPhysicsModule` is the first user of all of it, and the shipped editor's: it publishes the
`ITerrainColliders` the sculpt tools push strokes into, and contributes the `TerrainColliderSystem`
that turns those strokes into Jolt height fields while a session is running.

## What it is for

Two things a person can now do that they could not.

**Press Play and have the ground be solid.** The editor holds a `PhysicsScene` for the duration of a
session — created on Play, disposed on Stop — with the four fixed-step passes and the render-time
interpolation in the loop. A terrain in the scene gets one height-field body per tile, so a character
controller dropped into a session stands on the hill somebody just sculpted instead of falling
through it.

**Sculpt while playing and have the collision follow.** `TerrainEdit.Commit` calls
`ITerrainColliders` after every stroke that moved a height, and what answers is the session's
collider system, rebuilding only the tiles the stroke's rectangle touched.

⚠ **Physics belongs to play, not to editing.** Nothing simulates while the editor is editing, and
that is a decision rather than an omission: a body that falls while somebody is dragging a gizmo is a
scene that edits itself, and ground that settles a centimetre every time you look at it is ground
nothing can be placed on. It also puts every body *inside* the snapshot — `WorldSnapshot.Capture`
runs before any contribution attaches and `Restore` clears the world afterwards — so the entities a
collider system creates leave with everything else the session made, rather than being saved into
somebody's level.

⚠ **Pause and Step Frame need nothing from a contribution.** `PlayModeController.Tick` decides
whether the loop runs at all, so a paused session simply does not call `Frame` and the fixed-step
accumulator stops being advanced. A physics pass that read a "paused" flag of its own would be a
second opinion about the same question, and the two would disagree on the frame a step was consumed.

⚠ **This is not "a project declares its frame", and it does not pretend to be.** A project's own
systems are constructed by that project's `OnInitialise` against services it builds; the editor lists
them by reflection and says it is not running them, which is still the closest true statement
available. What this closes is the other half — a system whose service the *editor* can own no longer
needs an embedding host to add it by hand.

## Using it

A contribution's whole job is `Attach`. Add systems, register the teardown, say what you added:

```csharp no-compile="a fragment; PhysicsScene comes from Vixen.Physics"
sealed class PlayPhysics : IPlaySystems {
    public void Attach(PlaySession session) {
        var scene = session.Owns(new PhysicsScene(session.World));

        session.Loop.AddPhysics(scene);
        session.Provide(scene);
        session.Runs("physics");
    }
}
```

**Contributions attach in registration order, and `TryGet` answers for what came before.** A
`TryGet` that answers `false` means "run without it", not "fail".

**Say what you need and it stops being a matter of registration order.** `[Provides]` and
`[RunsAfter]` name the *service*, and `PlaySystemOrder` puts every provider before everything that
asked for it:

```csharp no-compile="fragments; each is a contribution class in its own assembly"
[Provides(typeof(PhysicsScene))]
sealed class PlayPhysics : IPlaySystems { … }          // in Vixen.Editor.App

[RunsAfter(typeof(PhysicsScene))]
sealed class PlayTerrainColliders : IPlaySystems { … } // in Vixen.Editor.Terrain.Physics
```

⚠ **They name a service and not a contribution, and that is the only form that works here.** The two
that need the ordering in the shipped editor live in different assemblies, one above the other, so a
`[RunsAfter(typeof(PlayPhysics))]` could not be written without inverting the layering. Both already
name `PhysicsScene`, because that is what one hands over and the other asks for.

⚠ **Registration order stays the default and the tie-break.** Contributions with nothing to say about
each other come out in the order they were added, so a set with no attributes anywhere behaves
exactly as it did before these existed.

⚠ **Three things it will not do quietly**, each one a line in `PlayModeController.Ordering` and none
of them a failure: a `[RunsAfter]` for a service nothing declares, a cycle, and — worth reading twice
— a `[Provides]` that attached without providing. The last is checked by asking the session
afterwards rather than by trusting the attribute, because a declaration that has gone stale still
orders everything that asked for it.

⚠ **The sort happens after the world snapshot is captured**, inside `Play`'s call to `Contribute`,
which is what keeps an entity a contribution creates *outside* the snapshot and therefore inside what
Stop clears. An ordering mechanism that ran at registration time would be the one arrangement able to
move that boundary.

**An `Attach` that throws does not stop the session.** The contribution is named in
`PlayModeController.Refused` and reported to the person, because standing systems up takes native
libraries and devices that can be missing on one machine — and a Play button that refused to work
because audio could not open a device would be worse than one that plays without sound and says so.

**Say what you added.** `PlaySession.Running` is read out when the session starts, beside the
systems the project declares and the behaviours the session could not take over. A frame that runs
most of a game and says nothing moves the failure out of the editor, where it is, and into the user's
game, where it is not.

## Examples

Reaching a session's simulation from a test, and asserting about the world rather than a flag:

```csharp no-compile="a fragment against the editor harness"
fixture.Run("play.play");
fixture.Frames(2);

var session = fixture.Editor.PlayMode.Session;

Assert.Equal(["physics", "terrain collision"], session!.Running);
Assert.True(session.TryGet<PhysicsScene>(out var scene));
Assert.True(scene!.BodyCount >= 4);

fixture.Run("play.stop");
fixture.Frames(2);

Assert.Null(fixture.Editor.PlayMode.Session);
Assert.True(scene.IsDisposed);
```

A module that publishes a long-lived seam and a per-session implementation of it — the shape
`TerrainPhysicsModule` uses, and the reason it is two objects:

```csharp no-compile="a fragment; PlayColliders is a switch over the session's collider system"
public void Activate(PluginContext context) {
    var extensions = context.Services.Require<IEditorRegistry>();

    context.Services.Add<ITerrainColliders>(colliders);
    context.Owns(extensions.Add<IPlaySystems>(new PlayTerrainColliders(colliders, extensions)));
}
```

⚠ **The published object has to outlive every session.** `TerrainModule` resolves `ITerrainColliders`
in its per-frame follow and keeps the first answer, and `PluginServices` has no removal — so a
per-session adapter published there would leave the sculpt tools holding a disposed Jolt world for
every stroke after the first Stop. The service is a switch; what it points at is the session's.

⚠ **What a contribution `Provide`s is also what a project's declared systems are resolved against.**
`PlaySession` is an `IServiceProvider`, and `PlayModeController.Contribute` builds the project's
`[GameSystem]` declarations *after* every contribution has attached — so a project system that takes
a `PhysicsScene` finds the one `PlayPhysics` made. Neither side knows about the other; they agree
because `Provide<T>` and a constructor parameter are both keyed on the static type. See
[declaring a project's frame](../engine/declaring-a-frame.md) for the other half.

## See also

- [Declaring a project's frame](../engine/declaring-a-frame.md) — the project side of the same seam:
  how a system says it belongs to the frame, and what happens when its service is not there.
- [Collision under the sculpt brush](terrain-sculpt-collision.md) — the adapter behind the seam, and
  the counter that says the wiring is wrong.
- [Terrain mode](terrain-mode.md) — the tools that push the strokes.
- [Writing a plugin](writing-a-plugin.md) — the registry and the services a contribution comes
  through.
- `docs/plan/11-editor.md` § "Play mode runs a system graph" — which loop drives the editor's frame,
  what a restore has to undo, and what an in-editor session still does not run.
- `docs/plan/31-terrain-grass-and-trees.md` § D10 — why terrain collision is one shape per tile.

---
title: Declaring a project's frame
slug: engine/declaring-a-frame
kind: guide
area: Engine
summary: How a project says which systems its frame is made of — the `[GameSystem]` attribute a generator collects, the constructor that names the services a system needs, and what happens to a declared system nobody can supply.
api: [T:Vixen.Engine.Frames.GameSystemAttribute, T:Vixen.Engine.Frames.GameSystemDeclaration, T:Vixen.Engine.Frames.GameSystemRegistry, T:Vixen.Engine.Frames.GameSystems, T:Vixen.Engine.Frames.FrameActivation, T:Vixen.Engine.Generators.GameSystemGenerator, L:13031, L:13032]
tags: [engine, systems, frame, editor, play-mode, generators]
since: 0.1
status: preview
related: [editor/play-mode-systems, ecs/queries, engine/booting-an-application]
---

## What it is

`[GameSystem]` marks a system as part of this project's frame. A generator collects every one it
sees, writes the constructor call, and `AddDeclaredSystems` builds and adds them:

```csharp no-compile="a fragment; Warehouse and the Game base class are the project's own"
[GameSystem]
[UpdateInGroup(SystemPhase.Update)]
public sealed class RestockSystem(Warehouse warehouse) : SystemBase {
    public override JobHandle Update(in SystemContext context, JobHandle dependency) { … }
}

protected override void OnInitialise() {
    // The service. Not the system — nothing here registers RestockSystem.
    Services.Registry.Add(new Warehouse());
}
```

That is the whole of it. `VixenApplication.Initialise` calls `AddDeclaredSystems` immediately after
`OnInitialise` returns, so a game registers the *services* its systems ask for and the host puts the
systems in the frame. Three things are worth noticing, because each is the point.

**The attribute carries nothing.** A system's phase is already `[UpdateInGroup]` and its order is
already `[UpdateBefore]` / `[UpdateAfter]`; what it reads and writes is already `[Reads]` /
`[Writes]` or `IDeclaredAccess`. `[GameSystem]` says one thing that nothing else said: *this system
is part of the frame*. Anything else it carried would be a second opinion that can go stale.

**The constructor is the service list.** `RestockSystem` needs a `Warehouse` because it takes one.
There is no second list to keep in step, and the key is the *static* parameter type — a system asking
for an `ITerrainScene` gets whatever was registered as an `ITerrainScene`, which is the same key
`ServiceRegistry.Add<T>` and `PlaySession.Provide<T>` already use. A system taking two services is
not a special case:

```csharp no-compile="a fragment; both services are registered by whoever owns their lifetime"
[GameSystem]
public sealed class ColliderSystem(PhysicsScene physics, ITerrainScene terrain) : SystemBase { … }
```

**And the host adds them, in a game and in the editor both.** That symmetry is the reason the
declaration is worth having: a `[GameSystem]` that ran in play mode and quietly did not run in the
shipped game would be a worse trap than no declaration at all. The editor's
`PlayModeController.Contribute` makes the same call against the session, after every `IPlaySystems`
contribution has provided its services.

## What it is for

**So that something other than the game can run the game's frame.** Until this, a project's system
set existed only as the imperative body of its `Game.OnInitialise`. Nothing could read it: the
editor's Play mode could list a project's `ISystem` types by reflection and could not run one of
them, and said so in a warning. A declaration is a fact in the compiled assembly, so the editor now
reads it, resolves what it can, and runs it.

That works because the collection happens at compile time. The generator emits one
`[ModuleInitializer]` per assembly, and the editor's `ProjectAssemblies.Load` runs an assembly's
module constructor — so a project's declarations are in `GameSystemRegistry` the moment its code is
loaded, without running a line of the game's boot path.

**So that a missing service is a sentence rather than a symptom.** `AddDeclaredSystems` returns a
`FrameActivation`: what ran, and one readable line per declared system that did not.

⚠ **`Missing` is an answer, not an error, and it must be read out.** A system whose service nobody
registered is an ordinary situation — the editor has no `PhysicsScene` until a contribution makes
one — and a caller that drops the list on the floor turns an unregistered service into a bug that
presents as a script that stopped working. This is the same rule `PlayModeController.Refused` and
`Unsupported` follow.

## Using it

| Piece | What it is for |
|---|---|
| `[GameSystem]` | On a concrete class implementing `ISystem`, with one public constructor |
| `GameSystemRegistry.Declared` | Every declaration the loaded assemblies made, by type name |
| `GameSystemDeclaration.Requires` | The constructor's parameter types, in order |
| `GameSystemDeclaration.TryCreate` | Builds it, or names the first service that was not there |
| `loop.AddDeclaredSystems(services)` | Builds and adds all of them, and reports |
| `FrameActivation.Running` / `.Missing` | What is in the frame, and what is not and why |

`services` is any `IServiceProvider`. `ServiceRegistry` is one — that is what the host passes, so a
game only ever registers services — and so is `PlaySession`, which is how the editor resolves a
project's systems against what its `IPlaySystems` contributions provided. Calling
`AddDeclaredSystems` yourself is for a host that is neither: a test, or a bare `EngineLoop`.

⚠ **It is additive, and doing both is the one mistake.** A project may go on constructing its systems
by hand; a declared system and a hand-constructed one are the same thing to `SystemGraph`, which
sorts them by their phase and their edges either way. Nothing dedupes, so a system that is both
marked *and* passed to `loop.Add` runs twice — and, for the same reason, a game under
`VixenApplication` must not call `AddDeclaredSystems` from its own `OnInitialise`.

⚠ **A missing service is `13032`, a warning, not an exception.** One system that cannot be built must
not stop the game from starting — the trade the catalog, the shader bundle and the startup scene each
make — but it is said, at boot, while somebody is watching. `13031` is the other half: what did get
added.

⚠ **The engine's own systems do not carry the attribute.** A system in `Vixen.Physics` or
`Vixen.Rendering` is added by whatever built the service it runs against, because that owner is the
only thing that knows the service's lifetime — the app host in a game, an `IPlaySystems`
contribution in the editor. `[GameSystem]` is for the half a project owns.

The generator refuses three shapes and says why rather than emitting code that will not compile:
`VXS0404` for a marked type that is not an `ISystem`, `VXS0405` for one whose public constructors are
not exactly one, and `VXS0406` for an abstract or generic one.

⚠ **The generator has to reach your compilation, and before 2026-08-21 it did not travel with the
package.** `Vixen.Engine` now carries `Vixen.Engine.Generators` under `analyzers/dotnet/cs`, so a
`PackageReference` is enough. A project inside this repository references the generator project
itself with `OutputItemType="Analyzer"`, because analyzers do not flow through a `ProjectReference`.
Without it nothing is collected and nothing complains — which was also true of `[Component]` and of
scene behaviours, and is the reason to check the fix rather than assume it.

## Examples

The whole of what a system has to say, and the whole of what reading it back looks like:

```csharp compile
using Vixen.Core;
using Vixen.Core.Threading;
using Vixen.Ecs.Systems;
using Vixen.Engine.Frames;

// A service the project owns. Whoever creates it registers it; nothing auto-wires.
public sealed class Hunger {
    public float Rate { get; set; } = 0.1f;
}

// A system the project declares. Its constructor is what it needs; its attributes are when it runs.
[GameSystem]
[UpdateInGroup(SystemPhase.Update)]
public sealed class HungerSystem(Hunger hunger) : SystemBase {
    public Hunger Hunger { get; } = hunger;

    public override JobHandle Update(in SystemContext context, JobHandle dependency) => dependency;
}

public static class Boot {
    public static FrameActivation Start(EngineLoop loop) {
        var services = new ServiceRegistry();

        services.Add(new Hunger());

        // Every declared system whose services are all there, added; the rest named.
        return loop.AddDeclaredSystems(services);
    }
}
```

Leave the `services.Add(new Hunger())` out and nothing throws. `Running` is empty, and `Missing`
holds one line naming `HungerSystem` and the `Hunger` nobody provided.

To consider only some declarations — which is what the editor does not need but a host embedding two
projects would — pass a predicate:

```csharp no-compile="a fragment; `project` is the assembly whose frame is wanted"
var frame = loop.AddDeclaredSystems(services, declaration => declaration.SystemType.Assembly == project);
```

## See also

- [What a play session runs](../editor/play-mode-systems.md) — the other half of the seam: how the
  editor supplies the services these systems ask for.
- [Queries](../ecs/queries.md) — what a system spends its Update doing, a column at a time.
- [Booting an application](booting-an-application.md) — where `Services.Registry` comes from and what
  the host has already put in it.

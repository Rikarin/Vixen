---
title: The player delivery service
slug: editor/player-delivery
kind: guide
area: Editor
summary: What a plugin gets by asking for IPlayerDelivery — the build settings, the scene list and what would stop each scene shipping, why a build cannot start, and starting one — and why no device is part of it.
api: [T:Vixen.Editor.Core.IPlayerDelivery]
tags: [editor, build, deploy, plugins, services]
since: 0.1
status: preview
related: [editor/index, editor/writing-a-plugin, editor/content-library, editor/project-session]
---

## What it is

`IPlayerDelivery` is the editor's Delivery context as one service (doc 50 § D2, #1295): what a player
build would be, whether it can start, and starting one. The editor publishes it through
`PluginServices`, so a module gets the same object the Build menu, the Build Settings window and the
diagnostics module's Deploy all use:

| Member | What it is |
|---|---|
| `Settings` | The project's `PlayerBuildSettings` — the one copy every surface reads and writes. |
| `Target` | The target a build would use: the setting, or this machine when the setting is empty. |
| `Scenes()` | Every scene in the project, project-relative, in path order — what could be put in a build. |
| `SceneTrouble(path)` | What would stop a listed scene shipping, or `null`. |
| `Refusal()` | Why a player build cannot start, as a sentence, or `null` when it can. |
| `Build(launch, finished)` | Builds the player and optionally runs it. Returns whether a build started. |
| `SaveSettings()` | Writes the settings — what a menu tick and a field both do. |
| `Changed` | Raised when something that is not an edit to the settings may have changed `Refusal()`. |

## What it is for

Build Settings, Build and Run and the two radio submenus used to be private methods of the
application, closed over its fields, and the device panel reached them through an `IDeviceDeploy`
the application implemented by forwarding to the same privates. Now the editor's own `DeliveryModule`
registers the Build menu and the settings window through the `PluginContext` a third party is handed,
over this service — so Build and Run is a name for `Build(launch: true)` and not the place it lives,
and switching the module off in the plugin manager takes the Build menu's lines with it.

⚠ **One `Refusal()`, read by every surface.** The menu line's enablement, the window's two buttons
and the device panel's Deploy all ask it, because three surfaces asking three differently-worded
questions is how one of them ends up enabled when the other two are not. It refuses an unpublishable
target, a build while an import or another build is running, and a project with no `.csproj`.

⚠ **`Changed` is an event and not an answer.** `Refusal()` enumerates the project root, so it is
asked when something happens — a build starting or ending, an import finishing, a `.csproj`
arriving in a checkout — rather than once a frame. A panel that shows the answer listens and asks
again.

What it deliberately does **not** do:

- **Know about devices.** What Deploy means differs per kind of device — this machine is a publish and
  a launch, a phone is an install — and the kinds are the device manager's vocabulary. The build
  answers "can I build and run here"; the module that owns the devices maps its kinds onto that.
- **Return its failures.** `Build` returning `false` means it was refused before anything ran and the
  user has already been told why by a notification; `finished` is not called. A caller does not
  report twice.
- **Read the settings late.** Everything is resolved on the frame thread when `Build` is called and
  handed over whole, so editing the window while a build runs — usually for the next variant —
  cannot change the build already started.
- **Live in the asset pipeline.** It is in `Vixen.Editor.Core`, beside `PlayerBuildSettings`, and not
  beside `BuildStep`: nothing in it is a pipeline type, and the module that deploys references this
  assembly and not the import pipeline.

## Using it

A module that wants a build but can live without one asks with `TryGet`, the way the diagnostics
module does — a host that cannot build a player greys the action with a sentence rather than hiding
the panel:

```csharp no-compile="a fragment of a module's Activate; the command id and title are the plugin's"
public void Activate(PluginContext context) {
    context.Services.TryGet<IPlayerDelivery>(out var delivery);

    context.AddCommand(
        new EditorCommand("mine.ship", new StringId("mine.ship", "Ship It"), () => delivery?.Build(launch: false)) {
            Enablement = () => delivery?.Refusal() is null
        }
    );
}
```

A module that cannot exist without it — the Build menu is one — uses `Require`, and subscribes to
`Changed` through the context so an unload takes the handler back:

```csharp no-compile="a fragment of a module's Activate; Refresh is the module's own"
var delivery = context.Services.Require<IPlayerDelivery>();

context.With<IPlayerDelivery>(
    service => service.Changed += Refresh,
    service => service.Changed -= Refresh
);
```

## Examples

Listing what would stop the build's scenes shipping, before asking for a build:

```csharp no-compile="a fragment; `delivery` is the service from Require"
foreach (var scene in delivery.Settings.Scenes) {
    if (delivery.SceneTrouble(scene) is { } trouble) {
        Console.WriteLine($"{scene}: {trouble}");
    }
}
```

Building, and hearing when it ends however it ends:

```csharp no-compile="a fragment; `delivery` is the service, `building` the caller's own flag"
building = delivery.Build(launch: false, finished: () => building = false);

if (!building) {
    // Refused, and the user has been told why — nothing to add.
}
```

## See also

- [Writing a plugin](writing-a-plugin.md) — `Require`, `TryGet` and the registration scope; the
  diagnostics module's optional use of this service.
- [The content library](content-library.md) — the same shape for the project's assets.
- [The project session](project-session.md) — which project is open, and the rescan.

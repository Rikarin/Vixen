<!--
SPDX-FileCopyrightText: Copyright (c) Rikarin
SPDX-License-Identifier: Apache-2.0
-->

# Vixen

Vixen is a game engine and application framework for .NET 10 and C# 14. The same stack that ships a
game also ships desktop tools: Vixen's own editor is written in the engine, using the engine's own
UI framework.

> **Preview.** Vixen is at 0.1.0 and nothing is published to nuget.org yet. You build the packages
> from this repository and restore them from a local folder. The steps are below.

## What you get

- **An engine that boots on Windows, Linux and macOS**, plus the iOS Simulator and the Android
  emulator, with a WebAssembly head for the browser. Graphics run on Vulkan, OpenGL/GLES/WebGL2 or
  WebGPU, and a Null backend runs headless servers and tests.
- **A data-driven renderer.** It does forward+ physically based rendering with shadows, image-based
  lighting, reflection probes and post-processing. A frame is an asset you configure, not code you
  write.
- **An ECS** with Roslyn source generators in place of reflection, so games trim and compile ahead of
  time (NativeAOT).
- **`Vixen.Ui`**, a reactive UI framework with markup (`.vxml`) and styles (`.vcss`). It passes the
  full Yoga layout and Unicode conformance suites, and it works without the engine, so you can build
  ordinary desktop applications with it.
- **The editor.** It opens a project, imports assets, builds content, edits scenes and runs the game.
- **Raven**, a shader language that compiles to SPIR-V and GLSL.
- **Networking.** A server-authoritative stack with replication, prediction, lag compensation and
  voice chat. The `Gameplay` and `Live` libraries cover everything up to MMO-scale realms.
- **Asset pipeline and addressables.** Content builds into bundles you can update remotely.
- **Audio, physics, animation, navigation, video and XR.**

[`docs/overview.md`](docs/overview.md) lists every feature with its status, including what is not
finished yet.

## Requirements

- The .NET SDK named in [`global.json`](global.json) (10.0.301 or a later 10.0.3xx).
- A GPU with Vulkan or OpenGL drivers to open a window. macOS runs Vulkan through MoltenVK.

A desktop build needs no workloads and no native SDKs. Only the Android, iOS and Web heads need the
.NET workloads for their platforms.

## Getting started

1. **Clone the repository and build the packages:**

   ```bash
   git clone https://github.com/Rikarin/Vixen.git
   cd Vixen
   ./build.sh Pack        # build.cmd Pack on Windows; writes artifacts/packages/*.nupkg
   ```

2. **Point NuGet at those packages.** Put a `nuget.config` next to your project that maps `Vixen.*`
   to `artifacts/packages` and everything else to nuget.org.
   [Getting started](docs/guide/engine/getting-started.md) has the file ready to copy.

3. **Install the templates and create a game:**

   ```bash
   dotnet new install artifacts/packages/Vixen.Templates.<version>.nupkg
   dotnet new vixen-game -n MyGame -o MyGame
   cd MyGame
   dotnet run
   ```

   A 1280 × 720 window opens and draws your game's frame.

There are six templates:

| Template | What it creates |
|---|---|
| `vixen-game` | A game, with an `Assets/` folder, a project the editor opens, and a dedicated-server variant |
| `vixen-app` | A desktop application on `Vixen.Ui` alone, with no engine |
| `vixen-lib` | A library |
| `vixen-tool` | A command-line tool |
| `vixen-plugin` | An editor plugin |
| `vixen-mmo` | A multi-project MMO starting point, built from the `Gameplay` and `Live` libraries |

Next, read [Building a game and a dedicated server](docs/manual/building-a-game-and-a-server.md).

## The editor

Run it from a clone of the repository:

```bash
dotnet run --project Editor/Vixen.Editor.Host
```

## Samples

Each sample in [`Samples/`](Samples/) is a runnable project with a README that explains what it shows.

| Sample | What it shows |
|---|---|
| [`01-HelloTriangle`](Samples/01-HelloTriangle/) | The smallest program that opens a window and draws, with Android and iOS versions |
| [`02-HelloUi`](Samples/02-HelloUi/) | A small editor shell: menus, docking panels, a tree, a property grid and the control gallery |
| [`03-PbrShowcase`](Samples/03-PbrShowcase/) | Materials across the metallic and roughness range, terrain, and blend-shaped heads |
| [`04-EcsStressTest`](Samples/04-EcsStressTest/) | The ECS under load, with its performance targets measured |
| [`07-AddressablesRemote`](Samples/07-AddressablesRemote/) | A remote content update: build, serve, download, change one asset, download again |
| [`08-Multiplayer`](Samples/08-Multiplayer/) | Eight players and one authoritative server |
| [`09-NetworkSoak`](Samples/09-NetworkSoak/) | A hundred connections and five thousand entities, held to their budgets |
| [`10-VoiceChat`](Samples/10-VoiceChat/) | Two players talking over UDP: capture, Opus, jitter buffer and mixing |
| [`11-VideoPlayback`](Samples/11-VideoPlayback/) | Video decoded and drawn on screen |
| [`12-VirtualGeometry`](Samples/12-VirtualGeometry/) | A mesh streamed and culled cluster by cluster |
| [`13-ThirdPersonShooter`](Samples/13-ThirdPersonShooter/) | A complete project in the shape `vixen new` creates, which the editor opens |
| [`14-Mmo`](Samples/14-Mmo/) | An MMO vertical slice: maps, classes, a dungeon, a battleground and guilds |
| [`15-AiVillage`](Samples/15-AiVillage/) | Three AI agents, one per planner type, reacting to an intruder |

Run one with `dotnet run --project Samples/01-HelloTriangle`.

## Documentation

- **[The guide](docs/guide/)**: how to use each feature, grouped by area. Start with
  [Getting started](docs/guide/engine/getting-started.md), or with
  [Coming from Unity](docs/guide/engine/unity-migration.md) if that is where you are coming from.
- **[The manual](docs/manual/)**: shipping a game and a server, diagnostic codes, log events, and
  third-party licences.
- **[What works today](docs/overview.md)**: the status of every feature.
- **[The design record](docs/plan/README.md)**: why Vixen is built the way it is.

Each library also has a `README.md` beside its code that explains how it works and what it
deliberately does not do.

## Building from source

```bash
./build.sh Compile      # or build.cmd on Windows
./build.sh Test
```

`./build.sh` is the one entry point, shared by CI and developers. There are forty targets:
`Clean Restore RestoreNativeDeps Compile CompileRelease Test Coverage Pack CheckPackages CheckTemplates
GoldenImages Benchmark CheckBenchmarks CheckArchitecture CheckApi CheckFormat CheckWhitespace
CheckAttribution CheckStrings CheckShaders CheckDocComments CheckDocs CheckDocsCoverage CheckPathCase Docs
CheckAot CheckAotIos CompileMobile CompileWeb PublishWeb BrowserSmoke PublishEditor Release
ContentBytes RemeshBytes SampleFrame AffectedProjects AffectedTests TestOrder PruneWorktrees`.
[`docs/plan/12`](docs/plan/12-build-ci-and-testing.md) says what each one does and which are gates.

To change the engine itself, read [CONTRIBUTING.md](CONTRIBUTING.md) first. It covers the repository
layout, the test setup and the rules the build enforces.

## Licence

Apache-2.0. See [`LICENSE`](LICENSE) and [`NOTICE`](NOTICE).

⚠ **Not every dependency is permissive.** `Silk.NET.OpenAL.Soft.Native` is licensed
**LGPL-2.0-or-later** and ships `libopenal` in every build that has sound. It is dynamically linked,
so its terms can be met, but they are not met for you today, and four further packages ship no
licence statement at all. Read [`docs/manual/third-party.md`](docs/manual/third-party.md) before you
distribute a binary.

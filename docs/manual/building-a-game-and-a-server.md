# Building a game and a dedicated server

One project, two heads, selected by the **variant**. This is
[doc 17](../plan/17-app-heads-and-shipping.md) Q5a: shared gameplay code is the point, and two
projects invite drift.

Every command below was run against the tree it documents. Where something is not built yet it says
so rather than describing what it will look like.

## Start a project

```bash
vixen new game Skirmish
```

That writes a `.csproj` that says `<Project Sdk="Vixen.Sdk/x.y.z">`, a `Program.cs` that is one
line, a `Game` subclass, an `Assets/` folder with a default group and the project's frame in it,
and a `.gitignore`. The SDK version comes from the tool's own assembly, so a scaffolded project
asks for the SDK that made it.

The frame is `Assets/Frame.vxcompositor` — seven lines, one `!StandardFrame` node, knobs for
shadows, GI, antialiasing and exposure — and the `Game` subclass registers the factory that makes
it bind. The commented-out `Assets/RenderQuality.vxpreset` beside it is where a tier's numbers get
overridden the day one needs to be. [Choosing a frame](../guide/rendering/choosing-a-frame.md) is
the story of when to stop turning knobs and start authoring.

The project references `Vixen.App` for the host and `Vixen.Rendering.PostFx` for the frame's
nodes, and nothing else. The SDK wires the build and deliberately adds no engine references; what
you link against is your decision.

> **Until the packages are on nuget.org** you need a `nuget.config` next to the project pointing at a
> feed that has them — `./build.sh Pack` writes them to `artifacts/packages`.

## Run it while you work

```bash
dotnet run
```

The SDK imports the assets, builds the content, and copies it next to the binary, so `dotnet run` is
the whole loop. Nothing else has to be run by hand.

```bash
vixen run                     # the same thing through the tool, with the host target picked for you
vixen run -- --vixen-frames 5 # anything after -- goes to the game
```

## Build the game

```bash
vixen build --target MacOS --variant Release --output Publish/Client
```

`--target` is the platform: `Windows`, `Linux`, `MacOS`, `Android`, `iOS`. `--variant` is one of doc
17's five. They are **orthogonal** — any variant builds for any target — which is why the variant is
not the compiler configuration.

What the command does, in order: content build, then `dotnet publish`. That ordering is the whole
reason it is a command rather than a note in a README.

**The same build is on the editor's Build menu.** `Build ▸ Build Settings…` is the window — target,
configuration, output path and the scenes that ship — and `Build and Run` (`Ctrl+B`) runs it, with
`dotnet publish`'s own output going into the Console panel. It is not a second build system: the
editor and this command make the same calls in the same order, so a project builds the same way
whichever one asked.

One difference, and it is stated rather than left to be found: **the editor does not compile the
ahead-of-time shader bundle.** That step links Raven's compiler, which the editor deliberately does
not carry — so a project with a `ProjectSettings/Shaders.effects.json` gets a line in the build log
saying so, and `vixen build` is what produces a player carrying one.

## Build the dedicated server

The same project, one word different:

```bash
vixen build --target Linux --variant Server --output Publish/Server
```

Run it and it says what it is:

```
[info] Vixen.App: Vixen Server on Headless, 9 workers.
[info] Vixen.App: Content mounted from /app/Content: 0 addresses.
```

**No flag was passed at run time.** The variant is compiled into the binary as a
`[BuildVariant(...)]` attribute on the entry assembly, the host reads it at boot, and a Server build
selects the headless platform on its own. That is the whole of the difference at boot — every
subsystem then takes the path it already had to have for the browser and the phone.

A server build is also the one variant that will not silently be quiet: doc 17 gives it "full
logging", so the console provider is on.

### What a server head owes you today

- **Headless platform, no window, no GPU.** Working, and `Vixen.Graphics.Null` is a shipping backend
  rather than a test one.
- **No networking.** [Doc 16](../plan/16-networking.md) is Phase 9. A server today is a game loop
  with no window; there is nothing for a client to connect to.
- **No metrics endpoint.** Doc 17's Server row names one. Not built.
- **Content is stripped by group, not by type.** Doc 17 says a server ships bundles "with no
  textures, audio or shaders". The content build knows the variant now: `vixen content build
  --variant Server`, which the SDK passes from `VixenVariant`, leaves out every addressable group
  whose `.vxgroup` sets `includeInServerBuild: false` and compiles no shader bundle at all. What it
  will not do is work out which assets those are on its own — a terrain heightmap is a texture and a
  dedicated server bakes its collision out of one, so a build that stripped by asset type would take
  the ground out from under it silently. Mark the groups; the build then refuses, by name, if
  something it still ships depends on something it left out.
- ~~**No `Dockerfile`.**~~ Q5c's `Dockerfile` ships in `vixen-game`, so `vixen new game` and
  `dotnet new vixen-game` both write one: multi-stage, chiselled base, non-root, and it builds the
  Server variant because a client in a container has no display. What it produces is still a server
  with nothing to connect to it — see the networking line above.

## The five variants

| Variant | Window | Assertions | Console | What it is for |
|---|---|---|---|---|
| `Editor` | yes | on | yes | the editor itself |
| `Debug` | yes | on | yes | daily development — the default |
| `Development` | yes | on | yes | QA and playtests: optimised, and still diagnosable |
| `Release` | yes | off | **no** | shipping |
| `Server` | no | on | yes | a dedicated server |

`Development` is the row engines usually omit and teams discover they need: an optimised build that
still has its diagnostics, because without one "it only reproduces in release" is undiagnosable.

`Release` is the only variant with no console. A shipped game has no terminal to write to and would
pay for every string it formatted; its log lives in the always-on ring buffer that the crash reporter
uploads. To see a shipped build talk, run it as `Development` — which is the same binary:

```bash
./Skirmish --vixen-variant Development
```

## Content

The SDK copies the content build into `Content/` beside the binary, and the host mounts it from
`/app/Content` at boot — through the virtual file system rather than a path, so an APK's assets and
an iOS bundle answer the same call a desktop directory does.

An application with no content starts anyway and says why. That is ordinary: a batch tool and a
smoke test both have nothing to load.

To point a **built** binary at content it did not ship with — the one case doc 17 Q5b allows, so that
a bug which only reproduces in a shipping configuration can be poked at:

```bash
./Skirmish --vixen-loose-content ../Build/MacOS
```

It weakens "release reads only bundles", so it is not allowed to be quiet: the host warns at startup
and **every sixty seconds** after. Once is not visible in a build that has been running overnight.

## Serving content to a device

```bash
vixen content build --target Android
vixen content serve --any
```

`--any` binds every interface, which is what a phone on the same network needs and what a laptop in a
café does not. It is a development server: no TLS, no authentication, no access control.

## Useful arguments

Every host argument starts `--vixen-`; anything else is left for the game. `--vixen-x=y` and
`--vixen-x y` both work.

| Argument | Effect |
|---|---|
| `--vixen-variant <name>` | Overrides the compiled-in variant |
| `--vixen-frames <n>` | Runs *n* frames and exits — what makes any head CI-runnable |
| `--vixen-fixed-step <s>` | Tells every frame it took *s* seconds, whatever the clock says, so frame *n* is a fixed instant. Implied by `--vixen-capture`; `0` puts the wall clock back |
| `--vixen-headless` | No display server, whatever the variant says |
| `--vixen-offscreen` | Open a real GPU device with no window and write no picture — what `--vixen-capture` implies, for a run that wants counters rather than a photograph. ⚠ Either of them refuses the `null` backend rather than falling through to it |
| `--vixen-workers <n>` | Job-system workers. `0` is supported and tested |
| `--vixen-frame-limit <n>` | Frames per second, `0` for uncapped |
| `--vixen-log-level <level>` | The lowest level kept and printed |
| `--vixen-log-file <dir>` | Also write rolling JSON-line log files into a directory. Off unless asked for |
| `--vixen-loose-content <dir>` | See above |

## What `vixen build` does not do

It stops at what `dotnet publish` produces. Nothing is signed, notarised, or packaged into a DMG, an
IPA or an AAB — doc 17's packaging table is Nuke's job and needs credentials. Android and iOS
publish through their own SDKs and need those workloads installed.

Licensed under Apache-2.0.

<!--
SPDX-FileCopyrightText: Copyright (c) Rikarin
SPDX-License-Identifier: Apache-2.0
-->

# Vixen.Ui.Desktop.HotReload

Hot reload for a `Vixen.Ui.Desktop` application. **Referencing it is the whole of the setup.**

```xml
<PackageReference Include="Vixen.Ui.Desktop.HotReload"
                  Version="..."
                  Condition="'$(Configuration)' == 'Debug'" />
```

```bash
dotnet watch
```

Now edit a `.vxml` and the running window updates; edit a `.vcss` and it repaints without even a
rebuild. There is no call to make, no options to set and no `#if` in your `Main`.

## The two channels are two different mechanisms

| Save a… | What happens | What survives |
|---|---|---|
| `.vcss` | A file watcher reloads the sheet. `dotnet watch` does not even notice. | Everything — element identity is untouched, so focus, scroll offsets and a docking arrangement all stay put. |
| `.vxml` | `dotnet watch` recompiles it into a new `Build`, the runtime patches the assembly, and `Build` re-runs on the same component objects. | The components and their fields, so their signals. **Not** the elements: two `Build` bodies are two different programs. Focus is put back by path. |

Measured on the sample: a `.vxml` edit reloads in about **750 ms** with no restart.

## Why it is a separate assembly

`Vixen.Ui.HotReload` is under `Core/` and may not reference a window — doc 00's layer rule, which
`CheckArchitecture` enforces. `Vixen.Ui.Desktop` is shipped and AOT-clean and must not reference a
development tool. **Neither restriction is negotiable and neither side can do this**; a third
assembly can, because it is under `Platform/` and is itself development-only.

What it does is fill two hooks — `UiDevelopment.Mount` and `UiDevelopment.Started` — from a
`[ModuleInitializer]`. `UiApplication` reads them if they are set and builds the ordinary way if they
are not.

⚠ **A module initializer runs on first *access*, not on load**, so `UiApplication` asks for this
assembly by name and calls `RunModuleConstructor`. Without that the DLL sits in the output directory
fully loaded and completely inert, which is exactly what the first version did — a reference that
looked wired and did nothing.

## Two things worth knowing

⚠ **Your `Content` factory is what a re-created component is rebuilt from.** An edit the runtime
cannot patch makes the host construct a replacement, and `() => new Shell { Model = model }` is the
only thing that knows the shell takes a model. Handed the instance alone a host falls back to the
parameterless constructor — and the panel comes up bound to a model nothing else holds, with the
reload still reporting success. Write `Content`, not `Mount`, and this is handled.

⚠ **`UiDevelopment` is process-wide**, so tests that construct two applications while one has taken
over mounting will see each other's components. `Vixen.Ui.Desktop.Tests` serialises the classes that
touch it, and the remark on `SerialUiDevelopment` says what it looked like when they did not.

## What it does not do

**Tokens still need a rebuild.** `@theme` in a `vixen.ui.vcss` is compiled into the generated utility
sheet at build time, and that sheet is a `const string` — which hot reload cannot patch into existing
callers. Editing a colour token restarts.

**A deleted rule keeps applying until the next build.** `HotReloadWatcher.Load` binds a path to a
sheet the document already holds *when the two texts match*, and a sheet the build concatenated into a
generated one matches nothing — so a save layers on top rather than replacing. Changing a rule works
either way. The line this prints at start-up says which of the two you have, per sheet.

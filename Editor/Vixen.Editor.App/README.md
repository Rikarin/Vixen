# Vixen.Editor.App

The editor's executable: a platform, a window, a device, and a frame loop over a `UiDocument`.

```bash
dotnet run --project Editor/Vixen.Editor.App -- --frames 5
```

`--frames N` runs exactly N frames and exits, which is how CI proves the whole stack starts,
presents and stops without a validation error or a hang — the flag `Samples/01` introduced and for
the same reason. With no `--frames` it runs until the window closes.

## Three files, three jobs

| | |
|---|---|
| `Program.cs` | the platform and the window |
| `EditorHost.cs` | the device and the four steps of a frame |
| `EditorApplication.cs` | which panels exist, which layouts, and what persists |

The third is the one a game team would fork. The first two are the same hundred lines
`Samples/02-HelloUi` has, and the loop is four steps worth naming: pump the platform's events into
the document, run the layout and draw passes, turn the draw list into geometry, record that geometry
into a frame. Only the last knows what a GPU is — which is why `--frames` means something on a
machine with no Vulkan at all.

## Not Vixen.App, and the reason is the frame loop

`Tools/Vixen.App` exists to run a game: it builds an ECS world, a fixed-step accumulator and a
systems graph, and it is the right answer for a game and for the editor's play mode. The editor's own
loop is an interface. It has no world, and the moment it has one it has it because a viewport is
hosting a game rather than because the editor is one. Doc 17's "the app is the executable" is the
same argument from the other side.

## What persists, and when

The user's — not the project's, which is `ProjectSettings/` and `Vixen.Editor.Core`'s business:

| File | What |
|---|---|
| `current.vxlayout` | the arrangement the editor was left in |
| `<name>.vxlayout` | arrangements the user saved by name |
| `keybindings.yaml` | only the bindings that differ from the defaults |
| `theme.yaml` | token overrides, if the user wrote any |

They live in the platform's data directory — `%APPDATA%`, `~/Library/Application Support`,
`$XDG_DATA_HOME` — because `IFileSystemHost` is what knows which, and the editor should not have a
second opinion.

⚠ **Written on the way down, not on every change.** A splitter drag raises `LayoutChanged` per
mouse-move, and an editor that wrote a file per frame of a drag would have the noisiest thing on the
disk be its window layout.

⚠ **Persisting happens before the document is disposed**, which is why it is at the end of `Run`
rather than in `Dispose`: reading the arrangement out of a disposed docking host would write an empty
layout over the one the user spent the afternoon arranging.

## Load order, which is not arbitrary

1. Register panels, layouts and commands.
2. Load the keymap — **after** the commands that own its defaults, or every override in the file
   lands on a command with no default and the file rewrites itself with the whole map in it.
3. Load the theme tokens.
4. Apply the saved layout — **after** the panels are registered, or a saved arrangement names panels
   the workspace cannot build.

A first run has none of the three files and opens on the Default preset in dark.

## The panels are placeholders and are meant to read as such

`Vixen.Editor.SceneView`, `.Inspector`, `.NodeGraph`, `.Profiler` and `.Debugger` are separate
assemblies in doc 11's tree and none of them exists yet. What is here is a hierarchy and a project
browser built from `TreeView`, an inspector that is a bare `PropertyGrid`, a console that is a line
of text, and a scene panel that says what will replace it. They exist so the shell is exercised by
something real rather than by five empty boxes, and each becomes a one-line change when its assembly
lands — because a panel is an id and a factory.

## Known gaps

- **No plugin loading.** Doc 11 puts it here, and `Vixen.Editor.Plugin` — the contract, the manifest,
  the `AssemblyLoadContext` — does not exist yet. It is the reason this project is not NativeAOT, and
  the `PublishAot` property says so already.
- **No project is opened.** `Vixen.Editor.Core` is referenced and not yet used: `file.open-project`
  needs a file dialog, which is `Vixen.Platform`'s and not built.
- **It redraws every frame.** Redrawing only on change is the right end state and is not free — every
  animation, toast expiry and task progress has to say so, and one that forgets leaves a progress bar
  frozen at forty per cent.
- **The four SPIR-V modules are committed here and in `Samples/02-HelloUi`**, byte for byte. They
  belong in one place once Raven's `Ui/*.rvn` path is wired; until then a caller hands the renderer
  whatever it has.
- **`PlatformInput.cs` is the second copy of the same file.** The sample's copy says a
  `Vixen.Platform.Ui` assembly is where it goes "when the editor becomes the second one". It now has.

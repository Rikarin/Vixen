# Vixen.Editor.Host

The editor's executable. A platform, a window, a device, and a frame loop over a `UiDocument` —
`Vixen.Editor.App` is the editor and this is the head that runs it.

```bash
dotnet run --project Editor/Vixen.Editor.Host
```

| Flag | |
|---|---|
| `--project PATH` | which project to open. With none, the last one this user had open, then a scratch one under their data directory. |
| `--frames N` | run exactly N frames and exit. What CI uses to prove the stack starts, presents and stops. |
| `--run ID` | execute one editor command on the first frame, then forget it. |
| `--hot-reload DIR` | **development only.** Reload the `.vcss` files under `DIR` as they are saved. |

## Editing the editor while it is running

Three channels, because three different things can change. **Two of them work today and one of them
half does**, and the table says which is which rather than describing the design.

| | What you change | Does it reach a running editor? |
|---|---|---|
| **Markup** | a `.vxml` panel | **Yes**, under `dotnet watch`, for a panel mounted through the reload host — see below |
| **Styles** | a `.vcss` under `--hot-reload DIR` | **Yes** |
| **Styles** | `EditorTheme.cs` and its four neighbours | **No.** They are C# `const` strings, and a `const` edit is a rude edit |
| **Component** | a type, incompatibly | `dotnet watch` restarts the process. `HotReloadHost.Replace` exists and nothing in the editor calls it |

### Markup: the command

```bash
dotnet watch run --project Editor/Vixen.Editor.Host
```

That is the whole of it. Edit a `.vxml`, save, and the panel is different about a second later
without the window closing. `dotnet watch` loads the whole project graph — twenty-five projects from
this head — so a `.vxml` in `Vixen.Editor.Terrain` or `Vixen.Editor.AssetEditors` is watched too, not
only this project's.

**What actually happens**, because it is worth knowing which half is whose:

1. `dotnet watch` sees the `.vxml`. It is an `AdditionalFiles` item — `Core/Vixen.Ui/build/Vixen.Ui.targets`
   globs it — and watch picks additional files up without anything in the project saying so.
2. `Vixen.Ui.Markup.Generators` recompiles it into a different `Build` method.
3. Roslyn calls it an ordinary method-body edit and the runtime applies it in place.
4. The runtime calls `Vixen.Ui.HotReload.MetadataUpdate.UpdateApplication`, which is registered as
   the assembly's `[MetadataUpdateHandler]`.
5. That reloads every live `HotReloadHost`, which re-runs `Build` on the components it mounted.
6. `MarkupInspector` re-binds, because a rebuild throws the elements away and a panel that did not
   re-bind would come back showing rows that edit nothing.

Measured on this checkout, on a probe driving exactly that path: **28–378 ms from save to rebuilt
tree**, over three consecutive edits, with the process never restarting. Adding an element and
adding a field to `@code` were both applied in place.

And measured on this editor, which is the only measurement that settles it. With the window open on
the Undo History panel, adding a `<TextBlock>` to `UndoHistory.vxml` and saving put the new line on
screen in **789 ms**, with the window never closing and every other panel where it was.

### ⚠ Which panels reload, and which do not

**Only a component mounted through `HotReloadHost.Mount` is rebuilt**, and in this editor the only
thing that calls it is `MarkupInspector.Of<T>` — plus the undo history, which was moved onto it for
this reason.

| Panel | |
|---|---|
| `Vixen.Editor.Terrain/TerrainBrushInspector.vxml` | reloads |
| `Vixen.Editor.AssetEditors/Frame/StandardFrameInspector.vxml` | reloads |
| `Vixen.Editor.AssetEditors/Frame/LookInspector.vxml` | reloads |
| `Vixen.Editor.App/UndoHistory.vxml` | reloads |
| `Vixen.Editor.Ui/Tasks/TaskCenter.vxml` | **does not** — `EditorShell` builds it directly, and the shell is constructed before the host exists |

A panel built with `BuildContext.Build<T>` rather than `host.Mount<T>` keeps whatever `Build` body it
was constructed with until the editor restarts. That is a one-line difference at the call site and
it is the whole of whether a panel is reloadable.

### Styles: what `--hot-reload DIR` is for

```bash
mkdir -p ~/vixen-dev-styles
dotnet watch run --project Editor/Vixen.Editor.Host -- --hot-reload ~/vixen-dev-styles
```

Every `.vcss` under `DIR`, recursively, is loaded at **`Author` origin after** the five sheets the
editor ships — which are `UserAgent`, and lose to `Author` for every normal declaration. So a rule
written there beats the shipped one without having to out-specify it, and saving the file changes the
editor without rebuilding a single element: the rule set is replaced and the cascade runs again, so
every panel keeps its focus, its scroll offset and its animation state.

**There is a `.vcss` in the editor now, and until doc 43's `@theme` work there was not one anywhere
in the tree.** `Editor/Vixen.Editor.Ui/Theming/EditorTheme.vcss` is a real file on disk, embedded by
the glob in `Core/Vixen.Ui/build/Vixen.Ui.targets`; `EditorTheme.Css` reads it back out of the
assembly. So this channel finally has something to point at:

```bash
dotnet watch run --project Editor/Vixen.Editor.Host -- --hot-reload Editor/Vixen.Editor.Ui/Theming
```

⚠ **What that does and does not give you, stated exactly, because the difference is a cascade
question rather than a plumbing one.** The watcher loads what it finds at `Author` origin, and the
copy already inside the assembly is at `UserAgent` — so an edit to `EditorTheme.vcss` *layers over*
the shipped one rather than replacing it. Every normal declaration in the edited copy wins, which is
what you want; a declaration you **delete** does not disappear, because the `UserAgent` copy still
has it. Iterating on values is live and true; iterating on which rules exist is not. Replacing the
shipped sheet in place needs `UiDocument.Replace` against the index `EditorTheme.Install` returns,
which nothing calls yet.

⚠ **What is fixed for good is the way back.** It used to be "paste the result into a `const string`",
and the constant was not a way round the restart either: editing CSS inside `const string Sheet =
"""…"""` was measured to make `dotnet watch` report *"Restart is needed to apply the changes"* and
kill the process, because a const's value is baked into metadata and into every use site. The file
you edit is now the source, so there is nothing to paste.

⚠ **One file in that directory is not for the cascade.** `Theming/vixen.ui.vcss` is the `@theme`
block the utility generator reads at build time, and the watcher's glob is `*.vcss` recursively — so
pointing at the folder sweeps it up, `@theme` reaches ExCSS as an at-rule nothing knows, and
`StyleSheetLoader` drops it with a diagnostic. Harmless and noisy. Point at a copy of
`EditorTheme.vcss` in a scratch directory to avoid it.

⚠ **There is now more than one file to point at, which there was not when this was written.**
`ControlTheme`, `AdvancedTheme`, `AssetEditorTheme`, `InspectorTheme`, `BrowserTheme`,
`ProfilerTheme`, `DebuggerTheme`, `NodeGraphTheme` and `WorldTheme` are all `.vcss` files beside
their loaders now, so the watcher's `*.vcss` glob finds ten sheets across nine projects rather than
the one it used to. **The `Author`/`UserAgent` mismatch above applies to every one of them
identically** — extraction gave the watcher more to see and changed nothing about what it does with
what it sees.

⚠ **The directory is named and has no default.** A watcher over a folder with nothing in it is a
channel that looks wired and does nothing; a mistyped path says so in the console rather than looking
like a channel that broke.

### What CI does, which is none of this

`--frames N` opens no watcher. `--hot-reload` is off unless it is passed and CI does not pass it, so
the flag adds one null check per frame to that path and nothing else. A `FileSystemWatcher` is a
platform handle and a pool callback bought for a channel a run with nobody at the keyboard cannot
use — and it is the kind of handle that keeps a process from shutting down, in the one run that has
to shut down cleanly.

## Threading

A `FileSystemWatcher` callback is on a pool thread and the element tree has no lock. Changes are
coalesced by path where they arrive and applied in `EditorApplication.Update`, which the frame loop
calls — the same shape the asset watcher already follows, and for the same reason: a reload replaces
the rule set and re-runs the cascade over every element in the document, which from the pool would be
rewriting the tree underneath the layout pass.

⚠ **Editors write files more than once.** Save-to-temp-then-rename, a truncate followed by a write, a
tool that touches the timestamp afterwards — one save can raise three events, and three reloads is
two replays of every sheet nobody asked for. The coalescing is a set of paths, and
`HotReloadWatcherTests` drives the notices directly rather than by writing a file three times: what
the operating system chooses to deliver is not the class's contract, and a machine that coalesced at
the kernel would pass a filesystem-driven version of that test however broken the set was.

## Owed

- **`TaskCenter` is not reloadable**, because `EditorShell` builds it and the shell is constructed
  before `EditorApplication` makes the host. Closing it means the shell taking a host, or the
  application re-mounting the popover's contents after construction.
- **Replacing rather than layering.** The sheets are all files now, so the remaining gap is entirely
  the origin mismatch: the watcher loads at `Author`, the shipped copy sits at `UserAgent`, and a
  deleted rule therefore does not disappear. Closing it means `UiDocument.Replace` against the index
  each `Install` returns, which nothing calls yet. A published editor still has no source tree to
  watch either way, so the reload has to be pointed at one.
- **`node-search-port:empty` in `NodeGraphTheme` is a rule that never matches** — the selector
  compiler does not implement `:empty`, and says so. Harmless in itself and it is why the style
  channel silently did nothing until `HotReloadHost.ReloadStyles` learned to compare against the
  diagnostics the document already had.

Licensed under Apache-2.0.

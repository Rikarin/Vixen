# Vixen.Ui.HotReload

Editing a running interface. Three channels, because three different things can change and they
cost three different amounts.

Dev-only, and the one assembly under `Core/` that says so in its project file: reloading reads a
component's fields by name and exists to serve `dotnet watch`, and a published trimmed build has no
metadata updates to receive.

## The three channels

| | What changed | What is rebuilt | What is lost |
|---|---|---|---|
| **Styles** | a `.vcss` | nothing | nothing |
| **Markup** | a `.vxml`, once recompiled | every element of the affected components | element identity; the focus is put back by path |
| **Component** | a type, incompatibly | the component object too | everything not marked `[HotReloadState]` |

### Styles

The channel that is genuinely free, and the one a designer uses all day. The rule set is replaced
and the cascade runs again; every element keeps its identity and therefore its focus, its scroll
offset and its animation state.

**A reload replaces, it does not overlay.** Rules are appended and never removed — an index, a layer
order and a declaration arena all assume it — so the engine keeps the text of every sheet and
rebuilds from them. That is the difference between a reload and an overlay: replaying the sheets is
what makes a *deleted* rule stop applying, where re-adding the new text on top leaves the old one
underneath, still winning wherever the new one says nothing.

**A stylesheet that does not load puts the previous one back.** Half a stylesheet is worse than the
old one — a rule somebody is midway through typing drops the colour off everything it used to
match — and the previous text is right there.

⚠ The diagnostics come from *two* lists. The loader reports what it could not use and the selector
compiler reports separately; reading only the loader's misses exactly the mistakes a person makes
while typing a selector, which is most of them. Found by a test that expected a rollback and did not
get one.

⚠ **And what rolls a sheet back is a diagnostic the save *introduced*, not one the document
happens to have.** A reload replays every sheet — that is what makes a deleted rule stop applying —
so the diagnostics afterwards belong to the whole document, and one unsupported selector anywhere in
any sheet rolls back every save of every other sheet, for ever. This is not hypothetical and it is
not subtle to hit: wiring the channel to the editor found it immediately, because the editor's own
chrome contains a `node-search-port:empty` that the compiler does not implement. The file was saved,
the event arrived, the reload ran, and it silently put the old text straight back — a channel that
was wired, correct at every step, and did nothing. The fix is a multiset difference against the
diagnostics that were already there.

### Markup

`Build` runs again on the same component objects. Their fields survive because the objects do — and
their signals are most of what "state was preserved" means in practice.

**The elements do not survive, and cannot.** Two `Build` bodies are two different programs. There is
no identity an element from the first shares with one from the second beyond its position, and
reconciling on position alone would move state onto whatever happened to be in the same slot. The
focus is put back by path from the component root, and the report says whether that worked rather
than assuming it did.

⚠ **A `Build` that throws leaves the component empty.** Clear-then-build has no snapshot to fall
back to. It is reported rather than swallowed, and said plainly rather than described as "the
previous UI is kept" — which is what the plan promised and is only true of the *file* case, where a
broken `.vxml` does not compile so no update ever arrives.

### Component replacement

For an instance the update left behind. The new one starts from its own field initialisers, and
`[HotReloadState]` says what to carry — by name, because two instances of two versions of a type
share nothing else. A value whose type no longer fits is left behind rather than thrown at the
field: the whole point of a reload is that the type changed. Reached by hand and, since the metadata
handler stopped dropping the runtime's type list, by the runtime — see below.

## What this does not do

**It does not deliver the new code.** A changed `.vxml` becomes a different `Build` only after
something has recompiled it. That is `dotnet watch`'s job plus the source generator's —
`Vixen.Ui.Markup.Generators`, which was owed when this was written and now exists. The boundary the
sentence names is unchanged: this assembly reloads, and something else compiles.

The runtime's own callback is wired: `MetadataUpdate` is registered as a `MetadataUpdateHandler` and
reloads every host still alive. Hosts are held weakly — a static list of every document ever created
is a leak with a development-only cause and a production-shaped consequence.

## The watcher

Watches `.vcss` only, for the reason above: watching for a file it could not act on would put a
spinner on an operation that never happens.

⚠ **A file the document already holds is adopted rather than loaded again, and that is what makes
the watcher's reload a reload.** Every stylesheet in this repository is a `.vcss` embedded from the
file a developer edits, and installed at `UserAgent`. Loading the same text again puts an `Author`
copy on top of it — which wins wherever it says something and says nothing where a rule was
*deleted*, so the copy underneath goes on applying it. Values iterate live and the set of rules does
not, which is the exact shape of a channel that looks wired and half works. `Load` compares the
file's text against the text of every sheet the engine holds — the text is the only thing the two
copies share, because a sheet is loaded from a string and remembers no path — and binds the path to
the sheet that is already there. A save then replaces it, at its own origin, and a deleted rule
disappears. A file that matches nothing is still an overlay on top, because an overlay is what a
scratch directory of overrides is; `Replaces` says which of the two a path got.

⚠ **Editors write files more than once.** Save-to-temp-then-rename, a truncate followed by a write,
a tool that touches the timestamp afterwards — one save can raise three events. Changes are
coalesced by path and applied on `Poll()`, which the frame loop calls; that also puts the reload on
the caller's thread, which matters because the element tree has no lock and a `FileSystemWatcher`
callback is on a pool thread.

⚠ **The coalescing is tested through a seam and not through the filesystem.** `Notify` is internal
and the tests drive it directly, because what the operating system chooses to deliver is not this
class's contract: a machine that coalesced three writes at the kernel would pass a filesystem-driven
version of the test however broken the set was. One test does go through `FileSystemWatcher`, for
the wiring — the filter, the notify flags, the three events subscribed to — which is exactly the part
that is wrong when a save does nothing at all.

## Who uses it

`Editor/Vixen.Editor.Host`, behind `--hot-reload DIR`. ⚠ **It used to be the only caller and is not
any more**, which is the direction that matters: `Platform/Vixen.Ui.Desktop.HotReload` fills
`UiDevelopment.Mount` and `UiDevelopment.Started` from a `[ModuleInitializer]`, so an ordinary
`UiApplication.Run` mounts through a `HotReloadHost` and polls a `HotReloadWatcher` on its frame
event **for a project reference and no bootstrap at all** — which is how `Samples/02-HelloUi` and the
`vixen-app` template get it. `Vixen.Editor.Inspector`, `Vixen.Editor.AssetEditors` and
`Vixen.Editor.Terrain` resolve the host out of the service container. See
[its README](../../Editor/Vixen.Editor.Host/README.md) for what each channel delivers in a real
application — including the measurement that says a `.vxml` edit reaches a running process in tens
of milliseconds and that a stylesheet held in a C# `const` cannot reach one at all.

### Component replacement, and what reaches it

⚠ **`Replace` has a caller now, and it is the runtime's own.** It used to have none — the component
channel was tested and nothing anywhere reached it, on the reasoning that a rude edit is a
`dotnet watch` restart in practice. That reasoning was about the wrong signal. `UpdateApplication`
is handed **the types the runtime changed**, and it dropped them; passing them on is what closes the
gap, because the case rebuilding cannot cover is *not* a rude edit — it is an instance the update
left behind.

The runtime can add an instance field to a live type. The initialiser that would have filled it does
not run on an object that already exists, so a component holding a `Signal<T>` field the edit
introduced holds `null` there, the new `Build` dereferences it, and rebuilding again fails
identically for ever — a panel that goes empty on one save and never comes back. A fresh instance
runs its own initialisers, and `[HotReloadState]` is how anything worth keeping crosses over. That
is what the attribute has always been for.

⚠ **Only for a type the runtime named, and only after a throw.** Both halves guard the same thing:
replacing an instance discards everything not marked, and state preservation is what this channel is
for. A `Build` that throws for any other reason is a component whose type nobody edited, and it keeps
its fields and its error. A `.vxml` with a typo in it does not compile, so no update arrives for it
at all — a throw *after* a successful compile of its own type is the stale-instance shape and nothing
else is.

The report counts replacements separately from rebuilds, because they cost different things: a
rebuild keeps the object and therefore its signals, and a replacement keeps only what was marked.

## Owed

Scroll offsets and selection in the preserved set (neither exists yet to preserve). And a reload of
a *subtree* rather than a whole component, for when a large screen is being edited a control at a
time.

Licensed under Apache-2.0.

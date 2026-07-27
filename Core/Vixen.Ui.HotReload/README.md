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

For the edits .NET calls rude. The new instance starts from its own field initialisers, and
`[HotReloadState]` says what to carry — by name, because two instances of two versions of a type
share nothing else. A value whose type no longer fits is left behind rather than thrown at the
field: the whole point of a reload is that the type changed.

## What this does not do

**It does not deliver the new code.** A changed `.vxml` becomes a different `Build` only after
something has recompiled it. That is `dotnet watch`'s job plus the source generator's, and the
generator is owed — until it exists, the markup channel reloads whatever `Build` is currently in the
assembly, which is what makes it testable but not yet what makes it useful on a file save.

The runtime's own callback is wired: `MetadataUpdate` is registered as a `MetadataUpdateHandler` and
reloads every host still alive. Hosts are held weakly — a static list of every document ever created
is a leak with a development-only cause and a production-shaped consequence.

## The watcher

Watches `.vcss` only, for the reason above: watching for a file it could not act on would put a
spinner on an operation that never happens.

⚠ **Editors write files more than once.** Save-to-temp-then-rename, a truncate followed by a write,
a tool that touches the timestamp afterwards — one save can raise three events. Changes are
coalesced by path and applied on `Poll()`, which the frame loop calls; that also puts the reload on
the caller's thread, which matters because the element tree has no lock and a `FileSystemWatcher`
callback is on a pool thread.

## Owed

The source generator, so a file save produces a metadata update. Scroll offsets and selection in the
preserved set (neither exists yet to preserve). And a reload of a *subtree* rather than a whole
component, for when a large screen is being edited a control at a time.

Licensed under Apache-2.0.

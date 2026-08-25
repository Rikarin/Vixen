# 45 — Commands, and a Focus Scope That Is Derived

⚠️ **Extends [09](09-ui-framework.md) § Element tree and input, amends [36](36-an-extensible-editor.md),
and is the first thing an application outside the editor asks Vixen for and does not get.**

Vixen has a command system. It is good, it is complete, and it is in the wrong assembly — and its one
weak point is the one its own doc comment admits to.

## What exists, and is worth keeping

In `Editor/Vixen.Editor.Ui/Commands/`, 1 629 lines:

| Type | What it does |
|---|---|
| `EditorCommand` | Id, title, a live `Caption`, category, `Context`, `RadioGroup`, icon, `Enablement`, `Checked`, `Unavailable`, `CanExecute` |
| `CommandRegistry` | Registration, `Changed`, `Executed`, `IsInScope`, and a single `Execute` that all three entry points go through |
| `CommandDispatcher` | Keystroke → command. **One handler on the root, on the bubble leg**, so a control that wanted the key has already had it |
| `KeyMap`, `KeyChord`, `KeyMapPreset` | Bindings, per-context overrides with a global fallback, and `ForPlatform` so one spelling serves ⌘ and Ctrl |
| `CommandPalette`, `PaletteSource` | ⌘P over the registry |

Three decisions in there are right and this document does not touch them: the bubble-leg handler,
the guard that refuses an unmodified chord while a `TextField` is in the focus path, and ignoring
auto-repeat so held ⌘S saves once.

## Five gaps

**G1 — It is in `Vixen.Editor.Ui`.** `Vixen.Ui.Controls` has `MenuItem : ButtonBase` with a
`Disabled` bool and nothing that ever sets it; the only code that computes enablement lives in the
editor. So an application built on `Vixen.Ui` — a game's settings screen, or Trinix's shell and the
twenty applications behind it — has menus and no commands, and every menu item becomes a captured
lambda plus a hand-maintained `Disabled`. This is [36](36-an-extensible-editor.md)'s thesis one level
further out: the built-in was wired through a door only the editor has.

**G2 — The focus scope is pushed, not derived.** `CommandRegistry.FocusedContext` is a
`Func<string?>`, and what it calls returns `EditorShell.Context` — one mutable string that panels
assign by hand in their focus handlers (`Shell.Context = context;`, in `WaterModule` and
`BlockoutModulePanels`). The property's own remarks say the quiet part:

> a value pushed on focus change is one that is right only if every path that moves the focus
> remembered to push it

and then say it is asked on demand *rather than* pushed — but what it asks is a variable somebody
pushed. The pull is one level too shallow, and every new panel is a new chance to forget.

> ⚠️ **G2 is half wrong, and the half that is wrong is the half step 2 was built on. Amended
> 2026-08-25, after step 1 landed and step 2 met the code.**
>
> **The push is real; "in their focus handlers" is not.** Not one context in the editor is pushed
> from a focus handler. Every one is pushed from a `PointerEvent` on the **capture** leg of a
> `DockPanel` — `EditorApplication.Contextual` (`EditorApplication.cs:1774`) and
> `ContextualViewport` (`:1801`), plus four verbatim copies of the same eight lines in
> `BlockoutModulePanels.cs:93`, `TerrainModule.cs:181`, `WaterModule.cs:231` and
> `DiagnosticsModule.cs:439`.
>
> **And it is press-based on purpose.** `Contextual`'s own remarks say so:
>
> > **The press rather than the focus, because most of these panels do not take one.** A tree row is
> > focusable and a viewport is not, and "which panel did the user last act in" is the question a
> > scoped command is actually asking.
>
> That is load-bearing and it is true: `git grep -a "Focusable = true" -- 'Editor/**/*.cs'` matches
> **nothing** outside one test. No editor panel is focusable, so `CommandRoute.ScopeOf` — which
> walks `UiDocument.Focused` — answers `null` for the viewport, the console, the content browser and
> every other panel, and answers a `TextField`'s ancestry for the rest.
>
> **Four of the nine contexts are not places at all.** `blockout`, `terrain`, `water` and `foliage`
> are *modes* — "a statement about what the viewport's input means right now" — claimed by
> `Shell.Modes.Changed` (`EditorParity.cs:1199`) without any pointer or focus event. There is no
> element the focus could be on that would derive them. Hanging a `CommandScope` on the viewport and
> rewriting it on mode change is a push wearing the new API's clothes, which is not what G2 asked
> for.
>
> So: **a focus route is the right derivation for `Vixen.Ui` and the wrong one for this editor.**
> Step 1 stands on its own merits — G3 and G4 are what it is for, and neither depends on G2. What
> step 2 needs before it can be written is a decision this document does not yet contain: whether
> the editor's scope derives from the focus, from the last-pressed panel, or from both with a
> stated precedence — and what a mode is in that model. Until then, wiring `FocusedContext` through
> a route that is structurally `null` for every editor panel would change nothing while claiming to
> have changed something, which is worse than leaving it alone.
>
> The genuine defect the audit *did* find here is a different one and is worth a line: `Contextual`
> is six copies of eight identical lines across five assemblies. That is a `DockPanel` member, not a
> command-system problem.

**G3 — A command has exactly one implementation.** `EditorCommand.Run` is an `Action` captured at
registration. The property that makes AppKit's Edit ▸ Copy work everywhere — the menu declares
*what*, the focus decides *who* — is entirely absent. A global `edit.copy` must itself work out what
is focused, so one closure has to know about every kind of thing that can be copied, and a plugin's
new view cannot join in at all. **This is the largest gap and the reason the other four matter.**

**G4 — Enablement cannot fall out of a handler's absence.** `Enablement` is `Func<bool>?` defaulting
to always-enabled. AppKit's best affordance — *nobody in the chain responds, so the item greys, and
nobody wrote a rule* — cannot be expressed. Every command supplies a closure that reasons about
global state, which is the same coupling G3 describes wearing a different hat.

**G5 — There is no invalidation signal.** `CanExecute` is a poll. `CommandRegistry.Changed` fires
when the *set* of commands changes, not when one becomes executable. That is fine for a menu
evaluated as it opens and wrong for a persistently visible surface: a toolbar, and Trinix's global
menu bar, which has to *push* an `update` over a Wayland protocol at the moment an item greys.

## The shape

```
Vixen.Ui
  UiElement.AddCommandHandler(id, execute, canExecute?)   a handler, on an element
  UiElement.CommandScope                                  an attached scope name, inherited
  CommandRoute.Resolve(document, id)                      focused → parents → document → application
  CommandRoute.Invalidated                                one event, coalesced per frame
Vixen.Ui.Controls
  MenuItem.Command, Toolbar, ButtonBase — bind an id; Disabled, title and check state follow
Vixen.Editor.Ui
  EditorCommand keeps what is genuinely the editor's — Art, RadioGroup, palette visibility,
  IsUnavailable — and becomes a consumer of the core route rather than the owner of a private one
```

| Decision | Reason |
|---|---|
| Scope is **derived by walking `Document.Focused` → `Parent`**, not assigned | It is the same walk `CommandDispatcher.Available` already does looking for a `TextField`, so the mechanism is proven in the file that needs it. Deriving makes G2 structurally impossible rather than a thing to remember |
| A scope is **inherited down the tree**, and an element may declare a narrower one | So a panel declares its scope once at its root instead of every leaf inside it needing to know |
| **String ids stay** | They are already the vocabulary of the keymap file, the palette, the preset files and every plugin. Making them typed would be a better API and a migration of everything that names a command in data |
| **No selector dispatch, no reflection** | ADR-002. `AddCommandHandler` is an explicit registration; the chain walk is the only dynamic part, and it walks a tree Vixen already owns |
| The route resolves to **the first handler that responds**, and `canExecute` is asked of *that* handler only | AppKit's rule. A second responder that would also have handled it is not consulted, because "which one" must not depend on how many are listening |
| Nobody responds ⇒ **not executable** | G4, in one line. A command with a registration-time `Run` — which most editor commands legitimately are — is a handler on the document, so it always responds and nothing changes for it |
| Invalidation is **one coalesced event per frame**, not per query | A menu bar that re-asks forty items on every mutation is a menu bar that stutters. Focus change, handler registration, and an explicit `InvalidateCommands()` are the three sources |
| `EditorCommand` is **not** deleted or renamed | 1 629 lines and the whole editor depend on it. It gains a route and loses its private context resolution; the public surface it exposes to plugins does not move |

## Staging

Each step is independently shippable and leaves the editor working.

1. ✅ **`CommandRoute` + `AddCommandHandler` + `CommandScope` in `Vixen.Ui`.** No consumers. Tests
   only. — *Landed 2026-08-25.* `Core/Vixen.Ui/Commands.cs`, `Core/Vixen.Ui.Tests/CommandRouteTests.cs`
   (15 tests), `docs/guide/ui/commands.md`. Both features sit behind one nullable reference on
   `UiElement`, so an element that never takes part costs eight bytes and no allocation. Sabotage-
   verified three ways: ignoring the focus fails 9 of 15, falling through on a refusal fails 1, and
   an un-inherited scope fails 2. `CheckArchitecture` and `CheckDocs` green; the baseline additions
   are approved in `Core/Vixen.Ui/PublicAPI.Unshipped.txt`.
2. ⛔ **`CommandRegistry.FocusedContext` resolves through the route** instead of through the shell's
   string. `EditorShell.Context` becomes a fallback for anything not yet declaring a scope, and then
   is deleted once the panels are converted. ⚠ This is the step that can regress a keybinding, so it
   lands with a test per existing context. — **Not attempted: blocked on G2's amendment above.**
   The editor pushes its context from pointer presses on panels that are not focusable, so
   `CommandRoute.ScopeOf` is `null` for all nine contexts and this step would be a rename of the
   fallback. It needs a stated derivation for the editor first — see the box under G2 — and that is a
   design decision rather than a coding one.
3. **`MenuItem.Command`, `Toolbar` and `ButtonBase` binding in `Vixen.Ui.Controls`**, with `Disabled`,
   title and check state following the route.
4. **Editor menus and toolbars move onto the binding**, deleting the hand-maintained enablement.
5. **`Invalidated`**, and the two persistently visible surfaces subscribe.

## Acceptance criteria

- 🟡 A `Vixen.Ui` application with **no reference to `Vixen.Editor.Ui`** can declare a menu whose
  items are commands, and an item whose command nothing handles is disabled without the application
  writing a rule. — *The route half is done: `CommandRoute.CanExecute` is `false` when nothing
  responds (`Nobody_responds_so_it_is_not_executable`). The menu half is step 3.*
- ✅ Two views declare a handler for the same id; focusing each in turn runs a different one, and
  neither view knows the other exists. — `The_focused_leaf_decides_which_of_two_handlers_runs`.
- 🟡 A view registers a handler for `edit.copy` with `canExecute` returning false while its selection
  is empty; the menu item greys and un-greys with the selection, with no code in the menu. — *The
  predicate half is done: `Enablement_follows_a_selection_with_no_code_in_the_caller`. The menu item
  is step 3.*
- ⛔ Moving focus between two scopes changes which binding a chord resolves to **without any code
  assigning a context**, and `EditorShell.Context` no longer exists. — *Blocked. See the amendment
  under G2: the editor's panels are not focusable and four of its nine contexts are modes, so
  "moving focus between two scopes" is not a thing that happens in this editor as written.*
- ⬜ `Invalidated` fires once per frame at most, under a test that mutates state fifty times in one
  tick. — *Step 5. Deliberately not built in step 1: an event nothing raises and nothing subscribes
  to is this repository's commonest defect.*
- ✅ Every existing editor keybinding test passes unchanged. — *Trivially, nothing under `Editor/`
  was touched.*
- ✅ Public API additions are approved in `PublicAPI.Unshipped.txt`; `CheckApi` and
  `CheckArchitecture` are clean — in particular `Vixen.Ui` gains no reference to anything under
  `Editor/`. — *26 additions, no removals; `CheckArchitecture` "Checked 393 projects; no violations";
  `CheckDocs` reports the graph and the baselines agree.*

## Non-goals

Typed command ids · a `Vixen.Ui` command palette (the editor's stays where it is) · moving `KeyMap`
presets · undo integration · AppKit-style automatic responder insertion for controls that merely
*look* focusable.

## Effort and risk

**1.5 EM.** Step 2 carries the risk: it changes how every existing editor shortcut resolves, and the
failure mode is a chord silently resolving to the wrong context rather than an exception. The
mitigation is that step 2 ships with a test per context *before* the shell's string is deleted, and
that the fallback path keeps the old behaviour until each panel is converted.

⚠️ **Re-estimated after step 1.** Step 1 was a day, not a fortnight, because the walk it needed
already existed. Steps 2 and 4 are the ones that grew: they are no longer "wire the route in and
convert the panels" but "decide what an editor scope is derived from, given that its panels do not
take the focus and four of its contexts are modes" — a design question this document answered
wrongly and now does not answer at all. Steps 3 and 5 are unaffected and are still the cheap way to
reach the criteria that matter to a `Vixen.Ui` application, so **the next sitting should be 3 and 5,
not 2**. Step 2 is unblocked by an amendment to this document, not by code.

## Why now

[36](36-an-extensible-editor.md) argued that a built-in wired through the back door means the front
door was never proved. This is the same finding with a second witness: Trinix builds a desktop and
twenty applications on `Vixen.Ui`, its global menu bar is a Wayland protocol that needs pushed
enablement (G5), and its SDK's standard menu is exactly the "one menu item, no wiring, correct
behaviour everywhere" property that G3 blocks. It is the first consumer outside this repository, and
it arrives before the applications are written rather than after — which is the only cheap moment
this will ever have.

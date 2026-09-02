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
> That is load-bearing, and it was measured rather than argued.
> `CommandContextTests.Only_one_panel_in_seven_leaves_a_focus_behind_for_a_route_to_read` presses in
> each of the seven panels that claim a context and reads `UiDocument.Focused` afterwards:
>
> | Panel | Context claimed | `Document.Focused` after the press |
> |---|---|---|
> | `hierarchy` | `scene` | `<tree-view>`, inside the panel |
> | `scenes` · `project` · `console` · `world-settings` · `lighting` · `navigation` | `scene` · `project` · `console` · `world` ×3 | **none** |
>
> **Six of seven leave nothing for a route to read**, because `git grep -a "Focusable = true" --
> 'Editor/**/*.cs'` matches nothing outside one test: no editor panel is focusable, and the press
> lands on nothing that is. `hierarchy` is the exception only because it contains a `TreeView`, whose
> rows are — and note what that costs rather than what it buys: a press in `hierarchy` gives the
> route a *row*, so the scope it derives is whatever the row's panel declared, not the panel the
> press was in. Those happen to coincide today.
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
> **And the scope machinery has almost no consumers, which changes what step 2 is worth.** Exactly
> one command in `Vixen.Editor.App` declares a `Context` at all — `edit.rename`, at
> `EditorParity.cs:286`, through the `Scoped` helper at `:1312`. Of the nine context strings, four —
> `console`, `project`, `world`, `diagnostics` — are **only ever written**: no command and no keymap
> override is filed under any of them, and their entire effect is to *not* equal `scene`, which is
> what takes `edit.rename` out of scope while the console has been clicked in. The rest of the
> scoping is the four mode contexts, in the mode modules.
>
> So the thing step 2 was going to make derivable is a mechanism with one panel-scoped verb and four
> mode-scoped groups behind it. That is worth doing correctly and it is not worth doing urgently.
>
> The genuine defect the audit *did* find here is a different one and is worth a line: `Contextual`
> is six copies of eight identical lines across five assemblies. That is a `DockPanel` member, not a
> command-system problem.
>
> **Done 2026-09-02 ([#419](https://github.com/Rikarin/Vixen/issues/419)).** `DockPanel.WhenPressedIn`
> takes the claim as an `Action` and all six copies are gone. The claim is read on *every* press
> rather than captured once, which is what let `ContextualViewport` — the one that reports whichever
> mode is active rather than a constant — fold into the same member instead of needing a second.
> ⚠ **And the eleven cases of `CommandContextTests` did not pin the thing every one of those copies'
> comments called the point.** Rewriting the shared member to the bubble leg with
> `handledEventsToo: false` left all eleven green: a press in an *empty* panel reaches the panel on
> either leg, and every panel those tests press is empty. `DockPanelPressTests` puts a child that
> consumes the press in the panel and asserts the order, which is what makes the leg falsifiable.
> This changes nothing about what a scope is derived from — that is still step 2's decision, and it
> is now one edit rather than six.

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
  CommandRoute.Resolve(document, id)                      focused → parents → root → document → application
  ICommandResponder / CommandResponder                    the two levels past the root, owning no element
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
3. ✅ **`MenuItem.Command`, ~~`Toolbar`~~ and `ButtonBase` binding in `Vixen.Ui.Controls`**, with
   `Disabled`, title and check state following the route. — *Landed 2026-08-25.* `Command` is on
   `ButtonBase`, so `Button`, `IconButton`, `MenuItem`, `ToggleButton` and `Link` all have it;
   `MenuItem` overrides only the tick gutter. `CommandHandler` gained `Title`, `IsCheckable` and
   `IsChecked`, because the route as step 1 left it carried neither of the two things this step was
   asked to bind. 10 tests in `Core/Vixen.Ui.Controls.Tests/CommandBindingTests.cs`, one of them
   through a real compiled `.vxml`.

   > ⚠️ **`Toolbar` is not built, and the doc naming it was the last of G5's assumptions to survive
   > contact.** No `Toolbar` type exists in `Vixen.Ui.Controls`; what exists is
   > `Editor/Vixen.Editor.Ui/Menus/ToolbarPresenter.cs`, whose `Refresh` (`:166`) does exactly three
   > things per button — `Disabled`, `Label` from `Caption`, `ElementState.Checked` — and all three
   > are now what `ButtonBase.Command` does by itself. What is left of a toolbar is a `Panel` of
   > `Button`s and `Separator`s, both of which already exist, plus the `toolbar-group` class the
   > editor theme already has. A new public control would have been a container with no behaviour in
   > it.

   > ⚠️ **A second, larger finding: the route could not see past the surfaces that display it.**
   > `Menu.OnOpened` (`Menus.cs:364`) focuses its first item so the arrow keys work, and
   > `MenuBarItem` is a `ButtonBase` that takes the focus when pressed. So a menu item resolving
   > `edit.copy` from `UiDocument.Focused` resolved it **from inside the menu**, found nothing, and
   > greyed every line — the criterion below would have been met by a menu in which every command
   > was permanently disabled. Step 1's model is right and was one flag short of usable: a surface
   > that shows commands must be able to say it is not a *place*. `UiElement.IsCommandTransparent`
   > is that flag, `UiDocument.CommandFocus` is the focus that ignores it, and
   > `CommandRoute.Origin` now reads the latter. `Menu`, `MenuBar` and any control with a bound
   > `Command` set it. It is AppKit's "a menu is not in the responder chain", stated as data.
   > Sabotage: removing the transparency test in `Focus` fails 6 of 10 binding tests.
3b. ✅ **The chain continues past the root**, which the shape block above promised
   (`focused → parents → document → application`) and step 1 did not build. — *Landed 2026-08-25.*
   `CommandRoute.Resolve` stopped at the root element; AppKit's action chain does not — per the
   Cocoa Event Handling Guide it runs first responder → views → window → window controller →
   window delegate → `NSDocument` → `NSApp` → app delegate → `NSDocumentController`, and most of
   that tail is not views. Two consequences, both closed: there was no application or document
   level at all, and **a handler had to hang on a `UiElement`**, so a view-model or a document
   object that wanted to own `edit.copy` had to own a piece of the view tree to say so.

   `Vixen.Ui` gains `ICommandResponder` (one method, id → handler), `CommandResponder` (the table
   almost everything wants — same five arguments and same duplicate-id throw as
   `AddCommandHandler`), `CommandHandler.For`, `CommandHandler.Responder`, and two slots on the
   document: `CommandResponder` then `ApplicationCommandResponder`, asked in that order after the
   last parent. 12 tests in `Core/Vixen.Ui.Tests/CommandResponderTests.cs`. 11 baseline additions
   plus one nullability change — `CommandHandler.Element` is now `UiElement?`, because past the
   root a handler has no element and one that claimed an element it did not have would make a
   diagnostic overlay lie.

   > ⚠️ **The level has a real consumer, and it is not step 4.** An extension point nothing
   > registers into is this repository's commonest defect, so the application level ships wired:
   > `CommandRegistry` implements `ICommandResponder` over the table it already had — the interface
   > rather than a mirrored `CommandResponder`, which would be a second copy wrong the first time a
   > plugin registered into one and not the other — and `EditorShell` installs it as its document's
   > `ApplicationCommandResponder`. So a plain `Vixen.Ui` control bound to `edit.rename` now
   > resolves, greys and runs the editor's command, through the registry's own scope-and-enablement
   > gate and raising its `Executed`, with nothing editor-shaped in the control. 8 tests in
   > `Editor/Vixen.Editor.Ui.Tests/CommandChainTests.cs`. Nothing existing changed: nothing in the
   > editor resolved through `CommandRoute` before, so this only adds answers where there were
   > none. **Menus and toolbars are still not bound — that is step 4 and it is still owed.**

   > ⚠️ **Three of the doc's other parity assumptions were checked and hold, so nothing was
   > rebuilt.** `UiElement.Focusable` + `TabIndex = -1` is already `acceptsFirstResponder` plus
   > exclusion from the key view loop (`Focus.cs:206`, *"Negative is focusable but not a stop"*),
   > so **focus acceptance needed no new API**. `CommandDispatcher`'s single root handler on the
   > **bubble** leg (`CommandDispatcher.cs:57`, `RoutingStrategy.Bubble` by default) gives the inner
   > control the same priority AppKit's downward `performKeyEquivalent:` does — different mechanism,
   > same outcome. `IsCommandTransparent` on `Menu` (`Menus.cs:198`), `MenuBar` (`:609`) and bound
   > controls (`ButtonBase.cs:82`) is already "a menu is not in the responder chain".

   > ⚠️ **One claim in the brief for this step was refuted: the editor's `CommandRegistry` does not
   > outlive its shell.** `EditorShell.cs:102` is the only place one is constructed and the shell
   > owns it, so the `Changed` subscription added here is a cycle inside one ownership unit — the
   > pair is collected together and this is *not* the ~95 MB-a-reload shape. It is unsubscribed in
   > `Dispose` anyway, because a disposed shell invalidating a disposed document is wrong on its own
   > terms, and `A_kept_registry_no_longer_reaches_the_shell_that_made_it` asserts it with an
   > internal subscriber count rather than with a leak test that would have been asserting a leak
   > that is not there. The direction that *can* leak is the framework's, and it points the safe
   > way: `ICommandResponder` has no event and no back-reference, so a responder never learns which
   > documents it was installed on —
   > `A_long_lived_responder_does_not_keep_a_closed_document_alive` proves that against the
   > collector, and `UiDocument.Dispose` drops both slots and the `CommandsInvalidated` subscribers.

   Sabotage, both directions: deleting the two responder blocks from `Resolve` — the chain stopping
   at the root again — fails **7 of 896** in `Vixen.Ui.Tests` and **5 of 439** in
   `Vixen.Editor.Ui.Tests`; asking the application *before* the document fails **4** and only the
   four ordering tests, leaving `The_document_is_asked_after_the_root_and_the_root_wins` green
   because it is about the other join.

4. **Editor menus and toolbars move onto the binding**, deleting the hand-maintained enablement.
5. ✅ **`Invalidated`**, and the two persistently visible surfaces subscribe. — *Landed 2026-08-25.*
   `UiDocument.CommandsInvalidated` and `UiDocument.InvalidateCommands()`, raised from
   `UiDocument.Tick`; `ButtonBase` subscribes for as long as it has a bound id, so the event ships
   with its consumers rather than ahead of them. 6 tests in
   `Core/Vixen.Ui.Tests/CommandInvalidationTests.cs` plus 2 subscriber tests in the controls
   project. Sabotage both ways: raising eagerly instead of coalescing fails 2 of 6 (100 raises where
   1 is asserted), and never raising at all fails 4 of 6 and 1 of 12 bindings.

   > **On the document, not on `CommandRoute`.** The doc's shape block says
   > `CommandRoute.Invalidated`, and `CommandRoute` is a static class: a static event would hold
   > every subscribing control — and through it every element it can reach — alive for the life of
   > the process, and one document's focus change would invalidate every other document's surfaces.
   > All three sources are per-document facts, and "once per frame" needs a frame, which is
   > `UiDocument.Tick`. `MenuPresenter` already carries a comment about `Strings.Changed` being
   > static and outliving the document it was subscribed from, which is the same defect one level
   > out.

   > **`Tick` rather than `Update`, and the difference is load-bearing.** `UiDocument.Update`
   > returns early when nothing dirtied the document, and a command becoming executable is not a
   > thing that dirties one — so a surface hung on the pass goes stale for exactly as long as the
   > interface is still, which is most of the time. `Tick` is the one call a host must make every
   > frame whether anything happened or not. `It_is_raised_from_the_tick_because_a_still_document_runs_no_pass`
   > asserts `Update()` returns `false` on the frame the raise happens.

## Acceptance criteria

- ✅ A `Vixen.Ui` application with **no reference to `Vixen.Editor.Ui`** can declare a menu whose
  items are commands, and an item whose command nothing handles is disabled without the application
  writing a rule. — *`An_id_nothing_handles_disables_the_item_and_the_menu_writes_no_rule`, and
  `An_item_bound_from_markup_is_bound_the_same_as_one_bound_from_code` proves it from a compiled
  `.vxml` containing nothing but labels and ids.*
- ✅ Two views declare a handler for the same id; focusing each in turn runs a different one, and
  neither view knows the other exists. — `The_focused_leaf_decides_which_of_two_handlers_runs`.
- ✅ A view registers a handler for `edit.copy` with `canExecute` returning false while its selection
  is empty; the menu item greys and un-greys with the selection, with no code in the menu. —
  `Enablement_follows_a_view_s_predicate_with_no_code_in_the_menu`.
- ⛔ Moving focus between two scopes changes which binding a chord resolves to **without any code
  assigning a context**, and `EditorShell.Context` no longer exists. — *Blocked. See the amendment
  under G2: the editor's panels are not focusable and four of its nine contexts are modes, so
  "moving focus between two scopes" is not a thing that happens in this editor as written.*
- ✅ `Invalidated` fires once per frame at most, under a test that mutates state fifty times in one
  tick. — `Fifty_mutations_in_one_tick_raise_it_once`, which makes a hundred mutations (fifty
  explicit invalidations and fifty registrations, so the sources coalesce against each other and not
  merely each against itself) and asserts one raise. The other direction is asserted too, because an
  event that never fires satisfies "at most once" perfectly: `Each_of_the_three_sources_raises_it`
  checks the three one at a time with a frame between, and
  `A_button_that_is_always_on_screen_follows_the_invalidation_instead_of_polling` proves a real
  control follows it — and that ten quiet frames ask no predicate at all.
- ✅ An object that is **not** a `UiElement` — a view-model, a document, an application shell — can
  own a command id, and the chain reaches it after the tree without changing a single rule about
  which handler wins or whose `canExecute` is asked. —
  *`A_responder_owns_no_element_and_says_so`,
  `The_document_wins_over_the_application_and_the_application_is_never_asked` (counter-asserted:
  the further responder's lookups **and** predicates are both nought), and
  `A_document_responder_that_refuses_does_not_fall_through_to_the_application`.*
- ✅ Every existing editor keybinding test passes unchanged. — *439 pass in
  `Vixen.Editor.Ui.Tests`; the registry's own `Execute` path is unchanged and the route enters it
  through the same single gate.*
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

# 46 — What an Application Needs

⚠️ **Extends [09](09-ui-framework.md) and [36](36-an-extensible-editor.md), and is the general case of
[45](45-commands-and-focus-scope.md).** Written from outside this repository. Trinix builds a desktop
shell and twenty applications on `Vixen.Ui`, is the first consumer of the UI framework that is not the
editor, and this is the consolidated list of what it finds it cannot reach.

[45](45-commands-and-focus-scope.md) found one piece of application-framework machinery in the editor
and treated it as a fact about commands. Two later investigations — one about localisation, one about
accessibility — arrived at the same sentence from different directions, and a deliberate sweep of
`Vixen.Editor.Ui` for this document found two more. **One thing in the wrong assembly is a bug in a
file. Five is a property of the tree**, and it is [36](36-an-extensible-editor.md)'s thesis one level
further out: a built-in wired through a door only the editor has means the front door was never
proved.

Every number below was measured against the tree rather than recalled. Where a conclusion is
judgement it says so.

---

## Part 1 — The pattern, measured

| # | What an application needs | Where it is | Lines | What `Vixen.Ui` has instead |
|---|---|---|---|---|
| 1 | Commands: an id, a handler, enablement, a keymap, a palette | `Editor/Vixen.Editor.Ui/Commands/` | **1 629** | `MenuItem : ButtonBase`, and a `Disabled` bool declared on `Control` (`Control.cs:78`) that nothing in the control set ever sets on one |
| 2 | A string catalogue | `Editor/Vixen.Editor.Ui/Localisation/` | **783** | Nothing — plus twelve English literals baked into the controls |
| 3 | A modal question that returns an answer | `Editor/Vixen.Editor.Ui/Dialogs/DialogService.cs` | **376** | `Dialog` (`Vixen.Ui.Controls/Dialogs.cs`) — an overlay with a body, a footer and no answer |
| 4 | An undo history | `Editor/Vixen.Editor.Core/CommandStack.cs` | **372** | Nothing, and `CodeBuffer.cs:49` says so in as many words |
| 5 | An accessibility tree | *nowhere* | **0** | Nothing. Three doc comments, two of them in the future tense |

Rows 1 and 2 are 45's finding and Trinix's localisation audit. Rows 3, 4 and 5 are this document's,
and row 5 is the one that is not a misplacement at all.

### The sweep, and where the line actually falls

`Vixen.Editor.Ui` is 12 794 lines of `.cs`, `.vxml` and `.vcss`. Decomposed by directory, and the
figures sum exactly:

| Directory | Lines | Is it the editor's? |
|---|---|---|
| `Commands/` | 2 139 | **No.** A command is an id and a delegate |
| `Menus/` — `MenuModel`, `MenuPresenter`, `ToolbarModel`, `ToolbarPresenter` | 1 024 | **No.** A menu bar built from command ids |
| `Menus/` — `EditorIcons`, `EditorArt`, `ModeArt`, `EditorIconAttribute` | 986 | Yes — an icon set is an identity |
| `Localisation/` | 783 | **No** |
| `Tasks/` | 485 | **No.** Progress and cancellation for long work |
| `Notifications/` | 466 | **No.** A toast plus a history |
| `Dialogs/` | 376 | **No** |
| `Theming/` | 2 001 | Yes — 1 470 lines of it are the editor's own stylesheet |
| `EditorShell.cs` | 1 009 | Yes |
| `Console/` | 857 | Yes |
| `Palette/` | 798 | Yes, by choice — 45 puts a `Vixen.Ui` palette in its non-goals |
| `Docking/` | 782 | Yes |
| `Modes/` | 492 | Yes |
| `Settings/` | 463 | Yes |
| `Parts/` | 133 | Yes |

**5 273 lines — 41 % of the assembly — are application-framework machinery that no application can
reach.** Not one of the six "No" rows names a project, a document, an asset or a scene.

That is not an accident of this sweep, either. The assembly's own project file certifies it, in a
comment on the reference it deliberately does not have:

> ⚠ Deliberately *not* `Vixen.Editor.Core`. The shell is chrome: a command is an id and a delegate, a
> panel is an id and a factory, and nothing here knows what a project, a document or an undo stack is.

That discipline is real and it is good. What it does not do is separate *editor chrome* from *any
application's chrome*, because it was never asked to — so a dock workspace and a command registry are
on the same side of the only line anybody drew.

### The third case, found by looking

`Vixen.Ui.Controls` gets modality right, and says so at length: a real backdrop element rather than a
colour, `IsFocusScope` so Tab cannot walk out of it, and the focus restored to *whatever had it*
rather than to whatever opened it (`Dialogs.cs:21-34`, `OnOpened`, `OnClosed`). All of that is 192
lines shared with `Drawer`, and none of it answers a question.

The 376 lines that make a dialog *answerable* are in the editor, and they encode four things a first
attempt gets wrong — each one written down where it was learned:

- **Answering does not remove anything.** The click that answers is dispatched inside the dialog's own
  subtree; tearing that subtree down inside its own event leaves the router walking removed elements.
  `Answer` records and closes; `Pump` removes on the next tick.
- **The caller's continuation runs on the frame loop**, between two frames, from `Pump` — not from the
  click handler.
- **One at a time, queued rather than refused.** Two backdrops over each other is a picture with no
  answer in it, and the callers are commands: *"your Save prompt was dropped because a rename was
  open"* is the failure that loses work.
- **`CancelAll` answers everything waiting rather than dropping it**, so a command awaiting a dialog
  during shutdown unwinds instead of never finishing.

Trinix's exposure to exactly this is not hypothetical. Save / Don't Save / Cancel on close is the one
modal that every one of twenty applications has, it is the one that runs while the process is going
away, and it is the case `CancelAll`'s remark is about.

⚠ And the reason the editor's dialogs are *drawn* rather than native is Trinix's reason too, already
written down here: a modal that is an OS window cannot be screenshotted by a golden-image suite or
driven by a headless harness. Trinix gates every first-party window that way.

### The fourth, one assembly further out

`CommandStack` is not in `Vixen.Editor.Ui` — it is in `Vixen.Editor.Core`, which is one more assembly
an application cannot reference. `CodeBuffer` (`Vixen.Ui.Controls.Advanced/CodeBuffer.cs:49`) states
the division correctly:

> ⚠ **No undo stack.** Undo belongs to the application, because it has to be interleaved […] and an
> undo stack inside the text control can only ever undo typing.

That is right, and the consequence is that **⌘Z does nothing in a `TextField` in a `Vixen.Ui`
application** — there is no undo anywhere below `Vixen.Editor.Core`, and no seam for one. Trinix is not
asking for `CommandStack` to move (Part 3), but it is the fourth witness, and it carries the line this
document keeps coming back to:

> **Everything a menu binds to is a signal.** "Undo" is enabled from `CanUndo` and labelled from
> `UndoName`, and a title bar's asterisk comes from `IsDirty`, with no change notifications to wire up
> and nothing to forget to raise.

Three directories away, `Strings` is a static field and a plain `event`. **Vixen already knows the
answer that the localisation ask below is asking for.**

### The fifth is not in the wrong assembly, because it is not anywhere

⚠ **This corrects the document that sent it, and the correction matters more than the ask.** Trinix's
accessibility plan says Vixen's controls "have accessibility in the design (doc 09 lists it as part of
every control's base API), and what exists is not audited." Both halves are wrong, and they were
checked before being repeated:

- **[09](09-ui-framework.md) mentions accessibility once**, at the Controls row of its *Testing* table:
  *"Per control: keyboard interaction matrix, ARIA-role snapshot, virtualisation […] and a golden
  image."* That is a promise about a test. § The element tree and property system lists focus scopes,
  tab order, arrow navigation and `accesskey` — the keyboard half — and no role, no name, no value, no
  relations. There is no § Accessibility in doc 09 and no base-API line anywhere in it.
- **There is nothing in the code.** No `Role`, no `AccessibleName`, no `AutomationId`, no accessibility
  namespace, no platform bridge, in `Vixen.Ui`, `.Controls`, `.Controls.Advanced`, `.Layout`, `.Text`,
  `.Testing` or `Vixen.Platform`. The entire surface is three doc comments — `Buttons.cs:31`,
  `Display.cs:161`, `BuildContext.cs:620` — and two of them are written in the future tense: *"what an
  accessibility bridge **will** read"*.

"Unaudited" implies an audit could find something. It cannot. **The correct word is greenfield**, and
that changes what the ask is and what it costs.

---

## Part 2 — What Trinix needs, ranked

Ranked by consequence, which is not the same as by size. A1 is first because it is cheapest and has a
hard deadline; A2 is a close first on consequence and is ranked second only because its deadline is
one phase later.

### A1 — Commands, bound to menus, with pushed invalidation

**Ask: [45](45-commands-and-focus-scope.md) steps 3 and 5.** Not step 2 — that is the editor's own
question about what an editor scope derives from, Trinix has no stake in it, and 45's own amendment
says the next sitting should be 3 and 5.

- **Step 3** — `MenuItem.Command`, `ButtonBase` and `Toolbar` binding in `Vixen.Ui.Controls`, with
  `Disabled`, title and check state following `CommandRoute`.
- **Step 5** — `Invalidated`, one coalesced event per frame.

Step 1 landed on 2026-08-25 (`Core/Vixen.Ui/Commands.cs`, 325 lines), so `CommandRoute.Resolve`,
`CanExecute` and `AddCommandHandler` already exist and have no consumers. Step 3 is what gives them
one.

**Why a poll is not enough, stated precisely.** 45's G5 says `CanExecute` is a poll and that this is
*"fine for a menu evaluated as it opens and wrong for a persistently visible surface"*. In Trinix it is
worse than that, and the evidence is in the protocol rather than in an opinion:

- Trinix's menu bar is a Wayland protocol, `trinix-menu-v1`. An item's state is pushed with
  `update(id, label, state)`, where `state` is a bitfield whose first entry is `enabled`.
- `set_accelerator`'s own description: *"The shell both displays this and honours it: a shortcut on a
  menu item works whether or not the menu has ever been opened."*
- And the compositor's accelerator lookup **skips any item that is not `enabled`** — the comment above
  it reads *"Disabled and hidden items are not reachable by their shortcut."*

So in Trinix **the enabled bit gates the keyboard shortcut, in another process, before the application
is asked anything.** A menu that computes enablement as it opens — which is what
`MenuPresenter.cs:404` does, and its own remark says so: *"Enablement is applied as the menu opens, not
as it is built"* — is a menu whose ⌘S silently does nothing, because the accelerator path never opens
a menu. `about_to_show` does not rescue it: it fires per submenu, when a submenu opens, and it is
explicitly best-effort (*"the shell shows the menu on the next commit, or immediately if none arrives
promptly"*).

**Consequence of not having it.** Twenty applications × every menu item, each carrying a
hand-maintained `Disabled` bool *and* a hand-remembered `update` call, with a failure mode that is
invisible in-process — the application thinks the item is enabled; the compositor has the stale bit and
eats the chord. Trinix's own menu design took *"item state stays the application's job"* as a decision,
on the grounds that a responder chain in a compositor would be inventing a UI framework in a
compositor. That is correct, and it makes the responder chain the UI framework's job. `CommandRoute`
*is* the responder chain, in the client, and it already exists.

⚠ **One note on step 5, and it makes it cheaper rather than dearer.** The coalesced event does not need
to carry which ids changed. Re-asking sixty predicates once a frame is nothing in-process; issuing
sixty Wayland requests is not — so the *binding* must diff the resolved state against what it last
pushed and send `update` only on a change. That is the consumer's discipline, not Vixen's API. Said
here so it is not later requested as a signature change. [Judgement: mine.]

### A2 — An accessibility tree, and the reason it has to be now

**Ask: role, name, description, value, state and relations on `UiElement`; a document-level change
notification; and population for every control in `Vixen.Ui.Controls` and `.Controls.Advanced`.** The
tree, not a bridge.

**The timing argument is the whole of why this is in this document rather than its own.** Whoever
implements A1 is inside `UiElement`, `Focus.cs` and every control in both control assemblies. An
accessibility tree is four things, and three of them are that same code at that same moment:

| What the tree needs | Where it comes from |
|---|---|
| A role and a name per element | A per-control pass over `Vixen.Ui.Controls` and `.Controls.Advanced` — **the same pass step 3 makes** to add `Command` to `MenuItem`, `ButtonBase` and `Toolbar` |
| Focus, focus scopes, tab order | `Focus.cs` already has `TabOrder`, `IsFocusScope` and the scope walk; a screen reader's "what has focus" is the query `CommandRoute.Origin` already answers |
| A coalesced change notification | **Structurally the same object as step 5's `Invalidated`** — one event per frame, three sources, nothing raised per mutation |
| The platform bridge | Not asked for. AT-SPI2 is Trinix's, UIA and NSAccessibility are whoever wants them |

Batching costs the shared traversal once. A second pass costs it twice and costs a second round of
API-baseline approvals over the same types. [Judgement: mine, and it is the argument for sequencing
rather than for the work.]

**Doc 09 already owes this to itself.** Its Testing table promises an ARIA-role snapshot per control.
That test cannot be written today, and making it writable is the acceptance criterion — a Vixen
criterion, from a Vixen document, that happens to be what Trinix needs.

**Consequence of not having it.** Trinix's accessibility plan budgets 2.0 EM for an AT-SPI2 bridge that
caches the tree and pushes changes, because AT-SPI is chatty and a naïve bridge is a round trip per
node. There is no tree to cache and no change to push. Its CI gate — every interactive element has a
role and an accessible name, no control keyboard-unreachable, the bridge's tree for a reference window
matches an expected shape — cannot be written at all, so the failure is not "accessibility is late": it
is that twenty applications ship, and then the accessible name of every control in them has to be
invented retroactively by somebody who did not write them.

⚠ **And it is the one ask where Trinix cannot help itself.** A Trinix-side shim over a tree that does
not exist is a second element tree, maintained by hand, in another repository, drifting from the first
— which is Trinix's own risk register calling it *"fast, wrong, and permanent"*. Its deadline for
asking is its Phase 9, and asking is this document.

### A3 — The string catalogue: promoted, and read through a signal

**Ask, in three parts, smallest first.**

**1. Promote `StringId`, `StringCatalog` and `Strings` out of `Vixen.Editor.Ui`.** 783 lines and 123
declared ids, and the design is right: the source text lives at the declaration, so a missing
catalogue shows English rather than `editor.menu.file`; ids are dotted paths saying where a string is
used rather than what it says; `Missing` is the translator's worklist; `Template` exports one.

⚠ **The promotion must leave `Save` and `Load` behind.** `StringCatalog.cs:4` is
`using Vixen.Core.Yaml`, and those two methods are its only use of it. Trinix's vendored closure is 41
packages and does not include `Vixen.Core.Yaml`; Trinix ships catalogues as JSON with a source-generated
reader because it publishes NativeAOT. A `StringCatalog` promoted with its YAML attached adds a package
to a consumer's pin for a code path that consumer will never call. The catalogue proper is
`Set`/`Find`/`Ids`/`Count`; the YAML pair is an editor convenience and can stay one, or become an
extension on the Yaml side.

**2. Make the lookup a signal.** `Strings.cs:56` is a `static StringCatalog current` field and
`Changed` is a plain `event Action<StringCatalog>`. The class concedes the consequence itself —
*"Changing the language does not re-label what is already on screen"* — and answers it by rebuilding
the menu bar and asking for a restart for everything else.

That answer is fine for one editor and permanent for a desktop. Every `@expr` in a `.vxml` is already
a region-scoped `Effect`; if `Strings.Get` read a `Signal<StringCatalog>`, a language change would
re-label a running interface with no code at any call site, in any application, ever. If it reads a
field, twenty applications and a shell need a restart forever — and by the time that is noticed there
are twenty of them.

**This is one line in one file today**, `Vixen.Ui.Reactive` is already `Vixen.Ui`'s dependency, and
`CommandStack` three directories away has the rule written down. After the type is public it is a
public-API change to a static class.

**3. Put the control set's own literals through it.** Twelve, exactly, and they are a small pull
request rather than a project:

| String | Where |
|---|---|
| `"Clear"` | `Vixen.Ui.Controls/TextInputs.cs:69` |
| `"Close"` | `Vixen.Ui.Controls/Dialogs.cs:91`, `Controls.Advanced/DockingHost.cs:472` |
| `"Dismiss"` | `Vixen.Ui.Controls/Toasts.cs:66` |
| `"Show suggestions"` | `Vixen.Ui.Controls/Selects.cs:621` |
| `"Previous tab"` · `"Next tab"` | `Controls.Advanced/DockingHost.cs:548`, `:557` |
| `"Reset"` · `"Search"` | `Controls.Advanced/PropertyGrid.cs:53`, `:117` |
| `"Intensity"` · `"Pick a colour from the screen"` | `Controls.Advanced/ColorPicker.cs:486`, `:478` |

**`Strings.Resource`, checked as asked: planned, not built.** [11](11-editor.md) § asks for it at line
87; the *As built* box at line 104 records that it is not generated; [`../overview.md`](../overview.md)
carries it as owed against `Vixen.Editor.Ui`; and `EditorStrings.cs:9` says the type is *"written by
hand until it does"*. So *"an id used nowhere and an id declared nowhere are both build errors"* is
owed on both sides of the fence.

⚠ **Trinix is not asking Vixen to build it.** Trinix is building its own — a `Strings.yaml` →
declarations generator in `Trinix.Sdk.Generators`, because the catalogue source and the tooling are
Trinix's by its own scope rule — and it will build it whether or not Vixen does. **What is worth
having upstream is the declaration shape, unchanged**, so that two generators emit the same thing and a
string moved between the two projects is a copy rather than a translation. That is what promotion
buys; the generator itself is Vixen's own business on Vixen's own schedule.

**Consequence of not having it.** `Trinix.Sdk` carries a private copy of a type Vixen already has —
which costs about the same either way — and Vixen's own controls stay English inside every localised
Trinix window: "Clear" in every search box, "Dismiss" on every toast, "Previous tab" on every docked
group. A visible seam in the one place a user cannot avoid looking, and the only party who can close it
is Vixen.

### A4 — A dialog that answers

**Ask: `DialogService` in `Vixen.Ui.Controls`, or the four behaviours it encodes on `Dialog` itself.**
Confirm, prompt, choose, and a `ShowAsync<T>` a caller fills in; a queue; a completion that runs from
the frame loop; and a cancel-all for shutdown.

**Consequence of not having it, honestly.** This is the ask Trinix can most afford to lose. A dialog
*service* is not a control, so writing one in `Trinix.Sdk` does not violate Trinix's rule against
forking the widget library, and the cost is about 0.25 EM plus a permanent divergence in how the two
projects' modals behave under shutdown. It is ranked fourth and it is on the list for two reasons: it
is the fourth witness to the pattern, and the version that exists already has the four subtleties
right — which is precisely the value a framework adds and a re-implementation loses.

### Acceptance, in four lines

- A `Vixen.Ui` application with **no reference to anything under `Editor/`** declares a menu of command
  ids, and an item whose command nothing handles is disabled without the application writing a rule.
  (45's first criterion; its route half is done.)
- Subscribing to one coalesced per-frame event is enough to keep an out-of-process menu bar's enabled
  bits correct, with no polling and no per-mutation callback.
- Doc 09's promised **ARIA-role snapshot test can be written**, for every control in both control
  assemblies.
- A language change re-labels a running interface, under a test that changes the catalogue between two
  frames and asserts a bound label changed — with no code in the application.

---

## Part 3 — What Trinix is not asking for

This matters as much as Part 2, and it is deliberately long. Trinix's SDK document forbids adding a
control, a layout mode, a styling feature or a text capability to itself — *"a proposal to add [one] is
a proposal to fork Vixen, and the answer is a pull request against Vixen instead"* — and the same rule
cuts the other way: **nothing here asks Vixen to grow a surface for Trinix's benefit.** Every ask in
Part 2 is either "move an existing file up" or "the thing doc 09 already promised itself". No API in
any of them names Trinix, knows a compositor exists, or is shaped by a Wayland protocol.

Where the right answer is that Trinix writes it, it is written here so it stops being an open question:

| Not asked for | Why |
|---|---|
| `ThemeService`, `EditorTheme.vcss` | Trinix **replaces** `ControlTheme` at the UserAgent origin rather than overriding it. The editor's visual identity is the editor's, and the mechanism it uses — nine custom properties on the root — is already in `Vixen.Ui.Styling` |
| `NotificationCenter`, `MessageLogView` | Trinix notifications are a system service with a permission behind them, not an in-process toast history. `ToastHost` already exists for the in-process half |
| `BackgroundTaskManager` | ⚠ The closest call in this table. It is 485 lines, it is signal-backed and correct, and every application needs progress with cancellation. It stays out because a Trinix application's long work is usually a service call with its own progress, and because taking it would mean asking for `Vixen.Editor.Ui`'s task model *and* its task centre. Trinix writes its own. **Would take it if it were offered** |
| `CommandStack` and the editing pipeline | Undo is the application's — `CodeBuffer` is right about that, and a document's undo history is shaped by what the document is. Trinix writes one per application family. Named in Part 1 as evidence, not as an ask |
| `DockingWorkspace`, `LayoutPresets`, `EditorUserStore` | Trinix applications are not docking editors, and where the user's preferences live is answered by Trinix's own settings store |
| `CommandPalette`, `PaletteSource`, `FuzzyMatcher` | 45 puts a `Vixen.Ui` palette in its non-goals and that is right. Trinix has a system-wide search of its own |
| `KeyMap`, `KeyMapPreset`, the keymap files | Trinix's accelerators are registered with and consumed by the **compositor**; a client-side keymap with per-context overrides would be a second, disagreeing source of truth |
| 45's step 2, and what an editor scope derives from | The editor's question, on the editor's schedule. Trinix has no opinion and would rather not be cited in it |
| The catalogues, the plural engine, the message formatter, the pseudo-locale, the string checker | Trinix's, by its own scope rule, and costed there. **.NET has no plural API with or without ICU**, so this is real work and it is not Vixen's |
| The AT-SPI2 bridge, its cache, its conformance probe | Trinix's — 2.0 EM, already budgeted. A2 asks for the tree only |
| CSS Grid; variable-height virtualisation | Real Trinix exposures and both already have homes — [43](43-web-styling-parity.md) and doc 09's control library. Not this document's, and repeating them here would only blur it |
| Any control, layout mode, styling feature or text capability | The rule above. If Trinix ever needs one it arrives as a pull request against Vixen, on Vixen's terms |

**Adjacent, and deliberately not folded in.** Two text-stack seams were found by the same localisation
audit and verified here: `ParagraphDirection` has **zero references outside `Vixen.Ui.Text`** — so an
element styled `direction: rtl` whose text begins with a Latin word still gets base level 0 — and bidi
reordering does not cross a font-fallback span, because `FontRegistry.Cover` splits by coverage before
shaping and `TextRun` carries no level. Both are real, both are upstream, and both belong to doc 09
§ Text rather than to a document about application-framework machinery. Neither blocks an
English-language 1.0, and neither is counted below.

---

## Part 4 — Effort

| Ask | EM | How much to trust it |
|---|---|---|
| **A1** — 45 steps 3 and 5 | **0.5** | Good. 45 costed all five steps at 1.5 EM and re-estimated downward after step 1 came in at a day; steps 3 and 5 are the two it calls unaffected by the design question that blocked step 2 |
| **A2** — the accessibility tree | **2.0** | ⚠ **Poor, and it is the largest number here.** Greenfield rather than a move: no code exists to extend and no test exists to keep passing. Range 1.5–3.0, and the spread is almost entirely the per-control population across ~40 controls and 11 advanced ones. It is also the figure most improved by batching with A1 |
| **A3a** — promote the catalogue, split the YAML off, make the lookup a signal | **0.4** | Good. It is a file move, a dependency split and one field becoming a `Signal<T>` |
| **A3b** — the twelve control literals through the catalogue | **0.1** | Good. Twelve call sites and twelve declarations |
| **A3c** — `Strings.Resource` | **0.5** | Fair, and **not asked for** — carried so the total is honest if Vixen chooses to close its own owed row |
| **A4** — the dialog service | **0.25** | Good. A move plus generalising `Pump` onto the document's frame |
| **Total, asked for (A1 + A2 + A3a + A3b + A4)** | **3.25** | |
| Total including A3c | 3.75 | |

⚠ **This is an outside estimate and is labelled as one.** It was made by somebody who has read this
code and written none of it, who does not know this repository's review cadence, its `PublicAPI`
baseline overhead, or how long a `CheckArchitecture` argument takes to settle. Every figure is what the
work looks like from the consuming side, which is the side that systematically underestimates. The
correct amount of trust to place in the table is the amount 45 placed in its own after one step met the
code: it found step 1 was a day rather than a fortnight, and steps 2 and 4 were not what it had
described at all.

**Sequencing, which is worth more than the totals.** A1 and A2 share one traversal of both control
assemblies and one coalesced-invalidation design. Done together they are cheaper than 0.5 + 2.0; done
six months apart they are dearer, and the second one has to re-argue an API baseline over types the
first one just settled. A3 is independent and small, and its signal line gets structurally more
expensive the moment the type is public.

**Trinix's deadlines**, stated as facts about Trinix rather than as pressure: A3 is wanted by its
Phase 7, when the SDK and the theme are written and before the first application exists; A2 by its
Phase 9, after which its own risk register says it will not exist. A1 has no deadline and is simply
cheapest before twenty menu bars are written by hand.

---

## Why this is Vixen's finding and not a favour

[36](36-an-extensible-editor.md) argued that a built-in wired through the back door means the front
door was never proved, and it proved that about *plugins* by counting how many of fifteen things a
plugin needs to reach it could actually reach. [45](45-commands-and-focus-scope.md) found the same
shape one level out and called Trinix a second witness.

This document is what happens when somebody walks all the way around the building. **41 % of the
editor's shell assembly is machinery with no editor in it, and there is a sixth thing that is not in the
wrong assembly because it was never written at all.** This directory's own introduction says the
editor is *"the primary proof that the framework is general-purpose"*. The editor proves that the
framework can carry an
application; it cannot prove that the framework *offers* one, because the editor is on the inside of
every door.

The cheap moment is now, and it is cheap for one reason that will not come again: **Trinix's twenty
applications have not been written.** Every ask above is a file move, a signal, a binding or a promise
doc 09 already made to itself. After the applications exist, every one of them is a migration.

## Sources

Every claim in this document was read in the tree at `dc9608ab`. The line counts are `wc -l`; the
absences are `grep` over `Core/`, `Platform/` and `Editor/` excluding `obj/` and `bin/`; the protocol
and compositor citations are from Trinix's `trinix-menu-v1.xml` and `src/Trinix.Compositor/MenuBar.cs`
and are quoted rather than paraphrased. Where a sentence is judgement rather than measurement it is
marked in place.

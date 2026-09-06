# 49 — The Responder Chain, and Parity with AppKit and SwiftUI

⚠️ **Extends [09](09-ui-framework.md), amends [45](45-commands-and-focus-scope.md) § G2 and
[46](46-what-an-application-needs.md) § Part 1, and is the sibling of [43](43-web-styling-parity.md)
on the other axis.** 43 measures the *styling* language against the web. This measures the
*interaction* model against AppKit, and the *capability* set against AppKit and SwiftUI together.

**The authoring language is not in question.** `.vxml` + `.vcss` is the authoring path and stays the
authoring path; nothing below proposes a Swift-shaped DSL, `@ViewBuilder`, or view structs. Where
SwiftUI appears it is as a *capability* target — the list of things a modern toolkit lets an author
say — never as a syntax to copy. Where AppKit appears it is because `NSResponder` is the correct
model for a first responder and this document adopts it deliberately.

Every number below was measured against the tree at the time of writing, by grepping for **callers**
rather than for types. Where a conclusion is judgement it says so, and where a claim in an existing
document or code comment turned out to be wrong it is named in § Part 9 rather than quietly dropped.

---

## Part 0 — The finding, in one sentence

> **The responder chain is well designed, better than AppKit in six specific ways, and has never
> decided anything at runtime, because nothing has ever registered a responder.**

That is the whole document in miniature, and it is a sharper defect than "the architecture is bad".
`Core/Vixen.Ui/Commands.cs` is 834 lines of carefully-reasoned routing whose defining rule — *the
nearest responder that answers wins, all the way out* (`Commands.cs:387-392`) — is unfalsifiable in
production, because the element leg of the walk always finds nothing and falls through to a single
flat table.

The measurement:

| API | Purpose | Production callers |
|---|---|---|
| `UiElement.AddCommandHandler` (`Commands.cs:721`) | how an element *becomes* a responder | **0** |
| `UiElement.RemoveCommandHandler` (`Commands.cs:749`) | — | **0** |
| `UiElement.CommandScope` (`Commands.cs:636`) | the derived scope 45 § G2 was written to build | **0** |
| `CommandRoute.ScopeOf` (`Commands.cs:371`) | reads it | **0** |
| `UiDocument.CommandResponder` (`Commands.cs:474`) | `NSDocument`'s slot | **0** |
| `UiElement.AccessKey` (`UiElement.cs:673`) | Alt-mnemonics | **0** |
| `UiDocument.MoveFocus(NavigationDirection)` (`Navigation.cs:48`) | arrow/D-pad navigation | **0** |

Outside test projects, the **only** files in the repository that mention `AddCommandHandler`,
`CommandScope` or `AccessKey` are their own definitions plus `Core/Vixen.Ui.Controls/ButtonBase.cs`.
Not one sample, not one application, and — this is the load-bearing part — **not the editor either**.

So `CommandRoute.Resolve` (`Commands.cs:394-416`) in production is: a loop over parents that finds
nothing, followed by one dictionary lookup in `ApplicationCommandResponder`. Every property the
design is *about* is inert.

This is [36](36-an-extensible-editor.md)'s thesis and this repository's standing warning —
*the commonest defect here is a finished thing nothing calls* — applied to the one subsystem that
was supposed to be the proof that the front door works.

---

## Part 1 — Two chains, and why that is the architectural defect

AppKit has **one** chain. The same `NSResponder` that receives `keyDown:` also answers `copy:`,
validates the menu item that would send it, and supplies the `undoManager`. `nextResponder` is the
only link, and every one of those four questions walks it.

Vixen has three, and they do not meet.

| | **A — routed events** | **B — commands** | **C — the editor's keymap** |
|---|---|---|---|
| Entry | `UiDocument.Dispatch(KeyEvent)` `Keyboard.cs:191` | `CommandRoute.Resolve` `Commands.cs:394` | `CommandDispatcher.Pressed` `Editor/Vixen.Editor.Ui/Commands/CommandDispatcher.cs:76` |
| Origin | `Focused` `Focus.cs:36` | `CommandFocus` `Focus.cs:54` | one handler on `Root`, bubble leg (`:57`) |
| Walk | `Parent`, capture → target → bubble (`EventRouter.cs:39-59`) | `Parent`, then two document slots (`Commands.cs:398-414`) | **none** — a flat `KeyMap` → `CommandRegistry` lookup |
| Non-element links | **structurally impossible** — the route is `List<UiElement>` | 2, hard-coded | n/a |
| Honours `IsCommandTransparent` | **no** | yes | no |
| Reaches `IResponder`-like objects | no | yes (`ICommandResponder`, one method) | no |

Four consequences, each independently a defect:

**1.1 — A non-element responder can never see a key.** `EventRouter.Raise` is `UiElement`-typed end
to end (`EventRouter.cs:35-60`); `ICommandResponder` has exactly one member,
`TryGetCommandHandler` (`Commands.cs:38-49`). A document object that owns `edit.copy` cannot also
own ⌘C's *raw* handling, an editing gesture, or a first-responder-only key. In AppKit these are the
same object by construction.

**1.2 — The chain cannot be extended anywhere but its two ends.** The complete extensibility surface
is `UiDocument.CommandResponder` and `UiDocument.ApplicationCommandResponder`. `UiElement`'s virtual
surface is `TagName`, `ContentHost`, `NamedHost`, `OnCreated`, `OnChildAdded`, `OnRemoved`,
`OnPropertyChanged`, `OnDraw` (`UiElement.cs:97,115,143,1639,1686,1718,1730,1775`) — there is no
`OnKeyDown`, no `AcceptsFirstResponder`, no `ValidateCommand`. A view controller, a window
controller, or a document cannot sit *in the middle* of the walk, which is exactly where AppKit puts
all three.

**1.3 — The keyboard bypasses the chain entirely.** `CommandDispatcher.Pressed` resolves a chord
against the flat registry and never calls `CommandRoute` (`CommandDispatcher.cs:85-107`).
`EditorShell.cs:212` admits it in a comment: *nothing in the editor resolved through `CommandRoute`
before*. Combined with Part 0 this means an element-level handler, if anyone ever wrote one, would
be reachable by **clicking a button** and unreachable by **pressing its shortcut** — the two things a
command system exists to make identical.

**1.4 — There is no window level, and no seam for one.** `UiDocument.Focused` is a single
document-global field (`Focus.cs:35`). `Dispatch(KeyEvent)` takes no surface (`Keyboard.cs:191`),
unlike `Dispatch(UiSurface, PointerEvent)` (`UiDocument.cs:1771`) and
`Dispatch(UiSurface, WheelEvent)` (`Hover.cs:75`); `Platform/Vixen.Platform.Ui/PlatformInput.cs:173`
says so outright. With nothing focused, keys land on the **primary** surface's root
(`Keyboard.cs:196` + `Surfaces.cs:27`) — so a keystroke aimed at a torn-off inspector executes
against the main window. `PlatformEventKind.WindowFocusGained`/`Lost` are produced by every backend
(`Platform/Vixen.Platform.Desktop/DesktopPlatform.cs:745,750`) and **dropped on the floor** by the UI
bridge: `PlatformInput.Dispatch` has no arm for either and they fall to `default: return false`
(`PlatformInput.cs:214-216`). There is no `NSApp.keyWindow`, and `IUiWindow` (`UiWindows.cs:52-87`)
has nowhere to put the answer.

**1.5 — A focus change cannot be refused.** `UiDocument.Focus` gates on `element.Focusable` and
nothing else (`Focus.cs:96-98`), writes `Focused` at `:100`, and raises two `FocusEvent`s at
`:120-121` — *after* the change is committed. `UiEvent` has no `Cancel` (`UiEvent.cs:46-58`). So
AppKit's canonical validation pattern — a field refusing to resign first responder while its value is
invalid — is not expressible. The nearest available thing, pre-emptively setting `Focusable = false`,
is a different rule and does not know where the focus is going.

Minor, in the same file: `Focus(null)` returns `false` on success (`Focus.cs:99,127`), so a caller
cannot tell "cleared the focus" from "refused"; and `TabOrder.Collect` tests only `Focusable`
(`Focus.cs:227-235`), so a `display: none` control is a tab stop — while its two sibling traversals,
`AccessKeys.cs:158` and `Navigation.cs:107`, both skip zero-box subtrees. Three walks, two of which
agree.

---

## Part 2 — What to keep, stated first

The design is not the problem, and a rewrite that discarded these would be a regression. Each is a
place where Vixen is **ahead of AppKit**, and each survives Part 3 unchanged.

1. **The first responder's `CanExecute` is the only one asked** (`Commands.cs:423-424,436-443`).
   AppKit's `validateUserInterfaceItem:` is famously ambiguous about whether a later responder gets a
   say. Vixen states it once, in one place, and names it as the invariant.
2. **`IsCommandTransparent` as data rather than as a private event loop** (`Commands.cs:669-693`,
   `Focus.cs:104`). AppKit gets "a menu is not in the responder chain" for free because a menu is not
   a view; Vixen gets it declaratively for menus, menu bars, palettes **and toolbar buttons**
   (`ButtonBase.cs:82`) — and AppKit has no answer at all for the toolbar case.
3. **A real capture phase over a snapshotted route** (`EventRouter.cs:39-42`). AppKit has no capture
   phase, and walks `nextResponder` live.
4. **Pointer fall-through is free.** Hit test → target → bubble (`UiDocument.cs:1784,1791`). In
   AppKit every `mouseDown:` that wants to pass the event on must remember to call `super`.
5. **`Defocus`** (`Focus.cs:139-180`): a press that lands on nothing focusable clears the focus, on
   the whole ancestor chain, with a pointer-capture exemption. AppKit has no rule for this and every
   AppKit application writes it by hand or ships without it.
6. **Coalesced command invalidation** (`Commands.cs:593,603-611`, raised from `UiDocument.cs:1204`,
   consumed by `ButtonBase.cs:100-108`). AppKit's answer is polling `validateUserInterfaceItem:` per
   item per menu-open plus an `NSToolbar` revalidation timer. Vixen's is strictly better.
7. **`CommandFocus` surviving a menu close** (`Focus.cs:39-50`). AppKit gets this free from a nested
   event loop; Vixen gets it without one, which is the harder problem.
8. **Key position and typed text as distinct events, with IME as a third** (`Keyboard.cs:49-61,117-152`).

⚠ **The bubble-leg keymap is right and AppKit is wrong.** AppKit runs `performKeyEquivalent:` *before*
`keyDown:`, so a global chord outranks the focused control. Doc 45 records the opposite choice — one
handler on the root's **bubble** leg, so a control that wanted the key has already had it — plus the
guard that refuses an unmodified chord while a `TextField` is in the focus path. That ordering is
kept below and is the one deliberate divergence from `NSResponder` in this document.

---

## Part 3 — The design: one chain, derived, extensible at the links

### 3.1 `IResponder`

```
public interface IResponder {
    bool TryGetCommandHandler(string id, out CommandHandler handler);   // exists today
    bool OnKey(KeyEvent args) => false;                                 // new: doCommandBySelector's seam
    IUndoManager? UndoManager => null;                                  // new: NSResponder.undoManager
}
```

`ICommandResponder` becomes `IResponder` with two defaulted members, so every existing implementation
compiles unchanged and `CommandResponder` (`Commands.cs:237`) keeps its table.

### 3.2 The walk is structural, and insertion is additive

⚠ **`nextResponder` is deliberately *not* copied as a settable pointer.** A mutable next-link is the
source of AppKit's worst chain bugs: a responder inserted and never removed, a chain that outlives
the view it was spliced behind, and a link that can cut the application out of the walk entirely.

Instead: the walk stays derived from `Parent`, and an element may **append** responders *at its own
position*.

```
UiElement.Responders          // IList<IResponder>, empty by default, ordered
```

The full walk becomes, from `CommandRoute.Origin(document)` outwards:

> element → *that element's `Responders`, in order* → `Parent` → … → `Root` → `Root.Responders` →
> `UiDocument.CommandResponder` → `UiDocument.ApplicationCommandResponder`

A view-model, a window controller and a document object all reach the chain by being appended to the
element that owns them, and the invariant `Commands.cs:387-392` states — *nearer wins, all the way
out* — is unchanged and now has something to decide between. Nothing can rewrite the walk, so a
responder can never orphan the root.

**Lifetime keeps the rule `Commands.cs` already established**: the element holds the responder and
never the reverse; `IResponder` has no back-reference and no event; removing the element drops the
list. `A_long_lived_responder_does_not_keep_a_closed_document_alive` extends to the element case.

### 3.3 One tail for both chains

`UiDocument.Dispatch(KeyEvent)` keeps capture → target → bubble over elements — that is better than
AppKit and `.vxml`'s `on:` handlers depend on it. What changes is what happens **after** the bubble
leg and before the Tab fallback:

```
raise (capture/target/bubble over elements)
  ↓ unhandled
walk the responder tail: Responders of each element on the route, then document, then application
  ↓ unhandled
access keys  →  Tab                       (unchanged, Keyboard.cs:203-216)
```

So the *same* objects answer `edit.copy` and see the raw key, which is the property that makes
`NSResponder` one thing rather than four. `IsCommandTransparent` is honoured on the tail (a menu is
not a place) and not on the element legs (a menu must still receive arrow keys) — the split that
exists today, now stated once.

### 3.4 First responder: acceptance, and refusal

- **`UiElement.AcceptsFirstResponder`** — already expressible as `Focusable` + `TabIndex = -1`, and
  already used by `Select`, `Tabs` and the text inputs. No new API; § 45 was right.
- **`becomeFirstResponder` / `resignFirstResponder`** — `FocusEvent` gains a `Cancel`, and
  `UiDocument.Focus` asks the outgoing element **before** writing `Focused`:

  ```
  previous.Raise(new FocusEvent { Gained = false, … })   // may set Cancel
  if (cancelled) return false;
  Focused = element;                                     // then commit
  ```

  ⚠ **The veto is asked on a user-initiated move only** — never on removal (`UiDocument.Release`,
  `UiDocument.cs:757`), never on document teardown, never on `Defocus` from a press that captured the
  pointer. A refusal that could survive its own element being deleted is how an application becomes
  permanently unfocusable, which is the failure mode this feature has in every framework that ships
  it. `UiDocument.Focus(element, force: true)` is the escape hatch the shutdown paths use.

- **`Focus(null)`** starts returning `true` when it clears the focus. The current `false`-on-success
  (`Focus.cs:99,127`) is indistinguishable from a refusal and will be a bug the moment a refusal
  exists.

### 3.5 The window level

- `UiSurface` gains `Focused` and `UiDocument` gains `KeySurface`; `Focused` becomes
  `KeySurface?.Focused`, so every existing call site keeps working and a multi-window application
  stops routing into the primary window.
- `Dispatch(KeyEvent)` takes a `UiSurface`, matching the pointer and wheel overloads.
- `PlatformInput.Dispatch` grows arms for `WindowFocusGained`/`WindowFocusLost`
  (`PlatformInput.cs:214`) — the events already exist and are already produced on every backend.
- `IUiWindow` gains `IsKey` and `DidBecomeKey`.

⚠ **No `acceptsFirstMouse`.** Every first click stays a normal click. That is a decision rather than
an omission and this is where it is written down: the AppKit behaviour exists because a click that
activates a window is ambiguous, and a UI framework that draws its own windows can afford the simpler
rule. Revisit only if a real application complains.

### 3.6 The rule that makes it real

⚠ **None of the above is worth building unless something registers a responder**, and the tree's
history says a mechanism with no callers stays that way. So § 3 is *complete* only when all four of
these hold, and each is a gate in Part 10:

1. `TextField` and `CodeEditor` register `edit.cut/copy/paste/select-all/undo/redo` as element
   command handlers.
2. The editor's `CommandDispatcher` resolves through `CommandRoute` instead of the flat registry.
3. `EditorShell.Context` — a mutable string pushed by hand from pointer handlers in ten places
   (`EditorApplication.cs:1989,2076,2155,2267`; `EditorParity.cs:560,1203-1205,1770`;
   `EditorWorlds.cs:118`) — is deleted in favour of `CommandScope`, which was built to replace it and
   is assigned only in tests.
4. `Samples/02-HelloUi` has at least one panel whose Copy means something different from the shell's.

---

## Part 4 — What the chain carries

In AppKit these are not separate features; they are what having a chain *is for*. Each is absent
here, and each becomes cheap once Part 3 exists.

### 4.1 Undo, found rather than owned

**Confirmed still true**: there is no undo below `Editor/Vixen.Editor.Core/CommandStack.cs`.
`git grep "IUndo\|UndoManager"` returns zero hits repository-wide. Doc 46 § Part 1 row 4 stands.

`CodeBuffer.cs:49` argues correctly that a text control must not own an undo stack, and its seam —
`Changed` — has a real consumer in `Editor/Vixen.Editor.AssetEditors/Code/CodeDocument.cs:27,86-93,258`.
The argument does not transfer to `TextField`, which has no stack and no seam, so **a dialog's text
box has no ⌘Z in any Vixen application including the editor**.

The AppKit answer resolves this exactly: `NSResponder.undoManager` walks the chain, so the control
*finds* a manager rather than owning one. `IUndoManager` lands in `Vixen.Ui`; `CommandStack` becomes
one implementation of it; a control registers its edit with the nearest manager on the chain and gets
nothing when there is none — which is the correct behaviour for a throwaway field in a dialog with no
document behind it.

### 4.2 Editing commands as a table, not two switch statements

`TextField.cs:1072-1152` and `CodeEditor.cs:1376-1441` are two independently hand-maintained
`switch (args.Key)` blocks over the same vocabulary. They have already diverged: `TextField` treats
Ctrl **or** Cmd as the word modifier with a comment saying it cannot know which platform it is on
(`TextField.cs:1067-1070`), while `CodeEditor` tests `Control` only (`:1375`) — so ⌥←/⌘← do nothing
in the code editor on macOS.

AppKit's `doCommandBySelector:` is the fix, and it buys three things at once: rebindable keys,
per-platform default tables, and the macOS emacs bindings (⌃A/⌃E/⌃K/⌃Y) that are currently absent
entirely. `EditingCommands` maps a chord to a semantic id (`text.move-word-left`,
`text.delete-to-line-start`, `text.transpose`), one table per platform, and both controls register
handlers for the ids rather than for the keys.

⚠ **`CodeEditor` needs the semantic ids anyway** for an unrelated reason: `CodeBuffer.WordStart` /
`WordEnd` (`CodeBuffer.cs:233-245`) are `char.IsWhiteSpace`-based, so ⌃← in Japanese or Thai jumps a
whole clause, while `TextField` gets it right through `WordBreaker` (UAX #29, `TextField.cs:351`).
One table, one word-breaker.

### 4.3 The clipboard, which exists and is untouched

`IClipboard` (`Core/Vixen.Platform/IClipboard.cs:32`) is a good multi-flavour abstraction —
`SetText`, `SetImage`, `SetData(format, span)`, `TryGetData` — with real native backends
(`Platform/Vixen.Platform.MacOS/MacOSClipboard.cs:206` goes to `generalPasteboard`, plus Windows and
Linux). **Nothing above `Vixen.Platform` calls it.** The editor's `PropertyClipboard` and
`NodeGraphClipboard` are in-process object stores that never reach the OS pasteboard.

⌘C in a Vixen text box does nothing, in every application, today.

⚠ **And an application cannot fix this itself**, because `UiApplication` exposes `Window`
(`Platform/Vixen.Ui.Desktop/UiApplication.cs:324`) and keeps `platform` private (`:110`). One missing
property is what makes the clipboard, the native dialogs, the displays and the lifecycle all
unreachable from `UiApplication.Run(options)`.

### 4.4 Key equivalents in `Vixen.Ui`

`MenuItem.ShowShortcut` (`Core/Vixen.Ui.Controls/Menus.cs:103`) *draws* "⌘S" and nothing dispatches
it. Every binding lives in `Editor/Vixen.Editor.Ui/Commands/`, measured at **1 730 lines**, of which
roughly **1 475 are application-generic**:

| File | Lines | Generic? |
|---|---|---|
| `KeyMap.cs` | 520 | **Yes** — bind/rebind, conflict detection, defaults→preset→user, YAML round-trip |
| `KeyChord.cs` | 312 | **Yes** — parse/format, `ForPlatform()` Ctrl↔⌘ |
| `CommandRegistry.cs` | 264 | **Yes** — the table plus an `ICommandResponder` adapter |
| `KeyMapPreset.cs` | 255 | Mechanism yes; **data editor-specific** (`:212-254` binds `scene.translate`) |
| `EditorCommand.cs` | 193 | **Yes** except `StringId` conventions |
| `CommandDispatcher.cs` | 131 | Yes **except** `element is TextField` at `:124` |
| `KeyBindingsView.cs` + `.vxml` | 55 | **Yes** |

⚠ `CommandDispatcher.Available`'s `element is TextField` (`:124`) is the editor asking a
controls-library type a question that should be a responder's own answer. It becomes
`IResponder.WantsRawKeys` — the AppKit question "is the first responder a field editor" — the moment
Part 3 lands.

---

## Part 5 — The application layer

`UiApplicationOptions` describes **one** window with one `Content` component
(`docs/guide/ui/desktop-application.md:60-100`). AppKit's `NSApplication`/`NSWindow`/`NSDocument` and
SwiftUI's `App`/`Scene`/`WindowGroup`/`DocumentGroup` all start one level above that.

| Capability | Vixen today | Verdict |
|---|---|---|
| Second top-level window | `UiDocument.CreateSurface` `Surfaces.cs:70`; `IUiWindowHost.Open` `UiWindows.cs:106` | **present** |
| One document across windows (shared style, focus, cross-window drag) | `UiSurface.cs:9-46`, `Reparent.cs:42` | **present, ahead of AppKit** |
| Key window | — | **absent** (§ 1.4) |
| Native menu bar | `MenuBar : Control` `Menus.cs:633`, drawn; **no seam interface exists** | **absent** |
| System menu items (About, Services, Hide, Quit, Window, Help) | — | **absent** |
| Toolbar | no type in either controls assembly; the editor's is a drawn strip (`Menus/ToolbarPresenter.cs:51`) | **absent** |
| Clipboard from a control | § 4.3 | **present-but-unwired** |
| OS drag-in (files from Finder/Explorer) | `DropFile`/`DropText` produced (`DesktopPlatform.cs:664`, `WebPlatform.cs:541`) and **dropped** (`PlatformInput.cs:214`) | **present-but-unwired** |
| In-app drop model | no `DataObject`, no `IDropTarget`, no `AllowDrop`. `TreeView.cs:247` and `AssetFieldDrop.cs:22-27` each hit-test by hand | **absent** |
| Native open/save panels | `INativeDialogs` complete with six backends; one consumer, in `Editor/Vixen.Editor.App/EditorServices.cs:39`. ⚠ the SDL fallback returns `null` (`DesktopServices.cs:211-241`) | **present-but-unwired** |
| Recent documents | zero occurrences | **absent** |
| Answerable modal | `DialogService.cs` (465 lines) — doc 46 § A4 landed and the editor's copy is gone | **present** |
| Sheets (window-attached modals) | `runModal` on purpose (`MacOSDialogs.cs:20-23`) | **absent by decision** |
| Document model (dirty, save, revert, proxy title) | `EditorDocument.cs:50,125,224` — one assembly no application can reference | **absent below the editor** |
| Quit / close with unsaved changes | ⚠ `UiApplication.Pump` sets `running = false` outright (`UiApplication.cs:548-566`). `ILifecycle.CancelQuit` exists (`ILifecycle.cs:91`) and `EditorHost.cs:401-427` uses it correctly — the framework host is the copy that still has the bug | **absent** |
| Activation / deactivation into the UI | produced; consumed only by the game host (`Core/Vixen.App.Hosting/PlatformInput.cs:109`) | **present-but-unwired** |
| Reopen, Settings/preferences scene, status item, services, printing | — | **absent** |
| Window placement autosave | editor-only (`Docking/EditorUserStore.cs:131`) | **absent** |
| Background tasks and progress | `BackgroundTask`/`BackgroundTaskManager` in Core, **pumped** by `UiApplication.cs:507` | **present** (no Core UI for it) |
| DPI, per-monitor rescale, colour gamut | `UiSurface.cs:101`, `Surfaces.cs:159`, `UiWindowSurface.cs:257` | **present and fed** |

**The pattern has moved since doc 46, and it is worth naming precisely.** 46 found five things in the
*wrong assembly*; four of the five are now fixed. What replaces that defect is a different one:
`IClipboard`, `INativeDialogs`, `DropFile`/`DropText`, `MediaContext.ColorScheme`,
`ILifecycle.CancelQuit` and `WindowFocusGained` are all **finished, tested, cross-platform
implementations in `Core/Vixen.Platform` with no consumer above it** — and
`Platform/Vixen.Platform.Ui/PlatformInput.cs` plus `Platform/Vixen.Ui.Desktop/UiApplication.cs` are
the two files where every one of those wires would terminate.

⚠ **The native menu bar is the one item on this list that is a design question rather than a wiring
job.** There is no interface in `Core/Vixen.Ui` to fill — `UiWindows.cs` is the only host seam that
exists — and the decision interacts with the golden-image discipline doc 46 records: a drawn menu can
be screenshotted and driven headless, an `NSMenu` cannot. The recommendation is a seam
(`IUiMenuHost`) with the drawn `MenuBar` as the default implementation and a native implementation
per platform, so the test suite keeps the drawn one and a shipped macOS application gets ⌘Q, About
and the Window menu.

---

## Part 6 — Markup parity: the capabilities, not the syntax

`.vxml` is the authoring path. The question is which *capabilities* SwiftUI's modifier and data-flow
system provides that the markup cannot express — and the answer is concentrated in three places.

### 6.1 There is no ambient/inherited value (SwiftUI `Environment`)

Checked hard. The framework has exactly three ancestor-walking mechanisms and none is general:
`[UiProperty(Inherits = true)]` (`UiProperty.cs:35`), whose only producers in the whole tree are in
`Core/Vixen.Ui.Tests/SampleElements.cs`; `EffectiveCommandScope` (`Commands.cs:695`), whose value is
one `string?`; and `UiDocument.Mounted` (`UiDocument.cs:279`), which records a component for an
element and offers no "nearest ancestor of type T" query.

So every cross-cutting value is threaded through props by hand. `Samples/02-HelloUi/Shell.vxml:69-83`
repeats `Model="@Model"` on three panels and `:126-135` wires two callbacks in `OnComposed` for want
of a channel. Multiply by an editor with forty panels. This is the single biggest markup gap, and it
compounds with § 6.2.

**Proposal**: `Provide` / `Inject` on `BuildContext`, keyed by type, resolved by walking `Parent` and
reading the element's provided table — the same walk § 3.2 adds for responders, and worth sharing one
implementation with it. Markup spelling: `<provide value="@theme" />` and a typed `@inject` in
`@code`.

### 6.2 ⚠ Component props are assigned *after* `Build` runs

`BuildContext.Child<T>` constructs, mounts (runs `Build`), and *then* assigns parameters
(`BuildContext.cs:510`, `ComponentEmitter.cs:657`). So every effect has already read the property once
at its default, and **a plain C# property used as a component prop silently never updates**.
`Samples/02-HelloUi/Shell.vxml:105-110` documents the trap; nothing enforces it, and there is no
diagnostic. Either assign parameters before mounting, or emit a `VXML2xxx` for a non-signal-backed
public property used as a parameter. The second is cheaper and catches the case the first cannot.

### 6.3 ⚠ On a lowercase tag, every non-directive attribute is inert

`EmitAttribute` (`ComponentEmitter.cs:657-670`) splits on the tag's case: a capitalised tag gets a
real, Roslyn-typechecked property assignment; a lowercase tag gets
`Styles.Tree.SetAttribute(...)` (`BuildContext.cs:711`) — data a selector can match and nothing
reads. So `<div AccessibleName="Save" Focusable="true">` compiles, matches `[AccessibleName]`, and
does nothing. No diagnostic. This is the same defect class the language already fixed twice, for
`style=` and for `slot=` (`VXML2016`).

### 6.4 The modifier table

| SwiftUI modifier | `.vxml` / `.vcss` today | Verdict |
|---|---|---|
| `.padding`, `.frame`, `.background`, `.shadow`, `.opacity`, `.rotationEffect`, `.scaleEffect` | utility classes / `style=` / real CSS | ✅ full |
| `.overlay` | absolute child + `inset`/`z-index` | ✅ idiom |
| `.animation` / `.transition` (property) | `transition-*`, `@keyframes`, `spring()` | ✅ full |
| `.disabled` | `Disabled="@x"` + `:disabled` | ✅ |
| `.clipShape` | `border-radius` + `overflow: hidden`. ⚠ **`clip-path` is not implemented at all** | ⚠ rects only |
| `.onChange` | `change:Prop` — ⚠ `[UiProperty]` on elements only; **cannot be used on a component tag** (`ComponentEmitter.cs:643-648`) | ⚠ partial |
| `.task` / `.onAppear` | `partial void OnComposed()` — sync, no cancellation, no async | ⚠ partial |
| `.focusable` | real on component tags; inert on `<div>` (§ 6.3) | ⚠ partial |
| `.accessibilityLabel` | `AccessibleName=` compiles on a capitalised tag, is inert on a lowercase one; **zero `.vxml` in the repo sets it either way** | ⚠ accidental |
| `.keyboardShortcut` | ❌ `Shell.vxml:120-122` says it outright: *a keyboard shortcut is a method call with two enum arguments and no attribute spelling* | ❌ |
| `.contextMenu` | `ContextMenu.Attach` is a C# call | ❌ |
| `.help` (tooltip) | `Tooltip.Attach` is a C# call | ❌ |
| `.alert` / `.confirmationDialog` / `.sheet` / `.popover` | `DialogService`/`Overlay` exist; nothing binds a presentation to state | ❌ markup |
| `.searchable`, `.refreshable` | ❌ nothing | ❌ |

⚠ **`.searchable`'s middle third is not missing, which narrows [#767](https://github.com/Rikarin/Vixen/issues/767).**
Two audits called "what does it filter" the sharpest open question, on the grounds that a framework
cannot know what matching means for an arbitrary `@for` sequence. It does not have to: a `SearchBox`
bound to a signal and an `@for` whose sequence expression reads that signal narrows as it is typed
into, with the predicate staying the author's `Where(...)`.
`Core/Vixen.Ui.Controls.Tests/Markup/SearchableSheet.vxml` is seven lines of that and
`SearchableReachTests` asserts it on the *rows*. What `.searchable` would add is the other two
thirds — where the field goes, and an empty state, which is genuinely absent: an `@for` has no
fallback arm, so a filter that matches nothing leaves a list that is empty and silent. Filed as
[#908](https://github.com/Rikarin/Vixen/issues/908).
| `.draggable` / `.dropDestination` | `on:dragstart/drag/dragend` exist; **no drop target, no payload type, no `AllowDrop`** | ⚠ half |

For a project whose thesis is *markup is the authoring path*, that ❌ column is the parity claim's
weakest evidence, and closing it is mostly attribute spellings over APIs that already exist.

⚠ **The `.keyboardShortcut` row is right about the sample and was read as a claim about the
framework.** A complete chord system exists — `Editor/Vixen.Editor.Ui/Commands/CommandDispatcher.cs`
attaches to any `UiDocument` (`:55`), builds a platform-adapted `KeyChord` (`:76`), resolves it
against the focused context with a global fallback (`:85`), falls *through* rather than refusing when
the chord belongs elsewhere (`:92`), and marks a disabled command's chord handled so a greyed-out ⌘S
cannot type an `s` (`:98`). What is true is narrower: there is no `.vxml` spelling and no chord table
below `Vixen.Editor.Ui`, which is #650.

The rest are one issue each, because each needs a spelling decided before it can be built: #762 (the
tooltip, which also carries the layering decision — `BuildContext` is in `Vixen.Ui` and `Tooltip` is
in `Vixen.Ui.Controls`, which references it), #763 (the context menu, behind it), #764 (a dialog that
is a function of state, beside the one a command awaits), #766 (an overlay whose open state is bound,
where `IsOpen` is deliberately not a `[UiProperty]`), #767 (`.searchable` / `.refreshable`, the two
with no API behind them), #768 (the async arrival hook).

⚠ And the `.task` row's substrate is a finished thing nothing calls: `AsyncComputed<TRequest, T>` has
fourteen references in the committed tree, ten of them in `AsyncComputedTests.cs`, two in its own
file, and two that are cross-references in `EffectScheduler` and `ReactiveGraph`. No production
caller.

### 6.5 Lists

`BuildContext.For` (`BuildContext.cs:1359`) builds a region per item over the whole sequence.
`VirtualizingPanel`/`VirtualizingGrid` are C# controls fed by delegates, reachable from markup only
through `use=`. `BoundFor` is `(Variable, Sequence, Key, Body)` — **no index, no sections, no
grouping** (`BoundNodes.cs:262`). `Region.Clear()` removes synchronously (`Region.cs:143`), so there
is **no enter/exit transition** even though the animator is real. SwiftUI gets all four free, and the
absence of the first is what makes a 10 000-row panel fall back to hand-written C#.

⚠ **"No index" reads as an omission and is a refusal**, which is the correction that changes what
gets built. `For` matches a key, keeps that item's region and does not re-run the body — so a name
bound to the item's position would be captured once and be a lie after the first reorder. An index
that behaves is a per-row **signal** the reconciler writes when it repositions, which is a different
feature from the one the spelling suggests; `docs/guide/ui/markup-panels.md` and
`Core/Vixen.Ui.Markup/README.md` both carry the trap.

The four are one issue each, because each is its own design piece and none of them is blocked on the
others: #758 (a markup spelling for the virtualizing controls), #759 (the index, as a signal),
#760 (sections, and whether a nested `@for` is already the answer), #761 (deferring `Region.Clear`
so anything can animate out). The LIS reorder is #178 / #56.

### 6.6 `bind:` is too narrow to be used, and the repo proves it

`ctx.TwoWay` requires an **lvalue of the property's exact type** with no converter and no coercion
(`ComponentEmitter.cs:629-630`, `BuildContext.cs:942-974`). Nested properties and settable indexers
work; expressions, method calls and conversions do not. Across every committed `.vxml`: **8 `bind:`**
against 26 `change:` and 239 `ref`, and **all eight `bind:` attributes are in one file** —
`Samples/02-HelloUi/Panels/Gallery.vxml`. Two-way binding is nominally present and practically absent.
(Those three numbers are the audit's, kept as written; the first correction below recounts them.)

Four corrections to the paragraph above, from #663 and `BindReachTests`:

- ⚠ **The measurement has moved twice and the conclusion has hardened.** Recounted over the 83
  committed `.vxml` at this writing: **13 `bind:` attributes in two files** —
  `Samples/02-HelloUi/Panels/Gallery.vxml` (10) and `Core/Vixen.Ui.Controls.Tests/Markup/BindReachSheet.vxml`
  (3), a sample and a test fixture — against **26 `change:` attributes in thirteen files** and
  **274 `ref`/`refs`**. So the earlier figures (8 / 26 / 239, and before that 7 / 29 / 281) are both
  stale, and the four editor files that a `bind:` grep now hits are hits *in comments* explaining
  that `change:` is `bind:`'s write-back leg. What the recount says is stronger than the original
  claim: `change:` has doubled and spread across the editor while `bind:` has not left the two files
  it was written in, and **no product `.vxml` in the repository binds anything two-way.**

- **`string?` against `string` is not a mismatch.** Nullable annotations on a *reference* type are
  erased, so `typeof(string?)` is `typeof(string)` and the exact-type check never sees them. `int?`
  against `int` is two types and that half is real.
- **A mismatch used to do nothing at all**, which is the best available explanation for the eight.
  Both legs box through `UiPropertyKey`, the unbox is exact, and the forward leg is an `Effect` —
  which catches, suspends and logs rather than propagating. `TwoWay` now refuses a mismatch at
  compose, naming both types.
- ⚠ **The narrowness the section does not name: a model that is not reactive gets one forward write
  and never another.** Nested paths and indexers do bind, and every `bind:` in the tree binds
  `Something.Value` on a `Signal<T>` — so the forward effect has a dependency. Over a plain property
  it has none, the write-back still works, and the result is a half-live binding that fails in the
  direction an author tests second.
- ⚠ **That half-live binding now reports itself, and the mechanism was already in the graph with
  `internal` on it.** `ReactiveNode.DependencyCount` existed "for tests and diagnostics" and nothing
  outside the assembly could ask the question it answers. `TwoWay` runs the bound expression once
  under a `Computed` before it makes the effect, and a count of zero is a forward leg nothing can
  ever wake — logged as `7008` with the element and property named, rather than refused, because
  unlike a type mismatch this half-works and is sometimes what its author meant. The remaining two
  items below stay design calls; this one was not one.

### 6.7 Smaller, but each is a real edge

`<slot>` has **no fallback content** (`VXML2017`) and no dynamic name (`VXML2018`); an `@inherits`
component gets one slot only (`VXML2012`); there is no first-class component *event*, only a callback
prop; `AsyncComputed` and `LinkedSignal` (`Core/Vixen.Ui.Reactive/AsyncComputed.cs:56`,
`LinkedSignal.cs:24`) have no producers or consumers outside their own tests — and `AsyncComputed` is
exactly the substrate `.task`/`.refreshable` would need; and `Core/Vixen.Ui.Markup/README.md:44`
documents a `[Parameter]` attribute that **does not exist in the tree**.

---

## Part 7 — Controls, text and presentation

### 7.1 Controls: what is missing, ranked

Measured against `PublicAPI.Unshipped.txt` in both control assemblies (87 and 86 public types).

**Absent entirely**, in the order an application hits them: **Toolbar** (the editor's is a bare
`UiElement` with a tag name, `ToolbarPresenter.cs:147`); **SegmentedControl**; **SplitView** — a
draggable two-pane divider exists only welded inside `DockingHost` (`DockingHost.cs:767`), so a
two-pane application must adopt the whole docking model; **Sidebar/source list**; **StatusBar**
(`EditorShell.cs:138`);
**DatePicker**; **secure text field** — zero hits for `secure|password` in the controls assembly, so
any login screen is blocked; **formatted/validated field** — no formatter, no validation seam;
**GroupBox / Form / Section / LabeledContent** — and note that `PropertyField`, the single
most-used tag in the repo's `.vxml` (46 occurrences), lives in
`Editor/Vixen.Editor.Inspector/MarkupBinding.cs:33` and no application can reach it; **Gauge /
LevelIndicator**; **charts**; **PathControl**; **TokenField**; **refresh control**; **ruler**;
**media/PDF/web view**.

**Landed since this section was written**: **Stepper** — ⚠ and the interesting part is that it was
never a control-shaped gap at all. `NumericInput.Nudge` was the whole mechanism and the field's own
summary already claimed "arrows, spinners and a drag", as did the theme's read-only rule; what was
missing was two buttons. `Stepper` is `NumericInput` with them (`TextInputs.cs`), which is why one
press is the field's *proportional* step rather than `Number + Step`. ⚠ The trap it turned up is
general and worth carrying to the next control put inside a field: **a scrub starts on the capture
leg and marks the press handled**, so a nested control's press is swallowed — and not visibly, since
activation comes off the gesture recogniser's tap. `NumericInput.Presses` walks from the source to
the field and declines a scrub when it meets a control on the way; a version of it that only tested
`args.Source` never fires, because what a pointer hits inside a button is the `Icon`.

**Present but with a named gap**: `Button` has no default (Return) or cancel (Esc) key equivalent, no
attached menu (so no pull-down or pop-up button), no repeat-on-hold; `Slider` has no tick marks;
`SearchBox` has no recents menu or scope bar; `ComboBox` has no completion; `Tabs` has no overflow,
close or reorder (`DockingHost` has all three); `ScrollView` has no magnification and no rulers;
`DataGrid` has no column show/hide menu and no layout autosave.

**Exists and nothing calls it** — the standing defect class, found again: `GradientEditor` (+
`GradientBar`, `GradientRail`), `ComboBox`, `Avatar`, `Skeleton`, `Link`, `Badge`, `Drawer` all have
**zero construction sites** outside their own unit tests. `GradientEditor` is named in
`Core/Vixen.Ui.Controls.Advanced/README.md:4` as one of the eleven controls that prove the framework.

**Declared and never set**, still: `Image.SourceBorder`/`HollowCentre` (`Display.cs:245,249`) — which
between them gate the nine-slice branch at `Display.cs:259`, so **that branch is unreachable in
practice**; `Link.Href` (`Display.cs:60`), the one thing that makes a `Link` a link;
`RadialMenu.DeadZone`. Doc 46's original example, `Control.Disabled`, is now genuinely set in eight
places and that row is closed.

**Not writable from `.vxml`**: ⚠ the standing note that `MenuBar`, `DockingHost`, `RadioGroup` and
`Select` need `ref` + `OnComposed` is **stale** — all four are declarative today
(`Samples/02-HelloUi/Shell.vxml:38-83`, `Panels/Gallery.vxml:74-98`). What remains C#-only is every
control whose content model is **not a `UiElement`**: `TreeView` (`TreeNode`), `DataGrid`
(`DataColumn`), `Timeline`, `NodeCanvas`, `CurveEditor`, `GradientEditor`, `CodeEditor`
(`CodeBuffer`), `PropertyGrid`, `ColorPicker`, `Viewport`, and both virtualizing panels. Across all
71 committed `.vxml` files these appear only as self-closing `ref="@x"` tags.

### 7.2 Text

`Core/Vixen.Ui.Text` is a genuine Unicode engine — real HarfBuzz, UAX #9/#14/#29, variable fonts,
bidi wired end-to-end through `TextRun.Level` and reordered across fallback boundaries, and
`direction: rtl` resolved through layout, flex axis mirroring, floats and scrolling. Do not read the
gaps below as a criticism of that layer; they are all in the controls above it.

- ⚠ **`ITextInput.SetCandidateArea` is implemented on every platform and called by nothing**
  (`Core/Vixen.Platform/Input/ITextInput.cs:55`; impls in Desktop/Web/Android/iOS/Headless). So the
  IME candidate window sits at a screen corner. `TextField` computes the caret rectangle already — it
  draws one.
- ⚠ **`ITextInput.Activate` has one caller in the whole repository, the debug console**
  (`Core/Vixen.App.Hosting/VixenApplication.cs:654`). No text control activates text input on focus.
  Desktop works only because SDL leaves it on; a focused `TextField` on web or mobile receives
  nothing.
- **`CodeEditor` cannot be used with an IME at all** — it registers no `TextCompositionEvent`
  handler (`CodeEditor.cs:584-587`), and its geometry is a monospace grid where a column *is* a
  UTF-16 index (`:474-480`), which is false for CJK width, combining marks and surrogate pairs. This
  one is architectural, not a missing handler.
- No colour fonts (no `COLR`/`CPAL`/`sbix`/`CBDT`; outlines are `glyf`+CFF only), so emoji render
  blank.
- No author-controlled attributed runs: an element's text has one style, and runs exist only as the
  itemizer's output. No bold word inside a sentence, no inline link — which also means no way to mark
  up a translated string that needs emphasis.
- No spell check, substitution, dictation or system font/colour panels; no drag-select autoscroll; no
  undo in `TextField` (§ 4.1).

Two performance notes found in passing: `TextField.Step` (`TextField.cs:1198-1204`) allocates a list
and re-runs the grapheme breaker over the **entire value** on every arrow keypress, and
`CodeEditor.RowOf` (`:1070`) is a linear `IndexOf` called per caret move and per draw.

### 7.3 Presentation gaps doc 43 structurally cannot see

Doc 43 measures Tailwind roots. Anything with no class name is invisible to it, and that is where the
expensive items are.

1. ⚠ **No damage tracking.** `DrawListBuilder.Build` (`:527`) reconstructs the entire draw list every
   frame, and `DrawList.cs:463-478` only reports *whether* the frame differs, not where. There is no
   retained per-element surface — which is why `will-change-*` is refused. Combined with
   `UiApplication`'s unconditional frame loop (`docs/guide/ui/desktop-application.md:44-58`, a
   documented decision), this is the difference between an idle editor at 0.5 W and one at 15 W.
   Every other item here is cosmetic beside it.
2. ⚠ **The UI has no white level, so it cannot composite over an HDR scene.** The renderer works in
   cd/m²; UI colours are linear with no scale and there is no paper-white anywhere under
   `Core/Vixen.Ui*`. The editor is safe because the scene arrives as a texture, but a HUD drawn by
   `UiRenderFeature` into an HDR target renders at roughly 1 cd/m² — black. Gamut *is* handled and
   *is* fed (`UiWindowSurface.cs:257`); luminance is not handled at all.
3. ⚠ **`prefers-color-scheme` is built and never fed.** The query works (`MediaQuery.cs:146,151`),
   the property exists per surface (`UiSurface.cs:152`, `Media.cs:80`), and **the only writers in the
   tree are two test files**. No platform assembly reads the OS appearance. The editor hides this by
   using the class-based dark strategy; any *application* ships light-only against a dark system.
   This is doc 43's own finding F11 one axis over — F11 fed width, height, DPI and gamut and left
   this one behind.
4. **No momentum, no rubber-band, no scroll anchoring.** `ScrollView` has smooth programmatic
   scrolling, overscroll chaining and snapping, and zero velocity state. A trackpad flick stops dead
   at the finger. On macOS this is the single most immediate "not a native app" tell.
5. **No `position: sticky` and no `fixed`** — `PositionType` has three values
   (`LayoutEnums.cs:271-279`). Sticky section headers and frozen `DataGrid` header rows are table
   stakes and are currently hand-positioned per control.
6. **No reduced-motion, no forced-colors, no system accent, no semantic colours.** `MediaQuery`
   supports six features and none of them is a preference other than colour scheme. ⚠ Now that
   transitions, keyframes and springs are real *and driven by a clock*, shipping animation with no
   reduced-motion switch is an accessibility regression the animator's own success created.
7. **No OS material/vibrancy.** `UiBackdrop` is a real backdrop-filter — it re-renders the backdrop
   from the draw list rather than reading back — but its root is the top-level group, so a translucent
   sidebar blurs the app's own content and never the desktop.

### 7.4 Accessibility: 898 lines, zero consumers

`Core/Vixen.Ui/Accessibility.cs` is the best-modelled part of this audit: 62 ARIA roles chosen so the
token *is* the enum name lowercased, a three-step accname subset, 17 states computed on every read so
no control can fall out of step, six relations, and coalesced invalidation. The test discipline
around it is unusually good — `AccessibilitySnapshot.Unnamed` is explicitly designed to be
unsatisfiable vacuously, and `AccessibilityCoverageTests` sweeps every public `UiElement` type with
its exemption list held to its own residue.

⚠ **And there is no platform bridge.** Every `AccessibilityInvalidated += ` in the repository is in a
test (`AccessibilityTests.cs:32,176,208`; two `AccessibilityNotificationTests`). Grepping all forty
platform projects for `NSAccessibility`, `IRawElementProvider`, `UIAutomation`, `AtSpi`, `IAccessible`
returns **zero**. No screen reader on any platform can see a single Vixen element. The file's own
docs reason about AT-SPI being a chatty bus protocol — in the future tense, for a bridge author who
does not exist.

This is the exact question the working agreement says to ask: *what does this gate print on the day
it does not run?* It prints a green suite. The tree is correct and is consumed by xUnit.

Adjacent, and cheap: `AccessibleRelation.FlowsTo` has no producers anywhere;
`AccessibleStates.Required` and `.Invalid` have no producers, so no control ever reports a required
or invalid field; there is no live-region *mechanism* (the `Alert`/`Status`/`Log` roles are assigned
by `Toasts.cs:31,114` with nothing to deliver an announcement), no custom actions, and no text-range
protocol — so `CodeEditor` is a textbox a screen reader could not read even with a bridge.

---

## Part 8 — Sequencing

Seven waves. W1 is first because six other items hang off it, and each wave's gate is written to fail
on the day the work did not happen.

| Wave | Contents | Est. | Gate |
|---|---|---|---|
| **W1 — The chain, made real** | `IResponder`; `UiElement.Responders`; one tail for keys and commands; per-surface focus + `KeySurface`; `WindowFocusGained/Lost` wired; the resign veto; `Focus(null)` returns `true`; `TabOrder` skips zero-box subtrees | 1.5 EM | ⚠ An architecture test that **counts production callers** of `AddCommandHandler` and `CommandScope` and fails at zero. Plus a sabotage: delete the element leg of `Resolve` and the editor's scoped Copy must go red |
| **W2 — What the chain carries** | `IUndoManager` in `Vixen.Ui`, `CommandStack` as one implementation; `EditingCommands` table replacing both key switches, with per-platform defaults and the emacs bindings; clipboard cut/copy/paste in `TextField` and `CodeEditor`; `KeyMap`/`KeyChord`/`CommandRegistry` promoted from `Editor/Vixen.Editor.Ui/Commands/` (~1 475 of 1 730 lines); `UiApplication.Platform` | 2.5 EM | ⌘Z, ⌘C and ⌘V asserted in a `Vixen.Ui.Testing` harness with **no editor assembly referenced**; `CommandDispatcher.Available`'s `element is TextField` deleted |
| **W3 — The application layer** | `IUiMenuHost` seam + native menu bar on macOS/Windows; quit/close veto through `ILifecycle.CancelQuit`; `DropFile`/`DropText` wired plus a drop model (`DataObject`, `AllowDrop`, `on:drop`); file dialogs reachable; a document model (dirty, save, revert, proxy title, close prompt); window placement autosave; Settings scene | 3.5 EM | A sample application, not the editor, that opens a file from the OS menu, edits it, is dragged a file, and refuses to quit dirty |
| **W4 — Markup parity** | `Provide`/`Inject` (§ 6.1) sharing W1's walk; props assigned before mount or a diagnostic for the trap; a diagnostic for inert attributes on lowercase tags; spellings for `keyboardShortcut`, `contextMenu`, `help`, `alert`/`sheet`/`popover` bound to state, `searchable`, drop targets; `@for` index + sections + virtualization from markup; enter/exit transitions; slot fallback content | 3 EM | Every ❌ row in § 6.4 has a `.vxml` fixture; `bind:` usage count rises off 7 |
| **W5 — Controls** | Toolbar, SegmentedControl, SplitView (extracted from `DockingHost`), StatusBar, Stepper, secure field, formatter/validation seam, GroupBox/Form/Section, `PropertyField` promoted out of the editor, DatePicker, Gauge; default/cancel buttons; slider ticks; combo completion | 3 EM | The zero-caller list in § 7.1 is empty or each survivor has a written reason; `Samples/02-HelloUi` uses every new control |
| **W6 — Presentation and system integration** | Feed `prefers-color-scheme` from the OS; add `prefers-reduced-motion` and `forced-colors` as features **and** as modes; system accent + semantic colours; momentum and rubber-band scrolling; `position: sticky`; a UI white level for HDR; OS material seam | 3 EM | A media-query test that fails when the OS source is disconnected — the F11 shape, not the F11 omission |
| **W7 — Damage tracking** | A retained per-element surface and a dirty-rect path; `will-change` stops being refused | 2.5 EM | Idle-frame GPU work measured before and after on the same machine at the same moment, per the repo's differential rule |
| **W8 — The accessibility bridge** | `NSAccessibility` first, then UIA, then AT-SPI; text ranges; live-region announcements; custom actions; producers for `Required`/`Invalid`/`FlowsTo` | 3 EM | ⚠ VoiceOver reads a `Samples/02-HelloUi` panel. Nothing short of a real AT counts, because a second in-process consumer would reproduce exactly the defect this wave exists to fix |

**Total ≈ 22 EM**, and that figure is judgement rather than measurement. W1 and W2 together (4 EM) are
what turn the existing 834 lines from a design into a mechanism, and they are the ones with the
highest ratio of unlocked capability to cost.

**Exit criteria**

1. No API in `Core/Vixen.Ui`'s responder, focus or command surface has zero production callers.
2. A keystroke and a menu item reach the same handler by the same walk, asserted in one test.
3. `Vixen.Ui.Testing` can drive cut/copy/paste/undo with no `Editor/` assembly referenced.
4. A two-window application routes a command to the window the user is in.
5. Every ❌ in § 6.4 is either spelled in markup or has a written refusal.
6. A screen reader reads a Vixen window on at least one platform.
7. Each stale claim in Part 9 is corrected in the file that makes it.

**Non-goals.** No SwiftUI-shaped DSL and no second authoring language — `.vxml`/`.vcss` is the path.
No settable `nextResponder` (§ 3.2). No `acceptsFirstMouse` (§ 3.5). No sheets, printing, services
menu or system notifications in this document. No command palette in `Vixen.Ui` — doc 45 put it in
its non-goals and that stands.

---

## Part 8b — The issue register

Filed on `Rikarin/Vixen` 2026-09-05, one `area:` label each plus `doc-audit`, and a defect-class label
only where the signal is specific.

| Wave | Issues |
|---|---|
| **W1 — the chain, made real** | [#642](https://github.com/Rikarin/Vixen/issues/642) the chain has no responders · [#643](https://github.com/Rikarin/Vixen/issues/643) `IResponder`, one chain · [#644](https://github.com/Rikarin/Vixen/issues/644) no key window · [#645](https://github.com/Rikarin/Vixen/issues/645) no focus veto · [#646](https://github.com/Rikarin/Vixen/issues/646) `TabOrder` visits hidden elements |
| **W2 — what the chain carries** | [#647](https://github.com/Rikarin/Vixen/issues/647) `IUndoManager` · [#648](https://github.com/Rikarin/Vixen/issues/648) the editing-command table · [#649](https://github.com/Rikarin/Vixen/issues/649) `IClipboard` has no consumer · [#650](https://github.com/Rikarin/Vixen/issues/650) accelerators live in the editor · [#651](https://github.com/Rikarin/Vixen/issues/651) `UiApplication` hides `IPlatform` |
| **W3 — the application layer** | [#652](https://github.com/Rikarin/Vixen/issues/652) native menu bar (decision) · [#653](https://github.com/Rikarin/Vixen/issues/653) no quit veto · [#654](https://github.com/Rikarin/Vixen/issues/654) drag-in dropped, no drop model · [#655](https://github.com/Rikarin/Vixen/issues/655) `INativeDialogs` unreachable · [#656](https://github.com/Rikarin/Vixen/issues/656) no document model · [#657](https://github.com/Rikarin/Vixen/issues/657) Toolbar/StatusBar/SplitView/Segmented |
| **W4 — markup parity** | [#658](https://github.com/Rikarin/Vixen/issues/658) no ambient value · [#659](https://github.com/Rikarin/Vixen/issues/659) props assigned after `Build` · [#660](https://github.com/Rikarin/Vixen/issues/660) inert attributes on lowercase tags · [#661](https://github.com/Rikarin/Vixen/issues/661) six modifiers with no spelling · [#662](https://github.com/Rikarin/Vixen/issues/662) `@for` gaps · [#663](https://github.com/Rikarin/Vixen/issues/663) `bind:` too narrow |
| **W5 — controls** | [#664](https://github.com/Rikarin/Vixen/issues/664) seven controls nothing constructs · [#665](https://github.com/Rikarin/Vixen/issues/665) declared and never set · [#666](https://github.com/Rikarin/Vixen/issues/666) the missing controls, ranked |
| **W6 — presentation and system** | [#667](https://github.com/Rikarin/Vixen/issues/667) `prefers-color-scheme` never fed · [#668](https://github.com/Rikarin/Vixen/issues/668) reduced motion, forced colours, accent · [#669](https://github.com/Rikarin/Vixen/issues/669) no momentum or anchoring · [#670](https://github.com/Rikarin/Vixen/issues/670) no UI white level |
| **W7 — damage tracking** | [#671](https://github.com/Rikarin/Vixen/issues/671) |
| **W8 — accessibility bridge** | [#672](https://github.com/Rikarin/Vixen/issues/672) no platform bridge · [#673](https://github.com/Rikarin/Vixen/issues/673) the IME is half-wired |
| **Part 9 — stale claims** | [#674](https://github.com/Rikarin/Vixen/issues/674) |

**Not filed, because they already exist**: [#128](https://github.com/Rikarin/Vixen/issues/128) (doc 45
step 2 — an editor scope is not derivable from focus) is § 3.6's third bullet reached from doc 45's
side, and [#642](https://github.com/Rikarin/Vixen/issues/642) names it rather than repeating it;
[#248](https://github.com/Rikarin/Vixen/issues/248) is `position: sticky`;
[#283](https://github.com/Rikarin/Vixen/issues/283) is the refuted touch-events claim, commented
rather than re-filed; [#362](https://github.com/Rikarin/Vixen/issues/362) and
[#361](https://github.com/Rikarin/Vixen/issues/361) are the editor-side consequences of
[#654](https://github.com/Rikarin/Vixen/issues/654); [#627](https://github.com/Rikarin/Vixen/issues/627)
is the other half of [#670](https://github.com/Rikarin/Vixen/issues/670)'s seam;
[#421](https://github.com/Rikarin/Vixen/issues/421), [#420](https://github.com/Rikarin/Vixen/issues/420)
and [#330](https://github.com/Rikarin/Vixen/issues/330) sit under
[#672](https://github.com/Rikarin/Vixen/issues/672).

---

## Part 9 — Claims that turned out wrong

A refuted claim is worth as much as a fix, and each of these is a sentence a future contributor would
otherwise take on trust.

| Claim | Where | Verdict |
|---|---|---|
| "`scale` and `rotate` are refused — a `DrawCommand` is an axis-aligned rectangle" | `Core/Vixen.Ui/README.md:38` | **False.** `Transform.cs:12` reads `transform`, `rotate` and `scale`; the matrix reaches a composite quad and the hit test inverts it |
| "Zoom is arithmetic, not a transform… there is no `transform` property" | `Core/Vixen.Ui.Controls.Advanced/NodeCanvas.cs:659` | **False** for the same reason. The workaround is arguably still correct — text reshaped at its real size beats a scaled atlas — but the stated reason is not |
| "touch events never reach `UiDocument` at all" | doc 43's `touch-action` refusal | **False.** `PlatformInput.cs:138-170` routes them, produced by Desktop and Android |
| `Vixen.Ui` has "`MenuItem : ButtonBase` and a `Disabled` bool nothing sets" | doc 46 § Part 1 row 1 | **Refuted as a statement of today.** `Commands.cs` is 834 lines; `ButtonBase.Command` sets `Disabled`, title and check from `CommandRoute`. What is still open is the *registry, keymap and palette* — and `Editor/Vixen.Editor.Ui/Commands/` has **grown** to 2 240 lines |
| Doc 46 rows 2, 3 and 5 (strings, answerable dialog, accessibility tree) | doc 46 § Part 1 | **Confirmed closed.** The editor's `Dialogs/` directory no longer exists |
| Doc 46 row 4 (no undo below `Vixen.Editor.Core`) | doc 46 § Part 1 | **Confirmed open** |
| Doc 45 § G2's "derived scope" was built | `Commands.cs:367` | **Built, tested, documented — and dead.** `EditorShell.Context`, the pushed mutable string it was written to replace, is what ships |
| `MenuBar`/`DockingHost`/`TreeView`/`RadioGroup`/`Select` need `ref` + `OnComposed` | working note | **Stale for four of five.** Only model-backed controls still need it (§ 7.1) |
| `[Parameter]` attribute on a component property | `Core/Vixen.Ui.Markup/README.md:44` | **The type does not exist** |
| Doc 43's own track markers (A/B/C) | `43-web-styling-parity.md` | **Stale and understating.** Block, grid and inline have all landed; B2 is still marked 🔴, and A7 is 🟢 while its own cell describes the work as done. The 18.8 EM figure should be re-costed before it is quoted again |

⚠ **`RefusalExpiryTests` guards the TSV's `note` cells only**, so the same defect doc 43 built a test
for (#288) is live again in three other files. Widening that gate to prose refusals in `README.md`
and doc comments is a half-day and would have caught all three above.

---

## Part 10 — The one-line summary for each part

- **The responder chain is right and unreachable.** Fix the reachability before adding to the design.
- **There are three chains; AppKit has one.** Give them one tail, and let a non-element sit in the
  middle by appending rather than by rewriting a pointer.
- **What a chain is *for* — undo, editing commands, key equivalents, the clipboard — is all absent**,
  and all of it is cheap once the chain has responders.
- **The application layer stops one level below where AppKit and SwiftUI start**, and six finished
  cross-platform implementations terminate in two files that never call them.
- **Markup lacks capabilities, not syntax**: an environment, a safe prop assignment, and about ten
  attribute spellings over APIs that already exist.
- **The best-built thing in the audit — 898 lines of correct accessibility — reaches nobody.**

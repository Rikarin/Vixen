# Vixen.Ui

The UI framework proper: an element tree, the stylesheets that describe it, and the pass that turns
one into geometry.

`Vixen.Ui.Styling` decides which declaration wins without knowing what a length measures.
`Vixen.Ui.Layout` measures without knowing where its numbers came from. Neither references the other,
which is what keeps a flexbox engine usable without a stylesheet and a cascade testable without a
layout — and it leaves a gap that something has to close. This closes it, and then puts a tree on
top.

## State

| | |
|---|---|
| `LengthContext` | What a relative length is relative to: the element's font size, the root's, the viewport. |
| `LayoutStyleBuilder` | `ComputedStyle` → `LayoutStyle`. Every layout-affecting property, the nine CSS edges, and the font-size chain. |
| `UiElement` | One node. A class, holding no geometry and no style — a handle into the two stores that do. |
| `UiDocument` | The tree, its stylesheets, and the four-walk pass. |
| `[UiProperty]` | Generated properties with defaults, coercion, change callbacks, inheritance and a runtime identity. |
| `UiDocument.HitTest` | What is under a point, front to back, with `pointer-events` and `overflow` honoured. |
| `EventRouter` | Capture, target, bubble, `Handled`, and pointer capture. |
| `DrawList`, `DrawListBuilder` | Backgrounds, borders, radii and clips as commands, diffed frame to frame. |
| `UiDocument.Focus`, `MoveFocus` | Focus, focus scopes, and HTML's tab order. |
| `UiDocument.FindInDirection` | Arrow navigation over the layout, by the beam model. |
| `GestureRecognizer` | Taps with a count, long presses and drags from one pointer; pinch and rotation from two, as one `TransformEvent`. |
| `visibility`, `opacity` | Honoured by the draw list *and* the hit test: hidden elements are not painted and are not pointer targets, but keep their space and their subtree; `collapse` reads as `hidden`; opacity multiplies down the tree. |
| `FontRegistry`, `TextRun`, `TextLine`, `TextLayout` | `font-family` → a fallback chain, a face per character, shaping through a cache, measurement into layout, glyphs into the draw list. |
| `PathBuilder`, `OnDraw` | Lines, curves, fills and strokes for the controls a stylesheet cannot describe. |
| `DrawBatcher` | Contiguous, order-preserving, maximal runs a renderer can submit as one. |
| `UiDocument.Move` | Reordering a sibling in all three stores, so `:nth-child` moves with it. |
| `Component`, `BuildContext` | What a compiled `.vxml` calls: elements, effects, branches, keyed lists, events, slots. |
| `KeyEvent`, `TextInputEvent` | Keys routed from the focus outwards; typed text as its own event. Tab is the document's default, after the route. |
| `UiDocument.Track` | `:hover` and `:active` on the ancestor chain, `Entered`/`Exited` per element crossed, `:focus-visible` from how the focus arrived. |
| `WheelEvent` | Hit-tested and bubbling, so nested scrolling chains on `Handled` rather than on a rule. Carries `Modifiers`, because Ctrl-wheel means zoom in every canvas and timeline ever written. |
| `DropEvent` | A file or a string dragged in from another application, hit-tested and bubbling like a wheel. ⚠ The OS half only: there is no `DataObject` and no in-app drop model, because the payload an in-app drag negotiates is the half an OS drop cannot fill in. |
| `UiElement.OnCreated`, `TagName` | The constructor a control cannot have, and the element name a type answers to. |
| `UiElement.OffsetX/Y` | A translation applied after layout — scrolling, popups and drag previews, at the cost of a walk. |
| `translate` (CSS) | The declarative half of the same idea, resolved by `TranslationReader` and added into the same sum. Separate from `OffsetX` on purpose: a stylesheet must not be able to erase a scroll position. `scale` and `rotate` are *not* refused — `TransformReader` composes them into one `UiTransform` a composited group's four vertices carry and the hit test inverts, because a shape change cannot be folded into a position the way a translation can. |
| `UiElement.SetStyle` | Declarations written on an element, for the lengths no stylesheet was given: a splitter's ratio, a virtualised row's position. |
| `UiDocument.Reparent` | Moving a subtree to a different parent: fresh style slots, the same elements. What docking and drag-and-drop between lists are made of. |
| `UiElement.Role`, `AccessibleName`, `AccessibleState`, relations | What a screen reader is told: a WAI-ARIA role, a name, a value, a state set, and the pairings the tree does not show. Computed from the control, not stored on it. |
| `UiDocument.AccessibilityInvalidated` | One coalesced raise per frame when anything above may have changed — including a change to `ElementState.Checked` or `.Disabled`, which between them carry ticked, selected and open. |
| Access keys | ⏳ |

## Focus

`Focusable`, `TabIndex` and `IsFocusScope` are themselves `[UiProperty]`s, which is the property
system's first real user rather than a test of it.

**HTML's tab order, implemented faithfully rather than sanely.** A positive `TabIndex` comes before
*every* zero, in numeric order — so one element written at the bottom of a form jumps to the front of
it. Zero is document order; negative is focusable but not a stop. A framework that quietly
reinterprets this produces a tab order nobody can predict from the markup.

The sort is stable, and that is not decoration: two elements sharing a positive index must stay in
document order relative to each other, or the tab order changes with how many elements are on the
page — a bug nobody can reproduce.

**Tab stays inside the innermost focus scope and wraps there**, which is what makes a dialog modal to
the keyboard.

`:focus` and `:focus-within` are set on the style tree, so a focus ring is a stylesheet's business
rather than a special case in the renderer.

**A press that lands on nothing focusable takes the focus away.** Which control a press *gives* the
focus to is that control's own decision — some decline it, a `NumericInput` being scrubbed among
them — but the other half of the rule belongs to the document, because no control is in a position
to notice that the user has clicked somewhere else. The test is the whole ancestor chain rather than
the element under the pointer: a press on a field lands on the part that draws its text, which is not
itself focusable. A press that captures the pointer is exempt too — capture is how a control says
the press *began* something, and a field must not lose its caret because the scrollbar beside it was
dragged.

## Commands: the focus route

A command is a string id, and `CommandRoute` is the answer to "who handles it right now". Nothing
stores that answer: it is worked out by walking from `UiDocument.Focused` outwards through `Parent`
until an element says it handles the id. `UiElement.AddCommandHandler(id, execute, canExecute?)` is
how an element says so. See [the guide page](../../docs/guide/ui/commands.md).

Three rules, and each of them is a decision:

**The first responder wins, and its `canExecute` is the only one asked** — not even to break a tie
when it refuses. A second element further up that would also have handled the id is never consulted,
because otherwise "which handler runs" would depend on how many things happen to be listening, and
adding an unrelated panel above a view would silently change what that view's disabled Copy did.

**Nobody responds ⇒ not executable.** That is the affordance a hand-written enablement rule cannot
express: an application declares a menu of ids and the items grey themselves out wherever the chain
is silent, with no rule written anywhere. A command with a single registration-time implementation is
simply a handler on the root, which always responds — so nothing changes for one.

**The walk starts at `CommandFocus ?? Root`.** The root rather than nothing, so a document-wide
handler still answers while the focus is nowhere. With something focused the root is on the walk
anyway.

⚠ **What runs a command is here; what a keystroke means is not.** There is no chord table in
`Vixen.Ui` and no `.vxml` spelling of a shortcut — `MenuItem.ShowShortcut(key, modifiers)` *draws* one
and registers nothing, which is why `Samples/02-HelloUi/Shell.vxml` says in as many words that a menu
claiming ⌘S would be lying. The half that is missing is only the table, though, and it is not
missing from the tree: `Editor/Vixen.Editor.Ui/Commands/CommandDispatcher.cs` attaches to any
`UiDocument`, turns a `KeyEvent` into a platform-adapted `KeyChord`, resolves it against a `KeyMap` in
the focused context and executes — falling through rather than refusing when the chord belongs
somewhere the user is not. So an application that is not the editor has the route and not the chords,
and what it lacks is a home for that dispatcher below `Vixen.Editor.Ui`, not the dispatcher.

### Past the root: responders that are not elements

The walk does not stop at the root. Past the last parent it asks `UiDocument.CommandResponder` and
then `UiDocument.ApplicationCommandResponder`, which is AppKit's chain continuing through the
document to `NSApp` and its delegate once the view hierarchy has run out — the guide is explicit
that a delegate gets its chance "even though a delegate isn't formally in the responder chain".

The gap that closed: **a handler had to hang on a `UiElement`**, so a view-model or a document
object that wanted to own `edit.copy` had to own a piece of the view tree in order to say so.
`ICommandResponder` is one method, id to handler; `CommandResponder` is the table almost everything
wants, with the same five arguments and the same duplicate-id throw as `AddCommandHandler`.

⚠ **No rule changed, only the length of the walk.** Nearer wins all the way out — leaf, panel, root,
document, application — the first responder that *answers* wins, and only that one is asked
`CanExecute`. A responder further along is not consulted even to break a tie when the nearer one
refuses. `CommandResponderTests` asserts that with a counter on the further responder rather than by
observing which one ran: "the document won" is also true of a chain that asked the application and
preferred the document anyway, and nought lookups is the claim the rule actually makes.

⚠ **Answering is not being able to run.** A responder whose verb is temporarily impossible returns
`true` with a predicate that says no. Returning `false` drops the id out of the chain, and there is
nothing after the application to catch it.

⚠ **Lifetime: the document holds the responders and never the reverse.** `ICommandResponder` has no
event and no back-reference by design, so a responder never learns which documents it was installed
on and a long-lived one cannot pin a closed window's element tree —
`A_long_lived_responder_does_not_keep_a_closed_document_alive` asserts that against the collector.
`UiDocument.Dispose` drops both slots and the `CommandsInvalidated` subscribers regardless.
Installing a responder invalidates; changing its table does not, because it does not know a
document, so its owner calls `InvalidateCommands()`.

Focus *acceptance* is already separable from tab participation and needed no new API:
`UiElement.Focusable` plus `TabIndex = -1` is `acceptsFirstResponder = YES` plus exclusion from the
key view loop, and `Selects`, `Tabs`, `TextInputs` and the advanced controls already use it.

⚠ **`CommandFocus`, not `Focused`, and that distinction is what made step 3 possible at all.** The
surfaces that *display* commands have to take the focus to be operable: `Menu.OnOpened` focuses its
first item so the arrow keys work, and a `MenuBarItem` takes the focus when it is pressed. A menu
item resolving `edit.copy` from `Focused` therefore resolved it from inside the menu, found nothing,
and greyed itself — a menu in which every command is permanently disabled, which is indistinguishable
from a correct implementation of "nobody responds ⇒ not executable" unless something looks.
`UiElement.IsCommandTransparent` is how a surface says **"I am not a place"**; focusing anything
inside one leaves `CommandFocus` where it was. It changes nothing about focusability — Tab still
lands there, the ring still shows, arrows still move between menu items — and it is inherited
downwards on `CommandScope`'s terms, so a surface declares it once at its root. It is AppKit's rule
that a menu is not in the responder chain, stated as data rather than as a private event loop.

`CommandHandler` also carries `Title`, `IsCheckable` and `IsChecked`, all of them asked rather than
captured for `CanExecute`'s reason. The handler supplies them because there is no command *object*
in this model to hang a caption on — "Undo Move" is a fact about the view that owns the undo stack,
and that view is the one that answers. `Title` being `null` means "leave the surface's own label
alone", never "no name": a binding that read it the other way would blank every ordinary line in a
menu, and every enablement assertion would still pass.

`CommandScope` is a name a panel declares once on its own root; `EffectiveCommandScope` is the same
upward walk asked for a different thing, so everything inside that panel reports it — including
controls added later and controls added by a plugin. It is deliberately **not** a `[UiProperty]`: an
inheriting one would cost every element in the document a value field *and* an is-set flag, and would
put "which panel am I in" somewhere a stylesheet could change it.

⚠ **Cost.** Both features live behind one nullable reference on `UiElement`, so an element that never
takes part pays eight bytes and no allocation at all — the same bargain the routed-event `handlers`
list makes. The small store behind it is allocated only for elements that declare a handler or a
scope, and holds a `List` rather than a `Dictionary` because an element declares a handful of ids at
most and a linear scan over four strings beats hashing one.

**Invalidation is one coalesced event.** `UiDocument.CommandsInvalidated` is raised from `Tick`, at
most once a frame, when the command focus moved, a handler was added or removed, or anybody called
`UiDocument.InvalidateCommands()`. A menu is asked as it opens and needs none of it; a toolbar is on
screen continuously and has no such moment, and what it replaces is `EditorShell.Tick` asking every
command on the strip every frame. The flag is set as often as anyone likes and read once — fifty
registrations during a load raise it once. It is on the **document** and not on the static
`CommandRoute`, because a static event would keep every subscribing control alive for the life of
the process and one document's focus change would invalidate another's surfaces; and it is raised
from `Tick` rather than from `Update` because `Update` returns early when nothing dirtied the
document, and a command becoming executable is not a thing that dirties one.

**What consumes it.** `Vixen.Ui.Controls`' `ButtonBase.Command` — so `Button`, `IconButton`,
`MenuItem`, `ToggleButton` and `Link` all bind an id, from markup as readily as from code, and each
follows the invalidation for as long as it has one bound. The extended chain's consumer is the
editor: `CommandRegistry` implements `ICommandResponder` over the table it already had, and
`EditorShell` installs it as its document's `ApplicationCommandResponder`, so a plain `Vixen.Ui`
control bound to an editor command id resolves, greys and runs it — through the registry's own
scope-and-enablement gate and raising its `Executed` — with nothing editor-shaped in the control.
The editor's `CommandRegistry` still resolves its *scope* through `EditorShell.Context` — see
[doc 45](../../docs/plan/45-commands-and-focus-scope.md), whose staging step 1 is what this is, and
whose § G2 was **refuted** when it met the editor: the editor's contexts are pushed from *pointer
presses*, not focus changes, because its panels are not focusable — six of its seven context-claiming
panels leave `Document.Focused` null, measured.

## Strings: what a label says

The other half of a menu item. A command id answers *who runs it*; a `StringId` answers *what it
says* — a pair of an id a catalogue calls the string by and the source text it was written as, so
`item.Label = EditorStrings.Save.Text` is no more work at the call site than the literal. See
[the guide page](../../docs/guide/ui/strings.md).

⚠ **The fallback is in the source, not in a file.** An application whose English lives in an `en`
catalogue shows `editor.command.file.save` to anybody whose install is missing that file, and a
missing file is exactly what a localisation bug looks like. `StringCatalog` therefore holds no
fallback chain at all: it is a flat id-to-text map, and `Strings.Missing` is the list of ids the
running application asked for and did not get — the translator's worklist, gathered rather than
logged.

⚠ **And it holds no file format.** `Set`/`Find`/`Ids`/`Count` and nothing else. This assembly is
referenced by every application that shows a word, so attaching a serialiser here would put one in
every consumer's package pin for a code path most of them never call — sharpest for an application
publishing NativeAOT against a vendored closure. The editor reads YAML through
`Vixen.Editor.Ui.StringCatalogYaml`; another application answers differently, and neither choice
reaches the other.

⚠ **And the catalogue in use is a `Signal<StringCatalog>`, not a field.** That is the whole of what
makes a language change re-label a running interface: every `@expr` in a `.vxml` is a region-scoped
effect, so an expression that reads a string is a consumer of that signal without saying so.
`Strings.Use` marks it dirty, the next `Document.Update` re-runs it, and the label changes with no
code at any call site. The field version is pixel-identical until the moment somebody changes
language, which is why the sabotage matters more than the test: reverting the signal to a field
leaves both assertions in `Vixen.Ui.Controls.Tests/LocalisationTests.cs` reading `"Close"` where they
expect `"Zavřít"`.

⚠ **A label assigned once in C# is not an expression.** A control whose constructor writes
`Button.Label = ControlStrings.Close.Text` reads the signal outside any effect and shows the language
that was in use when it was built. `Strings.Changed` — a plain event, and static — is what a
hand-built surface subscribes to in order to rebuild itself whole.

⚠ **A declaration class is a shape, and the shape is checked.** An application declares its strings
as a static class of `StringId` properties with an `All` list beside them — every string written
twice, which is the cost of `All` being data a trimmer cannot shorten.
`Vixen.Ui.Generators.StringDeclarationAnalyzer` compares the two sides (`VXS0310`), refuses two
declarations under one id (`VXS0311`) and a `StringId` built outside the class in an assembly that
has one (`VXS0312`). ⚠ **A project has to name the analyzer** — analyzers do not travel through a
project reference, so referencing this assembly is not enough. The remaining half, an id declared and
used nowhere, needs the whole source tree rather than one compilation: `nuke CheckStrings` is this
repository's, and an application outside it wants the equivalent over its own.

These three types were `Vixen.Editor.Ui`'s until doc 46 § A3 counted them among the 41 % of that
assembly that is application-framework machinery no application can reach.

## Accessibility

Six things on `UiElement` and one event on `UiDocument`, and it is a tree rather than a bridge:
AT-SPI2, UI Automation and `NSAccessibility` all read it and none of them is here.

`Role` is a **WAI-ARIA 1.2** token. The enum's member names are the ARIA names PascalCased and
nothing else — which is why `img` is `AccessibleRole.Img` rather than `Image`: the rule this keeps is
that `role.ToString().ToLowerInvariant()` is the ARIA token for *every* member, so there is no
exception table for the next role added to be left out of. A role this enum does not have is added by
its ARIA name rather than approximated with a neighbour.

⚠ **A control's accessible view is computed, never stored, and that is the decision the rest hangs
off.** `NativeRole`, `NativeAccessibleName`, `NativeAccessibleValue` and `NativeAccessibleState` are
virtual members answered by the type from what it already holds, so a `CheckBox` reads its own
`IsChecked` when asked. There is no second copy to update, no change callback to remember, and no
state in which a box is ticked on screen and unticked to a screen reader. It is also what makes the
population cheap: `ButtonBase` overriding two members gives every button, menu item, tab, option and
link in the control assembly a role and a name at once.

⚠ **Three states are the framework's and no control declares them.** `AccessibleState` always adds
`Disabled` from `ElementState.Disabled`, `Focused` from `ElementState.Focus` and `Focusable` from
`Focusable` — for every element, whether or not its type overrode anything. Fifty controls cannot
each forget one, and the symptom of a forgotten one is a screen reader saying a greyed button is
available, which nobody writing the control would ever see. `DeclaredAccessibleState` is the
application's half, or'd on top and never subtracted from.

**The cost is one nullable reference**, on `CommandBindings`' terms: eight bytes per element, and no
allocation at all unless somebody sets a name, a role, a value or a relation on that particular
element. A control that only overrides virtuals allocates nothing.

**Relations are the pairings parent-and-child is the wrong shape for**, named after ARIA's
relationship attributes with the prefix dropped. Two of them are load-bearing here:

* A `TabItem` `Controls` its panel, and the panel is `LabelledBy` the tab. The two are in different
  parts of the tree — the class remark on `Tabs` says they cannot be one element — so no walk over
  `Parent` recovers either direction. Both are established in `Tabs.Adopt`, not `AddTab`, so a
  `<TabItem />` in a `.vxml` reaches the same state as the code path.
* A `Select` `Owns` its option list, which is a child of the document *root* because an overlay
  inside the field that opens it would be clipped by every scrolling ancestor between the two; and
  it points `ActiveDescendant` at the chosen option, because the focus stays on the field while the
  list is open and a screen reader told only "the field has the focus" would never announce a
  choice.

`AccessibleName` resolves in accname 1.2's order to the three steps that decide a control set's
answer: an explicit assignment, then the `LabelledBy` target's name, then `NativeAccessibleName` —
which for a button is its label and for a plain element is its own `Text`.

⚠ **A `TextField` answers `null`, deliberately.** A placeholder is a hint rather than a name and it
disappears the moment there is a value, so a form named from placeholders is a form whose fields lose
their names as they are filled in — four numeric fields all announced as "0.00". A field's name is
the words beside it, which are somebody else's element, and answering nothing until a `LabelledBy`
says so is what lets a gate fail an unlabelled field rather than pass it with a plausible-looking
lie.

**Most elements are not in the tree at all.** `Role` defaults to `AccessibleRole.None` — ARIA's
`none` — for every element, including `Panel`, `Card`, `Tabs` itself and every part a control builds
itself out of. A bridge walks through them and reads their children in their place, which is what
stops a four-field form being announced as thirty nested groups. `IsInAccessibilityTree` is the
question; `ClearRole()` hands a role back to the type, which is not the same as assigning `None`.

`AccessibilityInvalidated` is `CommandsInvalidated`'s object one field over, on purpose rather than
by coincidence: a flag set as often as anybody likes, read once from `Tick`, cleared before the
handlers run so a handler's own re-invalidation survives. It says *that* something changed and never
*what* — accumulating the set per mutation is the allocation the coalescing exists to prevent, and a
bridge holds a cached tree it has to diff anyway. ⚠ `Focus.cs` raises it **outside** the
command-transparency branch, the one place the two events deliberately disagree: a focus move into a
menu cannot have changed which view answers a verb, and it certainly changed what has the focus.
`Attach`, `Insert` and `Detach` set it too, because the shape of the tree is the one thing no
property setter could report.

⚠ **And `State` sets it, for two of its seven bits.** It was written here and in the code that a
computed state — ticked, selected, open — reached a bridge "through the restyle it already causes",
and that was false: a restyle invalidates the cascade and touches nothing a bridge reads, so a
consumer that re-read only when told missed every tick of every checkbox. The whole control set
carries those three meanings on `ElementState.Checked` and greys with `ElementState.Disabled`, so
masking the setter to those two bits closes it framework-side with nothing stored and no per-control
callback. `Hover` and `Active` are excluded because they are not announced and would set the flag on
every frame a pointer moves; the focus bits because `UiDocument.Focus` already raises.

⚠ **And a state computed from a control's *own* field calls `InvalidateAccessibility()` itself**,
which is a protected method on `UiElement` for exactly this. The framework cannot see those: a
half-ticked `CheckBox`, an `Overlay` opening under a `MenuItem` or a `Select`, a `TreeNode`
expanding, a `Slider` arrowed one step. Each says so on the line that writes the field — a
notification and not a mirror, since nothing is stored and the `Native*` override is still read on
demand. ⚠ Two of them are worth knowing about: the announced element and the changed element are
**not the same object** for an overlay — `MenuItem` reads `Submenu.IsOpen` — which is why the raise
belongs in `Overlay.Restate` and works only because the flag is per document rather than per node;
and a `TreeView` was *half* covered before this, because expanding inserts a row and a structural
edit already raises, while collapsing parks a pooled row and raises nothing. Half a notification is
the worse half.

The gate is `Vixen.Ui.Testing.AccessibilitySnapshot`: `Render` for the tree as comparable text, and
`Unnamed` for the assertion a snapshot cannot make. ⚠ Assert `Unnamed` first — a snapshot of a
document with no accessibility at all is the empty string, and an expectation of the empty string
matches it.

## What a screen-reader bridge has to be, and why there is not one

⚠ **Everything above is read by xUnit and by nothing else.** Every `AccessibilityInvalidated +=` in
the repository is in a test — `Vixen.Ui.Tests/AccessibilityTests.cs:32,176,208` and the two
`AccessibilityNotificationTests` — and grepping every platform assembly for `NSAccessibility`,
`IRawElementProvider`, `UIAutomation`, `AtSpi` and `IAccessible` returns nothing. No screen reader on
any platform can see a Vixen element, and the suite that covers the tree is green either way, which
is exactly the question the working agreement says to ask of an instrument: *what does this print on
the day it does not run?* This section is the shape the missing half has to take, written down
because the tree above was designed against it and the reasons are not recoverable from the code.

**The bridge is per surface, not per document.** Every one of the three protocols roots at a *window*
— an `NSWindow`, an `HWND`, an AT-SPI object with `ROLE_FRAME` — and `UiDocument` deliberately spans
several (`Surfaces.cs:20`, and one document across windows is the thing this framework does that
AppKit does not). So the attach and detach points are `SurfaceAdded`/`SurfaceRemoved`
(`Surfaces.cs:35,38`), `SurfaceOf` (`Surfaces.cs:199`) is how a node answers which window it belongs
to, and the per-frame raise stays document-wide because coalescing across surfaces is cheaper than
three flags and the diff has to walk anyway.

⚠ **What a bridge publishes is a snapshot, and it may not read the element tree at all.** This is the
load-bearing decision and it is not obvious. Assistive technology calls *in*, on its own thread: AT-SPI2
is a D-Bus server answering from the bus thread, UI Automation calls providers from an RPC thread it
owns, and only `NSAccessibility` is main-thread — and even that one is re-entrant inside the run loop,
so it can arrive between a layout walk and the draw walk. `Vixen.Ui`'s reactive graph is
single-threaded by contract, a signal read asserts its owning thread, and `AccessibleName` on a
control is a *computed* property that reads the control's own fields. A bridge that answered
`accessibilityLabel` by reaching for the live element would therefore be reading the UI thread's
graph from the AT's thread, and the failure mode is a torn read on a good day and the assert on a
bad one. The rule is: build an immutable snapshot on the UI thread from `AccessibilityInvalidated`,
answer every protocol call out of the most recently published snapshot, and never dereference a
`UiElement` off the UI thread. ⚠ This is also the real reason the event says *that* something changed
and never *what* — a bridge was always going to hold a whole cached tree, because the protocol
requires it to answer questions about nodes nobody touched this frame.

**Node identity has to outlive the frame and must not keep the element alive.** UIA hands out
provider references an AT retains for as long as it likes, AT-SPI hands out bus paths, and
`NSAccessibility` retains the element objects it is given; none of the three re-fetches because a
frame ended. So a snapshot node carries a stable id allocated once per element and kept across
republishes, and the map from id back to a live element for the *action* path is weak — a request
against an id whose element is gone answers "no longer valid" rather than resurrecting it or
crashing. `ReleaseAccessibilitySubscribers` (`Accessibility.cs:897`) exists for the mirror image of
this leak and is the precedent for taking it seriously.

**Three things the model owes a bridge and does not have yet, in the order a bridge needs them.**

* **Actions.** Every protocol asks an element to *do* something — `accessibilityPerformPress`,
  `IInvokeProvider.Invoke`, AT-SPI's `Action.DoAction` — and there is no mapping here from a role to
  a verb. `UiDocument` has commands and a hit test; what is missing is the small table that says a
  `Button` has one action named "press" that raises a `ClickEvent` on it, and the seam for a control
  with more than one.
* **Text ranges.** `NativeAccessibleValue` answers a whole string. A screen reader reads a text field
  by asking for the line the caret is on, the word to its right, the selected range and the character
  offsets of each — `AXTextMarker`, `ITextProvider`, AT-SPI's `Text` interface. Without it `TextField`
  is announced as one opaque value and `CodeEditor` is unreadable, bridge or no bridge.
* **Live-region announcements.** `Toasts.cs:31,114` assigns `Alert` and `Log` correctly and there is
  nothing to deliver: a toast appears where the user is not looking and takes itself away again, so a
  reader that is only told when the user walks to it is never told. The announcement is a *message*
  rather than a tree change, which is why the coalesced per-frame diff cannot carry it and why it
  needs its own small queue drained on the same tick.

Adjacent and cheap, and none of them is a bridge: `AccessibleStates.Required` and `.Invalid` have no
producer anywhere in the tree, so no control ever reports a required or invalid field — they wait on
a validation seam that does not exist rather than on a bridge — and `AccessibleRelation.FlowsTo` has
no producer either.

**Geometry crosses two coordinate systems and neither of them is the one AT asks for.** Layout gives
a rect in document space; a surface knows its size and DPI scale (`UiSurface.cs:101`); the protocols
want screen space, and on macOS in a bottom-left origin. The snapshot therefore carries surface-space
rects and the platform half adds the window origin, because the window origin is the one number that
only the platform assembly can know and the one that changes when the user drags the window without
anything in the document changing.

**`NSAccessibility` first, and it is first because it is worth most rather than because it is
cheapest.** ⚠ `Platform/Vixen.Platform.MacOS/ObjC.cs` is send-only — `objc_getClass`,
`sel_registerName` and a set of `objc_msgSend` prototypes — and a bridge is the first thing in this
repository that has to be *called by* Objective-C rather than call it. That needs
`objc_allocateClassPair`, `class_addMethod` and IMPs that survive the GC, which is a genuinely new
capability in that file and is the largest single cost in the wave; it is not visible from the
outside, and estimating the bridge from the 163 lines already there would be wrong by an order of
magnitude. ⚠ Second cost, also invisible: the `NSWindow` is not reachable from what exists.
`DesktopSurface.Resolve` (`DesktopSurface.cs:54-63`) deliberately takes the `SDL_Metal_CreateView`
route on macOS and hands back a `CAMetalLayer`, precisely so nothing has to reach into AppKit — so
the bridge needs its own `SDL_GetWindowWMInfo` route to the window and its content view, and it is
the first consumer that does. Windows is the same shape one level over: UIA arrives as `WM_GETOBJECT`
on a window procedure SDL owns.

⚠ **The gate is a real assistive technology and nothing else counts.** A second in-process consumer —
a test double that subscribes, diffs and asserts — reproduces exactly the defect this work exists to
fix, because it is another reader inside the same process reading a tree that is already correct.
What is unproven is not the tree; it is that a `NSAccessibility` object graph built from it is one
VoiceOver will read. So the gate is VoiceOver reading a `Samples/02-HelloUi` panel, and it is a
manual gate on purpose.

## Background tasks

`BackgroundTaskManager` is the list of long operations an application is running and
`BackgroundTask` is one of them — a title, a status, a progress fraction, a state and a cancellation
token. It is here for the same reason `ICommandResponder` is: **it is application-framework
machinery, and it was reachable only by the editor.** It came out of `Vixen.Editor.Ui/Tasks/` whole;
what stayed behind is `TaskCenter.vxml`, the panel that shows them, which is the editor's chrome.
The split cost one `@using` line — the model named nothing editor-shaped, and the centre names
`EditorStrings`, `ControlIcons` and the editor's own tags.

**The threading contract is the whole design and it is deliberately the smallest one that works.**
Reporting is safe from any thread; reading is only safe from the UI one. The work runs wherever the
caller put it, `Report` enqueues, and `Pump` applies the queue at one point in the frame — so there
are no locks around the task list, no concurrent collection for the interface to walk, and a frame
sees one consistent set of numbers. A progress bar read during layout cannot see a title replaced
between two reads.

**Every property is signal-backed rather than signal-typed.** `Progress` is still
`float Progress { get; }` and `Tasks` is still an `IReadOnlyList<T>`; the fields behind them are a
`Signal<float>` and a `CollectionSignal<T>`. Not one caller changed, and what changed is that
reading one inside a binding subscribes — which is what makes a task panel markup over the model
rather than a view told to look once a frame. The safety of that rests on the paragraph above: every
write lands on the UI thread because it lands in `Pump`, and a signal written from the pool is a
race the reactive graph is entitled to refuse.

⚠ **`IsCancellationRequested` is the one exception and says why.** It mirrors the token rather than
being it. `Cancellation` is what the *work* polls, from whatever thread it is on, and a signal read
asserts the owning thread — so making that reactive would put the graph in the way of a cancellation
check in a tight loop. `Cancel` sets both, token first.

⚠ **Something has to pump it, and nothing here can.** `Vixen.Ui.Desktop`'s `UiApplication` owns a
manager and pumps it once a frame before raising `Frame`, so an application on the standard loop
gets progress without writing a pump; a host with its own loop owns a manager and pumps it itself,
which is what `EditorShell` does. A manager nobody pumps is a list of tasks stuck at nought per
cent — it fails silently rather than loudly, which is why the property is pinned by a test that runs
a real headless loop rather than by one that calls `Pump` directly.

⚠ **Disposal is not tidiness; it is the leak.** Work on the pool reports through a queue, so an owner
that dropped the manager would leave every running task enqueueing into a queue nothing drains — and
every closure in that queue holds the task, the manager and the assembly the work's delegate came
from. A plugin unloaded while one of its imports is running is exactly the shape that has twice
pinned a collectible `PluginLoadContext` here. `Dispose` cancels everything and then *stops
accepting*: reports after it are dropped rather than enqueued, so work that ignores its token for
another minute costs one thread and no memory. It asks and does not wait, because waiting would be a
frame thread blocked on a file copy.

See [the guide page](../../docs/guide/ui/background-tasks.md), and [doc 46](../../docs/plan/46-what-an-application-needs.md) § *Offered, and taken* for why it moved.

## Arrow navigation

Tab walks an *order* — a list the document decides in advance. An arrow walks a *layout*, decided by
where things actually ended up. Two different questions that move the same focus, which is why
`NavigationDirection` is its own enum rather than two more members of `FocusDirection`.

**The beam model, and it has no constant to tune.** A candidate has to start past the edge the arrow
points at. Among those, the ones whose other axis overlaps this element's are *in the beam*, and any
of them beats any candidate outside it however close that one is; inside the beam nearest along the
axis wins, outside it nearest by straight line between the two rectangles.

The alternative is a weighted score — distance along plus some multiple of distance across — and the
multiple is the problem. It has no principled value, so it gets tuned until the layouts someone
happened to test behave, and Down drifts diagonally in the layouts they did not.

⚠ **Touching is not overlapping.** The beam test is a strictly positive overlap, and it has to be:
two cells of a grid share an edge exactly, so a non-strict test puts the diagonal neighbour in the
beam alongside the one directly below and the grid navigates sideways.

**An element's own focusable children are in no direction from it**, and nothing had to say so — they
are inside it, so they are past none of its edges. Entering a group is a separate idea from moving
between things, and conflating them makes Right mean two things.

**Arrows do not wrap.** Tab is a cycle because an order has no far end; an arrow points at somewhere,
and running out of somewhere is a wall. Holding Down in a list that wrapped would never settle.

⚠ Distance is measured between the *rectangles*, not between their centres. The centre metric says a
wide element eighty pixels away is nearer than a narrow one ten pixels away, because distance to a
shape is distance to the shape.

## Gestures

**Time arrives on the event rather than from a clock the recogniser reads.** One that calls
`DateTime.Now` cannot be tested without sleeping, cannot replay a recorded trace, and reports a
different gesture when a breakpoint holds the frame — and the platform layer already knows what time
the input happened, which is a better answer than what time anything downstream got round to asking.

**A long press is the one gesture that fires because nothing happened**, so `Tick` exists: a
recogniser fed only by input cannot produce it, because there is no input to be fed.

⚠ **Slop is one-way.** Once a press has wandered far enough to be a drag it can never be a tap again,
even when the pointer comes back to where it started — which it does at the end of every flick that
overshoots and settles. Asking how far the pointer is from the press *now* fires a tap at the end of
a scroll, which is a list that scrolls and then opens whatever stopped under the finger.

**A double tap raises `TapEvent` twice, counting up**, rather than raising a different event.
Splitting them forces every handler to answer "is a double tap also two taps", which has no general
answer — a button wants both, a rename wants only the second. This is what the web does, for the same
reason.

**Every gesture goes to the element the press landed on**, for its whole life. That is pointer
capture's rule and it is here for pointer capture's reason; the two coexist rather than duplicate,
because capture redirects raw events and this remembers a target already decided.

**One pointer taps, presses and drags; two transform.** Two fingers on two different controls stay
two independent gestures, which is right. Two that start moving *relative to each other* become one
`TransformEvent` carrying a scale and a rotation — one event rather than a pinch and a rotate,
because they are computed from the same pair on the same frame and cannot occur apart.

⚠ **Starting one cancels the drags those fingers had begun**, and neither produces a tap or a long
press afterwards. A map that both panned and zoomed from the same two fingers moves twice as far as
either gesture asked for, so the suppression is as much of the feature as the arithmetic.

⚠ **The gesture goes to the nearest element containing both fingers**, not to the first one's target.
Two fingers pinching a map land on two different tiles, and a gesture delivered to one tile is one
the map never hears about.

⚠ **The rotation is accumulated, not wrapped.** `Atan2` returns in (-π, π], so an angle measured
against the start jumps a full turn when the fingers pass the wrap point; each sample is unwrapped
against the previous one instead, and a gesture spun twice round reports 4π.

⚠ **Two, not more.** A third finger arriving during a transform is ignored rather than folded in.
Three-finger gestures have no agreed meaning across platforms, and averaging an arbitrary number of
pointers into one scale is an approximation worse than the gap.

## Text

`font-family` names a face in the `FontRegistry`, the string is shaped through the document's cache,
the layout tree asks the shaping how big it is, and the draw list gets a `Text` command naming a
range of one glyph buffer. Four things that were built separately, joined.

**Registered rather than discovered.** Nothing walks the machine's font directories: a game ships its
fonts, and an interface laid out by whatever the operating system happened to have installed lays out
differently on every machine it runs on.

**Three types, one for each thing a line of text is made of.** A `TextRun` is one face; a `TextLine`
is the runs sharing a baseline; a `TextLayout` is the lines stacked down the page. Most text is one
line of one run and costs no more than it did when that was the only shape available.

**Wrapping happens here because the widths do.** A paragraph in two faces has no single design-unit
scale, so `Vixen.Ui.Text`'s `LineWrapper` cannot measure it from a `ShapedText` — the per-character
advances are assembled a run at a time in pixels and handed to the overload that takes them. Break
opportunities are UAX#14's and are about the characters. `white-space: nowrap` and `overflow-wrap:
anywhere` reach it from the cascade.

⚠ **Each wrapped line is re-shaped on its own**, which is what a line break *is*: a ligature does not
cross one and an Arabic word unjoins at one.

**A declaration is a fallback chain, and a line is a list of runs.** `font-family: Inter, Noto Sans
JP` means both faces in that order, and `FontRegistry.Cover` hands each grapheme cluster to the first
that draws all of it — so one element's text can be in several faces at once. `TextLine` is that list
and `TextRun` is one face of it; a draw command names one font, so a mixed line is a command each.

⚠ **Composition happens in pixels, and that is not an implementation detail.** A 1000-unit face and a
2048-unit face measure an em differently, so two advances from different fonts cannot be added at
all. It is why `Vixen.Ui.Text`'s size-independent `ShapedText` stays single-font and the run list
lives up here.

⚠ **Per cluster, not per code point.** Splitting a base letter from its combining mark puts the
accent at a pen position derived from another font's em — a floating accent, where one visible tofu
would have been the better failure. A cluster no face covers whole goes to the head of the chain.

`AddFallback` is the tail behind every declaration: the emoji or CJK face an application wants
everywhere and should not have to write into each rule. `Default` keeps its narrower meaning — a
substitute for a declaration that named nothing registered, in *front* of the fallbacks rather than
behind them.

⚠ **Registering a face re-measures the text that is already laid out.** `FontRegistry.Revision`
moves, `UiElement.Line` drops the runs it shaped against the old chain, and `UiDocument.Update`
dirties the layout node of every element that measures its own text — before its "is anything dirty"
check, because a registration is the one change that leaves the document otherwise clean. All three
are needed and the last is the one that is easy to miss: a line is rebuilt only when the measure
function asks for one, and the measure function runs only for a dirty node, so without it a host that
builds its interface and *then* installs a font gets labels that measured zero and keep the zero for
the life of the document — the right strings, the right colour, nought pixels wide.

**A family is a set of variants, and a face's weight and slant are stated rather than sniffed.** They
could be read from the file's `OS/2` table, and that would be the same mistake in miniature as
walking the font directories: a shipped asset whose metadata disagrees with what the designer meant
would silently pick the wrong face, and the fix would be editing a binary.

**Matching is CSS Fonts 4 §5.2, not nearest-neighbour** — which is what everybody writes first and is
wrong in the middle of the scale. The slant is settled before the weight, because an italic at the
wrong weight answers `italic` better than an upright at the right one. Then: an exact weight wins;
below 500 the search runs downwards before upwards and above 500 the other way; and ⚠ **400 checks
500 first**, so a family with a 300 and a 500 answers `font-weight: normal` with the *500*. That last
one is the asymmetry nearest-neighbour gets wrong half the time, and it has a test of its own.

⚠ **`lighter` and `bolder` are not read** and fall through to regular. They are relative to the
parent's *computed* weight, which this cascade does not have — it inherits specified values, so the
parent's declaration might itself be `bolder` and the chain has no bottom. Owed with the
computed-value stage, and left out rather than approximated as "one step from 400", which would be
right only for an element whose parent said nothing.

⚠ **An element with text cannot have children**, and the layout tree is what says so: a node that
measures itself and also has children has its size decided twice, by two rules that do not have to
agree. So a text element is a leaf, full stop, and mixed content is what the owed run list is for.

**`text-align` is an offset on the run's origin**, which works precisely because a run is one line:
there is one origin to move and its width is already measured. It stops working the day text wraps,
and at that point alignment belongs to whatever breaks the lines. `start` and `end` resolve against
`direction`, the same property the layout resolves its logical edges with, so `text-end` and `pe-2`
land on the same side of a mirrored panel. Negative slack is left alone — text wider than its box
overflows from the start edge whatever the alignment says, because centring it would hide the
beginning of the string, and the beginning is what a reader needs to see what was cut off.

⚠ **`letter-spacing` is added per cluster, not per glyph.** A combining mark is its own glyph and the
same cluster as the letter it sits on, so the per-glyph version spaces an accented `é` as two
characters and pushes the accent off the letter. It is also invisible in Latin: against `AB` the two
implementations agree exactly, which is why the test for it uses a Kannada syllable whose five code
points shape to more glyphs than clusters. Tracking reaches the *measure* as well as the drawing, or
an element sized from text it then drew wider would clip its own last letter.

⚠ **Tracking is added after the last character too**, which is what CSS specifies and every browser
does — so centred text with a wide tracking sits half a step left of true centre. Matched rather than
corrected, on the grounds that a toolkit which quietly disagrees with the specification is harder to
reason about than one that reproduces a known wart.

**A line box is `line-height` tall and the glyphs sit in the middle of it.** CSS's *half-leading*:
the text occupies ascender-plus-descender and the difference between that and the line box is split
evenly above and below. Putting it all underneath is what makes a generous `line-height` look like a
top margin, which then gets called a padding bug for a week. Half of a *negative* leading is negative
and that is correct too — a line height smaller than the glyphs crops them evenly at both ends.

**A decoration is a rectangle at a position the face states.** `underline`, `overline`, `line-through`
and their colour, style, thickness and offset. Every number but the overline's comes out of
`FontFace.Decoration`, which reads the face's own `post` and `OS/2` tables through HarfBuzz — and that
is the whole point, because across the fonts this repository ships the underline thickness runs from
a hundredth of an em to a tenth. A constant looks deliberate in one face and looks like a rendering
fault in the next, and no test of a single font can tell the two apart.

⚠ **It is a `Rectangle` command with a zero radius, so there is no second implementation to keep in
step.** The bar reaches `UiGeometryBuilder.Box`, the rounded-box field and both executors by the path
a background already takes; the device and the software rasteriser agree because they are drawing the
same quad rather than because two ports were kept aligned.

⚠ **One bar per line, not per run.** It spans `TextLine.Width` and takes its metrics from the line's
first run — CSS Text Decoration 3's "first available font". Per run it would break visibly at every
change of face and step in thickness in the middle of a word.

⚠ **The underline and the overline go under the glyphs and the line-through over them**, which is
CSS's painting order and the only reason a descender interrupts an underline. Both orders draw a
plausible picture; the wrong one is invisible until a `g` sits on the line.

⚠ **The overline sits entirely above the ascent, and that was a measurement.** An earlier draft put
its top edge on the ascent line so a thick one would stay inside the line box. In `TestShapeLana` the
ascent is 1556 design units and the cap height is 1493, so the bar landed on the tops of the
capitals — the letters looked struck rather than overlined. A face whose ascent clears its capitals
hides that completely, which is why it took a pixel test rather than a draw-list one to find.

⚠ **A decoration moves nothing that was measured**, which is CSS's rule and also the only behaviour
compatible with `TextLayout.Measure` reporting whole device pixels: a bar that widened a line would
round the block up and shift every element after it.

⚠ **The five properties inherit although CSS inherits none of them.** CSS *propagates* a decoration
from the block box across its own line boxes; one node produces one box here, so there is no ancestor
to draw the line and propagation has nowhere to happen. `text-overflow` is already here for a weaker
form of the same reason. The cost is that a child can escape a decoration with `no-underline` where
CSS says it cannot — and the benefit is that `<div class="underline">{Label}</div>` works at all,
since a `.vxml` interpolation emits its text as a child element.

## The cursor

`UiDocument.Cursor` is what the pointer should look like where it is, resolved from the hovered
element's computed style and read once a frame by whoever owns the window.

**`UiCursor` rather than the platform's `CursorShape`,** because this assembly cannot see
`Vixen.Platform` and should not — a UI tree that knew about windows would be a tree that could only
be shown in one. The mapping is not one to one in either direction either: `col-resize` and
`ew-resize` are one shape on every desktop and two different statements in a stylesheet.

**No walk up the tree, because `cursor` inherits.** The hovered element's computed style already
carries whatever its nearest ancestor with a declaration said. So an element covering its parent does
*not* get the parent's cursor unless it inherited it — which is exactly the CSS rule, and is why a
button inside a draggable panel can say `cursor: pointer` and be believed.

⚠ **The frame diff has to cover the side buffer.** A command names a *range* of the glyph array, so
two frames whose text changed from one word to another of the same length hold byte-identical
commands and completely different glyphs. Comparing commands alone, the label changes and the version
does not.

**The y is negated on the way in.** Shaping puts y positive upwards, because that is how a font's
design grid is drawn; the draw list is in document space. Invisible on Latin — every glyph sits on
the baseline at a zero offset — and it flips every mark in Arabic, Devanagari and Tai Tham to the
wrong side of its letter. The test that guards it is written in Tai Tham for exactly that reason; in
Latin it passed with the negation deleted.

⚠ **One line.** Nothing breaks a paragraph, so a string wider than its element overflows rather than
wrapping, and the measure function ignores the width it is offered. `Vixen.Ui.Text` already has the
UAX#14 line breaker this needs.

A glyph's position is relative to the start of its run and the command carries where that is, so two
labels saying the same thing in different places hold identical glyph runs — which is what will let
the batcher notice.

## Paths and custom drawing

A stylesheet describes boxes, and most of an interface is boxes. A chart, a sparkline, a knob and a
hand-drawn icon are not, and there is no property for those. `UiElement.OnDraw` is where a control
draws itself and `DrawContext` is what it draws with — called after the element's background, border
and text and before its children, which is where CSS puts an element's own content.

⚠ **Curves are kept as curves.** How finely to flatten a Bézier depends on how large it will be on
screen, which is a device scale the draw list does not know. Flattened here, a path built once and
drawn at two zoom levels is faceted at one of them, and nothing downstream can recover the curve to
do better.

**One fixed-size struct per verb**, points a verb does not use left at zero. Skia's design is a verb
array beside a point array, which is smaller — a line costs one point rather than three — and needs
two ranges on the command and two cursors to walk it. One array keeps the frame diff a comparison and
the command's reference one range, which is worth more here than the bytes.

**Fill and stroke are two commands over one path range**, not one command with a flag: they are
different draws, and a shape that is both still describes its outline once.

⚠ **`Close` carries the point it closes to.** A stroked path's closing join is drawn differently from
a line back to the same place, so the verb has to survive — and a second contour has to close to its
own `MoveTo`, which is what makes a path with a hole in it possible at all.

`PathFillRule.EvenOdd` is there because it is how most icon sets punch the hole in a letter `o`, and
a renderer that only knew non-zero would fill it in.

## Nine-slice

`DrawContext.DrawNineSlice` is what turns one small texture into a panel, a button and a tooltip at
three different sizes with the same corners. The cut itself is `NineSlice` in
`Vixen.Core.Mathematics` — shared with `Vixen.Rendering`'s sprites, because a stretched panel and a
stretched sprite are the same nine pairs of rectangles and the two assemblies cannot see each other.

**Not a command kind, and that is the point.** A nine-sliced image carries the same
`DrawCommandKind.Image` as a stretched one, so it goes through the same pipeline and batches with the
images around it: a panel and the icon on top of it are one draw as long as they come from the same
sheet. Nine quads instead of one, in a run that was going to exist anyway. A kind of its own would
have split every batch it appeared in, to describe geometry the renderer never sees.

⚠ **Two insets, and the second one is in UVs.** A border is a distance on the screen and a distance
into the texture at once, and those are the same number only when the image is drawn at its own pixel
size. Converting between them needs the texture's dimensions, which is the one thing this assembly
refuses to know — the same bargain `DrawCommand.Source` makes — so the caller who registered the
texture divides.

⚠ **Stretched, never tiled**, for the same reason: a repeat count is destination ÷ natural size, and
the natural size is in texels. Tiling lives where the pixel size does, in `SpriteGeometry`.

The destination inset is fitted to the box and the source inset is not, so a panel drawn narrower
than its own two corners shows them compressed rather than quietly reading different texels — which
would look like the artwork changed rather than like the box got small.

## Batching

**Runs of consecutive commands, and never a reordering.** Worth being blunt about, because reordering
is what batching means everywhere else: a 3D renderer sorts draws by material because a depth buffer
decides what ends up in front. A user interface has no depth buffer. Order *is* the answer to what is
in front, so moving two runs of the same font together across the panel between them draws the text
over the panel that was supposed to cover it.

So the win is bounded and honest. A hundred alternating labels and boxes batch into two hundred
batches, and that is the correct answer rather than a failure to optimise — what improves it is
emitting fewer interleavings, which is a question for whoever writes the controls.

**The batches partition the commands**: every command is in exactly one, in order, so a consumer
walks the batches alone and never has to fall back to the command list to find what it missed. That
is why a clip gets a batch of its own rather than being skipped.

**Behind the frame diff, not beside it.** Batching walks every command in the interface, and a frame
that drew the same thing has the same batches by construction — so the cached command buffer the
version exists to protect keeps its batches with it. `Batched` counts the rebuilds, because a claim
about work avoided that cannot be measured is one nobody can check.

⚠ **`BatchKind` is a coarse stand-in for two of the four things that decide a pipeline.**
`Vixen.Rendering` already answers this: a pipeline is keyed on the effect, the stage, the vertex
layout and the render output. Only two of those are a draw list's to know — which shader and which
vertex format — because the stage carries blend, depth and raster state and the output carries
attachment formats, and both belong to the compositor. Rectangles and borders are grouped as
signed-distance quads, filled and stroked paths separated as different tessellation work; both are
claims about a shader nobody has written. **What this must not do is grow to describe the other two.**

⚠ **The renderer does not use a batch list, and the difference is the point.** `MeshRenderFeature`
walks its nodes in sorted order and re-binds only when the pipeline handle changes — the same runs,
with two locals and no array. That is right for a mesh, whose nodes are rebuilt from culling every
frame so nothing precomputed would survive. A UI is the opposite case: most frames draw exactly what
the last one drew, so the runs are worked out behind the frame diff and a still interface pays
nothing. If the UI render feature binds on change anyway, this is what stops it regrouping every
frame; if it does not, this is the thing to delete.

⚠ **`RenderSortMode.ByGroup` already says "for UI and anything else already ordered"** — depth left
out, sorted on a group value alone, stably. So the UI render feature has to make that group *be* the
painting order: a group meaning a material or a texture would reorder the interface on the way to the
screen and undo everything above. The batch index is that number.

What is *not* a guess is that batches are contiguous, ordered and maximal, which holds whatever the
grouping turns out to be.

## The draw list

The last step of the chain: the cascade said what applies, the bridge turned it into lengths, flexbox
turned those into rectangles, and this turns the rectangles into commands. Nothing here decides
anything — it reads.

**Three ways to be unpainted, and they are not the same.** `display: none` arrives as a zero
rectangle from flexbox and takes the subtree with it. `visibility: hidden` skips the element's own
background, border and text but still descends — it is inherited, so a child is hidden by having
inherited the value, and a child that declares `visibility: visible` reappears inside a hidden
parent. `visibility: collapse` is the same thing as `hidden` here, per CSS 2.1 §11.2 — the keyword
only differs on a table row or column, and there is no table formatting context. `opacity: 0` skips
the subtree outright, because opacity multiplies and nothing below can bring it back.

⚠ **Only one of the three is also a hit-testing rule, and it is `visibility`.** CSS UI §5.2 makes an
invisible box untargetable, so `IsHitTestVisible` reads the property alongside `pointer-events` —
per element, for the same inheritance reason, so a `visible` island inside a hidden subtree is
clickable again. `display: none` never reaches a hit test because layout gave it no rectangle to be
inside, and `opacity: 0` deliberately stays clickable: CSS keeps a fully transparent box a pointer
target, which is what makes an invisible drag handle work.

⚠ **An element's own text is inside its own clip, and for a long time it was not.** `overflow` clips
an element's *content*; the background and the border are the two things it does not clip, which is
why the `ClipPush` sits between them and the text rather than above all three. Emitting the text
first meant `overflow: hidden` clipped an element's children and never its own string — so a label
too long for a fixed-width column drew straight across whatever was beside it, and five panels in
the editor had written `overflow: hidden` on a text-bearing element believing otherwise.

**It survived every kind of test this framework had**, and the shape of that is worth copying: *a
clip is invisible to the element tree*. Every rectangle was the right size, every string was the
right string, and the glyphs went somewhere nothing was looking. It was found by taking a picture of
a key/value row, and the regression test is an assertion about the *order* of the commands, which is
the whole of what a clip is.

⚠ **Opacity is carried down as a multiplier rather than composited as a group, and the difference is
visible.** CSS renders a translucent element's subtree into its own surface and blends that once, so
two overlapping children of a half-opaque panel do *not* show through each other. Multiplying each
element's alpha instead makes them show through. The two agree exactly whenever the subtree does not
overlap itself — most interfaces, and all of the ones a fade-in is applied to. The correct version
needs an offscreen target per translucent subtree, which is a compositor decision rather than a draw
list's, so it is **owed**. Said plainly because a half-right opacity reads as a bug in the renderer
rather than a gap in the model.

**Painting order is document order**, and hit testing walks it in reverse. The two have to agree: the
element drawn last is on top, so it is the one a click lands on, and any rule that made them disagree
would be a UI where things are not where they look. One test asserts both at once.

**The frame diff is against the previous content, not against a dirty flag.** A flag says what the
framework believes changed; the content says what actually did, and the two part company exactly when
something is invalidated too eagerly — which is the failure a cache is supposed to absorb rather than
propagate. `Version` changes when the drawing changes and not when it is merely rebuilt, so a
renderer compares one integer.

⚠ **`border-color` and `border-radius` are expanded before they are interned**, the same way `margin`
is — by ExCSS while parsing when the value is literal, and by `ShorthandExpansion` at load when it
holds a `var()`, which ExCSS is obliged to hand back whole. Written against the shorthands, every
border and every rounded corner in the document silently disappears.

⚠ **All eight longhands are read, and reading only the first of each set was worse than a subset.**
The builder used to intern `border-top-color` and `border-top-left-radius` alone. That made
`border-b-<colour>` inert, as you would expect — but it also made `border-top-width` paint a ring on
all four edges, made the other three widths paint nothing at all, and made `border-top-left-radius`
round the whole box while the other three corners were ignored. Twenty-one rules in the editor's own
themes were written against the three that drew nothing, including the selected-tab underline.

**A box whose four corners agree stays cheap.** `DrawCommand.Radius` is one `float` and it is still
what nearly every box uses; only a box whose corners differ, or whose corners are elliptical, gets an
entry in `DrawList.Boxes`. `CornerRadii.IsUniformCircular` is the test, and it insists on circular
rather than merely equal because four equal ellipses are still not one number.

**A border whose four edges agree stays one command.** Equal widths and equal colours are a single
`Border` — one quad, one distance field, one antialiased outer edge shared with the fill beneath it.
Edges that differ are drawn as up to four `Rectangle` bands instead, because the box shader resolves
a ring from one thickness and one colour and has no per-pixel notion of which edge a fragment belongs
to. ⚠ The bands are mitre-less: the horizontal edges take the corner squares and the vertical ones
are inset between them, which is the join CSS draws whenever the two edges meeting at a corner are
the same colour. The difference shows only where two adjacent edges are both thick *and* differently
coloured. Giving the shader a real mitre means four more colours and four more thicknesses in
`UiShape` — eighty more bytes on a record every box in the frame writes, to describe something almost
none of them have.

**`opacity` is multiplied down the walk, not read from the cascade.** It does not inherit — it makes
a group, and every descendant is in it whatever its own value says, so an element's alpha is the
product of its ancestors' and its own. A fully transparent subtree is skipped entirely, which is the
one case where the cheapest thing to do is also exactly right.

⚠ **And it is applied per command rather than per group.** CSS composites an element and its
descendants into a layer and fades that *once*, so two overlapping children of a half-transparent
parent show the background through both together; here each command carries the multiplied alpha and
the overlap is drawn twice, coming out darker than a browser would draw it. Doing it properly needs
an offscreen target per element that has an opacity, which is a renderer feature rather than a
builder one. Said plainly because the difference is invisible until something overlaps, and then it
looks like a blending bug rather than a known limit.

Fading is `alpha` on the colour and not `colour * alpha` — the operator scales all four components,
which is right in premultiplied space and would darken towards black here.

**A `box-shadow` is the same quad and the same distance field as a box**, with the one-pixel edge
widened to a blur radius. The offset and the spread are folded into the command's rectangle and the
spread into its radius, so what reaches the geometry is an ordinary rounded box that happens to be
soft — which is why a shadow needs no fields on `DrawCommand` that a box does not have, and why it
batches with its own background instead of splitting the frame.

⚠ **The quad is grown by twice the blur.** Coverage reaches zero a blur out from the boundary, but
the falloff is centred on it, so the visible tail runs a blur *beyond* where the edge sits. One blur
of margin leaves a faint straight line where the quad ends, which reads as a crease in the shadow.

⚠ **The command's thickness is half the CSS blur radius.** CSS's blur is the total distance the edge
fades over and the shader's is the half-extent either side of it; passing the whole radius through
makes every shadow twice as soft as it was asked to be, which reads as a blurry renderer rather than
as a unit mistake.

⚠ **One shadow, outer only, and not clipped to outside the border box.** CSS takes a comma-separated
list and an `inset` keyword; a list would be a command each, which is easy, and `inset` is a
different distance field, which is not — so both are refused rather than half-applied, because the
first shadow of a list being drawn and the rest silently dropped looks like it worked. The `inset`
half is the parity ledger's `inset-shadow-*` seen from inside the draw list rather than a second
decision, so it expires when that row does — `[expires-with inset-shadow-*]`, and
`RefusalExpiryTests` reads this sentence. And CSS
punches the box out of its own shadow, where here the blurred box is drawn whole with the background
on top: visible only under a background that is not opaque.

## Input

**Front to back means the last child first.** A later sibling is painted over an earlier one, so it
is the one a click lands on; testing in document order returns whatever happens to be underneath.

**`UiElement.PaintOrder` is the one place that order is decided**, read forwards by the draw list and
backwards by hit testing. The two have to agree — an element drawn on top must be the one a click
lands on — and the cheapest way to guarantee that is for neither of them to have its own opinion.
Document order costs nothing: with no `z-index` among the children it *is* the children list, not a
copy, and the sorted list is built only when some child has an index and cached until something
changes. The sort is stable, so lifting one child leaves every other exactly where it was.

⚠ **`z-index` orders siblings, and only siblings.** CSS lets a positioned descendant paint above an
element that is not its parent's sibling — what a dropdown escaping its row relies on — and that
needs stacking contexts, which needs the whole of CSS 2.1 Appendix E. Here a high index lifts a child
above its brothers and no further, so an overlay that must cover the window belongs to a container
near the root rather than to the row that opened it. The two models agree until the moment they
matter, which is why this is written down rather than left to be discovered.

⚠ **And it applies to every element, not only positioned ones.** The CSS restriction exists because a
static element establishes no stacking context for the index to be measured in; sibling ordering
needs no such thing, and demanding `position: relative` before `z-10` did anything would be a rule
with no reason behind it here.

⚠ **Being outside an element is not a reason to skip its children.** `overflow: visible` is CSS's
default and means precisely that a child may hang outside its parent and still be drawn — so it must
still be clickable. Returning early on a missed parent makes every dropdown, tooltip and popover
unhittable, and the bug looks like the click landing on whatever is behind them. The clip is asked
about on the *parent*, because it is the parent that clips and the child has no idea it is being cut.

⚠ **And it is outside *on a clipped axis*, not outside at all.** `overflow-x` and `overflow-y` are
real, so an element can cut one pair of edges and not the other — a point beside an `overflow-y:
hidden` panel is inside the part of the plane that panel draws in and has to stay clickable. Painting
and hit testing resolve the three properties through one object, `OverflowReader`, for the reason two
copies of one rule always eventually give: a control that is visibly clipped and invisibly clickable,
or the reverse. Written on the clip stack, an unclipped axis is a pair of edges at
`DrawListBuilder.UnboundedClip` — a finite stand-in for infinity, because the stack intersects
rectangles and `float.MaxValue + float.MaxValue` is an infinity that becomes a NaN and unclips
everything below it. The stand-in is exact rather than approximate: the stack starts at the viewport
and only ever narrows, so an edge past the viewport *is* the viewport.

⚠ **Neither axis coerces the other, where CSS's would.** A browser computes a `visible` to `auto`
when its partner is not visible, because a scrollport is one rectangle and painting outside it on one
axis alone is undefined there. There is no scrollport here — the clip is a rectangle and one axis
alone is expressible — so `overflow-x: auto` means what its author wrote instead of also hiding
everything below the box. The other departure is order: nothing expands `overflow` into its two
longhands on the way in, so the computed style holds whichever of the three a rule set and no record
of which came last, and a named axis therefore wins unconditionally.

⚠ **`overflow: auto` is `Overflow.Scroll` to the layout, and it used to be nothing at all.** The draw
list clips on any value that is not `visible`, so `auto` always clipped; the layout's keyword table
had `visible`, `hidden` and `scroll` and not `auto`, so flexbox went on treating the box as visible —
half a property, silently. `auto` and `scroll` are one layout mode in CSS too, differing only in
whether a scrollbar gutter is reserved, and nothing here draws a scrollbar of its own. What the
layout does with it is the CSS Flexbox §4.5 opt-out and the fit-content size of a scroll container,
each on **one axis**: §4.5 is about the main axis, so `overflow-y` on an item in a row says nothing
about whether it may be squeezed sideways.

⚠ **A clip is still not a scrollbar.** `overflow-y: auto` on a plain element cuts its content off and
offers nothing to scroll it — `ScrollView` is the control that owns bars and offsets content, and it
deliberately reads no `overflow` of its own.

⚠ **`pointer-events: none` is transparent without making its children so.** That asymmetry is what
makes an overlay usable; treating the subtree as one unit either blocks everything under a
full-screen layer or lets clicks through a modal.

**A captured pointer goes to the capturing element wherever it is** — a drag that leaves the
scrollbar it started on must keep reaching the scrollbar. Hit-testing during a drag is the bug
capture exists to prevent.

Handlers are invoked by index with the count re-read each step, because unsubscribing from inside a
handler is the ordinary case and a `foreach` throws part-way through delivering the event that
caused it.

Doc 09 asks for a quadtree over the top level. This descends the tree instead, entering only subtrees
that contain the point. The doc says the simple version was "measured to be sufficient"; **that
measurement has not been taken here**, and it should be before the quadtree is written.

## The property system

A plain C# property is invisible to everything that has to find it at runtime — a stylesheet naming
it, an animation targeting it, a binding writing it, an inspector listing it. `[UiProperty]` gives it
an identity without giving up the typed accessor: `element.Radius` is a field read, and
`key.SetValue(element, 4f)` reaches the same field.

Generated rather than reflected, and generated rather than rewritten — Stride builds the equivalent
with a runtime `DependencyPropertyFactory`, and ADR-002 rejects that whole category.

⚠ **Storage is a field, not a sparse table**, which is the opposite of what WPF does. A
dependency-property table pays a dictionary probe per read to save memory on the hundreds of
properties a WPF element declares and never sets; a Vixen control declares perhaps a dozen, there are
10⁴ elements, and reads happen every frame. The table is the more famous design and the slower one.

⚠ **Inheritance is a typed walk, not a name lookup.** Each inheriting property emits its own loop
testing `ancestor is TOwner`, so `Panel.Tint` finds the nearest `Panel` — and an `Overlay` that also
declares a `Tint` is not it. Keyed on the name, it would have found the wrong one and been
confidently right-looking.

⚠ **The old value is read through the property, not out of the field.** On an element that has only
ever inherited, the field is still empty, so comparing against it reports a change from zero on the
first write that agrees with the parent — a spurious invalidation on every element that matches its
ancestor.

**Construction and registration are two steps.** An element must be registered with both trees, which
needs a document; a base constructor taking one plus two internal node handles would put those
handles in every subclass's signature, in assemblies where they are not visible. So `UiElement()` is
parameterless and `UiDocument.Create<T>` binds it afterwards — which is also the shape markup needs,
since a generated `new Button()` cannot know a document either.

## The pass

Four walks, and they cannot be merged. The cascade needs parents resolved before children because
inheritance reads the parent's resolved table. Font size needs the same order for the same reason and
cannot fold into the cascade, because it is a *computed* value the cascade has no opinion about. The
layout style depends on the font size. And layout is the flexbox algorithm, which is not a walk.

**An unchanged document does nothing on the next frame**, and one changed class rebuilds one element.
That is what `ComputedStyle` being interned buys: two elements that resolved alike hold the same
object, so the test is a pointer comparison rather than a walk of a property table. `StylesApplied`
reports the count, because a claim about work avoided that cannot be measured is a claim nobody can
check.

⚠ The font size has to be part of that test as well as the style. An element whose own declarations
did not change still needs rebuilding when an ancestor's font size did, because every `em` on it
measures against a different number now — its computed style is the same interned object, so a check
on the style alone skips it and `2em` keeps meaning twenty pixels while the text around it doubles.

⚠ **`StylesApplied` is not `StylesResolved`, and reading the first as the second hid a defect for two
phases.** `StylesApplied` counts elements whose *layout style was rebuilt*, which the interning makes
a pointer comparison — so it reads 1 for a one-class change however the styles were arrived at.
`StylesResolved` counts the cascade that produced them. The claim "one changed class rebuilds one
element" was true the whole time the pass underneath it was cascading all ten thousand.

## The cascade, incrementally

The document **records what changed rather than that something did**. A class change and a state
change are the two mutations `StyleUpdater` can narrow, so `AddClass`, `RemoveClass` and `State` put
an entry in a log; the next pass replays it through the updater, which restyles what a rule could
have noticed and stops descending wherever the resolved style came back as the same interned object.

Everything else — a new element, a removal, a move, a reparent, an inline style, a stylesheet — comes
through `UiDocument.Invalidate` and costs a cold pass over every live node. That is correct for all
of them: the updater narrows a change to *an existing element's* names or state and cannot express
any of the others, and an element created this frame has no resolved style for an invalidation root
to reach. Widening `StyleChangeKind` is how the remaining ones would get their own path.

⚠ **Each recorded change is replayed as its own pass, not merged into one.** The invalidator answers
"what could *this* have reached", and the sharing cache has to be cleared between them because an
entry cached while resolving the first knows nothing about the second having happened. It is correct
to replay them in a batch only because the tree is fully mutated before any of them run — every pass
resolves against the final state, so the union of what they reach is what a cold pass would have
produced. Replaying against a tree being mutated in step would not be.

⚠ **A scroll resolves nothing at all.** An offset moves where boxes are drawn and cannot change what
any selector matches, so `OffsetX`/`OffsetY` ask for a pass with `InvalidatePositions` rather than
`Invalidate`. Before that existed the only way to ask for a frame was the conservative door, and
every frame of a scroll re-resolved the document.

**Gated through `UiDocument` rather than through `StyleUpdater`.** `Vixen.Ui.Styling.Tests` has run
this same property against the updater since Phase 4b — and stayed green for two phases while
`Update` called `StyleEngine.ResolveAll` and never touched the updater at all. A test that reaches
for the updater passes with the wiring deleted, so `IncrementalDocumentTests` drives the document:
mutate a tree, compare every resolved style against a second document built directly in the final
state, and assert the pass really was incremental.

## Surfaces: several windows, one document

A `UiSurface` is a rectangle the document is laid out into, drawn onto and clicked in. A new document
has one — the primary, whose root *is* `Document.Root` — and `CreateSurface` adds another for every
extra window.

**A window is a surface rather than a document of its own, and that is the load-bearing decision.**
A panel dragged from the main window into a torn-off one has to keep its scroll offset, its
selection and whatever the user has half-typed, and the only operation that preserves those is
`Reparent`, which is *within* a document by construction. Making a window a surface turns "move a
panel to another window" into the reparent a docking host already performs.

So every surface after the first is an ordinary element under `Root`. One style tree: a torn-off
panel inherits the theme, matches the same stylesheets and resolves `rem` against the same root. One
focus, one pointer capture, one gesture recogniser — which is what lets a drag that starts in one
window finish in another. What a surface root does *not* do is take part in its parent's flex
layout: it is removed from the layout tree's child list and laid out on its own, against its own
size. Three passes stop at that boundary — the accumulator, the hit test and the draw list — and
`UiElement.SurfaceRoot` is what they ask.

⚠ **So a parent that owns a window has two different child counts, and every writer that turns an
element index into a layout index has to say so.** The layout child list is the element child list
with the surface roots struck out, in the same order — appending preserves that for free, which is
why `Adopt` needs nothing, and an insertion *at a position* does not. `LayoutIndexOf` is the one
conversion; `Reparent` and `Move` are its two callers and both had it wrong. `Reparent` threw, which
meant the headline operation surfaces exist for — a floating window's panels docked back into the
element that owns the window — failed outright. `Move` threw only when the index ran off the end of
the shorter list and otherwise put the element in the wrong place in silence, which was worse: it is
reached by `HotReloadHost` restoring a rebuilt component's position, by `MenuPresenter` and
`ToolbarPresenter` pulling their strip back above the workspace, and by every markup region that
inserts at an index. `SurfaceIndexTests` asserts on geometry rather than on child counts, because an
off-by-one that still fits inside the shorter list changes no count and throws nothing.

⚠ **And a surface root being moved touches the layout tree not at all.** It is in no child list to be
moved within, so the removal half is a no-op and the insertion half would lay a whole window out
inside its new owner's flex line. Both writers skip the layout store entirely when the element being
moved is a surface root, and do the style and element halves as usual — `:nth-child` counts the
surface root, because the style tree holds it.

`vw`, `vh` and `%` are the surface's own. `50vw` in a torn-off inspector means half of *that*
window; resolving it against the main one would size a 400-pixel palette against a 3840-pixel
display.

**DPI is per surface, because two windows are routinely on two displays.** It is not a scale
anything above the renderer applies — lengths stay in logical points everywhere — it is the grid the
finished layout is snapped to, written into `LayoutTree.PointScaleFactor` before each surface's
layout call. ⚠ Changing it also needs `LayoutTree.Invalidate`: nothing an element *declared* changed
when a window was dragged onto a 2× display, so `SetStyle` compares equal, nothing is dirty, and the
rounding pass — which is what reads the grid — never runs.

A real operating-system window is asked for through `IUiWindowHost`, which this assembly declares and
cannot fill: `Vixen.Platform` is a layer above `Core/`, and a UI framework that referenced it would
stop being usable with no backend at all. `Vixen.Platform.Ui` is what fills it. `CanOpenWindows` is
false on a browser tab, an Android activity and iOS, and a control that wanted a second window is
expected to have something to do instead.

## Removal

`UiElement.Remove()` takes an element and its subtree out of all three stores at once — which is why
it lives on the document rather than in any of them. One that left either store behind would keep
matching selectors or keep taking up space in a flex line while being gone from the document.

⚠ **A removed style slot is tombstoned and never reused**, and that is the design decision rather
than a shortcut. The obvious implementation is a free list, and it would quietly break three separate
things that all rest on one unwritten invariant — *a parent's index is lower than its children's*.
`ResolveAll` walks slots ascending because that is parents-before-children and inheritance needs it;
the incremental pass uses the index as a queue priority for the same reason; and the bloom sweep
gives up the moment a climb passes below the ancestor's index. Fill a hole with a new child of a
later parent, and the first two resolve a child before its parent while the third answers "not a
descendant" about something that is — a descendant selector that silently stops matching.

So slots leak, `StyleTree.DeadCount` says by how much, and **compaction is the fix rather than
reuse**: rebuilding the arrays without the dead slots preserves relative order, which is exactly what
reuse does not. Owed.

⚠ **The layout tree reuses its slots and the style tree cannot**, and the asymmetry is not an
oversight. The layout algorithm descends from the root, so it never cared what order the slots were
in; the cascade walks the array by index and reads each parent's resolved table, so for it the slot
number *is* the ordering.

⚠ **`IndexInParent` has to come down with the removal.** It is what `:nth-child` and the sibling
combinators read, so a stale one leaves the third item of a list still believing it is the fourth
after the second is deleted.

**Whatever was pointing at it has to stop.** The focus, a captured pointer and a gesture in progress
each name an element and each outlives it unless something says otherwise — and each has to be
checked against the whole subtree, not the element itself, because a dialog closing takes the focused
field inside it. A drag whose target is removed ends *silently* rather than as a cancellation: a
cancelled drag tells its target to put back what it was carrying, and the target is the thing being
deleted.

**A removed element throws rather than answering.** Its node ids address slots the layout tree has
already handed to someone else, so answering means reading another element's width and restyling a
stranger — a wrong answer rather than an absent one.

⚠ **The frame pass walks the tree rather than a list in creation order**, which removal forced and
which should have been there anyway. The list version was correct only because elements were created
parents-first and never removed, so its index order happened to be its depth order. The property the
pass needs is "parents before children", and a descent is that by construction rather than by
coincidence — and it deletes two parallel arrays, since what each element had applied last time now
lives on the element.

## Lifetime: a disposed document says so

`UiDocument` is `IDisposable` because `LayoutTree` is: the store is four `NativeArray`s and the GC
cannot see any of them. Disposing the document frees them.

⚠ **Calling into a disposed document used to abort the process rather than throw**, and the failure
destroyed the evidence that would have named it. `LayoutTree.Dispose` frees the four arrays and
zeroes its capacity but leaves the struct fields holding the freed pointers — so the next
`CreateNode` grows from a capacity of nought, finds the arrays non-empty, copies out of memory that
is no longer ours and frees it a second time. The allocator aborts. There is no managed exception, no
message and no stack; the run ends with `SIGABRT` and output stops mid-sentence. Disposing twice
reaches the same double free by the shortest possible route, and `IDisposable` promises that is
allowed.

⚠ **The minute of silence in front of the abort belongs to the test runner and not to the document.**
The abort is instant — a standalone process dies within a millisecond of the call. What waits is
`xunit.runner.visualstudio`'s `TestProjectConfiguration.CrashDetectionSinkTimeoutOrDefault`, 60 000 ms,
before the adapter gives up on the dead test host and prints *Catastrophic failure: Test process
crashed with exit code 134*. A minute of nothing followed by an abort reads identically to a deadlock,
to a native crash in the RHI and to a test-host timeout, which is where the first hour goes.

So `UiDocument.Dispose` is idempotent, and a `disposed` field is checked at the entry points: the
loads, `Update`, `Tick`, `Draw`, the surface calls, and the four tree mutations `Adopt`, `Move`,
`Remove` and `Reparent`. **At the entry points and nowhere below them** — a pass walks every element
several times over, and a check inside one of those walks would be a branch per element per frame to
catch a mistake that can only be made once, at the top.

⚠ **`DocumentLifetimeTests` never performs the abort, even to check that it no longer happens.**
Written the obvious way — dispose, then `Add` an element — such a test proves the fix today and, on
the day it regresses, does not fail: it kills the run, after a minute, with no test name attached. So
each call is made in a form its own next line would refuse — a null element, an owner from another
document — which makes the exception *type* the assertion and keeps the process alive to report it.

The deeper fix is `LayoutTree.Dispose` clearing its four `NativeArray` fields rather than leaving
them holding freed pointers; a disposed store would then grow a fresh set instead of copying out of
dead memory, and the abort would stop being reachable at all. That is `Vixen.Ui.Layout`'s to make.

## What the bridge is for

**`em` on `font-size` means the parent's; everywhere else it means the element's own.** So font size is
resolved first and separately, and the caller walks the tree passing each element's resolved size to
its children. Conflating the two compounds: three nested `font-size: 1.2em` come out at 1.2× rather
than 1.728×, and the error grows with depth, so it reads as a rendering quirk rather than an
arithmetic one.

**Percentages are not resolved.** A percentage measures against the containing block, which only the
layout pass knows, so `50%` is handed on as `LayoutUnit.Percent` untouched. This is the one place
where doing less is the correct behaviour rather than an omission.

**An unparseable declaration leaves the initial value alone.** Zero is a perfectly good answer that
happens to be invisible, so using it for "I did not understand this" turns one typo into a missing
element with nothing said about it.

## Stylesheets: what the document does on the way in

Two things happen to a sheet between `Load` and the parser, and both exist because the alternative
was silence.

**`@apply` is expanded.** `ApplyExpander` lives in `Vixen.Ui.Styling.Utilities` and used to be
reachable only from the build step, over files an MSBuild item named and no project set — so the
at-rule was inert in every hand-written sheet in the tree and said nothing about it. The document
runs it over every sheet, through `StyleEngine.Preprocessor`, which is also what makes it survive a
reload. `Core/Vixen.Ui/StyleApply.cs` holds the reasoning; the part worth knowing from outside is
that the expansion is measured against **every** `@theme` in the document, not the ones that had
arrived when the sheet did — `EditorShell` installs its token sheet third of three, and expanding in
arrival order would give the first two the shipped palette's numbers and no error.

**Refused rules are logged.** `StyleSheetLoader` and `SelectorCompiler` have always recorded what
they had to drop, and nothing outside the tests had ever read either list. `UiDocument` drains both
onto log event 7004 after every load and reload, and drains `ApplyExpander`'s onto 7005 — see
`StyleDiagnostics.cs` and `docs/manual/log-events.md`. The channel is `ILogger`, so it lands in the
`RingBufferSink` the editor's Console panel reads and the crash reporter dumps; a document
constructed without a logger reports into `NullLogger` and behaves exactly as it did before.

⚠ **`LayoutStyleBuilder.Diagnostics` is the third list of the same shape and is still unread.** It
is produced inside the per-element pass rather than at load, so it wants a drain point in `Update`
rather than in `Load`; that is issue #56, and `Drain` takes the source name as a parameter precisely
so closing it is one call rather than a second mechanism.

## What it found

⚠ **Yoga's initial values are not CSS's, and they differ in four places.** `flex-direction` is
`column` against `row`, `align-content` `flex-start` against `stretch`, `position` `relative` against
`static`, `box-sizing` `border-box` against `content-box`. `Vixen.Ui.Layout` is right to start where
Yoga starts — it is judged by Yoga's conformance suite — and this is the boundary where a VCSS
author's expectations take over, so `LayoutStyleBuilder.CssInitial` exists and `LayoutStyle.Default`
is not what an element with no declarations gets. Starting from the wrong one produces stylesheets
full of redundant declarations by an author who decided the engine was quirky and never reported it.

⚠ **ExCSS expands the box shorthands, and the gap that was predicted does not exist.** The bridge was
first written to expand `margin`, `padding`, `border-width`, `gap` and `flex` itself, on the
reasoning that the cascade stores shorthand and longhand as separate properties and the layout store
resolves edges by fixed precedence rather than document order — so `margin-left: 0; margin: 8px`
would give zero where a browser gives eight. **Its tests said every one of those paths was dead.**
ExCSS expands on parse, exactly as a browser does, so by the time the cascade runs that is two
`margin-left` declarations and the later one wins. The prediction was reasonable and wrong, and the
only reason it did not become a documented "known limitation" is that the test was written before the
claim was believed. `inset` is the exception, because ExCSS does not know the property.

⚠ **CSS has a unit that begins with the exponent character.** The value parser scanned `e` as part of
a number unconditionally, so `2em` scanned as `2e`, failed to parse, and came back `Unknown` — every
`em` in the document silently dropped. `1e2px` still has to work, so the fix is to test whether digits
follow rather than to drop the exponent.

⚠ **`aspect-ratio: 16 / 9` arrives as `16/9`.** ExCSS normalises the spaces away, so a parser that
splits on whitespace sees one token. Read here rather than by teaching `StyleValueParser` that `/`
separates values — it does in CSS, but making it a general separator changes how every shorthand
parses.

⚠ **This cascade inherits specified values; CSS inherits computed ones.** A child inheriting the text
`font-size: 1.5em` resolves that `em` against its own parent a second time, so a size meant to apply
once compounds at every level — two deep comes out at 2.25× where CSS says 1.5×, and the error grows
with depth. CSS avoids it by computing `font-size` to an absolute length before anyone inherits it,
so `font-size` was removed from `InheritedProperties` and is inherited here in computed form instead.
An element that declares none simply keeps its parent's resolved pixel size, which is both what CSS
means and simpler than what was there.

**`line-height`, `letter-spacing`, `word-spacing` and `text-indent` have since joined it**, computed
and inherited by the same mechanism — `UiElement.LineHeight` and its three siblings are the resolved
pixels, and an element that declares none passes its parent's straight through. All four are read by
the text layout, so the bounded one-level error they used to carry was one the renderer could see.

⚠ **`line-height` is the one where computing is not simply resolving.** A *unitless* `1.5` inherits as
the number and is multiplied by each descendant's own font size; `1.5em` and `150%` inherit as the
length the ancestor resolved once. That distinction is the entire reason the unitless form exists, so
the computed value carries which of the two it is rather than collapsing both to pixels. A 10px panel
with a 30px label inside gives the label 45 from the first and 15 from the second.

⚠ **And percentages are resolved here rather than by `LengthContext`**, which deliberately refuses
them: there a percentage means the containing block, which only layout knows. On `line-height` it
means the font size, which the pass has in its hand.

⚠ **Changing them has to dirty the layout node by hand.** They are inherited outside the cascade, so
a label whose *parent* changed `line-height` has an unchanged — and still reference-equal — computed
style. The pass's usual test passes, `SetStyle` is never reached, and the label would keep measuring
itself at the old height for the rest of its life. Only nodes that measure themselves are marked,
which is what `MarkDirty` insists on and what having text means.

⚠ **That gap is closed, and `word-spacing` closing it was a *removal* from `InheritedProperties`
rather than an addition to it.** The sentence here used to say the gap stayed open for `word-spacing`
and `text-indent` because nothing read them, and that computing a value no consumer looks at would be
work with no way to be wrong. The second half was true of `text-indent` and false of `word-spacing` —
which was in the specified-value list the whole time. It takes relative units exactly as its three
siblings do, so `word-spacing: 0.5em` on a panel would have re-resolved against every descendant's own
font size and compounded down the tree, and it would have started doing so silently on the day a
consumer landed. `TextRun` is that consumer now: CSS Text 3 § 8.2's word-separator characters, which
are the space and the no-break space and deliberately not a tab.

⚠ **`text-indent` still refuses a percentage**, which is the one value of it this engine cannot
answer: CSS resolves one against the containing block's width, and that is a layout result the style
pass does not have. See `UiDocument.ResolveText`, which lands on the initial value rather than
guessing — and note that the refusal is *silent*, since nothing reports the dropped declaration.

**And relative units belong in `StyleValue` after all.** They were deliberately left out, on the
argument that resolving them needs a context that does not exist at parse time. That was right about
resolution and wrong about representation, and **transitions settled it**: the animator interpolates
`StyleValue`, so a unit the type cannot express is a unit that cannot animate. `width: 2em` under a
`transition` snapped while its neighbours eased, with nothing said about it.

## How it is tested

Through the whole path — write CSS, read a `LayoutStyle` — rather than against a hand-built
`ComputedStyle`. A wire is worth testing with something plugged into both ends: a property name no
rule can ever set shows up as a test that will not pass.

Verified by sabotage. Starting from Yoga's defaults, resolving `font-size`'s `em` against the
element's own size, resolving percentages here, swapping `vw` and `vh`, and dropping the
leave-the-initial-value guard each fail it.

⚠ That last one took two attempts, and the failure is the interesting part. Written against a
stylesheet, `width: 4furlongs` never reaches the bridge at all — **ExCSS validates as it parses and
drops what it does not recognise** — so the test passed whatever the bridge did with a bad value,
including overwriting a good one. Rewritten against inline declarations, which are interned directly
and get no such vetting, it still passed: the value has to be one that *parses* but is not a length,
because an unparseable one is already filtered a step earlier. A bare `5` is the case that reaches
the code being tested.

⚠ **"Drops what it does not recognise" turned out to hold for every property but four.**
`align-items`, `align-self`, `align-content` and `justify-content` are the ones ExCSS 4.3.2 models
with a `ConditionalStartsWithValueConverter`, and on a value it half-parses — a bare `safe`,
`unsafe`, `first` or `last`, with or without trailing junk — reading the property's value threw a
`NullReferenceException` out of the library rather than dropping anything. `StyleSheetLoader` now
guards that one read, so the sentence above is true again, but it is true because Vixen makes it so.

## The geometry a renderer submits

`UiGeometryBuilder` is the last step that is still the interface's own: a draw list in, vertices
out. Everything below it is a pipeline, a buffer and a scissor. Being a pure function of a draw list
is what lets all of it be checked without a device.

**Boxes are one quad each, not a tessellated outline.** A rounded rectangle and its border are both
a signed distance the shader evaluates per pixel, so a corner is exact at any radius and costs four
vertices — where tessellating one costs vertices in proportion to the radius and is still faceted.
That is also why the two share a batch kind: one shader draws both, and the thickness decides
whether the inside is filled. The texture coordinate is the offset from the box's own centre, which
is the space a signed distance to a rounded box is written in, so the shader needs no uniform per
box.

⚠ **Clips are resolved here rather than replayed.** A draw list pushes and pops; a renderer sets a
scissor. Carrying the resolved rectangle on each draw means the renderer holds no stack and cannot
be caught out by a batch it skipped having left one behind. A nested push **intersects** rather than
replaces — setting the scissor outright would let a child draw outside the panel containing it.

⚠ **A glyph's position is an offset along its run, not a place on the surface.** The command carries
where the line starts, which is what lets two identical labels in different places hold identical
glyph runs — and therefore what lets the batcher and the frame diff notice they are the same.
Reading the offset as absolute puts every label wherever the first one was; found while writing the
tests, because the first fixture had its run at the origin, where the two are the same thing.

⚠ **The placement is in ems and the pen is in pixels**, so the font size multiplies one and not the
other — and the threshold range with it, or text blurs as it grows and aliases as it shrinks. A
font's y runs up from the baseline and a surface's runs down, so a glyph's top edge is a subtraction.

⚠ **Every glyph the frame needs is packed before a single quad reads a region.** A quad reads its
region the moment it is written, and two things move a region afterwards: compaction moves all of
them at once, and eviction hands one glyph's slot to the next. Interleaved packing and reading
therefore lets the fortieth glyph of a label silently relocate the first thirty-nine, and what draws
is the right letters out of the wrong places — a glyph that was evicted mid-run comes back as
whichever letter took its slot. So `Build` resolves the whole frame's glyphs first and emits second;
the only packing that can happen during emission is none.

⚠ **What that cannot cover is reported rather than retried.** A frame wanting more distinct glyphs
at once than the atlas holds evicts, while resolving, what it is about to draw — so emission puts
them back and can take another one's slot doing it. `AtlasChanged` says so, and it watches the
atlas's *revision* rather than its version, because a version misses the eviction case entirely. A
retry has nothing to converge on: the second pass evicts the way the first did. The answer is a
bigger atlas or a lower field resolution, which belongs to whoever built the cache.

Verified by sabotage: reading glyph offsets as absolute fails 1, a quad that ignores the font size
fails 1, an unflipped baseline fails 1, a threshold range that does not scale fails 1, a nested clip
that replaces fails 1, a clip that is never popped fails 1, a box not parameterised from its centre
fails 1, emitting empty draws fails 3, a dropped glyph that is silent fails 1, removing the resolve
pass fails 2, and an `AtlasChanged` that never fires fails 3.

~~**Owed:** paths.~~ `PathFlattener` and `PathTessellator` are here — curves to contours at a
tolerance the caller chooses, contours to triangles by a trapezoid sweep, filled or stroked, with an
antialiasing fringe. See [Paths and custom drawing](#paths-and-custom-drawing) above.

~~Also owed: a wider index.~~ `UiGeometry.Indices` is `uint`. It was `ushort`, and the builder
refused to emit past 65 535 vertices rather than wrap — which was honest while the index was narrow
and was not a fix, because a dense editor frame really can pass sixteen thousand quads and the
symptom of dropping the rest is a frame missing its bottom half. Thirty-two bits not because a frame
is expected to need them, but because the one that does wraps *silently*, drawing geometry from the
top of the frame in the middle of it.

Licensed under Apache-2.0.

## Composition

`Vixen.Ui.Composition` is the runtime a `.vxml` compiles into, and it is the same API somebody
writing a component by hand would use — the generated half is ordinary, steppable C# rather than
magic (ADR-002).

**`Build` runs once.** That is the whole of ADR-010: no render function to call again, no virtual
DOM, and no reason to walk a tree that did not change. What changes later changes because an effect
ran, and an effect assigns exactly the property it was written for.

**`@if` and `@switch` are one primitive.** `ctx.Switch` takes a selector saying which arm is live and
a builder that constructs it; a condition chain and a pattern match differ only in how the number is
produced. Two constructs for swapping a subtree in and out would be two places to get the disposal
of a branch's effects wrong.

**Keys buy identity, and identity is what a list is for.** An item whose key survives keeps its
element — and so its focus, its scroll offset and its animation state. Without a key the fallback is
the item itself, never the index: an index makes every element after an insertion compare unequal,
which is exactly the failure `VXML2004` warns about.

### A capitalised tag is a component *or* a control

`<Counter />` names a `Component` and `<ProgressBar />` names a `UiElement`, and they are written
identically because the markup compiler resolves no types and is not going to start. `ctx.Child<T>`
takes both, and which one it got is settled by C# overload resolution at the use site rather than by
a registry: `IComposable` is the constraint, and it exists so that a tag naming a type that is
neither is an error on the tag the author wrote.

The two differ in exactly two places, and `BuildContext.Host` and `BuildContext.Inner` are those
places. An attribute on a component's tag applies to the element the component *drew*; on a
control's tag it applies to the control, which is already an element. Children written inside a
component's tag are content projected into its slot; inside a control's tag they are its children.
Both are static overloads that inline away, which is what lets the emitter write one call for a
distinction it cannot see.

⚠ **`on:click` therefore has to mean two things, and `Vixen.Ui`'s table only knows one of them.**
An element's click is a tap; a *control's* click is its activation — Space, Enter, an access key and
a tap, which is what `ClickEvent` exists to be. So `BuildContext.Subscribe` lets a control library
say so, and `Vixen.Ui.Controls` does it from a module initializer. Without that a `<Button on:click>`
works for everybody who tests with a mouse and for nobody who does not use one.

⚠ **And it subscribes to *both*, rather than choosing by the element's type.** ~~Choosing was the
first shape — `element is Control` took the activation and anything else took the tap~~ — and it was
wrong in a way that is invisible from the `<Button>` end: only `ButtonBase` and `ColorSwatch` raise a
`ClickEvent`, so `<Card on:click>`, `<Panel on:click>` and every other one of the thirty-odd plain
`Control`s bound a handler nothing could ever raise. The silent failure the type test was there to
avoid had been moved rather than removed. What stops one press counting twice is
`Control.RaisesActivation`, asked of the element the tap landed on and everything between that and
the listener — a control that reports its own activation has already told the handler, and anything
else has not, so its tap *is* the click. The walk matters because a button's label is a child
element and the hit test lands on it.

**`EventSubscription`, not a `RoutingStrategy`, is what a table entry is handed.** `stop`, `once` and
`self` are filters `On` applies around a handler it already owns; `capture` and `handled` are not —
they are arguments to `UiElement.AddHandler`, and only the entry can pass them. Entries call
`EventSubscription.Listen<T>` rather than `AddHandler` so that forgetting the second one is not
something that compiles.

**A component names its host tag.** `Component.TagName` defaults to the type's name in lower case
and is overridable for the reason `UiElement.TagName` is: a default taken from a type name cannot
produce a hyphen, and `task-center` is not spelled `taskcenter` in anybody's stylesheet. In markup
that is the `@tag` header.

**`Literals` is how a quoted attribute becomes something that is not a string.** `Variant="Subtle"`
is an enum member and `Value="0.5"` is a float; the markup compiler knows neither, so it writes
`Literals.Of(n1.Variant, "Subtle")` and lets overload resolution pick the conversion from the type
of the property being assigned. ⚠ **The first argument exists to be inferred from and is never
read** — C# infers nothing from an assignment target — so a property whose getter does work does it
once at build time, and a property that cannot be read is one to write with `@expr`. A type nothing
converts to is a *compile* error on the attribute, which is what an `object Convert(Type, string)`
would have turned into a run-time surprise.

### A mounted component is findable, and therefore alive

`UiDocument.ComponentAt` answers "which component drew this element", for the elements that are a
component's host. A control *is* the element a caller reaches for; a component is an object beside
the elements it drew, and without this there is no way back from the tree to it — which a test
harness needs, a debugger wants, and a future inspector will.

⚠ **The table is weak on the element, and that is what makes it free.** A component is reachable
for exactly as long as its host is, so a branch that leaves the tree takes its component with it and
nothing has to be told. It also retires the owed item below it: a mounted component's *effects* are
not in the document, so before this the only thing keeping a panel's bindings alive was whatever
reference the caller happened to hold.

### Content goes where the element says

`UiElement.ContentHost` is the control-side mirror of `Component.Content`: itself for most elements,
and the viewport for a `ScrollView`, the panel for a `Popover`. Markup written inside a
`<ScrollView>` means the rows are what scrolls; hung off the control they would sit beside the
scrollbars, laid out by neither.

### Effects belong to the document

`UiDocument.Effects` is where every binding queues, and a frame drains it in one place. The
scheduler's own default is per *thread*, which is the wrong granularity for a document: an editor
has several — a shell, a preview pane, a floating window — and a test process has one per test.

⚠ **This was found rather than reasoned about.** Flushing the thread's queue from one shell's tick
ran the bindings of every other document on that thread, disposed ones included, and turned a
ten-second test run into one that did not finish.

### Regions, and the question "where"

An `@if` in the middle of a `<div>` has siblings on both sides, and the element tree only appends.
So a region knows what it comes *after* and asks: an element answers "one past me", another region
answers "wherever I end", and an empty region defers to its host. Nothing is stored that can go
stale.

⚠ **That last case is not decoration.** A branch that *opens* a loop item follows no element, and
where its item starts is not known until the list is in its final order. An earlier version
snapshotted the position at build time and put every leading branch at index zero of the parent — in
somebody else's item. It is asked for now, and there is a test whose whole job is that shape.

The alternative was an anchor element, which is what the DOM frameworks use. Here it would have to
be a real element in all three stores, and a real element is counted by `:nth-child`. Rows that
stripe wrongly because of a hidden marker is a worse bug than this is complexity.

### Scoped styles

`scoped` on a component's `<style>` puts a class on every element the component built and welds the
same class onto the end of every selector: `.row { … }` becomes `.row.v-1f2e { … }`. So a
component's `.row` cannot reach a caller's `.row` that happens to be inside it, which is the whole
content of the keyword.

⚠ **Welded to the end, not prefixed to the front.** A descendant prefix — `.v-1f2e .row` — reads as
the obvious implementation and is wrong twice: it misses the component's own root, which is the
element a stylesheet most often wants, and it matches a caller's `.row` projected into a slot, which
is exactly what scoping is for.

⚠ **The scope is per type, and so is the stylesheet.** Every instance shares one class because they
share one sheet; a per-instance scope would mean a rule set per row of a list, which is the cost that
made loading it once per instance worth fixing in the first place. `ScopedStyles.ScopeOf` derives the
class from the **full** type name, because two components called `Row` in different namespaces are
two components.

⚠ **Nothing inside `@keyframes` is touched.** Its blocks are keyed by `from`, `to` and percentages,
which are not selectors — appending a class to `50%` produces a rule that parses and never matches,
and the animation quietly loses its middle.

### Owed

A longest-increasing-subsequence pass so a reorder moves a minimal set rather than every surviving
item. It is correctness-neutral: `Region.Reposition` calls `Document.Move` once per element and
`Move` returns immediately when the index has not changed, so an unchanged list already costs a walk
and nothing else. What it costs today is a real move per element on a list that *has* been reordered
— a rotation by one changes nearly every index — and each of those is a layout remove-and-insert
plus a style-tree move.

Also owed: **an ambient value — anything a descendant can read without being handed it**. A theme, an
edit target, a document scale: today each of them is a parameter repeated on every tag that needs one,
which `Samples/02-HelloUi/Shell.vxml` shows at three `Model="@Model"`s and an editor multiplies by
forty panels. ⚠ **Three ancestor walks exist and not one of them generalises**, which is worth writing
down because each looks from a distance as if it might:

- `[UiProperty(Inherits = true)]` emits a walk that matches only ancestors **of the declaring type**
  (`Vixen.Ui.Generators/UiPropertyGenerator.cs`, the `ancestor is <Owner> owner` test), so it inherits
  a property down a chain of one kind of element. It is CSS inheritance, and ⚠ its only producers in
  the tree are three fixtures in `Vixen.Ui.Tests/SampleElements.cs` — nothing ships with it on.
- `UiElement.EffectiveCommandScope` is a real nearest-ancestor walk whose value is one `string?`.
- `UiDocument.ComponentAt` is a dictionary keyed on the exact host element, not a walk: there is no
  "nearest ancestor component of type T" to ask.

What is wanted is a provide/inject keyed by type over the `Parent` walk — the same walk the responder
chain makes, and worth sharing one implementation with it rather than growing a fourth.

Also owed: **fallback content in a `<slot>`**. `<slot name="footer">Nothing yet</slot>` is how every
other framework spells a default, and it is `VXML2017` here — refused rather than supported, because
building the fallback and then removing it if the slot turns out to be filled needs an ordering the
build does not have: whether a consumer filled a slot is not known until the consumer's own build has
run, which is after the component's.

*(Named slot projection was the third item here. ⚠ It was listed as owed on the consumer's side alone
and the declaring side as built, which was true and misleading: `Component.Slots` was written by
`BuildContext.Slot` and read by exactly one line, looking up `default`, so a `<slot name="footer">`
compiled, ran, and could not be filled by anything. `BuildContext.Into` is the other end of it, and
`slot="footer"` on a direct child of a component tag is how a consumer reaches it. Two further items
that used to be here — a component rooted by its caller, and no teardown hook — are also done.)*

### A component leaves when its branch does

⚠ **It did not, and the shape of the bug is worth keeping.** A component's own build goes into a
region hanging off its *host*, which is not a slot of the region being built — so clearing the
enclosing branch removed the host element and never reached what the component put inside it. Its
effects went on running against elements that were no longer in the document, which is precisely
what regions exist to prevent, and `A_branch_that_leaves_takes_its_effects_with_it` had tested it
since regions existed — for plain elements only, which is the one case markup never produces on its
own.

The teardown is a *subscription* on the enclosing region rather than a slot in it, because slot
order is how a region computes indices within one parent element and this region's parent is the
host. `Region.Clear` disposes subscriptions before it removes elements, so the ordering falls out.

`Component.OnUnmounted` hangs off the same call, and runs **before** the teardown: a panel saving a
scroll offset or a selection needs its elements to still be there. It is where a component gives
back what the runtime did not give it — a handler on a model, which nothing else knows exists. An
unmount is not a dispose: the object survives, because a hot reload re-mounts the same instance.

## Diagnostics: what a debug overlay could read today, and what it cannot

Doc 13 calls a UI-debug overlay — *element bounds, layout boxes, style origin for a hovered element,
dirty-region highlight* — "the single most valuable tool for anyone building a UI in this framework",
and adds that it is nearly free because the styling engine already tracks rule provenance. Every
other overlay in doc 13's table is drawn ([#158](https://github.com/Rikarin/Vixen/issues/158)); this
one is not, and [#461](https://github.com/Rikarin/Vixen/issues/461) is where the reason is being
argued out. Two claims that have been made about it are wrong, and stating what *is* here is most of
the design.

⚠ **The assembly seam is not the blocker.** Nobody ever proposed `Vixen.Ui` → `Vixen.Engine`; the
legal direction is Engine → Ui, which [doc 02](../../docs/plan/02-repository-layout.md) prescribes and
`build/Build.ArchitectureRules.cs` permits — the ban is keyed on the *referencing* project's name.
`Vixen.Engine.Renderer` already joins `Vixen.Engine`, `Vixen.Rendering` and `Vixen.Assets` and is
where `GpuOverlay` and `StreamingOverlay` live, so an overlay has a home already built.

⚠ **And "`Vixen.Ui` exposes no statistics, counters or instrumentation surface at all" is not true
either.** What is true is narrower and more useful: the material exists and is *scattered*, one
property at a time, across the `UiDocument` partials that produce it.

| Doc 13 asks for | What is here | Where |
|---|---|---|
| layout-node count | `LayoutTree.NodeCount` | `Vixen.Ui.Layout/LayoutTree.cs:81` |
| the frame's work | `StylesResolved`, `StylesApplied`, `ContainerScopesEntered`, `StyleCompactions`, `SettlingPasses`, `Settled`, `LastPassWasCold` | `Restyle.cs:63`, `UiDocument.cs:387`, `Containers.cs:129`, `UiDocument.cs:821`, `UiDocument.cs:1088` |
| element bounds, box model | `UiElement.AbsoluteLeft`/`Top`/`Width`/`Height`, and the layout node behind them | `UiElement.cs` |
| the hovered element | `UiDocument.HitTest(x, y)`, and `HitTest(surface, x, y)` | `UiDocument.cs` |
| style origin for it | `StyleOrigin`, `CascadePrecedence`, `StyleRuleSet.Origin` — the cascade carries provenance because it needs it | `Vixen.Ui.Styling` |
| refused declarations | the four diagnostic producers and their drains, already routed to the log | `StyleDiagnostics.cs` |
| dirty-region highlight | **nothing survives the invalidation paths.** `Layout.MarkDirty` and `RaiseCommandsInvalidated` record that something changed, never what or where | — |

So the shape that is owed is an **aggregator, not an instrument**: one read-only view that gathers
what the passes already publish, plus exactly one new recording — the dirty regions, which is the
only row above with no raw material behind it.

### Landed: `UiDocument.Diagnostics`

`UiDiagnostics` is that view — `Diagnostics.cs`, and
[the guide page](../../docs/guide/ui/document-diagnostics.md) is written from it. It carries the
counters above, `LayoutTree.NodeCount`, `TryDescribe(x, y, …)` for the element under a point, and
`UiBoxModel` — the four boxes CSS names, because four nested outlines is what an overlay draws and
turning twelve edges into them is the arithmetic that is easy to get wrong once per overlay.

**And the dirty regions are recorded now**, at the four invalidation entry points that know which
element they are about: `AddClass`, `RemoveClass`, the state setter and `InlineStyle.Commit`, plus
`UiDocument.Invalidate` for the cold one, which has no element and records the root. Behind
`[Conditional("DEBUG")]` and `[Conditional("VIXEN_UI_DIAGNOSTICS")]`, so a build that did not ask has
no call site — `World.RaiseCreated`'s shape, chosen because this sits in the path a virtualised list
walks two dozen times a frame.

⚠ **`UiDiagnostics.RecordsRegions` exists because empty has two meanings**, and a panel that cannot
tell "nothing was invalidated" from "nobody was recording" is a panel that reports success on the day
it does not run. ⚠ **And a frame that finds nothing to do empties the regions** rather than leaving
the last real pass's boxes up — the same lie `Update`'s own counters told for a year, and the reason
the ring is turned on *both* of `Update`'s exits.

### Still owed: the overlay, and the reason it is not written here

⚠ **There is no host today that holds a `UiDocument` *and* a `DiagnosticOverlays`**, which is a
sharper statement of what #461 calls "a decision, not a constraint". `AppGraphics.BuildOverlays`
holds the overlays and no document; `UiApplication` holds the document and may not reference
`Vixen.Engine` — `CheckArchitecture` bans it by the referencing project's name, and
`Vixen.Ui.Desktop` starts with `Vixen.Ui`. So an `IDiagnosticOverlay` written today would be
registered nowhere, which is this repository's commonest defect wearing a diagnostics badge. The two
honest homes are the editor's panel system, which already has somewhere to put a view, and a game
host that mounts a document through `UiRenderFeature` — and ⚠ that feature has no registration
anywhere in the tree either, so the second one is a claim about a path nothing currently takes.

Three constraints decide the shape, and each of them rules something out.

**It reads, it does not sample.** `DiagnosticOverlays`' own remark is the rule — *"nothing here polls
or samples; an overlay reads what its subsystem already published"* — and it matters more here than
anywhere else, because `Vixen.Ui`'s reactive graph is single-threaded by contract. A panel that could
touch a signal to answer a question would be able to perturb the document it is describing.

**The read path allocates nothing.** A UI-debug overlay is on for minutes at a time in the frame it is
diagnosing, and a surface that allocated per read would be measuring itself — the trap
[#597](https://github.com/Rikarin/Vixen/issues/597) is about, one level up. That rules out returning
lists or strings from the read path: the matched rules for one element want a span or a caller-filled
buffer, not an `IReadOnlyList<T>` (which is also how the last three per-frame allocations got in).

**Only the dirty regions cost anything when nobody is looking.** Recording a region per invalidation
is work on a frame that has a debugger attached and waste on every other one, so it belongs behind the
same shape `VIXEN_ECS_EVENTS` uses in `Vixen.Ecs` — `[Conditional]`, so the call site is gone in a
build that did not ask for it — rather than behind a runtime `if`. The rest is already being computed
and merely has no reader.

⚠ **And the last step is the one that is usually skipped.** `AppGraphics.BuildOverlays` is where a
host registers a panel, and its rule is written there: an overlay is registered only where the host
holds the object its numbers come from. An aggregator with no `IDiagnosticOverlay` over it and no
registration is this repository's commonest defect wearing a diagnostics badge — a finished thing
nothing calls.

### ⚠ The registration site named above does not exist, and that decides the panel's shape

Two things were true when the paragraph above was written and neither was checked, because both are
facts about `.csproj` files rather than about code.

**`AppGraphics` holds no `UiDocument`.** `Core/Vixen.App.Hosting/Vixen.App.Hosting.csproj` does not
reference `Vixen.Ui` at all — not directly and not through `Vixen.Engine.Renderer` — so
`BuildOverlays` cannot register a UI panel *under its own stated rule*, which is the rule the
paragraph above quotes approvingly. It is the only production holder of a `DiagnosticOverlays` in the
tree: every other construction of one is in `Vixen.Engine.Tests`.

**And the host that does own a `UiDocument` cannot see `IDiagnosticOverlay`.**
`Platform/Vixen.Ui.Desktop`'s `UiApplication` is the framework's own application host, and its remark
says why it exists: *"No `Vixen.Engine` and no `Vixen.App`, and the absence is the reason this
assembly exists."* `IDiagnosticOverlay` and `DiagnosticOverlays` are `Vixen.Engine`'s. So the overlay
interface is unreachable from the one host whose whole job is drawing a `UiDocument` — which is
precisely the case a UI-debug panel is *for*.

`Editor/Vixen.Editor.App` is the single assembly in the tree that references both sides. It registers
no overlays today.

So the seam question has now been answered wrongly three times — first as "`Vixen.Ui` may not
reference `Vixen.Engine`" ([#461](https://github.com/Rikarin/Vixen/issues/461) refuted that), then as
"`Vixen.Ui` publishes nothing to report" (the table above refutes that), and now as "an overlay in
`Vixen.Engine.Renderer` registered by `AppGraphics`". ⚠ **The pattern is the tell: every answer has
assumed the panel must be an `IDiagnosticOverlay`, and that is the assumption to drop.**

**The defended shape: a `Vixen.Ui` control, not an overlay.** Doc 13's four rows — element bounds,
layout boxes, style origin for the hovered element, dirty-region highlight — are all *about* a
`UiDocument`, and every one of them is readable inside `Vixen.Ui` with no seam whatever. A control
reading the aggregator works in `UiApplication`, in the editor, and in any game that draws a document,
and it needs no new assembly, no reference either way across the `Ui`/`Engine` line, and no host to
grow a `DiagnosticOverlays` it does not have. `GpuOverlay` and `StreamingOverlay` are overlays because
their numbers come from a frame the UI knows nothing about; this panel's numbers come from the UI
itself, and that is the difference the interface was drawing all along.

⚠ **What that costs, said out loud, because it is the one real objection.** A panel drawn into the
document it describes is part of that document: it has elements, it is styled, it is laid out, and it
therefore moves `StylesApplied`, `NodeCount` and the settling counters it is reporting. `GpuOverlay`
has no such problem. Three ways out, in preference order — the panel reads a snapshot taken at the top
of the frame, before it is itself restyled, which makes the numbers a frame old and self-consistent
(the same trade `FrameStatsOverlay` already documents); or it subtracts its own subtree, which is
exact and fragile; or it lives in its own `UiDocument` on its own surface, which is honest and costs a
second document. The first is what `AppGraphics.BuildOverlays`' neighbour comment already argues for
on the frame stats, and it is why the aggregator wants to be a **snapshot** rather than a live view —
a decision the "reads, does not sample" constraint above does not settle on its own.

**So the owed items are re-ordered rather than re-scoped.** ⚠ Item 1, the aggregator, has since
landed — `UiDocument.Diagnostics` above — so what this section changes about it is not whether it
exists but what it must be: a snapshot taken at the top of the frame with an in-process reader,
rather than the live view "reads, does not sample" alone would have allowed. Item 2 is a control
rather than an `IDiagnosticOverlay`, and item 3 is composing it into a host's tree rather than
registering it in `BuildOverlays`. Neither of those two is built, and the reason for writing the seam
answer into the README rather than into a session is that the last two attempts each re-derived one
that was already there.

# Vixen.Editor.Ui

The editor shell from [docs/plan/11](../../docs/plan/11-editor.md) § "`Vixen.Editor.Ui` — the
shell": docking over the existing `DockingHost`, a command registry, menus/toolbars/palette as views
over it, theming, notifications, background tasks, and localisation.

```csharp
using var shell = new EditorShell(1600f, 1000f);

shell.RegisterPanel("scene", new StringId("editor.panel.scene", "Scene"), panel => panel.Add<Viewport>());
shell.RegisterLayout("Default", new StringId("editor.layout.default", "Default"),
    () => LayoutPresets.Standard(["hierarchy"], ["scene"], ["inspector"], ["console"]));

shell.Commands.Add(new EditorCommand("file.save", EditorStrings.CommandSave, project.Save) {
    Enablement = () => project.IsDirty
});
shell.Keys.SetDefault("file.save", new KeyChord(InputKey.S, ModifierKeys.Control));

shell.Workspace.Reset();
```

`file.save` is now in the File menu, in the command palette, on Ctrl+S, and greyed out when the
project is clean. Nothing was told about it twice.

## Everything is a command

Doc 11's claim is that "menus, toolbars, context menus and the command palette are all *views over
the command registry*, so a new action appears everywhere at once". `MenuPresenter`,
`ToolbarPresenter` and `CommandPaletteSource` are those views, and each holds command *ids* rather
than labels — so a command renamed, rebound, disabled or translated is right in all four places
without any of them being told.

**Enablement is a predicate, not a flag.** A flag has to be pushed at every view whenever the world
changes, which means a menu that is right only if somebody remembered to invalidate it. A menu asks
as it opens; a toolbar asks on the tick. The cost is that the predicate must be cheap —
`stack.CanUndo`, not a directory scan.

**A command carries no keybinding.** That is `KeyMap`'s, because a binding is the user's and a
command is the application's.

**A command may declare a *context*.** Delete in the outliner and Delete in the content browser are
two commands and one key. `EditorCommand.Context` names the place a verb belongs, `EditorShell.Context`
says which place has the focus, and `CommandRegistry.CanExecute` is what refuses the one belonging
somewhere else — so a keystroke aimed at the browser cannot delete an entity, and neither command has
to give up the key. A command with no context is in scope everywhere, which is almost all of them.

**A *mode* is what changes the meaning of a gesture, and it is not a command.** `IEditorMode` — id,
title, icon, context, an optional toolbar and panel, a register/unregister pair, an activation pair,
and first refusal on viewport input — is doc 20's A1, and `EditorModes` is the registry behind the
strip between the menu bar and the toolbar. One `Add` gives a mode a button, a radio entry in the
palette and a context in the keymap; a shell with no modes registered draws no bar at all.

⚠ **A mode's claim on a key is the context mechanism above and nothing new.** Doc 24's B2 is the case
that forced the seam: `1..9` recall a view bookmark and `1`/`2`/`3`/`4` are the element modes every
modelling tool binds, both are right, and the resolution is that the mode's commands declare its
context while the bookmarks declare none — so `KeyMap` files the two separately and each has the key
where it means something. See [the editor modes guide](../../docs/guide/editor/modes.md).

**A command may declare that it is not built yet.** `EditorCommand.Unavailable` carries the sentence
saying why and disables it wherever it appears. Doc 20's first bar is that "a verb that is not
implemented is *visibly* not implemented rather than absent": a menu line that is missing reads as an
editor that cannot do the thing, and one that is there and greyed reads as an editor that will.
Replacing one with a real implementation is deleting a property initialiser.

⚠ **The toolbar grows *sections*, not entries.** `ToolbarPresenter.Show` takes a list of
`ToolbarEntry` — a button, a rule, a `ToolbarGroup` drawn as one segmented control, or a
`ToolbarDropdown` that opens a small menu — because three adjacent buttons say nothing about being
one choice. The flat `Show(params string?[])` overload is still there and is the same thing with
every entry a button.

⚠ **A presenter that rebuilds puts itself back where it was.** Registering a command rebuilds the
menu bar, and rebuilding replaces the bar rather than editing it — for the good reason that its
menus hang off the document root and editing in place would leak one per rebuild. But adding a child
*appends* it, and the shell registers the application's commands long after the workspace and the
status bar are in the chrome. So both presenters remember the position they were constructed for and
move the new strip back into it; without that the menu bar and the toolbar arrive along the bottom
edge of the window, on whichever frame the application happened to register its last command.

## Keybindings

Three layers: the defaults the application ships, a **preset**, and the overrides the user made. Only
the last is saved, plus the preset's name. A keymap file holding every binding freezes the defaults at
the version the user first ran — every editor that shipped one has a support burden to prove it.

`Vixen`, `Unity` and `Unreal` ship (`KeyMapPresets`), and a team's own is one YAML file in the same
format. ⚠ **A preset is a layer and not an edit**, because choosing Unreal and then rebinding one key
has to leave the other two hundred following the preset — applied by copying, the next preset update
would reach nobody who had ever rebound anything. ⚠ **`Vixen`'s preset is empty and that is not a
stub**: the shipped defaults *are* the Vixen keymap, declared beside the commands where a default
belongs, so choosing it means "no layer".

⚠ **The composition takes a chord off whatever held it**, most-specific layer first and within a
layer in command-id order. That is what lets a preset be twenty lines: Unity puts Play on `Ctrl+P`,
which is this editor's palette, and the preset says where the palette goes rather than enumerating
everything it displaced.

`KeyBindingsView` is the panel over all of it — command, category, binding and which layer it came
from, with a filter, a preset picker, a "press a key" capture, inline conflict reporting, per-row and
global reset, and import/export raised as events for whoever has a file picker. ⚠ **Capture is a mode
rather than a modal**, so the harness can drive it; the consequence is that Escape is the one chord it
will not bind.

`KeyBindingsView.vxml` since doc 36 § F7 wave 1b, and two things about that port are worth keeping.

⚠ **`KeyMap` and `CommandRegistry` needed no signals.** The wave's brief was that every panel ported
needed its model made signal-backed; these two did not, and the reason is *where their values are
read*. Every chord and every source on screen is read by a `DataGrid` column projection —
`Func<object, object?>`, evaluated by `Grid.Refresh()` — and no markup attribute can bind a column.
Nothing the file binds reads the map at all: the strip and the status line are functions of the
panel's own state, which is the selection, the capture mode, the refused chord and the complaint
about a preset. Those are five signals, granular rather than one snapshot, because they are five
independent facts written from five different places.

⚠ **Two things stayed imperative on purpose.** `Record.State |= Checked` is a *flag set* that also
holds Hovered, Focused and Pressed, so a binding assigning it whole would undo whatever the pointer
had just put there — `Capture` is the one place the mode changes and is where the bit is set. And the
unknown-preset complaint used to be written straight into `Status.Text`, which a bound line paints
over on the next flush; it is a signal now, cleared by every path that used to run `Restate`.

Conflicts are **detected, not resolved**: a chord belongs to one command *per context*, and binding
an occupied chord fails and says who has it. Across contexts, sharing a chord is the point rather
than the hazard — `KeyMap.ContextOf` asks the registry which context a command belongs to, and a
binding made in a context shadows the global one while that context has the focus. Bindings
**survive the commands they name**, so unloading a plugin does not throw away the shortcut the user
gave it.

⚠ **A chord with no Control or Meta is not taken from a text field.** A single-key binding — `F` for
frame-selection, which every 3D editor has — would otherwise fire while somebody was naming an
object, and the object would end up called `Cubeaaa` with the camera somewhere else.

## Docking

`DockingHost` (in `Vixen.Ui.Controls.Advanced`) knows about panels by id, splits, tabs, dragging and
a serialisable arrangement. It knows nothing about where a panel's contents come from or what "the
Shading layout" is, which is right for a control in `Vixen.Ui`. `DockingWorkspace` is the other half:
a registry of what can be shown, lazy construction, named presets and one place that saves.

⚠ **A preset is a factory, not a layout.** One handed out as an object is the object the first
splitter drag edits — and "reset to Default" then puts the window back to whatever the user last
dragged it to.

⚠ **Restoring an arrangement writes the saved tab selection back after the panels are built.**
Opening a panel brings it to the front, which is what it means everywhere else, so a two-tab group
would otherwise always come back showing whichever panel happened to be built last.

**Panels tear out into real operating-system windows**, by dragging a tab off the window or through
**View ▸ Panels ▸ Float Panel** (`view.float-panel`). The window opens over exactly where the panel
was — a panel that visibly jumped somewhere else at the moment of undocking would leave the user
hunting for it to find out whether the command did what they meant. `FloatActive` acts on the panel
holding the focus, falling back to the front tab of the first group, because a menu item that acted
on the first panel in the arrangement regardless is one that surprises every time it is used from the
keyboard. The command greys itself out where the platform has one window — a browser tab, an Android
activity, iOS — rather than being absent, which is a runtime question with a runtime answer.

**A panel can be taken back out, and unregistering closes it.** `EditorShell.UnregisterPanel` drops
the descriptor *and* the command that shows it, because `RegisterPanel` made both — half of that
undone leaves a View-menu line that toggles nothing. It exists for `Vixen.Editor.Plugin`: a panel
built by a plugin's factory is a live reference into that plugin's assembly, so a workspace that
merely forgot the panel while it was still docked would keep the whole load context alive. The saved
layout still names it, which is what puts the panel back in its own place when the plugin returns —
the same bargain `KeyMap` makes with a plugin's shortcut. `MenuModel.Remove` and `MenuGroup.Remove`
are the same story for a menu, and remove **by identity**: `MenuCommand` is a record, so removing
"the line naming `file.save`" would take out whichever of them compared equal first.

## The palette, and search-everywhere

`Ctrl/Cmd+P`, fuzzy, over an ordered list of `IPaletteSource`. Commands are one source; assets,
scene objects and settings are others and are not this assembly's business.

`EditorShell.Search` is a **second** `CommandPalette` on `Ctrl+Shift+F`, grouped by source and with a
preview pane, and it starts with no sources at all — the shell knows what a command is and nothing
else. ⚠ **A second overlay rather than a mode on the first**, because the two want opposite answers to
three questions: whether to group by source or by the command's own category, whether an empty query
should list anything, and what Return means. One runs a verb; the other reveals a thing.

The matching is subsequence-with-bonuses — a run beats scattered letters, a word start beats the
middle, a shorter candidate beats a longer one that matched equally well. A source does its **own**
matching, because an asset index with a hundred thousand entries wants something better than a
linear scan and a palette that dictated the search would prevent it.

⚠ **The field keeps the focus and the rows never take it.** A palette where Down moved the focus
into the list is one where the next letter typed goes nowhere.

## Theming

Nine custom properties on the root and one class name. `ThemeService.Mode` writes `dark`; a user
theme file is a YAML mapping of token to value compiled into an *author* stylesheet, which beats the
three user-agent sheets by origin — so overriding `--accent` needs no `!important` and no knowledge
of the control set's selectors.

The sheet order is `ControlTheme` → `AdvancedTheme` → `EditorTheme`, and it matters: each is written
against tokens the one before declares, and a custom property nothing declared substitutes to
nothing.

`EditorTheme` also **overrides** the control set's tokens rather than only adding to them, which is
the mechanism `ControlTheme`'s own remarks nominate for exactly this: an application shipping a
button gets the neutral palette, and a tool window is a different room. Neither of the other two
sheets is edited, so a game that never installs this one is untouched — and the control screenshots
still show what a game gets.

### What it is trying to look like

The neutral density of a 3D tool and the material layering of an audio workstation. The unit is the
**pane**: a working grey with a seam around it, not a card on a desk.

- **Mid greys, not near-black.** A tool window that bottoms out at black has nowhere left to put a
  recess — every field, gutter and gap ends up the same colour and the ramp collapses into one flat
  sheet. The working surface sits in the middle of the range so there is room below it for the wells
  and above it for the things you press.
- ⚠ **The hairline is darker than the surface it edges.** A lighter border is a bevel and belongs on
  something raised; a darker one is a seam, and a tool window is made of seams. The gap between two
  panes is the same colour as the line around one, so a split and an edge are the same fact drawn at
  two widths and cannot disagree.
- **Small radii.** A pane is a region of the window with work in it, not a card that arrived from
  somewhere. `--radius-panel` is 5px and `--radius-control` is 4px — rounding a panel like a dialog
  throws away four pixels of every corner and makes a dense tool read as a settings screen.
- **Four surfaces.** `--workspace` (the seam) → `--surface-sunken` → `--surface` →
  `--surface-raised`. Depth is a luminance step. Raised where you press, sunken where you type.
- ⚠ **The well is a fill, not an inner shadow.** `DrawListBuilder` refuses `inset` box-shadows
  outright and says why, so a recessed field is two steps down the ramp and nothing else. That is
  the reason the ramp has four entries rather than three.
- **`--elevation` is a token**, so what floats and how far is one edit rather than forty. Everything
  that leaves the plane — menu, popover, dialog, palette, toast, floating panel — shares it. A docked
  pane has no shadow at all; it is not on top of anything.
- **`--accent-deep` for resting selection, `--accent` for what you just did.** A list is mostly
  selection, and the accent at full strength is the brightest thing in a dark editor, so the
  outliner sits a step below the palette's highlight.
- **Focus is answered twice.** `dock-group:focus-within` tints the focused panel's hairline, and its
  selected tab's label turns accent — one says *a* panel has the keyboard, the other says which.

`EditorChromeVisualTests` holds all of it to a picture, in both themes and with the palette open.
The tests beside it ask which panel is open and which command ran, which stays true through any
palette at all — the inspector bugs that started this work were invisible to every one of them and
obvious in the first screenshot.

### Utility classes

`EditorStyles` is the other half of the sheet, and there is no code behind it.
`Theming/vixen.ui.vcss` is the design tokens — an `@theme` block over the palette the engine ships;
every source file and every `.vxml` in this assembly is
scanned for class names at **build time** by `Vixen.Ui.Styling.Utilities.targets`; the sheet — inside
`@layer utilities`, with only the rules something refers to — is compiled into `obj/` before the
compiler runs and carried in the binary as a constant. `EditorTheme.Install` loads it immediately
after the hand-written sheet, so one call still installs everything.

That is a build step and not a startup scan, and the difference is not only start-up time.
`Samples/14-Mmo/Mmo.Ui/Theme/MmoStyles.cs` is what this looked like before it — a hundred and thirty
lines that embed the markup as resources, walk the manifest and run the scanner — and it could only
ever see *markup*. Most of this assembly's chrome is built in C# with `AddClass("…")`, so every
utility a code-built panel asked for was silently missing. The step scans `@(Compile)` too.

**The `@theme` block declares no colours of its own.** Every one is a `var(--…)` reference to a token
`EditorTheme` already puts on the root — so `bg-surface` and `background: var(--surface)` are the
same declaration, and the light/dark toggle and a user's theme file move both. A token file full of
hex would have been a second palette that agreed with the first until the day one of them was edited.

⚠ **And it clears the engine's colour and breakpoint namespaces before declaring its own.** Vixen
ships Tailwind v4's default `@theme` — twenty-six ramps in `oklch()`, a type scale, radii,
breakpoints — so a *game* writing `bg-blue-500` needs no theme file. The editor wants neither: its
palette is designed around four surfaces and a hairline darker than the surface it edges, and a tool
window is sized by the dock that holds it rather than by the display, so `md:` asks the wrong
question. `--color-*: initial;` and `--breakpoint-*: initial;` are v4's own way of saying so. The
radius scale is kept, because it collides with nothing and a new panel wanting `rounded-md` should
have it.

⚠ **`EditorTheme.vcss` is a file now, and it was fourteen hundred lines of CSS in a
`const string`.** There was no `.vcss` item type in the tree at all until doc 43's `@theme` work; the
glob in `Vixen.Ui.targets` embeds it and `EditorTheme.Css` reads it back.

⚠ **And it is no longer the only one — every hand-authored sheet in the tree is a file.**
`ControlTheme`, `AdvancedTheme`, `AssetEditorTheme`, `InspectorTheme`, `BrowserTheme`,
`ProfilerTheme`, `DebuggerTheme`, `NodeGraphTheme` and `WorldTheme` followed, byte for byte: the CSS
each one now loads is the old constant's text unchanged, with an SPDX header comment ahead of it and
a trailing newline. Nothing about the cascade moved, which is the point — the extraction was
verified by reading every sheet through its own `Css` accessor before and after and comparing the
bytes. The only sheets still carried as constants are the **generated** ones (`EditorStyles.Utilities`
and its per-assembly siblings), which have no file to edit because a build step writes them.

⚠ **The utility layer wins every argument it has with the sheet above, and it used to lose every one
of them.** The cascade reads origin, then layer, then specificity, then order — and an unlayered rule
beats a layered one whatever the last two say. `EditorTheme` was unlayered, so `task-row { padding:
6px }` beat `p-3` with neither saying `!important`, and a utility only took effect on a property no
hand-written rule set for that element. The sheet is `@layer components` now, the ladder is `base,
components, utilities`, and retro-fitting a class onto styled chrome no longer starts by deleting a
rule. A rule that genuinely must not be overridden says `!important`, which in a layered cascade
reverses the layer order and is therefore a precise instrument rather than a blunt one.

⚠ **The generated sheet has to load at the same origin as this one, and that is what the ladder rests
on.** A layer is the cascade's *second* question; the origin is its first, so `components` →
`utilities` orders nothing across an origin boundary — a design that reached for origins to express
the tiers would look right and do nothing. `StylesheetTests` keeps the mismatched arrangement in the
suite as a measured fact rather than a claim in a comment, alongside the sabotage twin with the
chrome sheet's `@layer` wrapper replaced by `@media all`, which is what stops the main assertion
passing on source order — the way the utilities README's layer test was once found to have been doing
all along.

⚠ **Not every family the generator emits is one the engine reads.** `overflow-x-*` and `overflow-y-*`
are the pair to know about: the unprefixed `overflow` is read and the per-axis forms are interned by
nothing, so they compute cleanly and do nothing. `UtilityFamilySupportTests` is the list, resolved
against real elements, and [the guide page](../../docs/guide/editor/utility-styles.md) has it as a
table.

⚠ **A class name assembled at run time is still invisible to the scan**, because it is never written
down whole. There are four such sites here — `ThemeService`'s `dark`, `ConsoleView`'s and
`MessageLogView`'s `level-*`, and whatever a plugin puts in `EditorCommand.ClassName` — and none of
them names a utility, so `@(VixenStyleSafelist)` is empty. A future one that does has to go in it.

**The step is given no `--base` files, and now never needs to be.** `EditorStyles.Utilities` is the
whole of what it produces, and `EditorTheme.vcss` reaches the runtime as its own `EmbeddedResource`,
installed in its own place in the cascade. The reason to fold it into the generated sheet was to get
`@apply` expanded, and `@apply` is expanded at install time now — `UiDocument` runs `ApplyExpander`
over every sheet it loads, against the merged theme rather than one sheet's. So the sheets keep the
load order the chrome is written against, which is the cascade change nobody wanted to make.

⚠ **The order that made the ordering question real is this file's.** `EditorShell` installs
`ControlTheme`, then `AdvancedTheme`, then `EditorTheme` — and the editor's tokens are in the last of
the three. An `@apply` written into `ControlTheme` and expanded when it arrived would be measured
against the shipped palette, not the editor's, and would render plausibly. See
`Core/Vixen.Ui/StyleApply.cs` for what stops that.

⚠ **The shell now takes a logger, and `Vixen.Editor.App` passes one.** `UiDocument` reports every
rule the cascade dropped — a mistyped at-rule, an unsupported selector, an `@apply` naming no
utility — and a document handed no logger reports them into `NullLogger`. The editor's goes to
`EditorLog.Sink`, which is the ring the console below reads, under the category `Vixen.Ui.Styling`
rather than `Vixen.Editor` so the two are separable in the filter.

## The console

A virtualised list over `Vixen.Core.Diagnostics`' `RingBufferSink`, which is the log the crash
reporter dumps rather than a second one the editor keeps for itself. `ConsoleModel` is the half
worth testing — which records are visible, what a level toggle means, what collapse collapses, what
the badges count — and `ConsoleView` is the rows and the buttons over it.

⚠ **It must not allocate per line**, and doc 20 says why: "a game logging per frame into a panel
that keeps strings is a leak with a UI". Three things make that true. `RingBufferSink.CopySince`
hands over only what has arrived since the reader's sequence number, so the console never
snapshots a hundred thousand records to find the four that are new. The model keeps *indices* into
a bounded buffer of the records the sink already allocated. And `VirtualizingPanel` keeps about
thirty row elements whatever the list holds — a hundred thousand lines is thirty elements, and
there is a test that says so.

⚠ **The badges count what arrived, not what is shown.** A warning badge reading zero because
warnings are hidden answers the opposite of the question somebody clicks it to ask.

⚠ **Following the tail is a mode the user leaves by scrolling**, not a checkbox — and it is applied
from `LayoutFinished` rather than when rows are added, because at the moment a row arrives the
scroller's extent has just been invalidated and a scroll aimed at the bottom lands at the top.

## Notifications and background work

A toast **and** a history, because the thing a user does after an import fails is look away, look
back, and find the message gone. ⚠ **An error does not expire**; everything else does.

Background tasks are never a modal dialog. Work runs wherever the caller put it, what it reports is
queued, and `Pump()` applies the queue at one point in the frame — so a status bar never draws a
title that was replaced between two reads. The pump has a budget, which is a livelock guard rather
than a nicety: a task reporting per file over a hundred thousand files can enqueue faster than a
frame can drain.

## The task centre is written in VXML

`Tasks/TaskCenter.vxml` is the first interface in the repository written in the markup language the
UI framework exists for, and it is here as a proof of concept: one panel, small enough to read
whole, with a list, a control, an event and a piece of state — the four things every other panel is
also made of.

```html
@for (var task in Running) {
    <task-row key="@task">
        <task-line>
            <task-title>@task.Title</task-title>
            <IconButton LeadingIcon.Geometry="@ControlIcons.Close"
                        Variant="Subtle"
                        Disabled="@Live(task.IsCancellationRequested)"
                        on:click.stop="@(() => Cancel(task))" />
        </task-line>

        <ProgressBar Value="@Live(task.Progress)" IsIndeterminate="@Live(task.IsIndeterminate)" />
    </task-row>
}
```

What the C# version was is a `Control` with a pool of rows, rebound from `EditorShell.Tick` sixty
times a second whether or not anything had changed, and a click handler that walked up from the
event's source to find out which row had been pressed. None of those three things survives: the
loop's key is the task, so a row that is still running keeps its elements; the handler is the
row's, so there is nothing to walk; and the bindings re-run when the manager says something changed
rather than on a timer.

**Four things it needed that the framework did not have.** They are written up where they live —
[`Vixen.Ui`](../../Core/Vixen.Ui/README.md#composition) and
[`Vixen.Ui.Markup`](../../Core/Vixen.Ui.Markup/README.md) — and named here because "rewrite one
component first" is what found them:

| | |
|---|---|
| A capitalised tag could not name a control | `ctx.Child<T>` took `Component` only, so `<ProgressBar />` did not compile. Nothing in the control library was reachable from markup. |
| `on:click` was a tap | Which is not what a button is: it is also Space, Enter and an access key. `BuildContext.Subscribe` is how `Vixen.Ui.Controls` says so. |
| A component could not name its host tag | The default is the type's name in lower case, which cannot produce the hyphen every tag in these stylesheets has. `@tag task-center` is the header that says so. |
| A quoted value was always a string | So an enum had to be written `Variant="@ControlVariant.Subtle"` — a qualified expression, and an effect registered to assign a constant. `Variant="Subtle"` is the same thing said once. |
| Effects were queued per thread | And a shell that flushed the thread's queue ran every other document's bindings. `UiDocument.Effects` is the fix; `Tick` drains this document's. |

**And the model holds signals, which is what the panel is actually over.** `BackgroundTask`'s
properties are signal-backed and `BackgroundTaskManager.Tasks` is a `CollectionSignal`, so the list
and every number in it follow the model with nothing in between. The panel keeps one signal, for
which *manager* it is pointed at, and that is the only thing here no signal covers.

⚠ **Signal-backed, not signal-typed, and that is the whole migration.** `Progress` is still
`float Progress { get; }` — the field behind it is a `Signal<float>` — so not one caller of a task
changed. What changed is that reading it inside a binding subscribes. The first version of this
panel had a revision signal that every row binding read through a `Live(…)` helper, which is the
adaptation a view needs when its model raises events instead; that helper and the
`manager.Changed` handler behind it are both gone, and so is the subscription that would have
outlived the panel.

⚠ **`IsCancellationRequested` is the exception and says why.** It used to read the
`CancellationToken`, which the *work* polls from whatever thread it is on — and a signal read
asserts the owning thread. So the token stays a token and a mirror signal, written by `Cancel` on
the UI thread, is what the button reads.

**A text node is an element.** `<task-title>@task.Title</task-title>` puts a `text` element inside
`task-title` where the C# version set `.Text` on `task-title` itself. Nothing in the stylesheet
cared, because none of its selectors reach past the part — but a rule written as `task-title` and
meaning "the thing with the words in it" would.

## `Parts/` — the pieces more than one panel is made of

`Parts/FactRow.vxml` is the first, and the folder is here rather than beside any one panel because
nothing in it is this assembly's own chrome: a fact row is doc 20's `(derived)` convention — a row, a
name, a value — and four assemblies had a copy of the four lines that build one.

```csharp
var row = facts.Add<FactRow>();

row.Name = "Terrains to carve";
row.Value = CarvableTerrains.ToString();
```

⚠ **A `UiElement` rather than a `Component`, so it is reachable from both spellings.** The panels
that show facts are still C#; a part only a `.vxml` could name would have waited for them.
`@inherits` costs nothing and the same type is `<FactRow Name="…" Value="…" />` in a `@for` the day
one of them is ported — which is the point, because a part is also the *only* way markup can set an
intrinsic child's own `Text` inside a loop. See the ledger's shape 5.

## The panel ledger — what is markup, what is next, and what never will be

Doc 36 § F7's number was "three `.vxml` files against ~120,000 lines of hand-written editor C#", and
the honest version of that ratio has never been written down. This is it: **every panel in the
editor, surveyed once, so that a wave picks its work instead of discovering it.**

**Where it stands.** Eighteen `.vxml` files across six assemblies — seventeen panels and one shared
part, `Parts/FactRow.vxml` — against **62 files and ~31,700
lines** of editor C# that construct UI. Sixty-two is not sixty-two panels — a third of those files
turn out not to be panels at all, which is the first finding. There are 25 `RegisterPanel` call
sites and 34 `editor.panel.*` ids, so **34 is the denominator**, not 62 and not 120,000 lines.

⚠ **[`docs/overview.md`](../../docs/overview.md) and doc 36 § "F7's number" had both gone stale** —
they said eleven and three — and are corrected in the same commit as this section. A count nobody
can re-derive goes stale again: it is `find Editor -name '*.vxml' -not -path '*/bin/*'` — the
`-not` matters, because `Vixen.Editor.Ui.Tests` copies this assembly's markup into its output as
fixtures and a bare `find` counts each of them twice.

### The four shapes that decide a port — and a fifth, found by trying

Wave 1b found one reason a panel should be left alone. There are four, and only the first was known.
Wave 3 added the fifth, which is below the other four because it was found by building against
them rather than by reading.

**1. The content reaches the screen through a control, not the tree.** `PluginHost`, `KeyMap` and
`CommandRegistry` were left alone because every value they show goes through a `DataGrid` column
projection — a `Func<object, object?>` run by `Grid.Refresh()` — which no markup attribute can bind.
⚠ **That shape is not unique to `DataGrid`, and naming its four other spellings is the most useful
thing in this section**: `TreeView` (rows are `TreeNode` *data*, painted by `Refresh()`),
`VirtualizingPanel`/`VirtualizingGrid` (`CreateRow`/`BindRow`, a pool indexed by *viewport position*,
so a keyed `@for` cannot stand in — item 40 000 has no element), `Timeline` and `NodeCanvas`
(`AddTrack`/`AddSpan`, `Canvas.Graph = …`, plus their own culled element pools), and
`IPropertyDrawer` (the tree's *shape* is a function of a reflected descriptor). A panel built out of
any of them is correctly imperative, and signal-backing its model moves no pixel.

**2. Markup can bind a gesture but not a value change.** `BuildContext.Subscriptions` holds ten
names — `tap`, `click`, `dblclick`, `longpress`, the three pointer verbs, and the three drag verbs —
and its entries are `Action<UiElement, Action<UiEvent>, RoutingStrategy>`: a *routed* handler. Every
value-change event in the control library is `Action<TControl, TValue>` — `Slider.ValueChanged`,
`TextField.ValueChanged`, `NumericInput.NumberChanged`, `ToggleBase.CheckedChanged`,
`Select.SelectionChanged` — so no entry in that table could carry one, and `on:change` is not a name
somebody forgot but a shape the table cannot hold. The workaround is `ref` plus a subscription in
`OnComposed`, which `PluginManagerView` and `KeyBindingsView` both use and both explain.

⚠ **The workaround fails inside `@for`, and that is what makes it a blocker rather than a wart.**
`ref` in a loop body is `VXML2010`: the body runs once per item and there is one member to assign.
So a panel whose *rows* contain value-editing controls cannot be expressed at all — not awkwardly,
not through `OnComposed`. `AudioMixerView` is the pure case (every strip is a fader, a mute and a
solo, and the handlers read each other), and it is the one panel in the whole editor that is
unportable for a reason the engine could fix.

**3. A bound value is right on the next frame, not this one.** `EffectScheduler`'s contract is that
writing a signal *only ever queues*, and `Flush` drains it once per frame, after input and before
layout, because an effect running at the write would mutate the tree while the renderer walked it
(ADR-007). ⚠ **This one is invisible from the panel's own source — only from its callers.**
`BuildSettingsView` reads `BuildButton.Disabled` on the line after `Rebuild()`, with no frame
between, and that is the assertion, not an accident: the panel has to be right the instant it is
asked. **Before signal-backing anything, grep for callers that read it back synchronously.**

**4. It is not a panel.** Roughly a third of the "UI" files are presenters that build into a
caller's element (`ProjectBrowser`, `ViewportLayout`, `SceneHierarchyView`, `ToolbarPresenter`,
`MenuPresenter`), services with no fixed tree (`DialogService`, `AssetPicker`), registration wiring
(`EditorSettingsPanels`), or scanners with no UI at all (`DeclaredContributions`,
`EditorDiagnostics`, `FoliageMode`, `BlockoutUvPanel`). ⚠ `MenuPresenter` and `ToolbarPresenter` are
worth their own line: their menus and popovers hang off the **document root**, so the bar is not an
ancestor of its own items. There is no tree for markup to describe.

**5. Markup cannot write an element's own `Text`.** An interpolation is `BuildContext.Text`, which
creates a `text` *child*; and an attribute on an intrinsic tag is not a property assignment —
`BuildContext.Attribute` special-cases `class` and `style` and sends everything else to
`StyleTree.SetAttribute`, which is a selector attribute nothing reads back. So
`row.Add("fact-name").Text = label` has **no markup spelling at all**: `<fact-name>@Name</fact-name>`
adds a box and `<fact-name Text="@Name" />` silently does nothing. This is the mechanism behind the
"a wrapped paragraph rounds differently at four, five and six lines" note — the difference is not
rounding, it is an extra element.

⚠ **The escape is a capitalised tag, and that is why the shared parts had to come first.** A
*component* tag does get real property assignment (`ComponentEmitter.EmitParameter` writes
`ctx.Bind(() => n1.Prop = …)`), and a component's `ref`s are its own. So a row wrapped in a
component can set an intrinsic child's `Text` with a `ref` **and still be used inside a caller's
`@for`**, where a bare `ref` is `VXML2010`. That is item 2 of the build list below, already
available in the one shape that matters most: put the row in a part.

### Two recorded gaps are stale, and were verified closed

⚠ **`class=` on a control tag no longer clobbers.** `BuildContext.Attribute`'s remark reads as if it
still does; it is describing the bug in the past tense. `SetClasses` takes back only the names the
attribute itself last wrote, so `<Button class="row" Variant="Subtle" />` keeps `variant-default`
and `size-md`. The `OnComposed` workarounds in `ImportSettingsView` and `UndoHistory` are no longer
needed.

⚠ **Inline `display` was never refused.** There is no property allowlist in `SetInlineStyle` and no
`display` diagnostic anywhere in `Vixen.Ui.Markup`; the only refused character is `}`. The guide's
"a `display` toggle is a class" is *advice about taste*, and taste is right — but a port blocked on
it was blocked on nothing.

Both had been carried forward as blockers in wave notes. **Verify a gap before you design around it.**

### The shared parts: `FactRow` exists, `Section` is blocked by a bug, `VerbRow` is not worth one

`Parts/FactRow.vxml` is the first of the three this section asked for, and it lands with its callers
rather than ahead of them: `EditorWorlds`, `TerrainModule`, `WaterModule` and the terrain layer list
all build their rows out of it now, and `FactRowTests` holds it to the four lines it replaced by
dumping the whole document — tag, classes and every rectangle — for the hand-written form and for the
part, in the same position, and comparing the two strings.

⚠ **`Fact` was hand-written *six* times, not seven, and two of the six were a different row.**
`EditorWorlds`, `TerrainModule`, `WaterModule` and `FontView` put the value in a `text` child;
`TextureImportView` and `CompiledSceneView` set `fact-value`'s own `Text`. The part reproduces the
four, because those are the four it replaces — and by shape 5 above, reconciling the other two is a
layout change and not a tidy-up.

⚠ **Three of the six are out of reach, and the reason is the reference graph rather than the code.**
`FontView`, `TextureImportView` and `CompiledSceneView` are in `Vixen.Editor.AssetEditors`, which
does not reference `Vixen.Editor.Ui` and should not start to for a row. The only assembly all six can
see is `Vixen.Editor.Inspector`; whether a shared *panel* part belongs in the property-drawer
assembly is a question for whoever wants the other three, and the honest answer today is that
`Vixen.Editor.Ui` covers terrain, water, blockout and the app's own world panels, which is where the
six S-sized ports are.

⚠ **The rules stay in `AssetEditorTheme.vcss`.** `fact-row`, `fact-name` and `fact-value` are
declared there, and `frame-editor fact-name` overrides two of them in the same sheet; moving the
declarations into `EditorTheme` would move them earlier in the load order and change which one wins.
The tag names are the contract, and `EditorApplication` installs both sheets into the one document.

⚠ **`Section` cannot be shared until a two-year-old typo is decided, and the typo is that four
titles have never been styled.** `WorldTheme.vcss` says `world-title`; `EditorWorlds` writes
`panel.Add("world-title")` and `TerrainModulePanels`, `WaterModulePanels`, `BlockoutModulePanels` and
`StandardFrameView` all write `panel.Add("World-title")` with a capital. `NameTable` interns
ordinally and says why — VXML's own rule is that case distinguishes a component from an element — so
the rule reaches one of the five call sites and never reached the other four. A shared `<Section>`
has to pick a spelling, and either choice moves pixels in some panel: the right fix is to correct the
four and accept `font-weight: 600; margin-top: 6px` appearing where it was always meant to, which is
a change somebody should look at rather than one a refactor smuggles in.

**`VerbRow` was not built, and the reason is that it buys nothing.** `verb-row` has no rule in any
stylesheet in the tree; there are two copies of the helper (`TerrainModulePanels`,
`WaterModulePanels`) and one inline use (`StandardFrameView`), each about eight lines; and a part
whose `@for` followed a verb list would need that list to be a signal, so a caller assigning it after
construction is a frame behind where three lines of C# are not. Worth revisiting when a panel that
has verbs is actually being ported.

⚠ **And the six S-sized ports need one more thing this section did not know.** Blockout settings, the
two water panels and the three terrain ones build *directly into a `DockPanel`*, and
`dock-panel.scrolls > * { flex-shrink: 0 }` reaches direct children only — so wrapping a whole panel
in one markup component inserts a box that stops that rule reaching the content, and a tall panel
compresses instead of scrolling. The way through is the one `FactRow` takes: port the panel's
**parts**, each component's host tag being an element the panel already creates, so the tree is
unchanged and the C# factory shrinks a piece at a time.

### The ledger

Sizes are the wave-1b unit: four panels, ~1,130 lines of C# removed, ~1,370 of `.vxml` added, one
model signal-backed. ⚠ **"Signal-backed? no" is the answer for every row**, which is itself the
finding — outside `Vixen.Editor.Core` (`EditorDocument`, `Selection`, `EditorProperty`,
`EditorProject`) and the four models wave 1b touched, nothing in the editor holds a signal. So no
port in this table starts from a reactive model, and each one has to decide between a snapshot
record, an additive signal-backing, and shapes 1–3 above saying leave it alone.

| Panel | Model | Signal-backed? | Verdict | Size |
|---|---|---|---|---|
| `BuildSettingsView` | snapshot | no, and cannot be — shape 3 | **done, wave 2** | M |
| `FlameChartView` | snapshot | no | **no, and the nomination was backwards** — see below | S/M |
| `CompiledSceneView` | snapshot | no | **port.** Purest snapshot in the tree; 6 tests, all on the view | M |
| `VariationHarnessView` | snapshot | no | **port.** Read-only text and classes, *zero* value-change subscriptions | S |
| `SettingsView` | live (view-local) | no | **port — the exclusion is lifted.** See below | M |
| `TextureImportView` | snapshot | no | **port.** The direct `<Tabs>`/`<TabItem>` + `<ImportSettingsView>` case those two features were built for | M |
| Blockout settings · Water zone · Water body · Terrain grass/growth/splines | snapshot | no | **port, six small ones.** `TerrainBrushInspector.vxml` is the model; splines has a literal duplicated fact block a `@for` deletes | S each |
| `QueryView` · `GoapDomainView` · `AgentDebuggerView` | snapshot | no | port; leave `CurveEditor`/`NodeCanvas` behind a `ref` | S/M |
| `CodeEditorView` · `VfxGraphView` · `ShaderGraphView` · `CompositorView` | live | no | port the chrome; the editor/canvas/`KeyValueList` rows stay | S–M |
| `NodeInspector` · `NodeSearchPopup` · `CommandPalette` · `AddComponentMenu` | snapshot | no | port; each deletes a hand-rolled element pool or reconciler | M |
| `RemoteInspectorView` | **live** | no | port; signal-back `RemoteInspectorClient` additively, per `DeviceManager` | M |
| `Terrain` main · `Terrain foliage` · `MaterialView` · `FontView` · `StandardFrameView` · `ShapeVocabularyView` · `UtilitySetView` | mixed | no | port the readouts, keep the field rows (shape 2) | M |
| `ComponentsView` | snapshot | no | chrome only — the foldout bodies are `IPropertyDrawer` output | M |
| `MoveSetView` · `ProxyShapeView` · `SequenceView` · `BehaviorTreeView` · `SpriteSheetView` · `AnimationGraphView` | mixed | no | **defer.** Each is half unportable; `AnimationGraphView` has no tests at all | L–XL |
| `AudioMixerView` | snapshot | no | **no** — per-row value controls, shape 2's blocking form | XL |
| `AnimationClipView` | snapshot | no | **no** — `Timeline.AddTrack`/`AddSpan` + `CurveEditor` is the whole panel | L |
| `NodeGraphView` | live | no | **no** — `Canvas.Graph = built` and four `OnDraw` layers; nodes, ports and wires are not elements | XL |
| `ConsoleView` · `MessageLogView` · `AssetGrid` | live | no | **no** — `VirtualizingPanel`/`Grid` row templates | — |
| `InspectorView` + the four drawers · `TargetOverrideMatrix` | — | — | **no** — a drawer *is* a factory, and markup cannot be one | — |
| `ProjectBrowser` · `SceneHierarchyView` · `ViewportLayout` · `ToolbarPresenter` · `MenuPresenter` · `DialogService` · `AssetPicker` · `ViewportChrome` · `EditorSettingsPanels` · `EditorDiagnostics` · `DeclaredContributions` | — | — | **not panels** — shape 4 | — |

⚠ **`FlameChartView` was nominated for the wrong reason and is a "no" for three.** The nomination
read "it still pools, and a keyed `@for` is that pool", which is what made the GPU timeline's port
free — and the asymmetry between the two bars is the whole answer. `gpu-bar` has no interactive
state in the sheet; `flame-bar` has `:hover` **and** `:checked`.

1. **The pool carries the hover, and a keyed `@for` cannot.** `UiDocument.Track` sets `:hover` as a
   *difference between two element chains* (`Hover.cs`) — an element that is replaced loses the state
   until the pointer moves again, and clicking a bar re-zooms, which changes every bar's geometry and
   therefore every key. The C# pool keeps the same object in the same slot; whether that is *right*
   is a separate question — after a zoom the surviving hover is on whatever bar now occupies the slot
   — but the two behaviours differ, and a port that changes pixels is a defect until argued for.
2. **The caption is `bar.Text` on a bare `UiElement`, which by shape 5 markup cannot write** —
   inside a `@for`, where the `ref` escape is `VXML2010`. Every bar would gain a `text` child inside a
   `padding: 0 4px; overflow: hidden; align-items: center` box.
3. **The selection is `bar.State |= Checked`**, a flag set shared with Hover and Active — exactly
   what `KeyBindingsView` kept imperative and for exactly the same reason — and there is no per-row
   handle to keep it imperative with.

So it belongs beside `AudioMixerView`: blocked on **item 2** of the build list rather than item 1,
and unblocked the day a `@for` body can hold a handle of its own. Until then `GpuTimelineView.vxml`'s
remark that the `parked` rule "still exists for `FlameChartView`, which still pools" stays true.

⚠ **`ViewportChrome` is a "no" for a positive reason**, not an absence: it throttles its stats to
every fifteenth frame on purpose, to keep the window's draw list re-usable, and a binding would
remove exactly that.

⚠ **`BlockoutUvPanel` is a "no" that is really a "not yet written"** — 317 lines of headless model
with an immutable `Views` snapshot and no view at all. Doc 42 § D13 asks for one. Written fresh it
is `GpuTimelineView` almost line for line, and it is the best *new*-panel candidate in the tree.

### The two earlier exclusions, re-checked

**`MessageLogView` — still excluded, but the reason is narrower than recorded.** There is no tag
registry to add `VirtualizingPanel` to: the emitter writes `ctx.Child<Tag>(…)` for any capitalised
tag and lets C# overload resolution settle it, so `<VirtualizingPanel ref="@List" />` is already
legal. What markup cannot express is the **row template and its per-index binder** — `CreateRow`,
`BindRow`, and a pool indexed by scroll position. `ConsoleView` is the same panel with five columns
instead of four, and is the least suitable file in the editor.

**`SettingsView` — no longer excluded.** `SettingsCategory.Build` is still an `Action<UiElement>`,
invoked at one site (`Reload()`), from seven callers in `EditorSettingsPanels`. But the factory never
had to be *invoked from* the `.vxml` — it needs a host element to be invoked *into*, and `ref` gives
one. `PrefabView.vxml` is the proof: `<TabItem ref="@HierarchyTab" Label="Hierarchy" />` has no
content and `Show` builds the tree against `HierarchyTab.Panel`. `<settings-pane ref="@Pane" />` is
the same pattern and simpler — an element owned by no region, so `Reload()`'s clear-and-refill is safe.

### What to build, in order of leverage

1. **A value-change subscription markup can name.** It is what blocks every field list in the
   editor, and unlike shape 1 it is a nameable feature rather than an inherent limit. It needs a
   routed value-change event, or a second table whose entries are not `Action<UiEvent>`.
2. **A per-iteration handle** — either `ref` inside `@for`, or a `@for` body that is a nested
   component so its `ref`s are its own. With (1) this turns one XL "never" and four L "halves" into
   ordinary Ms.
3. **A row template for `VirtualizingPanel`.** Frees `ConsoleView`, `MessageLogView` and `AssetGrid`,
   which are three of the most-looked-at surfaces in the editor.
4. **A `Select` whose options come from markup.** `AddOption` is a method, and combined with
   `VXML2010` an enum dropdown inside a `@for` is inexpressible.
5. ~~**Shared `<Section>`, `<FactRow>` and `<VerbRow>` components.**~~ **`FactRow` is built** and has
   four callers; `Section` is blocked on the `World-title` casing bug and `VerbRow` earns nothing
   yet. See "The shared parts" above. What the exercise proved is worth more than the row: a part is
   the *only* way markup can write an intrinsic element's own text inside a loop, which promotes
   item 2 from a convenience to the thing everything else is waiting on.

## Localisation

Every user-visible string is a `StringId` — an id and the English text it says — so
`item.Label = EditorStrings.Save.Text` is no more work than the literal and there is never a reason
to write the literal. That is the retrofit `Stride.Core.Translation` exists to repair.

⚠ **The source text lives at the declaration, not in an `en` catalog.** An editor whose fallback is
a file shows `editor.command.file.save` to anybody whose install is missing it. Here the worst case
is English. `Strings.Missing` is the list a translator works from.

`Strings` is the one static thing in the assembly, and the price is that anything subscribed to
`Strings.Changed` must unsubscribe — `MenuPresenter` is `IDisposable` for exactly this, and
`EditorShell.Dispose` calls it.

## What is deliberately not here

- **`Vixen.Editor.Core`.** A command is an id and a delegate; a panel is an id and a factory. Nothing
  here knows what a project, a document or an undo stack is, which is what lets the whole assembly be
  tested against a bare `UiDocument` — the same bargain `Vixen.Ui` makes with `Vixen.Platform`.
  Joining the two is `Vixen.Editor.App`'s job.
- **A window.** A floating dock group becomes a real one through `IUiWindowHost`, which this assembly
  declares and cannot implement: a second surface, swapchain and input queue belong to
  `Vixen.Platform` and the app head. Whether a *saved* window position is still on a display is the
  host's answer too, for the same reason — `PlatformWindowHost.IsReachable`. `EditorShell.Title` is
  composed here and *applied* by the host, on the same terms.
- **A settings store.** `SettingsView` is a rail, a pane, a search over every page and an Apply, and
  it has no idea what a setting is: a page is an id, a title and something that fills an element.
  Which store the pages are over — the user's or the project's — and what draws them is
  `Vixen.Editor.App`'s, because the inspector is not this assembly's business either.
- **A native file picker.** `DialogService` is the editor's own modal questions — confirm, prompt,
  choose — drawn as a `Vixen.Ui.Controls` `Dialog` in the shell's document, because a modal that is
  an OS window cannot be screenshotted by the golden suite or driven by the automation harness. The
  *file* pickers are the opposite case: they are about the user's disk rather than the editor's
  state, they go through `INativeDialogs`, and reaching one is `Vixen.Editor.App`'s job.

## Known gaps

- **Palette recency.** Doc 20 calls "recently used" boosting the single cheapest thing that makes a
  palette feel fast, and the ranking is still score-only.
- **Command history and repeat.** `CommandRegistry.Executed` fires for every run and nothing keeps
  the list, so there is no `Ctrl+Shift+R`.
- **An icon set.** `EditorIcons` is the two dozen glyphs the chrome cannot be drawn without, on the
  same 24×24 grid `ControlIcons` uses and reachable by id so a plugin can name one. Doc 20 puts the
  real set at about a hundred and twenty and calls it a design dependency; the mitigation is that
  `ToolbarPresenter` draws a command with no icon as a labelled button, so a missing glyph costs a
  wider button and never a blocked feature.
- **`Strings.Resource` generation.** `EditorStrings` is hand-written in the shape the generator will
  emit, so nothing at a call site changes when it lands — but an id used nowhere and an id declared
  nowhere are not yet build errors.
- **A mode bar.** `IEditorMode` — the seam Select / Landscape / Foliage would hang off — does not
  exist, which doc 20's A1 calls the one structural addition still owed to the frame.

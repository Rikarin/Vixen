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
lines that embedded the markup as resources, walked the manifest and ran the scanner — and it could
only ever see *markup*. Most of this assembly's chrome is built in C# with `AddClass("…")`, so every
utility a code-built panel asked for was silently missing. The step scans `@(Compile)` too. (That
file is gone: the sample declares `VixenUi` and the build writes its sheet like everybody else's.)

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

⚠ **The model is no longer here.** `BackgroundTask`, `BackgroundTaskManager` and
`BackgroundTaskState` moved to `Vixen.Ui` — see [its README](../../Core/Vixen.Ui/README.md#background-tasks)
and [the guide page](../../docs/guide/ui/background-tasks.md). They were application-framework
machinery reachable only by the editor — the pattern [doc 46](../../docs/plan/46-what-an-application-needs.md) measures; what stays here is the
*task centre*, the panel below, because a panel made of `EditorStrings` and the editor's own tags is
chrome rather than framework. The seam cost one `@using Vixen.Ui` line in `TaskCenter.vxml`.

`EditorShell` still owns its own manager and pumps it in `Tick`, rather than using
`UiApplication.Tasks`: the editor's host is `EditorHost` and has its own loop. What changed for the
shell is that `Dispose` now disposes the manager instead of calling `CancelAll` — `CancelAll` asks
and leaves the manager listening, so work still on the pool keeps enqueueing into a queue the shell
will never pump again, and a task whose delegate came from a plugin keeps that plugin's collectible
load context alive through the closure.

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

**Where it stands.** ~~Twenty~~ ~~Twenty-seven~~ ~~Thirty-four~~ ~~Forty-one~~ **Forty-eight**
`.vxml` files across
~~six~~ ~~seven~~ **nine** assemblies — ~~thirty-three~~ **forty** panels, seven shared parts
(`Vixen.Editor.Ui/Parts/FactRow.vxml`, `Vixen.Editor.AssetEditors/AnalysisRow.vxml`,
`Vixen.Editor.Terrain`'s `FactBlock`, `LayerBlock` and `PaletteBlock`, and water's
`WaterZoneFacts` and `WaterNotice`) and one test fixture
(`Vixen.Editor.NodeGraph.Tests/SealedControlHost.vxml`, which is not a panel and is the ninth
assembly) — against **62 files and ~31,700
lines** of editor C# that construct UI. Sixty-two is not sixty-two panels — a third of those files
turn out not to be panels at all, which is the first finding. There are 25 `RegisterPanel` call
sites and 34 `editor.panel.*` ids, so **34 is the denominator**, not 62 and not 120,000 lines.
⚠ ~~**`Vixen.Editor.Water` is the seventh assembly and it cost two lines of `.csproj` to become one**
— see the wave-5 note under the six S-sized ports; the first `.vxml` in an assembly needs the markup
generator and the `Vixen.Ui.targets` import naming it, and not having them is not a diagnostic.~~
⚠ ~~**`Vixen.Editor.NodeGraph` is the eighth, and it had one of the two lines, which is worse than
having neither.**~~ The `Vixen.Ui.targets` import was already there for the `**/*.vcss` glob — with a
comment saying "there is no `.vxml` in this project, so the other half of the .targets is inert
here" — so the moment one appeared the glob made it an item and no generator read it. ~~The failure
is identical to having no import at all and reads as a mistake in the markup~~; the comment that said
what the file assumed is the only reason it took a minute rather than an hour. ~~**Wave 6's
prescription: before writing an assembly's first `.vxml`, `grep -c Vixen.Ui.Markup.Generators` its
`.csproj` and expect 1.**~~

✅ **All three struck 2026-08-23: it is two diagnostics now, and it is one line rather than two.**
`VX4001` is a `.vxml` on disk that is not compiler input, checked in `Directory.Build.targets`
because a check inside `Vixen.Ui.targets` is a check inside the thing that was never imported.
`VX4002` is a `.vxml` that *is* an `AdditionalFiles` item with no `Vixen.Ui.Markup.Generators.dll` in
`@(Analyzer)` — the NodeGraph shape — and lives in `Vixen.Ui.targets`, so it travels to package
consumers as well. Both are **errors** naming the `.csproj` and the one line that fixes it, both are
built and sabotage-verified by `BuildIntegrationTests`, and the escape for markup that is
deliberately uncompiled is `<VixenUiMarkupCheck>false</VixenUiMarkupCheck>` (one project needs it:
`Tools/Vixen.Templates`, whose `.vxml` belongs to a scaffold it copies rather than builds).

⚠ **Three things the wave-6 note got wrong, and each is worth more than the fix.**

- **Not a `VXML` code.** That space belongs to a generator that has read the file, and the whole
  content of this failure is that none ran. `VX4001`/`VX4002` are MSBuild diagnostics in
  `docs/manual/diagnostic-codes.md`, in the `VX4000` range already reserved for UI markup.
- **Not a warning.** A warning ahead of a wall of C# errors buys a log line — and in the case nobody
  had met, there is no wall: ⚠ **a `.vxml` with no hand-written partner does not fail at all.** It is
  simply not read, the class does not exist, and the build succeeds. That is the state the two
  diagnostics exist for, more than the noisy one.
- **Not two lines.** `<VixenUi>true</VixenUi>` has been the whole of it since `Directory.Build.targets`
  became the in-repo stand-in for the package. Twelve projects still hand-write the import and the
  two analyzer references — the count in that file's own comment says fifteen and is stale — and
  converting them now deletes duplication rather than fixing a trap.

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

⚠ **One half of this is narrower since 2026-08-23, and it is not the half about painting.** "The
content reaches the screen through a control" stays true of all five: a `DataGrid` column projection
and a `VirtualizingPanel` row template are still nothing markup can describe. What changed is
*feeding* the control. `use="@(v => v.SetItems(rows))"` is an ordinary binding — the call is an
effect, so it re-runs when what it read changes — so `Canvas.Graph = built`, `Inspect(…)` and
`SetItems(…)` are all sayable now, and a panel that is imperative only because its control is fed by
a method no longer has to be. Read this shape as being about what a control *draws*, never about how
it is told.

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

✅ **Both were fixed (2026-08-22), and the diagnosis above is right about `on:` and wrong about what
follows from it.** No entry in the `Subscribe` table can carry a value — that part stands — but a
value change was never an event to begin with. Every one of those four notifications is a
`[UiProperty]` underneath, and `UiElement.PropertyChanged` already carries all of them, which is what
`bind:` has always ridden. So `change:Value` is `bind:`'s write-back leg with a handler instead of an
assignment: no new table, nothing registered per control, and it hears more than the control's own
event does. Read `change:X` as "whatever `bind:X` could have bound, but *run* this".

And `refs="@Faders"` gives a row its own handle, keyed on the identity `BuildContext.For` reconciled
it on — so `Faders[bus]` inside a strip's handler is that strip's fader whatever the list has done
since. `VXML2013` refuses it outside a loop, the mirror of `VXML2010`. Both are documented in
`Core/Vixen.Ui.Markup/README.md`; the ledger below is updated, and the two remedies at the end of it
are struck.

✅ **And `AudioMixerView` is ported (2026-08-23), which is the evidence rather than the claim.** Both
directives are used exactly as they were designed to be: `refs="@Faders"`/`refs="@Mutes"` and two
`change:` handlers per strip, each reaching *its own* row's other control by key. The port is held to
a whole-tree rectangle dump — every element's tag, classes and absolute rectangle, in three states —
that is **byte-identical** to the hand-written panel's, and the "a change made while effects are
draining is not reported" rule is asserted directly: opening a mixer leaves the undo stack at depth
nought, and the very next fader move takes it to one.

**3. A bound value is right on the next frame, not this one.** `EffectScheduler`'s contract is that
writing a signal *only ever queues*, and `Flush` drains it once per frame, after input and before
layout, because an effect running at the write would mutate the tree while the renderer walked it
(ADR-007). ⚠ **This one is invisible from the panel's own source — only from its callers.**
`BuildSettingsView` reads `BuildButton.Disabled` on the line after `Rebuild()`, with no frame
between, and that is the assertion, not an accident: the panel has to be right the instant it is
asked. **Before signal-backing anything, grep for callers that read it back synchronously.**

⚠ **And then read what the grep found, because wave 5 nearly refused a panel on it.**
`CompiledSceneView` looks identical to `BuildSettingsView` from here — `CompiledSceneTests` reads
`view.Blocks.Children.Count` on the line after `Refresh()`, three times. The difference is *who*: the
one production caller is `tabs.AddTab("Compiled").Panel.Add<CompiledSceneView>().Show(scene)` and
reads nothing back at all, and the test file itself says why its element assertions exist — "a
projection that produced the right numbers and drew none of them would pass every test about the
numbers", which is a claim about coverage rather than about timing. The shape is therefore
**"a caller reads it back synchronously", not "a test does"**, and the two are worth telling apart:
the values that were genuinely synchronous — `Refresh()`'s answer, `Content`, `Reported` — still are,
because signal *reads* are immediate and only the effects are queued. Three assertions gained a
`Frame()`. `BuildSettingsView` stays imperative because its callers really do read it back.

**4. It is not a panel.** Roughly a third of the "UI" files are presenters that build into a
caller's element (`ProjectBrowser`, `ViewportLayout`, `SceneHierarchyView`, `ToolbarPresenter`,
`MenuPresenter`), services with no fixed tree (`AssetPicker`), registration wiring
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

✅ **And the escape is four lines rather than a file, which is what wave 3's mixer port found.**
"Capitalised tag" does not mean "`Component`" and does not mean "`.vxml`": the emitter writes
`ctx.Child<T>(…)` for any PascalCase tag and lets C# overload resolution settle it, and `Text` is a
`[UiProperty]` on **every** `UiElement`. So

```csharp
internal sealed class MixerTitle : UiElement {
    protected override string TagName => "mixer-title";
}
```

makes `<MixerTitle Text="@Heading" />` a real assignment to that element's own `Text`, with the same
tag the stylesheet already names, in the same position, with no extra box. `AudioMixerView` needed
nine of them and they cost about forty lines between them — where nine `.vxml` parts would have been
nine files. **A part is worth a file when it has a shape; a caption has none.** `FactRow` stays a
part because it is four elements and two cells that disagree about where the text goes; these are one
element whose only content is its own text.

⚠ ~~**What this does *not* buy is an intrinsic tag written in lowercase.** The subclass is still a
type somebody has to declare, so shape 5 is unchanged as a statement about the language — it is the
*cost* of the escape that turned out to be small~~, which is why `FlameChartView`'s second withdrawal
reason is now closed.

✅ **Shape 5 *is* changed as a statement about the language, since 2026-08-23.**
`<fact-name use="@(cell => cell.Text = Label)" />` writes an intrinsic element's own `Text`, with no
type declared and no extra box: `use` runs an `Action<T>` against what the tag made, as an effect. So
"markup cannot write an element's own `Text`" is now false, and what is left of shape 5 is a
*preference*: `<FactName Text="@Label" />` is checked at the tag and reads as the property assignment
it is, so the four-line subclass stays the first answer for a caption and `use` is the answer where
there is no subclass to be had — a `sealed` control, or a call with three arguments. Nothing here was
rewritten; the nine captions and the two `Captions.cs` cells are still the better spelling.

⚠ **And the escape is not about `Text`, which is wave 5's correction to this whole block.** Every
sentence above says "`Text`", and `Text` is only the first thing it was needed for. The general
statement is: **a binding may not assign a flag set; it may assign a property that owns one bit of
it.** `UiElement.State` holds Hover, Focused, Pressed and Checked, so `<Button State="…" />` would
undo whatever the pointer had just put there — which is why `KeyBindingsView` and `FlameChartView`
both keep `State |= Checked` imperative and say so. `SettingsView`'s rail could not: its handles would
have come from `refs`, `refs` is filled by an effect, and `Restate` is called synchronously from
`Select` — including once from `Add` before any frame has run, where the dictionary would have been
empty and the first tab would silently not have highlighted. So the rail's tab is

```csharp
internal sealed class SettingsTab : ButtonBase {
    protected override string TagName => "button";

    public bool Selected {
        get => (State & ElementState.Checked) != 0;
        set { if (value) State |= ElementState.Checked; else State &= ~ElementState.Checked; }
    }
}
```

and `Selected="@IsChosen(page)"` is an ordinary binding. Same tag, same `size-md variant-subtle
settings-tab`, so `settings-rail > button.settings-tab:checked` reaches it unchanged — which is the
test of whether an escape is an escape or a redesign. ⚠ **`ButtonBase` rather than `Button` only
because `Button` is sealed**, and the two are the same type: `Button` adds a tag name and nothing
else. ⚠ **This is also what `FlameChartView`'s reason 3 was actually waiting for** — `refs` was
nominated for that job and would work there, where the flag is written from a *click handler* rather
than from a synchronous restate, but the property is the smaller answer and needs no handle at all.

### The surviving-region rule is not only about `@for`, and that is wave 4's finding

⚠ **"A surviving key never re-runs its body" is a statement about *regions*, and `@if` opens one
too.** The `@for` corollary is written down twice above and both times as a loop rule; it is not.
`BuildContext.Switch` and `BuildContext.For` are deliberately the same mechanism — the file says so —
and `Switch` rebuilds its arm **only when the arm index changes**. So a side panel whose `@if` reads
"is anything selected" does not rebuild when the selection moves from one thing to another, and a
binding inside that arm which closed over an `is { } shown` pattern variable keeps showing whatever
was selected first.

⚠ **It is worse than the `@for` version in one specific way: nothing warns you.** A `ref` in a loop
is `VXML2010` and a `refs` outside one is `VXML2013`, so the loop shape has two diagnostics pointing
at it. A pattern variable in an `@if` arm is ordinary, legal C# that compiles, runs, and is correct
for the first value it ever sees. `VariationHarnessView` was written with one and the whole existing
suite passed: **every test in `HarnessViewTests` selected exactly one cell.**

**So the rule generalises to: a binding may close over a region's *identity* and never over its
content.** For a `@for` row that identity is the key; for an `@if` arm it is the *predicate* — and a
predicate like "is anything selected" identifies far less than a key does, which is why this is the
sharper edge of the two. Every readout in that arm goes back through the signal
(`ChosenCase`, `ChosenResidual`, …) and the arm's condition is the only thing allowed to be a shape.
`ChoosingASecondCellMovesTheSidePanelOffTheFirst` is the test, and it was confirmed to fail against a
deliberately reintroduced stale readout while the other six passed.

⚠ **And `refs` has a second use, which the mixer's write-up did not have a case for.** There it was
"a handler must reach a sibling control it cannot read off the model". Here nothing is edited at all
— what needs the per-row handle is a **hit test**: a plain element raises no click, seventy cells are
deliberately not seventy controls, and the press is resolved by asking each cell whether it contains
the point. `ElementRefs` refusing to enumerate is exactly right for this: the report is already the
authority on what order the rows are in, and the loop walks the model and looks each row up. **A
read-only panel can still need `refs`**, which the ledger's sizing did not anticipate — it had
`VariationHarnessView` down as needing neither directive.

⚠ **The one state that is not byte-identical, and why it is allowed.** Six states were dumped —
nothing shown, a report shown, a passing cell selected, an unresolved cell selected, a declared plan
not yet run, and a refused run. Five match to the byte. The sixth is the *first*: a panel that is
added to a document and framed **without `Show` ever being called**. The hand-written panel left the
verdict blank and the side panel empty there, because `Reload` had never run; the markup builds its
empty state at construction, so the verdict says "No run yet." and the side panel says "Select a
cell.". ⚠ **That is not a new appearance — it is the state the hand-written panel's own public
`Reload()` produces**, arriving one call earlier. Nothing reaches it: `HarnessEditorFactory.CreateView`
calls `Show` on the line after `Add`, before any frame, and every test does the same. Recorded rather
than papered over, because the way to make it match would be a "has been shown" signal gating the
whole tree, which is a worse panel for an unreachable state.

⚠ **And one behaviour deliberately kept, bug and all.** The hit test is geometric —
`element.Bounds.Contains(point)` over the cells in the model's order — so a press that lands outside
`harness-matrix` but inside some cell's rectangle selects that cell, including a cell scrolled out of
view under `overflow: auto` or sitting behind the side panel. Routing the press through
`on:pointerdown` on each cell instead would fix it for free and was the more natural markup. It was
not done: a port that changes behaviour is a defect until argued for, and this belongs in a commit
that says so. Same call as the mixer's "renaming a bus deselects it".

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

✅ **And that is exactly what happened: wave 5 used the part for terrain and water, and gave the
other assembly its own two cells.** `Vixen.Editor.AssetEditors/Captions.cs` holds `FactName` and
`FactValue` — four lines each, the shape-5 escape — and `AudioMixerView`, `VariationHarnessView`,
`CompiledSceneView` and `TextureImportView` all resolve them from the enclosing namespace with no
`@using`. ⚠ **Wave 4 argued against hoisting them and was right at the time**: "a shared declaration
would buy a file and move nothing" is true of two private copies and false of four, because four
copies of a tag name is how two of them come to disagree about it. The hoist is byte-neutral — same
tags, same `AssetEditorTheme` rules, and both existing panels' dumps are unchanged.

⚠ **The rules stay in `AssetEditorTheme.vcss`.** `fact-row`, `fact-name` and `fact-value` are
declared there, and `frame-editor fact-name` overrides two of them in the same sheet; moving the
declarations into `EditorTheme` would move them earlier in the load order and change which one wins.
The tag names are the contract, and `EditorApplication` installs both sheets into the one document.

⚠ **`Section` was blocked on a two-year-old typo, and the typo was that four titles had never been
styled.** `WorldTheme.vcss` says `world-title`; `EditorWorlds` wrote `panel.Add("world-title")` and
`TerrainModulePanels`, `WaterModulePanels`, `BlockoutModulePanels` and `StandardFrameView` all wrote
`panel.Add("World-title")` with a capital. `NameTable` interns ordinally and says why — VXML's own
rule is that case distinguishes a component from an element — so the rule reached one of the five
call sites and never reached the other four. **Fixed 2026-08-23 by correcting the four**, which is
what the sheet always meant: the four section titles gain `font-weight: 600` and `margin-top: 6px`,
and `Section` is now free to be shared on the one spelling.

⚠ **Nothing rendered those four panels in a test, which is why the fix moved no baseline.**
`WorldTheme.Install` is called from `EditorApplication` and nowhere else, so no suite ever loaded the
sheet and no `__screenshots__` reference contains a `world-title` in either spelling. The pixels do
move — in the running editor, in the terrain, water and blockout panels and the standard-frame
inspector — and no committed picture was in a position to notice.

⚠ **The class of bug is now gated, narrowly.** `TypeSelectorReachTests` in `Vixen.Ui.Styling.Tests`
sweeps every `.vcss` and every element-creation call site in the tree and fails a *hyphenated* tag
whose lowercase spelling the sheets style and whose written spelling they do not. Narrow on purpose:
"every tag has a rule" is false for hundreds of legitimate class-styled containers, and "every rule
has a tag" is false for well over a hundred control parts written ahead of their panels. A
hyphenated capital is the one unambiguous case — `ComponentEmitter` emits a capitalised tag as
`Child<Tag>`, so a hyphen there is a C# syntax error and cannot survive a build, which is exactly why
this bug could only ever hide in a C# string literal.

✅ **And `AnalysisRow` is the second shared part, built by wave 6 on the condition wave 3 set.**
`AudioMixerView.cs` declared `AnalysisStage`/`AnalysisMessage` privately and said "hoisting it into
a part is a job for whoever ports the second of them, because doing it here would move pixels in a
panel this change has not measured". `QueryView` and `GoapDomainView` are the second and the third,
so the triple is `Vixen.Editor.AssetEditors/AnalysisRow.vxml` (`@tag analysis-row`, `@inherits
Vixen.Ui.UiElement`) with its two cells and a shared `AnalysisNote` record in `Captions.cs`. The
mixer moved onto it in the same commit and its dump was re-taken to check: **unchanged**, because
the part's host tag *is* the `analysis-row` the loop built. Nine panels in that assembly build this
row by hand; ~~three~~ ~~five~~ **eight** of them are on the part now — wave 7 added `UtilitySetView`
and `StandardFrameView`, the latter with both of its lists, and wave 8 added `VfxGraphView`,
`ShaderGraphView` (also with both of its lists) and `CompositorView`.

⚠ **And the part had no dump test of its own until wave 8, which is the thing to notice about it.**
Five panels were building their output out of it on the strength of one sentence in a commit
message. `ChromeDumpTests.The_analysis_row_is_the_four_lines_every_report_wrote` is the gate now:
both forms in the same place, tree and flags compared as strings, plus an assertion that the two
cells are still `Children[0]` and `Children[^1]` — because that is how `ShaderGraphTests` reads them,
and a part that wrapped either cell would pass a comparison against itself and fail those.

⚠ **A caller with a *severity* has to hold it somewhere, and that is the one seam in the part.**
`class` is deliberately not a parameter, so a caller writes `class="@note.Class"` — which means its
row record needs a field the shared `AnalysisNote` does not have. `StandardFrameView` declares
`FrameNote` for exactly that and nothing else. Widening `AnalysisNote` was considered and refused: it
would give seven other callers a field they do not use, to save one panel a four-line record.

⚠ **`class` is deliberately not a parameter of it.** `analysis-row.error` and `.warning` are the two
severity colours and `AddressableGroupsView` passes them positionally, but `class` is one of markup's
three universal attributes — so a caller writes `class="@note.Class"` on the tag and the part has no
opinion about severity at all. A `Severity` parameter would have been a second place to decide what
red means.

⚠ **`PanelTitle` is in `Captions.cs` rather than being a part, and it is the shape-5 line again.**
`panel.Add("panel-title").Text = "Diagnostics"` is written twenty-three times across the AI,
behaviour-tree and utility editors — a caption, not a row, so four lines of `UiElement` rather than a
file. ⚠ And it is a *tag* with no rule: `EditorTheme`'s only `panel-title` rule is
`viewport-panel > .panel-title`, a **class** selector reached from `ViewportChrome.AddClass`, and
every panel in `Vixen.Editor.AssetEditors` writes the tag instead. That asymmetry is preserved rather
than tidied — giving these headings the class would restyle five panels in a commit about markup —
and it is recorded here because it looks exactly like a bug and is not one this wave should fix.

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

✅ **Wave 7 took it again for the last two panels in the module, and the prescription needed no
amendment.** `Terrain` main gave up two containers and `Terrain foliage` one: `terrain-facts` turned
out to *be* `FactBlock` already — the module's `Fact` helper has built `FactRow`s since wave 5, so
the change is `panel.Add<FactBlock>()` and a `Show` — while the layer stack is `LayerBlock` and the
palette is `PaletteBlock`. ⚠ **`LayerBlock` is a second type and not a parameter**, which is wave 5's
`WaterFacts`/`WaterZoneFacts` call made for the third time: `@tag` is a compile-time directive, and
neither `terrain-facts` nor `terrain-layers` is styled by anything, so one shared type would have
rendered identically and lied about what the panel is made of. ⚠ **`PaletteBlock` is the one with a
shape** — two arms, because an empty palette says *why* it is empty, and a `change:IsChecked` per
type with **no `refs`**: the handler closes over the entry's slot and the slot is in the key, which
is what tells the two uses of `refs` apart. Held to the loops they replace in `PaletteBlockTests`,
whole document, tree **and per-element state**, byte-for-byte in four states.

✅ **Wave 5 took that way through for five of the six and it is the right prescription.**
`Vixen.Editor.Terrain/FactBlock.vxml` (`@tag terrain-facts`) serves grass, growth and splines;
`WaterZoneFacts` and `WaterNotice` serve the two water panels — the zone part twice, under two
tags, which was two types until `tag=` landed. Every one is
`@inherits Vixen.Ui.UiElement` rather than `Control` — a `Control` gives itself `variant-default` and
`size-md` in `OnCreated` and the plain elements they replace have neither — and each is asserted by
dumping the whole document's rectangles for the hand-written loop and for the part and comparing the
two strings, in `FactBlockTests` and `WaterFactsTests`.

⚠ ~~**Three things the prescription did not say, and the first one costs an hour if you meet it
cold.** **The first `.vxml` in an assembly needs two lines of `.csproj` and there is no diagnostic
when they are missing.**~~ `Vixen.Editor.Water` had never had one, so the generator never ran, the
`.vxml` was not an item at all, and the build failed with "`WaterZoneFacts` does not contain a
definition for `Show`" on every member the markup declares — which reads as a mistake in the markup
and is a mistake in the project file. ~~The two lines are the `Vixen.Ui.Markup.Generators`
`ProjectReference` as an `Analyzer` and the `Vixen.Ui.targets` import at the bottom of the file~~;
`Vixen.Editor.Terrain` and `Vixen.Editor.AssetEditors` each already carry them with a comment saying
a `PackageReference` to `Vixen.Ui` would have brought both and a `ProjectReference` does not. ~~Worth
a `VXML` diagnostic, or a `.vxml` in a project with no generator being an MSBuild warning; today it
is neither.~~ ✅ **`VX4001` and `VX4002`, both errors, since 2026-08-23 — and the line count was
wrong too: it is `<VixenUi>true</VixenUi>` and nothing else.** See the strike under "Where it
stands".

**And two more.**

- ~~**`@tag` is a compile-time directive, so "the same part under another name" is not sayable.**
  `WaterFacts` is `WaterZoneFacts` minus the refusal and under a different tag, and there is no
  parameter for that. Neither tag is styled by any sheet in the tree — nor is `terrain-facts`,
  `water-notice`, `water-refusal` or `verb-row` — so a single shared type under one tag would have
  rendered identically and lied in five places about what the panel is made of. Two types.~~
  ✅ **One type, since 2026-08-23, and this entry was wrong about the mechanism rather than about the
  taste.** Everything after "there is no parameter for that" is right and still is: a shared type
  under *one* tag would have lied, so refusing that was correct. What the entry did not check is
  whether a tag has to come from the type at all — and it does not. `UiDocument.Adopt` takes the tag
  and only falls back to `TagName`, so `panel.Add<WaterZoneFacts>("water-facts")` was already legal
  C# on the day this was written; the markup half is now `tag="water-facts"`. `WaterFacts.vxml` is
  deleted, the body panel calls the zone part under the tag its own structure names, and
  `WaterFactsTests`' dump of the body block is unchanged — the refusal arm is simply never built,
  because that caller passes no reason. ⚠ **The lesson is the one the "two recorded gaps are stale"
  section already teaches**: this was recorded as a language limitation without anyone asking the
  runtime whether it had the feature.
- **The refusal row is the one place markup's *natural* spelling is the right one.**
  `element.Add("water-refusal").Add("text").Text = why` is `<water-refusal>@Refusal</water-refusal>`
  exactly, because an interpolation appends a `text` child and that is what the C# built. Shape 5 is
  about a tag's *own* `Text`; where the target is a child, there is no escape needed at all.

⚠ **`Blockout settings` is the sixth and it is a "no", for a reason that is not the `DockPanel`
rule.** Its seven children are three `world-title`s, three `InspectorView`s and one `Button` — every
one a single element, so there is no part to make: a part is worth a file when it has a shape, and
seven things with no shape between them is the panel, which is the thing that may not become a
component. The whole-panel route was not attempted and the `flex-shrink` warning above is therefore
still **unverified**: a probe against a bare `DockPanel` measures nothing, because a panel outside a
`DockingHost` has no box at all. Whoever wants this panel should measure the warning first, in a real
session — and if it turns out that a `flex-shrink: 0` wrapper scrolls correctly after all, this
paragraph and the one above it both change.

**`VerbRow` is still not built, and porting a panel that has verbs did not change the answer.** The
water body panel has five of them across two rows, and `Verbs(panel, …)` is eight lines that build a
`verb-row` and a button each. A part would need the verb list to be a signal and a callback to run
the command — more surface than it removes, for a tag no sheet styles. The revisit condition this
section set has now been met and answered: still no.

### ⚠ "Byte-identical in N dumped states" is a wave note, not a test

Nine rows below and half the prose above claim a port was byte-identical in three, six, seven, eight,
nine, fifteen or sixteen dumped states. **Three test files in the whole editor dump a tree**, and all
three belong to the shared parts: `FactRowTests`, `FactBlockTests`, `WaterFactsTests`. Every other
dump was a throwaway harness, run once, compared by eye or by `diff`, and deleted — which is exactly
what wave 5's note about `CompiledSceneView` asked for and is *not* what a reader of this table would
assume. Nothing in the tree re-checks any of them, so a later change that moves those pixels is a
change no gate will notice.

Found while converting `RemoteInspectorClient` (build item 8), whose row claims eight of nine states:
its committed guard is `PortedPanelTests`, which asserts behaviour and not geometry. The claims are
believed — each was made by somebody who ran the comparison — and they are **evidence about a
commit**, not coverage. Whoever next wants one of these to hold should promote the harness rather
than trust the sentence, and the three that exist are the pattern to copy.

⚠ **Asked a third time by wave 7 and answered the same, with one number that sharpens it.** The
Terrain panel has seven verbs across two `verb-row`s and Foliage has two, so `Verbs(panel, …)` now
has the most callers of any helper in that module — which is the case that would normally argue
*for* a part. It still does not, and the reason has moved from "not enough callers" to something
firmer: every one of those buttons is `Shell.Commands.Execute(command)`, so a part would have to
take the shell, or a callback per verb, and a verb list that a caller assigns after construction is
a frame behind where three lines of C# are not. `StandardFrameView` has the counter-example in the
same wave — its one `verb-row` holds a single button whose handler is the panel's own method, and
that is `<verb-row><Button on:click="…" /></verb-row>`, which needs no part at all.

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
| `FlameChartView` | snapshot | no | **no — withdrawn a second time (2026-08-23), and two of its three reasons are now wrong.** See below | S/M |
| `CompiledSceneView` | snapshot | no | ~~**port**~~ **done, wave 5 (2026-08-23).** The sizing was right and "purest snapshot" was right; what it got wrong is that this row and `BuildSettingsView`'s look alike from the outside and are not. 238 lines of C# → a 323-line `.vxml` and a 132-line `.cs` of two key records and six captions; **byte-identical in all six dumped states**. See below | M |
| `VariationHarnessView` | snapshot | no | ~~**port**~~ **done, wave 4 (2026-08-23).** The sizing was right and the reasoning was not: read-only and zero `change:` subscriptions, but it needed `refs` all the same — for the hit test, not for a handler. 230 lines of C# → a `.vxml` and a 60-line `.cs` of one key record and seven captions; byte-identical in five of six dumped states, and the sixth is argued below | S |
| `SettingsView` | live (view-local) | no | ~~**port — the exclusion is lifted**~~ **done, wave 5 (2026-08-23).** The lift was right about the pane and silent about the rail, which is where the work was: `Restate` writes `State \|= Checked` on one of a *list* of buttons and `refs` cannot serve it. 332 lines → a 344-line `.vxml` and a 119-line `.cs`; byte-identical in eight dumped states including the checked bit and the three `Disabled` flags. See below | M |
| `TextureImportView` | snapshot | no | ~~**port**~~ **done, wave 5 (2026-08-23).** The `<Tabs>`/`<TabItem>` half was exactly as advertised and cost four tags; the part the sizing did not see is that its four facts had to become *one* snapshot record, because three of them depend on a mutable settings object no signal watches. 277 lines → a 356-line `.vxml` and an 88-line `.cs`; byte-identical in seven dumped states | M |
| Water zone · Water body · Terrain grass/growth/splines | snapshot | no | ~~**port, six small ones**~~ **five done, wave 5 (2026-08-23).** Ported as *parts*, exactly as the block below this table prescribes, and that prescription is the finding: each is now `panel.Add<FactBlock>()` where it was `panel.Add("terrain-facts")`. Splines' duplicated fact block is gone — into one method rather than a `@for`, which is where it actually lived | S each |
| Blockout settings | snapshot | no | ~~**port**~~ **no — it has no part with a shape.** Its seven children are three `world-title`s, three `InspectorView`s and a `Button`: every one is a single element, so there is nothing for a part to be, and the only container is the `DockPanel` itself. See below | S |
| `QueryView` · `GoapDomainView` · `AgentDebuggerView` | snapshot | no | ~~**port; leave `CurveEditor`/`NodeCanvas` behind a `ref`**~~ **done, wave 6 (2026-08-23).** The sizing was right and the `ref` instruction was right; what it got wrong is calling all three snapshots the same shape. Two of them choose a *tag* from the data — `query-row-selected`, `agent-row-live` — and a tag is not a class and cannot be bound, so the flag has to be in the **key**. 861 lines of C# → three `.vxml` (1,132 lines) and three `.cs` of records and captions (506); **byte-identical in all sixteen dumped states** | S/M |
| `CodeEditorView` · `VfxGraphView` · `ShaderGraphView` · `CompositorView` | live | no | ~~port the chrome; the editor/canvas/`KeyValueList` rows stay~~ **all four done, wave 8 (2026-08-24).** The instruction was right and the sizing missed the biggest single win in it: three of the four had a `Report` that rebuilt `analysis-row`s **by hand**, four elements at a time, a wave after `AnalysisRow` was extracted for exactly that. 1,336 lines of C# → 923 of `.vxml` and 361 of `.cs` (two accessibility modifiers and four factories that never moved). What the row says about "the chrome" is also wrong for `CodeEditorView`, whose chrome is *one element*: the port there is `PreviewCodeEditorView`'s four, and it is the tree's first `.vxml` that `@inherits` another `.vxml`. See below | S–M |
| `NodeSearchPopup` · `CommandPalette` | snapshot | no | ~~**port; each deletes a hand-rolled element pool or reconciler**~~ **done, wave 6 (2026-08-23).** The pool was the point and it was worse than "churn": it only ever *grew*, so a list that had once shown twelve rows carried the surplus under `display: none` **still labelled with the previous query's results**. 601 lines → two `.vxml` (565) and two `.cs` (288), with 31 and 26 lines of pooling gone. Every visible element byte-identical in fifteen dumped states; the only differences are the parked rows, which no longer exist. ⚠ **Wave 9 (2026-08-24) finished both**: the capture-leg key handler each kept in `OnComposed` is `<self on:keydown.capture>` now, neither takes `.handled`, and both got the key press their suites had never had — `PaletteTests` drove the palette with `Move`/`Accept` and `NodeSearchPopup` had **no test of any kind**. ⚠ And the first attempt at that test proved nothing: both panels focus their `SearchBox` on open, so a handler mis-written on `<SearchBox>` is still on the route as its *target* and every key press passes. The assertion that separates the host from a root beside it raises at the **list** | M |
| `NodeInspector` · `AddComponentMenu` | snapshot | no | ~~**port**~~ ~~**stopped, wave 6 — both blocked by the same cause, and it is not a markup gap.** Shape 1's escape is "keep the control behind a `ref`", and where the control is fed by a *method* the sanctioned workaround is the mixer's four-line wrapper subclass. `InspectorView` and `ScrollView` are both `sealed`, so neither panel can be written.~~ **both done, wave 8 (2026-08-24), and the blocker was two different things wearing one word.** `use` fed the inspector and `tag=` named the list; nothing was unsealed. 703 lines of C# → 780 of `.vxml` and 179 of `.cs`. The prize was real: `NodeInspector`'s 27-line `StringBuilder` signature is **12 lines** now, and they are two questions the provider already answered — `Describes` plus a selection-sequence compare. `AddComponentMenu` gave up 31 lines of pooling, and its pool was the fourth of wave 6's four to keep the previous query's labels under `display: none`. See below. ⚠ **Wave 9 (2026-08-24)** moved `AddComponentMenu`'s key handler to `<self on:keydown.capture>` — no `.handled` — and found that `AddComponentMenuDumpTests`, the file wave 6 held up as the pattern, **could not tell the host from the first root**: its `Arrowing_down` test presses Down with the focus on the search box, so the box is the route's target and a handler written there passes. Sabotage confirmed it. One test added, raising at the list | M |
| `RemoteInspectorView` | **live** | ~~no~~ **yes, additively** | ~~**port; signal-back `RemoteInspectorClient` additively, per `DeviceManager`**~~ **done, wave 6 (2026-08-23).** The sizing and the worked example were both right. What the row did not say is that this is the panel where the signals pay most: `Poll` runs from the tick, so `Restate` relabelled a button, rewrote a sentence, rebuilt the entity tree and re-ran a pool **sixty times a second whether or not the far end had said anything**. 293 lines → a 341-line `.vxml` and a 75-line `.cs`; byte-identical in eight of nine dumped states, the ninth being the parked counter rows. **Wave 7 finished it twice over**: the counters are a `SignalDictionary` rather than a `Signal<ImmutableDictionary<…>>`, so a per-frame counter update allocates nothing, and the `OnComposed` that subscribed to the tree's selection is gone — `change:SelectedNodes` is the whole of it | M |
| `Terrain` main · `Terrain foliage` · `MaterialView` · `FontView` · `StandardFrameView` · `ShapeVocabularyView` · `UtilitySetView` | mixed | no | ~~**port the readouts, keep the field rows (shape 2)**~~ **all seven done, wave 7 (2026-08-23/24).** The sizing was right about the block being the biggest one left and wrong about what "keep the field rows" means, four ways at once — stale for `FontView`, right-for-the-wrong-reason for `MaterialView` and again, differently, for `ShapeVocabularyView`, an understatement for `StandardFrameView`, and inapplicable to `UtilitySetView`, which has no field rows at all. 2,256 lines of panel C# → seven `.vxml` (2,504) and six `.cs` (1,023, of which 244 are `FontAtlasView` unchanged and ~180 are the four factories that never moved). Byte-identical in **twenty-seven dumped states** for the five asset-editor panels, plus **three** hand-written-versus-part dump comparisons committed in `PaletteBlockTests` for the two terrain ones — with two deliberate exceptions, both in one panel and both argued below. See below | M |
| `InputActionsView` | snapshot | no | **done, wave 9 (2026-08-24)** — and it was never in this table, which is the first thing to record: it is the fifth of the five capture-leg pickers and the only one with **no `.vxml` at all**, so wave 6 ported four of a set of five and this one was tracked only in the prose below. The chrome moved (a bar of six, a tree, a column of two) and with it the key handler, as `<self on:keydown.capture.handled>` — one of the two panels in the tree that should carry `.handled`. Three things stayed and each has a reason rather than a shrug: the **fields column**, because `Choice` calls `AddOption` once per enum member and `Restate` picks between five different forms; the **tree**, because `Reload` feeds a control by method; and **`Tree.SelectionChanged`**, because `change:SelectedNodes` is the *quieter* notification and this panel turns listening off in that handler, so switching would leave the mode running on a second click. The diagnostics list did move — a keyed `@for` over `AnalysisRow`. ⚠ **It had no view test before this** (`AuthoringTests` covers the document and never builds the view), so `InputActionsViewDumpTests` is both the port's evidence and the panel's first gate — and that means **no before-and-after dump was possible**, which is weaker than every other row here and is said rather than glossed: the chrome is asserted against the tree `OnCreated` is *described* as building, not against a recorded dump of it | M |
| `ComponentsView` | snapshot | no | chrome only — the foldout bodies are `IPropertyDrawer` output. ⚠ **Wave 8 reached it and stopped, and the sizing is what it got wrong.** "Chrome only" is three elements here — `component-list`, `component-drop-indicator` and the Add button — and the work is the *foldout loop* the row does not mention: one `Expander` per bridge, each with a `Document.Move(glyph, 1)` putting its icon between the chevron the header made and the header's own label, a routed `DragEvent` handler per header, and a `Sections` projection that `Drop` scans with `ReferenceEquals` while `Aim` does bounds arithmetic against `<components>` as the positioned ancestor. Four tests drive it with a real pointer drag. That is an M for the loop and an S for the chrome, and taking the S alone would move nothing. ⚠ **Declined a third time, wave 9 (2026-08-24) — and wave 8's four obstacles are really one, which is worth more than the decline.** Three of the four are not about the drag at all. `Sections` is `host.Children.OfType<Expander>()`, so a keyed `@for` producing one `<Expander>` per bridge yields *exactly* that projection and `Drop`'s `ReferenceEquals` scan survives untouched; `Aim` measures `Bounds` against this control's `AbsoluteLeft`/`AbsoluteTop`, which markup does not change either. Those three would port for free. **The one that blocks it is `Expander.ContentHost => Content`.** Nested tags under a control go to its content host, so markup can fill a foldout's *body* and has no spelling for its **header** — and the header is where all three of this panel's per-section additions go: the icon (`Document.Move(glyph, 1)`, deliberately between the chevron the control made and the label it made), the remove button, and the routed `DragEvent` that is the grab handle. So the `@for` would produce three empty foldouts and C# would still build every header, which is the whole of the loop.

⚠ **The escape exists and is why this is a decline rather than a "no": `use="@(fold => Header(fold, bridge))"` would run the header surgery per row**, since `use` is the sanctioned spelling for a control fed by a method. It is refused here on taste rather than on capability — `use` is an *effect*, so a header-building expression is one signal read away from appending the icon twice, and the four tests behind it drive a real pointer drag. **The honest fix is `Expander` publishing a header slot**, at which point the icon needs no `Move` and the port is ordinary. Whoever takes this should build that first and port second | M |
| `MoveSetView` · `ProxyShapeView` · `SequenceView` · `BehaviorTreeView` · `SpriteSheetView` · `AnimationGraphView` | mixed | no | ~~**defer.**~~ The half that was unportable was the field rows, which `change:` now expresses; `AnimationGraphView` still has no tests at all and still goes last | ~~L–XL~~ M–L |
| `AudioMixerView` | snapshot | no | ~~**no**~~ ~~**port**~~ **done, wave 3 (2026-08-23).** 541 lines of C# → a 250-line `.vxml`, a 60-line `.cs` of records and captions, and a whole-tree rectangle dump in three states that is byte-identical to what it replaced | ~~XL~~ M |
| `AnimationClipView` | snapshot | no | **no** — `Timeline.AddTrack`/`AddSpan` + `CurveEditor` is the whole panel | L |
| `NodeGraphView` | live | no | **no** — `Canvas.Graph = built` and four `OnDraw` layers; nodes, ports and wires are not elements | XL |
| `ConsoleView` · `MessageLogView` · `AssetGrid` | live | no | **no** — `VirtualizingPanel`/`Grid` row templates | — |
| `InspectorView` + the four drawers · `TargetOverrideMatrix` | — | — | **no** — a drawer *is* a factory, and markup cannot be one | — |
| `ProjectBrowser` · `SceneHierarchyView` · `ViewportLayout` · `ToolbarPresenter` · `MenuPresenter` · `AssetPicker` · `ViewportChrome` · `EditorSettingsPanels` · `EditorDiagnostics` · `DeclaredContributions` | — | — | **not panels** — shape 4 | — |

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

⚠ **Re-checked 2026-08-23 before starting it, and two of the three are now wrong. It is still a
"no", and reason 1 is the whole of why.**

- **Reason 2 is closed.** Shape 5's escape turned out to cost four lines rather than a file — see the
  block under shape 5 — so `internal sealed class FlameBar : UiElement` with
  `TagName => "flame-bar"` makes `<FlameBar Text="@caption" />` an assignment to the bar's own `Text`
  with no extra box. `AudioMixerView` shipped nine of them.
- **Reason 3 is closed.** `refs` *is* the per-row handle this said did not exist:
  `Bars[node].State |= ElementState.Checked` in the click handler is the same two lines the pool
  writes, reaching the same element.
- **Reason 1 is wrong as written and right underneath.** "Clicking a bar re-zooms, which changes
  every bar's geometry and therefore every key" is only true of a *value* key like
  `GpuTimelineView`'s. A `FlameNode` is a reference and survives a zoom, so a node-keyed `@for` keeps
  the clicked bar's element — and `BuildContext.For` genuinely reorders survivors rather than
  rebuilding them. **But the asymmetry it was pointing at is real and is not a panel's to fix.**
  `ElementState.Hover` is written in exactly one place in the whole engine — `Hover.cs`'s `Restate`,
  reached from `UiDocument.Track`, which has exactly one call site: a pointer dispatch. So an element
  that comes into existence between two pointer events **cannot** be hovered until the pointer moves,
  and `ForgetHover` takes the old one out as it goes. The pool never removes an element, so it never
  loses the state. That bites hardest where it is least of a corner case: `Show()` replaces every
  node, so a chart repainted from a live capture while the pointer rests on it loses `:hover` under a
  keyed loop and keeps it under the pool.

So the verdict stands and the reason has moved. It is no longer blocked on a markup feature — both of
those landed — it is blocked on **hover being recomputed only by pointer input**, which is
`Core/Vixen.Ui`'s business and a change every panel would feel. Whoever wants this panel should take
that up first, or argue the new behaviour is better and record the pixel change deliberately; either
is a commit that says so, and neither is a port. Until then `GpuTimelineView.vxml`'s remark that the
`parked` rule "still exists for `FlameChartView`, which still pools" stays true.

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

✅ **Ported 2026-08-23, and the pane was the easy half exactly as written.** What the lift did not
mention is the **rail**, which is where the work turned out to be: `Restate` sets
`button.State |= Checked` on one of a *list* of buttons, `refs` cannot serve a synchronous restate,
and the answer is the `Selected` property on a four-line `ButtonBase` — written up under shape 5,
because the generalisation it forces is worth more than this panel. Three other things about the
port:

- **`Restate` is gone rather than moved.** The three footer `Disabled` flags and the rail's checked
  bit were one method called from six places, every one of which was somebody remembering to. They
  are four bindings and four dependencies now.
- ⚠ **`categories` had to stop being a `List<T>`.** The rail is a projection of the list and the
  filter, and a list appended to in place is a value change no signal can see — the shape a revision
  counter gets invented to paper over. It is an `ImmutableArray` in a signal that `Add` replaces,
  which is the call `AudioMixerView`'s solo set makes for the same reason.
- **The rail keys on the `SettingsCategory`**, which is an immutable record holding delegates: its
  value is its identity, and a page that survives a filter change keeps its region and its two live
  bindings. Nothing in the body reads the loop variable for anything that changes.

⚠ **And one gate had to be corrected rather than satisfied.**
`StylesheetTests.Every_class_name_in_the_markup_is_a_utility_or_one_of_ours` failed on
`settings-tab`, and its `Ours` escape hatch had been empty with the note that keeping it empty was
worth doing — "`EditorTheme` styles the editor's chrome by *tag* almost throughout". "Almost" was
carrying the weight: `settings-rail > button.settings-tab` needs a class precisely because what it
styles is a **control**, and a tag selector cannot tell one `button` from another. Every such rule
was previously reached from C# with `AddClass`, which that gate does not read — so the first panel to
write one in markup is the first to be accused of a typo. The premise is corrected in place.

### `sealed` is the sixth shape, and it is wave 6's finding

⚠ **Two of wave 6's eight panels stopped, and neither is blocked by anything markup cannot say.**
Both are blocked by a control being `sealed`, and the chain that gets there is worth writing down
because every escape this document has recorded ends at the same door.

The ledger's shape 1 says a control-fed panel is correctly imperative and the answer is to keep the
control behind a `ref`. That is right *when the control is fed by properties* — `AttachButton.Label`,
`RefreshButton.Disabled` — because a property is what a component-tag parameter assigns. It is not
right when the control is fed by a **method**: `panel.Inspect(descriptor, provider, targets)` and
`List.SetItems(…)` have no markup spelling at all, and neither does `Part<ScrollView>("add-component-list")`,
which asks for a tag the control does not have.

**The sanctioned escape for all three is the same four lines**, and this document has written it up
twice: shape 5's `internal sealed class MixerTitle : UiElement`, and `AudioMixerView`'s `OptionCell`
— *"wrap the control in a four-line element whose `Choices` is a property, because binding a
property is an ordinary effect"*. Both are **subclasses**. So:

- **`NodeInspector`** needs `<InspectorView Source="@Inspecting" />`, where `Source` is a property
  that calls `Inspect`. `InspectorView` is `sealed`, and there is no base class to reach for the way
  `Button`'s `ButtonBase` was there for `SettingsTab`.
- **`AddComponentMenu`** needs a `ScrollView` whose tag is `add-component-list`, because
  `BrowserTheme.vcss` styles that tag and the C# spells it `Part<ScrollView>("add-component-list")`.
  `ScrollView` is `sealed` too.

⚠ **This is a bigger statement than two panels.** There are twenty-nine `sealed class … : Control`
declarations in `Vixen.Ui.Controls`, and every one of them is a control that can be `ref`'d and
cannot be *extended* — so any panel whose relationship with a control is "feed it by a method" or
"give it my tag" is unportable, and no amount of markup design changes that. The choices are: unseal
the controls a panel needs to wrap (a one-word change per type, with no behavioural effect — a
subclass keeps the tag and the classes); give the control the property the wrapper would have added;
or add a markup directive meaning "run this expression when the region builds", which is a real
feature with real design questions and is the only one of the three that is not local.

**Nothing was worked around.** `NodeInspector`'s reconciler — 27 lines of `StringBuilder` signature
building, which exists only to decide whether the tree needs rebuilding and is exactly what a keyed
loop makes structural — is still there, and is the prize whenever this is unblocked.

#### ✅ Answered 2026-08-23, and the answer is that **nothing was unsealed**

⚠ **The three choices above are the right three and the first one is the wrong one.** Wave 7 took the
third — a markup directive — plus a thing the list did not contain, and the two of them close both
panels' blockers with no change to `Vixen.Ui.Controls` at all. Twenty-nine types keep their `sealed`.

The argument against unsealing, in the order it convinced:

1. **The two blockers are two problems, and only one of them is about extension.**
   `Part<ScrollView>("add-component-list")` does not want a subclass; it wants a *string*. Unsealing
   `ScrollView` to write `internal sealed class AddComponentList : ScrollView` invents a type whose
   entire content is a tag name — which is the `WaterFacts` mistake one level up, and this document
   has already argued that one ("a tag that lies is the thing `TypeSelectorReachTests` exists to
   catch"). ⚠ **The runtime never needed it:** `UiDocument.Adopt` takes the tag and only *falls back*
   to `TagName`, so `panel.Add<ScrollView>("add-component-list")` has always been legal C#. What was
   missing was a `.vxml` spelling, and it is now `tag="add-component-list"` — a universal attribute,
   refused on a lowercase tag as `VXML2014` because a lowercase tag already writes its own name.
2. **The other blocker is not about extension either; it is about a *call*.** `Source` was only ever a
   place to put `Inspect(descriptor, provider, targets)` so that a property assignment could reach it.
   `use="@(view => view.Inspect(…))"` reaches it directly: `BuildContext.Use` is `Bind` with a
   subject, so it is an effect — every signal the expression reads is a dependency, the control is
   re-fed when one changes, and the whole thing leaves with the region that declared it. A wrapper
   property gets *none* of that for free; every one written so far had a `Restate` somebody had to
   remember to call.
3. **A subclass is the wrong shape for this even where it is allowed.** `MixerTitle`, `OptionCell` and
   `SettingsTab` are all a type invented to hold one line of behaviour, and the ledger has counted the
   cost twice — "nine of them … about forty lines", "four copies of a tag name is how two of them come
   to disagree". Unsealing would have made that pattern *more* available. It should be less.
4. **`sealed` is load-bearing here in a way the paragraph above did not notice.** A control builds its
   parts in `OnCreated` and answers for its own tag, and `UiDocument.Create<T>`'s remark says why the
   tag comes from the type: "a caller that had to pass `"button"` alongside `Button` would eventually
   pass something else, at which point the control is still a `UiElement` and silently unstyled." A
   subclass is exactly a second place that can disagree. ⚠ Note the tension with the previous point:
   `tag=` *is* a caller passing a name, so it re-opens that door — deliberately, at one call site,
   named in the file that uses it, rather than in a type that outlives the reason.

So the correction to this section is: ~~"any panel whose relationship with a control is 'feed it by a
method' or 'give it my tag' is unportable, and no amount of markup design changes that"~~ — **both
were markup gaps and markup closed them.** What is genuinely left of `sealed` is a panel that needs to
*override* a control's behaviour, and no panel in this ledger does.

⚠ **The claim is held to the two named types rather than argued.**
`Editor/Vixen.Editor.NodeGraph.Tests/SealedControlHost.vxml` is a `.vxml` whose two tags are the real
`InspectorView` and the real `ScrollView` — a fixture that could be derived from would let the test
pass by writing the subclass the actual panels cannot write — and `SealedControlTests` reads it:
`add-component-list` is the list's tag and still a scroller, the inspector is fed by
`view.Inspect(descriptor, provider, targets)` (`NodeInspector.Rebuild`'s own call, arguments and all),
and pointing it somewhere else re-runs it. The test project gained the markup wiring in one line —
`<VixenUi>true</VixenUi>` — which is worth noticing on its own: the "two lines of `.csproj`" this
document warns about twice have been one line since `Directory.Build.targets` learned to stand in for
the package.

⚠ ~~**What this section still owes, and it is the part that is not done.** The two panels are
unblocked and **neither is ported**.~~ ✅ **Both are ported (wave 8, 2026-08-24)**, and the two things
this paragraph told whoever took them were both right.

- The shape-3 answer held: no production caller reads either panel back synchronously, so this was
  `CompiledSceneView`'s situation. Three *tests* did, and gained a frame each, exactly as
  `CompiledSceneView`'s three did in wave 5.
- `Rebuild`'s last four lines were the thing to solve first, and the answer is
  `provider.Descriptor.Members.Count == 0 && provider.Connected.Count == 0`. The rows are built from
  those members, so it is the same fact one step earlier — and a fact about the node rather than
  about whether a frame has passed. The one case where the two disagree is a `[CustomInspector]` for
  `GraphNode` drawing no rows; there is none, and a graph node is the last type that would get one.

⚠ **And the 27 lines became 12, which is the number this section was owed.** `Signature` existed to
answer "have the rows stopped being the right rows", and the answer was already written twice: the
tree is `@if`/`@for` regions, which reconcile themselves, and
`NodePortEditProvider.Describes(graph, nodes)` is *exactly* "are these nodes all of this type and
wired the way the rows assume" — the check `Rebuild` already ran before showing anything. What
`Describes` cannot see is the selection becoming a different set of nodes, and that is a
sequence compare. ⚠ It is also **less eager than the signature was**: the signature listed every
input port including the ones that carry no typed value, so a wire arriving at a texture port
rebuilt a panel whose rows cannot mention it.

⚠ **`AddComponentMenu` wanted neither `use` nor a subclass; it wanted `tag=` and a keyed loop.** The
correction above is right that the two blockers were two problems. The pool it deletes was the
fourth of wave 6's four and failed the same way — surplus rows parked with `display: none`, still
labelled with the previous query's components. `Rows` is read off the tree now
(`List.Content.Children.OfType<AddComponentRow>()`, which is where `ComponentsView.Sections` gets its
foldouts), because a `ref` in a loop is `VXML2010` and `refs` is keyed rather than listed.

⚠ **And one thing the sixth shape says that is now false in general.** `use` is also shape 5's escape:
`<fact-name use="@(cell => cell.Text = Label)" />` writes an intrinsic element's *own* `Text` with no
subclass and no extra box. The four-line subclass is still the better answer where it is possible —
`<FactName Text="@Label" />` is checked at the tag and reads as a property — so this changes which
answer is *general*, not which one to reach for first.

### Two more things wave 6 measured rather than argued

⚠ **The pool's hover is real, `FlameChartView` was right about it, and now there is a number.**
`node-search-row:hover` and `add-component-row:hover` are rules; `palette-row`'s deliberately is not
(*"a hover rule as well would give two highlighted rows"* — `EditorTheme.vcss` says so). So of the
three pooled pickers, only the palette escapes the trap, and `NodeSearchPopup` walks into it. The
harness dumps the state directly: the pointer rests on a row, the query changes what that row says,
and the hand-written panel keeps `state=Hover, Checked` where the ported one keeps only `Checked`.
⚠ **The port was taken anyway, and the argument is that the pool's behaviour was the wrong one**:
hover followed the *slot*, so after a keystroke it drew a hovered row over a node type the pointer
had never been over. `BuildContext.For` moves it with the *item*, and a row that survives a re-query
keeps its hover — which the `search-8` dump shows happening. Neither is what a pointer-driven
`:hover` should do; one of them is at least not misleading. Recorded here because it is a pixel
change and this section is where those are argued.

⚠ **A pooled row is not merely a spare element, it is a stale one.** Every one of the four pools
this wave met (`CommandPalette`, `NodeSearchPopup`, `AddComponentMenu`, `RemoteInspectorView`) parks
its surplus with a class and leaves the text alone — so the tree under `display: none` holds the
*previous* query's labels indefinitely. The dumps show it plainly: narrowing the node search to
"Comb" leaves seven hidden rows still reading Constant, Named, Settings, Texture, Vector. That is
the whole of the difference between the hand-written dumps and the ported ones, in every state where
they differ at all: no visible element moved a pixel in any of the forty states dumped this wave.

⚠ **And `change:` refuses a `TreeView`'s selection, correctly.** `change:Selection` compiles and
throws at compose time — "'tree-view' has no property called 'Selection'" — because `change:` is
`bind:`'s property lookup with a handler and a selection is state inside a control that paints its
own rows, not a `[UiProperty]`. That is shape 1 seen from the event side, and ~~the remedy is the one
already documented: a `ref` plus a subscription in `OnComposed`, writing a signal the `Disabled`
binding reads.~~

✅ **The diagnosis is right, the remedy was wrong, and the difference is whose side the fix is on
(2026-08-23).** `change:Selection` still throws and always will — but a `ref` and an `OnComposed`
were never the answer, because `Selection` is a read-only view over a `HashSet` **mutated in place**:
the same instance before and after every change, so nothing built on `PropertyChanged` could ever
have reported it. What was missing was a *value*. `TreeView.SelectedNodes` is an
`[UiProperty] ImmutableArray<TreeNode>` snapshot published by `Restate` **only when the set really
differs**, so `change:SelectedNodes` is one attribute — and it is *quieter* than the
`SelectionChanged` event it replaces, which fires again for a click on the row that was already
selected. `RemoteInspectorView.vxml` is ported and its whole `OnComposed` is gone;
`PortedPanelTests.Apply_lights_only_with_a_selection_and_a_member` fails without the attribute.

⚠ **The rule for a control author, which is the part worth more than the panel:** *a collection is
bindable only as a value, written where the mutation happens.* `DataGrid` and `NodeCanvas` are the
identical shape — a `HashSet`, a read-only view, an event and one restate funnel — and the same four
members apply verbatim. Still owed.

⚠ ~~**`OnComposed` is also where a *capture-leg* handler has to live**, which is a limitation nothing
had hit before. `BuildContext.Subscriptions`' entries are
`Action<UiElement, Action<UiEvent>, RoutingStrategy>` and **the `on:` syntax has no way to say which
leg** — so the three pickers' `AddHandler<KeyEvent>(…, RoutingStrategy.Capture)`, which is what stops
a search box turning Down into caret movement, cannot be written as an attribute. Worked around in
`OnComposed`, named here because the next picker will hit it too. An `on:keydown.capture` modifier
would close it and the modifier list is already parsed.~~

⚠ **Wrong, and instructively so: `on:` could always say which leg.** `capture` was in
`Binder.EventModifiers` *and* honoured by `BuildContext.On`, which reads
`modifiers.Contains("capture") ? RoutingStrategy.Capture : RoutingStrategy.Bubble`. What the table
had no entry for was **`keydown`**, so the attribute threw *"'keydown' is not an event"* at compose
and the symptom was read as a syntax gap. This is the "two recorded gaps are stale" lesson a third
time: the sentence "the modifier list is already parsed" was in the note, and nobody followed it one
step further to ask what else the line could be failing on. `keydown`, `keyup` and `textinput` are
registered now (2026-08-23) — `textinput` because `KeyEvent.Key` is a *physical* US-QWERTY position,
so an author with no name for the event carrying characters reads letters out of `on:keydown` and
ships the AZERTY bug.

⚠ **And a typed `on:` handler must be an explicitly typed lambda.**
`on:keydown.capture="@((KeyEvent e) => Keyed(e))"` works; `@Keyed` for a `void Keyed(KeyEvent)` fails
with "cannot convert from 'method group' to 'System.Action'". That is C#'s rule rather than an
emitter choice: one call is written for both `On` overloads and the emitter cannot name the event
type — the table owns that — so `TEvent` is inferred from the argument, and a method group has no
natural type until the delegate's parameters are known. `@Increment` and `@(() => …)` are unaffected,
being `Action`s.

⚠ ~~**So the pickers are still not ported, and the reason has moved to a bigger place.** All five
capture-leg handlers in the tree — `CommandPalette`, `NodeSearchPopup`, `AddComponentMenu`,
`KeyBindingsView`, `InputActionsView` — call `AddHandler` on **`this`**, the component's own element.
An `@inherits` file's markup roots are *children* of the host and `ComponentEmitter.Target` can only
ever name one of those, so `on:keydown.capture` on the `<SearchBox>` is a different element with
different route coverage: a key arriving while the focus is anywhere else in the panel would no
longer be seen. A port would be a behaviour change, which is a defect until argued for. ⚠ Two of the
five also want `handledEventsToo: true`, which `on:` has no modifier for either.~~

✅ **Both built 2026-08-24, and the diagnosis above was right about both.** `<self />` is a reserved
lowercase tag that emits `BuildContext.Host(this)` and then applies its attributes to that variable
like any other element — so `<self on:keydown.capture="@((KeyEvent e) => Keyed(e)) />"` is the same
element the hand-written `AddHandler` was on, not a root beside it. `VXML2015` refuses it anywhere
but the component's top level, because inside an `@for` it would subscribe the host once per row.
`.handled` is the fifth modifier, and it needed the subscription table's value type to change:
`stop`, `once` and `self` are filters `BuildContext.On` applies itself, while `handled` is
`AddHandler`'s third argument and only a table entry can pass it — hence `EventSubscription` and
`EventSubscription.Listen`. ~~**All five pickers are unblocked; none is ported in this branch.**~~
✅ **All five ported, wave 9 (2026-08-24.)**

**Which two wanted `handledEventsToo`, and why it is not a free upgrade.** `KeyBindingsView` and
`InputActionsView` — the two that *record a chord*. Both are on the capture leg for the same reason
they need the flag: `CommandDispatcher` is attached to the document, so pressing Ctrl+S to **bind**
Ctrl+S has to be seen whether or not the dispatcher has already run "save scene" for it.
`CommandPalette`, `NodeSearchPopup` and `AddComponentMenu` deliberately did **not** get it: they
*act* on the key — Enter runs a command or creates a node — and acting on an event another handler
has claimed is a palette running something nobody chose. The two directions are asserted against the
same arrangement in `SelfHandlerKeyTests`, and swapping the modifier between the palette and
`KeyBindingsView` fails exactly one test each way.

⚠ **Three findings came out of testing it, and the second is the one to carry forward.**

1. **`<self />` as shipped doubled the host's handlers on a rebuild.** `BuildContext.Rebuild` clears
   the host's children and re-enters `Build` on the same `component.Root` — which is what
   `Host(this)` names — so a `.vxml` save counted one press twice. Every *other* element a body
   binds to is one the body made, and disposing the composition takes it away; the host outlives it.
   `ctx.On` now registers its `RemoveHandler` against the region being built. Pinned by
   `EmitterTests.Self_does_not_subscribe_the_host_again_when_a_component_is_rebuilt`, which failed
   with three presses counted for two before the fix. This is also why `KeyBindingsView`'s
   hand-rolled `keyed` guard could go. ⚠ An `@inherits` class could never have reached it —
   it composes in `OnCreated` and `UiElement.Remove` is terminal — so all five pickers were safe;
   the exposure was the plain-`Component` flavour.
2. ⚠ **A key-press test does not automatically distinguish the host from the first markup root**,
   and three of the five nearly shipped one that did not. All three search pickers focus their
   `SearchBox` on open, so the capture route is root → panel → box and a handler mis-written on
   `<SearchBox>` is on that route **as its target** — it hears every key such a test presses.
   Moving the attribute there left `AddComponentMenuDumpTests` (the file wave 6 held up as the
   pattern) entirely green. What separates them is a key arriving *elsewhere* in the panel, which is
   the README's own example: raise at the **list**. The two panels focused on the host itself —
   `KeyBindingsView`, `InputActionsView` — distinguish without help.
3. **`on:click` is now wider than `AddHandler<ClickEvent>`, not narrower**, so all five kept their
   click handler in `OnComposed`. `ControlMarkup` subscribes `ClickEvent` *and* `TapEvent`; `On`'s
   `args is not TEvent` test discards the tap for a `@((ClickEvent e) => …)` handler, so a port
   would behave identically — but it would register a subscription for an event none of these
   panels wanted, to say what one line already says.

### How wave 7's dumps were taken, and the one thing the instrument got wrong

⚠ **Every dump carries a per-element flags block, because `UiTest.Tree()` cannot see
`ElementState`.** It prints tag, id, classes, rectangle and text — so a `Disabled` button, a
`:checked` toggle and a hovered row are all indistinguishable from their opposites in it, which is
exactly where a port is riskiest. The harness walked the document a second time writing
`tag state=… Label=… Disabled=… IsChecked=… Number=…` per element, and the two halves were compared
together. `PaletteBlockTests` keeps that shape permanently for the terrain parts; the panel dumps
were taken against the hand-written code and then against the port, and are not committed, because
comparing them requires the code they replaced.

⚠ **And the instrument had one false positive worth knowing about, because the next wave will see
it.** A shared `.vxml` part is a *type*, so it has public properties the plain element it replaced
did not — `AnalysisRow.Message` above all. A reflection-driven flags block prints those, so a panel
that moves onto `AnalysisRow` shows a differing flags line per row while its `Tree()` half is
identical to the byte. That happened in two panels here (`UtilitySetView`, `StandardFrameView`) and
was resolved by asserting separately that **no `Tree()` line differs at all** — twelve flags lines
moved in `StandardFrameView` and zero tree lines did. Read a part-adoption diff that way rather than
widening the tolerance.

⚠ **And a dump has three blind spots that no number of states fixes, all of which wave 7 had to
close with ordinary tests.** They are worth naming because every wave so far has treated the dump as
the whole of the evidence, and the third one was carrying a live defect.

- **A dump drives the panel from the model, so it only ever exercises the *binding* leg.** Every
  state above was reached with `document.Edit(…)` or `Show(…)` and compared the tree that came out —
  which says nothing about `change:`, where a person moves a control and the document is supposed to
  move **once**. That leg is new code in every port that closes shape 2, and it is where a write-back
  loop would live. `FontViewTests` is the assertion: undo depth, because `FontDocument.Edit` no-ops
  on an unchanged YAML and a loop that landed back on the same value would be invisible to a value
  check. It is wave 3's mixer assertion and should be the standing one.
- **A dump only covers the arms it happens to walk into.** `MaterialView`'s four states were all
  byte-identical and every one of them had an empty `Header.Graph`, so the graph link was `hidden`
  throughout and two of its three arms were never drawn — including the one the port's
  `MaterialGraphLink` record exists to make unstateable, a button reading "Open shader graph" while
  greyed out. `MaterialGraphLinkTests` covers it. **Before trusting a set of states, list the
  branches in the panel and tick them off**; "all N states matched" is a claim about N, not about the
  panel.

⚠ **And a third blind spot, which is the one that actually caught a defect: a dumped state that never
crosses a branch says nothing about the binding on it.** `StandardFrameView`'s empty-quality-table
sentence was first written `@if (QualityRows == 0)`. `QualityRows` is a plain `int` — a public
counter the panel promises — so **the arm registered no signal dependency at all**:
`BuildContext.Switch` wraps its condition in a `Bind`, and a condition that reads no signal is
evaluated once and never again. It would also have been evaluated *wrongly*, because `Show` runs
before the first flush, so the table was already full when the arm was first picked and the sentence
could never have appeared however empty the table later got. All six dumped states had knobs in the
table, so the ported panel matched the hand-written one to the byte while carrying a binding that
could not work.

⚠ **The general rule is one line and it is easy to break by accident: a binding may only read a
signal.** A plain field the panel also writes looks identical at the call site, the compiler cannot
see the difference, and a dump only catches it if a state happens to cross the branch. The fix here
is one word — `QualityKnobs.Length == 0`, which reads the signal — and the two counts are always
equal, so no dump moved. `FrameViewTests` is the assertion.

⚠ **All three test files were confirmed to fail against the defect before being accepted** —
`change:Number` deleted from the pixel-size row, `Disabled` pinned to `false` on the graph button,
and `QualityRows == 0` put back. A test written to prove a port correct will prove it correct.

### The flags block is `UiTest`'s now, and every wave-8 dump is committed

⚠ **"The panel dumps … are not committed, because comparing them requires the code they replaced" is
the sentence wave 8 was sent to stop writing.** Three files in the whole editor dumped a tree when
that was written; there are **seven** now, and every panel this wave touched has one. The objection
is answerable and the answer is four lines: *keep* the code they replaced, in the test, as the
reference builder. `FactRowTests` has always done it, `NodeInspectorDumpTests` does it for a whole
panel — `HandWritten` is `NodeInspector.Rebuild` as it stood, and eleven states run both and compare
two strings — and `ChromeDumpTests` does it for `AnalysisRow`, which five panels had been building
output out of with **no dump test of its own**.

⚠ **The flags block is a method on `UiTest` rather than a harness that is thrown away.**
`UiTest.Flags(root)` walks a subtree and prints nine values a tree dump cannot see — `State`,
`Disabled`, `ReadOnly`, `IsChecked`, `IsExpanded`, `Label`, `Value`, `Placeholder`, `Number` —
and `UiTest.Tree(root)` scopes the existing dump to one element so a panel can be compared without
the document around it. Three test assemblies use them; wave 7 wrote the same walk three times and
deleted it three times.

- ⚠ **A fixed list of names, read by reflection, and not every public property.** That is wave 7's
  false positive fixed at the source: reflection over the whole surface makes the dump move whenever
  a control or a shared part gains a property, which is what made twelve flags lines move in
  `StandardFrameView` while zero tree lines did. Nine names move only when one of nine values does.
- ⚠ **A false flag prints nothing and a number always prints.** A line per control saying
  `Disabled=False` hides the one that says True. A regression is visible either way: a token that
  should be there and is not is as much a diff as one that changed.
- ⚠ **`[RequiresUnreferencedCode]`, because the walk is reflective and `Vixen.Ui.Testing` is
  trim-analysed.** The attribute is the honest form of the trade and costs its callers nothing —
  test projects do not run the analyser.

⚠ **And the standing blind-spot list above was worked rather than read.** Every wave-8 dump drives
its panel through a *control*: `AddComponentMenuDumpTests` types into the search field and presses
Down, `ChromeDumpTests` toggles Generated-code twice and Play twice and renames two different
property nodes through the same surviving `@if` arm, and `NodeInspectorDumpTests` types into a port
row and wires the port a row belongs to. ⚠ **Two of them were sabotage-verified** — deleting
`change:Value` from the picker's search box fails exactly the two tests that claim to cover it, and
no others — and one existing assertion turned out to be unfailable:
`row.Arrow.HasClass("parked")` looked for the class on a row's chevron, where it was never written.

⚠ **A binding over a plain field is a build failure in one shape, and it is worth knowing which.**
`@if (named.Value is { } naming)` puts the pattern variable in the *predicate's* scope, and the arm's
body is a separate lambda — so a readout inside the arm that names it is `CS0103`, on the attribute's
own characters. That is the wave-4 trap turned into a compile error for the `is { }` spelling, and it
is the only spelling that gets one: `@if (Rows == 0)` over a plain `int` still compiles and still
runs once.

### "Keep the field rows" was wrong four ways, and that is wave 7's finding

⚠ **One instruction covered seven panels and did not fit any of them.** The row above read "port the
readouts, keep the field rows (shape 2)", written when shape 2 was open. It is worth taking apart,
because the *reason* a panel keeps its imperative half is the thing a wave has to get right and the
four reasons here are four different things wearing one sentence.

- **Stale, for `FontView`.** Shape 2 closed in August: `NumericInput.Number` and `CheckBox.IsChecked`
  are `[UiProperty]`, so all five of its editable fields are an ordinary binding one way and a
  `change:` handler the other. Nothing in that panel is imperative now but the glyph page and the
  picker's options. **A ledger row written before a feature landed is a row that has to be re-read,
  not obeyed.**
- **Right, wrong reason, for `MaterialView`.** Its parameter list stays C# — but not because it has
  field rows. Every row is `expander.Content.Add<InspectorView>()` then `rows.Inspect(parameter)`: a
  control fed by a **method**, inside a `@for`, and `InspectorView` is `sealed`. That is wave 6's
  sixth shape exactly, the same door `NodeInspector` and `AddComponentMenu` stopped at.
- **Right, wrong reason again, for `ShapeVocabularyView`** — and this one is not shape 6 either.
  `Restate` is a `switch` over the **type** of the selection. Written as `@if` arms the arm's
  identity would be the type, so moving from one name to another name would not change the arm index,
  the region would survive, and every binding in it would go on showing the first name. That is wave
  4's trap with its sharpest edge, and the honest fix is four snapshot records plus five write-back
  controls, which is a different commit.
- **An understatement, for `StandardFrameView`.** Its "field rows" are not rows at all: they are two
  whole `InspectorView`s, which is ordinary shape 1 and always was. ⚠ **Worth telling apart from
  `MaterialView`'s: `sealed` blocks the *wrapper*, not the `ref`.** A single control fed once by a
  method needs no wrapper type — hold it in a member and call the method. Only a control fed *inside
  a loop* needs a property for a component tag to assign, and that is where `sealed` bites.

⚠ **And `UtilitySetView` had no field rows at all**, which nothing in the row suggested. Every cell in
it is a readout, so the port is the whole panel.

### `Add`'s first parameter is the tag, and a class written into it is invisible for ever

⚠ **`Add("utilityset-action selected")` made an element whose *name* was that whole string, space
included.** `UiElement.Add(string tag, string? id, params string[] classNames)` — the class list is
the third parameter, and the panel passed one string. So `selected` was never a class, no rule could
ever have reached it, and the row that was supposed to be highlighted never was. It survived because
**nothing in the tree styles `utilityset-*` at all**: `grep -rl utilityset-action` finds one file and
it is the panel's own source, so every element in it computes to zero width and no arrangement of
them ever looked wrong.

The port writes what the C# meant — `<utilityset-action class="selected">` — and that is the only
difference in six dumped states. It moves no pixel, and it is recorded here rather than fixed quietly
because a port that changes behaviour is a defect until argued for. ⚠ **The general lesson is about
the dumps rather than about this bug**: a whole-tree rectangle comparison, which is what every wave
since the first has leant on, proves much less on a panel nothing styles. Read those dumps for **tag,
class, text and order**, and say so.

### `refs` inside an `@if` inside a `@for` threw, silently — fixed

⚠ **`VXML2013` reads the markup lexically, so a `refs` in an `@if` arm inside a `@for` compiles.** At
compose time it threw: `BuildContext.For` sets the iteration key around the *synchronous* build of a
new region and restores it in a `finally`, while `BuildContext.Switch` registers its own `Bind`,
which the scheduler runs afterwards. `Refs` found no key and threw the "only meaningful inside an
@for" message whose own remark says nothing generated can reach it.

⚠ **What it looked like is the part worth remembering.** The arm's builder was abandoned at the
throw, so the element created on the line *above* the `refs` survived with **no classes, no bindings
and no children**, while every other panel on the screen was correct. `ShapeVocabularyView` showed
seven `vocab-row`s as empty boxes with a byte-perfect side pane and report beside them.

✅ **Fixed the same day**, four lines in `Switch`: capture the key where the arm is declared and
restore it round the build, which is the bargain `For` already makes for a nested loop and rests on
the same sentence — an arm inside a row belongs to the row.
`CompositionTests.A_refs_inside_a_branch_inside_a_row_is_filed_under_the_rows_key` is the assertion,
and it was confirmed to fail against the unfixed `Switch` before it was accepted. It is in
`Vixen.Ui.Tests` and not in the markup suite because the markup compiler genuinely cannot see this.

### Three smaller things wave 7 had to find out by hitting them

- ⚠ **`key=` is not optional at any depth.** `VXML2004` is an error inside a nested `@for` too. Worth
  saying because `Vixen.Ui.Markup`'s README has the sentence "it is the item itself when the loop
  declares none", and that sentence is about which key `refs` files under — not about whether `key=`
  may be omitted.
- ⚠ **A markup component may not override `OnRemoved`.** The generator emits it; that is where the
  composition is disposed. `partial void OnUnmounted()` is the hook it calls first, before the
  effects go, and it is where a subscription to something *outside* the document belongs —
  `StandardFrameView` drops its document listener there.
- ⚠ **`change:` cannot express "assign the initial value before subscribing", and one panel needed
  to.** `MaterialView`'s shape picker runs `AddOption` ×3, then `Value = "Sphere"`, and *then*
  `SelectionChanged +=`, so the initial value is set while nothing is listening. `change:Value` is
  `bind:`'s write-back leg over `PropertyChanged`, wired when the region is built — there is no way
  to say "assign without notifying" and no way to order a `change:` handler after an `OnComposed`
  assignment, so it would fire once at compose time and raise a `PreviewChanged` the hand-written
  panel does not. Worked around with `ref` + `OnComposed` in the C#'s exact order. The shape of a fix
  is either a modifier meaning "not for the first value" or build-list item 4, an `AddOption` markup
  can name — at which point the whole ordering question goes away, because the options and the value
  would both be bindings and a binding's own write is not reported.

### Two reasons a handler stays in `OnComposed`, and only one of them was ever the reason

⚠ **`NodeSearchPopup.vxml` says its capture-leg key handler is hand-written because
"`BuildContext.Subscriptions`' entries carry a `RoutingStrategy` that no `on:` attribute has a
spelling for", and that is false and was false when it was written.** The markup README's own
`on:keydown.capture` section says so: `capture` has been in the modifier list since the list existed,
`keydown` and `keyup` are in the table, and `keydown` already filters to `KeyAction.Pressed` — which
is the exact guard those handlers open with. Three editor pickers kept a hand-written `AddHandler`
on the strength of a diagnosis that had already been corrected one directory away.

The two reasons that *are* real, both met porting `AddComponentMenu`:

- ⚠ ~~**A handler on the component's own host element has no markup spelling.** `on:` is an attribute
  on a tag, and a `.vxml` body has no tag for the thing it is building. The picker's key handler is
  subscribed on `this` — above every row, which is the whole point of taking Down and Enter before
  the search box treats them as caret movement and submit — so it cannot be an attribute on anything.
  That is a real gap and a small one: a header, or `on:` on the `@component` line, would close it.~~
  ✅ **`<self />`, 2026-08-24.** The shape guessed at was right; what it got wrong is only that the
  `@component` line could carry it — the lexer takes that directive as a keyword plus exactly one
  name, so a tag was the cheaper answer. See the `<self />` block above.
- ⚠ ~~**`on:click` is a `TapEvent` and a control's activation is a `ClickEvent`.** They are two sealed
  types: `BuildContext.Subscriptions["click"]` registers `AddHandler<TapEvent>`, and
  `Control.Raise(new ClickEvent …)` is what `ButtonBase.Activate` produces. So a markup `on:click`
  hears a pointer tap and **misses a keyboard activation** — and `Activate()` is what every editor
  test presses a button with.~~ **Stale when it was written, and wrong twice over.**
  `Vixen.Ui.Controls/ControlMarkup.cs` has replaced the `click` entry from a module initializer since
  2026-07-31, so a markup `on:click` on a `<Button>` has heard Space, Enter, an access key and
  `Activate()` for as long as capitalised tags have named controls. ⚠ **What was actually broken was
  the other half of the same line**: the replacement chose by `element is Control`, and only
  `ButtonBase` and `ColorSwatch` raise a `ClickEvent` — so `<Card on:click>`, `<Panel on:click>` and
  every other plain `Control` bound a handler nothing could raise, which is the *same* silent failure
  one type down. Fixed 2026-08-24: both events are subscribed on every element and
  `Control.RaisesActivation` keeps one press from counting twice. ~~Panels whose click handler walks
  the source chain (the picker's rows, the three graph editors' transports) can be `on:click` now.~~
  ⚠ **They *can*, and wave 9 left all five pickers' click handlers hand-written anyway** — for the
  opposite reason to the one first recorded. Subscribing both events makes `on:click` **wider** than
  the `AddHandler<ClickEvent>` it would replace, not narrower. `On`'s `args is not TEvent` test does
  discard the tap for a `@((ClickEvent e) => …)` handler, so the *behaviour* is identical; what is
  not identical is that a `TapEvent` subscription is registered on a panel that never wanted one.
  One line of `AddHandler<ClickEvent>` states the narrower thing exactly, so it stayed. **A
  preference, not a blocker** — a panel that wants both legs should write `on:click` and say so.

### What to build, in order of leverage

1. ~~**A value-change subscription markup can name.**~~ Built 2026-08-22 as `change:X`, and it
   needed neither of the two things guessed at here: not a routed value-change event, and not a
   second table. It is `bind:`'s property lookup with a handler instead of an assignment, because a
   value change was a `[UiProperty]` change all along.
2. ~~**A per-iteration handle.**~~ Built as `refs` into an `ElementRefs<T>`, keyed on the loop's own
   identity — *not* `ref` in a loop, which is still `VXML2010` and still wrong for the reason it
   always was, and not a nested component either.
3. **A row template for `VirtualizingPanel`.** Frees `ConsoleView`, `MessageLogView` and `AssetGrid`,
   which are three of the most-looked-at surfaces in the editor.
4. **A `Select` whose options come from markup.** `AddOption` is a method — the options are not the
   control's children at all, they live in a popover hanging off the document root — so with `refs`
   an enum dropdown inside a `@for` can now be reached and subscribed to, but its options still have
   to be added from C#. ⚠ **`AudioMixerView` shows the workaround and it is a good one:** wrap the
   control in a four-line element whose `Choices` is a *property*, because binding a property is an
   ordinary effect that re-runs with the region it was declared in. That is `OptionCell`, and it is
   why this item is a convenience rather than a blocker now. ⚠ **And `use` is the same thing without
   the type**, with one caveat `OptionCell` does not have: a `use` re-runs, so it must say what the
   control should *be* rather than append to it — and `Select` offers `AddOption` and `ClearOptions`
   and no setter, so the honest spelling today is
   `use="@(s => { s.ClearOptions(); foreach (…) { s.AddOption(…); } })"`, which works and reads badly.
   This stays on the list, and what it is owed is now precise: a `Select.SetOptions`.

   why this item is a convenience rather than a blocker now. ⚠ **Wave 7 found the second thing it
   would buy, which is an ordering problem rather than a convenience.** A picker whose options are
   added from `OnComposed` cannot also use `change:Value`: the initial assignment happens after the
   handler is wired, so it fires once at compose time and raises whatever the panel raises. If the
   options were a binding, the value would be one too — and a binding's own write is not reported,
   so the question would not arise. `MaterialView`'s `Shapes` is the panel that hit it and kept
   `ref` + `OnComposed` in the hand-written order instead.
5. ~~**Shared `<Section>`, `<FactRow>` and `<VerbRow>` components.**~~ **`FactRow` is built** and has
   four callers. `VerbRow` earns nothing yet — `verb-row` has no rule in any sheet. `Section` was
   blocked on the `World-title` casing bug, which is fixed, so it is now merely unbuilt. What the
   exercise proved is worth more than the row — though ⚠ **wave 3 corrected the conclusion**: a
   *part* is not the only way markup can write an intrinsic element's own text inside a loop, a
   four-line `UiElement` subclass with a `TagName` override is, and it is the cheaper one whenever
   the thing being written is a caption rather than a row.
6. ~~**Un-`sealed` controls, or a way to feed a sealed one from markup.**~~ **Built 2026-08-23 as
   `tag=` and `use`, and the "cheapest form" this item recommends is the one that was not taken.**
   Unsealing the two types is cheap and buys a type per tag name, which is the thing the ledger
   already argues against everywhere else. `tag="add-component-list"` says the string at the place it
   is true, and `use="@(v => v.Inspect(…))"` is the directive — an effect, so it re-runs and leaves
   with its region, which is strictly more than the wrapper property would have been. Neither panel
   is ported yet; both are unblocked. See the block under "`sealed` is the sixth shape".
7. ~~**`on:` with a routing strategy — `on:keydown.capture`.**~~ **Built 2026-08-23, and it was not
   the routing strategy — that always worked. It was that the `Subscriptions` table had no keyboard
   entry at all.** `keydown`, `keyup` and `textinput` are registered. ⚠ ~~**What is left is a different
   item and it now blocks more than this one did: a markup spelling for the component's *own*
   element.** All five capture-leg handlers in the tree subscribe on `this`, an `@inherits` file's
   markup roots are children of the host, and `ComponentEmitter.Target` can only name a child — so
   every one of those ports would change which element the route reaches. The shape is a `<self />`
   pseudo-tag whose attributes emit against `this`; two of the five also need a `handledEventsToo`
   modifier.~~ **Both built 2026-08-24, as `<self />` and `on:….handled`, and the guessed shape was
   the built one.** The five pickers are unblocked and none is ported here.
8. ~~**A `CollectionSignal` for a map.**~~ **Built 2026-08-23 as `SignalDictionary<TKey, TValue>`,
   and the sizing was right about the trade and wrong about what would trigger it.** It was not the
   thousandth counter; it is that the type is under three hundred lines once you decline to build a
   change log — and a map has no use for one, because `@for` cannot bind to a dictionary at all (its
   order is its hashing), so what a reconciler keys on is a *sorted projection* and the log would be
   written every update and read by nobody. ⚠ **One node for the whole map**, which is
   `CollectionSignal<T>`'s choice and is the right one here for a reason specific to maps: a binding
   that reads a key *that is not there yet* still has to wake when it appears, so per-key nodes would
   have to be created on read as well as write and kept after removal — an unbounded set keyed by
   whatever strings a caller invents. The coarse edge over-approximates and cannot under-approximate,
   so the cost is a re-run and never a stale answer. ⚠ **`SignalDictionary`, not `DictionarySignal`**:
   CA1710 requires an `IReadOnlyDictionary` implementor to end in `Dictionary`, and `CollectionSignal`
   escapes the same rule only because `IReadOnlyList<T>` is not on it. `RemoteInspectorClient`'s
   counters are converted and the panel's nine states dump identically. ⚠ And the conversion turned
   `Counters` from a snapshot into a **live view**, which is the one behavioural difference.

## Localisation

⚠ **`StringId`, `StringCatalog` and `Strings` are in `Vixen.Ui` now**, not here — doc 46 § A3
counted them among the 41 % of this assembly that is application-framework machinery with no editor
in it, and an application that cannot reference `Editor/` was left writing the literal. What stays
here is `EditorStrings` — the editor's 119 declarations and its `All` list — and `StringCatalogYaml`.

⚠ **The two sides of a declaration are checked now rather than remembered.** `EditorStrings` writes
every string twice, as a property and as a name in `All`; `StringDeclarationAnalyzer` compares them
(`VXS0310`), refuses two declarations under one id (`VXS0311`), and refuses a `StringId` built
anywhere else in this assembly (`VXS0312`) — which is how `SelectMode`'s title stopped being a
declaration nobody could translate. `nuke CheckStrings` adds the half no compilation can see: a
declaration used nowhere in the tree. **It found seven here**, five of them strings the editor does
not say and two — `CommandUndo` and `CommandRedo` — whose ids had drifted from the ones
`EditorApplication` actually registered.

Every user-visible string is a `StringId` — an id and the English text it says — so
`item.Label = EditorStrings.Save.Text` is no more work than the literal and there is never a reason
to write the literal. That is the retrofit `Stride.Core.Translation` exists to repair.
[docs/guide/ui/strings](../../docs/guide/ui/strings.md) is the written half.

⚠ **The YAML did not travel with the catalogue.** `StringCatalogYaml.Save`/`.Load` are here because
they are this assembly's only use of `Vixen.Core.Yaml`, and a `StringCatalog` promoted with a
serialiser attached would add a package to the pin of every application that shows a word — including
the ones publishing NativeAOT that ship their catalogues as something else entirely. The format is
the application's choice; the editor's is YAML, for the reason `DockLayout` gives about layouts.

`Strings` is the one static thing on either side of the fence, and the price is that anything
subscribed to `Strings.Changed` must unsubscribe — `MenuPresenter` is `IDisposable` for exactly this,
and `EditorShell.Dispose` calls it.

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
- **A modal question, or a native file picker.** ⚠ `DialogService` **left this assembly** — it is
  `Vixen.Ui.Controls.DialogService` now, and doc 46 § A4 is why: modality was already in the control
  library and the 376 lines that made a dialog *answerable* were here, where no application could
  reach them. `EditorShell.Dialogs` is the promoted type, and the shell no longer pumps it — the
  service is subscribed to `UiDocument.Ticked`, so `Document.Tick` is the pump. What is still drawn
  rather than native, and for the same reason: a modal that is an OS window cannot be screenshotted
  by the golden suite or driven by the automation harness. The *file* pickers are the opposite case:
  they are about the user's disk rather than the editor's state, they go through `INativeDialogs`,
  and reaching one is `Vixen.Editor.App`'s job.

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
- ~~**`Strings.Resource` generation.**~~ Closed 2026-08-25, and **not as a generator** — see
  [docs/plan/11](../../docs/plan/11-editor.md) § As built. An id used nowhere fails
  `nuke CheckStrings`, an id repeated at a call site fails it too, a name no declaration class has is
  CS0117, and `VXS0310`–`VXS0312` refuse the three things a declaration class can get wrong on its
  own. `EditorStrings` is unchanged in shape, which doc 46 § A3 requires. What is still owed is the
  178 ids the editor's *module* assemblies build at call sites and declare in no class; that gate
  counts them.
- **A mode bar.** `IEditorMode` — the seam Select / Landscape / Foliage would hang off — does not
  exist, which doc 20's A1 calls the one structural addition still owed to the frame.

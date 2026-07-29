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
